/// Pure view state for the continuous page view: zoom, rotation, scrolling and navigation.
/// The UI sends `Msg`s and renders whatever `State` results; nothing here touches the engine.
module PdfButler.Core.Viewer

type ZoomMode =
    | FitWidth
    | FitPage
    | Manual

type State =
    { Pages: Size[]
      Zoom: float
      Mode: ZoomMode
      Rotation: Rotation
      /// Scroll offset of the viewport's top-left corner in canvas space.
      Offset: Point
      Viewport: Size
      History: History.State
      Layout: Layout.DocLayout }

type Msg =
    | ViewportResized of Size
    | ScrollBy of dx: float * dy: float
    | ScrollTo of Point
    /// Zoom keeping the content under `anchor` (viewport coordinates) fixed. None = viewport centre.
    | ZoomTo of zoom: float * anchor: Point option
    | ZoomStep of steps: int * anchor: Point option
    | ZoomByFactor of factor: float * anchor: Point option
    | SetMode of ZoomMode
    | RotateClockwise
    | RotateCounterClockwise
    | GoToPage of int
    /// Show a page without recording history (restoring a previous session).
    | ShowPage of int
    | GoToDestination of DisplayDestination
    | NextPage
    | PreviousPage
    | FirstPage
    | LastPage
    | PageDown
    | PageUp
    | Back
    | Forward
    /// Scroll just enough to show a display-space rectangle on a page (search hits). No history.
    | Reveal of page: int * rect: Rect

/// Preset zoom levels used by zoom in / zoom out.
let zoomSteps =
    [| 0.1; 0.25; 0.33; 0.5; 0.67; 0.75; 0.8; 0.9; 1.0; 1.1; 1.25; 1.5; 1.75; 2.0; 2.5; 3.0; 4.0; 5.0; 6.4; 8.0; 12.0; 16.0; 24.0; 32.0 |]

let private relayout (s: State) zoom rotation =
    Layout.compute s.Pages { s.Layout.Params with Zoom = zoom; Rotation = rotation }

let private clamp (s: State) = { s with Offset = Layout.clampOffset s.Layout s.Viewport s.Offset }

let create (pages: Size[]) (viewport: Size) : State =
    let p = Layout.defaultParams
    let zoom = 1.0
    let layout = Layout.compute pages { p with Zoom = zoom }

    clamp
        { Pages = pages
          Zoom = zoom
          Mode = Manual
          Rotation = R0
          Offset = { X = 0.0; Y = 0.0 }
          Viewport = viewport
          History = History.empty
          Layout = layout }

let currentPage (s: State) = Layout.currentPage s.Layout s.Offset s.Viewport

/// The reading position at the top-left of the viewport.
let location (s: State) : History.Location =
    match Layout.anchorAt s.Layout { X = s.Offset.X; Y = max 0.0 s.Offset.Y } with
    | Some a -> { Page = a.Page; Top = a.Local.Y }
    | None -> { Page = 0; Top = 0.0 }

/// Re-layout at a new zoom/rotation keeping `anchorView` (viewport coordinates) stable.
let private rezoom (s: State) (zoom: float) (rotation: Rotation) (anchorView: Point option) =
    let anchorView = defaultArg anchorView { X = s.Viewport.W / 2.0; Y = s.Viewport.H / 2.0 }
    let canvasPt = { X = s.Offset.X + anchorView.X; Y = s.Offset.Y + anchorView.Y }
    let anchor = Layout.anchorAt s.Layout canvasPt
    let layout = relayout s zoom rotation

    let offset =
        match anchor with
        | Some a ->
            let c = Layout.canvasOf layout a
            { X = c.X - anchorView.X; Y = c.Y - anchorView.Y }
        | None -> s.Offset

    clamp { s with Zoom = zoom; Rotation = rotation; Layout = layout; Offset = offset }

let private fitZoom (s: State) mode rotation =
    let p = { s.Layout.Params with Rotation = rotation }

    match mode with
    | FitWidth -> Some(Layout.fitWidthZoom s.Pages p s.Viewport)
    | FitPage -> Some(Layout.fitPageZoom s.Pages (currentPage s) p s.Viewport)
    | Manual -> None

let private scrollToPageTop (s: State) (page: int) =
    if s.Pages.Length = 0 then s
    else
        let page = max 0 (min (s.Pages.Length - 1) page)
        let slot = s.Layout.Slots[page]
        let top = slot.Bounds.Y - s.Layout.Params.Gap / 2.0

        let s =
            match s.Mode with
            | FitPage -> { s with Offset = { s.Offset with Y = slot.Bounds.Y - (s.Viewport.H - slot.Bounds.H) / 2.0 } }
            | _ -> { s with Offset = { s.Offset with Y = top } }

        clamp s

let private goToLocation (s: State) (loc: History.Location) =
    if s.Pages.Length = 0 then s
    else
        let page = max 0 (min (s.Pages.Length - 1) loc.Page)
        let c = Layout.canvasOf s.Layout { Page = page; Local = { X = 0.0; Y = loc.Top } }
        clamp { s with Offset = { s.Offset with Y = c.Y } }

