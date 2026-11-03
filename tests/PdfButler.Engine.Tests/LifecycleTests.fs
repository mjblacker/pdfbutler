/// Opening failures, passwords, close/dispose ordering, concurrency, read-only guarantees.
module PdfButler.Engine.Tests.LifecycleTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open PdfButler.Core
open PdfButler.Pdfium
open PdfButler.TestSupport
open PdfButler.TestSupport.SyntheticPdf
open PdfButler.Engine.Tests.Helpers

let private simple =
    lazy (synthetic "simple.pdf" (doc [ for i in 1..3 -> { page 400.0 400.0 with Texts = [ { X = 40.0; Y = 200.0; Size = 30.0; Text = $"Simple {i}" } ] } ]))

let private expectError (path: string) (password: string option) =
    match openDoc path password with
    | Ok d ->
        (d :> IDisposable).Dispose()
        failwith $"expected {path} to fail to open"
    | Error e -> e

[<Fact>]
let ``missing file is reported as not found`` () =
    match expectError (tempFile "does-not-exist.pdf") None with
    | FileNotFound _ -> ()
    | e -> failwith $"got {e}"

[<Fact>]
let ``empty, random and text files are reported as malformed`` () =
    let empty = tempFile "empty.pdf"
    File.WriteAllBytes(empty, [||])
    let random = tempFile "random.pdf"
    File.WriteAllBytes(random, Array.init 65536 (fun i -> byte ((i * 7919 + 13) % 251)))
    let txt = tempFile "notes.pdf"
    File.WriteAllText(txt, "this is not a pdf\n")
    for p in [ empty; random; txt ] do
        Assert.Equal(Malformed, expectError p None)

[<Fact>]
let ``a directory is not opened`` () =
    match expectError tempDir.Value None with
    | FileNotFound _ | FileUnreadable _ -> ()
    | e -> failwith $"got {e}"

[<Fact>]
let ``truncated files fail cleanly or open with whatever is recoverable`` () =
    let full = File.ReadAllBytes simple.Value
    for fraction in [ 0.1; 0.5; 0.9; 0.99 ] do
        let p = tempFile $"truncated-{fraction}.pdf"
        File.WriteAllBytes(p, full[.. int (float full.Length * fraction)])
        match openDoc p None with
        | Error _ -> ()
        | Ok d ->
            use d = d
            for i in 0 .. d.Info.PageCount - 1 do
                try render d i R0 0.5 None |> ignore
                with e when (e.GetBaseException() :? PageLoadException) -> ()

[<Fact>]
let ``unicode file names and paths with spaces open`` () =
    let dir = Path.Combine(tempDir.Value, "dossier spécial 文件")
    Directory.CreateDirectory dir |> ignore
    let p = Path.Combine(dir, "résumé – ünïcødé.pdf")
    File.Copy(simple.Value, p, true)
    use d = openOk p
    Assert.Equal(3, d.Info.PageCount)

[<Fact>]
let ``opening never modifies the file and does not lock it against other readers or writers`` () =
    let p = tempFile "readonly-check.pdf"
    File.Copy(simple.Value, p, true)
    let before = Corpus.sha256File p
    let stamp = File.GetLastWriteTimeUtc p
    use d = openOk p
    render d 0 R0 1.0 None |> ignore
    text d 1 |> ignore
    // Another process-style writer can still open the file while we hold it.
    (use other = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)
     Assert.True other.CanWrite)
    (d :> IDisposable).Dispose()
    Assert.Equal(before, Corpus.sha256File p)
    Assert.Equal(stamp, File.GetLastWriteTimeUtc p)

[<Fact>]
let ``calls after close fail with ObjectDisposedException, and close is idempotent`` () =
    let d = openOk simple.Value
    await' (d.CloseAsync().ContinueWith(fun (_: Task) -> 0)) |> ignore
    Assert.True d.IsClosed
    let t = d.RenderAsync({ Page = 0; Rotation = R0; Scale = 1.0; Clip = None }, Priority.Interactive, CancellationToken.None)
    let ex = Assert.ThrowsAny<exn>(fun () -> t.Wait(timeout) |> ignore)
    Assert.IsType<ObjectDisposedException>(ex.GetBaseException()) |> ignore
    d.CloseAsync().Wait(timeout) |> ignore
    (d :> IDisposable).Dispose()

[<Fact>]
let ``close while work is queued: queued jobs finish or report disposal, never crash`` () =
    let d = openOk simple.Value
    let jobs =
        [| for i in 0..59 ->
               d.RenderAsync({ Page = i % 3; Rotation = R0; Scale = 1.0; Clip = None }, Priority.Background, CancellationToken.None) :> Task |]
    let closing = d.CloseAsync()
    Assert.True(closing.Wait timeout)
    for j in jobs do
        try j.Wait timeout |> ignore
        with :? AggregateException as e -> Assert.IsType<ObjectDisposedException>(e.GetBaseException()) |> ignore
    Assert.True(jobs |> Array.forall (fun j -> j.IsCompleted))

[<Fact>]
let ``page index out of range is an argument error, not a crash`` () =
    use d = openOk simple.Value
    for bad in [ -1; 3; Int32.MaxValue ] do
        let ex = Assert.ThrowsAny<exn>(fun () -> render d bad R0 1.0 None |> ignore)
        Assert.IsType<ArgumentOutOfRangeException>(ex.GetBaseException()) |> ignore

[<Fact>]
let ``nonsense render requests are refused`` () =
    use d = openOk simple.Value
    for scale in [ 0.0; -1.0; Double.NaN ] do
        Assert.ThrowsAny<exn>(fun () -> render d 0 R0 scale None |> ignore) |> ignore
    Assert.ThrowsAny<exn>(fun () -> render d 0 R0 1.0 (Some { X = 5000; Y = 5000; W = 10; H = 10 }) |> ignore) |> ignore
    Assert.ThrowsAny<exn>(fun () -> render d 0 R0 1.0 (Some { X = 0; Y = 0; W = 0; H = 10 }) |> ignore) |> ignore

[<Fact>]
let ``many threads opening, rendering and closing concurrently`` () =
    let work (k: int) =
        task {
            for _ in 1..10 do
                match! engine.OpenAsync(simple.Value, None, CancellationToken.None) with
                | Ok d ->
                    let! img = d.RenderAsync({ Page = k % 3; Rotation = Rotation.ofQuarterTurns k; Scale = 0.5; Clip = None }, Priority.Prefetch, CancellationToken.None)
                    let! t = d.GetTextAsync(k % 3, Priority.Background, CancellationToken.None)
                    if img.Width <= 0 || t.Chars.Length = 0 then failwith "bad result"
                    do! d.CloseAsync()
                | Error e -> failwith $"open failed: {e}"
        }
    let all = Task.WhenAll([| for k in 0..7 -> Task.Run<unit>(fun () -> work k) |])
    Assert.True(all.Wait(TimeSpan.FromMinutes 2.0))

[<Fact>]
let ``repeated open and close does not exhaust resources`` () =
    for _ in 1..300 do
        use d = openOk simple.Value
        render d 0 R0 0.25 None |> ignore

[<Fact>]
let ``each open gets a new document id`` () =
    use a = openOk simple.Value
    use b = openOk simple.Value
    Assert.NotEqual(a.Id, b.Id)
