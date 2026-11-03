namespace PdfButler.Pdfium

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// Scheduling priority for engine work. Lower runs first.
type Priority =
    /// Opening documents and rendering what is on screen right now.
    | Interactive = 0
    /// Neighbouring pages, links/text for visible pages.
    | Prefetch = 1
    /// Search, bulk text extraction.
    | Background = 2

type internal Job =
    { Run: unit -> unit
      /// True if the job's result is no longer wanted (cancelled before it started).
      IsAbandoned: unit -> bool }

/// A single dedicated thread that owns all PDFium calls for the whole process.
///
/// PDFium documents: "None of the PDFium APIs are thread-safe." That covers *all* documents,
/// so every native call (open, render, text, close) is funnelled through this queue.
/// Cancellation removes work that has not started; it cannot interrupt a running native call.
type Worker(name: string, init: unit -> unit) =
    let queue = PriorityQueue<Job, struct (int * int64)>()
    let gate = obj ()
    let mutable seq = 0L
    let mutable stopping = false
    let mutable ownerThreadId = -1
    let initDone = new ManualResetEventSlim(false)
    let mutable initError: exn option = None

    let loop () =
        ownerThreadId <- Thread.CurrentThread.ManagedThreadId

        try
            init ()
        with e ->
            initError <- Some e

        initDone.Set()

        let mutable running = true

        while running do
            let job =
                lock gate (fun () ->
                    while queue.Count = 0 && not stopping do
                        Monitor.Wait gate |> ignore

                    if queue.Count = 0 then None else Some(queue.Dequeue()))

            match job with
            | None -> running <- false
            | Some j -> if not (j.IsAbandoned()) then j.Run()

    let thread =
        let t = Thread(loop, 4 * 1024 * 1024)
        t.IsBackground <- true
        t.Name <- name
        t.Start()
        t

    /// True when called from the owner thread.
    member _.IsOwnerThread = Thread.CurrentThread.ManagedThreadId = ownerThreadId

    /// Number of jobs waiting (including abandoned ones not yet skipped).
    member _.QueueLength = lock gate (fun () -> queue.Count)

    /// Blocks until the owner thread has finished initialisation; rethrows any init failure.
    member _.EnsureInitialized() =
        initDone.Wait()

        match initError with
        | Some e -> raise (InvalidOperationException($"PDF engine failed to initialise: {e.Message}", e))
        | None -> ()

    member this.Invoke<'T>(priority: Priority, ct: CancellationToken, f: unit -> 'T) : Task<'T> =
        let tcs = TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)

        if ct.IsCancellationRequested then
            tcs.TrySetCanceled ct |> ignore
            tcs.Task
        else
            let reg =
                if ct.CanBeCanceled then
                    ct.Register(fun () -> tcs.TrySetCanceled ct |> ignore)
                else
                    new CancellationTokenRegistration()

            let job =
                { Run =
                    fun () ->
                        try
                            try
                                if not tcs.Task.IsCompleted then
                                    match initError with
                                    | Some e -> tcs.TrySetException(InvalidOperationException("PDF engine is unavailable", e)) |> ignore
                                    | None -> tcs.TrySetResult(f ()) |> ignore
                            with e ->
                                tcs.TrySetException e |> ignore
                        finally
                            reg.Dispose()
                  IsAbandoned =
                    fun () ->
                        let abandoned = tcs.Task.IsCompleted
                        if abandoned then reg.Dispose()
                        abandoned }

            if this.IsOwnerThread then
                // Re-entrant call from engine code: run inline to avoid deadlock.
                job.Run()
            else
                lock gate (fun () ->
                    if stopping then
                        tcs.TrySetException(ObjectDisposedException name) |> ignore
                    else
                        seq <- seq + 1L
                        queue.Enqueue(job, struct (int priority, seq))
                        Monitor.Pulse gate)

            tcs.Task

    member this.Invoke<'T>(priority: Priority, f: unit -> 'T) : Task<'T> =
        this.Invoke(priority, CancellationToken.None, f)

    /// Run synchronously on the owner thread and wait (tests / shutdown only).
    member this.Run<'T>(f: unit -> 'T) : 'T =
        this.Invoke(Priority.Interactive, f).GetAwaiter().GetResult()

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                stopping <- true
                Monitor.PulseAll gate)

            if Thread.CurrentThread.ManagedThreadId <> ownerThreadId then
                thread.Join(TimeSpan.FromSeconds 5.0) |> ignore
