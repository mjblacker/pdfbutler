// Run after restoring the app. Copy upstream notices verbatim; keep earlier versions.
// dotnet fsi tools/Collect-Licenses.fsx [--assets <project.assets.json>] [--output <directory>]
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

let repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let rec options assets output = function
    | [] -> assets, output
    | "--assets" :: path :: rest -> options (Path.GetFullPath path) output rest
    | "--output" :: path :: rest -> options assets (Path.GetFullPath path) rest
    | _ -> failwith "Usage: dotnet fsi tools/Collect-Licenses.fsx [--assets <project.assets.json>] [--output <directory>]"

let assetsPath, output =
    options (Path.Combine(repo, "src/PdfButler.App/obj/project.assets.json"))
        (Path.Combine(repo, "licenses/packages")) (fsi.CommandLineArgs |> Array.skip 1 |> Array.toList)

let noticeName = Regex(@"^(LICENSE|THIRD.PARTY.NOTICES|COPYING)(\..*)?$", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)
let collect () =
    use assets = JsonDocument.Parse(File.ReadAllText assetsPath)
    let folders = assets.RootElement.GetProperty("packageFolders").EnumerateObject() |> Seq.map _.Name |> Seq.toArray
    let mutable copied = 0
    for library in assets.RootElement.GetProperty("libraries").EnumerateObject() do
        if library.Value.GetProperty("type").GetString() = "package" then
            let relative = library.Value.GetProperty("path").GetString()
            let package =
                folders
                |> Array.map (fun folder -> Path.Combine(folder, relative))
                |> Array.tryFind Directory.Exists
                |> Option.defaultWith (fun () -> failwithf "Missing restored package: %s" library.Name)
            for file in Directory.EnumerateFiles package do
                if noticeName.IsMatch(Path.GetFileName file) then
                    let destination = Path.Combine(output, library.Name)
                    Directory.CreateDirectory destination |> ignore
                    File.Copy(file, Path.Combine(destination, Path.GetFileName file), true)
                    copied <- copied + 1
    printfn "Copied %d notice files to %s" copied output
collect ()
