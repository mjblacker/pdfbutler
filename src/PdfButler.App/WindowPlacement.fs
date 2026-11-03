/// Window bounds are physical screen coordinates; sizes are device-independent units.
module PdfButler.App.WindowPlacement

open System
open System.IO
open System.Text.Json
open Avalonia
open Avalonia.Controls
open Avalonia.Threading

[<CLIMutable>]
type Placement =
    { X: int; Y: int; Width: float; Height: float; Maximized: bool }

let constrain (area: PixelRect) scaling (p: Placement) =
    let dimension value fallback maximum =
        min maximum (max 1.0 (if Double.IsFinite value && value > 0.0 then value else fallback))
    let width = dimension p.Width 1180.0 (float area.Width / scaling)
    // Leave room for the native title bar and frame.
    let height = dimension p.Height 850.0 (max 1.0 (float area.Height / scaling - 40.0))
    { p with Width = width; Height = height
             X = max area.X (min p.X (area.Right - int (ceil (width * scaling))))
             Y = max area.Y (min p.Y (area.Bottom - int (ceil ((height + 40.0) * scaling)))) }

/// window.json, hand-written because Native AOT disables reflection-based JsonSerializer.
/// Property names match the earlier serializer output so existing files still load.
let serialize (p: Placement) =
    use stream = new MemoryStream()
    (use w = new Utf8JsonWriter(stream)
     w.WriteStartObject()
     w.WriteNumber("X", p.X)
     w.WriteNumber("Y", p.Y)
     w.WriteNumber("Width", p.Width)
     w.WriteNumber("Height", p.Height)
     w.WriteBoolean("Maximized", p.Maximized)
     w.WriteEndObject())
    Text.Encoding.UTF8.GetString(stream.ToArray())

/// None for invalid JSON, a non-object, or a present value of the wrong type. Missing values are 0/false
/// (as before); `constrain` replaces unusable sizes.
let parse (json: string) : Placement option =
    try
        use doc = JsonDocument.Parse json
        let root = doc.RootElement
        if root.ValueKind <> JsonValueKind.Object then None
        else
            let read name (get: JsonElement -> 'T option) (fallback: 'T) =
                match root.TryGetProperty(name: string) with
                | true, v -> get v
                | _ -> Some fallback
            let integer (v: JsonElement) =
                if v.ValueKind <> JsonValueKind.Number then None
                else match v.TryGetInt32() with
                     | true, i -> Some i
                     | _ -> None
            let number (v: JsonElement) =
                if v.ValueKind <> JsonValueKind.Number then None
                else match v.TryGetDouble() with
                     | true, d -> Some d
                     | _ -> None
            let boolean (v: JsonElement) =
                match v.ValueKind with
                | JsonValueKind.True -> Some true
                | JsonValueKind.False -> Some false
                | _ -> None
            match read "X" integer 0, read "Y" integer 0, read "Width" number 0.0, read "Height" number 0.0, read "Maximized" boolean false with
            | Some x, Some y, Some width, Some height, Some maximized ->
                Some { X = x; Y = y; Width = width; Height = height; Maximized = maximized }
            | _ -> None
    with :? JsonException -> None

let attach (window: Window) =
    let path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfButler", "window.json")
    let mutable normal = { X = 0; Y = 0; Width = 1180.0; Height = 850.0; Maximized = false }
    let mutable maximized = false
    let capture () =
        if window.WindowState <> WindowState.Minimized then
            maximized <- window.WindowState = WindowState.Maximized
        if window.WindowState = WindowState.Normal then
            normal <- { X = window.Position.X; Y = window.Position.Y
                        Width = window.ClientSize.Width; Height = window.ClientSize.Height; Maximized = false }
    let fit p =
        let screen =
            window.Screens.All |> Seq.tryFind (fun s -> s.WorkingArea.Contains(PixelPoint(p.X, p.Y)))
            |> Option.orElseWith (fun () -> Option.ofObj window.Screens.Primary)
        match screen with
        | Some s -> constrain s.WorkingArea s.Scaling p
        | None -> p
    let apply p =
        window.MinWidth <- min 440.0 p.Width
        window.MinHeight <- min 300.0 p.Height
        window.Width <- p.Width
        window.Height <- p.Height
        window.Position <- PixelPoint(p.X, p.Y)
    let timer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 250.0)
    timer.Tick.Add(fun _ -> capture ())
    window.Opened.Add(fun _ ->
        capture ()
        try
            match (if File.Exists path then parse (File.ReadAllText path) else None) with
            | Some saved ->
                normal <- fit saved
                apply normal
                if saved.Maximized then window.WindowState <- WindowState.Maximized
                maximized <- saved.Maximized
            | None -> ()
        with _ -> () // Missing/corrupt preferences must never prevent opening the viewer.
        timer.Start())
    let screensChanged = EventHandler(fun _ _ ->
        normal <- fit normal
        if window.WindowState = WindowState.Normal then apply normal)
    window.Screens.Changed.AddHandler screensChanged
    window.Closing.Add(fun _ -> capture ())
    window.Closed.Add(fun _ ->
        timer.Stop()
        window.Screens.Changed.RemoveHandler screensChanged
        try
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            let saved = { normal with Maximized = maximized }
            let temp = path + ".tmp"
            File.WriteAllText(temp, serialize saved)
            File.Move(temp, path, true)
        with _ -> ())
