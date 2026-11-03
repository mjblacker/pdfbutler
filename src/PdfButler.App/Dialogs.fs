/// Small modal dialogs built in code.
module PdfButler.App.Dialogs

open System.Threading.Tasks
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open PdfButler.Core

let private button (text: string) (isDefault: bool) (isCancel: bool) =
    let b = Button(Content = text, MinWidth = 88.0, IsDefault = isDefault, IsCancel = isCancel, HorizontalContentAlignment = HorizontalAlignment.Center)
    AutomationProperties.SetName(b, text)
    b

let private dialogWindow (title: string) (content: Control) =
    Window(
        Title = title,
        Content = Border(Padding = Thickness 20.0, Child = content),
        SizeToContent = SizeToContent.WidthAndHeight,
        CanResize = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        MinWidth = 360.0
    )

/// Ask for a document password. None = cancelled. The password is never logged or stored.
let askPassword (owner: Window) (fileName: string) (wrongBefore: bool) : Task<string option> =
    let message =
        TextBlock(
            Text = (if wrongBefore then $"The password was not correct. Enter the password for “{fileName}”:"
                    else $"“{fileName}” is password protected. Enter the password to open it:"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420.0
        )
    let box = TextBox(PasswordChar = '•', MinWidth = 320.0)
    AutomationProperties.SetName(box, "Password")
    let ok = button "Open" true false
    let cancel = button "Cancel" false true
    let buttons = StackPanel(Orientation = Orientation.Horizontal, Spacing = 8.0, HorizontalAlignment = HorizontalAlignment.Right)
    buttons.Children.Add ok
    buttons.Children.Add cancel
    let panel = StackPanel(Spacing = 12.0)
    panel.Children.Add message
    panel.Children.Add box
    panel.Children.Add buttons
    let w = dialogWindow "Password required" panel
    let mutable result = None
    ok.Click.Add(fun _ ->
        result <- Some(if isNull box.Text then "" else box.Text)
        w.Close())
    cancel.Click.Add(fun _ -> w.Close())
    w.Opened.Add(fun _ -> box.Focus() |> ignore)
    task {
        do! w.ShowDialog(owner)
        return result
    }

/// Yes/no confirmation. Returns true when the user accepts.
let confirm (owner: Window) (title: string) (message: string) (detail: string) (acceptText: string) : Task<bool> =
    let panel = StackPanel(Spacing = 12.0, MaxWidth = 480.0)
    panel.Children.Add(TextBlock(Text = message, TextWrapping = TextWrapping.Wrap))
    if detail <> "" then
        let d = SelectableTextBlock(Text = detail, TextWrapping = TextWrapping.Wrap, FontFamily = FontFamily "Consolas,Menlo,monospace")
        AutomationProperties.SetName(d, "Details")
        panel.Children.Add(Border(Child = d, Padding = Thickness 8.0, Background = SolidColorBrush(Color.FromArgb(24uy, 128uy, 128uy, 128uy))))
    let ok = button acceptText false false
    let cancel = button "Cancel" true true
    let buttons = StackPanel(Orientation = Orientation.Horizontal, Spacing = 8.0, HorizontalAlignment = HorizontalAlignment.Right)
    buttons.Children.Add ok
    buttons.Children.Add cancel
    panel.Children.Add buttons
    let w = dialogWindow title panel
    let mutable result = false
    ok.Click.Add(fun _ -> result <- true; w.Close())
    cancel.Click.Add(fun _ -> w.Close())
    w.Opened.Add(fun _ -> cancel.Focus() |> ignore)
    task {
        do! w.ShowDialog(owner)
        return result
    }

let info (owner: Window) (title: string) (message: string) : Task =
    let panel = StackPanel(Spacing = 12.0, MaxWidth = 480.0)
    panel.Children.Add(TextBlock(Text = message, TextWrapping = TextWrapping.Wrap))
    let ok = button "OK" true true
    ok.HorizontalAlignment <- HorizontalAlignment.Right
    panel.Children.Add ok
    let w = dialogWindow title panel
    ok.Click.Add(fun _ -> w.Close())
    w.ShowDialog(owner)

// ---- settings --------------------------------------------------------------------------------------

type SettingsControls =
    { Window: Window
      TabLayout: ComboBox
      MaxOpenFiles: NumericUpDown
      ConfirmClose: CheckBox
      RestoreSession: CheckBox
      Theme: ComboBox
      DefaultZoom: ComboBox
      Reset: Button }

let private layouts = [| Settings.Top, "Tabs across the top"; Settings.Sidebar, "Open files in a sidebar" |]
let private themes = [| Settings.FollowSystem, "Match system"; Settings.Light, "Light"; Settings.Dark, "Dark" |]
let private zooms = [| Settings.ActualSize, "Actual size (100%)"; Settings.FitWidth, "Fit width"; Settings.FitPage, "Fit page" |]

/// Build the settings window. Every change is passed to `apply` straight away (no OK button).
let settingsWindow (current: Settings.Settings) (apply: Settings.Settings -> unit) : SettingsControls =
    let combo (name: string) (choices: ('T * string)[]) =
        let c = ComboBox(MinWidth = 220.0, HorizontalAlignment = HorizontalAlignment.Stretch)
        for _, label in choices do c.Items.Add label |> ignore
        AutomationProperties.SetName(c, name)
        c
    let indexOf (choices: ('T * string)[]) value = choices |> Array.findIndex (fst >> (=) value)
    let layout = combo "Tab layout" layouts
    let theme = combo "Theme" themes
    let zoom = combo "Default zoom" zooms
    let maxOpen = NumericUpDown(Minimum = decimal Settings.minOpenFiles, Maximum = decimal Settings.maxOpenFilesLimit,
                                Increment = 1m, FormatString = "0", MinWidth = 140.0, HorizontalAlignment = HorizontalAlignment.Left)
    AutomationProperties.SetName(maxOpen, "Maximum open PDFs")
    let confirmClose = CheckBox(Content = "Ask before closing the window when more than one PDF is open")
    let restore = CheckBox(Content = "Reopen the PDFs that were open last time")
    let note (text: string) = TextBlock(Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12.0, MaxWidth = 420.0)
    let heading (text: string) = TextBlock(Text = text, FontWeight = FontWeight.SemiBold, Margin = Thickness(0.0, 6.0, 0.0, 0.0))
    let labelled (text: string) (c: Control) =
        let g = Grid(ColumnDefinitions = ColumnDefinitions("150,*"))
        let l = TextBlock(Text = text, VerticalAlignment = VerticalAlignment.Center)
        Grid.SetColumn(c, 1)
        g.Children.Add l
        g.Children.Add c
        g
    let reset = button "Reset to defaults" false false
    let close = button "Close" true true
    let buttons = Grid(ColumnDefinitions = ColumnDefinitions("*,Auto"), Margin = Thickness(0.0, 8.0, 0.0, 0.0))
    Grid.SetColumn(close, 1)
    buttons.Children.Add reset
    buttons.Children.Add close
    let panel = StackPanel(Spacing = 10.0, MinWidth = 440.0)
    for c in
        [ heading "Tabs" :> Control; labelled "Show open PDFs as" layout; labelled "Maximum open PDFs" maxOpen
          note "Each open PDF runs in its own small helper process. PDFs reopened from last time load when you first switch to them."
          heading "Closing and starting"; confirmClose; restore
          note "Only file locations and page numbers are remembered, never passwords."
          heading "View"; labelled "Theme" theme; labelled "Default zoom" zoom
          note "The default zoom applies to PDFs opened from now on."
          buttons ] do
        panel.Children.Add c
    let w = dialogWindow "Settings" panel
    let mutable state = Settings.normalize current
    let mutable loading = false
    let show (s: Settings.Settings) =
        loading <- true
        layout.SelectedIndex <- indexOf layouts s.TabLayout
        theme.SelectedIndex <- indexOf themes s.Theme
        zoom.SelectedIndex <- indexOf zooms s.DefaultZoom
        maxOpen.Value <- System.Nullable(decimal s.MaxOpenFiles)
        confirmClose.IsChecked <- System.Nullable s.ConfirmCloseMultiple
        restore.IsChecked <- System.Nullable s.RestoreSession
        loading <- false
    let change (f: Settings.Settings -> Settings.Settings) =
        if not loading then
            let next = Settings.normalize (f state)
            if next <> state then
                state <- next
                apply next
    let pick (choices: ('T * string)[]) (c: ComboBox) = if c.SelectedIndex >= 0 then Some(fst choices[c.SelectedIndex]) else None
    show state
    layout.SelectionChanged.Add(fun _ -> pick layouts layout |> Option.iter (fun v -> change (fun s -> { s with TabLayout = v })))
    theme.SelectionChanged.Add(fun _ -> pick themes theme |> Option.iter (fun v -> change (fun s -> { s with Theme = v })))
    zoom.SelectionChanged.Add(fun _ -> pick zooms zoom |> Option.iter (fun v -> change (fun s -> { s with DefaultZoom = v })))
    maxOpen.ValueChanged.Add(fun _ ->
        if maxOpen.Value.HasValue then
            let n = int (System.Math.Round maxOpen.Value.Value)
            change (fun s -> { s with MaxOpenFiles = n }))
    confirmClose.IsCheckedChanged.Add(fun _ -> change (fun s -> { s with ConfirmCloseMultiple = confirmClose.IsChecked = System.Nullable true }))
    restore.IsCheckedChanged.Add(fun _ -> change (fun s -> { s with RestoreSession = restore.IsChecked = System.Nullable true }))
    reset.Click.Add(fun _ ->
        show Settings.defaults
        change (fun _ -> Settings.defaults))
    close.Click.Add(fun _ -> w.Close())
    { Window = w; TabLayout = layout; MaxOpenFiles = maxOpen; ConfirmClose = confirmClose
      RestoreSession = restore; Theme = theme; DefaultZoom = zoom; Reset = reset }

type CloseChoice = Cancel | Current | All

let closeDocuments (owner: Window) (count: int) : Task<CloseChoice> =
    let panel = StackPanel(Spacing = 16.0)
    panel.Children.Add(TextBlock(Text = $"You have {count} PDFs open. Close the current tab or all tabs?"))
    let buttons = StackPanel(Orientation = Orientation.Horizontal, Spacing = 8.0)
    panel.Children.Add buttons
    let w = dialogWindow "Close PDFs" panel
    let mutable result = Cancel
    for label, choice in [ "Close current tab", Current; "Close all tabs", All; "Cancel", Cancel ] do
        let b = button label (choice = Cancel) (choice = Cancel)
        b.Click.Add(fun _ -> result <- choice; w.Close())
        buttons.Children.Add b
    task {
        do! w.ShowDialog(owner)
        return result
    }
