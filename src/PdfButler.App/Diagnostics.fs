/// Startup/performance milestones. Enabled with PDFBUTLER_TRACE=1 (stderr) or `--measure <file>`.
/// Never records document contents or passwords.
module PdfButler.App.Diagnostics

open System
open System.Diagnostics
open System.Globalization
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
    if traceEnabled then
        Console.Error.WriteLine(String.Format(CultureInfo.InvariantCulture, "[pdfbutler] {0,8:F1} ms  {1}", t, name))

/// JSON object of the marks, process memory and `extra` values (string, int, int64, float or bool).
/// Written with Utf8JsonWriter rather than reflection-based serialization, which Native AOT disables.
let snapshotJson (extra: (string * obj) list) =
    let values = Collections.Generic.List<string * obj>()
    lock marks (fun () -> for (n, t) in marks do values.Add((n, box (Math.Round(t, 1)))))
    use p = Process.GetCurrentProcess()
    p.Refresh()
    values.Add(("workingSetMB", box (Math.Round(float p.WorkingSet64 / 1048576.0, 1))))
    values.Add(("privateMB", box (Math.Round(float p.PrivateMemorySize64 / 1048576.0, 1))))
    values.Add(("managedMB", box (Math.Round(float (GC.GetTotalMemory false) / 1048576.0, 1))))
    values.AddRange extra
    use stream = new IO.MemoryStream()
    (use w = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
     w.WriteStartObject()
     for (name, value) in values do
         match value with
         | null -> w.WriteNull name
         | :? string as s -> w.WriteString(name, s)
         | :? int as i -> w.WriteNumber(name, i)
         | :? int64 as i -> w.WriteNumber(name, i)
         | :? float as f -> w.WriteNumber(name, f)
         | :? bool as b -> w.WriteBoolean(name, b)
         | v -> w.WriteString(name, v.ToString())
     w.WriteEndObject())
    Text.Encoding.UTF8.GetString(stream.ToArray())
