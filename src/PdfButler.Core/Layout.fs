/// Continuous vertical layout of pages in canvas space (DIPs).
module PdfButler.Core.Layout

open System

type Params =
    { Zoom: float
      Rotation: Rotation
      /// Vertical space between pages, DIPs.
      Gap: float
      /// Space around the whole strip, DIPs.
      Margin: float }

let defaultParams =
    { Zoom = 1.0
      Rotation = R0
      Gap = 8.0
      Margin = 8.0 }

type Slot = { Index: int; Bounds: Rect }

type DocLayout =
    { Slots: Slot[]
      Extent: Size
      Params: Params
      /// Page sizes in display points (before view rotation).
      PageSizes: Size[] }

let compute (pages: Size[]) (p: Params) : DocLayout =
    let dpp = Geometry.dipsPerPoint p.Zoom

    let sizes =
        pages
        |> Array.map (fun s ->
            let r = Geometry.rotateSize p.Rotation s
            { W = max 1.0 (r.W * dpp); H = max 1.0 (r.H * dpp) })

    let maxW = if sizes.Length = 0 then 0.0 else sizes |> Array.map (fun s -> s.W) |> Array.max
    let slots = Array.zeroCreate sizes.Length
    let mutable y = p.Margin

    for i in 0 .. sizes.Length - 1 do
        let s = sizes[i]
        slots[i] <- { Index = i; Bounds = { X = p.Margin + (maxW - s.W) / 2.0; Y = y; W = s.W; H = s.H } }
        y <- y + s.H + p.Gap

    let height = if sizes.Length = 0 then 2.0 * p.Margin else y - p.Gap + p.Margin

    { Slots = slots
      Extent = { W = maxW + 2.0 * p.Margin; H = height }
      Params = p
      PageSizes = pages }

/// First slot whose bottom edge is at or below `y`.
let private firstEndingAfter (l: DocLayout) (y: float) =
    let mutable lo = 0
    let mutable hi = l.Slots.Length - 1
    let mutable ans = l.Slots.Length

    while lo <= hi do
        let mid = (lo + hi) / 2

        if l.Slots[mid].Bounds.Bottom >= y then
            ans <- mid
            hi <- mid - 1
        else
            lo <- mid + 1

    ans

/// Inclusive range of pages intersecting the vertical band [top, top + height].
let visibleRange (l: DocLayout) (top: float) (height: float) : (int * int) option =
    if l.Slots.Length = 0 then
        None
    else
        let first = firstEndingAfter l top

        if first >= l.Slots.Length then
            None
        else
            let bottom = top + height
            let mutable last = first - 1
            let mutable i = first

            while i < l.Slots.Length && l.Slots[i].Bounds.Y <= bottom do
                last <- i
                i <- i + 1

            if last < first then None else Some(first, last)

/// Page under (or nearest below/above) a canvas y coordinate.
let pageAtY (l: DocLayout) (y: float) : int =
    if l.Slots.Length = 0 then 0
    else
        let i = firstEndingAfter l y
        if i >= l.Slots.Length then l.Slots.Length - 1
        elif i > 0 && y < l.Slots[i].Bounds.Y then
            // In the gap: pick whichever page edge is closer.
            if y - l.Slots[i - 1].Bounds.Bottom < l.Slots[i].Bounds.Y - y then i - 1 else i
        else i

/// The page occupying the most of the viewport; ties go to the earlier page.
let currentPage (l: DocLayout) (offset: Point) (viewport: Size) : int =
    match visibleRange l offset.Y viewport.H with
    | None -> pageAtY l offset.Y
    | Some(a, b) ->
        let mutable best = a
        let mutable bestArea = -1.0

        for i in a..b do
            let s = l.Slots[i].Bounds
            let visible = (min s.Bottom (offset.Y + viewport.H)) - (max s.Y offset.Y)

            if visible > bestArea + 0.5 then
                best <- i
                bestArea <- visible

        best

/// A position pinned to a page in display points; stable across zoom and rotation changes.
type Anchor = { Page: int; Local: Point }

let private scaleOf (l: DocLayout) = Geometry.dipsPerPoint l.Params.Zoom

let anchorAt (l: DocLayout) (canvas: Point) : Anchor option =
    if l.Slots.Length = 0 then
        None
    else
        let page = pageAtY l canvas.Y
        let slot = l.Slots[page]
        let size = l.PageSizes[page]
        let m = Geometry.viewToDisplay l.Params.Rotation size (scaleOf l)
        let local = Affine.apply m { X = canvas.X - slot.Bounds.X; Y = canvas.Y - slot.Bounds.Y }
        let clamp lo hi v = max lo (min hi v)

        Some
            { Page = page
              Local = { X = clamp 0.0 size.W local.X; Y = clamp 0.0 size.H local.Y } }

let canvasOf (l: DocLayout) (a: Anchor) : Point =
    if l.Slots.Length = 0 then
        { X = 0.0; Y = 0.0 }
    else
        let page = max 0 (min (l.Slots.Length - 1) a.Page)
        let slot = l.Slots[page]
        let m = Geometry.displayToView l.Params.Rotation l.PageSizes[page] (scaleOf l)
        let v = Affine.apply m a.Local
        { X = slot.Bounds.X + v.X; Y = slot.Bounds.Y + v.Y }

/// Converts a display-space rectangle on `page` to canvas space.
let canvasRect (l: DocLayout) (page: int) (r: Rect) : Rect =
    let slot = l.Slots[page]
    let m = Geometry.displayToView l.Params.Rotation l.PageSizes[page] (scaleOf l)
    Affine.applyRect m r |> Rect.offset slot.Bounds.X slot.Bounds.Y

let private clampAxis extent view v =
    if extent <= view then (extent - view) / 2.0 else max 0.0 (min (extent - view) v)

/// Clamp a scroll offset. When the document is smaller than the viewport on an axis
/// the offset becomes negative so that the content is centred.
let clampOffset (l: DocLayout) (viewport: Size) (o: Point) : Point =
    { X = clampAxis l.Extent.W viewport.W o.X
      Y = clampAxis l.Extent.H viewport.H o.Y }

let minZoom = 0.05
let maxZoom = 32.0
let clampZoom z = if Double.IsNaN z then 1.0 else max minZoom (min maxZoom z)

let private maxRotatedWidth (pages: Size[]) r =
    if pages.Length = 0 then 1.0
    else pages |> Array.map (fun s -> (Geometry.rotateSize r s).W) |> Array.max |> max 1.0

let fitWidthZoom (pages: Size[]) (p: Params) (viewport: Size) =
    let avail = max 1.0 (viewport.W - 2.0 * p.Margin)
    clampZoom (avail / (Geometry.dipsPerPoint 1.0 * maxRotatedWidth pages p.Rotation))

let fitPageZoom (pages: Size[]) (page: int) (p: Params) (viewport: Size) =
    if pages.Length = 0 then 1.0
    else
        let s = Geometry.rotateSize p.Rotation pages[max 0 (min (pages.Length - 1) page)]
        let availW = max 1.0 (viewport.W - 2.0 * p.Margin)
        let availH = max 1.0 (viewport.H - 2.0 * p.Margin)
        let d = Geometry.dipsPerPoint 1.0
        clampZoom (min (availW / (d * max 1.0 s.W)) (availH / (d * max 1.0 s.H)))
