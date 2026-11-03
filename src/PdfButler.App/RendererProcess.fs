/// One disposable renderer process per document. This is crash isolation, NOT a sandbox:
/// the helper still has the current user's OS permissions and opens the supplied path itself.
module PdfButler.App.RendererProcess

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open PdfButler.Core
open PdfButler.Pdfium
open PdfButler.App.RendererProtocol

exception RendererUnavailableException of message: string

type private Reply<'T> = Value of 'T | PageError of int | LimitError | OperationError

type internal RendererSession(startInfo: ProcessStartInfo, requestTimeout: TimeSpan) =
    let child = new Process(StartInfo = startInfo, EnableRaisingEvents = true)
    let worker = new Worker("renderer-ipc", ignore)
    let gate = obj ()
    let failed = Event<string>()
    let mutable failure: string option = None
    let mutable closed = false
    let mutable cleaned = 0

    let cleanup () =
        if Interlocked.Exchange(&cleaned, 1) = 0 then
            try if not child.HasExited then child.Kill(true) with _ -> ()
            try child.StandardInput.Close() with _ -> ()
            try child.StandardOutput.Close() with _ -> ()
            try child.StandardError.Close() with _ -> ()
            child.Dispose()
            // Never wait for an IPC thread on the UI thread (it may be unwinding a broken pipe).
            Task.Run(fun () -> (worker :> IDisposable).Dispose()) |> ignore

    let fail message =
        let notify = lock gate (fun () ->
            if closed || failure.IsSome then false
            else failure <- Some message; true)
        if notify then
            cleanup ()
            failed.Trigger message

    let ensureOpen () =
        lock gate (fun () ->
            if closed then raise (ObjectDisposedException "renderer")
            failure |> Option.iter (fun m -> raise (RendererUnavailableException m)))

    do
        child.Exited.Add(fun _ -> fail "The PDF renderer stopped unexpectedly. Other documents are unaffected. Try reopening this PDF.")
        // Consume stderr, but never log it: native/managed diagnostics may contain document data.
        try
            if not (child.Start()) then failwith "Could not start the PDF renderer."
            // Drain bytes directly: an unterminated native error line must not grow a string
            // without bound in the UI process. Observe disposal errors without exposing data.
            child.StandardError.BaseStream.CopyToAsync(Stream.Null).ContinueWith(fun (t: Task) ->
                if t.IsFaulted then t.Exception |> ignore) |> ignore
        with e ->
            cleanup ()
            raise (RendererUnavailableException $"The PDF renderer could not be started ({e.GetType().Name}).")

    member _.ProcessId = child.Id
    member _.MemoryBytes =
        try
            child.Refresh()
            child.WorkingSet64, child.PrivateMemorySize64
        with _ -> 0L, 0L
    member _.Failure = lock gate (fun () -> failure)
    [<CLIEvent>]
    member _.Failed = failed.Publish

    member _.Invoke<'T>(command: Command, write, read: BinaryReader -> 'T, priority: Priority, ct: CancellationToken) : Task<'T> =
        let pending = worker.Invoke(priority, ct, fun () ->
            ensureOpen ()
            let request = encode (fun w -> w.Write(int command); write w)
            let reply =
                try
                    use timeout = new CancellationTokenSource(requestTimeout)
                    let exchange = task {
                        do! writeFrameAsync child.StandardInput.BaseStream request timeout.Token
                        return! readFrameAsync child.StandardOutput.BaseStream timeout.Token
                    }
                    let bytes = exchange.GetAwaiter().GetResult()
                    decode (fun r ->
                        match r.ReadByte() with
                        | 0uy -> Value(read r)
                        | 1uy -> PageError(r.ReadInt32())
                        | 2uy -> LimitError
                        | 3uy -> OperationError
                        | _ -> invalid ()) bytes
                with e ->
                    let message =
                        if e :? OperationCanceledException then
                            "The PDF renderer did not respond within the time limit and was stopped. Try reopening this PDF."
                        else "Communication with the PDF renderer was lost. Other documents are unaffected. Try reopening this PDF."
                    fail message
                    ensureOpen ()
                    raise (RendererUnavailableException message)
            match reply with
            | Value v -> v
            | PageError page -> raise (PageLoadException page)
            | LimitError -> raise (ArgumentOutOfRangeException "renderer request")
            | OperationError -> raise (InvalidOperationException "The PDF renderer could not complete this operation."))
        task {
            try return! pending
            with :? ObjectDisposedException as e ->
                // The process-exit callback may stop the queue before this request reaches it.
                // Preserve a renderer failure rather than exposing an implementation detail.
                ensureOpen ()
                return raise e
        }

    member _.Close() =
        lock gate (fun () -> closed <- true)
        cleanup ()

    interface IDisposable with
        member this.Dispose() = this.Close()

