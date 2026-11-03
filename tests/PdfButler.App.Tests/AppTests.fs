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
    for i in 1 .. maxTabs do openFile w (synthetic $"limit-{i}.pdf" inkDoc)
    let active = w.Canvas.Document
    openFile w (synthetic "over-limit.pdf" inkDoc)
    Assert.Equal(maxTabs, w.TabCount)
    Assert.Same(active, w.Canvas.Document)
    Assert.Contains("Close a tab", w.StatusMessage)
    w.CloseTab 0
    Assert.Same(active, w.Canvas.Document)
    Assert.Equal(maxTabs - 2, w.SelectedTabIndex)
    openFile w (synthetic "over-limit.pdf" inkDoc)
    Assert.Equal(maxTabs, w.TabCount)
    w.Close()

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
