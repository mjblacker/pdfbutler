// Cross-platform smoke tests for deployment guards and maintenance scripts.
// dotnet fsi tools/Test-BuildTools.fsx
open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Text.Json

let repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let root = Path.Combine(repo, "artifacts/build-tools-tests", Guid.NewGuid().ToString("N"))
Directory.CreateDirectory root |> ignore
let check condition message = if not condition then failwith message
let run (arguments: string list) expectedExitText =
    let start = ProcessStartInfo("dotnet", WorkingDirectory = repo, UseShellExecute = false,
                                 RedirectStandardOutput = true, RedirectStandardError = true)
    for argument in arguments do start.ArgumentList.Add argument
    use child = Process.Start start
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    if not (child.WaitForExit 120000) then
        child.Kill(true)
        failwithf "Timed out: dotnet %A" arguments
    let output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()
    match expectedExitText with
    | None -> check (child.ExitCode = 0) (sprintf "dotnet %A failed:\n%s" arguments output)
    | Some (text: string) ->
        check (child.ExitCode <> 0 && output.Contains text) (sprintf "Expected failure containing %s:\n%s" text output)

let deployment target profile directory extra expected =
    run (["msbuild"; "src/PdfButler.App"; "-v:quiet"; "-t:" + target
          "-p:PublishProfile=" + profile; "-p:PublishDir=" + directory + string Path.DirectorySeparatorChar] @ extra) expected
