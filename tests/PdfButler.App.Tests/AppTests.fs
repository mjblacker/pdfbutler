/// End-to-end UI tests: the real main window, real engine, real Skia frames, synthetic PDFs.
module PdfButler.App.Tests.AppTests

open System
open System.IO
open PdfButler.Core
open Avalonia
open Avalonia.Headless
open Avalonia.Controls
open Avalonia.Headless.XUnit
open Avalonia.Input
open Avalonia.Input.Platform
open Avalonia.VisualTree
open Xunit
open PdfButler.App
open PdfButler.App.MainWindow
open PdfButler.App.Tests.Harness
open PdfButler.TestSupport
open PdfButler.TestSupport.SyntheticPdf

let private letter = page 612.0 792.0
let private text x y size s = { X = x; Y = y; Size = size; Text = s }
let private rect x0 y0 x1 y1 gray = { R = box x0 y0 x1 y1; Gray = gray }

/// Black square on page 0 at user space (100,600)-(200,700) => display (100,92)-(200,192).
let private inkDoc =
    { doc
        [ { letter with Rects = [ rect 100.0 600.0 200.0 700.0 0.0 ]; Texts = [ text 72.0 400.0 14.0 "First page" ] }
          { letter with Texts = [ text 72.0 700.0 14.0 "Second page" ] }
          { letter with Texts = [ text 72.0 700.0 14.0 "Third page" ] }
          letter
          letter ] with
        Labels = [ { StartPage = 0; Style = "r"; Prefix = ""; First = 1 }; { StartPage = 2; Style = "D"; Prefix = ""; First = 1 } ] }

let private dark (f: Frame) (p: Point) = f.LumAt p < 60
let private light (f: Frame) (p: Point) = f.LumAt p > 200

[<Fact>]
let ``window placement fits disconnected monitors and changed DPI`` () =
    let saved: WindowPlacement.Placement =
        { X = -3000; Y = 1500; Width = 2400.0; Height = 1400.0; Maximized = true }
    let area = PixelRect(0, 0, 1920, 1040)
    let p = WindowPlacement.constrain area 1.5 saved
    Assert.Equal(0, p.X)
    Assert.True(p.Y >= 0)
    Assert.True(float p.X + p.Width * 1.5 <= 1920.0)
    Assert.True(float p.Y + (p.Height + 40.0) * 1.5 <= 1041.0)
    Assert.True(p.Maximized)
    let left = WindowPlacement.constrain (PixelRect(-1920, 0, 1920, 1080)) 1.0 { saved with X = -1800; Y = 40; Width = 900; Height = 700 }
    Assert.Equal(-1800, left.X)
    Assert.Equal(40, left.Y)

[<Fact>]
let ``window placement JSON round-trips and reads files written by the earlier serializer`` () =
    let p: WindowPlacement.Placement = { X = -1800; Y = 40; Width = 1180.5; Height = 850.0; Maximized = true }
    Assert.Equal(Some p, WindowPlacement.parse (WindowPlacement.serialize p))
    // Format previously produced by JsonSerializer.Serialize.
    let earlier = """{"X":12,"Y":-3,"Width":1180,"Height":850.25,"Maximized":false}"""
    Assert.Equal(Some ({ X = 12; Y = -3; Width = 1180.0; Height = 850.25; Maximized = false }: WindowPlacement.Placement), WindowPlacement.parse earlier)
    Assert.Equal(Some ({ X = 0; Y = 0; Width = 0.0; Height = 0.0; Maximized = false }: WindowPlacement.Placement), WindowPlacement.parse "{}")
    for bad in [ ""; "not json"; "[]"; "null"; """{"X":"1"}"""; """{"X":1.5}"""; """{"Maximized":1}""" ] do
        Assert.Equal(None, WindowPlacement.parse bad)

[<Fact>]
let ``activation messages round-trip and reject anything but an array of strings`` () =
    let files = [| @"C:\a file with spaces.pdf"; "/home/u/日本語 \"quoted\".pdf" |]
    Assert.Equal<string[]>(files, (SingleInstance.decodeFiles (SingleInstance.encodeFiles files)).Value)
    Assert.Equal<string[]>([||], (SingleInstance.decodeFiles (SingleInstance.encodeFiles [||])).Value)
    for bad in [ "null"; "{}"; "\"a.pdf\""; """["a.pdf", null]"""; """["a.pdf", 1]"""; "[" ; "" ] do
        Assert.True((SingleInstance.decodeFiles (Text.Encoding.UTF8.GetBytes(bad: string))).IsNone, bad)

[<Fact>]
let ``measure snapshot is valid JSON with typed values`` () =
    let json = Diagnostics.snapshotJson [ "file", Operators.box "a \"b\".pdf"; "exitCode", Operators.box 2; "bytes", Operators.box 5L; "mb", Operators.box 1.5; "ok", Operators.box true ]
    use doc = Text.Json.JsonDocument.Parse(json: string)
    let root = doc.RootElement
    Assert.Equal("a \"b\".pdf", root.GetProperty("file").GetString())
    Assert.Equal(2, root.GetProperty("exitCode").GetInt32())
    Assert.Equal(5L, root.GetProperty("bytes").GetInt64())
    Assert.Equal(1.5, root.GetProperty("mb").GetDouble())
    Assert.True(root.GetProperty("ok").GetBoolean())
    Assert.True(root.GetProperty("workingSetMB").GetDouble() > 0.0)

[<AvaloniaFact>]
let ``window close can cancel close current or close all without duplicate prompts`` () =
    let w, _ = newWindow ()
    openFile w (synthetic "close-a.pdf" inkDoc)
    openFile w (synthetic "close-b.pdf" inkDoc)
    let answer = System.Threading.Tasks.TaskCompletionSource<Dialogs.CloseChoice>()
    let mutable prompts = 0
    w.ConfirmClose <- fun _ -> prompts <- prompts + 1; answer.Task
    w.Close()
    w.Close()
    Assert.Equal(1, prompts)
    Assert.True(w.IsVisible)
    answer.SetResult Dialogs.Cancel
    pumpFor 20
    Assert.Equal(2, w.TabCount)
    w.ConfirmClose <- fun _ -> System.Threading.Tasks.Task.FromResult Dialogs.Current
    w.Close()
    pumpUntil "current tab closed" (fun () -> w.TabCount = 1)
    Assert.True(w.IsVisible)
    Assert.Equal("close-a.pdf", w.TabNames[0])
    openFile w (synthetic "close-b.pdf" inkDoc)
    w.ConfirmClose <- fun _ -> System.Threading.Tasks.Task.FromResult Dialogs.All
    w.Close()
    pumpUntil "all tabs closed" (fun () -> not w.IsVisible)

