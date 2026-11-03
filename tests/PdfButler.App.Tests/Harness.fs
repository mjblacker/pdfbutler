/// Headless Avalonia (Skia + HarfBuzz, real rendering) plus helpers to drive the main window.
module PdfButler.App.Tests.Harness

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open PdfButler.Core
open Avalonia
open Avalonia.Controls
open Avalonia.Headless
open Avalonia.Input
open Avalonia.Platform
open Avalonia.Threading
open PdfButler.Pdfium
open PdfButler.App.MainWindow
open PdfButler.TestSupport
open Xunit

type TestAppBuilder() =
    static member BuildAvaloniaApp() =
        AppBuilder
            .Configure<PdfButler.App.Program.App>()
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(AvaloniaHeadlessPlatformOptions(UseHeadlessDrawing = false))

[<assembly: AvaloniaTestApplication(typeof<TestAppBuilder>)>]
do ()

let timeout = TimeSpan.FromSeconds 60.0

/// Run UI jobs and render ticks until `cond` holds. Engine work runs on its own thread meanwhile.
let mutable describeState: unit -> string = fun () -> ""

/// Run the real dispatcher loop for a moment, so timers (e.g. the search debounce) fire as in the app.
let private spin (ms: int) =
    let frame = DispatcherFrame()
    Task.Delay(ms).ContinueWith(fun (_: Task) ->
        frame.Continue <- false
        Dispatcher.UIThread.Post(ignore)) |> ignore
    Dispatcher.UIThread.PushFrame frame

let pumpUntil (what: string) (cond: unit -> bool) =
    let sw = Stopwatch.StartNew()
    while not (cond ()) do
        if sw.Elapsed > timeout then failwith $"timed out waiting for: {what} ({describeState ()})"
        Dispatcher.UIThread.RunJobs()
        AvaloniaHeadlessPlatform.ForceRenderTimerTick 1
        spin 2

let pumpTask (t: Task) =
    pumpUntil "task" (fun () -> t.IsCompleted)
    if t.IsFaulted then raise (t.Exception.GetBaseException())

