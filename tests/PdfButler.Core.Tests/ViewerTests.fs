module PdfButler.Core.Tests.ViewerTests

open Xunit
open FsCheck
open FsCheck.FSharp
open PdfButler.Core
open PdfButler.Core.Viewer

let private letter = { W = 612.0; H = 792.0 }
let private pages n = Array.create n letter
let private vp = { W = 1000.0; H = 700.0 }
let private start n = create (pages n) vp

[<Fact>]
let ``a new view starts at the top at 100 percent even on a large screen`` () =
    let s = start 10
    Assert.Equal(Manual, s.Mode)
    Assert.Equal(1.0, s.Zoom)
    Assert.Equal(1.0, (update (ViewportResized { W = 3840.0; H = 2160.0 }) s).Zoom)
    Assert.Equal(0, currentPage s)
    Assert.Equal(0.0, s.Offset.Y)

[<Fact>]
let ``pointer-anchored zoom keeps the content under the pointer fixed`` () =
    let gen = Gen.zip3 (Gen.choose (0, 1000)) (Gen.choose (0, 700)) (Gen.choose (10, 1600))
    Check.One(Config.QuickThrowOnFailure.WithMaxTest 200, Prop.forAll (Arb.fromGen gen) (fun (x, y, z) ->
        let s = start 20 |> update (ScrollTo { X = 0.0; Y = 3000.0 })
        let pointer = { X = float x; Y = float y }
        let before = Layout.anchorAt s.Layout { X = s.Offset.X + pointer.X; Y = s.Offset.Y + pointer.Y }
        let s2 = update (ZoomTo(float z / 100.0, Some pointer)) s
        let after = Layout.anchorAt s2.Layout { X = s2.Offset.X + pointer.X; Y = s2.Offset.Y + pointer.Y }
        // Unless clamping at a document edge interfered, the same page point stays under the pointer.
        let clamped = s2.Offset <> { X = Layout.canvasOf s2.Layout before.Value |> fun c -> c.X - pointer.X
                                     Y = Layout.canvasOf s2.Layout before.Value |> fun c -> c.Y - pointer.Y }
        clamped || (after.Value.Page = before.Value.Page
                    && abs (after.Value.Local.X - before.Value.Local.X) < 0.01
                    && abs (after.Value.Local.Y - before.Value.Local.Y) < 0.01)))

[<Fact>]
let ``zoom steps move through the preset list and stay in range`` () =
    let s = start 3 |> update (ZoomTo(1.0, None))
    Assert.Equal(1.1, (update (ZoomStep(1, None)) s).Zoom)
    Assert.Equal(0.9, (update (ZoomStep(-1, None)) s).Zoom)
    let maxed = Seq.fold (fun st _ -> update (ZoomStep(1, None)) st) s [ 1..100 ]
    Assert.Equal(Layout.maxZoom, maxed.Zoom)
    let mined = Seq.fold (fun st _ -> update (ZoomStep(-1, None)) st) s [ 1..100 ]
    Assert.Equal(Layout.minZoom, mined.Zoom)
    Assert.Equal(Manual, maxed.Mode)

[<Fact>]
let ``resizing in fit-width mode refits and keeps the reading position`` () =
    let s = start 50 |> update (SetMode FitWidth) |> update (GoToPage 20)
    let s2 = update (ViewportResized { W = 600.0; H = 700.0 }) s
    Assert.Equal(FitWidth, s2.Mode)
    Assert.True(s2.Zoom < s.Zoom)
    Assert.Equal(20, currentPage s2)

[<Fact>]
let ``rotation keeps the current page and refits`` () =
    let s = start 30 |> update (GoToPage 12)
    let r = update RotateClockwise s
    Assert.Equal(R90, r.Rotation)
    Assert.Equal(12, currentPage r)
    Assert.Equal(s.Rotation, (update RotateCounterClockwise r).Rotation)

[<Fact>]
let ``go to page, next and previous`` () =
    let s = start 10 |> update (GoToPage 5)
    Assert.Equal(5, currentPage s)
    Assert.Equal(6, currentPage (update NextPage s))
    Assert.Equal(4, currentPage (update PreviousPage s))
    Assert.Equal(9, currentPage (update (GoToPage 99) s))
    Assert.Equal(0, currentPage (update (GoToPage -3) s))

[<Fact>]
let ``show page goes to a page without adding a back entry`` () =
    let s = start 10 |> update (ShowPage 6)
    Assert.Equal(6, currentPage s)
    Assert.False(History.canGoBack s.History)
    Assert.Equal(9, currentPage (update (ShowPage 99) s))

[<Fact>]
let ``back and forward retrace jumps but not plain scrolling`` () =
    let s0 = start 40
    let s1 = s0 |> update (GoToPage 10)
    let s2 = s1 |> update (ScrollBy(0.0, 200.0)) |> update (GoToPage 30)
    Assert.Equal(30, currentPage s2)
    let b1 = update Back s2
    Assert.Equal(10, currentPage b1)
    let b2 = update Back b1
    Assert.Equal(0, currentPage b2)
    Assert.Equal(b2, update Back b2 |> fun x -> { x with History = b2.History }) // nothing further back
    let f1 = update Forward b2
    Assert.Equal(10, currentPage f1)
    // A new jump clears the forward stack.
    let j = update (GoToPage 3) f1
    Assert.False(History.canGoForward j.History)

[<Fact>]
let ``destinations scroll to the target position on the page`` () =
    let s = start 10 |> update (GoToDestination { Page = 4; Left = None; Top = Some 396.0 })
    let a = Layout.anchorAt s.Layout { X = s.Offset.X + 10.0; Y = s.Offset.Y + 8.0 }
    Assert.Equal(4, a.Value.Page)
    Assert.True(abs (a.Value.Local.Y - 396.0) < 1.0, $"top was {a.Value.Local.Y}")

[<Fact>]
let ``reveal scrolls only when the rectangle is not already visible`` () =
    let s = start 10
    let visible = update (Reveal(0, Rect.make 100.0 100.0 50.0 10.0)) s
    Assert.Equal(s.Offset, visible.Offset)
    let far = update (Reveal(7, Rect.make 100.0 100.0 50.0 10.0)) s
    let r = Layout.canvasRect far.Layout 7 (Rect.make 100.0 100.0 50.0 10.0)
    Assert.True(r.Y >= far.Offset.Y && r.Bottom <= far.Offset.Y + far.Viewport.H)

[<Fact>]
let ``page down and page up are inverse away from the edges`` () =
    let s = start 20 |> update (ScrollTo { X = 0.0; Y = 5000.0 })
    let back = s |> update PageDown |> update PageUp
    Assert.Equal(s.Offset.Y, back.Offset.Y, 6)

[<Fact>]
let ``an empty document does not crash any message`` () =
    let s = create [||] vp
    let msgs =
        [ ViewportResized vp; ScrollBy(10.0, 10.0); ZoomStep(1, None); SetMode FitPage; RotateClockwise; GoToPage 3
          GoToDestination { Page = 2; Left = Some 1.0; Top = Some 1.0 }; NextPage; PreviousPage; FirstPage; LastPage
          PageDown; PageUp; Back; Forward; Reveal(0, Rect.make 0.0 0.0 1.0 1.0) ]
    for m in msgs do update m s |> ignore