[<AvaloniaFact>]
let ``open shows the first page with ink where the PDF puts it`` () =
    let w, _ = newWindow ()
    let path = synthetic "ink.pdf" inkDoc
    Assert.Equal(Welcome, w.Centre)
    openFile w path
    Assert.Equal(DocumentShown, w.Centre)
    Assert.Equal("ink.pdf — PdfButler", w.Title)
    Assert.Equal("Page i (1 of 5)", w.StatusPageText)
    Assert.Equal("i", w.PageBox.Text)
    let f = capture w
    // Ground truth from the PDF spec, independent of PDFium: user (150,650) -> display (150,142).
    let ex, ey = expectedDisplayPoint inkDoc.Pages[0] 150.0 650.0
    Assert.True(dark f (windowPointOf w 0 ex ey), "square centre should be black")
    let ox, oy = expectedDisplayPoint inkDoc.Pages[0] 300.0 650.0
    Assert.True(light f (windowPointOf w 0 ox oy), "beside the square should be white")
    // Just inside each edge of the square is ink, just outside is paper (within ~2 DIPs).
    let dpp = Geometry.dipsPerPoint w.Canvas.State.Zoom
    let edge = 3.0 / dpp
    Assert.True(dark f (windowPointOf w 0 (100.0 + edge) 142.0))
    Assert.True(light f (windowPointOf w 0 (100.0 - edge) 142.0))
    Assert.True(dark f (windowPointOf w 0 150.0 (92.0 + edge)))
    Assert.True(light f (windowPointOf w 0 150.0 (92.0 - edge)))
    w.Close()

[<AvaloniaFact>]
let ``keyboard navigation, page box labels and history`` () =
    let w, _ = newWindow ()
    openFile w (synthetic "nav.pdf" inkDoc)
    Assert.Equal(0, w.Canvas.CurrentPage)
    let y0 = w.Canvas.State.Offset.Y
    press w Key.PageDown RawInputModifiers.None
    Assert.True(w.Canvas.State.Offset.Y > y0)
    press w Key.End RawInputModifiers.None
    Assert.Equal(4, w.Canvas.CurrentPage)
    Assert.Equal("Page 3 (5 of 5)", w.StatusPageText)
    // Ctrl+G, type a label.
    press w Key.G RawInputModifiers.Control
    Assert.True(w.PageBox.IsFocused)
    w.PageBox.SelectAll()
    typeText w "ii"
    press w Key.Enter RawInputModifiers.None
    Assert.Equal(1, w.Canvas.CurrentPage)
    // Numbers work too, and a bad entry changes nothing.
    press w Key.G RawInputModifiers.Control
    w.PageBox.SelectAll()
    typeText w "zz"
    press w Key.Enter RawInputModifiers.None
    Assert.Equal(1, w.Canvas.CurrentPage)
    Assert.Contains("No page", w.StatusMessage)
    press w Key.Escape RawInputModifiers.None
    // Back returns to where Ctrl+G jumped from.
    press w Key.Left RawInputModifiers.Alt
    Assert.Equal(4, w.Canvas.CurrentPage)
    press w Key.Right RawInputModifiers.Alt
    Assert.Equal(1, w.Canvas.CurrentPage)
    // N / P step pages.
    press w Key.N RawInputModifiers.None
    Assert.Equal(2, w.Canvas.CurrentPage)
    press w Key.P RawInputModifiers.None
    Assert.Equal(1, w.Canvas.CurrentPage)
    press w Key.Home RawInputModifiers.None
    Assert.Equal(0, w.Canvas.CurrentPage)
    w.Close()

[<AvaloniaFact>]
let ``ctrl+wheel zooms around the pointer`` () =
    let w, _ = newWindow ()
    openFile w (synthetic "zoom.pdf" inkDoc)
    let p = windowPointOf w 0 150.0 142.0
    let before = w.Canvas.State
    let o = canvasOrigin w
    let anchorBefore = Layout.anchorAt before.Layout { X = before.Offset.X + p.X - o.X; Y = before.Offset.Y + p.Y - o.Y }
    w.MouseMove(p, RawInputModifiers.None)
    w.MouseWheel(p, Vector(0.0, 1.0), RawInputModifiers.Control)
    pumpFor 10
    let after = w.Canvas.State
    Assert.Equal(before.Zoom * 1.2, after.Zoom, 6)
    Assert.Equal(Viewer.Manual, after.Mode)
    let back = windowPointOf w anchorBefore.Value.Page anchorBefore.Value.Local.X anchorBefore.Value.Local.Y
    Assert.True(abs (back.X - p.X) <= 1.0 && abs (back.Y - p.Y) <= 1.0, $"anchor moved from {p} to {back}")
    // The rendered frame agrees: the square is still under the pointer once sharp tiles arrive.
    settle w
    Assert.True(dark (capture w) p)
    Assert.Equal(PageInput.zoomText after.Zoom, w.ZoomBox.Text)
    w.Close()

[<AvaloniaFact>]
let ``rotate moves ink to the expected quadrant`` () =
    let w, _ = newWindow ()
    // Square in the page's top-left quadrant (display space).
    let spec = doc [ { letter with Rects = [ rect 103.0 544.0 203.0 644.0 0.0 ] } ]
    openFile w (synthetic "rotate.pdf" spec)
    press w Key.D0 RawInputModifiers.Control // fit page: whole page visible
    settle w
    let quadrantCentre (r: Rect) fx fy = Point(r.X + r.Width * fx, r.Y + r.Height * fy)
    // Square centre is at display (153, 198) = (0.25 W, 0.25 H) of a 612 x 792 page.
    let f0 = capture w
    let r0 = pageRectOnScreen w 0
    Assert.True(dark f0 (quadrantCentre r0 0.25 0.25), "before: top-left")
    press w Key.OemPlus (RawInputModifiers.Control ||| RawInputModifiers.Shift)
    settle w
    Assert.Equal(R90, w.Canvas.State.Rotation)
    let f1 = capture w
    let r1 = pageRectOnScreen w 0
    Assert.True(r1.Width > r1.Height, "rotated page is landscape")
    // Clockwise quarter turn: top-left -> top-right.
    Assert.True(dark f1 (quadrantCentre r1 0.75 0.25), "after: top-right")
    Assert.True(light f1 (quadrantCentre r1 0.25 0.25))
    Assert.True(light f1 (quadrantCentre r1 0.75 0.75))
    press w Key.OemMinus (RawInputModifiers.Control ||| RawInputModifiers.Shift)
    settle w
    Assert.Equal(R0, w.Canvas.State.Rotation)
    w.Close()

let private isHighlight r g b = r > 200 && g > 90 && b < 140

