/// Hit testing, highlight geometry, selection and copy over an engine-provided text layer.
module PdfButler.Core.TextLayer

open System
open System.Text

let private usable (c: TextChar) = not c.Generated && c.Box.W > 0.0 && c.Box.H > 0.0

/// Drop characters that lie entirely outside the visible page box (e.g. text hidden by a crop box),
/// so search and copy never return text the reader cannot see. Engine-generated characters
/// (line breaks, inferred spaces) are kept only right after visible characters, so separators
/// around hidden text are never doubled. A whole run is kept: PDFium writes a line break as
/// two generated characters, "\r\n".
let visibleOnly (pageSize: Size) (chars: TextChar[]) : TextChar[] =
    let page = Rect.inflate 0.5 { X = 0.0; Y = 0.0; W = pageSize.W; H = pageSize.H }
    let out = ResizeArray<TextChar>(chars.Length)
    // Has visible text been kept since the last hidden character?
    let mutable lastVisible = false

    for c in chars do
        if c.Generated then
            if lastVisible then out.Add c
        elif c.Box.W <= 0.0 || c.Box.H <= 0.0 then
            // Zero-area real characters (some spaces): keep them next to visible text.
            if lastVisible then out.Add c
        elif Rect.intersects c.Box page then
            out.Add c
            lastVisible <- true
        else
            // Hidden character: separators that follow it are dropped.
            lastVisible <- false

    // No trailing engine-generated characters.
    while out.Count > 0 && out[out.Count - 1].Generated do
        out.RemoveAt(out.Count - 1)

    out.ToArray()

/// Character whose box contains `p` (display space), allowing `tolerance` points of slack.
let charAt (text: PageText) (tolerance: float) (p: Point) : int option =
    let chars = text.Chars
    let mutable found = -1
    let mutable i = 0

    while found < 0 && i < chars.Length do
        if usable chars[i] && Rect.contains p (Rect.inflate tolerance chars[i].Box) then
            found <- i

        i <- i + 1

    if found >= 0 then Some found else None

/// Nearest character to `p`, preferring characters on the same line. Used for drag selection.
let nearestChar (text: PageText) (p: Point) : int option =
    let chars = text.Chars
    let mutable best = -1
    let mutable bestScore = Double.MaxValue

    for i in 0 .. chars.Length - 1 do
        let c = chars[i]

        if usable c then
            let b = c.Box
            let sameLine = p.Y >= b.Y && p.Y <= b.Bottom
            let dx = max 0.0 (max (b.X - p.X) (p.X - b.Right))
            // Same-line characters always beat characters on other lines.
            let score = if sameLine then dx else 1.0e6 + Rect.distance p b

            if score < bestScore then
                best <- i
                bestScore <- score

    if best >= 0 then Some best else None

let private sameLine (a: Rect) (b: Rect) =
    let overlap = (min a.Bottom b.Bottom) - (max a.Y b.Y)
    let minH = min a.H b.H
    minH > 0.0 && overlap >= 0.5 * minH

/// Merge the boxes of characters [s..e] into one rectangle per line run.
let lineRects (chars: TextChar[]) (s: int) (e: int) : Rect list =
    let s = max 0 s
    let e = min (chars.Length - 1) e
    let result = ResizeArray<Rect>()
    let mutable current: Rect option = None

    for i in s..e do
        let c = chars[i]

        if usable c then
            match current with
            | Some r when sameLine r c.Box && Rect.distance (Rect.center c.Box) r < 3.0 * max r.H c.Box.H ->
                current <- Some(Rect.union r c.Box)
            | Some r ->
                result.Add r
                current <- Some c.Box
            | None -> current <- Some c.Box

    current |> Option.iter result.Add
    List.ofSeq result

let private appendCode (sb: StringBuilder) (code: int) =
    if code = 0x0D then ()
    elif code = 0x0A then sb.Append '\n' |> ignore
    elif code = 0x02 then () // PDFium marks end-of-line hyphenation positions with U+0002
    elif code > 0 && code <= 0x10FFFF && not (code >= 0xD800 && code <= 0xDFFF) then
        sb.Append(Char.ConvertFromUtf32 code) |> ignore

/// Plain text for characters [s..e], with engine line breaks as '\n'.
let extract (chars: TextChar[]) (s: int) (e: int) : string =
    let sb = StringBuilder()

    for i in max 0 s .. min (chars.Length - 1) e do
        appendCode sb chars[i].Code

    sb.ToString()

let fullText (text: PageText) = extract text.Chars 0 (text.Chars.Length - 1)

let private isWordCode (code: int) =
    code > 0 && code <= 0x10FFFF && not (code >= 0xD800 && code <= 0xDFFF)
    && (let s = Char.ConvertFromUtf32 code
        Char.IsLetterOrDigit(s, 0) || s = "_" || s = "'")

/// Word around character `i` (for double-click selection).
let wordAt (chars: TextChar[]) (i: int) : int * int =
    if i < 0 || i >= chars.Length || not (isWordCode chars[i].Code) then
        i, i
    else
        let mutable a = i
        let mutable b = i

        while a > 0 && isWordCode chars[a - 1].Code do
            a <- a - 1

        while b < chars.Length - 1 && isWordCode chars[b + 1].Code do
            b <- b + 1

        a, b

type TextPos = { Page: int; Index: int }

type Selection = { Anchor: TextPos; Focus: TextPos }

module Selection =
    let ordered (s: Selection) =
        if (s.Anchor.Page, s.Anchor.Index) <= (s.Focus.Page, s.Focus.Index) then
            s.Anchor, s.Focus
        else
            s.Focus, s.Anchor

    let pages (s: Selection) =
        let a, b = ordered s
        [ a.Page .. b.Page ]

    /// Inclusive character range of the selection on `page`, if any.
    let rangeOnPage (s: Selection) (page: int) (charCount: int) : (int * int) option =
        let a, b = ordered s

        if page < a.Page || page > b.Page || charCount = 0 then
            None
        else
            let first = if page = a.Page then a.Index else 0
            let last = if page = b.Page then b.Index else charCount - 1
            let first = max 0 first
            let last = min (charCount - 1) last
            if last < first then None else Some(first, last)

    /// Selected text across pages. `getText` must return every page in the selection.
    let text (s: Selection) (getText: int -> PageText) : string =
        let sb = StringBuilder()

        for page in pages s do
            let t = getText page

            match rangeOnPage s page t.Chars.Length with
            | Some(a, b) ->
                if sb.Length > 0 && sb[sb.Length - 1] <> '\n' then
                    sb.Append '\n' |> ignore

                sb.Append(extract t.Chars a b) |> ignore
            | None -> ()

        sb.ToString()
