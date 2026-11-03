/// Text search over the engine's text layer.
///
/// Matching rules (documented so the tests can pin them down):
///  * case-insensitive unless MatchCase;
///  * Unicode NFKC per character, so ligatures (U+FB01 'ﬁ') match "fi" and full-width forms match ASCII;
///  * any run of whitespace or engine line breaks matches a single space;
///  * a hyphen (or soft hyphen) immediately followed by a line break is dropped together with
///    the break, so "exam-<newline>ple" is found by "example";
///  * diacritics are significant ("é" does not match "e").
module PdfButler.Core.Search

open System
open System.Text

type Options = { MatchCase: bool }

let defaultOptions = { MatchCase = false }

/// Normalised searchable text plus a map from each normalised char back to its source char index.
type NormalizedText = { Text: string; Map: int[] }

let private isBreak code = code = 0x0A || code = 0x0D

let private isSpace code =
    code = 0x20 || code = 0x09 || isBreak code || code = 0xA0 || code = 0x3000
    || (code < 0x10000 && Char.IsWhiteSpace(char code))

let private isHyphen code = code = 0x2D || code = 0xAD || code = 0x02 || code = 0x2010

let private validScalar code =
    code > 0 && code <= 0x10FFFF && not (code >= 0xD800 && code <= 0xDFFF)

/// Core normaliser over (codepoint, sourceIndex) pairs.
let private normalizeSeq (opts: Options) (items: (int * int)[]) : NormalizedText =
    let sb = StringBuilder()
    let map = ResizeArray<int>()
    let mutable lastSpace = true
    let mutable i = 0

    let nextIsBreak j =
        // Skip spaces between a hyphen and the line break.
        let mutable k = j
        while k < items.Length && fst items[k] = 0x20 do
            k <- k + 1
        k < items.Length && isBreak (fst items[k])

    while i < items.Length do
        let code, src = items[i]

        if isHyphen code && nextIsBreak (i + 1) then
            // Drop the hyphen and the following break/space run.
            i <- i + 1
            while i < items.Length && isSpace (fst items[i]) do
                i <- i + 1
        elif code = 0x02 || code = 0xAD then
            i <- i + 1
        elif isSpace code then
            if not lastSpace then
                sb.Append ' ' |> ignore
                map.Add src
                lastSpace <- true

            i <- i + 1
        elif validScalar code then
            let s = Char.ConvertFromUtf32(code).Normalize(NormalizationForm.FormKC)
            let s = if opts.MatchCase then s else s.ToLowerInvariant()

            for ch in s do
                sb.Append ch |> ignore
                map.Add src

            lastSpace <- false
            i <- i + 1
        else
            i <- i + 1

    // Trim trailing space.
    if sb.Length > 0 && sb[sb.Length - 1] = ' ' then
        sb.Length <- sb.Length - 1
        map.RemoveAt(map.Count - 1)

    { Text = sb.ToString(); Map = map.ToArray() }

let normalizeChars (opts: Options) (chars: TextChar[]) : NormalizedText =
    chars |> Array.mapi (fun i c -> c.Code, i) |> normalizeSeq opts

let private codepoints (s: string) =
    let out = ResizeArray<int * int>()
    let mutable i = 0

    while i < s.Length do
        if Char.IsSurrogatePair(s, i) then
            out.Add(Char.ConvertToUtf32(s, i), i)
            i <- i + 2
        else
            out.Add(int s[i], i)
            i <- i + 1

    out.ToArray()

let normalizeQuery (opts: Options) (query: string) : string =
    if isNull query then "" else (normalizeSeq opts (codepoints query)).Text

