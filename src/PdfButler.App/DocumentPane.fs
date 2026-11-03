/// The main window: toolbar, notices, search bar, outline, document view and status bar.
/// It only wires effects; navigation state lives in `Viewer` (via the canvas).
module PdfButler.App.DocumentPane

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Input.Platform
open Avalonia.Interactivity
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.Threading
open Avalonia.VisualTree
open PdfButler.Core
open PdfButler.Pdfium
open PdfButler.App.RendererProcess
open PdfButler.App.PdfView

/// Schemes that may be opened (after confirmation). Everything else, including file:, is refused.
let allowedSchemes = set [ "http"; "https"; "mailto" ]

/// Keys typed here are text, so plain-key shortcuts (Space pan/page, N, P, arrows...) must not fire.
/// Form filling must extend this for a focused form field, so Space types a space instead of panning.
let isTypingTarget (source: obj) = source :? TextBox

type DocumentPane(host: Window, engine: RendererEngine, requestOpen: unit -> unit) as this =
    inherit ContentControl()

    let view = DocumentView()
    let canvas = view.Canvas
    let mutable openGeneration = 0
    let mutable centre = Welcome
    let mutable labels: string option[] = [||]
    let mutable lastSearch = ("", false)
    let mutable active = true
    let mutable currentPath: string option = None
    let mutable openCts: CancellationTokenSource option = None
    let mutable loadingFirstPage = false

    // ---- helpers -----------------------------------------------------------------------------

    let named (name: string) (tip: string) (c: Control) =
        AutomationProperties.SetName(c, name)
        ToolTip.SetTip(c, tip)

    let toolButton (text: string) (name: string) (tip: string) =
        let b = Button(Content = text, Padding = Thickness(9.0, 5.0), CornerRadius = CornerRadius(5.0),
                       VerticalAlignment = VerticalAlignment.Center)
        named name tip b
        if List.contains name [ "Open"; "Find"; "Fit width"; "Fit page"; "Rotate clockwise"; "Dismiss notice"; "Close search" ] then
            b.Content <- Icons.create name
        b.MinWidth <- 32.0
        b.Height <- 32.0
        b.Background <- Brushes.Transparent
        b.BorderThickness <- Thickness 0.0
        b

    let fileName (path: string) = try Path.GetFileName path with _ -> path

    // ---- toolbar ---------------------------------------------------------------------------------

    let openButton = toolButton "Open" "Open" "Open a PDF (Ctrl+O)"
    let backButton = toolButton "←" "Back" "Previous location (Alt+Left)"
    let forwardButton = toolButton "→" "Forward" "Next location (Alt+Right)"
    let prevButton = toolButton "◀" "Previous page" "Previous page (P)"
    let nextButton = toolButton "▶" "Next page" "Next page (N)"
    let pageBox = TextBox(Width = 64.0, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center)
    let pageCount = TextBlock(VerticalAlignment = VerticalAlignment.Center, Margin = Thickness(4.0, 0.0, 8.0, 0.0), Text = "/ 0")
    let zoomOut = toolButton "−" "Zoom out" "Zoom out (Ctrl+−)"
    let zoomBox = TextBox(Width = 64.0, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center)
    let zoomIn = toolButton "+" "Zoom in" "Zoom in (Ctrl+=)"
    let fitWidth = toolButton "Fit width" "Fit width" "Fit width (Ctrl+2)"
    let fitPage = toolButton "Fit page" "Fit page" "Fit page (Ctrl+0)"
    let rotate = toolButton "Rotate" "Rotate clockwise" "Rotate clockwise (Ctrl+Shift+=)"
    let outlineButton = ToggleButton(Content = Icons.create "Outline", Padding = Thickness(7.0), Height = 32.0, VerticalAlignment = VerticalAlignment.Center)
    let findButton = toolButton "Find" "Find" "Find in document (Ctrl+F)"

    let documentControls: Control list =
        [ backButton; forwardButton; prevButton; nextButton; pageBox; zoomOut; zoomBox; zoomIn; fitWidth; fitPage; rotate; findButton ]

    let toolbar =
        let p = StackPanel(Orientation = Orientation.Horizontal, Spacing = 5.0, Margin = Thickness(12.0, 8.0))
        named "Page" "Page number or label (Ctrl+G)" pageBox
        named "Zoom" "Zoom percentage" zoomBox
        named "Outline" "Show or hide the outline" outlineButton
        let sep () = Border(Width = 1.0, Margin = Thickness(6.0, 4.0), Background = SolidColorBrush(Color.FromArgb(60uy, 128uy, 128uy, 128uy)))
        for c in
            [ openButton :> Control; sep (); backButton; forwardButton; prevButton; pageBox; pageCount; nextButton; sep ()
              zoomOut; zoomBox; zoomIn; fitWidth; fitPage; sep (); rotate; outlineButton; findButton ] do
            p.Children.Add c
        ScrollViewer(Content = p, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                     VerticalScrollBarVisibility = ScrollBarVisibility.Disabled)

    // ---- notice bar --------------------------------------------------------------------------------

    let noticeText = TextBlock(TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center)
    let noticeDismiss = toolButton "Dismiss" "Dismiss notice" "Hide this notice"

    let noticeBar =
        let g = Grid(ColumnDefinitions = ColumnDefinitions("*,Auto"))
        Grid.SetColumn(noticeDismiss, 1)
        g.Children.Add noticeText
        g.Children.Add noticeDismiss
        Border(
            Child = g,
            Padding = Thickness(10.0, 4.0),
            Background = SolidColorBrush(Color.FromArgb(70uy, 255uy, 190uy, 0uy)),
            IsVisible = false
        )

    // ---- search bar ----------------------------------------------------------------------------------

    let searchBox = TextBox(Width = 260.0, PlaceholderText = "Find in document", VerticalAlignment = VerticalAlignment.Center)
    let matchCase = CheckBox(Content = "Match case", VerticalAlignment = VerticalAlignment.Center)
    let searchPrev = toolButton "▲" "Previous match" "Previous match (Shift+F3)"
    let searchNext = toolButton "▼" "Next match" "Next match (F3)"
    let searchStatus = TextBlock(VerticalAlignment = VerticalAlignment.Center, Margin = Thickness(8.0, 0.0))
    let searchClose = toolButton "✕" "Close search" "Close search (Esc)"
    let searchTimer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 250.0)

    let searchBar =
        named "Search text" "Text to find (Enter: next match)" searchBox
        named "Match case" "Match upper and lower case exactly" matchCase
        let p = StackPanel(Orientation = Orientation.Horizontal, Spacing = 4.0, Margin = Thickness(6.0, 2.0))
        for c in [ searchBox :> Control; matchCase; searchPrev; searchNext; searchStatus ] do
            p.Children.Add c
        let g = Grid(ColumnDefinitions = ColumnDefinitions("*,Auto"))
        Grid.SetColumn(searchClose, 1)
        searchClose.Margin <- Thickness(6.0, 2.0)
        g.Children.Add p
        g.Children.Add searchClose
        Border(Child = g, IsVisible = false)

    // ---- outline -------------------------------------------------------------------------------------

    let outlineTree = TreeView()
    let outlinePanel =
        AutomationProperties.SetName(outlineTree, "Outline")
        Border(Child = outlineTree, Width = 260.0, IsVisible = false, BorderThickness = Thickness(0.0, 0.0, 1.0, 0.0),
               BorderBrush = SolidColorBrush(Color.FromArgb(60uy, 128uy, 128uy, 128uy)))

    // ---- centre ----------------------------------------------------------------------------------------

    let welcome =
        let t = TextBlock(Text = "Open a PDF (Ctrl+O) or drop one here", FontSize = 18.0,
                          HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                          Opacity = 0.7)
        AutomationProperties.SetName(t, "Welcome")
        t

    let errorTitle = TextBlock(FontSize = 18.0, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap)
    let errorMessage = TextBlock(TextWrapping = TextWrapping.Wrap)
    let retryButton = Button(Content = "Reopen PDF", HorizontalAlignment = HorizontalAlignment.Left)

    let errorPanel =
        let p = StackPanel(Spacing = 8.0, MaxWidth = 560.0, HorizontalAlignment = HorizontalAlignment.Center,
                           VerticalAlignment = VerticalAlignment.Center, IsVisible = false)
        p.Children.Add errorTitle
        p.Children.Add errorMessage
        p.Children.Add retryButton
        AutomationProperties.SetName(p, "Error")
        p

    let loadingTitle = TextBlock(FontSize = 18.0, FontWeight = FontWeight.SemiBold,
                                 TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center)
    let loadingProgress = ProgressBar(Width = 240.0, Height = 4.0, IsIndeterminate = false,
                                     HorizontalAlignment = HorizontalAlignment.Center)
    let loadingPanel =
        let p = StackPanel(Spacing = 14.0, MaxWidth = 440.0, Margin = Thickness 24.0,
                           HorizontalAlignment = HorizontalAlignment.Center,
                           VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
                           IsHitTestVisible = false)
        p.Children.Add loadingTitle
        p.Children.Add loadingProgress
        p.Children.Add(TextBlock(Text = "Cloud-backed files may take longer if they need to download first.",
                                 TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = 0.7))
        p.Children.Add(TextBlock(Text = "You can switch tabs or close this tab while it loads.",
                                 TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = 0.7))
        AutomationProperties.SetName(p, "Loading document")
        AutomationProperties.SetName(loadingProgress, "Loading PDF")
        p

    let centrePanel =
        let g = Grid()
        g.Children.Add view
        g.Children.Add welcome
        g.Children.Add errorPanel
        g.Children.Add loadingPanel
        g

    // ---- status bar -------------------------------------------------------------------------------------

    let statusPage = TextBlock(VerticalAlignment = VerticalAlignment.Center)
    let statusZoom = TextBlock(VerticalAlignment = VerticalAlignment.Center, Margin = Thickness(16.0, 0.0))
    let statusMessage = TextBlock(VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis)
    let messageTimer = DispatcherTimer(Interval = TimeSpan.FromSeconds 5.0)

    let statusBar =
        AutomationProperties.SetName(statusPage, "Current page")
        AutomationProperties.SetName(statusZoom, "Current zoom")
        AutomationProperties.SetName(statusMessage, "Status message")
        let g = Grid(ColumnDefinitions = ColumnDefinitions("Auto,Auto,*"), Margin = Thickness(8.0, 3.0))
        Grid.SetColumn(statusZoom, 1)
        Grid.SetColumn(statusMessage, 2)
        g.Children.Add statusPage
        g.Children.Add statusZoom
        g.Children.Add statusMessage
        Border(Child = g, BorderThickness = Thickness(0.0, 1.0, 0.0, 0.0),
               BorderBrush = SolidColorBrush(Color.FromArgb(60uy, 128uy, 128uy, 128uy)))

    let showMessage (msg: string) =
        statusMessage.Text <- msg
        messageTimer.Stop()
        if msg <> "" then messageTimer.Start()

    // ---- state -> controls ---------------------------------------------------------------------------

    let hasDoc () = centre = DocumentShown && not (isNull canvas.Document)

    let setCentre (c: Centre) =
        centre <- c
        view.IsVisible <- (c = DocumentShown)
        welcome.IsVisible <- (c = Welcome)
        errorPanel.IsVisible <- (match c with OpenFailed _ -> true | _ -> false)
        let loading =
            match c with
            | Opening name -> loadingTitle.Text <- $"Opening “{name}”…"; true
            | DocumentShown when loadingFirstPage -> loadingTitle.Text <- "Loading first page…"; true
            | _ -> false
        loadingPanel.IsVisible <- loading
        loadingProgress.IsIndeterminate <- loading
        match c with
        | OpenFailed(name, message) ->
            errorTitle.Text <- $"“{name}” could not be opened"
            errorMessage.Text <- message
        | _ -> ()
        for ctl in documentControls do
            ctl.IsEnabled <- (c = DocumentShown)
        outlineButton.IsEnabled <- (c = DocumentShown) && outlineTree.ItemCount > 0

    let refreshState () =
        let s = canvas.State
        if hasDoc () then
            let page = canvas.CurrentPage
            statusPage.Text <- PageInput.pageStatus labels page
            statusZoom.Text <- PageInput.zoomText s.Zoom
            if not pageBox.IsFocused then pageBox.Text <- PageInput.pageBoxText labels page
            if not zoomBox.IsFocused then zoomBox.Text <- PageInput.zoomText s.Zoom
            pageCount.Text <- $"/ {labels.Length}"
            prevButton.IsEnabled <- page > 0
            nextButton.IsEnabled <- page < labels.Length - 1
            backButton.IsEnabled <- History.canGoBack s.History
            forwardButton.IsEnabled <- History.canGoForward s.History
        else
            statusPage.Text <- ""
            statusZoom.Text <- ""
            pageBox.Text <- ""
            zoomBox.Text <- ""
            pageCount.Text <- "/ 0"

    let focusCanvas () = canvas.Focus() |> ignore

    // ---- space-bar pan ----------------------------------------------------------------------------------

    let mutable spacePan = SpacePan.Idle

    let setSpacePan (s: SpacePan.State) =
        spacePan <- s
        canvas.HandMode <- SpacePan.isHeld s

    /// The key-up may never arrive (focus moved, window deactivated): drop the hold without paging.
    let cancelSpacePan () = if SpacePan.isHeld spacePan then setSpacePan (SpacePan.cancel spacePan)

    let showNotices (info: DocumentInfo) =
        let notices =
            [ if info.Forms = XfaFull then "Dynamic XFA form, not supported: content may be missing."
              if info.SignatureCount > 0 then
                  let s = if info.SignatureCount = 1 then "" else "s"
                  $"Contains {info.SignatureCount} digital signature{s}: shown, not verified." ]
        noticeText.Text <- String.Join("  ", notices)
        noticeBar.IsVisible <- not notices.IsEmpty

    // ---- outline ---------------------------------------------------------------------------------------

    let rec outlineNode (item: OutlineItem) : TreeViewItem =
        let node = TreeViewItem(Header = (if item.Title = "" then "(untitled)" else item.Title), Tag = box item)
        AutomationProperties.SetName(node, item.Title)
        if not item.Children.IsEmpty then
            // Children are built when first expanded, so huge outlines stay cheap.
            let mutable built = false
            let build () =
                if not built then
                    built <- true
                    node.Items.Clear()
                    for c in item.Children do node.Items.Add(outlineNode c) |> ignore
            if item.StartsOpen then
                build ()
                node.IsExpanded <- true
            else
                node.Items.Add(TreeViewItem(Header = "…")) |> ignore
                node.PropertyChanged.Add(fun e -> if e.Property = TreeViewItem.IsExpandedProperty && node.IsExpanded then build ())
        node

    let setOutline (items: OutlineItem list) =
        outlineTree.Items.Clear()
        for i in items do outlineTree.Items.Add(outlineNode i) |> ignore
        let any = not items.IsEmpty
        outlineButton.IsEnabled <- any && centre = DocumentShown
        outlinePanel.IsVisible <- any && outlineButton.IsChecked = Nullable true

    // ---- links -----------------------------------------------------------------------------------------

    let rec handleLink (target: LinkTarget) =
        match target with
        | GoTo d ->
            canvas.GoTo d
            focusCanvas ()
        | Uri text ->
            match System.Uri.TryCreate(text, UriKind.Absolute) with
            | true, uri when allowedSchemes.Contains(uri.Scheme.ToLowerInvariant()) ->
                task {
                    let! ok = this.ConfirmLink uri
                    if ok then
                        let! launched = this.LaunchUri uri
                        if not launched then showMessage "The link could not be opened."
                } |> ignore
            | true, uri -> showMessage $"Link not opened: “{uri.Scheme}:” links are not supported."
            | _ -> showMessage "Link not opened: the address is not valid."
        | Unsupported kind -> showMessage $"This link can't be followed ({kind})."

    // ---- search ---------------------------------------------------------------------------------------

    let runSearch () =
        searchTimer.Stop()
        let q = if isNull searchBox.Text then "" else searchBox.Text
        let mc = matchCase.IsChecked = Nullable true
        if (q, mc) <> lastSearch then
            lastSearch <- (q, mc)
            if String.IsNullOrWhiteSpace q then canvas.ClearSearch() else canvas.StartSearch(q, mc)

    let openSearch () =
        if hasDoc () then
            searchBar.IsVisible <- true
            searchBox.Focus() |> ignore
            searchBox.SelectAll()

    let closeSearch () =
        searchTimer.Stop()
        searchBar.IsVisible <- false
        lastSearch <- ("", false)
        canvas.ClearSearch()
        focusCanvas ()

    let failed name message =
        let old = canvas.Document
        closeSearch ()
        canvas.SetDocument null
        labels <- [||]
        setOutline []
        noticeBar.IsVisible <- false
        setCentre (OpenFailed(name, message))
        refreshState ()
        if not (isNull old) then old.CloseAsync() |> ignore

    let nextHit forward =
        if searchBar.IsVisible then
            runSearch ()
            canvas.NextHit forward

    // ---- copy ------------------------------------------------------------------------------------------

    let copySelection () =
        if canvas.HasSelection then
            task {
                let! text = canvas.GetSelectedTextAsync()
                match host.Clipboard with
                | null -> ()
                | cb when text <> "" ->
                    do! cb.SetTextAsync text
                    showMessage "Copied."
                | _ -> ()
            } |> ignore

    // ---- page / zoom boxes -------------------------------------------------------------------------------

    let commitPageBox () =
        if hasDoc () then
            match PageInput.parsePage labels pageBox.Text with
            | Some p ->
                canvas.Dispatch(Viewer.GoToPage p)
                focusCanvas ()
            | None -> showMessage $"No page “{pageBox.Text}”."
        refreshState ()

    let commitZoomBox () =
        if hasDoc () then
            match PageInput.parseZoom zoomBox.Text with
            | Some z ->
                canvas.Dispatch(Viewer.ZoomTo(z, None))
                focusCanvas ()
            | None -> showMessage "Enter a zoom percentage, for example 150."
        refreshState ()

    do
        openButton.Background <- SolidColorBrush(Color.Parse "#65c9bc")
        openButton.Foreground <- SolidColorBrush(Color.Parse "#163342")
        openButton.FontWeight <- FontWeight.SemiBold
        let top = StackPanel()
        top.Children.Add toolbar
        top.Children.Add noticeBar
        top.Children.Add searchBar
        let dock = DockPanel(LastChildFill = true)
        DockPanel.SetDock(top, Dock.Top)
        DockPanel.SetDock(statusBar, Dock.Bottom)
        DockPanel.SetDock(outlinePanel, Dock.Left)
        dock.Children.Add top
        dock.Children.Add statusBar
        dock.Children.Add outlinePanel
        dock.Children.Add centrePanel
        this.Content <- dock

        // Toolbar.
        let afterClick (b: Button) (f: unit -> unit) =
            b.Click.Add(fun _ ->
                f ()
                if hasDoc () then focusCanvas ())
        openButton.Click.Add(fun _ -> requestOpen ())
        retryButton.Click.Add(fun _ -> currentPath |> Option.iter (fun path -> this.OpenFileAsync path |> ignore))
        afterClick backButton (fun () -> canvas.Dispatch Viewer.Back)
        afterClick forwardButton (fun () -> canvas.Dispatch Viewer.Forward)
        afterClick prevButton (fun () -> canvas.Dispatch Viewer.PreviousPage)
        afterClick nextButton (fun () -> canvas.Dispatch Viewer.NextPage)
        afterClick zoomOut (fun () -> canvas.Dispatch(Viewer.ZoomStep(-1, None)))
        afterClick zoomIn (fun () -> canvas.Dispatch(Viewer.ZoomStep(1, None)))
        afterClick fitWidth (fun () -> canvas.Dispatch(Viewer.SetMode Viewer.FitWidth))
        afterClick fitPage (fun () -> canvas.Dispatch(Viewer.SetMode Viewer.FitPage))
        afterClick rotate (fun () -> canvas.Dispatch Viewer.RotateClockwise)
        findButton.Click.Add(fun _ -> openSearch ())
        outlineButton.IsCheckedChanged.Add(fun _ -> outlinePanel.IsVisible <- outlineButton.IsChecked = Nullable true && outlineTree.ItemCount > 0)
        noticeDismiss.Click.Add(fun _ -> noticeBar.IsVisible <- false; if hasDoc () then focusCanvas ())

        pageBox.KeyDown.Add(fun e ->
            match e.Key with
            | Key.Enter -> commitPageBox (); e.Handled <- true
            | Key.Escape -> refreshState (); focusCanvas (); e.Handled <- true
            | _ -> ())
        pageBox.LostFocus.Add(fun _ -> refreshState ())
        pageBox.GotFocus.Add(fun _ -> pageBox.SelectAll())
        zoomBox.KeyDown.Add(fun e ->
            match e.Key with
            | Key.Enter -> commitZoomBox (); e.Handled <- true
            | Key.Escape -> refreshState (); focusCanvas (); e.Handled <- true
            | _ -> ())
        zoomBox.LostFocus.Add(fun _ -> refreshState ())
        zoomBox.GotFocus.Add(fun _ -> zoomBox.SelectAll())

        // Search.
        searchBox.TextChanged.Add(fun _ ->
            searchTimer.Stop()
            searchTimer.Start())
        searchTimer.Tick.Add(fun _ -> runSearch ())
        matchCase.IsCheckedChanged.Add(fun _ -> runSearch ())
        searchBox.KeyDown.Add(fun e ->
            match e.Key with
            | Key.Enter ->
                nextHit (not (e.KeyModifiers.HasFlag KeyModifiers.Shift))
                e.Handled <- true
            | _ -> ())
        searchPrev.Click.Add(fun _ -> nextHit false)
        searchNext.Click.Add(fun _ -> nextHit true)
        searchClose.Click.Add(fun _ -> closeSearch ())
        canvas.SearchChanged.Add(fun info -> searchStatus.Text <- info.Text)

        // Outline: click or Enter navigates (the expander arrow only expands).
        outlineTree.Tapped.Add(fun e ->
            match e.Source with
            | :? Visual as v when isNull (v.FindAncestorOfType<ToggleButton>(true)) ->
                match v.FindAncestorOfType<TreeViewItem>(true) with
                | null -> ()
                | item ->
                    match item.Tag with
                    | :? OutlineItem as o -> o.Target |> Option.iter handleLink
                    | _ -> ()
            | _ -> ())
        outlineTree.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                match outlineTree.SelectedItem with
                | :? TreeViewItem as item ->
                    match item.Tag with
                    | :? OutlineItem as o ->
                        o.Target |> Option.iter handleLink
                        e.Handled <- true
                    | _ -> ()
                | _ -> ())

        // Canvas.
        canvas.StateChanged.Add(fun () -> refreshState ())
        canvas.InitialContentReady.Add(fun () ->
            loadingFirstPage <- false
            setCentre centre)
        canvas.LinkActivated.Add handleLink
        canvas.PanStarted.Add(fun () -> spacePan <- SpacePan.panned spacePan)
        canvas.LostFocus.Add(fun _ -> cancelSpacePan ())
        host.Deactivated.Add(fun _ -> cancelSpacePan ())
        let copyItem = MenuItem(Header = "Copy", InputGesture = KeyGesture(Key.C, KeyModifiers.Control))
        copyItem.Click.Add(fun _ -> copySelection ())
        let menu = ContextMenu()
        menu.Items.Add copyItem |> ignore
        menu.Opening.Add(fun _ -> copyItem.IsEnabled <- canvas.HasSelection)
        canvas.ContextMenu <- menu

        messageTimer.Tick.Add(fun _ -> messageTimer.Stop(); statusMessage.Text <- "")

        setCentre Welcome
        refreshState ()

    // ---- hooks replaceable by tests ------------------------------------------------------------------

    /// Ask for a password: file name, whether the last one was wrong. None = cancel.
    member val AskPassword: string -> bool -> Task<string option> =
        (fun name wrong -> Dialogs.askPassword host name wrong) with get, set

    /// Confirm opening an external link. Shows the full address.
    member val ConfirmLink: System.Uri -> Task<bool> =
        (fun uri ->
            Dialogs.confirm host "Open link?" "This document links to an outside address. Open it in your browser or mail app?"
                uri.AbsoluteUri "Open link") with get, set

    member val LaunchUri: System.Uri -> Task<bool> =
        (fun uri -> host.Launcher.LaunchUriAsync uri) with get, set

    /// Zoom applied to each newly opened document (from settings).
    member val DefaultZoom: Settings.DefaultZoom = Settings.ActualSize with get, set

    // ---- public surface (also used by tests) ----------------------------------------------------------

    member _.View = view
    member _.Canvas = canvas
    member _.Centre = centre
    member _.StatusPageText = string statusPage.Text
    member _.StatusMessage = string statusMessage.Text
    member _.SearchStatusText = string searchStatus.Text
    member _.SearchBox = searchBox
    member _.PageBox = pageBox
    member _.ZoomBox = zoomBox
    member _.OutlineTree = outlineTree
    member _.OutlineButton = outlineButton
    member _.IsOutlineShown = outlinePanel.IsVisible
    member _.NoticeText = if noticeBar.IsVisible then noticeText.Text else ""
    member _.IsSearchOpen = searchBar.IsVisible
    member _.RunSearchNow() = runSearch ()
    member _.ShowMessage(message) = showMessage message
    /// The 0-based page at the top of the view, when a document is shown.
    member _.CurrentPageIndex = if hasDoc () then Some canvas.CurrentPage else None
    member _.SetActive(value) =
        active <- value
        if not value then cancelSpacePan ()
        canvas.SetActive value
        if value then focusCanvas ()

    /// Close the current document and return to the welcome panel.
    member _.CloseDocument() =
        openGeneration <- openGeneration + 1
        openCts |> Option.iter (fun c -> c.Cancel())
        currentPath <- None
        let old = canvas.Document
        closeSearch ()
        canvas.SetDocument null
        labels <- [||]
        setOutline []
        noticeBar.IsVisible <- false
        setCentre Welcome
        refreshState ()
        if not (isNull old) then old.CloseAsync() |> ignore

    /// Open a file, asking for a password if needed. A newer open supersedes an older one.
    /// `startPage` (0-based) is shown without a history entry, for restored tabs.
    member _.OpenFileAsync(path: string, ?startPage: int) : Task =
        openGeneration <- openGeneration + 1
        openCts |> Option.iter (fun c -> c.Cancel())
        let cts = new CancellationTokenSource()
        openCts <- Some cts
        currentPath <- Some path
        let gen = openGeneration
        let name = fileName path
        Diagnostics.mark "open-start"
        // Show feedback before starting any helper or file I/O (cloud hydration can block it).
        let previous = canvas.Document
        closeSearch ()
        canvas.SetDocument null
        loadingFirstPage <- false
        labels <- [||]
        setOutline []
        noticeBar.IsVisible <- false
        showMessage ""
        setCentre (Opening name)
        refreshState ()
        if not (isNull previous) then previous.CloseAsync() |> ignore
        let failed = failed name

        let attach (d: RemoteDocument) =
            Diagnostics.mark "document-opened"
            let old = canvas.Document
            closeSearch ()
            labels <- d.Info.Pages |> Array.map (fun p -> p.Label)
            setOutline []
            loadingFirstPage <- true
            setCentre DocumentShown
            // Detach the old document from the view before closing it.
            canvas.SetDocument d
            match this.DefaultZoom with
            | Settings.FitWidth -> canvas.Dispatch(Viewer.SetMode Viewer.FitWidth)
            | Settings.FitPage -> canvas.Dispatch(Viewer.SetMode Viewer.FitPage)
            | Settings.ActualSize -> ()
            startPage |> Option.iter (fun p -> if p > 0 then canvas.Dispatch(Viewer.ShowPage p))
            let rendererFailed message =
                Dispatcher.UIThread.Post(fun () ->
                    if obj.ReferenceEquals(canvas.Document, d) then failed message)
            d.Failed.Add rendererFailed
            // Covers a helper exit between the open reply and subscribing to the event.
            d.Failure |> Option.iter rendererFailed
            if not (isNull old) then old.CloseAsync() |> ignore
            showNotices d.Info
            refreshState ()
            if active then focusCanvas ()
            d.GetOutlineAsync(CancellationToken.None).ContinueWith(fun (t: Task<OutlineItem list>) ->
                Dispatcher.UIThread.Post(fun () ->
                    if obj.ReferenceEquals(canvas.Document, d) && t.IsCompletedSuccessfully then setOutline t.Result))
            |> ignore

        let opening = task {
            let mutable password: string option = None
            let mutable finished = false
            while not finished do
                let! result =
                    task {
                        try
                            let! r = engine.OpenAsync(path, password, cts.Token)
                            return Ok r
                        with e ->
                            return Error e
                    }
                if gen <> openGeneration then
                    // Superseded by a later open or close.
                    match result with
                    | Ok(Ok d) -> do! d.CloseAsync()
                    | _ -> ()
                    finished <- true
                else
                    match result with
                    | Ok(Ok d) ->
                        attach d
                        finished <- true
                    | Ok(Error(PasswordRequired | WrongPassword as err)) ->
                        let! answer = this.AskPassword name (err = WrongPassword)
                        if gen <> openGeneration then finished <- true
                        else
                            match answer with
                            | Some p -> password <- Some p
                            | None ->
                                failed (OpenError.describe PasswordRequired)
                                finished <- true
                    | Ok(Error err) ->
                        failed (OpenError.describe err)
                        finished <- true
                    | Error e ->
                        failed $"The PDF engine is not available ({e.GetBaseException().Message})."
                        finished <- true
        }
        task {
            try do! opening
            finally
                if openCts |> Option.exists (fun c -> obj.ReferenceEquals(c, cts)) then openCts <- None
                cts.Dispose()
        }

    // ---- keyboard -------------------------------------------------------------------------------------

    override this.OnKeyDown(e: KeyEventArgs) =
        base.OnKeyDown e
        if not e.Handled then
            let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control
            let shift = e.KeyModifiers.HasFlag KeyModifiers.Shift
            let alt = e.KeyModifiers.HasFlag KeyModifiers.Alt
            let inText = isTypingTarget e.Source
            let doc = hasDoc ()
            let go (msg: Viewer.Msg) = if doc then canvas.Dispatch msg
            let isPlus = e.Key = Key.OemPlus || e.Key = Key.Add
            let isMinus = e.Key = Key.OemMinus || e.Key = Key.Subtract
            let handled =
                match e.Key with
                // Shortcuts that work everywhere, including while typing in a box.
                | Key.F when ctrl -> openSearch (); true
                | Key.G when ctrl && doc -> pageBox.Focus() |> ignore; pageBox.SelectAll(); true
                | Key.F3 -> nextHit (not shift); true
                | Key.Escape ->
                    if searchBar.IsVisible then closeSearch ()
                    elif canvas.HasSelection then canvas.ClearSelection()
                    elif inText then focusCanvas ()
                    true
                | _ when ctrl && shift && isPlus -> go Viewer.RotateClockwise; true
                | _ when ctrl && shift && isMinus -> go Viewer.RotateCounterClockwise; true
                | _ when ctrl && isPlus -> go (Viewer.ZoomStep(1, None)); true
                | _ when ctrl && isMinus -> go (Viewer.ZoomStep(-1, None)); true
                | Key.D0 | Key.NumPad0 when ctrl -> go (Viewer.SetMode Viewer.FitPage); true
                | Key.D1 | Key.NumPad1 when ctrl -> go (Viewer.ZoomTo(1.0, None)); true
                | Key.D2 | Key.NumPad2 when ctrl -> go (Viewer.SetMode Viewer.FitWidth); true
                | Key.Left when alt -> go Viewer.Back; true
                | Key.Right when alt -> go Viewer.Forward; true
                | _ when inText -> false
                // Plain keys: only when not typing.
                | Key.C when ctrl -> copySelection (); true
                | Key.PageDown -> go Viewer.PageDown; true
                | Key.PageUp -> go Viewer.PageUp; true
                // Hold to pan; a tap pages on release (see OnKeyUp).
                | Key.Space -> (if doc then setSpacePan (SpacePan.press shift spacePan)); true
                | Key.Home when not alt -> go Viewer.FirstPage; true
                | Key.End when not alt -> go Viewer.LastPage; true
                | Key.Down -> go (Viewer.ScrollBy(0.0, 40.0)); true
                | Key.Up -> go (Viewer.ScrollBy(0.0, -40.0)); true
                | Key.Left -> go (Viewer.ScrollBy(-40.0, 0.0)); true
                | Key.Right -> go (Viewer.ScrollBy(40.0, 0.0)); true
                | Key.N when e.KeyModifiers = KeyModifiers.None -> go Viewer.NextPage; true
                | Key.P when e.KeyModifiers = KeyModifiers.None -> go Viewer.PreviousPage; true
                | _ -> false
            if handled then e.Handled <- true

    override this.OnKeyUp(e: KeyEventArgs) =
        base.OnKeyUp e
        if e.Key = Key.Space && SpacePan.isHeld spacePan then
            let s, action = SpacePan.release spacePan
            setSpacePan s
            if hasDoc () then
                match action with
                | SpacePan.PageDown -> canvas.Dispatch Viewer.PageDown
                | SpacePan.PageUp -> canvas.Dispatch Viewer.PageUp
                | SpacePan.NoAction -> ()
            e.Handled <- true

    member this.Close() =
        searchTimer.Stop()
        messageTimer.Stop()
        this.CloseDocument()
