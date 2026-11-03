/// Small modal dialogs built in code.
module PdfButler.App.Dialogs

open System.Threading.Tasks
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media

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
