/// The owner-thread queue: ordering, cancellation, errors. Uses a private Worker, not PDFium.
module PdfButler.Engine.Tests.WorkerTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Xunit
open PdfButler.Pdfium

let private newWorker () = new Worker("test-worker", ignore)

/// Occupy the worker so that subsequently queued jobs pile up.
let private block (w: Worker) =
    let gate = new ManualResetEventSlim(false)
    let started = new ManualResetEventSlim(false)
    let t = w.Invoke(Priority.Interactive, fun () -> started.Set(); gate.Wait())
    started.Wait(TimeSpan.FromSeconds 10.0) |> ignore
    gate, t

[<Fact>]
let ``higher priority jobs run before lower priority ones queued earlier`` () =
    use w = newWorker ()
    let gate, blocker = block w
    let order = ConcurrentQueue<string>()
    let bg = w.Invoke(Priority.Background, fun () -> order.Enqueue "background")
    let pf = w.Invoke(Priority.Prefetch, fun () -> order.Enqueue "prefetch")
    let ia = w.Invoke(Priority.Interactive, fun () -> order.Enqueue "interactive")
    gate.Set()
    Task.WaitAll([| blocker :> Task; bg; pf; ia |], TimeSpan.FromSeconds 10.0) |> ignore
    Assert.Equal<string list>([ "interactive"; "prefetch"; "background" ], List.ofSeq order)

[<Fact>]
let ``same priority is first-in first-out`` () =
    use w = newWorker ()
    let gate, _ = block w
    let order = ConcurrentQueue<int>()
    let tasks = [| for i in 1..20 -> w.Invoke(Priority.Prefetch, fun () -> order.Enqueue i) |]
    gate.Set()
    Task.WaitAll(tasks |> Array.map (fun t -> t :> Task), TimeSpan.FromSeconds 10.0) |> ignore
    Assert.Equal<int list>([ 1..20 ], List.ofSeq order)

[<Fact>]
let ``cancelling a queued job completes it as cancelled and it never runs`` () =
    use w = newWorker ()
    let gate, _ = block w
    use cts = new CancellationTokenSource()
    let mutable ran = false
    let t = w.Invoke(Priority.Background, cts.Token, fun () -> ran <- true)
    cts.Cancel()
    // The task completes promptly even though the worker is still busy.
    Assert.True(SpinWait.SpinUntil((fun () -> t.IsCompleted), 2000))
    Assert.True(t.IsCanceled)
    gate.Set()
    w.Invoke(Priority.Background, ignore).Wait(TimeSpan.FromSeconds 10.0) |> ignore
    Assert.False ran

[<Fact>]
let ``pre-cancelled token never queues`` () =
    use w = newWorker ()
    let t = w.Invoke(Priority.Interactive, CancellationToken(true), fun () -> 42)
    Assert.True t.IsCanceled

[<Fact>]
let ``exceptions are delivered to the caller and the worker keeps running`` () =
    use w = newWorker ()
    let bad = w.Invoke(Priority.Interactive, fun () -> failwith "boom" : int)
    let ex = Assert.Throws<AggregateException>(fun () -> bad.Wait(TimeSpan.FromSeconds 5.0) |> ignore)
    Assert.Contains("boom", ex.InnerException.Message)
    Assert.Equal(7, w.Invoke(Priority.Interactive, fun () -> 7).Result)

[<Fact>]
let ``all jobs run on one dedicated thread`` () =
    use w = newWorker ()
    let ids = [| for _ in 1..50 -> w.Invoke(Priority.Prefetch, fun () -> Thread.CurrentThread.ManagedThreadId) |]
    let distinct = ids |> Array.map (fun t -> t.Result) |> Array.distinct
    Assert.Single distinct |> ignore
    Assert.NotEqual(Thread.CurrentThread.ManagedThreadId, distinct[0])

[<Fact>]
let ``re-entrant calls from the owner thread run inline instead of deadlocking`` () =
    use w = newWorker ()
    let t = w.Invoke(Priority.Interactive, fun () -> w.Invoke(Priority.Background, fun () -> 5).Result + 1)
    Assert.True(t.Wait(TimeSpan.FromSeconds 5.0))
    Assert.Equal(6, t.Result)

[<Fact>]
let ``init failure is reported instead of hanging`` () =
    use w = new Worker("failing", fun () -> failwith "no native library")
    let ex = Assert.Throws<InvalidOperationException>(fun () -> w.EnsureInitialized())
    Assert.Contains("no native library", ex.Message)
    let t = w.Invoke(Priority.Interactive, fun () -> 1)
    Assert.Throws<AggregateException>(fun () -> t.Wait(TimeSpan.FromSeconds 5.0) |> ignore) |> ignore
