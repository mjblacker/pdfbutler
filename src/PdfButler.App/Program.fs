module PdfButler.App.Program

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Styling
open Avalonia.Themes.Fluent
open Avalonia.Threading
open PdfButler.Pdfium
open PdfButler.App.MainWindow

/// Command line: `PdfButler [file.pdf ...]` or `PdfButler --measure file.pdf`.
type Options =
    { Files: string[]
      Measure: bool }

let parseArgs (args: string[]) =
    let rec go (o: Options) =
        function
        | "--measure" :: rest -> go { o with Measure = true } rest
        | f :: rest when not (f.StartsWith "--") -> go { o with Files = Array.append o.Files [| f |] } rest
        | _ :: rest -> go o rest
        | [] -> o
    go { Files = [||]; Measure = false } (List.ofArray args)

let mutable private instance: SingleInstance.Instance option = None

[<DllImport("kernel32.dll")>]
extern bool AttachConsole(int processId)

type App() =
    inherit Application()

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        this.RequestedThemeVariant <- ThemeVariant.Default // follow the system light/dark setting

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            let options = parseArgs (if isNull desktop.Args then [||] else desktop.Args)
            let window = MainWindow(PdfEngine.Shared)
            if not options.Measure then WindowPlacement.attach window
            desktop.MainWindow <- window
            let drainActivations () =
                instance |> Option.iter (fun host ->
                    for paths in host.Drain() do
                        if window.WindowState = WindowState.Minimized then window.WindowState <- WindowState.Normal
                        window.Activate()
                        window.OpenFilesAsync paths |> ignore)
            instance |> Option.iter (fun host -> host.Received.Add(fun () -> Dispatcher.UIThread.Post drainActivations))
            let mutable finished = false
            let finish (code: int) =
                if not finished then
                    finished <- true
                    let json =
                        Diagnostics.snapshotJson
                            [ "file", box (options.Files |> Array.tryHead |> Option.defaultValue "")
                              "exitCode", box code
                              "cachedBytes", box (window.Canvas.Renderer |> Option.map (fun r -> r.CachedBytes) |> Option.defaultValue 0L) ]
                    Console.Out.WriteLine json
                    Console.Out.Flush()
                    desktop.Shutdown code
            if options.Measure then
                window.Canvas.FirstPagePresented <- fun () ->
                    // Let the frame reach the screen before reporting.
                    Dispatcher.UIThread.Post((fun () -> finish 0), DispatcherPriority.Background)
                DispatcherTimer.RunOnce((fun () -> finish 3), TimeSpan.FromSeconds 60.0) |> ignore
            window.Opened.Add(fun _ ->
                Diagnostics.mark "window-opened"
                match options.Files |> Array.tryHead with
                | Some f ->
                    task {
                        if options.Measure then do! window.OpenFileAsync f
                        else do! window.OpenFilesAsync options.Files
                        // An open failure never presents a page: report it and stop.
                        if options.Measure && window.Centre <> DocumentShown then finish 2
                    } |> ignore
                | None -> if options.Measure then finish 1
                drainActivations ())
        | _ -> ()
        base.OnFrameworkInitializationCompleted()

let buildAvaloniaApp () =
    AppBuilder.Configure<App>().UsePlatformDetect()

[<EntryPoint; STAThread>]
let main argv =
    Diagnostics.mark "main"
    let options = parseArgs argv
    if OperatingSystem.IsWindows() && options.Measure then
        // WinExe has no console: write the measurement to the one we were started from.
        AttachConsole -1 |> ignore
    let run () =
        // Load and initialise PDFium on its owner thread while the UI starts.
        Task.Run(fun () -> try PdfEngine.Shared.EnsureReady() with _ -> ()) |> ignore
        buildAvaloniaApp().StartWithClassicDesktopLifetime(argv)
    if OperatingSystem.IsWindows() && not options.Measure then
        use host = new SingleInstance.Instance()
        if host.IsPrimary then
            instance <- Some host
            run ()
        else
            let files = options.Files |> Array.truncate maxTabs |> Array.map Path.GetFullPath
            if host.ForwardAsync(files).GetAwaiter().GetResult() then 0 else 2
    else run ()
