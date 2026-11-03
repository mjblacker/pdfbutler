/// Corpus maintenance tool.
///
///   fetch [--extended] [--pin] [--only <id>]   download missing files from pinned upstream commits,
///                                              verify SHA-256 (--pin records hashes for new entries)
///   oracle [--only <id>]                       run the independent pdf.js oracle over cached files
///   list                                       show entries, tags and cache state
module PdfButler.Corpus.Program

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text.RegularExpressions
open PdfButler.TestSupport

let private http =
    let h = new HttpClient(Timeout = TimeSpan.FromMinutes 3.0)
    h.DefaultRequestHeaders.UserAgent.ParseAdd "PdfButler-corpus/1.0 (+test corpus fetcher)"
    h

let private download (url: string) (isBase64: bool) : byte[] =
    let rec attempt n =
        try
            let bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult()
            if isBase64 then Convert.FromBase64String(Text.Encoding.ASCII.GetString(bytes).Trim()) else bytes
        with e when n > 0 ->
            eprintfn "  retrying (%s)" e.Message
            Threading.Thread.Sleep 1500
            attempt (n - 1)
    attempt 2

/// Insert `"sha256": "..."` right after the entry's id in the manifest text (keeps comments/formatting).
let private pinHash (manifestText: string) (id: string) (hash: string) =
    let pattern = "(\"id\":\\s*\"" + Regex.Escape id + "\",)"
    let rx = Regex(pattern)
    if not (rx.IsMatch manifestText) then failwith $"could not find id {id} in manifest"
    rx.Replace(manifestText, (fun (m: Match) -> m.Value + $" \"sha256\": \"{hash}\","), 1)

let fetch (args: string list) =
    let extended = List.contains "--extended" args
    let pin = List.contains "--pin" args
    let only = args |> List.pairwise |> List.tryPick (fun (a, b) -> if a = "--only" then Some b else None)
    let m = Corpus.load ()
    let mutable text = File.ReadAllText(Corpus.manifestPath ())
    let mutable failures = 0
    let mutable fetched = 0
    let mutable pinned = 0
    let docs =
        m.Docs
        |> List.filter (fun d -> extended || d.Tier = "core")
        |> List.filter (fun d -> only.IsNone || only = Some d.Id)
    for d in docs do
        let path = Corpus.cachePath d
        let valid () =
            File.Exists path && (match d.Sha256 with Some h -> Corpus.sha256File path = h | None -> true)
        if valid () && File.Exists path && d.Sha256.IsSome then
            ()
        else
            try
                if not (File.Exists path && d.Sha256.IsNone) then
                    let url = Corpus.downloadUrl m d
                    let isB64 = (match m.Sources.TryFind d.Source with Some s -> s.Kind = "gitiles" | None -> false)
                    printfn "fetch %s" d.Id
                    let bytes = download url isB64
                    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                    let tmp = path + ".part"
                    File.WriteAllBytes(tmp, bytes)
                    File.Move(tmp, path, true)
                    fetched <- fetched + 1
                let hash = Corpus.sha256File path
                match d.Sha256 with
                | Some h when h <> hash ->
                    eprintfn "  SHA-256 MISMATCH for %s: manifest %s, downloaded %s" d.Id h hash
                    File.Delete path
                    failures <- failures + 1
                | Some _ -> ()
                | None when pin ->
                    text <- pinHash text d.Id hash
                    pinned <- pinned + 1
                    printfn "  pinned %s" hash
                | None ->
                    eprintfn "  %s has no sha256 in the manifest; re-run with --pin after reviewing the source" d.Id
                    failures <- failures + 1
            with e ->
                eprintfn "  FAILED %s: %s" d.Id e.Message
                failures <- failures + 1
    if pinned > 0 then File.WriteAllText(Corpus.manifestPath (), text)
    printfn "fetched %d, pinned %d, failures %d, documents %d" fetched pinned failures docs.Length
    if failures > 0 then 1 else 0

let oracle (args: string list) =
    let root = Corpus.repoRoot.Value
    let script = Path.Combine(root, "tools", "oracle", "oracle.mjs")
    let nodeModules = Path.Combine(root, "tools", "oracle", "node_modules")
    if not (Directory.Exists nodeModules) then
        eprintfn "pdf.js oracle is not installed. Run: npm ci --prefix tools/oracle"
        2
    else
        let psi = ProcessStartInfo("node", UseShellExecute = false)
        psi.ArgumentList.Add script
        psi.ArgumentList.Add(Corpus.manifestPath ())
        psi.ArgumentList.Add(Path.Combine(Corpus.corpusDir (), "cache"))
        psi.ArgumentList.Add(Path.Combine(Corpus.corpusDir (), "oracle"))
        for a in args do psi.ArgumentList.Add a
        use p = Process.Start psi
        p.WaitForExit()
        p.ExitCode

let list () =
    let m = Corpus.load ()
    for d in m.Docs do
        let state = if File.Exists(Corpus.cachePath d) then "cached" else "missing"
        let oracle = if File.Exists(Corpus.oraclePath d) then "oracle" else "-"
        printfn "%-45s %-8s %-7s %-6s %s" d.Id d.Tier state oracle (String.Join(",", d.Tags))
    let bySource = m.Docs |> List.countBy (fun d -> d.Source)
    printfn "\n%d documents: %s" m.Docs.Length (String.Join(", ", bySource |> List.map (fun (s, n) -> $"{s}={n}")))
    0

[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | "fetch" :: rest -> fetch rest
    | "oracle" :: rest -> oracle rest
    | "list" :: _ -> list ()
    | _ ->
        eprintfn "usage: PdfButler.Corpus (fetch [--extended] [--pin] [--only id] | oracle [--only id] | list)"
        2