[<AvaloniaFact>]
let ``search highlights hits, reports progress and F3 cycles`` () =
    let w, _ = newWindow ()
    let spec =
        doc
            [ { letter with Texts = [ text 72.0 700.0 14.0 "a needle here" ] }
              { letter with Texts = [ text 72.0 700.0 14.0 "nothing" ] }
              { letter with Texts = [ text 72.0 700.0 14.0 "Needle and needle" ] } ]
    openFile w (synthetic "search.pdf" spec)
    let pageArea = visiblePageRect w 0
    Assert.Equal(0, countPixels (capture w) pageArea isHighlight)
    press w Key.F RawInputModifiers.Control
    Assert.True(w.IsSearchOpen)
    Assert.True(w.SearchBox.IsFocused)
    typeText w "needle"
    // Debounced: the search starts on its own after ~250 ms.
    pumpUntil "search results" (fun () -> w.SearchStatusText = "1 of 3")
    settle w
    Assert.True(countPixels (capture w) (visiblePageRect w 0) isHighlight > 20, "hit should be highlighted")
    press w Key.F3 RawInputModifiers.None
    Assert.Equal("2 of 3", w.SearchStatusText)
    Assert.Equal(2, w.Canvas.CurrentPage)
    press w Key.F3 RawInputModifiers.None
    Assert.Equal("3 of 3", w.SearchStatusText)
    press w Key.F3 RawInputModifiers.None
    Assert.Equal("1 of 3", w.SearchStatusText)
    press w Key.F3 RawInputModifiers.Shift
    Assert.Equal("3 of 3", w.SearchStatusText)
    // Esc closes the bar and the highlights.
    press w Key.Escape RawInputModifiers.None
    Assert.False(w.IsSearchOpen)
    Assert.True(w.Canvas.SearchState.IsNone)
    w.Close()

[<AvaloniaFact>]
let ``search in a document without text says so`` () =
    let w, _ = newWindow ()
    openFile w (synthetic "notext.pdf" (doc [ { letter with Rects = [ rect 100.0 100.0 300.0 300.0 0.3 ] }; letter ]))
    press w Key.F RawInputModifiers.Control
    typeText w "anything"
    w.RunSearchNow()
    pumpUntil "no-text status" (fun () -> w.SearchStatusText = Search.describe Search.NoTextLayer)
    w.Close()

[<AvaloniaFact>]
let ``drag-select across two lines then ctrl+c copies the text`` () =
    let w, _ = newWindow ()
    let spec = doc [ { letter with Texts = [ text 72.0 700.0 12.0 "Hello first line"; text 72.0 680.0 12.0 "Second line here" ] } ]
    openFile w (synthetic "select.pdf" spec)
    // Hovering loads the page's text layer.
    w.MouseMove(windowPointOf w 0 300.0 300.0, RawInputModifiers.None)
    settle w
    // Line 1 occupies display y ~ 80..92, line 2 ~ 100..112.
    let start = windowPointOf w 0 70.0 88.0
    let finish = windowPointOf w 0 400.0 108.0
    w.MouseMove(start, RawInputModifiers.None)
    w.MouseDown(start, MouseButton.Left, RawInputModifiers.None)
    w.MouseMove(Point((start.X + finish.X) / 2.0, (start.Y + finish.Y) / 2.0), RawInputModifiers.LeftMouseButton)
    w.MouseMove(finish, RawInputModifiers.LeftMouseButton)
    w.MouseUp(finish, MouseButton.Left, RawInputModifiers.None)
    pumpFor 10
    Assert.True(w.Canvas.HasSelection)
    let selected = pumpResult (w.Canvas.GetSelectedTextAsync())
    Assert.Equal("Hello first line\nSecond line here", selected.Replace("\r\n", "\n"))
    press w Key.C RawInputModifiers.Control
    let clip = pumpResult (w.Clipboard.TryGetTextAsync())
    Assert.Equal("Hello first line\nSecond line here", clip.Replace("\r\n", "\n"))
    // Esc clears the selection.
    press w Key.Escape RawInputModifiers.None
    Assert.False(w.Canvas.HasSelection)
    w.Close()

[<AvaloniaFact>]
let ``space tap pages down, shift+space pages up`` () =
    let w, _ = newWindow ()
    openFile w (synthetic "space-tap.pdf" inkDoc)
    let y0 = w.Canvas.State.Offset.Y
    press w Key.Space RawInputModifiers.None
    let y1 = w.Canvas.State.Offset.Y
    Assert.True(y1 > y0 + 100.0, $"space should page down: {y0} -> {y1}")
    Assert.False(w.Canvas.HandMode)
    press w Key.Space RawInputModifiers.Shift
    Assert.Equal(y0, w.Canvas.State.Offset.Y, 3)
    w.Close()

[<AvaloniaFact>]
let ``holding space and dragging pans instead of selecting, and does not page`` () =
    let w, _ = newWindow ()
    let spec = doc [ { letter with Texts = [ text 72.0 700.0 12.0 "Hello first line"; text 72.0 680.0 12.0 "Second line here" ] }; letter ]
    openFile w (synthetic "space-pan.pdf" spec)
    // Zoomed in so both directions can scroll; text layer loaded so a plain drag would select.
    for _ in 1..4 do press w Key.OemPlus RawInputModifiers.Control
    w.Canvas.Dispatch(Viewer.ScrollTo { X = 100.0; Y = 100.0 })
    w.MouseMove(windowPointOf w 0 100.0 100.0, RawInputModifiers.None)
    settle w
    let start = windowPointOf w 0 100.0 88.0
    Assert.True(w.Canvas.Bounds.Contains(w.TranslatePoint(start, w.Canvas).Value), "drag must start on the canvas")
    let before = w.Canvas.State.Offset
    w.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.None, null)
    w.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.None, null) // auto-repeat
    pumpFor 5
    Assert.True(w.Canvas.HandMode)
    w.MouseMove(start, RawInputModifiers.None)
    w.MouseDown(start, MouseButton.Left, RawInputModifiers.None)
    w.MouseMove(Point(start.X - 30.0, start.Y - 20.0), RawInputModifiers.LeftMouseButton)
    w.MouseMove(Point(start.X - 60.0, start.Y - 40.0), RawInputModifiers.LeftMouseButton)
    w.MouseUp(Point(start.X - 60.0, start.Y - 40.0), MouseButton.Left, RawInputModifiers.None)
    w.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.None, null)
    pumpFor 5
    let after = w.Canvas.State.Offset
    Assert.Equal(before.X + 60.0, after.X, 3)
    Assert.Equal(before.Y + 40.0, after.Y, 3)
    Assert.False(w.Canvas.HasSelection)
    Assert.False(w.Canvas.HandMode)
    // Without Space the same drag selects text again.
    let p = windowPointOf w 0 70.0 88.0
    w.MouseMove(p, RawInputModifiers.None)
    w.MouseDown(p, MouseButton.Left, RawInputModifiers.None)
    w.MouseMove(Point(p.X + 120.0, p.Y), RawInputModifiers.LeftMouseButton)
    w.MouseUp(Point(p.X + 120.0, p.Y), MouseButton.Left, RawInputModifiers.None)
    pumpFor 5
    Assert.True(w.Canvas.HasSelection)
    w.Close()

