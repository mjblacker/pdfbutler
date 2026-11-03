/// Contract suite over the public corpus (corpus/manifest.json).
///
/// For each document, checks are collected and reported together:
///   * open behaviour (ok / password / error / any) and typed errors;
///   * hand-written expectations from the manifest (page count, text, search, outline, forms);
///   * agreement with an independent engine (pdf.js oracle): page count, page sizes, text, links, outline;
///   * every page renders (bounded) without engine failure;
///   * visual regression against reviewed golden images.
///
/// Known, investigated differences are listed per document under "discrepancies" in the manifest.
/// Environment:
///   PDFBUTLER_BLESS=missing|all       write golden images (review them before committing!)
///   PDFBUTLER_ALLOW_MISSING_CORPUS=1  skip instead of fail when corpus/cache has not been fetched
module PdfButler.Engine.Tests.CorpusTests

open System
open System.IO
open System.Threading
open Xunit
open PdfButler.Core
open PdfButler.Pdfium
open PdfButler.TestSupport
open PdfButler.Engine.Tests.Helpers

type Data =
    static member CoreIds =
        let d = TheoryData<string>()
        for doc in Corpus.manifest.Value.Docs do
            if doc.Tier = "core" then d.Add doc.Id
        d

    static member ExtendedIds =
        let d = TheoryData<string>()
        for doc in Corpus.manifest.Value.Docs do
            if doc.Tier <> "core" then d.Add doc.Id
        d

let private bless = Environment.GetEnvironmentVariable "PDFBUTLER_BLESS"
let private allowMissing = Environment.GetEnvironmentVariable "PDFBUTLER_ALLOW_MISSING_CORPUS" = "1"

/// Golden render scale: 1 px per point, capped so the long side is at most 1000 px.
let goldenScale (s: Size) = min 1.0 (1000.0 / max s.W s.H)

let private normalizeWs (s: string) =
    String.Join(" ", s.Split([| ' '; '\n'; '\r'; '\t' |], StringSplitOptions.RemoveEmptyEntries))

let private formsName =
    function
    | NoForms -> "none"
    | AcroForm -> "acroform"
    // Static XFA with an AcroForm fallback: displays like an ordinary form.
    | XfaForeground -> "xfa-foreground"
    // Dynamic XFA: content only exists as XFA, which this viewer does not support.
    | XfaFull -> "xfa-full"

type private Report(doc: Corpus.Doc) =
    let failures = ResizeArray<string>()
    let notes = ResizeArray<string>()
    member _.Check(name: string, ok: bool, detail: string) =
        if not ok then
            if Corpus.isKnownDiscrepancy doc name then notes.Add $"known [{name}] {detail}"
            else failures.Add $"[{name}] {detail}"
    member _.Fail(name: string, detail: string) = failures.Add $"[{name}] {detail}"
    member _.Note(s: string) = notes.Add s
    member _.Finish() =
        let dir = Path.Combine(Corpus.artifactsDir (), Corpus.safeId doc.Id)
        if failures.Count > 0 || notes.Count > 0 then
            Directory.CreateDirectory dir |> ignore
            File.WriteAllLines(Path.Combine(dir, "report.txt"), Seq.append failures notes)
        if failures.Count > 0 then
            let msg = String.Join("\n  ", failures)
            Assert.Fail($"{doc.Id}: {failures.Count} check(s) failed\n  {msg}\n(artifacts: {dir})")

let private visual (r: Report) (doc: Corpus.Doc) (d: PdfDocument) (page: int) =
    let size = d.Info.Pages[page].Size
    let img = toRgb (render d page R0 (goldenScale size) None)
    // Guard against blessing a broken render: a blank page must not have text according to pdf.js.
    match Corpus.loadOracle doc with
    | Some o when o.Ok ->
        match o.Pages |> List.tryFind (fun p -> p.Index = page) with
        | Some op when (TextCompare.canonical op.Text).Length >= 3 ->
            r.Check($"blank:{page}", ImageCompare.inkFraction img > 0.0, $"page {page} rendered blank but pdf.js finds text on it")
        | _ -> ()
    | _ -> ()
    let golden = Corpus.goldenPath doc page
    let artifacts = Path.Combine(Corpus.artifactsDir (), Corpus.safeId doc.Id)
    if bless = "all" || (bless = "missing" && not (File.Exists golden)) then
        Png.save golden img
        r.Note $"blessed {golden}"
    elif not (File.Exists golden) then
        Png.save (Path.Combine(artifacts, $"p{page}-actual.png")) img
        r.Fail($"visual:{page}", $"no golden image; review artifacts and run with PDFBUTLER_BLESS=missing")
    else
        let tol = { ImageCompare.defaultTolerance with MaxDiffFraction = defaultArg doc.VisualMaxDiff ImageCompare.defaultTolerance.MaxDiffFraction }
        let result, diff = ImageCompare.compare (Png.load golden) img tol
        let ok = ImageCompare.passes tol result
        if not ok then
            Png.save (Path.Combine(artifacts, $"p{page}-actual.png")) img
            diff |> Option.iter (Png.save (Path.Combine(artifacts, $"p{page}-diff.png")))
        r.Check($"visual:{page}", ok, $"%A{result}")

