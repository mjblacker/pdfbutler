/// Window-level tab ownership. Each pane owns one document and its independent UI state.
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
open PdfButler.Pdfium
open PdfButler.App.DocumentPane

let maxTabs = 10

type private DocumentTab =
    { Path: string
      Pane: DocumentPane
      Header: Button
      Close: Button
      Surface: Border }

type MainWindow(engine: PdfEngine) as this =
    inherit Window()

    let tabs = ResizeArray<DocumentTab>()
    let headers = StackPanel(Orientation = Orientation.Horizontal, Spacing = 4.0)
    let body = Grid()
    let topSurface = Border(BorderThickness = Thickness(0.0, 0.0, 0.0, 1.0))
    let mutable selected = -1
    let mutable closed = false
    let mutable closeApproved = false
    let mutable closePromptPending = false
    let createPane () =
        let p = DocumentPane(this, engine, fun () -> this.ShowOpenDialog() |> ignore)
        p.AskPassword <- fun name wrong -> this.AskPassword name wrong
        p.ConfirmLink <- fun uri -> this.ConfirmLink uri
        p.LaunchUri <- fun uri -> this.LaunchUri uri
        p
    let welcome = createPane ()
    let current () = if selected < 0 then welcome else tabs[selected].Pane

    let refreshHeaders () =
        let dark = this.ActualThemeVariant = ThemeVariant.Dark
        let brush (color: string) = SolidColorBrush(Color.Parse color)
        topSurface.Background <- brush (if dark then "#182832" else "#f5f8fa")
        topSurface.BorderBrush <- brush (if dark then "#344650" else "#dce5e9")
        for i in 0 .. tabs.Count - 1 do
            let isSelected = (i = selected)
            let tab = tabs[i]
            tab.Surface.Background <- brush (if isSelected then (if dark then "#2b4350" else "#ffffff") else "#00808080")
            tab.Surface.BorderBrush <- brush (if isSelected then "#65c9bc" else "#00808080")
            let foreground = brush (if dark then "#edf5f6" else "#163342")
            tab.Header.Foreground <- foreground
            tab.Close.Foreground <- foreground
        this.Title <- if selected < 0 then "PdfButler" else $"{Path.GetFileName tabs[selected].Path} — PdfButler"

    let select index =
        if index >= 0 && index < tabs.Count then
            if index <> selected then
                (current ()).SetActive false
                body.Children.Clear()
                selected <- index
                body.Children.Add tabs[index].Pane
            tabs[index].Pane.SetActive true
            refreshHeaders ()
            let header = tabs[index].Header
            Dispatcher.UIThread.Post((fun () -> header.BringIntoView()), DispatcherPriority.Loaded)

    do
        this.Title <- "PdfButler"
        this.Width <- 1180.0
        this.Height <- 850.0
        this.MinWidth <- 440.0
        this.MinHeight <- 300.0
        let assets = typeof<MainWindow>.Assembly
        use ico = assets.GetManifestResourceStream("PdfButler.icon.ico")
        this.Icon <- WindowIcon(ico)
        headers.Margin <- Thickness(8.0, 5.0, 8.0, 0.0)
        AutomationProperties.SetName(headers, "Open documents")
        let tabStrip = ScrollViewer(Content = headers, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled)
        topSurface.Child <- tabStrip
        let root = DockPanel()
        DockPanel.SetDock(topSurface, Dock.Top)
        root.Children.Add topSurface
        root.Children.Add body
        body.Children.Add welcome
        this.Content <- root
        this.ActualThemeVariantChanged.Add(fun _ -> refreshHeaders ())
        refreshHeaders ()

        // Tunnel these shortcuts before text boxes/tab buttons handle them.
        this.AddHandler(InputElement.KeyDownEvent, EventHandler<KeyEventArgs>(fun _ e ->
            if e.KeyModifiers.HasFlag KeyModifiers.Control then
                match e.Key with
                | Key.O -> this.ShowOpenDialog() |> ignore; e.Handled <- true
                | Key.W -> this.CloseDocument(); e.Handled <- true
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
                let paths = files |> Seq.choose (fun f -> Option.ofObj(f.TryGetLocalPath())) |> Seq.truncate maxTabs |> Seq.toArray
                this.OpenFilesAsync paths |> ignore))

    member val AskPassword: string -> bool -> Task<string option> =
        (fun name wrong -> Dialogs.askPassword this name wrong) with get, set
    member val ConfirmLink: Uri -> Task<bool> =
        (fun uri -> Dialogs.confirm this "Open link?" "Open this address in your browser or mail app?" uri.AbsoluteUri "Open link") with get, set
    member val LaunchUri: Uri -> Task<bool> = (fun uri -> this.Launcher.LaunchUriAsync uri) with get, set
    member val ConfirmClose: int -> Task<Dialogs.CloseChoice> = Dialogs.closeDocuments this with get, set

    member _.TabCount = tabs.Count
    member _.SelectedTabIndex = selected
    member _.TabNames = tabs |> Seq.map (fun t -> Path.GetFileName t.Path) |> Seq.toArray
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

    member _.OpenFileAsync(path: string) : Task =
        if closed then Task.CompletedTask
        else
            let fullPath = try Path.GetFullPath path with _ -> path
            let comparison = if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
            match tabs |> Seq.tryFindIndex (fun t -> String.Equals(t.Path, fullPath, comparison)) with
            | Some i -> select i; Task.CompletedTask
            | None when tabs.Count >= maxTabs ->
                (current ()).ShowMessage $"Up to {maxTabs} PDFs can be open. Close a tab before opening another."
                Task.CompletedTask
            | None ->
                // Reuse the initial canvas so --measure's first-frame hook stays attached.
                let pane = if tabs.Count = 0 then welcome else createPane ()
                let label = TextBlock(Text = Path.GetFileName fullPath, MaxWidth = 180.0,
                                      TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center)
                let header = Button(Content = label, Padding = Thickness(12.0, 8.0, 36.0, 8.0),
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
                let surface = Border(Child = group, CornerRadius = CornerRadius(7.0, 7.0, 0.0, 0.0),
                                     BorderThickness = Thickness(0.0, 0.0, 0.0, 2.0))
                let tab = { Path = fullPath; Pane = pane; Header = header; Close = close; Surface = surface }
                tabs.Add tab
                headers.Children.Add surface
                header.Click.Add(fun _ -> select (tabs.IndexOf tab))
                close.Click.Add(fun _ -> this.CloseTab(tabs.IndexOf tab))
                select (tabs.Count - 1)
                pane.OpenFileAsync fullPath

    member _.OpenFilesAsync(paths: string[]) : Task =
        task {
            for path in paths |> Array.truncate maxTabs do
                do! this.OpenFileAsync path
        }

    member _.ShowOpenDialog() : Task =
        task {
            let options = FilePickerOpenOptions(Title = "Open PDFs", AllowMultiple = true,
                              FileTypeFilter = [| FilePickerFileType("PDF documents", Patterns = [| "*.pdf" |]); FilePickerFileTypes.All |])
            let! files = this.StorageProvider.OpenFilePickerAsync options
            let paths = files |> Seq.choose (fun f -> Option.ofObj(f.TryGetLocalPath())) |> Seq.truncate maxTabs |> Seq.toArray
            do! this.OpenFilesAsync paths
        }

    member _.CloseTab(index) =
        if index >= 0 && index < tabs.Count then
            let wasSelected = index = selected
            tabs[index].Pane.SetActive false
            tabs[index].Pane.Close()
            tabs.RemoveAt index
            headers.Children.RemoveAt index
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

    member _.CloseDocument() = this.CloseTab selected

    override _.OnClosing(e) =
        base.OnClosing e
        if not e.Cancel && not closeApproved && tabs.Count > 1 then
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
        for tab in tabs do tab.Pane.Close()
        welcome.Close()
        base.OnClosed e
