/// The continuous page view: draws pages, handles scrolling/zoom/selection/links, runs search.
/// All view state transitions go through the pure `Viewer.update`; this file is the effect runner.
module PdfButler.App.PdfView

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Styling
open Avalonia.Threading
open PdfButler.Core
open PdfButler.Pdfium
open PdfButler.App.RendererProcess
open PdfButler.App.PageRenderer

type CorePoint = PdfButler.Core.Point
type CoreRect = PdfButler.Core.Rect
type CoreSize = PdfButler.Core.Size

let private toAva (r: CoreRect) = Avalonia.Rect(r.X, r.Y, r.W, r.H)

/// Search progress for the UI.
type SearchInfo = { Status: Search.Status; Text: string }

type private Drag =
    | NoDrag
    | Selecting of anchor: TextLayer.TextPos
    | Panning of last: Avalonia.Point
    | PendingLink of link: Link * page: int * start: Avalonia.Point

/// The drawing surface. Not focus-stealing; keyboard is routed by the window.
type PageCanvas() as this =
    inherit Control()

    let mutable doc: RemoteDocument = null
    let mutable renderer: Renderer option = None
    let mutable state: Viewer.State = Viewer.create [||] { W = 800.0; H = 600.0 }
    let texts = Dictionary<int, PageText>()
    let textRequests = HashSet<int>()
    let links = Dictionary<int, Link[]>()
    let linkRequests = HashSet<int>()
    let mutable selection: TextLayer.Selection option = None
    let mutable drag = NoDrag
    let mutable search: Search.State option = None
    let mutable searchCts: CancellationTokenSource = null
    let mutable searchOptions = Search.defaultOptions
    let mutable updateQueued = false
    let mutable firstPresented = false
    let mutable initialContentReady = false
    let mutable active = true
    /// Temporary pan tool (Space held): left-drag pans everywhere, ignoring text and links.
    let mutable handMode = false

    let stateChanged = Event<unit>()
    let panStarted = Event<unit>()
    let linkActivated = Event<LinkTarget>()
    let searchChanged = Event<SearchInfo>()
    let selectionChanged = Event<unit>()
    let initialContentReadyEvent = Event<unit>()

    let renderScaling () =
        match TopLevel.GetTopLevel this with
        | null -> 1.0
        | t -> t.RenderScaling

    let dpp () = Geometry.dipsPerPoint state.Zoom

    /// Page origin in viewport DIPs, snapped to device pixels so 1:1 bitmaps stay sharp.
    let pageOrigin (slot: Layout.Slot) =
        let rs = renderScaling ()
        let snap v = Math.Round(v * rs) / rs
        Avalonia.Point(snap (slot.Bounds.X - state.Offset.X), snap (slot.Bounds.Y - state.Offset.Y))

    let viewportToCanvas (p: Avalonia.Point) : CorePoint = { X = p.X + state.Offset.X; Y = p.Y + state.Offset.Y }

    /// Page and display-space point under a viewport position.
    let hitPage (p: Avalonia.Point) : (int * CorePoint) option =
        if state.Pages.Length = 0 then None
        else
            let c = viewportToCanvas p
            let page = Layout.pageAtY state.Layout c.Y
            let slot = state.Layout.Slots[page]
            if Rect.contains c (Rect.inflate 2.0 slot.Bounds) then
                let m = Geometry.viewToDisplay state.Rotation state.Pages[page] (dpp ())
                Some(page, Affine.apply m { X = c.X - slot.Bounds.X; Y = c.Y - slot.Bounds.Y })
            else None

    let rec requestText (page: int) (priority: Priority) =
        if not (isNull doc) && not (texts.ContainsKey page) && textRequests.Add page then
            let d = doc
            d.GetTextAsync(page, priority, CancellationToken.None).ContinueWith(fun (t: Task<PageText>) ->
                Dispatcher.UIThread.Post(fun () ->
                    textRequests.Remove page |> ignore
                    if obj.ReferenceEquals(d, doc) then
                        texts[page] <- (if t.IsCompletedSuccessfully then t.Result else { Page = page; Chars = [||] })
                        this.InvalidateVisual()))
            |> ignore

    let requestLinks (page: int) =
        if not (isNull doc) && not (links.ContainsKey page) && linkRequests.Add page then
            let d = doc
            d.GetLinksAsync(page, Priority.Prefetch, CancellationToken.None).ContinueWith(fun (t: Task<Link[]>) ->
                Dispatcher.UIThread.Post(fun () ->
                    linkRequests.Remove page |> ignore
                    if obj.ReferenceEquals(d, doc) then
                        links[page] <- (if t.IsCompletedSuccessfully then t.Result else [||])))
            |> ignore

    /// Decide which tiles/text/links are needed for the current view and ask for them.
    let updateRequests () =
        updateQueued <- false
        match renderer with
        | None -> ()
        | Some r when active ->
            let rs = renderScaling ()
            let scale = dpp () * rs
            let vp = state.Viewport
            let wanted = ResizeArray()
            match Layout.visibleRange state.Layout state.Offset.Y vp.H with
            | None -> ()
            | Some(a, b) ->
                for page in a..b do
                    let slot = state.Layout.Slots[page]
                    let o = pageOrigin slot
                    // Visible part of the page in page device pixels.
                    let vx0 = max 0.0 (-o.X) * rs
                    let vy0 = max 0.0 (-o.Y) * rs
                    let vx1 = (min slot.Bounds.W (vp.W - o.X)) * rs
                    let vy1 = (min slot.Bounds.H (vp.H - o.Y)) * rs
                    if vx1 > vx0 && vy1 > vy0 then
                        let visible = { X = int vx0; Y = int vy0; W = int (ceil (vx1 - vx0)) + 1; H = int (ceil (vy1 - vy0)) + 1 }
                        for (k, rect) in plan doc.Id page state.Rotation state.Pages[page] scale visible do
                            wanted.Add((k, rect, Priority.Interactive))
                    requestLinks page
                // Neighbours, whole page at current scale when small enough (no tiles prefetched).
                for page in [ a - 1; b + 1 ] do
                    if page >= 0 && page < state.Pages.Length then
                        match plan doc.Id page state.Rotation state.Pages[page] scale { X = 0; Y = 0; W = 1; H = 1 } with
                        | [ (k, rect) ] when k.TX < 0 -> wanted.Add((k, rect, Priority.Prefetch))
                        | _ -> ()
                // A cheap low-resolution version of visible pages, used as a fallback while zooming.
                let lowScale = min scale (300.0 / max 1.0 (state.Pages |> Array.map (fun s -> max s.W s.H) |> Array.max))
                if lowScale < scale * 0.5 then
                    for page in a..b do
                        match plan doc.Id page state.Rotation state.Pages[page] lowScale { X = 0; Y = 0; W = 1; H = 1 } with
                        | [ (k, rect) ] -> wanted.Add((k, rect, Priority.Prefetch))
                        | _ -> ()
            r.Update(List.ofSeq wanted)
        | Some _ -> ()

    let scheduleUpdate () =
        if not updateQueued then
            updateQueued <- true
            Dispatcher.UIThread.Post(updateRequests, DispatcherPriority.Background)

    let apply (msg: Viewer.Msg) =
        let before = state
        state <- Viewer.update msg state
        if state <> before then
            this.InvalidateVisual()
            scheduleUpdate ()
            stateChanged.Trigger()

    let searchInfo () =
        match search with
        | None -> { Status = Search.Idle; Text = "" }
        | Some s ->
            let st = Search.status s
            { Status = st; Text = Search.describe st }

    let revealHit (hit: Search.Hit) =
        match texts.TryGetValue hit.Page with
        | true, t ->
            match TextLayer.lineRects t.Chars hit.Start hit.End with
            | r :: _ -> apply (Viewer.Reveal(hit.Page, r))
            | [] -> apply (Viewer.GoToPage hit.Page)
        | _ -> apply (Viewer.GoToPage hit.Page)

    // ---- drawing --------------------------------------------------------------------

    let pageBrush = SolidColorBrush(Colors.White) :> IBrush
    let shadowBrush = SolidColorBrush(Color.FromArgb(60uy, 0uy, 0uy, 0uy)) :> IBrush
    let selectionBrush = SolidColorBrush(Color.FromArgb(90uy, 0uy, 120uy, 215uy)) :> IBrush
    let hitBrush = SolidColorBrush(Color.FromArgb(110uy, 255uy, 210uy, 0uy)) :> IBrush
    let currentHitBrush = SolidColorBrush(Color.FromArgb(150uy, 255uy, 120uy, 0uy)) :> IBrush
    let errorBrush = SolidColorBrush(Color.FromRgb(160uy, 40uy, 40uy)) :> IBrush

    let drawTile (ctx: DrawingContext) (origin: Avalonia.Point) (tile: Tile) =
        // Tile pixels -> DIPs at the *current* zoom (fallback tiles may be at another scale).
        let k = dpp () / tile.Scale
        let dest = Avalonia.Rect(origin.X + float tile.Rect.X * k, origin.Y + float tile.Rect.Y * k, float tile.Rect.W * k, float tile.Rect.H * k)
        ctx.DrawImage(tile.Bitmap, Avalonia.Rect(0.0, 0.0, float tile.Rect.W, float tile.Rect.H), dest)

    let drawHighlights (ctx: DrawingContext) (page: int) =
        match texts.TryGetValue page with
        | true, t when t.Chars.Length > 0 ->
            let toView (r: CoreRect) = Layout.canvasRect state.Layout page r |> Rect.offset -state.Offset.X -state.Offset.Y |> toAva
            match search with
            | Some s ->
                match s.Hits.TryFind page with
                | Some hits ->
                    for h in hits do
                        let brush = if Some h = s.Current then currentHitBrush else hitBrush
                        for r in TextLayer.lineRects t.Chars h.Start h.End do
                            ctx.FillRectangle(brush, toView r)
                | None -> ()
            | None -> ()
            match selection with
            | Some sel ->
                match TextLayer.Selection.rangeOnPage sel page t.Chars.Length with
                | Some(a, b) ->
                    for r in TextLayer.lineRects t.Chars a b do
                        ctx.FillRectangle(selectionBrush, toView r)
                | None -> ()
            | None -> ()
        | _ -> ()

    do
        this.ClipToBounds <- true
        this.Focusable <- true
        this.Cursor <- new Cursor(StandardCursorType.Arrow)
        this.ActualThemeVariantChanged.Add(fun _ -> this.InvalidateVisual())

    // ---- public API -------------------------------------------------------------------

    [<CLIEvent>]
    member _.StateChanged = stateChanged.Publish
    [<CLIEvent>]
    member _.LinkActivated = linkActivated.Publish
    [<CLIEvent>]
    member _.SearchChanged = searchChanged.Publish
    [<CLIEvent>]
    member _.SelectionChanged = selectionChanged.Publish
    /// First visible pixels or a page-error placeholder, not merely metadata being available.
    [<CLIEvent>]
    member _.InitialContentReady = initialContentReadyEvent.Publish

    /// A left-button pan drag started in hand mode.
    [<CLIEvent>]
    member _.PanStarted = panStarted.Publish

    /// Hand mode is set by the pane while Space is held. Turning it off does not end a drag in progress.
    member _.HandMode
        with get () = handMode
        and set (value: bool) =
            if handMode <> value then
                handMode <- value
                match drag with
                | Panning _ -> ()
                | _ -> this.Cursor <- new Cursor(if value then StandardCursorType.SizeAll else StandardCursorType.Arrow)

    member _.Document = doc
    member _.State = state
    member _.Selection = selection
    member _.SearchState = search
    member _.Renderer = renderer
    member val FirstPagePresented: (unit -> unit) = ignore with get, set

    member _.Dispatch(msg: Viewer.Msg) = apply msg

    member _.SetActive(value: bool) =
        active <- value
        renderer |> Option.iter (fun r -> r.SetSuspended(not value))
        if value then
            this.InvalidateVisual()
            scheduleUpdate ()

    /// Attach a newly opened document (the caller owns closing the previous one).
    member this.SetDocument(d: RemoteDocument) =
        this.ClearSearch()
        renderer |> Option.iter (fun r -> (r :> IDisposable).Dispose())
        texts.Clear(); textRequests.Clear(); links.Clear(); linkRequests.Clear()
        selection <- None
        doc <- d
        firstPresented <- false
        initialContentReady <- false
        if isNull d then
            renderer <- None
            state <- Viewer.create [||] state.Viewport
        else
            renderer <- Some(new Renderer(d, (fun () -> this.InvalidateVisual())))
            renderer.Value.SetSuspended(not active)
            let sizes = d.Info.Pages |> Array.map (fun p -> p.Size)
            state <- Viewer.create sizes state.Viewport
        this.InvalidateVisual()
        scheduleUpdate ()
        stateChanged.Trigger()

    member _.CurrentPage = if state.Pages.Length = 0 then 0 else Viewer.currentPage state

    /// Resolve and navigate to an internal destination (records history).
    member _.GoTo(dest: Destination) =
        if not (isNull doc) then
            let d = doc
            d.ResolveAsync(dest, CancellationToken.None).ContinueWith(fun (t: Task<DisplayDestination>) ->
                Dispatcher.UIThread.Post(fun () ->
                    if obj.ReferenceEquals(d, doc) then
                        if t.IsCompletedSuccessfully then apply (Viewer.GoToDestination t.Result)
                        else apply (Viewer.GoToPage dest.Page)))
            |> ignore

    // ---- selection & copy ----------------------------------------------------------------

    member _.ClearSelection() =
        if selection.IsSome then
            selection <- None
            this.InvalidateVisual()
            selectionChanged.Trigger()

    member _.HasSelection = selection.IsSome

    /// Selected text, loading any page text that is not cached yet.
    member _.GetSelectedTextAsync() : Task<string> =
        match selection, doc with
        | Some sel, d when not (isNull d) ->
            task {
                let pagesText = Dictionary<int, PageText>()
                for p in TextLayer.Selection.pages sel do
                    match texts.TryGetValue p with
                    | true, t -> pagesText[p] <- t
                    | _ ->
                        let! t = d.GetTextAsync(p, Priority.Interactive, CancellationToken.None)
                        pagesText[p] <- t
                return TextLayer.Selection.text sel (fun p -> pagesText[p])
            }
        | _ -> Task.FromResult ""

    // ---- search ------------------------------------------------------------------------

    member _.ClearSearch() =
        if not (isNull searchCts) then
            searchCts.Cancel()
            searchCts <- null
        if search.IsSome then
            search <- None
            this.InvalidateVisual()
            searchChanged.Trigger(searchInfo ())

    /// Start an incremental search from the current page, page by page at background priority.
    member this.StartSearch(query: string, matchCase: bool) =
        this.ClearSearch()
        searchOptions <- { MatchCase = matchCase }
        if not (isNull doc) && not (String.IsNullOrWhiteSpace query) then
            let d = doc
            let n = d.Info.PageCount
            let startPage = this.CurrentPage
            let cts = new CancellationTokenSource()
            searchCts <- cts
            search <- Some(Search.start query n)
            searchChanged.Trigger(searchInfo ())
            let maxHits = 10_000
            let run () : Task =
                task {
                    let mutable i = 0
                    while i < n && not cts.IsCancellationRequested do
                        let page = (startPage + i) % n
                        let! result =
                            task {
                                try
                                    let! t = d.GetTextAsync(page, Priority.Background, cts.Token)
                                    return Some t
                                with
                                | :? OperationCanceledException -> return None
                                | _ -> return Some { Page = page; Chars = [||] }
                            }
                        match result with
                        | Some t when not cts.IsCancellationRequested ->
                            let hits = Search.findInPage searchOptions query t
                            do! Dispatcher.UIThread.InvokeAsync(fun () ->
                                    if obj.ReferenceEquals(searchCts, cts) then
                                        texts[page] <- t
                                        let s = search.Value
                                        let hadCurrent = s.Current.IsSome
                                        let s' = Search.addPage page hits (t.Chars.Length > 0) s
                                        search <- Some s'
                                        if not hadCurrent && s'.Current.IsSome then revealHit s'.Current.Value
                                        if hits.Length > 0 then this.InvalidateVisual()
                                        searchChanged.Trigger(searchInfo ())
                                        if Search.total s' >= maxHits then cts.Cancel()).GetTask()
                        | _ -> ()
                        i <- i + 1
                }
            run () |> ignore

    member _.SearchInfo = searchInfo ()

    member _.NextHit(forward: bool) =
        match search with
        | Some s ->
            let s' = if forward then Search.next this.CurrentPage s else Search.previous this.CurrentPage s
            search <- Some s'
            s'.Current |> Option.iter revealHit
            this.InvalidateVisual()
            searchChanged.Trigger(searchInfo ())
        | None -> ()

    // ---- layout & rendering -------------------------------------------------------------

    override _.OnSizeChanged(e) =
        base.OnSizeChanged e
        apply (Viewer.ViewportResized { W = e.NewSize.Width; H = e.NewSize.Height })

    override this.Render(ctx: DrawingContext) =
        let bg =
            if this.ActualThemeVariant = ThemeVariant.Dark then Color.FromRgb(43uy, 43uy, 43uy)
            else Color.FromRgb(229uy, 229uy, 229uy)
        ctx.FillRectangle(SolidColorBrush bg, Avalonia.Rect(this.Bounds.Size))
        match renderer with
        | None -> ()
        | Some r ->
            let rs = renderScaling ()
            let scale = dpp () * rs
            let sk = scaleKey scale
            let mutable anyExact = false
            let mutable anyContent = false
            match Layout.visibleRange state.Layout state.Offset.Y state.Viewport.H with
            | None -> ()
            | Some(a, b) ->
                for page in a..b do
                    let slot = state.Layout.Slots[page]
                    let o = pageOrigin slot
                    let pageRect = Avalonia.Rect(o.X, o.Y, slot.Bounds.W, slot.Bounds.H)
                    ctx.FillRectangle(shadowBrush, pageRect.Translate(Vector(2.0, 2.0)))
                    ctx.FillRectangle(pageBrush, pageRect)
                    use _clip = ctx.PushClip pageRect
                    for t in r.Fallback(page, state.Rotation, sk) do
                        drawTile ctx o t
                        anyContent <- true
                    // Exact tiles for the visible region.
                    let vis = { X = 0; Y = 0; W = Int32.MaxValue / 2; H = Int32.MaxValue / 2 }
                    let vx0 = max 0.0 (-o.X) * rs
                    let vy0 = max 0.0 (-o.Y) * rs
                    let visible = { vis with X = int vx0; Y = int vy0; W = int (state.Viewport.W * rs) + 2; H = int (state.Viewport.H * rs) + 2 }
                    for (k, _) in plan doc.Id page state.Rotation state.Pages[page] scale visible do
                        match r.TryGet k with
                        | Some t ->
                            drawTile ctx o t
                            anyExact <- true
                            anyContent <- true
                        | None -> ()
                    match r.PageError page with
                    | Some msg ->
                        anyContent <- true
                        let ft = FormattedText(msg, Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 14.0, errorBrush)
                        ctx.DrawText(ft, Avalonia.Point(o.X + 16.0, o.Y + 16.0))
                    | None -> ()
                    drawHighlights ctx page
            if anyContent && not initialContentReady then
                initialContentReady <- true
                let displayed = doc
                // Change the loading overlay after this frame, not inside the render pass.
                Dispatcher.UIThread.Post(fun () ->
                    if obj.ReferenceEquals(displayed, doc) then initialContentReadyEvent.Trigger())
            if anyExact && not firstPresented then
                firstPresented <- true
                Diagnostics.mark "first-page-presented"
                this.FirstPagePresented()

    // ---- input ----------------------------------------------------------------------------

    override _.OnPointerWheelChanged(e) =
        base.OnPointerWheelChanged e
        let d = e.Delta
        if e.KeyModifiers.HasFlag KeyModifiers.Control then
            let p = e.GetPosition this
            apply (Viewer.ZoomByFactor(Math.Pow(1.2, d.Y), Some { X = p.X; Y = p.Y }))
        elif e.KeyModifiers.HasFlag KeyModifiers.Shift then
            apply (Viewer.ScrollBy(-d.Y * 60.0, 0.0))
        else
            apply (Viewer.ScrollBy(-d.X * 60.0, -d.Y * 60.0))
        e.Handled <- true

    override this.OnPointerPressed(e) =
        base.OnPointerPressed e
        this.Focus() |> ignore
        let p = e.GetPosition this
        let props = e.GetCurrentPoint(this).Properties
        let startPan () =
            drag <- Panning p
            this.Cursor <- new Cursor(StandardCursorType.SizeAll)
            e.Pointer.Capture this
        if props.IsMiddleButtonPressed then
            startPan ()
        elif props.IsLeftButtonPressed && handMode then
            startPan ()
            panStarted.Trigger()
        elif props.IsLeftButtonPressed then
            match hitPage p with
            | None ->
                this.ClearSelection()
                drag <- Panning p
                e.Pointer.Capture this
            | Some(page, pt) ->
                let link =
                    match links.TryGetValue page with
                    | true, ls -> ls |> Array.tryFind (fun l -> Rect.contains pt (Rect.inflate 1.0 l.Bounds))
                    | _ -> None
                match link with
                | Some l -> drag <- PendingLink(l, page, p)
                | None ->
                    match texts.TryGetValue page with
                    | true, t when t.Chars.Length > 0 ->
                        match TextLayer.nearestChar t pt with
                        | Some i ->
                            if e.ClickCount >= 2 then
                                let a, b = TextLayer.wordAt t.Chars i
                                selection <- Some { Anchor = { Page = page; Index = a }; Focus = { Page = page; Index = b } }
                                drag <- NoDrag
                            else
                                selection <- None
                                drag <- Selecting { Page = page; Index = i }
                            this.InvalidateVisual()
                            selectionChanged.Trigger()
                        | None -> drag <- Panning p
                    | true, _ ->
                        this.ClearSelection()
                        drag <- Panning p // no text layer on this page: drag pans
                    | _ ->
                        requestText page Priority.Interactive
                        this.ClearSelection()
                        drag <- NoDrag
            e.Pointer.Capture this
        e.Handled <- true

    override this.OnPointerMoved(e) =
        base.OnPointerMoved e
        let p = e.GetPosition this
        match drag with
        | Panning last ->
            apply (Viewer.ScrollBy(last.X - p.X, last.Y - p.Y))
            drag <- Panning p
        | Selecting anchor ->
            // Auto-scroll when dragging past the edges.
            if p.Y < 0.0 then apply (Viewer.ScrollBy(0.0, p.Y / 2.0))
            elif p.Y > state.Viewport.H then apply (Viewer.ScrollBy(0.0, (p.Y - state.Viewport.H) / 2.0))
            match hitPage (Avalonia.Point(p.X, max 0.0 (min state.Viewport.H p.Y))) with
            | Some(page, pt) ->
                match texts.TryGetValue page with
                | true, t ->
                    match TextLayer.nearestChar t pt with
                    | Some i ->
                        selection <- Some { Anchor = anchor; Focus = { Page = page; Index = i } }
                        this.InvalidateVisual()
                    | None -> ()
                | _ -> requestText page Priority.Interactive
            | None -> ()
        | PendingLink(_, _, start) when abs (p.X - start.X) + abs (p.Y - start.Y) > 6.0 -> drag <- NoDrag
        | _ when handMode -> this.Cursor <- new Cursor(StandardCursorType.SizeAll)
        | _ ->
            // Hover feedback.
            let cursor =
                match hitPage p with
                | Some(page, pt) ->
                    let overLink =
                        match links.TryGetValue page with
                        | true, ls -> ls |> Array.exists (fun l -> Rect.contains pt (Rect.inflate 1.0 l.Bounds))
                        | _ -> false
                    if overLink then StandardCursorType.Hand
                    else
                        match texts.TryGetValue page with
                        | true, t when (TextLayer.charAt t 2.0 pt).IsSome -> StandardCursorType.Ibeam
                        | true, _ -> StandardCursorType.Arrow
                        | _ ->
                            requestText page Priority.Prefetch
                            StandardCursorType.Arrow
                | None -> StandardCursorType.Arrow
            this.Cursor <- new Cursor(cursor)

    override this.OnPointerReleased(e) =
        base.OnPointerReleased e
        match drag with
        | PendingLink(link, _, _) -> linkActivated.Trigger link.Target
        | Selecting _ -> selectionChanged.Trigger()
        | Panning _ -> this.Cursor <- new Cursor(if handMode then StandardCursorType.SizeAll else StandardCursorType.Arrow)
        | _ -> ()
        drag <- NoDrag
        e.Pointer.Capture null

/// Page canvas plus scroll bars.
type DocumentView() as this =
    inherit Grid()

    let canvas = PageCanvas()
    let vbar = ScrollBar(Orientation = Orientation.Vertical, AllowAutoHide = false)
    let hbar = ScrollBar(Orientation = Orientation.Horizontal, AllowAutoHide = false)
    let mutable syncing = false

    let sync () =
        syncing <- true
        let s = canvas.State
        let ext = s.Layout.Extent
        vbar.Maximum <- max 0.0 (ext.H - s.Viewport.H)
        vbar.ViewportSize <- s.Viewport.H
        vbar.Value <- max 0.0 s.Offset.Y
        vbar.LargeChange <- s.Viewport.H * 0.9
        vbar.SmallChange <- 60.0
        vbar.IsVisible <- ext.H > s.Viewport.H + 0.5
        hbar.Maximum <- max 0.0 (ext.W - s.Viewport.W)
        hbar.ViewportSize <- s.Viewport.W
        hbar.Value <- max 0.0 s.Offset.X
        hbar.LargeChange <- s.Viewport.W * 0.9
        hbar.SmallChange <- 60.0
        hbar.IsVisible <- ext.W > s.Viewport.W + 0.5
        syncing <- false

    do
        this.ColumnDefinitions <- ColumnDefinitions("*,Auto")
        this.RowDefinitions <- RowDefinitions("*,Auto")
        Grid.SetColumn(vbar, 1)
        Grid.SetRow(hbar, 1)
        this.Children.Add canvas
        this.Children.Add vbar
        this.Children.Add hbar
        AutomationProperties.SetName(canvas, "Document pages")
        AutomationProperties.SetName(vbar, "Vertical scroll")
        AutomationProperties.SetName(hbar, "Horizontal scroll")
        canvas.StateChanged.Add(fun () -> sync ())
        vbar.PropertyChanged.Add(fun e ->
            if not syncing && e.Property = RangeBase.ValueProperty then
                canvas.Dispatch(Viewer.ScrollTo { X = canvas.State.Offset.X; Y = vbar.Value }))
        hbar.PropertyChanged.Add(fun e ->
            if not syncing && e.Property = RangeBase.ValueProperty then
                canvas.Dispatch(Viewer.ScrollTo { X = hbar.Value; Y = canvas.State.Offset.Y }))

    member _.Canvas = canvas
