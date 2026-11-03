/// Ground-truth tests: PDFs generated with known content, checked against the PDF specification's
/// geometry rules rather than against any engine's output.
module PdfButler.Engine.Tests.SyntheticTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open Xunit
open PdfButler.Core
open PdfButler.Pdfium
open PdfButler.TestSupport
open PdfButler.TestSupport.SyntheticPdf
open PdfButler.Engine.Tests.Helpers

// A black square at a known PDF position plus a label, on pages with every /Rotate value,
// offset media boxes, crop boxes and a negative origin.
let private square = box 100.0 600.0 150.0 650.0

let private geometryPages =
    let withSquare (sq: Box) (label: string) (p: PageSpec) =
        { p with
            Rects = [ { R = sq; Gray = 0.0 } ]
            Texts = [ { X = sq.X0; Y = sq.Y1 + 20.0; Size = 24.0; Text = label } ] }
    [ withSquare square "ROT0" (page 612.0 792.0)
      withSquare square "ROT90" { page 612.0 792.0 with Rotate = 90 }
      withSquare square "ROT180" { page 612.0 792.0 with Rotate = 180 }
      withSquare square "ROT270" { page 612.0 792.0 with Rotate = 270 }
      withSquare (box 200.0 800.0 250.0 850.0) "OFFSET" { page 0.0 0.0 with MediaBox = box 100.0 200.0 712.0 992.0; CropBox = Some(box 150.0 250.0 650.0 950.0) }
      withSquare (box 200.0 800.0 250.0 850.0) "OFFSET90" { page 0.0 0.0 with MediaBox = box 100.0 200.0 712.0 992.0; CropBox = Some(box 150.0 250.0 650.0 950.0); Rotate = 90 }
      withSquare (box -200.0 100.0 -150.0 150.0) "NEG270" { page 0.0 0.0 with MediaBox = box -300.0 -300.0 300.0 300.0; Rotate = 270 }
      withSquare square "NEGROT" { page 612.0 792.0 with Rotate = -90 } ]

let private geometryDoc = lazy (synthetic "geometry.pdf" (doc geometryPages))

let private expectedDisplayRect (p: PageSpec) (b: Box) =
    let x0, y0 = expectedDisplayPoint p b.X0 b.Y0
    let x1, y1 = expectedDisplayPoint p b.X1 b.Y1
    Rect.ofLTRB x0 y0 x1 y1

[<Fact>]
let ``page sizes follow crop box and /Rotate`` () =
    use d = openOk geometryDoc.Value
    Assert.Equal(geometryPages.Length, d.Info.PageCount)
    geometryPages
    |> List.iteri (fun i p ->
        let ew, eh = expectedDisplaySize p
        let s = d.Info.Pages[i].Size
        Assert.True(abs (s.W - ew) < 0.01 && abs (s.H - eh) < 0.01, $"page {i}: expected {ew}x{eh}, engine {s.W}x{s.H}"))

[<Fact>]
let ``engine page matrix matches the PDF specification`` () =
    use d = openOk geometryDoc.Value
    geometryPages
    |> List.iteri (fun i p ->
        let m = await' (d.GetPageMatrixAsync i)
        for (x, y) in [ (p.Rects.Head.R.X0, p.Rects.Head.R.Y0); (p.Rects.Head.R.X1, p.Rects.Head.R.Y1); (0.0, 0.0) ] do
            let ex, ey = expectedDisplayPoint p x y
            let got = Affine.apply m { X = x; Y = y }
            Assert.True(abs (got.X - ex) < 0.05 && abs (got.Y - ey) < 0.05, $"page {i} point ({x},{y}): expected ({ex},{ey}) got ({got.X},{got.Y})"))

[<Fact>]
let ``rendered pixels land where the geometry says, for every page and view rotation`` () =
    use d = openOk geometryDoc.Value
    let scale = 1.5
    for i, p in List.indexed geometryPages do
        let size = d.Info.Pages[i].Size
        let display = expectedDisplayRect p p.Rects.Head.R
        for r in [ R0; R90; R180; R270 ] do
            let img = render d i r scale None
            let fw, fh = Geometry.devicePageSize r size scale
            Assert.Equal((fw, fh), (img.Width, img.Height))
            let v = Affine.applyRect (Geometry.displayToView r size scale) display
            let c = Rect.center v
            Assert.True(lum img (int c.X) (int c.Y) < 64, $"page {i} {r}: square centre should be black")
            // Just outside each edge must be white (and inside the page).
            for (x, y) in [ (v.X - 4.0, c.Y); (v.Right + 4.0, c.Y); (c.X, v.Y - 4.0); (c.X, v.Bottom + 4.0) ] do
                if x >= 0.0 && y >= 0.0 && x < float img.Width && y < float img.Height then
                    Assert.True(lum img (int x) (int y) > 200, $"page {i} {r}: ({x},{y}) outside the square should be white")

