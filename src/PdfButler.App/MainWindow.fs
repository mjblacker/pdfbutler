/// Window-level tab ownership. Each pane owns one document and its independent UI state.
/// Tabs show either across the top (shrinking, then arrow-scrolled) or as a list in a sidebar.
module PdfButler.App.MainWindow

open System
open System.IO
open System.Threading.Tasks
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Interactivity
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.Styling
open Avalonia.Threading
open Avalonia.VisualTree
open PdfButler.Core
open PdfButler.Pdfium
open PdfButler.App.RendererProcess
open PdfButler.App.DocumentPane
open PdfButler.App.TabStripPanel

type private DocumentTab =
    { Path: string
      Pane: DocumentPane
      Header: Button
      Close: Button
      Surface: Border
      /// Restored tabs open when first selected: Some page until then.
      mutable Pending: int option }

let private iconButton (icon: string) (name: string) (tip: string) =
    let i = Icons.create icon
    i.Width <- 14.0
    i.Height <- 14.0
    let b = Button(Content = i, Padding = Thickness(6.0), Width = 30.0, Height = 30.0, VerticalAlignment = VerticalAlignment.Center,
                   Background = Brushes.Transparent, BorderThickness = Thickness(0.0))
    AutomationProperties.SetName(b, name)
    ToolTip.SetTip(b, tip)
    b

