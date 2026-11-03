module PdfButler.Core.Tests.GeometryLayoutTests

open System
open Xunit
open FsCheck
open FsCheck.FSharp
open PdfButler.Core

let private check (p: Property) = Check.One(Config.QuickThrowOnFailure.WithMaxTest 300, p)

let private genRotation = Gen.elements [ R0; R90; R180; R270 ]
let private genDim = Gen.choose (1, 20000) |> Gen.map (fun v -> float v / 10.0) // 0.1 .. 2000 pt
let private genSize = Gen.map2 (fun w h -> { W = w; H = h }) genDim genDim
let private genScale = Gen.choose (5, 3200) |> Gen.map (fun v -> float v / 100.0)
let private genUnit = Gen.choose (0, 1000) |> Gen.map (fun v -> float v / 1000.0)

let private near (tol: float) (a: Point) (b: Point) = abs (a.X - b.X) < tol && abs (a.Y - b.Y) < tol

[<Fact>]
let ``displayToView then viewToDisplay is the identity`` () =
    let gen = Gen.zip (Gen.zip genRotation genSize) (Gen.zip genScale (Gen.zip genUnit genUnit))
    check (Prop.forAll (Arb.fromGen gen) (fun ((r, s), (scale, (u, v))) ->
        let p = { X = u * s.W; Y = v * s.H }
        let q = Affine.apply (Geometry.viewToDisplay r s scale) (Affine.apply (Geometry.displayToView r s scale) p)
        near 1e-6 p q))

[<Fact>]
let ``the page box maps exactly onto the rotated view box`` () =
    check (Prop.forAll (Arb.fromGen (Gen.zip3 genRotation genSize genScale)) (fun (r, s, scale) ->
        let v = Affine.applyRect (Geometry.displayToView r s scale) { X = 0.0; Y = 0.0; W = s.W; H = s.H }
        let rs = Geometry.rotateSize r s
        abs v.X < 1e-6 && abs v.Y < 1e-6 && abs (v.W - rs.W * scale) < 1e-6 && abs (v.H - rs.H * scale) < 1e-6))

[<Fact>]
let ``rotations are clockwise: the display top-left corner moves to the expected view corner`` () =
    let s = { W = 100.0; H = 200.0 }
    let corner r = Affine.apply (Geometry.displayToView r s 1.0) { X = 0.0; Y = 0.0 }
    Assert.Equal({ X = 0.0; Y = 0.0 }, corner R0)
    Assert.Equal({ X = 200.0; Y = 0.0 }, corner R90) // top-left -> top-right
    Assert.Equal({ X = 100.0; Y = 200.0 }, corner R180) // -> bottom-right
    Assert.Equal({ X = 0.0; Y = 100.0 }, corner R270) // -> bottom-left

[<Fact>]
let ``four quarter turns are the identity`` () =
    check (Prop.forAll (Arb.fromGen genRotation) (fun r ->
        r |> Rotation.clockwise |> Rotation.clockwise |> Rotation.clockwise |> Rotation.clockwise = r
        && Rotation.counterClockwise (Rotation.clockwise r) = r))

[<Fact>]
let ``affine inverse composes to identity`` () =
    let m = { A = 2.0; B = 0.5; C = -1.0; D = 3.0; E = 10.0; F = -7.0 }
    let inv = (Affine.invert m).Value
    let p = { X = 3.3; Y = -4.4 }
    Assert.True(near 1e-9 p (Affine.apply inv (Affine.apply m p)))
    Assert.True((Affine.invert { m with A = 0.0; B = 0.0 }).IsNone)

[<Fact>]
let ``device page size is never zero`` () =
    Assert.Equal((1, 1), Geometry.devicePageSize R0 { W = 0.0; H = 0.0 } 1.0)
    Assert.Equal((792, 612), Geometry.devicePageSize R90 { W = 612.0; H = 792.0 } 1.0)

// ---- layout --------------------------------------------------------------------------------

let private genPages = Gen.listOfLength 40 genSize |> Gen.map Array.ofList
let private genParams =
    Gen.map2 (fun z r -> { Layout.defaultParams with Zoom = z; Rotation = r }) (Gen.choose (10, 400) |> Gen.map (fun v -> float v / 100.0)) genRotation