[<AvaloniaFact>]
let ``space in a text box types instead of panning or paging`` () =
    let w, _ = newWindow ()
    openFile w (synthetic "space-text.pdf" inkDoc)
    let y0 = w.Canvas.State.Offset.Y
    press w Key.F RawInputModifiers.Control
    w.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.None, null)
    pumpFor 5
    Assert.False(w.Canvas.HandMode)
    w.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.None, null)
    pumpFor 5
    Assert.Equal(y0, w.Canvas.State.Offset.Y, 3)
    w.Close()

let private linkDoc =
    { doc
        [ { letter with
              Texts = [ text 72.0 700.0 14.0 "Links" ]
              Links =
                [ { Area = box 72.0 600.0 172.0 620.0; Target = ToPage(2, Some 400.0) }
                  { Area = box 72.0 560.0 172.0 580.0; Target = ToUri "https://example.com/a?b=1" }
                  { Area = box 72.0 520.0 172.0 540.0; Target = Launch "calc.exe" }
                  { Area = box 72.0 480.0 172.0 500.0; Target = ToUri "file:///C:/Windows/notepad.exe" } ] }
          letter
          { letter with Texts = [ text 72.0 400.0 14.0 "Target" ] } ] with
        Outline = [ { Title = "Chapter two"; Page = 1; Top = None; Children = [] }; { Title = "Chapter three"; Page = 2; Top = None; Children = [] } ] }

/// Window point at the centre of a link box given in user space on page 0 (612 x 792, no rotation).
let private linkPoint (w: MainWindow) (x0: float) (y0: float) (x1: float) (y1: float) =
    windowPointOf w 0 ((x0 + x1) / 2.0) (792.0 - (y0 + y1) / 2.0)

[<AvaloniaFact>]
let ``internal link navigates, external links need confirmation, others are refused`` () =
    let w, calls = newWindow ()
    openFile w (synthetic "links.pdf" linkDoc)
    // External link: confirmation shows the full URL; declined -> nothing launched.
    click w (linkPoint w 72.0 560.0 172.0 580.0)
    pumpFor 10
    Assert.Equal<System.Uri list>([ System.Uri "https://example.com/a?b=1" ], List.ofSeq calls.Confirms)
    Assert.Empty(calls.Launches)
    // Accepted -> handed to the launcher (a recorder in tests).
    calls.ConfirmAnswer <- true
    click w (linkPoint w 72.0 560.0 172.0 580.0)
    pumpFor 10
    Assert.Equal<System.Uri list>([ System.Uri "https://example.com/a?b=1" ], List.ofSeq calls.Launches)
    // Launch actions and file: links are never offered.
    click w (linkPoint w 72.0 520.0 172.0 540.0)
    pumpFor 10
    Assert.Contains("launch application", w.StatusMessage)
    click w (linkPoint w 72.0 480.0 172.0 500.0)
    pumpFor 10
    Assert.Contains("file:", w.StatusMessage)
    Assert.Equal(2, calls.Confirms.Count)
    Assert.Equal(1, calls.Launches.Count)
    // Internal link.
    click w (linkPoint w 72.0 600.0 172.0 620.0)
    pumpUntil "link navigation" (fun () -> w.Canvas.CurrentPage = 2)
    press w Key.Left RawInputModifiers.Alt
    Assert.Equal(0, w.Canvas.CurrentPage)
    w.Close()

[<AvaloniaFact>]
let ``outline click navigates`` () =
    let w, _ = newWindow ()
    openFile w (synthetic "outline.pdf" linkDoc)
    pumpUntil "outline loaded" (fun () -> w.OutlineTree.ItemCount = 2)
    Assert.True(w.OutlineButton.IsEnabled)
    w.OutlineButton.IsChecked <- Nullable true
    pumpFor 20
    Assert.True(w.IsOutlineShown)
    let item = w.OutlineTree.Items[1] :?> TreeViewItem
    Assert.Equal("Chapter three", string item.Header)
    let p = item.TranslatePoint(Point(40.0, 10.0), w).Value
    click w p
    pumpUntil "outline navigation" (fun () -> w.Canvas.CurrentPage = 2)
    w.Close()

[<AvaloniaFact>]
let ``wrong password asks again, cancel shows the error panel`` () =
    let path = corpusFile "pdfium/encrypted"
    let w, calls = newWindow ()
    calls.PasswordAnswers.Enqueue(Some "wrong")
    calls.PasswordAnswers.Enqueue(Some "1234")
    openFile w path
    Assert.Equal<(string * bool) list>([ ("encrypted.pdf", false); ("encrypted.pdf", true) ], List.ofSeq calls.Passwords)
    Assert.Equal(DocumentShown, w.Centre)
    // Cancel.
    calls.Passwords.Clear()
    w.CloseDocument() // Opening an already-open file now selects its tab instead of re-opening it.
    openFile w path
    Assert.Equal(1, calls.Passwords.Count)
    match w.Centre with
    | OpenFailed(name, msg) ->
        Assert.Equal("encrypted.pdf", name)
        Assert.Equal(OpenError.describe PasswordRequired, msg)
    | c -> failwith $"expected error panel, got {c}"
    Assert.Null(w.Canvas.Document)
    w.Close()

[<AvaloniaFact>]
let ``tabs retain independent state and inactive tabs release raster memory`` () =
    let w, _ = newWindow ()
    openFile w (synthetic "tabs-a.pdf" inkDoc)
    let a = w.Canvas
    let aDoc = a.Document
    Assert.Equal(1.0, a.State.Zoom)
    a.Dispatch(Viewer.GoToPage 3)
    a.Dispatch(Viewer.ZoomTo(1.5, None))
    a.Dispatch Viewer.RotateClockwise
    press w Key.F RawInputModifiers.Control
    typeText w "First"
    w.RunSearchNow()
    pumpUntil "tab A search" (fun () -> w.SearchStatusText = "1 of 1")
    let stateA = a.State
    openFile w (synthetic "tabs-b.pdf" inkDoc)
    let bDoc = w.Canvas.Document
    saveFrame w "tabs-light" |> ignore
    w.RequestedThemeVariant <- Avalonia.Styling.ThemeVariant.Dark
    pumpFor 30
    saveFrame w "tabs-dark" |> ignore
    w.RequestedThemeVariant <- Avalonia.Styling.ThemeVariant.Default
    Assert.Equal(2, w.TabCount)
    Assert.Equal<string[]>([| "tabs-a.pdf"; "tabs-b.pdf" |], w.TabNames)
    Assert.Equal(1.0, w.Canvas.State.Zoom)
    Assert.Equal(0L, a.Renderer.Value.CachedBytes)
    Assert.False(aDoc.IsClosed)
    press w Key.Tab (RawInputModifiers.Control ||| RawInputModifiers.Shift)
    Assert.Same(a, w.Canvas)
    Assert.Equal(stateA.Zoom, w.Canvas.State.Zoom)
    Assert.Equal(stateA.Rotation, w.Canvas.State.Rotation)
    Assert.Equal(stateA.History, w.Canvas.State.History)
    Assert.True(w.IsSearchOpen)
    Assert.Equal("First", w.SearchBox.Text)
    Assert.Equal("1 of 1", w.SearchStatusText)
    // Duplicate paths select the tab without re-opening it.
    openFile w (synthetic "tabs-b.pdf" inkDoc)
    Assert.Equal(2, w.TabCount)
    Assert.Same(bDoc, w.Canvas.Document)
    let cached = w.Canvas.Renderer.Value.CachedBytes
    pumpTask (w.OpenFileAsync(synthetic "tabs-b.pdf" inkDoc))
    Assert.Equal(cached, w.Canvas.Renderer.Value.CachedBytes)
    press w Key.W RawInputModifiers.Control
    pumpUntil "B closed" (fun () -> bDoc.IsClosed)
    Assert.Equal(1, w.TabCount)
    Assert.Same(a, w.Canvas)
    w.Close()
    pumpUntil "A closed with window" (fun () -> aDoc.IsClosed)