let internal startInfo () =
    // Assembly.Location is empty in a single-file publish. Locate the adjacent apphost
    // for tests/dotnet launches; a running apphost can relaunch itself even if renamed.
    let rendererAssembly = typeof<RendererSession>.Assembly
    let assembly = Path.Combine(AppContext.BaseDirectory, rendererAssembly.GetName().Name + ".dll")
    let host = Path.ChangeExtension(assembly, if OperatingSystem.IsWindows() then ".exe" else null)
    let psi = ProcessStartInfo(UseShellExecute = false, CreateNoWindow = true,
                              RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true)
    let currentHost = Environment.ProcessPath
    let isAppHost =
        System.Reflection.Assembly.GetEntryAssembly() = rendererAssembly
        && not (String.IsNullOrEmpty currentHost)
        && not (String.Equals(Path.GetFileNameWithoutExtension currentHost, "dotnet", StringComparison.OrdinalIgnoreCase))
    if isAppHost then psi.FileName <- currentHost
    elif File.Exists host then psi.FileName <- host
    else
        psi.FileName <- "dotnet"
        psi.ArgumentList.Add assembly
    psi.ArgumentList.Add "--renderer"
    psi

/// The UI's document proxy. No PDFium initialisation or native calls happen in this process.
[<AllowNullLiteral>]
type RemoteDocument internal (session: RendererSession, id: int, path: string, info: DocumentInfo) =
    let mutable closing = 0

    member _.Id = id
    member _.Path = path
    member _.Info = info
    member _.IsClosed = Volatile.Read(&closing) <> 0
    member _.Failure = session.Failure
    member internal _.ProcessId = session.ProcessId
    member internal _.MemoryBytes = session.MemoryBytes
    [<CLIEvent>]
    member _.Failed = session.Failed

    member _.RenderAsync(req: RenderRequest, priority: Priority, ct: CancellationToken) =
        session.Invoke(Command.Render, (fun w -> writeRenderRequest w req), readImage, priority, ct)

    member _.GetTextAsync(page: int, priority: Priority, ct: CancellationToken) =
        session.Invoke(Command.Text, (fun w -> w.Write page), (fun r ->
            let text = readText r
            if text.Page <> page then invalid ()
            text), priority, ct)

    member _.GetLinksAsync(page: int, priority: Priority, ct: CancellationToken) =
        session.Invoke(Command.Links, (fun w -> w.Write page), readLinks, priority, ct)

    member _.GetOutlineAsync(ct: CancellationToken) =
        session.Invoke(Command.Outline, ignore, readOutline, Priority.Prefetch, ct)

    member _.ResolveAsync(dest: Destination, ct: CancellationToken) =
        session.Invoke(Command.Resolve, (fun w -> writeDestination w dest), (fun r ->
            let target = readDisplayDestination r
            if target.Page <> dest.Page || target.Page < 0 || target.Page >= info.PageCount then invalid ()
            target), Priority.Interactive, ct)

    /// Closing never queues behind a running native call: terminate the dedicated helper.
    member _.CloseAsync() : Task =
        if Interlocked.Exchange(&closing, 1) = 0 then session.Close()
        Task.CompletedTask

    interface IDisposable with
        member this.Dispose() = this.CloseAsync().GetAwaiter().GetResult()
    interface IAsyncDisposable with
        member this.DisposeAsync() = ValueTask(this.CloseAsync())

/// Stateless factory: each successful open owns a fresh helper, never shared with another tab.
type RendererEngine internal (startSession: unit -> RendererSession) =
    static let shared = lazy (RendererEngine())
    static let mutable nextId = 0

    new() = RendererEngine(fun () -> new RendererSession(startInfo (), TimeSpan.FromSeconds 60.0))
    static member Shared = shared.Value

    member _.OpenAsync(path: string, password: string option, ct: CancellationToken) : Task<Result<RemoteDocument, OpenError>> =
        task {
            ct.ThrowIfCancellationRequested()
            let full = try Path.GetFullPath path with _ -> path
            // Starting processes must not block the UI, and cancellation must not leak an open helper.
            let! session = Task.Run startSession
            try
                use _registration = ct.Register(fun () -> session.Close())
                let! result = session.Invoke(Command.Open, (fun w ->
                    w.Write version
                    writeString w full
                    writeOption writeString w password), (fun r ->
                    if r.ReadBoolean() then Ok(readInfo r) else Error(readOpenError r)), Priority.Interactive, ct)
                ct.ThrowIfCancellationRequested()
                match result with
                | Ok info ->
                    let id = Interlocked.Increment(&nextId)
                    return Ok(new RemoteDocument(session, id, full, info))
                | Error e ->
                    session.Close()
                    return Error e
            with e ->
                session.Close()
                ct.ThrowIfCancellationRequested()
                return raise e
        }

    member this.OpenAsync(path: string, ?password: string) =
        this.OpenAsync(path, password, CancellationToken.None)