[<Fact>]
let ``character boxes are in display space and match the rendered text`` () =
    use d = openOk geometryDoc.Value
    for i, p in List.indexed geometryPages do
        let t = text d i
        let label = p.Texts.Head
        Assert.Contains(label.Text, TextLayer.fullText t)
        let first = t.Chars |> Array.findIndex (fun c -> c.Code = int label.Text[0])
        // A point just above the baseline at the start of the label must be inside the first glyph box.
        let ex, ey = expectedDisplayPoint p (label.X + 4.0) (label.Y + 6.0)
        let b = t.Chars[first].Box
        Assert.True(Rect.distance { X = ex; Y = ey } b < 1.5, $"page {i}: expected ({ex},{ey}) inside {b}")

[<Fact>]
let ``a tile equals the same region of a whole-page render`` () =
    use d = openOk geometryDoc.Value
    for i in [ 0; 1; 4 ] do
        let whole = render d i R90 2.0 None
        let clip = { X = 37; Y = 91; W = 300; H = 200 }
        let tile = render d i R90 2.0 (Some clip)
        Assert.Equal((300, 200), (tile.Width, tile.Height))
        let mutable diff = 0
        for y in 0 .. clip.H - 1 do
            for x in 0 .. clip.W - 1 do
                if abs (lum whole (clip.X + x) (clip.Y + y) - lum tile x y) > 16 then diff <- diff + 1
        Assert.True(float diff / float (clip.W * clip.H) < 0.002, $"page {i}: {diff} pixels differ between tile and whole render")

[<Fact>]
let ``rendered bitmaps are opaque white where the page is empty`` () =
    use d = openOk geometryDoc.Value
    let img = render d 0 R0 1.0 None
    Assert.Equal(255, lum img 2 2)
    Assert.Equal(img.Width * 4, img.Stride)

// ---- mixed page sizes ------------------------------------------------------------------

[<Fact>]
let ``mixed page sizes are reported exactly`` () =
    let sizes = [ 595.0, 842.0; 612.0, 792.0; 1191.0, 842.0; 72.0, 72.0; 200.0, 3000.0 ]
    let path = synthetic "sizes.pdf" (doc [ for (w, h) in sizes -> page w h ])
    use d = openOk path
    Assert.Equal<(float * float) list>(sizes, [ for p in d.Info.Pages -> p.Size.W, p.Size.H ])

// ---- long documents, labels --------------------------------------------------------------

let private longDocCount = 1500

let private longDoc =
    lazy
        (synthetic
            "long.pdf"
            { doc [ for i in 1..longDocCount -> { page 612.0 792.0 with Texts = [ { X = 72.0; Y = 700.0; Size = 18.0; Text = $"Page {i} of {longDocCount}" } ] } ] with
                Labels = [ { StartPage = 0; Style = "r"; Prefix = ""; First = 1 }; { StartPage = 4; Style = "D"; Prefix = ""; First = 1 } ] })

[<Fact>]
let ``long document opens quickly and labels follow the page label tree`` () =
    let sw = Stopwatch.StartNew()
    use d = openOk longDoc.Value
    sw.Stop()
    Assert.Equal(longDocCount, d.Info.PageCount)
    // Generous bound: this guards against accidental per-page work at open (e.g. loading every page).
    Assert.True(sw.Elapsed < TimeSpan.FromSeconds 5.0, $"open took {sw.Elapsed}")
    let label i = d.Info.Pages[i].Label
    Assert.Equal(Some "i", label 0)
    Assert.Equal(Some "iv", label 3)
    Assert.Equal(Some "1", label 4)
    Assert.Equal(Some(string (longDocCount - 4)), label (longDocCount - 1))

[<Fact>]
let ``text from deep in a long document is the right page`` () =
    use d = openOk longDoc.Value
    for i in [ 0; 777; longDocCount - 1 ] do
        Assert.Contains($"Page {i + 1} of {longDocCount}", TextLayer.fullText (text d i))
    let img = render d (longDocCount - 1) R0 0.5 None
    Assert.True(ImageCompare.inkFraction (toRgb img) > 0.0)

[<Fact>]
let ``search over engine text finds exactly the expected pages`` () =
    use d = openOk longDoc.Value
    let hits =
        [ for i in 0..99 -> Search.findInPage Search.defaultOptions "page 5" (text d i) ]
        |> Array.concat
    // "Page 5 of", "Page 50".."Page 59" -> 11 pages in the first 100.
    Assert.Equal<int list>([ 4; 49; 50; 51; 52; 53; 54; 55; 56; 57; 58 ], hits |> Array.map (fun h -> h.Page) |> List.ofArray)

// ---- giant pages and resource limits -----------------------------------------------------

