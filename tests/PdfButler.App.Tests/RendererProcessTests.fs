/// Real helper lifecycle/IPC tests; no deliberately crashing PDF is needed to kill a renderer.
module PdfButler.App.Tests.RendererProcessTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open PdfButler.Core
open PdfButler.Pdfium
open PdfButler.App.RendererProcess
open PdfButler.App.RendererProtocol
open PdfButler.App.Tests.Harness
open PdfButler.TestSupport

let private wait (t: Task<'T>) = t.WaitAsync(TimeSpan.FromSeconds 15.0).GetAwaiter().GetResult()
let private document () =
    let p = { SyntheticPdf.page 300.0 400.0 with
                Texts = [ { X = 40.0; Y = 300.0; Size = 16.0; Text = "Helper test" } ]
                Links = [ { Area = SyntheticPdf.box 40.0 280.0 150.0 310.0; Target = SyntheticPdf.ToPage(1, Some 250.0) }
                          { Area = SyntheticPdf.box 40.0 200.0 150.0 230.0; Target = SyntheticPdf.ToUri "https://example.org/" } ] }
    let spec = { SyntheticPdf.doc [ p; p ] with
                    Title = Some "Renderer metadata"
                    Outline = [ { Title = "Root"; Page = 1; Top = Some 250.0
                                  Children = [ { Title = "Child"; Page = 0; Top = None; Children = [] } ] } ]
                    Labels = [ { StartPage = 0; Style = "r"; Prefix = ""; First = 1 } ] }
    synthetic ($"renderer-{Guid.NewGuid():N}.pdf") spec

let private openRemote path =
    match RendererEngine.Shared.OpenAsync path |> wait with
    | Ok d -> d
    | Error e -> failwithf "open failed: %A" e

[<Fact>]
let ``helper preserves metadata text links outline destinations and rotated tile pixels`` () =
    let path = document ()
    use remote = openRemote path
    use direct =
        match PdfEngine.Shared.OpenAsync path |> wait with
        | Ok d -> d
        | Error e -> failwithf "%A" e
    Assert.NotEqual(Environment.ProcessId, remote.ProcessId)
    Assert.Equal(direct.Info, remote.Info)
    Assert.Equal(direct.GetTextAsync(0, Priority.Background, CancellationToken.None) |> wait,
                 remote.GetTextAsync(0, Priority.Background, CancellationToken.None) |> wait)
    Assert.Equal<Link[]>(direct.GetLinksAsync(0, Priority.Prefetch, CancellationToken.None) |> wait,
                         remote.GetLinksAsync(0, Priority.Prefetch, CancellationToken.None) |> wait)
    Assert.Equal<OutlineItem list>(direct.GetOutlineAsync(CancellationToken.None) |> wait,
                                  remote.GetOutlineAsync(CancellationToken.None) |> wait)
    let dest: Destination = { Page = 1; X = Some 40.0; Y = Some 250.0 }
    Assert.Equal(direct.ResolveAsync(dest, CancellationToken.None) |> wait,
                 remote.ResolveAsync(dest, CancellationToken.None) |> wait)
    for rot in [ R0; R90; R180; R270 ] do
        for clip in [ None; Some { X = 12; Y = 30; W = 130; H = 170 } ] do
            let req = { Page = 0; Rotation = rot; Scale = 1.25; Clip = clip }
            let actual = remote.RenderAsync(req, Priority.Interactive, CancellationToken.None) |> wait
            let expected = direct.RenderAsync(req, Priority.Interactive, CancellationToken.None) |> wait
            Assert.Equal(expected.Width, actual.Width)
            Assert.Equal(expected.Height, actual.Height)
            Assert.Equal(expected.Stride, actual.Stride)
            Assert.Equal<byte[]>(expected.Pixels, actual.Pixels)

[<Fact>]
let ``killing one helper faults its requests without affecting another document`` () =
    let path = document ()
    use a = openRemote path
    use b = openRemote path
    Assert.NotEqual(a.Id, b.Id)
    Assert.NotEqual(a.ProcessId, b.ProcessId)
    use child = Process.GetProcessById a.ProcessId
    child.Kill(true)
    Assert.True(child.WaitForExit 5000)
    Assert.True(SpinWait.SpinUntil((fun () -> a.Failure.IsSome), 5000))
    Assert.Throws<RendererUnavailableException>(fun () -> a.GetTextAsync(0, Priority.Interactive, CancellationToken.None) |> wait |> ignore) |> ignore
    Assert.NotEmpty((b.GetTextAsync(0, Priority.Interactive, CancellationToken.None) |> wait).Chars)
    use reopened = openRemote path
    Assert.NotEqual(a.Id, reopened.Id)
    Assert.NotEmpty((reopened.GetTextAsync(0, Priority.Interactive, CancellationToken.None) |> wait).Chars)

[<Fact>]
let ``helper death during an open faults both in flight and queued work`` () =
    use session = new RendererSession(startInfo (), TimeSpan.FromSeconds 5.0)
    use child = Process.GetProcessById session.ProcessId
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    let opening = session.Invoke(Command.Open, (fun w ->
        entered.Set()
        if not (release.Wait 5000) then failwith "test barrier timed out"
        w.Write version
        writeString w (document ())
        writeOption writeString w None), (fun _ -> ()), Priority.Interactive, CancellationToken.None)
    Assert.True(entered.Wait 5000)
    let queued = session.Invoke(Command.Text, (fun w -> w.Write 0), readText, Priority.Background, CancellationToken.None)
    try child.Kill(true)
    finally release.Set()
    Assert.Throws<RendererUnavailableException>(fun () -> opening |> wait) |> ignore
    Assert.Throws<RendererUnavailableException>(fun () -> queued |> wait |> ignore) |> ignore

[<Fact>]
let ``close kills helper immediately and is idempotent`` () =
    let d = openRemote (document ())
    use child = Process.GetProcessById d.ProcessId
    d.CloseAsync().GetAwaiter().GetResult()
    d.CloseAsync().GetAwaiter().GetResult()
    Assert.True(d.IsClosed)
    Assert.True(child.WaitForExit 5000)
    Assert.Throws<ObjectDisposedException>(fun () -> d.GetTextAsync(0, Priority.Interactive, CancellationToken.None) |> wait |> ignore) |> ignore

[<Fact>]
let ``timeout terminates an unresponsive helper and releases pending work`` () =
    // One millisecond includes cold process startup: intentionally too short for a reply.
    use session = new RendererSession(startInfo (), TimeSpan.FromMilliseconds 1.0)
    use child = Process.GetProcessById session.ProcessId
    let opening = session.Invoke(Command.Open, (fun w ->
        w.Write version; writeString w (document ()); writeOption writeString w None),
        (fun _ -> ()), Priority.Interactive, CancellationToken.None)
    Assert.Throws<RendererUnavailableException>(fun () -> opening |> wait) |> ignore
    Assert.True(child.WaitForExit 5000)
    Assert.Contains("time limit", session.Failure.Value)

[<Fact>]
let ``closing parent pipe exits an idle helper without explicit kill`` () =
    use child = new Process(StartInfo = startInfo ())
    Assert.True(child.Start())
    child.StandardInput.Close()
    Assert.True(child.WaitForExit 5000)
    Assert.Equal(0, child.ExitCode)

[<Fact>]
let ``failed and cancelled opens do not leave helpers running`` () =
    let children = Collections.Concurrent.ConcurrentBag<Process>()
    let launch () =
        let session = new RendererSession(startInfo (), TimeSpan.FromSeconds 5.0)
        children.Add(Process.GetProcessById session.ProcessId)
        session
    let engine = RendererEngine(launch)
    let path = document ()
    try
        use cancelled = new CancellationTokenSource()
        cancelled.Cancel()
        Assert.ThrowsAny<OperationCanceledException>(fun () -> engine.OpenAsync(path, None, cancelled.Token) |> wait |> ignore) |> ignore
        Assert.Empty children
        match engine.OpenAsync(path + ".missing") |> wait with
        | Error(FileNotFound _) -> ()
        | r -> failwithf "unexpected result %A" r
        let malformed = path + ".malformed"
        File.WriteAllText(malformed, "Not a PDF")
        match engine.OpenAsync malformed |> wait with
        | Error Malformed -> ()
        | r -> failwithf "unexpected result %A" r
        Assert.Equal(2, children.Count)
        for child in children do Assert.True(child.WaitForExit 5000)
    finally
        for child in children do child.Dispose()

[<Fact>]
let ``cancelling while a helper starts cannot leak or return a document`` () =
    use started = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    let mutable child: Process = null
    let launch () =
        let session = new RendererSession(startInfo (), TimeSpan.FromSeconds 5.0)
        child <- Process.GetProcessById session.ProcessId
        started.Set()
        if not (release.Wait 5000) then failwith "test barrier timed out"
        session
    let engine = RendererEngine(launch)
    use cts = new CancellationTokenSource()
    let opening = engine.OpenAsync(document (), None, cts.Token)
    try
        Assert.True(started.Wait 5000)
        cts.Cancel()
        release.Set()
        Assert.ThrowsAny<OperationCanceledException>(fun () -> opening |> wait |> ignore) |> ignore
        Assert.True(child.WaitForExit 5000)
    finally
        release.Set()
        if not (isNull child) then child.Dispose()

[<Fact>]
let ``cancelling a queued request does not desynchronise later replies`` () =
    use session = new RendererSession(startInfo (), TimeSpan.FromSeconds 5.0)
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    let path = document ()
    let opening = session.Invoke(Command.Open, (fun w ->
        entered.Set()
        if not (release.Wait 5000) then failwith "test barrier timed out"
        w.Write version; writeString w path; writeOption writeString w None),
        (fun r -> if r.ReadBoolean() then Ok(readInfo r) else Error(readOpenError r)), Priority.Interactive, CancellationToken.None)
    try
        Assert.True(entered.Wait 5000)
        use cts = new CancellationTokenSource()
        let cancelled = session.Invoke(Command.Text, (fun w -> w.Write 0), readText, Priority.Background, cts.Token)
        cts.Cancel()
        Assert.ThrowsAny<OperationCanceledException>(fun () -> cancelled |> wait |> ignore) |> ignore
        release.Set()
        match opening |> wait with
        | Ok info -> Assert.Equal(2, info.PageCount)
        | Error e -> failwithf "%A" e
        let text = session.Invoke(Command.Text, (fun w -> w.Write 1), readText, Priority.Interactive, CancellationToken.None) |> wait
        Assert.Equal(1, text.Page)
        Assert.NotEmpty text.Chars
    finally release.Set()

[<Fact>]
let ``a missing helper executable is a recoverable engine error`` () =
    let psi = startInfo ()
    psi.FileName <- Path.Combine(tempDir.Value, "missing-renderer-executable")
    Assert.Throws<RendererUnavailableException>(fun () ->
        use _session = new RendererSession(psi, TimeSpan.FromSeconds 5.0)
        ()) |> ignore

[<Theory>]
[<InlineData(-1)>]
[<InlineData(0)>]
[<InlineData(83886081)>]
let ``invalid frame lengths are rejected before allocation`` length =
    use stream = new MemoryStream(BitConverter.GetBytes(length: int))
    Assert.Throws<InvalidDataException>(fun () -> readFrameAsync stream CancellationToken.None |> wait |> ignore) |> ignore

[<Fact>]
let ``truncated frames and invalid pixel strides are rejected`` () =
    use stream = new MemoryStream(Array.append (BitConverter.GetBytes 20) [| 1uy |])
    Assert.Throws<EndOfStreamException>(fun () -> readFrameAsync stream CancellationToken.None |> wait |> ignore) |> ignore
    let badImage = encode (fun w -> w.Write 100; w.Write 100; w.Write 1)
    Assert.Throws<InvalidDataException>(fun () -> decode readImage badImage |> ignore) |> ignore
    let oversized = encode (fun w -> w.Write Int32.MaxValue; w.Write Int32.MaxValue; w.Write 4)
    Assert.Throws<InvalidDataException>(fun () -> decode readImage oversized |> ignore) |> ignore

[<Fact>]
let ``oversized arrays strings deep outlines and trailing bytes are rejected`` () =
    let oversized = encode (fun w -> w.Write (maxPages + 1))
    Assert.Throws<InvalidDataException>(fun () -> decode readInfo oversized |> ignore) |> ignore
    let bigString = encode (fun w -> w.Write (maxStringBytes + 1))
    Assert.Throws<InvalidDataException>(fun () -> decode readString bigString |> ignore) |> ignore
    let rec nodes n : OutlineItem list =
        if n = 0 then []
        else [ { Title = "node"; Target = None; StartsOpen = false; Children = nodes (n - 1) } ]
    let deep = encode (fun w -> writeOutline w (nodes (Limits.maxOutlineDepth + 1)))
    Assert.Throws<InvalidDataException>(fun () -> decode readOutline deep |> ignore) |> ignore
    let trailing = encode (fun w -> writeString w "test"; w.Write 123)
    Assert.Throws<InvalidDataException>(fun () -> decode readString trailing |> ignore) |> ignore

[<Fact>]
let ``all typed open errors round trip without losing payloads`` () =
    for error in [ FileNotFound "missing.pdf"; FileUnreadable "denied"; FileTooLarge 9876543210L
                   PasswordRequired; WrongPassword; UnsupportedSecurity; Malformed; EngineError 42 ] do
        Assert.Equal(error, encode (fun w -> writeOpenError w error) |> decode readOpenError)