let private checkOpened (r: Report) (doc: Corpus.Doc) (d: PdfDocument) =
    let info = d.Info
    // ---- manifest expectations
    doc.Expect.PageCount |> Option.iter (fun n -> r.Check("pageCount", info.PageCount = n, $"expected {n} pages, engine {info.PageCount}"))
    doc.Expect.Forms |> Option.iter (fun f -> r.Check("forms", formsName info.Forms = f, $"expected forms {f}, engine {formsName info.Forms}"))
    for (page, needle) in doc.Expect.TextContains do
        let t = normalizeWs (TextLayer.fullText (text d page))
        r.Check($"text-contains:{page}", t.Contains(normalizeWs needle), $"page {page} text does not contain '{needle}'")
    if not doc.Expect.SearchMinHits.IsEmpty then
        let texts = [ for p in 0 .. min (info.PageCount - 1) 199 -> await' (d.GetTextAsync(p, Priority.Background, CancellationToken.None)) ]
        for (q, minHits) in doc.Expect.SearchMinHits do
            let n = texts |> List.sumBy (fun t -> (Search.findInPage Search.defaultOptions q t).Length)
            r.Check($"search:{q}", n >= minHits, $"search '{q}' found {n}, expected at least {minHits}")
    let outline = lazy (await' (d.GetOutlineAsync CancellationToken.None))
    let rec countOutline (items: OutlineItem list) = items |> List.sumBy (fun i -> 1 + countOutline i.Children)
    doc.Expect.OutlineMin |> Option.iter (fun n ->
        let got = countOutline outline.Value
        r.Check("outlineMin", got >= n, $"outline has {got} entries, expected at least {n}"))

    // ---- agreement with the independent oracle
    match Corpus.loadOracle doc with
    | None -> r.Fail("oracle", "no oracle result; run: dotnet run --project tools/PdfButler.Corpus -- oracle")
    | Some o when not o.Ok -> r.Check("oracle-open", false, $"pdf.js could not open this file ({o.Error}) but PDFium did")
    | Some o ->
        r.Check("pageCount", o.NumPages = info.PageCount, $"pdf.js {o.NumPages} pages, PDFium {info.PageCount}")
        let minSim = defaultArg doc.TextMinSimilarity 0.85
        for op in o.Pages do
          try
            if op.Index < info.PageCount then
                let s = info.Pages[op.Index].Size
                let close a b = abs (a - b) <= max 1.0 (0.005 * max a b)
                r.Check($"size:{op.Index}", close s.W op.Width && close s.H op.Height, $"page {op.Index}: pdf.js {op.Width}x{op.Height}, PDFium {s.W}x{s.H}")
                let ours = TextLayer.fullText (await' (d.GetTextAsync(op.Index, Priority.Background, CancellationToken.None)))
                let a = TextCompare.canonical ours
                let b = TextCompare.canonical op.Text
                if a.Length >= 20 || b.Length >= 20 then
                    let sim = TextCompare.similarity ours op.Text
                    r.Check($"text:{op.Index}", sim >= minSim, $"page {op.Index} text similarity {sim:F3} < {minSim} (PDFium {a.Length} chars, pdf.js {b.Length})")
                let links = await' (d.GetLinksAsync(op.Index, Priority.Background, CancellationToken.None))
                r.Check($"links:{op.Index}", links.Length = op.Links, $"page {op.Index}: pdf.js {op.Links} links, PDFium {links.Length}")
          with e ->
            r.Check($"page:{op.Index}", false, $"page {op.Index} could not be loaded by PDFium ({e.GetBaseException().Message}) but pdf.js read it")
        o.OutlineTotal |> Option.iter (fun n ->
            let got = countOutline outline.Value
            let same = (got = n)
            r.Check("outline", same, $"outline entries: pdf.js {n}, PDFium {got}"))
        match o.Labels with
        | Some labels when labels.Length = info.PageCount ->
            let ours = [ for p in info.Pages -> defaultArg p.Label "" ]
            let same = (labels = ours)
            r.Check("labels", same, $"page labels differ: pdf.js %A{labels |> List.truncate 8}, PDFium %A{ours |> List.truncate 8}")
        | _ -> ()

    // ---- every page renders (bounded) without engine failure
    for p in 0 .. min (info.PageCount - 1) 29 do
        try
            let img = render d p R0 (min 0.25 (goldenScale info.Pages[p].Size)) None
            if img.Width <= 0 then r.Fail($"render:{p}", "empty bitmap")
        with e -> r.Check($"render:{p}", false, $"page {p} failed to render: {e.GetBaseException().Message}")

    // ---- visual regression
    for p in doc.Pages do
        if p < info.PageCount then visual r doc d p
        else r.Fail($"visual:{p}", $"manifest lists page {p} but the document has {info.PageCount}")

let private runContract (id: string) =
    let doc = Corpus.find id
    let path = Corpus.cachePath doc
    if not (File.Exists path) then
        let msg = $"{id}: not in corpus/cache. Run: dotnet run --project tools/PdfButler.Corpus -- fetch"
        if allowMissing then Assert.Skip msg else Assert.Fail msg
    match doc.Sha256 with
    | Some h -> Assert.True((Corpus.sha256File path = h), $"{id}: cached file does not match the pinned SHA-256; re-fetch")
    | None -> Assert.Fail $"{id}: manifest has no sha256 pin"

    let r = Report doc
    let opened =
        match doc.Expect.Open with
        | "password" ->
            (match openDoc path None with
             | Error PasswordRequired -> ()
             | Ok d -> (d :> IDisposable).Dispose(); r.Fail("password", "opened without a password")
             | Error e -> r.Fail("password", $"without password expected PasswordRequired, got {e}"))
            (match openDoc path (Some "definitely-not-the-password") with
             | Error WrongPassword -> ()
             | Ok d -> (d :> IDisposable).Dispose(); r.Fail("password", "opened with a wrong password")
             | Error e -> r.Fail("password", $"wrong password expected WrongPassword, got {e}"))
            match openDoc path doc.Password with
            | Ok d -> Some d
            | Error e -> r.Fail("open", $"with the correct password: {e}"); None
        | "error" ->
            match openDoc path doc.Password with
            | Ok d -> (d :> IDisposable).Dispose(); r.Fail("open", "expected an error but the file opened"); None
            | Error _ -> None
        | "any" ->
            match openDoc path doc.Password with
            | Ok d -> Some d
            | Error e -> r.Note $"did not open: {e}"; None
        | _ ->
            match openDoc path doc.Password with
            | Ok d -> Some d
            | Error e -> r.Fail("open", $"{e}"); None
    match opened with
    | Some d ->
        try checkOpened r doc d
        finally (d :> IDisposable).Dispose()
    | None -> ()
    r.Finish()

[<Theory>]
[<MemberData(nameof Data.CoreIds, MemberType = typeof<Data>)>]
let ``corpus contract`` (id: string) = runContract id

[<Theory>]
[<MemberData(nameof Data.ExtendedIds, MemberType = typeof<Data>)>]
let ``extended corpus contract`` (id: string) =
    let doc = Corpus.find id
    if not (File.Exists(Corpus.cachePath doc)) then
        Assert.Skip $"{id}: extended corpus not fetched (dotnet run --project tools/PdfButler.Corpus -- fetch --extended)"
    runContract id

[<Fact>]
let ``manifest is well formed`` () =
    let m = Corpus.manifest.Value
    let ids = m.Docs |> List.map (fun d -> d.Id)
    Assert.Equal(ids.Length, (List.distinct ids).Length)
    for d in m.Docs do
        Assert.True(m.Sources.ContainsKey d.Source, $"{d.Id}: unknown source {d.Source}")
        Assert.True(d.Sha256.IsSome, $"{d.Id}: missing sha256 pin")
        Assert.True(List.contains d.Expect.Open [ "ok"; "password"; "error"; "any" ], $"{d.Id}: bad expect.open")
        if d.Expect.Open = "password" then Assert.True(d.Password.IsSome, $"{d.Id}: password expected but not given")
    // No duplicated bytes counted twice.
    let hashes = m.Docs |> List.choose (fun d -> d.Sha256)
    Assert.Equal(hashes.Length, (List.distinct hashes).Length)