[<AvaloniaFact>]
let ``tab limit refuses another file and closing an inactive tab preserves the active one`` () =
    let w, _ = newWindow ()
    Assert.Equal(30, w.Settings.MaxOpenFiles)
    let limit = 3
    w.ApplySettings { w.Settings with MaxOpenFiles = limit }
    for i in 1 .. limit do openFile w (synthetic $"limit-{i}.pdf" inkDoc)
    let active = w.Canvas.Document
    openFile w (synthetic "over-limit.pdf" inkDoc)
    Assert.Equal(limit, w.TabCount)
    Assert.Same(active, w.Canvas.Document)
    Assert.Contains("Close a tab", w.StatusMessage)
    w.CloseTab 0
    Assert.Same(active, w.Canvas.Document)
    Assert.Equal(limit - 2, w.SelectedTabIndex)
    openFile w (synthetic "over-limit.pdf" inkDoc)
    Assert.Equal(limit, w.TabCount)
    w.Close()

let private restoredSession (prefix: string) (count: int) (page: int) =
    { Settings.Tabs = [ for i in 1 .. count -> { Settings.Path = synthetic $"{prefix}-{i}.pdf" inkDoc; Settings.Page = page } ]
      Settings.Selected = 0 }

[<AvaloniaFact>]
let ``many tabs shrink then scroll with arrows and the strip never changes height`` () =
    let w, _ = newWindow ()
    openFile w (synthetic "strip-first.pdf" inkDoc)
    let oneTabHeight = w.TabStrip.Bounds.Height
    Assert.False(w.ScrollTabsBackButton.IsVisible)
    Assert.False(w.TabStrip.Overflows)
    // Restored tabs are cheap: no helper starts until a tab is selected.
    pumpTask (w.RestoreSessionAsync(restoredSession "strip" 24 0, false))
    pumpFor 30
    Assert.Equal(25, w.TabCount)
    Assert.Equal(0, w.SelectedTabIndex)
    Assert.True(w.TabStrip.Overflows)
    Assert.True(w.ScrollTabsBackButton.IsVisible && w.ScrollTabsForwardButton.IsVisible && w.AllTabsButton.IsVisible)
    Assert.False(w.ScrollTabsBackButton.IsEnabled)
    Assert.Equal(oneTabHeight, w.TabStrip.Bounds.Height)
    Assert.Null(w.TabStrip.FindAncestorOfType<ScrollViewer>()) // no scroll bar can appear around the strip
    let widths = [ for i in 0 .. w.TabCount - 1 -> (w.TabHeader i).Bounds.Width ]
    Assert.All(widths, fun x -> Assert.Equal(TabStrip.defaultParams.MinWidth, x, 3))
    for i in 1 .. 24 do Assert.False(w.IsTabLoaded i)
    // Selecting the last tab loads only it and scrolls it fully into view.
    w.SelectTab 24
    pumpUntil "last tab open" (fun () -> w.Centre = DocumentShown)
    settle w
    Assert.True(w.IsTabLoaded 24)
    Assert.False(w.IsTabLoaded 12)
    let last = w.TabHeader 24
    let stripRight = w.TabStrip.Bounds.Width
    Assert.True(last.Bounds.Right <= stripRight + 0.5 && last.Bounds.X >= -0.5, $"last tab at {last.Bounds}, strip {stripRight}")
    Assert.True(w.ScrollTabsBackButton.IsEnabled)
    Assert.False(w.ScrollTabsForwardButton.IsEnabled)
    saveFrame w "tabs-overflow" |> ignore
    let before = w.TabStrip.Offset
    w.ScrollTabsBackButton.RaiseEvent(Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent))
    pumpFor 20
    Assert.True(w.TabStrip.Offset < before)
    Assert.True(w.ScrollTabsForwardButton.IsEnabled)
    // Closing tabs until they fit again hides the arrows (select a loaded tab so closing doesn't load others).
    w.SelectTab 0
    while w.TabCount > 3 do w.CloseTab(w.TabCount - 1)
    pumpFor 30
    Assert.False(w.TabStrip.Overflows)
    Assert.False(w.ScrollTabsBackButton.IsVisible)
    Assert.Equal(0.0, w.TabStrip.Offset)
    w.Close()

[<AvaloniaFact>]
let ``restored tabs open on first selection at their saved page without a back entry`` () =
    let w, _ = newWindow ()
    let session = restoredSession "restore" 3 2
    let missing = Path.Combine(tempDir.Value, "restore-missing.pdf")
    let session = { session with Settings.Tabs = session.Tabs @ [ { Settings.Path = missing; Settings.Page = 0 } ]; Settings.Selected = 1 }
    pumpTask (w.RestoreSessionAsync(session, true))
    Assert.Equal(3, w.TabCount) // the missing file is skipped
    Assert.Equal(1, w.SelectedTabIndex)
    pumpUntil "restored tab open" (fun () -> w.Centre = DocumentShown)
    settle w
    Assert.Equal(2, w.Canvas.CurrentPage)
    Assert.False(History.canGoBack w.Canvas.State.History)
    Assert.False(w.IsTabLoaded 0)
    w.Canvas.Dispatch(Viewer.GoToPage 4)
    let snap = w.SessionSnapshot()
    Assert.Equal(1, snap.Selected)
    Assert.Equal<int list>([ 2; 4; 2 ], snap.Tabs |> List.map (fun t -> t.Page))
    // Restoring again does not duplicate tabs.
    pumpTask (w.RestoreSessionAsync(session, true))
    Assert.Equal(3, w.TabCount)
    let mutable saved = None
    w.SaveSession <- fun s -> saved <- Some s
    w.ApplySettings { w.Settings with RestoreSession = true; ConfirmCloseMultiple = false }
    w.Close()
    pumpUntil "closed" (fun () -> not w.IsVisible)
    Assert.Equal(3, saved.Value.Tabs.Length)

[<AvaloniaFact>]
let ``with restore off the window saves an empty session`` () =
    let w, _ = newWindow ()
    openFile w (synthetic "no-restore.pdf" inkDoc)
    let mutable saved = None
    w.SaveSession <- fun s -> saved <- Some s
    w.Close()
    pumpUntil "closed" (fun () -> not w.IsVisible)
    Assert.Equal(Some Settings.emptySession, saved)