[<Fact>]
let ``slots are stacked without overlap and inside the extent`` () =
    check (Prop.forAll (Arb.fromGen (Gen.zip genPages genParams)) (fun (pages, p) ->
        let l = Layout.compute pages p
        let ok = ResizeArray<bool>()
        for i in 0 .. l.Slots.Length - 1 do
            let b = l.Slots[i].Bounds
            ok.Add(b.X >= 0.0 && b.Right <= l.Extent.W + 1e-6 && b.Bottom <= l.Extent.H + 1e-6)
            if i > 0 then ok.Add(b.Y >= l.Slots[i - 1].Bounds.Bottom + p.Gap - 1e-6)
        Seq.forall id ok))

[<Fact>]
let ``visibleRange agrees with a brute-force scan`` () =
    let gen = Gen.zip3 genPages genParams (Gen.zip genUnit genUnit)
    check (Prop.forAll (Arb.fromGen gen) (fun (pages, p, (u, v)) ->
        let l = Layout.compute pages p
        let top = u * l.Extent.H
        let height = 1.0 + v * 2000.0
        let brute =
            l.Slots
            |> Array.filter (fun s -> s.Bounds.Bottom >= top && s.Bounds.Y <= top + height)
            |> Array.map (fun s -> s.Index)
        match Layout.visibleRange l top height with
        | None -> brute.Length = 0
        | Some(a, b) -> brute = [| a..b |]))

[<Fact>]
let ``anchorAt and canvasOf round-trip on any page point, across zoom and rotation changes`` () =
    let gen = Gen.zip3 genPages (Gen.zip genParams genParams) (Gen.zip3 (Gen.choose (0, 39)) genUnit genUnit)
    check (Prop.forAll (Arb.fromGen gen) (fun (pages, (p1, p2), (page, u, v)) ->
        let a: Layout.Anchor = { Page = page; Local = { X = u * pages[page].W; Y = v * pages[page].H } }
        let l1 = Layout.compute pages p1
        let l2 = Layout.compute pages p2
        let back1 = Layout.anchorAt l1 (Layout.canvasOf l1 a)
        let back2 = Layout.anchorAt l2 (Layout.canvasOf l2 a)
        let same (b: Layout.Anchor option) = b.IsSome && b.Value.Page = page && near 1e-6 b.Value.Local a.Local
        same back1 && same back2))

[<Fact>]
let ``clampOffset keeps the viewport inside the document or centres it`` () =
    let l = Layout.compute [| { W = 612.0; H = 792.0 } |] Layout.defaultParams
    let vp = { W = 2000.0; H = 300.0 }
    let o = Layout.clampOffset l vp { X = 500.0; Y = -50.0 }
    Assert.True(o.X < 0.0, "narrow document is centred with a negative x offset")
    Assert.Equal(0.0, o.Y)
    let o2 = Layout.clampOffset l vp { X = 0.0; Y = 1.0e9 }
    Assert.Equal(l.Extent.H - vp.H, o2.Y)

[<Fact>]
let ``fit width makes the widest page fill the viewport`` () =
    let pages = [| { W = 612.0; H = 792.0 }; { W = 1224.0; H = 792.0 } |]
    let vp = { W = 1000.0; H = 800.0 }
    let z = Layout.fitWidthZoom pages Layout.defaultParams vp
    let l = Layout.compute pages { Layout.defaultParams with Zoom = z }
    Assert.Equal(vp.W, l.Extent.W, 6)

[<Fact>]
let ``fit page makes the current page fit in both directions`` () =
    let pages = [| { W = 612.0; H = 792.0 } |]
    let vp = { W = 1000.0; H = 500.0 }
    let z = Layout.fitPageZoom pages 0 Layout.defaultParams vp
    let l = Layout.compute pages { Layout.defaultParams with Zoom = z }
    let b = l.Slots[0].Bounds
    Assert.True(b.H <= vp.H - 2.0 * Layout.defaultParams.Margin + 1e-6 && b.W <= vp.W)

[<Fact>]
let ``pageAtY picks the closer page inside a gap`` () =
    let l = Layout.compute [| { W = 100.0; H = 100.0 }; { W = 100.0; H = 100.0 } |] { Layout.defaultParams with Zoom = 0.75; Gap = 100.0 }
    let gapTop = l.Slots[0].Bounds.Bottom
    Assert.Equal(0, Layout.pageAtY l (gapTop + 10.0))
    Assert.Equal(1, Layout.pageAtY l (gapTop + 90.0))
    Assert.Equal(1, Layout.pageAtY l 1.0e9)
    Assert.Equal(0, Layout.pageAtY l -50.0)
