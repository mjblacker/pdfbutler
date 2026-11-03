/// Writes small PDFs whose exact content is known, so tests have ground truth that does not
/// depend on any PDF engine. Everything is in PDF user space (points, y-up).
module PdfButler.TestSupport.SyntheticPdf

open System
open System.Globalization
open System.IO
open System.Text

type Box = { X0: float; Y0: float; X1: float; Y1: float }

let box x0 y0 x1 y1 = { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1 }

/// Helvetica (standard 14 font, WinAnsi). `X`,`Y` is the baseline start.
type TextItem = { X: float; Y: float; Size: float; Text: string }

/// Filled rectangle; Gray 0 = black, 1 = white.
type RectItem = { R: Box; Gray: float }

type LinkTarget =
    | ToPage of page: int * top: float option
    | ToUri of string
    | Launch of string

type LinkItem = { Area: Box; Target: LinkTarget }

type PageSpec =
    { MediaBox: Box
      CropBox: Box option
      Rotate: int
      Texts: TextItem list
      Rects: RectItem list
      Links: LinkItem list }

type OutlineSpec =
    { Title: string
      Page: int
      Top: float option
      Children: OutlineSpec list }

/// Page label range: style is "D" (decimal), "r"/"R" (roman), "a"/"A" (letters), or "" (none).
type LabelRange = { StartPage: int; Style: string; Prefix: string; First: int }

type DocSpec =
    { Pages: PageSpec list
      Outline: OutlineSpec list
      Labels: LabelRange list
      Title: string option
      /// Include a document-level JavaScript OpenAction (must never run).
      OpenActionJavaScript: string option }

let page (w: float) (h: float) =
    { MediaBox = box 0.0 0.0 w h
      CropBox = None
      Rotate = 0
      Texts = []
      Rects = []
      Links = [] }

let doc pages =
    { Pages = pages
      Outline = []
      Labels = []
      Title = None
      OpenActionJavaScript = None }

let private num (v: float) =
    let r = Math.Round(v, 4)
    r.ToString("0.####", CultureInfo.InvariantCulture)

let private pdfString (s: string) =
    let sb = StringBuilder("(")
    for ch in s do
        match ch with
        | '(' | ')' | '\\' -> sb.Append('\\').Append(ch) |> ignore
        | c when int c < 32 || int c > 255 -> sb.Append('?') |> ignore
        | c -> sb.Append(c) |> ignore
    sb.Append(")").ToString()

let private boxArr (b: Box) = $"[{num b.X0} {num b.Y0} {num b.X1} {num b.Y1}]"

let private contentStream (p: PageSpec) =
    let sb = StringBuilder()
    for r in p.Rects do
        sb.Append($"{num r.Gray} g {num r.R.X0} {num r.R.Y0} {num (r.R.X1 - r.R.X0)} {num (r.R.Y1 - r.R.Y0)} re f\n") |> ignore
    if not p.Texts.IsEmpty then
        sb.Append("0 g\n") |> ignore
    for t in p.Texts do
        sb.Append($"BT /F1 {num t.Size} Tf {num t.X} {num t.Y} Td {pdfString t.Text} Tj ET\n") |> ignore
    sb.ToString()