[<AvaloniaFact>]
let ``sidebar layout lists every tab and switching back restores the strip`` () =
    let w, _ = newWindow ()
    openFile w (synthetic "side-a.pdf" inkDoc)
    openFile w (synthetic "side-b.pdf" inkDoc)
    w.ApplySettings { w.Settings with TabLayout = Settings.Sidebar }
    pumpFor 30
    Assert.True(w.TabSidebar.IsVisible)
    Assert.Equal(0, w.TabStrip.Children.Count)
    Assert.True((w.TabHeader 0).IsVisible && (w.TabHeader 0).Bounds.Width > 150.0)
    Assert.True(w.Canvas.TranslatePoint(Point(0.0, 0.0), w).Value.X >= w.TabSidebar.Bounds.Width)
    saveFrame w "tabs-sidebar" |> ignore
    w.SelectTab 0
    Assert.Equal("side-a.pdf — PdfButler", w.Title)
    w.ApplySettings { w.Settings with TabLayout = Settings.Top }
    pumpFor 30
    Assert.False(w.TabSidebar.IsVisible)
    Assert.Equal(2, w.TabStrip.Children.Count)
    w.Close()

[<AvaloniaFact>]
let ``turning off the close prompt closes every tab without asking`` () =
    let w, _ = newWindow ()
    openFile w (synthetic "noprompt-a.pdf" inkDoc)
    openFile w (synthetic "noprompt-b.pdf" inkDoc)
    let mutable prompts = 0
    w.ConfirmClose <- fun _ -> prompts <- prompts + 1; Threading.Tasks.Task.FromResult Dialogs.Cancel
    w.ApplySettings { w.Settings with ConfirmCloseMultiple = false }
    w.Close()
    pumpUntil "closed" (fun () -> not w.IsVisible)
    Assert.Equal(0, prompts)

[<AvaloniaFact>]
let ``default zoom setting applies to newly opened PDFs`` () =
    let w, _ = newWindow ()
    openFile w (synthetic "zoom-a.pdf" inkDoc)
    Assert.Equal(Viewer.Manual, w.Canvas.State.Mode)
    Assert.Equal(1.0, w.Canvas.State.Zoom)
    w.ApplySettings { w.Settings with DefaultZoom = Settings.FitWidth }
    openFile w (synthetic "zoom-b.pdf" inkDoc)
    Assert.Equal(Viewer.FitWidth, w.Canvas.State.Mode)
    let fitted = Layout.fitWidthZoom w.Canvas.State.Pages w.Canvas.State.Layout.Params w.Canvas.State.Viewport
    Assert.Equal(fitted, w.Canvas.State.Zoom, 6)
    w.SelectTab 0
    Assert.Equal(Viewer.Manual, w.Canvas.State.Mode) // already-open PDFs keep their zoom
    w.Close()

[<AvaloniaFact>]
let ``settings window applies each change at once and reset restores defaults`` () =
    let changes = ResizeArray<Settings.Settings>()
    let c = Dialogs.settingsWindow { Settings.defaults with MaxOpenFiles = 12 } changes.Add
    c.Window.Show()
    pumpFor 20
    Assert.Equal(12m, c.MaxOpenFiles.Value.Value)
    Assert.Empty changes
    c.TabLayout.SelectedIndex <- 1
    c.RestoreSession.IsChecked <- Nullable true
    c.MaxOpenFiles.Value <- Nullable 40m
    c.DefaultZoom.SelectedIndex <- 2
    pumpFor 10
    let last = changes[changes.Count - 1]
    Assert.Equal({ Settings.defaults with TabLayout = Settings.Sidebar; RestoreSession = true; MaxOpenFiles = 40; DefaultZoom = Settings.FitPage }, last)
    c.Reset.RaiseEvent(Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent))
    pumpFor 10
    Assert.Equal(Settings.defaults, changes[changes.Count - 1])
    Assert.Equal(0, c.TabLayout.SelectedIndex)
    c.Window.Close()

[<AvaloniaFact>]
let ``Ctrl+comma opens one settings window and changes are saved`` () =
    let w, _ = newWindow ()
    let saved = ResizeArray<Settings.Settings>()
    w.SaveSettings <- saved.Add
    press w Key.OemComma RawInputModifiers.Control
    pumpFor 20
    let dialog = w.SettingsDialog.Value
    press w Key.OemComma RawInputModifiers.Control
    Assert.Same(dialog, w.SettingsDialog.Value)
    let box = dialog.GetVisualDescendants() |> Seq.pick (function :? CheckBox as c when string c.Content = "Reopen the PDFs that were open last time" -> Some c | _ -> None)
    box.IsChecked <- Nullable true
    pumpFor 10
    Assert.True(w.Settings.RestoreSession)
    Assert.True(saved[saved.Count - 1].RestoreSession)
    w.Close()
    pumpUntil "closed" (fun () -> not w.IsVisible)
    Assert.True(w.SettingsDialog.IsNone)

[<AvaloniaFact>]
let ``closing a tab during password prompt cannot resurrect the document`` () =
    let path = corpusFile "pdfium/encrypted"
    let w, _ = newWindow ()
    let answer = Threading.Tasks.TaskCompletionSource<string option>()
    let mutable asked = false
    w.AskPassword <- fun _ _ -> asked <- true; answer.Task
    let opening = w.OpenFileAsync path
    pumpUntil "password prompt" (fun () -> asked)
    w.CloseDocument()
    answer.SetResult(Some "1234")
    pumpTask opening
    Assert.Equal(0, w.TabCount)
    Assert.Equal(Welcome, w.Centre)
    Assert.Null(w.Canvas.Document)
    w.Close()

[<AvaloniaFact>]
let ``Windows activation forwards bounded file paths to the primary instance`` () =
    if OperatingSystem.IsWindows() then
        let name = "PdfButler.Test." + Guid.NewGuid().ToString("N")
        use primary = new SingleInstance.Instance(name)
        Assert.True(primary.IsPrimary)
        // Mutexes are re-entrant on the owning thread, so create the client on another thread.
        let paths = [| Path.GetFullPath "a file with spaces.pdf"; Path.GetFullPath "日本語.pdf" |]
        let forwarded = Threading.Tasks.Task.Run(fun () ->
            use client = new SingleInstance.Instance(name)
            Assert.False(client.IsPrimary)
            client.ForwardAsync(paths).GetAwaiter().GetResult())
        Assert.True(pumpResult forwarded)
        let mutable messages = [||]
        pumpUntil "activation" (fun () -> messages <- Array.append messages (primary.Drain()); messages.Length > 0)
        Assert.Equal<string[]>(paths, messages[0])