for profile, native in ["win-x64", "pdfium.dll"; "linux-x64", "libpdfium.so"] do
    let destination = Path.Combine(root, profile + " with spaces")
    let validate extra expected = deployment "ValidateDeploymentDestination" profile destination extra expected
    validate [] None // nonexistent
    Directory.CreateDirectory destination |> ignore
    validate [] None // existing, empty
    let hidden = Path.Combine(destination, ".hidden")
    File.WriteAllText(hidden, "keep me")
    if OperatingSystem.IsWindows() then File.SetAttributes(hidden, FileAttributes.Hidden)
    validate [] None // existing, hidden file
    check (File.ReadAllText hidden = "keep me") "Destination validation changed a user file."
    let existing = Path.Combine(destination, "unrelated.txt")
    File.WriteAllText(existing, "keep this too")
    let nested = Path.Combine(destination, "empty child")
    Directory.CreateDirectory nested |> ignore
    validate [] None // existing, regular file and subdirectory
    check (File.ReadAllText existing = "keep this too" && File.Exists hidden && Directory.Exists nested)
        "Destination validation removed existing content."
    validate ["-p:SelfContained=false"] None
    validate ["-p:SelfContained=true"] None
    validate ["-p:PublishSingleFile=true"] None
    validate ["-p:PublishSingleFile=false"] None
    validate ["-p:PublishTrimmed=true"] (Some "must be untrimmed")
    validate ["-p:IncludeNativeLibrariesForSelfExtract=true"] (Some "Native libraries must remain")
    validate ["-p:IncludeAllContentForSelfExtract=true"] (Some "Native libraries must remain")
    validate ["-p:RuntimeIdentifier=osx-x64"] (Some "supports only")
    deployment "VerifyDeploymentFiles" profile destination [] (Some ("Missing " + native))
    File.WriteAllBytes(Path.Combine(destination, native), [||])
    deployment "VerifyDeploymentFiles" profile destination [] None
    if not (OperatingSystem.IsWindows()) then
        File.SetUnixFileMode(existing, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    deployment "ArchiveDeployment" profile destination [] None
    let archive = destination + ".zip"
    let inspectArchive () =
        use zip = ZipFile.OpenRead archive
        check (zip.GetEntry(native) <> null && zip.GetEntry("unrelated.txt") <> null
               && zip.GetEntry(".hidden") <> null) "Publish ZIP omitted files or has an extra root folder."
        check (zip.Entries |> Seq.forall (fun entry -> not (entry.FullName.EndsWith(".zip")))) "Publish ZIP included itself."
        if not (OperatingSystem.IsWindows()) then
            check (((zip.GetEntry("unrelated.txt").ExternalAttributes >>> 16) &&& 0o100) <> 0)
                "Publish ZIP lost executable permissions."
        use reader = new StreamReader(zip.GetEntry("unrelated.txt").Open())
        reader.ReadToEnd()
    check (inspectArchive () = "keep this too") "Publish ZIP changed file contents."
    File.WriteAllText(existing, "updated")
    File.WriteAllText(Path.Combine(destination, "new.txt"), "new")
    deployment "ArchiveDeployment" profile destination [] None
    check (inspectArchive () = "updated") "Repeat publishing did not replace the ZIP."
    File.Delete(Path.Combine(destination, "new.txt"))
    deployment "ArchiveDeployment" profile destination [] None
    let checkNoStaleEntry () =
        use zip = ZipFile.OpenRead archive
        check (zip.GetEntry("new.txt") = null) "ZIP retained an obsolete file."
    checkNoStaleEntry ()
    let originalArchive = File.ReadAllBytes archive
    File.Delete(Path.Combine(destination, native))
    deployment "ArchiveDeployment" profile destination [] (Some ("Missing " + native))
    check (File.ReadAllBytes archive = originalArchive) "Invalid publish replaced the last good ZIP."
    File.WriteAllBytes(Path.Combine(destination, native), [||])
    let disabled = Path.Combine(destination, "no-archive")
    Directory.CreateDirectory disabled |> ignore
    deployment "ArchiveDeployment" profile disabled ["-p:CreatePublishArchive=false"] None
    check (not (File.Exists(disabled + ".zip"))) "Archive opt-out ignored."
printfn "PASS: deployment guards for both profiles (new/empty/hidden/subdirectory/invalid options/native files)."
printfn "PASS: deployment ZIPs (contents, repeat/obsolete files, validation before replacement, opt-out)."

// Exercise cleanup against an isolated repository, never the real default output.
let cleanupRepository = Path.Combine(root, "cleanup repository")
let cleanup mode profile output expected =
    run ["fsi"; "tools/Clean-Publish.fsx"; cleanupRepository; profile; output; mode] expected
let seed directory =
    let nested = Path.Combine(directory, "nested")
    Directory.CreateDirectory nested |> ignore
    File.WriteAllText(Path.Combine(directory, "stale.dll"), "stale")
    File.WriteAllText(Path.Combine(nested, ".hidden"), "stale")
    if OperatingSystem.IsWindows() then File.SetAttributes(Path.Combine(nested, ".hidden"), FileAttributes.Hidden)
for profile in ["win-x64"; "linux-x64"] do
    let standard = Path.Combine(cleanupRepository, "artifacts/publish", profile)
    seed standard
    cleanup "false" profile standard None
    check (File.Exists(Path.Combine(standard, "stale.dll"))) "Explicit cleanup opt-out ignored."
    cleanup "auto" profile (Path.Combine(standard, "..", profile, ".")) None
    check (not (Directory.Exists standard)) "Standard output was not cleaned."
    cleanup "auto" profile standard None // nonexistent output is fine
    let custom = Path.Combine(root, "custom publish " + profile)
    seed custom
    cleanup "auto" profile custom None
    check (File.Exists(Path.Combine(custom, "stale.dll"))) "Custom output cleaned without opt-in."
    cleanup "true" profile custom None
    check (not (Directory.Exists custom)) "Custom output was not cleaned after opt-in."
    // Verify MSBuild wiring as well as the standalone policy.
    seed custom
    deployment "CleanDeploymentDirectory" profile custom [] None
    check (File.Exists(Path.Combine(custom, "stale.dll"))) "MSBuild cleaned custom output without opt-in."
    deployment "CleanDeploymentDirectory" profile custom ["-p:CleanPublishDirectory=true"; "-p:PublishTrimmed=true"] (Some "must be untrimmed")
    check (File.Exists(Path.Combine(custom, "stale.dll"))) "Invalid deployment options cleaned output."
    deployment "CleanDeploymentDirectory" profile custom ["-p:CleanPublishDirectory=true"] None
    check (not (Directory.Exists custom)) "MSBuild cleanup opt-in did not clean custom output."
let source = Path.Combine(cleanupRepository, "src")
Directory.CreateDirectory source |> ignore
let sourceFile = Path.Combine(source, "keep.fs")
File.WriteAllText(sourceFile, "keep")
for unsafePath in [Path.GetPathRoot(root); root; cleanupRepository; source; Path.Combine(cleanupRepository, "artifacts")] do
    cleanup "true" "win-x64" unsafePath (Some "Refusing to clean")
check (File.ReadAllText sourceFile = "keep") "Unsafe cleanup changed source files."
cleanup "invalid" "win-x64" source (Some "CleanPublishDirectory must be true or false")
if not (OperatingSystem.IsWindows()) then
    // Windows symlink creation needs privileges; Linux CI covers both link cases.
    let target = Path.Combine(root, "linked source")
    seed target
    let link = Path.Combine(root, "linked output")
    Directory.CreateSymbolicLink(link, target) |> ignore
    cleanup "true" "linux-x64" link (Some "symbolic link or junction")
    cleanup "true" "linux-x64" (Path.Combine(link, "nested")) (Some "symbolic link or junction")
    check (File.Exists(Path.Combine(target, "stale.dll"))) "Linked cleanup changed its target."
    let containingLink = Path.Combine(root, "output containing link")
    Directory.CreateDirectory containingLink |> ignore
    Directory.CreateSymbolicLink(Path.Combine(containingLink, "external"), target) |> ignore
    cleanup "true" "linux-x64" containingLink None
    check (not (Directory.Exists containingLink) && File.Exists(Path.Combine(target, "stale.dll")))
        "Cleanup followed a child link into its target."
printfn "PASS: publish cleanup (automatic defaults, opt-out, custom opt-in, MSBuild wiring, source/root protection)."

let packageRoot = Path.Combine(root, "package cache")
let package = Path.Combine(packageRoot, "example/1.0.0")
Directory.CreateDirectory package |> ignore
let licenseBytes = [| 0xEFuy; 0xBBuy; 0xBFuy; 65uy; 13uy; 10uy |]
File.WriteAllBytes(Path.Combine(package, "license.txt"), licenseBytes)
File.WriteAllText(Path.Combine(package, "THIRD-PARTY-NOTICES"), "Notices\n")
File.WriteAllText(Path.Combine(package, "not-a-license.txt"), "Do not copy")
let assetsPath = Path.Combine(root, "project.assets.json")
let writeAssets folders =
    File.WriteAllText(assetsPath, JsonSerializer.Serialize(
        {| packageFolders = folders
           libraries = dict ["Example/1.0.0", {| ``type`` = "package"; path = "example/1.0.0" |}
                             "Local/1.0.0", {| ``type`` = "project"; path = "unused" |}] |}))
writeAssets (dict [Path.Combine(root, "absent cache"), box {| |}; packageRoot, box {| |}])
let licenses = Path.Combine(root, "notices")
let collect = ["fsi"; "tools/Collect-Licenses.fsx"; "--assets"; assetsPath; "--output"; licenses]
run collect None
let noticeDirectory = Path.Combine(licenses, "Example/1.0.0")
check (File.ReadAllBytes(Path.Combine(noticeDirectory, "license.txt")) = licenseBytes) "License bytes changed."
check (Directory.GetFiles(noticeDirectory).Length = 2) "Wrong notice files copied."
File.WriteAllText(Path.Combine(noticeDirectory, "previous-version-notice"), "Keep earlier files")
run collect None
check (File.Exists(Path.Combine(noticeDirectory, "previous-version-notice"))) "Removed an earlier notice."
writeAssets (dict [(Path.Combine(root, "absent cache"), box {| |})])
run collect (Some "Missing restored package")
printfn "PASS: license collection (cache fallback, case-insensitive names, verbatim bytes, no cleanup, missing package)."

let icons = Path.Combine(root, "icons")
run ["fsi"; "tools/Build-Icon.fsx"; "--output"; icons] None
let pngSignature = [| 137uy; 80uy; 78uy; 71uy; 13uy; 10uy; 26uy; 10uy |]
let png = File.ReadAllBytes(Path.Combine(icons, "pdfbutler.png"))
check (png[..7] = pngSignature) "Invalid PNG."
let ico = File.ReadAllBytes(Path.Combine(icons, "pdfbutler.ico"))
check (BitConverter.ToUInt16(ico, 2) = 1us && BitConverter.ToUInt16(ico, 4) = 8us) "Invalid ICO header."
for i, size in [|16; 20; 24; 32; 48; 64; 128; 256|] |> Array.indexed do
    let entry = 6 + i * 16
    check (int ico[entry] = size % 256 && ico[entry] = ico[entry + 1]) "Invalid ICO dimensions."
    let offset = int (BitConverter.ToUInt32(ico, entry + 12))
    let length = int (BitConverter.ToUInt32(ico, entry + 8))
    check (offset + length <= ico.Length && ico[offset .. offset + 7] = pngSignature) "Invalid embedded ICO PNG."
    if size = 256 then check (ico[offset .. offset + length - 1] = png) "ICO/PNG mismatch."
printfn "PASS: icon generation (PNG, eight ICO sizes, embedded PNG payloads)."

// A deterministic apphost exercises process arguments/JSON/percentiles without a desktop or PDFium.
let fixture = Path.Combine(root, "measurement fixture")
Directory.CreateDirectory fixture |> ignore
File.WriteAllText(Path.Combine(fixture, "Fixture.fsproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>
  <ItemGroup><Compile Include="Program.fs" /></ItemGroup>
</Project>
""")
File.WriteAllText(Path.Combine(fixture, "Program.fs"), """
open System
open System.IO
[<EntryPoint>]
let main args =
    if args.Length <> 2 || args[0] <> "--measure" || not (File.Exists args[1]) then 2
    else
        let log = args[1] + ".calls"
        File.AppendAllText(log, ".")
        let n = File.ReadAllText(log).Length
        printfn "{\"first-page-presented\":%d,\"open-start\":1,\"workingSetMB\":10,\"rendererWorkingSetMB\":20}" n
        0
""")
run ["build"; Path.Combine(fixture, "Fixture.fsproj"); "--configuration"; "Release"; "--verbosity"; "quiet"] None
let executable = Path.Combine(fixture, "bin/Release/net10.0", if OperatingSystem.IsWindows() then "Fixture.exe" else "Fixture")
let document = Path.Combine(root, "document with spaces.pdf")
File.WriteAllText(document, "Fixture checks arguments, not PDF content.")
let measurements = Path.Combine(root, "measurements/startup.json")
let measure = ["fsi"; "tools/Measure-Startup.fsx"; "--executable"; executable; "--output"; measurements; document]
run (measure @ ["--repetitions"; "19"]) (Some "Usage:")
run measure None
let verifyMeasurements () =
    use json = JsonDocument.Parse(File.ReadAllText measurements)
    let samples = json.RootElement.GetProperty("samples")
    check (samples.GetArrayLength() = 20 && File.ReadAllText(document + ".calls").Length = 21) "Warm-up or sample count is wrong."
    check (samples[0].GetProperty("first-page-presented").GetInt32() = 2) "Warm-up was not discarded."
    check (samples[0].GetProperty("rendererWorkingSetMB").GetInt32() = 20) "Renderer measurement field lost."
    let row = json.RootElement.GetProperty("summary")[0]
    check (row.GetProperty("launchP50Ms").GetDouble() = 11.0 && row.GetProperty("launchP95Ms").GetDouble() = 20.0) "Wrong nearest-rank percentiles."
    check (row.GetProperty("openP50Ms").GetDouble() = 10.0 && row.GetProperty("openP95Ms").GetDouble() = 19.0) "Wrong open timings."
verifyMeasurements ()
printfn "PASS: startup measurements (quoted paths, fresh processes, warm-up, nearest-rank percentiles, renderer samples)."
printfn "Build-tool checks passed. Fixtures: %s" root
