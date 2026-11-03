/// Startup/performance milestones. Enabled with PDFBUTLER_TRACE=1 (stderr) or `--measure <file>`.
/// Never records document contents or passwords.
module PdfButler.App.Diagnostics

open System
open System.Diagnostics
open System.Text.Json

let private processStart =
    try Process.GetCurrentProcess().StartTime.ToUniversalTime() with _ -> DateTime.UtcNow

let private clock = Stopwatch.StartNew()
let private startOffsetMs = (DateTime.UtcNow - processStart).TotalMilliseconds
let private marks = Collections.Generic.List<string * float>()
let private traceEnabled = Environment.GetEnvironmentVariable "PDFBUTLER_TRACE" = "1"

/// Milliseconds since the OS created the process.
let sinceProcessStart () = startOffsetMs + clock.Elapsed.TotalMilliseconds

let mark (name: string) =
    let t = sinceProcessStart ()
    lock marks (fun () ->
        if not (marks.Exists(fun (n, _) -> n = name)) then
            marks.Add((name, t)))
    if traceEnabled then eprintfn "[pdfbutler] %8.1f ms  %s" t name

let snapshotJson (extra: (string * obj) list) =
    let o = Collections.Generic.Dictionary<string, obj>()
    lock marks (fun () -> for (n, t) in marks do o[n] <- box (Math.Round(t, 1)))
    use p = Process.GetCurrentProcess()
    p.Refresh()
    o["workingSetMB"] <- box (Math.Round(float p.WorkingSet64 / 1048576.0, 1))
    o["privateMB"] <- box (Math.Round(float p.PrivateMemorySize64 / 1048576.0, 1))
    o["managedMB"] <- box (Math.Round(float (GC.GetTotalMemory false) / 1048576.0, 1))
    for (k, v) in extra do o[k] <- v
    JsonSerializer.Serialize(o, JsonSerializerOptions(WriteIndented = true))
