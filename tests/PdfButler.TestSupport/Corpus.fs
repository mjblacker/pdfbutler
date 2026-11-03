/// The test corpus: manifest model, file locations, oracle results.
/// See corpus/README.md for the policy behind these fields.
module PdfButler.TestSupport.Corpus

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

type Source =
    { Name: string
      Kind: string // "github" | "gitiles" | "url"
      Repo: string
      Commit: string
      License: string
      Redistributable: string }

type Discrepancy = { Check: string; Reason: string }

type Expect =
    { /// "ok" | "password" (fails without password, opens with it) | "error" | "any" (must not crash/hang)
      Open: string
      PageCount: int option
      TextContains: (int * string) list
      SearchMinHits: (string * int) list
      OutlineMin: int option
      LinksMin: (int * int) list
      Forms: string option }

type Doc =
    { Id: string
      Source: string
      Path: string
      Url: string option
      Sha256: string option
      Tags: string list
      Tier: string
      Pages: int list
      Password: string option
      Expect: Expect
      Discrepancies: Discrepancy list
      VisualMaxDiff: float option
      TextMinSimilarity: float option
      Notes: string }

type Manifest =
    { Sources: Map<string, Source>
      Docs: Doc list }

// ---- locations -------------------------------------------------------------------------

let repoRoot =
    lazy
        (let rec up (d: DirectoryInfo) =
            if isNull d then None
            elif File.Exists(Path.Combine(d.FullName, "corpus", "manifest.json")) then Some d.FullName
            else up d.Parent
         [ AppContext.BaseDirectory; Environment.CurrentDirectory ]
         |> List.tryPick (fun p -> up (DirectoryInfo p))
         |> Option.defaultWith (fun () ->
             failwith "Could not find the repository root (corpus/manifest.json) above the binaries or the current directory."))

let corpusDir () = Path.Combine(repoRoot.Value, "corpus")
let manifestPath () = Path.Combine(corpusDir (), "manifest.json")

let safeId (id: string) =
    let sb = StringBuilder()
    for c in id do
        if Char.IsLetterOrDigit c || c = '-' || c = '.' then sb.Append c |> ignore
        elif c = '/' then sb.Append "__" |> ignore
        else sb.Append '_' |> ignore
    sb.ToString()

let cachePath (d: Doc) =
    Path.Combine(corpusDir (), "cache", d.Source, d.Path.Replace('/', Path.DirectorySeparatorChar))

let goldenPath (d: Doc) (page: int) = Path.Combine(corpusDir (), "goldens", safeId d.Id, $"p{page}.png")
let oraclePath (d: Doc) = Path.Combine(corpusDir (), "oracle", safeId d.Id + ".json")
let artifactsDir () = Path.Combine(repoRoot.Value, "artifacts", "corpus")

let downloadUrl (m: Manifest) (d: Doc) =
    match d.Url with
    | Some u -> u
    | None ->
        let s = m.Sources[d.Source]
        let encPath = String.Join("/", d.Path.Split('/') |> Array.map Uri.EscapeDataString)
        match s.Kind with
        | "github" -> $"https://raw.githubusercontent.com/{s.Repo}/{s.Commit}/{encPath}"
        | "gitiles" -> $"{s.Repo}/+/{s.Commit}/{encPath}?format=TEXT"
        | k -> failwith $"source {s.Name}: no URL for kind {k}"

// ---- manifest parsing ------------------------------------------------------------------

let private str (n: JsonNode) (key: string) =
    match n[key] with
    | null -> None
    | v when v.GetValueKind() = JsonValueKind.Null -> None
    | v -> Some(v.GetValue<string>())

let private int' (n: JsonNode) (key: string) =
    match n[key] with
    | null -> None
    | v when v.GetValueKind() = JsonValueKind.Null -> None
    | v -> Some(v.GetValue<int>())

let private float' (n: JsonNode) (key: string) =
    match n[key] with
    | null -> None
    | v when v.GetValueKind() = JsonValueKind.Null -> None
    | v -> Some(v.GetValue<float>())

let private arr (n: JsonNode) (key: string) : JsonNode list =
    match n[key] with
    | :? JsonArray as a -> [ for x in a -> x ]
    | _ -> []