[<AvaloniaFact>]
let ``command line accepts multiple PDF paths and measure remains opt in`` () =
    let options = Program.parseArgs [| "a.pdf"; "a path with spaces.pdf" |]
    Assert.Equal<string[]>([| "a.pdf"; "a path with spaces.pdf" |], options.Files)
    Assert.False(options.Measure)
    Assert.True((Program.parseArgs [| "--measure"; "a.pdf" |]).Measure)

[<AvaloniaFact>]
let ``malformed file shows the error panel`` () =
    let path = Path.Combine(tempDir.Value, "garbage.pdf")
    File.WriteAllText(path, "this is not a PDF at all")
    let w, _ = newWindow ()
    openFile w path
    Assert.Equal(OpenFailed("garbage.pdf", OpenError.describe Malformed), w.Centre)
    openFile w (Path.Combine(tempDir.Value, "does-not-exist.pdf"))
    match w.Centre with
    | OpenFailed("does-not-exist.pdf", msg) -> Assert.Contains("could not be found", msg)
    | c -> failwith $"unexpected {c}"
    // The window still works afterwards.
    openFile w (synthetic "after-error.pdf" inkDoc)
    Assert.Equal(DocumentShown, w.Centre)
    w.Close()

[<AvaloniaFact>]
let ``a page that fails to load shows a placeholder on that page only`` () =
    let path = corpusFile "pdfium/bad_page_type"
    let w, _ = newWindow ()
    openFile w path
    Assert.Equal(DocumentShown, w.Centre)
    w.Canvas.Dispatch(Viewer.GoToPage 1)
    settle w
    w.Canvas.Dispatch(Viewer.GoToPage 0)
    settle w
    let r = w.Canvas.Renderer.Value
    Assert.True(r.PageError(1).IsSome, "page 1 should have an error")
    Assert.True(r.PageError(0).IsNone, "page 0 should render")
    w.Close()

[<AvaloniaFact>]
let ``opening B while A renders never shows A's tiles`` () =
    let w, _ = newWindow ()
    // A: a full-page black, heavy-ish page set; B: blank pages.
    let black = { letter with Rects = [ rect 0.0 0.0 612.0 792.0 0.0 ] }
    let a = synthetic "a.pdf" (doc (List.replicate 20 black))
    let b = synthetic "b.pdf" (doc (List.replicate 20 letter))
    let ta = w.OpenFileAsync a
    pumpUntil "A attached" (fun () -> not (isNull w.Canvas.Document))
    let aId = w.Canvas.Document.Id
    // A's first renders are queued or running now; B replaces it.
    openFile w b
    pumpTask ta
    let bId = w.Canvas.Document.Id
    Assert.NotEqual(aId, bId)
    Assert.True(w.Canvas.Renderer.Value.CachedKeys |> List.forall (fun k -> k.Doc = bId))
    let f = capture w
    let r = visiblePageRect w 0
    Assert.Equal(0, countPixels f (r.Deflate 4.0) (fun r g b -> (r + g + b) / 3 < 128))
    // Also when B is opened before A has even finished opening.
    let ta2 = w.OpenFileAsync a
    let tb2 = w.OpenFileAsync b
    pumpTask ta2
    pumpTask tb2
    settle w
    Assert.Equal("b.pdf — PdfButler", w.Title)
    Assert.Equal(0, countPixels (capture w) ((visiblePageRect w 0).Deflate 4.0) (fun r g b -> (r + g + b) / 3 < 128))
    w.Close()

[<AvaloniaFact>]
let ``raster cache stays within budget while scrolling 1500 pages`` () =
    let w, _ = newWindow ()
    let pages = List.init 1500 (fun i -> { letter with Texts = [ text 72.0 700.0 24.0 $"Page {i + 1}" ]; Rects = [ rect 72.0 72.0 540.0 600.0 0.85 ] })
    openFile w (synthetic "long.pdf" (doc pages))
    let budget = PageRenderer.cacheBudgetBytes
    let mutable maxSeen = 0L
    let check () =
        let used = w.Canvas.Renderer.Value.CachedBytes
        maxSeen <- max maxSeen used
        Assert.True(used <= budget, $"cache {used} > budget {budget}")
    // Zoomed in, so each page costs a lot of cache.
    w.Canvas.Dispatch(Viewer.ZoomTo(2.0, None))
    for target in 0..37..1499 do
        w.Canvas.Dispatch(Viewer.GoToPage target)
        settle w
        check ()
        press w Key.PageDown RawInputModifiers.None
        settle w
        check ()
    press w Key.End RawInputModifiers.None
    settle w
    check ()
    Assert.Equal(1499, w.Canvas.CurrentPage)
    // The cache was actually exercised, not trivially empty.
    Assert.True(maxSeen > budget / 2L, $"cache only reached {maxSeen} bytes")
    w.Close()

[<AvaloniaFact>]
let ``notices for signatures and dynamic XFA`` () =
    let w, _ = newWindow ()
    openFile w (corpusFile "pdfium/two_signatures")
    Assert.Equal("Contains 2 digital signatures: shown, not verified.", w.NoticeText)
    openFile w (corpusFile "pdfium/simple_xfa")
    Assert.Contains("Dynamic XFA form, not supported", w.NoticeText)
    openFile w (synthetic "plain.pdf" inkDoc)
    Assert.Equal("", w.NoticeText)
    w.Close()

[<AvaloniaFact>]
let ``ctrl+w closes the document`` () =
    let w, _ = newWindow ()
    openFile w (synthetic "close.pdf" inkDoc)
    let d = w.Canvas.Document
    press w Key.W RawInputModifiers.Control
    pumpFor 20
    Assert.Equal(Welcome, w.Centre)
    Assert.Null(w.Canvas.Document)
    Assert.True(d.IsClosed)
    Assert.Equal("PdfButler", w.Title)
    w.Close()

[<AvaloniaFact>]
let ``renderer crash affects only its tab and reopen starts a fresh renderer`` () =
    let w, _ = newWindow ()
    try
        openFile w (synthetic "crash-healthy.pdf" inkDoc)
        let healthy = w.Canvas.Document
        openFile w (synthetic "crash-victim.pdf" inkDoc)
        let victim = w.Canvas.Document
        let oldId = victim.Id
        use child = Diagnostics.Process.GetProcessById victim.ProcessId
        child.Kill(true)
        pumpUntil "crash shown" (fun () -> match w.Centre with OpenFailed _ -> true | _ -> false)
        Assert.Null(w.Canvas.Document)
        Assert.True(victim.IsClosed)
        Assert.True(w.IsVisible)
        Assert.Equal(2, w.TabCount)
        let retry =
            w.GetVisualDescendants() |> Seq.choose (function :? Button as b -> Some b | _ -> None)
            |> Seq.find (fun b -> string b.Content = "Reopen PDF")
        retry.RaiseEvent(Interactivity.RoutedEventArgs(Button.ClickEvent))
        pumpUntil "reopened" (fun () -> w.Centre = DocumentShown)
        settle w
        Assert.NotEqual(oldId, w.Canvas.Document.Id)
        Assert.True(dark (capture w) (windowPointOf w 0 150.0 142.0))
        w.SelectTab 0
        settle w
        Assert.Same(healthy, w.Canvas.Document)
        Assert.True(dark (capture w) (windowPointOf w 0 150.0 142.0))
    finally w.Close()