type MainWindow(engine: RendererEngine) as this =
    inherit Window()

    let tabs = ResizeArray<DocumentTab>()
    let strip = TabStripPanel()
    let sideList = StackPanel(Spacing = 2.0, Margin = Thickness(6.0, 0.0, 6.0, 6.0))
    let body = Grid()
    let topSurface = Border(BorderThickness = Thickness(0.0, 0.0, 0.0, 1.0))
    let sidebar = Border(Width = 240.0, BorderThickness = Thickness(0.0, 0.0, 1.0, 0.0), IsVisible = false)
    let sideCount = TextBlock(VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Margin = Thickness(6.0, 0.0))
    let scrollBack = iconButton "Scroll left" "Scroll tabs left" "Show earlier tabs"
    let scrollForward = iconButton "Scroll right" "Scroll tabs right" "Show later tabs"
    let allTabs = iconButton "All tabs" "All open PDFs" "List all open PDFs"
    let topSettings = iconButton "Settings" "Settings" "Settings (Ctrl+,)"
    let sideSettings = iconButton "Settings" "Settings" "Settings (Ctrl+,)"
    let tabFilter = TextBox(PlaceholderText = "Filter open PDFs", Width = 320.0)
    let tabList = ListBox(MaxHeight = 420.0, Width = 320.0)
    let tabFlyout = Flyout(Placement = PlacementMode.BottomEdgeAlignedRight)
    let mutable settings = Settings.defaults
    let mutable settingsDialog: Window option = None
    let mutable selected = -1
    let mutable closed = false
    let mutable closeApproved = false
    let mutable closePromptPending = false
    let createPane () =
        let p = DocumentPane(this, engine, fun () -> this.ShowOpenDialog() |> ignore)
        p.AskPassword <- fun name wrong -> this.AskPassword name wrong
        p.ConfirmLink <- fun uri -> this.ConfirmLink uri
        p.LaunchUri <- fun uri -> this.LaunchUri uri
        p.DefaultZoom <- settings.DefaultZoom
        p
    let welcome = createPane ()
    let current () = if selected < 0 then welcome else tabs[selected].Pane
    let isSidebar () = settings.TabLayout = Settings.Sidebar
    let tabName (t: DocumentTab) = Path.GetFileName t.Path

    let refreshHeaders () =
        let dark = this.ActualThemeVariant = ThemeVariant.Dark
        let brush (color: string) = SolidColorBrush(Color.Parse color)
        let chrome = brush (if dark then "#182832" else "#f5f8fa")
        let line = brush (if dark then "#344650" else "#dce5e9")
        topSurface.Background <- chrome
        topSurface.BorderBrush <- line
        sidebar.Background <- chrome
        sidebar.BorderBrush <- line
        let side = isSidebar ()
        let foreground = brush (if dark then "#edf5f6" else "#163342")
        for i in 0 .. tabs.Count - 1 do
            let isSelected = (i = selected)
            let tab = tabs[i]
            tab.Surface.CornerRadius <- if side then CornerRadius(6.0) else CornerRadius(7.0, 7.0, 0.0, 0.0)
            tab.Surface.BorderThickness <- if side then Thickness(3.0, 0.0, 0.0, 0.0) else Thickness(0.0, 0.0, 0.0, 2.0)
            tab.Surface.Background <- brush (if isSelected then (if dark then "#2b4350" else "#ffffff") else "#00808080")
            tab.Surface.BorderBrush <- brush (if isSelected then "#65c9bc" else "#00808080")
            tab.Header.Foreground <- foreground
            tab.Close.Foreground <- foreground
        for b in [ scrollBack; scrollForward; allTabs; topSettings; sideSettings ] do b.Foreground <- foreground
        sideCount.Text <- if tabs.Count = 0 then "" else string tabs.Count
        this.Title <- if selected < 0 then "PdfButler" else $"{tabName tabs[selected]} — PdfButler"

    let refreshArrows () =
        let show = strip.Overflows
        scrollBack.IsVisible <- show
        scrollForward.IsVisible <- show
        scrollBack.IsEnabled <- strip.CanScrollBack
        scrollForward.IsEnabled <- strip.CanScrollForward
        allTabs.IsVisible <- tabs.Count > 1

    let revealSelected () =
        if selected >= 0 then
            if isSidebar () then
                let header = tabs[selected].Header
                Dispatcher.UIThread.Post((fun () -> header.BringIntoView()), DispatcherPriority.Loaded)
            else strip.Reveal selected

    /// Put every tab surface in the container for the current layout.
    let applyLayout () =
        let side = isSidebar ()
        strip.Children.Clear()
        sideList.Children.Clear()
        let target = if side then sideList.Children else strip.Children
        for t in tabs do target.Add t.Surface
        topSurface.IsVisible <- not side
        sidebar.IsVisible <- side
        refreshHeaders ()
        refreshArrows ()
        revealSelected ()

    let select index =
        if index >= 0 && index < tabs.Count then
            if index <> selected then
                (current ()).SetActive false
                body.Children.Clear()
                selected <- index
                body.Children.Add tabs[index].Pane
            let tab = tabs[index]
            tab.Pane.SetActive true
            match tab.Pending with
            | Some page ->
                tab.Pending <- None
                tab.Pane.OpenFileAsync(tab.Path, page) |> ignore
            | None -> ()
            refreshHeaders ()
            revealSelected ()

    let fillTabList () =
        let filter = if isNull tabFilter.Text then "" else tabFilter.Text.Trim()
        tabList.Items.Clear()
        for i in 0 .. tabs.Count - 1 do
            let t = tabs[i]
            let name = tabName t
            if filter = "" || name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) then
                let text = StackPanel(Spacing = 1.0)
                text.Children.Add(TextBlock(Text = name, TextTrimming = TextTrimming.CharacterEllipsis,
                                            FontWeight = (if i = selected then FontWeight.SemiBold else FontWeight.Normal)))
                text.Children.Add(TextBlock(Text = Path.GetDirectoryName t.Path, TextTrimming = TextTrimming.PathSegmentEllipsis,
                                            FontSize = 11.0, Opacity = 0.65))
                let item = ListBoxItem(Content = text, Tag = box i)
                AutomationProperties.SetName(item, name)
                ToolTip.SetTip(item, t.Path)
                tabList.Items.Add item |> ignore
                if i = selected then tabList.SelectedItem <- item
        if isNull tabList.SelectedItem && tabList.ItemCount > 0 then tabList.SelectedIndex <- 0

    let chooseListed () =
        match tabList.SelectedItem with
        | :? ListBoxItem as item ->
            tabFlyout.Hide()
            select (unbox<int> item.Tag)
        | _ -> ()

    do
        this.Title <- "PdfButler"
        this.Width <- 1180.0
        this.Height <- 850.0
        this.MinWidth <- 440.0
        this.MinHeight <- 300.0
        let assets = typeof<MainWindow>.Assembly
        use ico = assets.GetManifestResourceStream("PdfButler.icon.ico")
        this.Icon <- WindowIcon(ico)

        // Top layout: strip, then arrows / all tabs / settings on the right.
        strip.Margin <- Thickness(8.0, 5.0, 4.0, 0.0)
        AutomationProperties.SetName(strip, "Open documents")
        let topButtons = StackPanel(Orientation = Orientation.Horizontal, Spacing = 2.0, Margin = Thickness(0.0, 3.0, 6.0, 0.0),
                                    VerticalAlignment = VerticalAlignment.Center)
        for b in [ scrollBack; scrollForward; allTabs; topSettings ] do topButtons.Children.Add b
        let topGrid = Grid(ColumnDefinitions = ColumnDefinitions("*,Auto"))
        Grid.SetColumn(topButtons, 1)
        topGrid.Children.Add strip
        topGrid.Children.Add topButtons
        topSurface.Child <- topGrid
        strip.LayoutChanged.Add(fun () -> refreshArrows ())
        scrollBack.Click.Add(fun _ -> strip.ScrollBySteps -2.0)
        scrollForward.Click.Add(fun _ -> strip.ScrollBySteps 2.0)
        strip.PointerWheelChanged.Add(fun e ->
            let delta = if abs e.Delta.X > abs e.Delta.Y then e.Delta.X else e.Delta.Y
            strip.ScrollBySteps(-delta)
            e.Handled <- true)

        // All-tabs dropdown with a filter.
        AutomationProperties.SetName(tabFilter, "Filter open PDFs")
        AutomationProperties.SetName(tabList, "Open PDFs")
        let flyoutPanel = StackPanel(Spacing = 6.0)
        flyoutPanel.Children.Add tabFilter
        flyoutPanel.Children.Add tabList
        tabFlyout.Content <- flyoutPanel
        allTabs.Flyout <- tabFlyout
        tabFlyout.Opened.Add(fun _ ->
            tabFilter.Text <- ""
            fillTabList ()
            tabFilter.Focus() |> ignore)
        tabFilter.TextChanged.Add(fun _ -> fillTabList ())
        tabFilter.KeyDown.Add(fun e ->
            match e.Key with
            | Key.Enter -> chooseListed (); e.Handled <- true
            | Key.Down when tabList.ItemCount > 0 ->
                tabList.Focus() |> ignore
                match tabList.ContainerFromIndex(max 0 tabList.SelectedIndex) with
                | null -> ()
                | c -> c.Focus() |> ignore
                e.Handled <- true
            | _ -> ())
        tabList.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                chooseListed ()
                e.Handled <- true)
        tabList.Tapped.Add(fun e ->
            match e.Source with
            | :? Visual as v when not (isNull (v.FindAncestorOfType<ListBoxItem>(true))) -> chooseListed ()
            | _ -> ())

        // Sidebar layout: heading with count and settings, then the scrolling list.
        let sideHeader = Grid(ColumnDefinitions = ColumnDefinitions("Auto,*,Auto"), Margin = Thickness(12.0, 6.0, 6.0, 4.0))
        let sideTitle = TextBlock(Text = "Open PDFs", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center)
        Grid.SetColumn(sideCount, 1)
        Grid.SetColumn(sideSettings, 2)
        sideHeader.Children.Add sideTitle
        sideHeader.Children.Add sideCount
        sideHeader.Children.Add sideSettings
        AutomationProperties.SetName(sideList, "Open documents")
        let sideScroll = ScrollViewer(Content = sideList, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                                      VerticalScrollBarVisibility = ScrollBarVisibility.Auto)
        let sideDock = DockPanel()
        DockPanel.SetDock(sideHeader, Dock.Top)
        sideDock.Children.Add sideHeader
        sideDock.Children.Add sideScroll
        sidebar.Child <- sideDock

        for b in [ topSettings; sideSettings ] do b.Click.Add(fun _ -> this.ShowSettings())

        let root = DockPanel()
        DockPanel.SetDock(topSurface, Dock.Top)
        DockPanel.SetDock(sidebar, Dock.Left)
        root.Children.Add topSurface
        root.Children.Add sidebar
        root.Children.Add body
        body.Children.Add welcome
        this.Content <- root
        this.ActualThemeVariantChanged.Add(fun _ -> refreshHeaders ())
        applyLayout ()

        // Tunnel these shortcuts before text boxes/tab buttons handle them.
        this.AddHandler(InputElement.KeyDownEvent, EventHandler<KeyEventArgs>(fun _ e ->
            if e.KeyModifiers.HasFlag KeyModifiers.Control then
                match e.Key with
                | Key.O -> this.ShowOpenDialog() |> ignore; e.Handled <- true
                | Key.W -> this.CloseDocument(); e.Handled <- true
                | Key.OemComma -> this.ShowSettings(); e.Handled <- true
                | Key.Tab ->
                    if tabs.Count > 0 then
                        let step = if e.KeyModifiers.HasFlag KeyModifiers.Shift then -1 else 1
                        select ((selected + step + tabs.Count) % tabs.Count)
                    e.Handled <- true
                | _ -> ()), RoutingStrategies.Tunnel)

        DragDrop.SetAllowDrop(this, true)
        this.AddHandler(DragDrop.DragOverEvent, EventHandler<DragEventArgs>(fun _ e ->
            e.DragEffects <- if isNull (e.DataTransfer.TryGetFiles()) then DragDropEffects.None else DragDropEffects.Copy))
        this.AddHandler(DragDrop.DropEvent, EventHandler<DragEventArgs>(fun _ e ->
            match e.DataTransfer.TryGetFiles() with
            | null -> ()
            | files ->
                let paths = files |> Seq.choose (fun f -> Option.ofObj(f.TryGetLocalPath())) |> Seq.toArray
                this.OpenFilesAsync paths |> ignore))

    /// Add a tab without selecting it. `pending` = restored tab that opens on first selection.
    member private _.AddTab(fullPath: string, pending: int option) =
        // Reuse the initial canvas so --measure's first-frame hook stays attached.
        let pane = if tabs.Count = 0 then welcome else createPane ()
        let label = TextBlock(Text = Path.GetFileName fullPath, TextTrimming = TextTrimming.CharacterEllipsis,
                              VerticalAlignment = VerticalAlignment.Center)
        let header = Button(Content = label, Padding = Thickness(12.0, 8.0, 36.0, 8.0), HorizontalAlignment = HorizontalAlignment.Stretch,
                            HorizontalContentAlignment = HorizontalAlignment.Left,
                            Background = Brushes.Transparent, BorderThickness = Thickness(0.0))
        ToolTip.SetTip(header, fullPath)
        AutomationProperties.SetName(header, $"Switch to {label.Text}")
        let closeIcon = Icons.create "Close"
        closeIcon.Width <- 12.0
        closeIcon.Height <- 12.0
        let close = Button(Content = closeIcon, Padding = Thickness(5.0), Width = 26.0, Height = 26.0,
                           HorizontalAlignment = HorizontalAlignment.Right, Margin = Thickness(0.0, 0.0, 5.0, 0.0), VerticalAlignment = VerticalAlignment.Center,
                           Background = Brushes.Transparent, BorderThickness = Thickness(0.0))
        AutomationProperties.SetName(close, $"Close {label.Text}")
        ToolTip.SetTip(close, "Close tab (Ctrl+W)")
        let group = Grid()
        group.Children.Add header
        group.Children.Add close
        let surface = Border(Child = group)
        let tab = { Path = fullPath; Pane = pane; Header = header; Close = close; Surface = surface; Pending = pending }
        tabs.Add tab
        (if isSidebar () then sideList.Children else strip.Children).Add surface
        header.Click.Add(fun _ -> select (tabs.IndexOf tab))
        close.Click.Add(fun _ -> this.CloseTab(tabs.IndexOf tab))
        // Middle-click closes, as in browsers.
        header.AddHandler(InputElement.PointerReleasedEvent, EventHandler<PointerReleasedEventArgs>(fun _ e ->
            if e.InitialPressMouseButton = MouseButton.Middle then
                this.CloseTab(tabs.IndexOf tab)
                e.Handled <- true), RoutingStrategies.Bubble, true)
        refreshHeaders ()
        refreshArrows ()
        tab

    member val AskPassword: string -> bool -> Task<string option> =
        (fun name wrong -> Dialogs.askPassword this name wrong) with get, set
    member val ConfirmLink: Uri -> Task<bool> =
        (fun uri -> Dialogs.confirm this "Open link?" "Open this address in your browser or mail app?" uri.AbsoluteUri "Open link") with get, set
    member val LaunchUri: Uri -> Task<bool> = (fun uri -> this.Launcher.LaunchUriAsync uri) with get, set
    member val ConfirmClose: int -> Task<Dialogs.CloseChoice> = Dialogs.closeDocuments this with get, set
    /// Persist settings after a change in the dialog. Tests leave this as a no-op.
    member val SaveSettings: Settings.Settings -> unit = ignore with get, set
    /// Persist the session when the window closes (empty when restoring is off). No-op by default.
    member val SaveSession: Settings.Session -> unit = ignore with get, set

    member _.TabCount = tabs.Count
    member _.SelectedTabIndex = selected
    member _.TabNames = tabs |> Seq.map tabName |> Seq.toArray
    member _.SelectTab(index) = select index
    member _.View = (current ()).View
    member _.Canvas = (current ()).Canvas
    member _.Centre = (current ()).Centre
    member _.StatusPageText = (current ()).StatusPageText
    member _.StatusMessage = (current ()).StatusMessage
    member _.SearchStatusText = (current ()).SearchStatusText
    member _.SearchBox = (current ()).SearchBox
    member _.PageBox = (current ()).PageBox
    member _.ZoomBox = (current ()).ZoomBox
    member _.OutlineTree = (current ()).OutlineTree
    member _.OutlineButton = (current ()).OutlineButton
    member _.IsOutlineShown = (current ()).IsOutlineShown
    member _.NoticeText = (current ()).NoticeText
    member _.IsSearchOpen = (current ()).IsSearchOpen
    member _.RunSearchNow() = (current ()).RunSearchNow()
    // Test hooks for the tab chrome.
    member _.TabStrip = strip
    member _.TabSidebar = sidebar
    member _.ScrollTabsBackButton = scrollBack
    member _.ScrollTabsForwardButton = scrollForward
    member _.AllTabsButton = allTabs
    member _.TabHeader(index: int) = tabs[index].Surface
    member _.IsTabLoaded(index: int) = tabs[index].Pending.IsNone

    member _.Settings = settings

    member _.ApplySettings(value: Settings.Settings) =
        let value = Settings.normalize value
        let layoutChanged = value.TabLayout <> settings.TabLayout
        settings <- value
        welcome.DefaultZoom <- value.DefaultZoom
        for t in tabs do t.Pane.DefaultZoom <- value.DefaultZoom
        match Application.Current with
        | null -> ()
        | app ->
            app.RequestedThemeVariant <-
                match value.Theme with
                | Settings.FollowSystem -> ThemeVariant.Default
                | Settings.Light -> ThemeVariant.Light
                | Settings.Dark -> ThemeVariant.Dark
        if layoutChanged then applyLayout ()

    /// Open (or bring forward) the settings window. Changes apply and save immediately.
    member _.ShowSettings() =
        match settingsDialog with
        | Some w -> w.Activate()
        | None ->
            let controls = Dialogs.settingsWindow settings (fun s -> this.ApplySettings s; this.SaveSettings settings)
            settingsDialog <- Some controls.Window
            controls.Window.Closed.Add(fun _ -> settingsDialog <- None)
            controls.Window.Show(this)

    member _.SettingsDialog = settingsDialog

    member _.SessionSnapshot() : Settings.Session =
        { Tabs =
            [ for t in tabs ->
                  { Settings.Path = t.Path
                    Settings.Page =
                      match t.Pending with
                      | Some p -> p
                      | None -> t.Pane.CurrentPageIndex |> Option.defaultValue 0 } ]
          Selected = selected }

    /// Add the session's tabs that still exist, without opening them; opening happens on selection.
    /// `selectRestored` = false when files from the command line will be shown instead.
    member _.RestoreSessionAsync(session: Settings.Session, selectRestored: bool) : Task =
        task {
            let session = Settings.normalizeSession SettingsStore.pathComparison settings.MaxOpenFiles session
            // Existence checks can block on network drives: keep them off the UI thread.
            let! present =
                Task.Run(fun () ->
                    session.Tabs |> List.mapi (fun i t -> i, t) |> List.filter (fun (_, t) -> try File.Exists t.Path with _ -> false))
            if not closed then
                let mutable toSelect = -1
                for i, t in present do
                    let exists = tabs |> Seq.exists (fun x -> String.Equals(x.Path, t.Path, SettingsStore.pathComparison))
                    if not exists && tabs.Count < settings.MaxOpenFiles then
                        this.AddTab(t.Path, Some t.Page) |> ignore
                        if i = session.Selected || toSelect < 0 then toSelect <- tabs.Count - 1
                if selectRestored && toSelect >= 0 && selected < 0 then select toSelect
        }

    member _.OpenFileAsync(path: string) : Task =
        if closed then Task.CompletedTask
        else
            let fullPath = try Path.GetFullPath path with _ -> path
            match tabs |> Seq.tryFindIndex (fun t -> String.Equals(t.Path, fullPath, SettingsStore.pathComparison)) with
            | Some i -> select i; Task.CompletedTask
            | None when tabs.Count >= settings.MaxOpenFiles ->
                (current ()).ShowMessage $"Up to {settings.MaxOpenFiles} PDFs can be open. Close a tab before opening another, or raise the limit in Settings."
                Task.CompletedTask
            | None ->
                let tab = this.AddTab(fullPath, None)
                select (tabs.Count - 1)
                tab.Pane.OpenFileAsync fullPath

    member _.OpenFilesAsync(paths: string[]) : Task =
        task {
            for path in paths |> Array.truncate Settings.maxOpenFilesLimit do
                do! this.OpenFileAsync path
        }

    member _.ShowOpenDialog() : Task =
        task {
            let options = FilePickerOpenOptions(Title = "Open PDFs", AllowMultiple = true,
                              FileTypeFilter = [| FilePickerFileType("PDF documents", Patterns = [| "*.pdf" |]); FilePickerFileTypes.All |])
            let! files = this.StorageProvider.OpenFilePickerAsync options
            let paths = files |> Seq.choose (fun f -> Option.ofObj(f.TryGetLocalPath())) |> Seq.toArray
            do! this.OpenFilesAsync paths
        }

    member _.CloseTab(index) =
        if index >= 0 && index < tabs.Count then
            let wasSelected = index = selected
            let tab = tabs[index]
            tab.Pane.SetActive false
            tab.Pane.Close()
            tabs.RemoveAt index
            strip.Children.Remove tab.Surface |> ignore
            sideList.Children.Remove tab.Surface |> ignore
            if wasSelected then
                selected <- -1
                body.Children.Clear()
                if tabs.Count = 0 then
                    body.Children.Add welcome
                    welcome.SetActive true
                    refreshHeaders ()
                else select (min index (tabs.Count - 1))
            else
                if index < selected then selected <- selected - 1
                refreshHeaders ()
                (current ()).SetActive true
            refreshArrows ()

    member _.CloseDocument() = this.CloseTab selected

    override _.OnClosing(e) =
        base.OnClosing e
        if not e.Cancel && not closeApproved && settings.ConfirmCloseMultiple && tabs.Count > 1 then
            e.Cancel <- true
            if not closePromptPending then
                closePromptPending <- true
                let target = tabs[selected]
                task {
                    try
                        let! choice = this.ConfirmClose tabs.Count
                        match choice with
                        | Dialogs.Current -> this.CloseTab(tabs.IndexOf target)
                        | Dialogs.All ->
                            closeApproved <- true
                            Dispatcher.UIThread.Post(fun () -> this.Close())
                        | Dialogs.Cancel -> ()
                    finally
                        closePromptPending <- false
                } |> ignore

    override _.OnClosed(e) =
        closed <- true
        (try this.SaveSession(if settings.RestoreSession then this.SessionSnapshot() else Settings.emptySession) with _ -> ())
        settingsDialog |> Option.iter (fun w -> w.Close())
        for tab in tabs do tab.Pane.Close()
        welcome.Close()
        base.OnClosed e