let parse (json: string) : Manifest =
    let root = JsonNode.Parse(json, documentOptions = JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip))
    let sources =
        match root["sources"] with
        | :? JsonObject as o ->
            [ for kv in o ->
                  let v = kv.Value
                  kv.Key,
                  { Name = kv.Key
                    Kind = defaultArg (str v "kind") "url"
                    Repo = defaultArg (str v "repo") ""
                    Commit = defaultArg (str v "commit") ""
                    License = defaultArg (str v "license") ""
                    Redistributable = defaultArg (str v "redistributable") "unknown" } ]
            |> Map.ofList
        | _ -> Map.empty
    let docs =
        [ for d in arr root "documents" ->
              let e = match d["expect"] with null -> JsonObject() :> JsonNode | x -> x
              { Id = (str d "id").Value
                Source = defaultArg (str d "source") "url"
                Path = defaultArg (str d "path") ""
                Url = str d "url"
                Sha256 = str d "sha256"
                Tags = arr d "tags" |> List.map (fun t -> t.GetValue<string>())
                Tier = defaultArg (str d "tier") "core"
                Pages = (match arr d "pages" with [] -> [ 0 ] | ps -> ps |> List.map (fun p -> p.GetValue<int>()))
                Password = str d "password"
                Expect =
                  { Open = defaultArg (str e "open") "ok"
                    PageCount = int' e "pageCount"
                    TextContains = arr e "text" |> List.map (fun t -> defaultArg (int' t "page") 0, (str t "contains").Value)
                    SearchMinHits = arr e "search" |> List.map (fun t -> (str t "query").Value, defaultArg (int' t "minHits") 1)
                    OutlineMin = int' e "outlineMin"
                    LinksMin = arr e "links" |> List.map (fun t -> defaultArg (int' t "page") 0, defaultArg (int' t "min") 1)
                    Forms = str e "forms" }
                Discrepancies =
                  arr d "discrepancies"
                  |> List.map (fun x -> { Check = (str x "check").Value; Reason = defaultArg (str x "reason") "" })
                VisualMaxDiff = float' d "visualMaxDiff"
                TextMinSimilarity = float' d "textMinSimilarity"
                Notes = defaultArg (str d "notes") "" } ]
    { Sources = sources; Docs = docs }

let load () = parse (File.ReadAllText(manifestPath ()))

let manifest = lazy (load ())

let find (id: string) = manifest.Value.Docs |> List.find (fun d -> d.Id = id)

let isKnownDiscrepancy (d: Doc) (check: string) =
    d.Discrepancies |> List.exists (fun x -> x.Check = check || (x.Check.EndsWith "*" && check.StartsWith(x.Check.TrimEnd '*')))

let sha256File (path: string) =
    use s = File.OpenRead path
    Convert.ToHexString(Security.Cryptography.SHA256.HashData s).ToLowerInvariant()

// ---- oracle (independent engine: pdf.js) ------------------------------------------------

type OraclePage =
    { Index: int
      Width: float
      Height: float
      Text: string
      Links: int
      Widgets: int }

type Oracle =
    { Engine: string
      Ok: bool
      Error: string option
      NumPages: int
      Pages: OraclePage list
      OutlineTotal: int option
      Labels: string list option
      HasForms: bool }

let loadOracle (d: Doc) : Oracle option =
    let p = oraclePath d
    if not (File.Exists p) then None
    else
        let n = JsonNode.Parse(File.ReadAllText p)
        Some
            { Engine = defaultArg (str n "engine") "?"
              Ok = (match n["ok"] with null -> false | v -> v.GetValue<bool>())
              Error = str n "error"
              NumPages = defaultArg (int' n "numPages") 0
              Pages =
                arr n "pages"
                |> List.map (fun pg ->
                    { Index = defaultArg (int' pg "index") 0
                      Width = defaultArg (float' pg "width") 0.0
                      Height = defaultArg (float' pg "height") 0.0
                      Text = defaultArg (str pg "text") ""
                      Links = defaultArg (int' pg "links") 0
                      Widgets = defaultArg (int' pg "widgets") 0 })
              OutlineTotal = (match n["outline"] with null -> None | o when o.GetValueKind() = JsonValueKind.Null -> None | o -> int' o "total")
              Labels =
                (match n["labels"] with
                 | :? JsonArray as a -> Some [ for x in a -> (if isNull x then "" else x.GetValue<string>()) ]
                 | _ -> None)
              HasForms = (match n["hasForms"] with null -> false | v -> v.GetValue<bool>()) }
