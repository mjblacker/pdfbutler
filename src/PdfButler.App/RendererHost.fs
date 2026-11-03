/// Headless entry point, reached before Avalonia or single-instance activation starts.
module internal PdfButler.App.RendererHost

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open PdfButler.Core
open PdfButler.Pdfium
open PdfButler.App.RendererProtocol

[<System.Runtime.InteropServices.DllImport("kernel32.dll")>]
extern uint32 private SetErrorMode(uint32 mode)

let run () =
    // A PDFium crash must not open a Windows Error Reporting dialog and hang the broker.
    if OperatingSystem.IsWindows() then
        SetErrorMode(0x0001u ||| 0x0002u ||| 0x8000u) |> ignore
    use input = Console.OpenStandardInput()
    use output = Console.OpenStandardOutput()
    use requests = new BlockingCollection<byte[]>(1)
    // Keep reading independently of native calls: parent death/closed stdin terminates even
    // a helper stuck forever in PDFium. Never call PDFium during this emergency shutdown.
    Task.Run(fun () ->
        try
            while true do
                let bytes = readFrameAsync input CancellationToken.None |> fun t -> t.GetAwaiter().GetResult()
                requests.Add bytes
        with _ -> Environment.Exit 0) |> ignore
    let mutable document: PdfDocument = null
    try
        for bytes in requests.GetConsumingEnumerable() do
            let reply =
                try
                    let payload = decode (fun r ->
                        let command = enum<Command>(r.ReadInt32())
                        let ready () = if isNull document then invalid ()
                        let wait (t: Task<'T>) = t.GetAwaiter().GetResult()
                        match command with
                        | Command.Open ->
                            if not (isNull document) || r.ReadInt32() <> version then invalid ()
                            let path = readString r
                            let password = readOption readString r
                            let result = PdfEngine.Shared.OpenAsync(path, password, CancellationToken.None) |> wait
                            encode (fun w ->
                                match result with
                                | Ok d ->
                                    document <- d
                                    w.Write true
                                    writeInfo w d.Info
                                | Error e -> w.Write false; writeOpenError w e)
                        | Command.Render ->
                            ready ()
                            let req = readRenderRequest r
                            document.RenderAsync(req, Priority.Interactive, CancellationToken.None) |> wait |> fun v -> encode (fun w -> writeImage w v)
                        | Command.Text ->
                            ready ()
                            document.GetTextAsync(r.ReadInt32(), Priority.Interactive, CancellationToken.None) |> wait |> fun v -> encode (fun w -> writeText w v)
                        | Command.Links ->
                            ready ()
                            document.GetLinksAsync(r.ReadInt32(), Priority.Interactive, CancellationToken.None) |> wait |> fun v -> encode (fun w -> writeLinks w v)
                        | Command.Outline ->
                            ready ()
                            document.GetOutlineAsync(CancellationToken.None) |> wait |> fun v -> encode (fun w -> writeOutline w v)
                        | Command.Resolve ->
                            ready ()
                            document.ResolveAsync(readDestination r, CancellationToken.None) |> wait |> fun v -> encode (fun w -> writeDisplayDestination w v)
                        | _ -> invalid ()) bytes
                    Array.append [| 0uy |] payload
                with e ->
                    // Preserve expected page/limit errors without treating them as process failure.
                    // Do not expose arbitrary exception messages (which might contain secrets).
                    encode (fun w ->
                        match e with
                        | PageLoadException page -> w.Write 1uy; w.Write page
                        | :? ArgumentOutOfRangeException -> w.Write 2uy
                        | _ -> w.Write 3uy)
            writeFrameAsync output reply CancellationToken.None |> fun t -> t.GetAwaiter().GetResult()
        0
    with _ -> 2
