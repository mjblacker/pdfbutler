module PdfButler.App.Program

open System
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

/// Command line: `PdfButler [file.pdf]` or `PdfButler --measure file.pdf`.
type Options =
    { File: string option
      Measure: bool }

let parseArgs (args: string[]) =
    let rec go (o: Options) =
        function
        | "--measure" :: f :: rest -> go { o with File = Some f; Measure = true } rest
        | f :: rest when not (f.StartsWith "--") && o.File.IsNone -> go { o with File = Some f } rest
        | _ :: rest -> go o rest
        | [] -> o
    go { File = None; Measure = false } (List.ofArray args)

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
            desktop.MainWindow <- window
            let mutable finished = false
            let finish (code: int) =
                if not finished then
                    finished <- true
                    let json =
                        Diagnostics.snapshotJson
                            [ "file", box (defaultArg options.File "")
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
                match options.File with
                | Some f ->
                    task {
                        do! window.OpenFileAsync f
                        // An open failure never presents a page: report it and stop.
                        if options.Measure && window.Centre <> DocumentShown then finish 2
                    } |> ignore
                | None -> if options.Measure then finish 1)
        | _ -> ()
        base.OnFrameworkInitializationCompleted()

let buildAvaloniaApp () =
    AppBuilder.Configure<App>().UsePlatformDetect()

[<EntryPoint; STAThread>]
let main argv =
    Diagnostics.mark "main"
    if OperatingSystem.IsWindows() && Array.contains "--measure" argv then
        // WinExe has no console: write the measurement to the one we were started from.
        AttachConsole -1 |> ignore
    // Load and initialise PDFium on its owner thread while the UI starts.
    Task.Run(fun () -> try PdfEngine.Shared.EnsureReady() with _ -> ()) |> ignore
    buildAvaloniaApp().StartWithClassicDesktopLifetime(argv)