/// Non-overlapping matches as inclusive source character ranges.
let findAll (nt: NormalizedText) (normalizedQuery: string) : (int * int) list =
    if String.IsNullOrEmpty normalizedQuery || nt.Text.Length = 0 then
        []
    else
        let results = ResizeArray()
        let mutable from = 0
        let mutable go = true

        while go do
            let k = nt.Text.IndexOf(normalizedQuery, from, StringComparison.Ordinal)

            if k < 0 then
                go <- false
            else
                results.Add(nt.Map[k], nt.Map[k + normalizedQuery.Length - 1])
                from <- k + normalizedQuery.Length

        List.ofSeq results

type Hit = { Page: int; Start: int; End: int }

let findInPage (opts: Options) (query: string) (text: PageText) : Hit[] =
    let q = normalizeQuery opts query
    let nt = normalizeChars opts text.Chars

    findAll nt q
    |> List.map (fun (s, e) -> { Page = text.Page; Start = s; End = e })
    |> Array.ofList

/// Incremental, page-by-page search results.
type State =
    { Query: string
      PageCount: int
      Hits: Map<int, Hit[]>
      Scanned: Set<int>
      PagesWithText: int
      Current: Hit option }

let start (query: string) (pageCount: int) =
    { Query = query
      PageCount = pageCount
      Hits = Map.empty
      Scanned = Set.empty
      PagesWithText = 0
      Current = None }

let isComplete (s: State) = s.Scanned.Count >= s.PageCount

let ordered (s: State) : Hit[] =
    s.Hits |> Map.toSeq |> Seq.collect snd |> Seq.sortBy (fun h -> h.Page, h.Start) |> Array.ofSeq

let total (s: State) = s.Hits |> Map.fold (fun acc _ v -> acc + v.Length) 0

/// Record the results for one page. The first hit found becomes current.
let addPage (page: int) (hits: Hit[]) (hasText: bool) (s: State) : State =
    if s.Scanned.Contains page then
        s
    else
        { s with
            Hits = (if hits.Length > 0 then s.Hits.Add(page, hits) else s.Hits)
            Scanned = s.Scanned.Add page
            PagesWithText = s.PagesWithText + (if hasText then 1 else 0)
            Current = (match s.Current with
                       | None when hits.Length > 0 -> Some hits[0]
                       | c -> c) }

let private step (dir: int) (fromPage: int) (s: State) : State =
    let all = ordered s

    if all.Length = 0 then
        s
    else
        let idx =
            match s.Current with
            | Some c ->
                match Array.tryFindIndex (fun h -> h = c) all with
                | Some i -> ((i + dir) % all.Length + all.Length) % all.Length
                | None -> 0
            | None ->
                if dir > 0 then
                    defaultArg (Array.tryFindIndex (fun h -> h.Page >= fromPage) all) 0
                else
                    defaultArg (Array.tryFindIndexBack (fun h -> h.Page <= fromPage) all) (all.Length - 1)

        { s with Current = Some all[idx] }

let next fromPage s = step 1 fromPage s
let previous fromPage s = step -1 fromPage s

type Status =
    | Idle
    | Searching of scanned: int * total: int * found: int
    | NoTextLayer
    | NoMatches
    | Found of index: int * total: int

let status (s: State) : Status =
    if String.IsNullOrWhiteSpace s.Query then
        Idle
    else
        let all = ordered s
        let idx = s.Current |> Option.bind (fun c -> Array.tryFindIndex (fun h -> h = c) all)

        match idx with
        | Some i when isComplete s -> Found(i + 1, all.Length)
        | _ when not (isComplete s) -> Searching(s.Scanned.Count, s.PageCount, all.Length)
        | _ when all.Length > 0 -> Found(1, all.Length)
        | _ when s.PagesWithText = 0 -> NoTextLayer
        | _ -> NoMatches

let describe =
    function
    | Idle -> ""
    | Searching(scanned, totalPages, found) -> $"Searching… page {scanned} of {totalPages} ({found} found)"
    | NoTextLayer -> "No searchable text in this document (it may be a scan without OCR)"
    | NoMatches -> "No matches"
    | Found(i, n) -> $"{i} of {n}"