let private withHistory (s: State) (f: State -> State) =
    let before = location s
    let after = f s
    { after with History = History.record before s.History }

let rec update (msg: Msg) (s: State) : State =
    match msg with
    | ViewportResized size ->
        let size = { W = max 1.0 size.W; H = max 1.0 size.H }
        let s' = { s with Viewport = size }

        match fitZoom s' s.Mode s.Rotation with
        | Some z ->
            // Keep the top-left reading position while refitting.
            let loc = location s
            let layout = relayout s' z s.Rotation
            goToLocation { s' with Zoom = z; Layout = layout } loc
        | None -> clamp s'

    | ScrollBy(dx, dy) -> clamp { s with Offset = { X = s.Offset.X + dx; Y = s.Offset.Y + dy } }
    | ScrollTo p -> clamp { s with Offset = p }

    | ZoomTo(z, anchor) -> { rezoom s (Layout.clampZoom z) s.Rotation anchor with Mode = Manual }
    | ZoomByFactor(f, anchor) -> update (ZoomTo(s.Zoom * f, anchor)) s
    | ZoomStep(steps, anchor) ->
        let z =
            if steps > 0 then
                zoomSteps |> Array.tryFind (fun z -> z > s.Zoom * 1.001) |> Option.defaultValue Layout.maxZoom
            elif steps < 0 then
                zoomSteps |> Array.tryFindBack (fun z -> z < s.Zoom * 0.999) |> Option.defaultValue Layout.minZoom
            else
                s.Zoom

        let s' = update (ZoomTo(z, anchor)) s
        if abs steps > 1 then update (ZoomStep(steps - sign steps, anchor)) s' else s'

    | SetMode Manual -> { s with Mode = Manual }
    | SetMode mode ->
        let page = currentPage s
        let z = defaultArg (fitZoom s mode s.Rotation) s.Zoom
        let s' = { rezoom s z s.Rotation None with Mode = mode }
        if mode = FitPage then scrollToPageTop s' page else s'

    | RotateClockwise
    | RotateCounterClockwise ->
        let r =
            if msg = RotateClockwise then Rotation.clockwise s.Rotation
            else Rotation.counterClockwise s.Rotation

        let z = defaultArg (fitZoom s s.Mode r) s.Zoom
        rezoom s z r None

    | GoToPage n -> withHistory s (fun s -> scrollToPageTop s n)
    | ShowPage n -> scrollToPageTop s n
    | GoToDestination d ->
        withHistory s (fun s ->
            if s.Pages.Length = 0 then s
            else
                let page = max 0 (min (s.Pages.Length - 1) d.Page)

                match d.Top, d.Left with
                | None, None -> scrollToPageTop s page
                | top, left ->
                    let local = { X = defaultArg left 0.0; Y = defaultArg top 0.0 }
                    let c = Layout.canvasOf s.Layout { Page = page; Local = local }
                    let x = if left.IsSome then c.X - 8.0 else s.Offset.X
                    let y = if top.IsSome then c.Y - 8.0 else s.Layout.Slots[page].Bounds.Y
                    clamp { s with Offset = { X = x; Y = y } })

    | NextPage -> scrollToPageTop s (currentPage s + 1)
    | PreviousPage ->
        // If the current page's top is above the viewport, go to its top first.
        let cur = currentPage s
        let slot = if s.Pages.Length > 0 then Some s.Layout.Slots[cur] else None

        match slot with
        | Some slot when slot.Bounds.Y < s.Offset.Y - 1.0 && s.Mode <> FitPage -> scrollToPageTop s cur
        | _ -> scrollToPageTop s (cur - 1)
    | FirstPage -> withHistory s (fun s -> clamp { s with Offset = { s.Offset with Y = 0.0 } })
    | LastPage -> withHistory s (fun s -> clamp { s with Offset = { s.Offset with Y = s.Layout.Extent.H } })
    | PageDown -> clamp { s with Offset = { s.Offset with Y = s.Offset.Y + max 1.0 (s.Viewport.H - 40.0) } }
    | PageUp -> clamp { s with Offset = { s.Offset with Y = s.Offset.Y - max 1.0 (s.Viewport.H - 40.0) } }

    | Back ->
        match History.back (location s) s.History with
        | Some(loc, h) -> { goToLocation s loc with History = h }
        | None -> s
    | Forward ->
        match History.forward (location s) s.History with
        | Some(loc, h) -> { goToLocation s loc with History = h }
        | None -> s

    | Reveal(page, rect) ->
        if page < 0 || page >= s.Pages.Length then s
        else
            let r = Layout.canvasRect s.Layout page rect
            let margin = 40.0
            let o = s.Offset

            let y =
                if r.Y >= o.Y + margin && r.Bottom <= o.Y + s.Viewport.H - margin then o.Y
                else r.Y - s.Viewport.H / 3.0

            let x =
                if r.X >= o.X && r.Right <= o.X + s.Viewport.W then o.X
                else r.X - s.Viewport.W / 3.0

            clamp { s with Offset = { X = x; Y = y } }
