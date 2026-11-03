/// Per-user Windows activation. The pipe accepts only bounded lists of absolute file paths,
/// never commands, URLs or document actions. Other platforms currently use separate windows.
module PdfButler.App.SingleInstance

open System
open System.Collections.Concurrent
open System.IO
open System.IO.Pipes
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

let private maxMessageBytes = 65536
let private identity =
    let user = Environment.UserDomainName + "\\" + Environment.UserName
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes user)).Substring(0, 24)

/// The pipe message: a JSON array of strings. Hand-written because Native AOT disables
/// reflection-based JsonSerializer.
let internal encodeFiles (files: string[]) =
    use stream = new MemoryStream()
    (use w = new Utf8JsonWriter(stream)
     w.WriteStartArray()
     for f in files do w.WriteStringValue f
     w.WriteEndArray())
    stream.ToArray()

/// None unless the message is a JSON array whose items are all strings.
let internal decodeFiles (bytes: byte[]) : string[] option =
    try
        use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
        let root = doc.RootElement
        if root.ValueKind <> JsonValueKind.Array then None
        else
            let items = [| for e in root.EnumerateArray() -> if e.ValueKind = JsonValueKind.String then e.GetString() else null |]
            if items |> Array.exists isNull then None else Some items
    with :? JsonException -> None

type Instance(?name: string) =
    let name = defaultArg name ("PdfButler." + identity)
    let mutex = new Mutex(false, "Local\\" + name)
    let ownsMutex = try mutex.WaitOne 0 with :? AbandonedMutexException -> true
    let cts = new CancellationTokenSource()
    let pending = ConcurrentQueue<string[]>()
    let received = Event<unit>()

    let listen () =
        task {
            while not cts.IsCancellationRequested do
                try
                    use server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                                                          PipeOptions.Asynchronous ||| PipeOptions.CurrentUserOnly)
                    do! server.WaitForConnectionAsync cts.Token
                    // A broken client must not monopolise the only activation pipe.
                    use timeout = CancellationTokenSource.CreateLinkedTokenSource cts.Token
                    timeout.CancelAfter(TimeSpan.FromSeconds 3.0)
                    let prefix = Array.zeroCreate<byte> 4
                    do! server.ReadExactlyAsync(prefix.AsMemory(), timeout.Token).AsTask()
                    let length = BitConverter.ToInt32 prefix
                    if length > 0 && length <= maxMessageBytes then
                        let bytes = Array.zeroCreate<byte> length
                        do! server.ReadExactlyAsync(bytes.AsMemory(), timeout.Token).AsTask()
                        match decodeFiles bytes with
                        | Some files when files.Length <= 10
                                          && files |> Array.forall (fun p -> not (String.IsNullOrWhiteSpace p) && p.Length <= 32767 && Path.IsPathFullyQualified p) ->
                            pending.Enqueue files
                            received.Trigger()
                        | _ -> ()
                with
                | :? OperationCanceledException -> ()
                | :? IOException -> ()
        }

    do if ownsMutex then listen () |> ignore

    member _.IsPrimary = ownsMutex
    member _.Drain() =
        [| let mutable files = Unchecked.defaultof<string[]>
           while pending.TryDequeue(&files) do yield files |]
    [<CLIEvent>]
    member _.Received = received.Publish

    member _.ForwardAsync(files: string[]) : Task<bool> =
        task {
            try
                let bytes = encodeFiles files
                if bytes.Length > maxMessageBytes then return false
                else
                    use client = new NamedPipeClientStream(".", name, PipeDirection.Out,
                                                          PipeOptions.Asynchronous ||| PipeOptions.CurrentUserOnly)
                    use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)
                    do! client.ConnectAsync timeout.Token
                    do! client.WriteAsync((BitConverter.GetBytes bytes.Length).AsMemory(), timeout.Token).AsTask()
                    do! client.WriteAsync(bytes.AsMemory(), timeout.Token).AsTask()
                    do! client.FlushAsync timeout.Token
                    return true
            with
            | :? OperationCanceledException -> return false
            | :? IOException -> return false
        }

    interface IDisposable with
        member _.Dispose() =
            cts.Cancel()
            if ownsMutex then mutex.ReleaseMutex()
            mutex.Dispose()
            // The listener still observes the token while unwinding; do not dispose its source here.