[<AvaloniaFact>]
let ``an inactive renderer crash does not replace the active document or leak on close`` () =
    let w, _ = newWindow ()
    try
        openFile w (synthetic "inactive-crash.pdf" inkDoc)
        let inactive = w.Canvas.Document
        use victim = Diagnostics.Process.GetProcessById inactive.ProcessId
        openFile w (synthetic "active-survivor.pdf" inkDoc)
        let survivor = w.Canvas.Document
        use healthy = Diagnostics.Process.GetProcessById survivor.ProcessId
        victim.Kill(true)
        pumpUntil "inactive document closed" (fun () -> inactive.IsClosed)
        Assert.Same(survivor, w.Canvas.Document)
        Assert.Equal(DocumentShown, w.Centre)
        Assert.False(healthy.HasExited)
        w.SelectTab 0
        match w.Centre with
        | OpenFailed(name, _) -> Assert.Equal("inactive-crash.pdf", name)
        | c -> failwithf "unexpected %A" c
        w.Close()
        pumpUntil "window closed" (fun () -> not w.IsVisible)
        Assert.True(healthy.WaitForExit 5000)
    finally w.Close()

[<AvaloniaFact>]
let ``a helper dying while opening shows an error and the next document still opens`` () =
    let mutable first = true
    let launch () =
        let session = new RendererProcess.RendererSession(RendererProcess.startInfo (), TimeSpan.FromSeconds 5.0)
        if first then
            first <- false
            use child = Diagnostics.Process.GetProcessById session.ProcessId
            child.Kill(true)
            Assert.True(child.WaitForExit 5000)
        session
    let w = MainWindow(RendererProcess.RendererEngine(launch), Width = 900.0, Height = 700.0)
    w.ConfirmClose <- fun _ -> Threading.Tasks.Task.FromResult Dialogs.All
    try
        w.Show()
        openFile w (synthetic "crash-during-open.pdf" inkDoc)
        match w.Centre with
        | OpenFailed(name, _) -> Assert.Equal("crash-during-open.pdf", name)
        | c -> failwithf "unexpected %A" c
        openFile w (synthetic "after-open-crash.pdf" inkDoc)
        Assert.Equal(DocumentShown, w.Centre)
        Assert.True(dark (capture w) (windowPointOf w 0 150.0 142.0))
    finally w.Close()

let private loadingProgress (w: MainWindow) =
    w.GetVisualDescendants() |> Seq.choose (function :? ProgressBar as p -> Some p | _ -> None)
    |> Seq.find (fun p -> Automation.AutomationProperties.GetName p = "Loading PDF")

[<AvaloniaFact>]
let ``slow opens show an immediate loader which stops on first page or open failure`` () =
    use release = new Threading.ManualResetEventSlim(false)
    let launch () =
        if not (release.Wait 10000) then failwith "test open barrier timed out"
        new RendererProcess.RendererSession(RendererProcess.startInfo (), TimeSpan.FromSeconds 5.0)
    let w = MainWindow(RendererProcess.RendererEngine(launch), Width = 900.0, Height = 700.0)
    w.ConfirmClose <- fun _ -> Threading.Tasks.Task.FromResult Dialogs.All
    try
        w.Show()
        let opening = w.OpenFileAsync(synthetic "cloud-backed.pdf" inkDoc)
        Assert.Equal(Opening "cloud-backed.pdf", w.Centre)
        Assert.False(opening.IsCompleted)
        pumpFor 30
        let progress = loadingProgress w
        Assert.True(progress.IsEffectivelyVisible)
        Assert.True(progress.IsIndeterminate)
        Assert.False(w.PageBox.IsEnabled)
        let texts = w.GetVisualDescendants() |> Seq.choose (function :? TextBlock as t when t.IsEffectivelyVisible -> Some t.Text | _ -> None) |> Seq.toList
        Assert.Contains("Opening “cloud-backed.pdf”…", texts)
        Assert.Contains("Cloud-backed files may take longer if they need to download first.", texts)
        saveFrame w "loading-cloud-file" |> ignore
        release.Set()
        pumpTask opening
        settle w
        pumpUntil "loader hidden after first content" (fun () -> not progress.IsEffectivelyVisible)
        Assert.Equal(DocumentShown, w.Centre)
        Assert.False(progress.IsIndeterminate)
        Assert.True(w.PageBox.IsEnabled)
        Assert.True(dark (capture w) (windowPointOf w 0 150.0 142.0))
        let path = Path.Combine(tempDir.Value, "loading-malformed.pdf")
        File.WriteAllText(path, "not a PDF")
        let failed = w.OpenFileAsync path
        Assert.Equal(Opening "loading-malformed.pdf", w.Centre)
        pumpTask failed
        match w.Centre with
        | OpenFailed _ -> ()
        | c -> failwithf "unexpected %A" c
        Assert.False((loadingProgress w).IsEffectivelyVisible)
        Assert.False((loadingProgress w).IsIndeterminate)
    finally
        release.Set()
        w.Close()

[<AvaloniaFact>]
let ``a loading tab can be switched away from and closed without resurrecting it`` () =
    use release = new Threading.ManualResetEventSlim(true)
    let launch () =
        if not (release.Wait 10000) then failwith "test open barrier timed out"
        new RendererProcess.RendererSession(RendererProcess.startInfo (), TimeSpan.FromSeconds 5.0)
    let w = MainWindow(RendererProcess.RendererEngine(launch), Width = 900.0, Height = 700.0)
    w.ConfirmClose <- fun _ -> Threading.Tasks.Task.FromResult Dialogs.All
    try
        w.Show()
        openFile w (synthetic "loaded-before-cloud.pdf" inkDoc)
        let healthy = w.Canvas.Document
        release.Reset()
        let opening = w.OpenFileAsync(synthetic "waiting-cloud.pdf" inkDoc)
        Assert.Equal(Opening "waiting-cloud.pdf", w.Centre)
        pumpFor 20
        Assert.True((loadingProgress w).IsEffectivelyVisible)
        w.SelectTab 0
        Assert.Equal(DocumentShown, w.Centre)
        Assert.Same(healthy, w.Canvas.Document)
        Assert.False((loadingProgress w).IsEffectivelyVisible)
        w.SelectTab 1
        Assert.Equal(Opening "waiting-cloud.pdf", w.Centre)
        Assert.True((loadingProgress w).IsEffectivelyVisible)
        w.CloseDocument()
        release.Set()
        pumpTask opening
        Assert.Equal(1, w.TabCount)
        Assert.Equal(DocumentShown, w.Centre)
        Assert.Same(healthy, w.Canvas.Document)
        Assert.False((loadingProgress w).IsEffectivelyVisible)
    finally
        release.Set()
        w.Close()
