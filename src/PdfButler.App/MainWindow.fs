/// The main window: toolbar, notices, search bar, outline, document view and status bar.
/// It only wires effects; navigation state lives in `Viewer` (via the canvas).
module PdfButler.App.MainWindow

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
open PdfButler.App.PdfView

/// Schemes that may be opened (after confirmation). Everything else, including file:, is refused.
let allowedSchemes = set [ "http"; "https"; "mailto" ]

/// What the centre area shows.
type Centre =
    | Welcome
    | DocumentShown
    | OpenFailed of fileName: string * message: string

type MainWindow(engine: PdfEngine) as this =
    inherit Window()

    let view = DocumentView()
    let canvas = view.Canvas
    let mutable openGeneration = 0
    let mutable centre = Welcome
    let mutable labels: string option[] = [||]
    let mutable lastSearch = ("", false)

    // ---- helpers -----------------------------------------------------------------------------

    let named (name: string) (tip: string) (c: Control) =
        AutomationProperties.SetName(c, name)
        ToolTip.SetTip(c, tip)

    let toolButton (text: string) (name: string) (tip: string) =
        let b = Button(Content = text, Padding = Thickness(8.0, 4.0), VerticalAlignment = VerticalAlignment.Center)
        named name tip b
        b

    let fileName (path: string) = try Path.GetFileName path with _ -> path

    // ---- toolbar ---------------------------------------------------------------------------------

    let openButton = toolButton "Open" "Open" "Open a PDF (Ctrl+O)"
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
    let outlineButton = ToggleButton(Content = "Outline", Padding = Thickness(8.0, 4.0), VerticalAlignment = VerticalAlignment.Center)
    let findButton = toolButton "Find" "Find" "Find in document (Ctrl+F)"

    let documentControls: Control list =
        [ prevButton; nextButton; pageBox; zoomOut; zoomBox; zoomIn; fitWidth; fitPage; rotate; findButton ]

    let toolbar =
        let p = StackPanel(Orientation = Orientation.Horizontal, Spacing = 4.0, Margin = Thickness(6.0, 4.0))
        named "Page" "Page number or label (Ctrl+G)" pageBox
        named "Zoom" "Zoom percentage" zoomBox
        named "Outline" "Show or hide the outline" outlineButton
        let sep () = Border(Width = 1.0, Margin = Thickness(6.0, 4.0), Background = SolidColorBrush(Color.FromArgb(60uy, 128uy, 128uy, 128uy)))
        for c in
            [ openButton :> Control; sep (); prevButton; pageBox; pageCount; nextButton; sep ()
              zoomOut; zoomBox; zoomIn; fitWidth; fitPage; sep (); rotate; outlineButton; findButton ] do
            p.Children.Add c
        p

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

    let errorPanel =
        let p = StackPanel(Spacing = 8.0, MaxWidth = 560.0, HorizontalAlignment = HorizontalAlignment.Center,
                           VerticalAlignment = VerticalAlignment.Center, IsVisible = false)
        p.Children.Add errorTitle
        p.Children.Add errorMessage
        AutomationProperties.SetName(p, "Error")
        p

    let centrePanel =
        let g = Grid()
        g.Children.Add view
        g.Children.Add welcome
        g.Children.Add errorPanel
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

    let hasDoc () = not (isNull canvas.Document)

    let setCentre (c: Centre) =
        centre <- c
        view.IsVisible <- (c = DocumentShown)
        welcome.IsVisible <- (c = Welcome)
        errorPanel.IsVisible <- (match c with OpenFailed _ -> true | _ -> false)
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
        else
            statusPage.Text <- ""
            statusZoom.Text <- ""
            pageBox.Text <- ""
            zoomBox.Text <- ""
            pageCount.Text <- "/ 0"

    let focusCanvas () = canvas.Focus() |> ignore

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

    let nextHit forward =
        if searchBar.IsVisible then
            runSearch ()
            canvas.NextHit forward

    // ---- copy ------------------------------------------------------------------------------------------

    let copySelection () =
        if canvas.HasSelection then
            task {
                let! text = canvas.GetSelectedTextAsync()
                match this.Clipboard with
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
        this.Title <- "PdfButler"
        this.Width <- 1100.0
        this.Height <- 850.0
        this.MinWidth <- 400.0
        this.MinHeight <- 300.0

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
        openButton.Click.Add(fun _ -> this.ShowOpenDialog() |> ignore)
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
        canvas.LinkActivated.Add handleLink
        let copyItem = MenuItem(Header = "Copy", InputGesture = KeyGesture(Key.C, KeyModifiers.Control))
        copyItem.Click.Add(fun _ -> copySelection ())
        let menu = ContextMenu()
        menu.Items.Add copyItem |> ignore
        menu.Opening.Add(fun _ -> copyItem.IsEnabled <- canvas.HasSelection)
        canvas.ContextMenu <- menu

        messageTimer.Tick.Add(fun _ -> messageTimer.Stop(); statusMessage.Text <- "")

        // Drag and drop.
        DragDrop.SetAllowDrop(this, true)
        this.AddHandler(DragDrop.DragOverEvent, EventHandler<DragEventArgs>(fun _ e ->
            e.DragEffects <- (if isNull (e.DataTransfer.TryGetFiles()) then DragDropEffects.None else DragDropEffects.Copy)))
        this.AddHandler(DragDrop.DropEvent, EventHandler<DragEventArgs>(fun _ e ->
            match e.DataTransfer.TryGetFiles() with
            | null -> ()
            | files ->
                match files |> Seq.tryPick (fun f -> match f.TryGetLocalPath() with null -> None | p -> Some p) with
                | Some p -> this.OpenFileAsync p |> ignore
                | None -> showMessage "Only local files can be opened."))

        setCentre Welcome
        refreshState ()

    // ---- hooks replaceable by tests ------------------------------------------------------------------

    /// Ask for a password: file name, whether the last one was wrong. None = cancel.
    member val AskPassword: string -> bool -> Task<string option> =
        (fun name wrong -> Dialogs.askPassword this name wrong) with get, set

    /// Confirm opening an external link. Shows the full address.
    member val ConfirmLink: System.Uri -> Task<bool> =
        (fun uri ->
            Dialogs.confirm this "Open link?" "This document links to an outside address. Open it in your browser or mail app?"
                uri.AbsoluteUri "Open link") with get, set

    member val LaunchUri: System.Uri -> Task<bool> =
        (fun uri -> this.Launcher.LaunchUriAsync uri) with get, set

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

    member _.ShowOpenDialog() : Task =
        task {
            let options =
                FilePickerOpenOptions(
                    Title = "Open PDF",
                    AllowMultiple = false,
                    FileTypeFilter = [| FilePickerFileType("PDF documents", Patterns = [| "*.pdf" |]); FilePickerFileTypes.All |]
                )
            let! files = this.StorageProvider.OpenFilePickerAsync options
            if files.Count > 0 then
                match files[0].TryGetLocalPath() with
                | null -> showMessage "Only local files can be opened."
                | p -> do! this.OpenFileAsync p
        }

    /// Close the current document and return to the welcome panel.
    member _.CloseDocument() =
        openGeneration <- openGeneration + 1
        let old = canvas.Document
        closeSearch ()
        canvas.SetDocument null
        labels <- [||]
        setOutline []
        noticeBar.IsVisible <- false
        this.Title <- "PdfButler"
        setCentre Welcome
        refreshState ()
        if not (isNull old) then old.CloseAsync() |> ignore

    /// Open a file, asking for a password if needed. A newer open supersedes an older one.
    member _.OpenFileAsync(path: string) : Task =
        openGeneration <- openGeneration + 1
        let gen = openGeneration
        let name = fileName path
        Diagnostics.mark "open-start"
        let failed (message: string) =
            let old = canvas.Document
            closeSearch ()
            canvas.SetDocument null
            labels <- [||]
            setOutline []
            noticeBar.IsVisible <- false
            this.Title <- $"{name} — PdfButler"
            setCentre (OpenFailed(name, message))
            refreshState ()
            if not (isNull old) then old.CloseAsync() |> ignore

        let attach (d: PdfDocument) =
            Diagnostics.mark "document-opened"
            let old = canvas.Document
            closeSearch ()
            labels <- d.Info.Pages |> Array.map (fun p -> p.Label)
            setOutline []
            setCentre DocumentShown
            // Detach the old document from the view before closing it.
            canvas.SetDocument d
            if not (isNull old) then old.CloseAsync() |> ignore
            this.Title <- $"{name} — PdfButler"
            showNotices d.Info
            refreshState ()
            focusCanvas ()
            d.GetOutlineAsync(CancellationToken.None).ContinueWith(fun (t: Task<OutlineItem list>) ->
                Dispatcher.UIThread.Post(fun () ->
                    if obj.ReferenceEquals(canvas.Document, d) && t.IsCompletedSuccessfully then setOutline t.Result))
            |> ignore

        task {
            let mutable password: string option = None
            let mutable finished = false
            while not finished do
                let! result =
                    task {
                        try
                            let! r = engine.OpenAsync(path, password, CancellationToken.None)
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

    // ---- keyboard -------------------------------------------------------------------------------------

    override this.OnKeyDown(e: KeyEventArgs) =
        base.OnKeyDown e
        if not e.Handled then
            let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control
            let shift = e.KeyModifiers.HasFlag KeyModifiers.Shift
            let alt = e.KeyModifiers.HasFlag KeyModifiers.Alt
            let inText = (e.Source :? TextBox)
            let doc = hasDoc ()
            let go (msg: Viewer.Msg) = if doc then canvas.Dispatch msg
            let isPlus = e.Key = Key.OemPlus || e.Key = Key.Add
            let isMinus = e.Key = Key.OemMinus || e.Key = Key.Subtract
            let handled =
                match e.Key with
                // Shortcuts that work everywhere, including while typing in a box.
                | Key.O when ctrl -> this.ShowOpenDialog() |> ignore; true
                | Key.W when ctrl -> this.CloseDocument(); true
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
                | Key.Space -> go (if shift then Viewer.PageUp else Viewer.PageDown); true
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

    override this.OnClosed(e) =
        base.OnClosed e
        let d = canvas.Document
        canvas.SetDocument null
        if not (isNull d) then d.CloseAsync() |> ignore
