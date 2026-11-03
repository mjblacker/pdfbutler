// dotnet fsi tools/Measure-Startup.fsx --executable <path> [--repetitions 20] [--output <json>] <pdf> [<pdf> ...]
open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text.Json

type Options = { Executable: string; Documents: string list; Repetitions: int; Output: string }
let usage = "Usage: dotnet fsi tools/Measure-Startup.fsx --executable <path> [--repetitions 20..1000] [--output <json>] <pdf> [<pdf> ...]"
let rec parse options = function
    | [] -> options
    | "--executable" :: path :: rest -> parse { options with Executable = path } rest
    | "--repetitions" :: count :: rest ->
        match Int32.TryParse count with
        | true, n when n >= 20 && n <= 1000 -> parse { options with Repetitions = n } rest
        | _ -> failwith usage
    | "--output" :: path :: rest -> parse { options with Output = path } rest
    | arg :: _ when arg.StartsWith("--") -> failwith usage
    | path :: rest -> parse { options with Documents = options.Documents @ [path] } rest

let options =
    parse { Executable = ""; Documents = []; Repetitions = 20
            Output = Path.Combine(__SOURCE_DIRECTORY__, "../artifacts/startup.json") }
        (fsi.CommandLineArgs |> Array.skip 1 |> Array.toList)
if options.Executable = "" || options.Documents.IsEmpty then failwith usage
let existingFile path =
    let full = Path.GetFullPath path
    if not (File.Exists full) then failwithf "File does not exist: %s" full
    full
let executable = existingFile options.Executable
let documents = options.Documents |> List.map existingFile

let runMeasurement document =
    let start = ProcessStartInfo(executable, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)
    start.ArgumentList.Add "--measure"
    start.ArgumentList.Add document
    use child = Process.Start start
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    if not (child.WaitForExit 65000) then
        child.Kill(true)
        child.WaitForExit()
        failwith "Measurement exceeded 65 seconds."
    let json = stdout.GetAwaiter().GetResult()
    let errors = stderr.GetAwaiter().GetResult()
    if child.ExitCode <> 0 then failwithf "Measurement failed (%d): %s %s" child.ExitCode errors json
    use parsed = JsonDocument.Parse json
    parsed.RootElement.Clone()

let percentile fraction (values: float array) =
    let sorted = Array.sort values
    Math.Round(sorted[int (Math.Ceiling(fraction * float sorted.Length)) - 1], 1)
let number (name: string) (sample: JsonElement) = sample.GetProperty(name).GetDouble()
let samples = ResizeArray<JsonElement>()
let summary =
    [| for document in documents do
        runMeasurement document |> ignore // warm filesystem cache, discarded warm-up
        let runs = Array.init options.Repetitions (fun _ -> runMeasurement document)
        samples.AddRange runs
        let launch = runs |> Array.map (number "first-page-presented")
        let opened = runs |> Array.map (fun sample -> number "first-page-presented" sample - number "open-start" sample)
        yield {| document = document; repetitions = options.Repetitions
                 launchP50Ms = percentile 0.50 launch; launchP95Ms = percentile 0.95 launch
                 openP50Ms = percentile 0.50 opened; openP95Ms = percentile 0.95 opened
                 workingSetP95MB = runs |> Array.map (number "workingSetMB") |> percentile 0.95 |} |]
let result =
    {| dateUtc = DateTime.UtcNow.ToString("o"); executable = executable
       os = RuntimeInformation.OSDescription; architecture = RuntimeInformation.OSArchitecture.ToString()
       filesystemCache = "Warm (one discarded warm-up per document); each sample is a new process."
       summary = summary; samples = samples.ToArray() |}
let output = Path.GetFullPath options.Output
Directory.CreateDirectory(Path.GetDirectoryName output) |> ignore
File.WriteAllText(output, JsonSerializer.Serialize(result, JsonSerializerOptions(WriteIndented = true)))
for row in summary do
    printfn "%s: launch p50/p95 %.1f/%.1f ms; open %.1f/%.1f ms; UI working set p95 %.1f MiB"
        row.document row.launchP50Ms row.launchP95Ms row.openP50Ms row.openP95Ms row.workingSetP95MB
printfn "Saved %s" output