[<Fact>]
let ``giant pages render at low zoom and as tiles, and oversized requests are refused`` () =
    let path = synthetic "giant.pdf" (doc [ { page 14400.0 14400.0 with Rects = [ { R = box 7000.0 7000.0 7400.0 7400.0; Gray = 0.0 } ] } ])
    use d = openOk path
    Assert.Equal(14400.0, d.Info.Pages[0].Size.W)
    let small = render d 0 R0 0.05 None
    Assert.Equal((720, 720), (small.Width, small.Height))
    // 57,600 px square at scale 4: a whole-page bitmap must be refused before allocation...
    let ex = Assert.ThrowsAny<exn>(fun () -> render d 0 R0 4.0 None |> ignore)
    Assert.IsType<ArgumentOutOfRangeException>(ex.GetBaseException()) |> ignore
    // ...but a tile in the middle works and contains the square (display y = 14400 - 7200 = 7200).
    let tile = render d 0 R0 4.0 (Some { X = 28700; Y = 28700; W = 512; H = 512 })
    Assert.True(lum tile 256 256 < 64)
    // Beyond PDFium's int device space: refused.
    Assert.ThrowsAny<exn>(fun () -> render d 0 R0 100.0 (Some { X = 0; Y = 0; W = 10; H = 10 }) |> ignore) |> ignore

// ---- links and outline -------------------------------------------------------------------

let private navDoc =
    lazy
        (let pages =
            [ { page 612.0 792.0 with
                  Texts = [ { X = 72.0; Y = 700.0; Size = 14.0; Text = "Go to page three" } ]
                  Links =
                    [ { Area = box 72.0 690.0 300.0 720.0; Target = ToPage(2, Some 500.0) }
                      { Area = box 72.0 600.0 300.0 630.0; Target = ToUri "https://example.com/a?b=1" }
                      { Area = box 72.0 500.0 300.0 530.0; Target = Launch "calc.exe" } ] }
              page 612.0 792.0
              { page 612.0 792.0 with Rotate = 90 } ]
         synthetic
             "nav.pdf"
             { doc pages with
                 Title = Some "Navigation test"
                 Outline =
                   [ { Title = "Chapter 1"; Page = 0; Top = None; Children = [] }
                     { Title = "Chapter 2"; Page = 1; Top = Some 400.0
                       Children = [ { Title = "Section 2.1"; Page = 2; Top = Some 300.0; Children = [] } ] } ] })

[<Fact>]
let ``links are typed: internal, web and launch (never followed)`` () =
    use d = openOk navDoc.Value
    let links = await' (d.GetLinksAsync(0, Priority.Interactive, CancellationToken.None))
    Assert.Equal(3, links.Length)
    let byY = links |> Array.sortBy (fun l -> l.Bounds.Y)
    match byY[0].Target with
    | GoTo dest ->
        Assert.Equal(2, dest.Page)
        let r = await' (d.ResolveAsync(dest, CancellationToken.None))
        // Page 2 has /Rotate 90: PDF y=500 maps to display x = 792 - 500 = 292.
        Assert.Equal(2, r.Page)
        Assert.True(r.Top.IsSome)
    | t -> failwith $"expected GoTo, got {t}"
    Assert.Equal(Uri "https://example.com/a?b=1", byY[1].Target)
    Assert.Equal(Unsupported "launch application", byY[2].Target)
    // Link rectangles are in display space: PDF y 690..720 -> display y 72..102.
    Assert.True(abs (byY[0].Bounds.Y - 72.0) < 0.5 && abs (byY[0].Bounds.Bottom - 102.0) < 0.5, $"{byY[0].Bounds}")

[<Fact>]
let ``destination positions resolve to display space`` () =
    use d = openOk navDoc.Value
    let r = await' (d.ResolveAsync({ Page = 1; X = None; Y = Some 400.0 }, CancellationToken.None))
    Assert.Equal(Some(792.0 - 400.0), r.Top |> Option.map (fun v -> Math.Round(v, 2)))
    Assert.Equal(None, r.Left)

[<Fact>]
let ``outline structure, titles and targets`` () =
    use d = openOk navDoc.Value
    let o = await' (d.GetOutlineAsync CancellationToken.None)
    Assert.Equal<string list>([ "Chapter 1"; "Chapter 2" ], o |> List.map (fun i -> i.Title))
    let ch2 = o[1]
    Assert.Equal<string list>([ "Section 2.1" ], ch2.Children |> List.map (fun i -> i.Title))
    match ch2.Target with
    | Some(GoTo dest) -> Assert.Equal(1, dest.Page); Assert.Equal(Some 400.0, dest.Y)
    | t -> failwith $"unexpected {t}"
    Assert.Equal(Some "Navigation test", d.Info.Title)

[<Fact>]
let ``documents with JavaScript open actions open and render without running anything`` () =
    let path = synthetic "js.pdf" { doc [ { page 300.0 300.0 with Texts = [ { X = 20.0; Y = 150.0; Size = 20.0; Text = "inert" } ] } ] with OpenActionJavaScript = Some "app.alert('should never run')" }
    use d = openOk path
    let img = render d 0 R0 1.0 None
    Assert.True(ImageCompare.inkFraction (toRgb img) > 0.0)
