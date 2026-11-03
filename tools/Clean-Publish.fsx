// Called by Deployment.targets. Clean only the standard publish directory by default;
// a custom directory requires explicit opt-in. Never follow a linked output/ancestor.
open System
open System.IO

let repository, runtime, output, mode =
    match fsi.CommandLineArgs |> Array.skip 1 |> Array.toList with
    | [repository; runtime; output; mode] when runtime = "win-x64" || runtime = "linux-x64" ->
        Path.TrimEndingDirectorySeparator(Path.GetFullPath repository), runtime,
        Path.TrimEndingDirectorySeparator(Path.GetFullPath output), mode.ToLowerInvariant()
    | _ -> failwith "Usage: Clean-Publish.fsx <repository> <win-x64|linux-x64> <output> <auto|true|false>"

let comparison = if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
let same (a: string) b = a.Equals(b, comparison)
let within (parent: string) (path: string) =
    let prefix = if Path.EndsInDirectorySeparator parent then parent else parent + string Path.DirectorySeparatorChar
    same parent path || path.StartsWith(prefix, comparison)
let artifacts = Path.Combine(repository, "artifacts")
let standardOutput = Path.Combine(artifacts, "publish", runtime)
let clean =
    match mode with
    | "auto" -> same output standardOutput
    | "true" -> true
    | "false" -> false
    | _ -> failwith "CleanPublishDirectory must be true or false (or omitted for automatic cleanup of the standard output)."

let rec rejectLinkedAncestors (path: string) =
    let attributes =
        try Some (File.GetAttributes path) with
        | :? FileNotFoundException | :? DirectoryNotFoundException -> None
    match attributes with
    | Some value when value.HasFlag FileAttributes.ReparsePoint ->
        failwithf "Refusing to clean a publish path through a symbolic link or junction: %s" path
    | _ -> ()
    match Directory.GetParent path with
    | null -> ()
    | parent -> rejectLinkedAncestors parent.FullName

if clean then
    // Disallow filesystem roots, repository ancestors, source directories and the
    // artifacts root. Within the repo, only a subdirectory of artifacts is eligible.
    if same output (Path.GetPathRoot output) || within output repository
       || same output artifacts || (within repository output && not (within artifacts output)) then
        failwithf "Refusing to clean a root, source or shared repository directory: %s" output
    rejectLinkedAncestors output
    if File.Exists output then failwithf "Publish output is a file, not a directory: %s" output
    if Directory.Exists output then
        printfn "Cleaning publish output: %s" output
        Directory.Delete(output, true)