let pumpResult (t: Task<'T>) : 'T =
    pumpTask t
    t.Result

/// Pump for a fixed time (debounce timers, "nothing else happens" checks).
let pumpFor (ms: int) =
    let sw = Stopwatch.StartNew()
    while sw.ElapsedMilliseconds < int64 ms do
        Dispatcher.UIThread.RunJobs()
        AvaloniaHeadlessPlatform.ForceRenderTimerTick 1
        spin 2

/// Wait until no renders are in flight and the engine queue stays empty for a few rounds.
let settle (w: MainWindow) =
    let mutable quiet = 0
    pumpUntil "renders to settle" (fun () ->
        let busy =
            PdfEngine.Shared.QueueLength > 0
            || (w.Canvas.Renderer |> Option.exists (fun r -> r.InFlightCount > 0))
        quiet <- if busy then 0 else quiet + 1
        quiet >= 5)
    Dispatcher.UIThread.RunJobs()
    AvaloniaHeadlessPlatform.ForceRenderTimerTick 1

type Calls() =
    member val Passwords = ResizeArray<string * bool>()
    member val PasswordAnswers = Collections.Generic.Queue<string option>()
    member val Confirms = ResizeArray<Uri>()
    member val ConfirmAnswer = false with get, set
    member val Launches = ResizeArray<Uri>()

/// A shown main window whose dialogs and launcher are replaced by recorders (nothing is ever launched).
let newWindow () =
    let w = MainWindow(PdfEngine.Shared, Width = 900.0, Height = 700.0)
    let calls = Calls()
    w.AskPassword <-
        fun name wrong ->
            calls.Passwords.Add((name, wrong))
            Task.FromResult(if calls.PasswordAnswers.Count > 0 then calls.PasswordAnswers.Dequeue() else None)
    w.ConfirmLink <-
        fun uri ->
            calls.Confirms.Add uri
            Task.FromResult calls.ConfirmAnswer
    w.LaunchUri <-
        fun uri ->
            calls.Launches.Add uri
            Task.FromResult true
    describeState <-
        fun () ->
            $"centre={w.Centre}; page={w.Canvas.CurrentPage}; status='{w.StatusMessage}'; search='{w.SearchStatusText}'; searchBox='{w.SearchBox.Text}'; focus={w.FocusManager.GetFocusedElement()}"
    w.Show()
    pumpFor 20
    w, calls

let openFile (w: MainWindow) (path: string) =
    pumpTask (w.OpenFileAsync path)
    settle w

// ---- files -----------------------------------------------------------------------------------------

let tempDir =
    lazy
        (let d = Path.Combine(Path.GetTempPath(), "pdfbutler-app-tests", string Environment.ProcessId)
         Directory.CreateDirectory d |> ignore
         d)

let synthetic (name: string) (spec: SyntheticPdf.DocSpec) =
    SyntheticPdf.writeFile (Path.Combine(tempDir.Value, name)) spec

let corpusFile (id: string) =
    let d = Corpus.find id
    let p = Corpus.cachePath d
    if not (File.Exists p) then
        if Environment.GetEnvironmentVariable "PDFBUTLER_ALLOW_MISSING_CORPUS" = "1" then
            Assert.Skip $"corpus file {id} not fetched"
        failwith $"corpus file {id} missing: run `dotnet run --project tools/PdfButler.Corpus -- fetch`"
    p

// ---- input -------------------------------------------------------------------------------------------

let press (w: MainWindow) (key: Key) (mods: RawInputModifiers) =
    w.KeyPress(key, mods, PhysicalKey.None, null)
    w.KeyRelease(key, mods, PhysicalKey.None, null)
    pumpFor 5

let typeText (w: MainWindow) (text: string) =
    w.KeyTextInput text
    pumpFor 5

let click (w: MainWindow) (p: Point) =
    w.MouseMove(p, RawInputModifiers.None)
    w.MouseDown(p, MouseButton.Left, RawInputModifiers.None)
    w.MouseUp(p, MouseButton.Left, RawInputModifiers.None)
    pumpFor 5

// ---- geometry ------------------------------------------------------------------------------------------

/// Top-left of the page canvas in window coordinates.
let canvasOrigin (w: MainWindow) =
    match w.Canvas.TranslatePoint(Point(0.0, 0.0), w) |> Option.ofNullable with
    | Some p -> p
    | None -> failwith "canvas is not in the window"

/// Window position of a display-space point (points, y-down) on a page.
let windowPointOf (w: MainWindow) (page: int) (x: float) (y: float) =
    let s = w.Canvas.State
    let c = Layout.canvasOf s.Layout { Page = page; Local = { X = x; Y = y } }
    let o = canvasOrigin w
    Point(o.X + c.X - s.Offset.X, o.Y + c.Y - s.Offset.Y)

/// A page's slot on screen in window coordinates.
let pageRectOnScreen (w: MainWindow) (page: int) =
    let s = w.Canvas.State
    let b = s.Layout.Slots[page].Bounds
    let o = canvasOrigin w
    Rect(o.X + b.X - s.Offset.X, o.Y + b.Y - s.Offset.Y, b.W, b.H)

/// The part of a page that is actually on screen (clipped to the canvas), in window coordinates.
let visiblePageRect (w: MainWindow) (page: int) =
    let o = canvasOrigin w
    let canvasRect = Rect(o, w.Canvas.Bounds.Size)
    (pageRectOnScreen w page).Intersect canvasRect

// ---- frames ------------------------------------------------------------------------------------------

type Frame =
    { Width: int
      Height: int
      /// R, G, B at a pixel.
      Rgb: int -> int -> int * int * int }
    member f.Lum(x: int, y: int) =
        let r, g, b = f.Rgb x y
        (r + g + b) / 3
    member f.LumAt(p: Point) = f.Lum(int p.X, int p.Y)

let capture (w: MainWindow) : Frame =
    use bmp = w.CaptureRenderedFrame()
    use fb = bmp.Lock()
    let wpx = fb.Size.Width
    let hpx = fb.Size.Height
    let stride = fb.RowBytes
    let data = Array.zeroCreate<byte> (stride * hpx)
    Runtime.InteropServices.Marshal.Copy(fb.Address, data, 0, data.Length)
    let bgr = fb.Format = PixelFormat.Bgra8888
    { Width = wpx
      Height = hpx
      Rgb =
        fun x y ->
            if x < 0 || y < 0 || x >= wpx || y >= hpx then failwith $"pixel ({x},{y}) outside {wpx}x{hpx}"
            let o = y * stride + x * 4
            let c0, c1, c2 = int data[o], int data[o + 1], int data[o + 2]
            if bgr then (c2, c1, c0) else (c0, c1, c2) }

/// Save the current frame to artifacts/app/<name>.png (for looking at failures).
let saveFrame (w: MainWindow) (name: string) =
    let dir = Path.Combine(Corpus.repoRoot.Value, "artifacts", "app")
    Directory.CreateDirectory dir |> ignore
    use bmp = w.CaptureRenderedFrame()
    let p = Path.Combine(dir, name + ".png")
    use file = File.Create p
    bmp.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions())
    p

/// Number of pixels in a window rectangle that satisfy `pred r g b`.
let countPixels (f: Frame) (r: Rect) (pred: int -> int -> int -> bool) =
    let mutable n = 0
    for y in int r.Y .. int (r.Bottom) - 1 do
        for x in int r.X .. int (r.Right) - 1 do
            if x >= 0 && y >= 0 && x < f.Width && y < f.Height then
                let rr, gg, bb = f.Rgb x y
                if pred rr gg bb then n <- n + 1
    n