/// Serialise the document to PDF bytes (classic xref table, uncompressed streams).
let write (spec: DocSpec) : byte[] =
    let pages = Array.ofList spec.Pages
    let n = pages.Length
    // Fixed object numbers: 1 catalog, 2 pages, 3 font, 4 info, 5 outlines, 6 JS action,
    // then 2 objects per page, then outline items.
    let pageObj i = 7 + 2 * i
    let contentObj i = 8 + 2 * i
    let firstOutline = 7 + 2 * n

    // Flatten outline in pre-order, assigning object numbers.
    let outlineItems = ResizeArray<OutlineSpec * int * int>() // item, obj, parentObj
    let rec assign (items: OutlineSpec list) parent =
        for it in items do
            let o = firstOutline + outlineItems.Count
            outlineItems.Add((it, o, parent))
            assign it.Children o
    assign spec.Outline 5
    let objOf (it: OutlineSpec) = outlineItems |> Seq.find (fun (x, _, _) -> obj.ReferenceEquals(x, it)) |> fun (_, o, _) -> o
    let rec countAll (items: OutlineSpec list) = items |> List.sumBy (fun i -> 1 + countAll i.Children)

    let dest pageIndex (top: float option) =
        match top with
        | Some t -> $"[{pageObj pageIndex} 0 R /XYZ null {num t} null]"
        | None -> $"[{pageObj pageIndex} 0 R /Fit]"

    let objects = Collections.Generic.SortedDictionary<int, string>()

    let catalogExtras =
        let sb = StringBuilder()
        if not spec.Outline.IsEmpty then sb.Append(" /Outlines 5 0 R") |> ignore
        if spec.OpenActionJavaScript.IsSome then sb.Append(" /OpenAction 6 0 R") |> ignore
        if not spec.Labels.IsEmpty then
            sb.Append(" /PageLabels << /Nums [") |> ignore
            for l in spec.Labels do
                let style = if l.Style = "" then "" else $" /S /{l.Style}"
                let prefix = if l.Prefix = "" then "" else $" /P {pdfString l.Prefix}"
                sb.Append($" {l.StartPage} <<{style}{prefix} /St {l.First} >>") |> ignore
            sb.Append(" ] >>") |> ignore
        sb.ToString()

    objects[1] <- $"<< /Type /Catalog /Pages 2 0 R{catalogExtras} >>"
    let kids = String.Join(" ", [ for i in 0 .. n - 1 -> $"{pageObj i} 0 R" ])
    objects[2] <- $"<< /Type /Pages /Kids [{kids}] /Count {n} >>"
    objects[3] <- "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
    let title = spec.Title |> Option.map (fun t -> $" /Title {pdfString t}") |> Option.defaultValue ""
    objects[4] <- $"<< /Producer (PdfButler synthetic){title} >>"
    if not spec.Outline.IsEmpty then
        let first = objOf spec.Outline.Head
        let last = objOf (List.last spec.Outline)
        objects[5] <- $"<< /Type /Outlines /First {first} 0 R /Last {last} 0 R /Count {countAll spec.Outline} >>"
    match spec.OpenActionJavaScript with
    | Some js -> objects[6] <- $"<< /Type /Action /S /JavaScript /JS {pdfString js} >>"
    | None -> ()

    for i in 0 .. n - 1 do
        let p = pages[i]
        let crop = p.CropBox |> Option.map (fun b -> $" /CropBox {boxArr b}") |> Option.defaultValue ""
        let rot = if p.Rotate <> 0 then $" /Rotate {p.Rotate}" else ""
        let annots =
            if p.Links.IsEmpty then ""
            else
                let items =
                    p.Links
                    |> List.map (fun l ->
                        let action =
                            match l.Target with
                            | ToPage(pg, top) -> $"/Dest {dest pg top}"
                            | ToUri u -> $"/A << /S /URI /URI {pdfString u} >>"
                            | Launch f -> $"/A << /S /Launch /F {pdfString f} >>"
                        $"<< /Type /Annot /Subtype /Link /Rect {boxArr l.Area} /Border [0 0 0] {action} >>")
                $" /Annots [{String.Join(' ', items)}]"
        objects[pageObj i] <-
            $"<< /Type /Page /Parent 2 0 R /MediaBox {boxArr p.MediaBox}{crop}{rot} /Resources << /Font << /F1 3 0 R >> >> /Contents {contentObj i} 0 R{annots} >>"
        let content = contentStream p
        objects[contentObj i] <- $"<< /Length {Encoding.Latin1.GetByteCount content} >>\nstream\n{content}endstream"

    // Outline items with sibling links.
    let rec emit (items: OutlineSpec list) parent =
        let arr = Array.ofList items
        for k in 0 .. arr.Length - 1 do
            let it = arr[k]
            let o = objOf it
            let sb = StringBuilder($"<< /Title {pdfString it.Title} /Parent {parent} 0 R /Dest {dest it.Page it.Top}")
            if k > 0 then sb.Append($" /Prev {objOf arr[k - 1]} 0 R") |> ignore
            if k < arr.Length - 1 then sb.Append($" /Next {objOf arr[k + 1]} 0 R") |> ignore
            if not it.Children.IsEmpty then
                sb.Append($" /First {objOf it.Children.Head} 0 R /Last {objOf (List.last it.Children)} 0 R /Count {countAll it.Children}") |> ignore
            sb.Append(" >>") |> ignore
            objects[o] <- sb.ToString()
            emit it.Children o
    emit spec.Outline 5

    use ms = new MemoryStream()
    let enc = Encoding.Latin1
    let w (s: string) = let b = enc.GetBytes s in ms.Write(b, 0, b.Length)
    w "%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"
    let maxObj = if objects.Count = 0 then 0 else Seq.max objects.Keys
    let offsets = Array.create (maxObj + 1) -1L
    for kv in objects do
        offsets[kv.Key] <- ms.Position
        w $"{kv.Key} 0 obj\n{kv.Value}\nendobj\n"
    let xref = ms.Position
    w $"xref\n0 {maxObj + 1}\n"
    w "0000000000 65535 f \n"
    for i in 1 .. maxObj do
        if offsets[i] >= 0L then w (sprintf "%010d 00000 n \n" offsets[i])
        else w "0000000000 65535 f \n"
    w $"trailer\n<< /Size {maxObj + 1} /Root 1 0 R /Info 4 0 R >>\nstartxref\n{xref}\n%%%%EOF\n"
    ms.ToArray()

let writeFile (path: string) (spec: DocSpec) =
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path)) |> ignore
    File.WriteAllBytes(path, write spec)
    path

/// Expected display-space transform per the PDF spec (independent of any engine):
/// crop box origin moved to top-left, y flipped, then rotated clockwise by /Rotate.
let expectedDisplayPoint (p: PageSpec) (x: float) (y: float) : float * float =
    let c = defaultArg p.CropBox p.MediaBox
    let w = c.X1 - c.X0
    let h = c.Y1 - c.Y0
    let ux = x - c.X0
    let uy = c.Y1 - y
    match ((p.Rotate % 360) + 360) % 360 with
    | 90 -> h - uy, ux
    | 180 -> w - ux, h - uy
    | 270 -> uy, w - ux
    | _ -> ux, uy

let expectedDisplaySize (p: PageSpec) : float * float =
    let c = defaultArg p.CropBox p.MediaBox
    let w = c.X1 - c.X0
    let h = c.Y1 - c.Y0
    if (((p.Rotate % 360) + 360) % 360) % 180 = 90 then h, w else w, h
