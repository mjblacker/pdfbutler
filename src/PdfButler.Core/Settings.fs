/// User preferences and the last session's open files, with tolerant JSON parsing.
/// A missing, corrupt or partly-invalid file never stops the viewer: bad fields fall back to defaults.
module PdfButler.Core.Settings

open System
open System.IO
open System.Text
open System.Text.Json

type TabLayout =
    | Top
    | Sidebar

type Theme =
    | FollowSystem
    | Light
    | Dark

type DefaultZoom =
    | ActualSize
    | FitWidth
    | FitPage

type Settings =
    { TabLayout: TabLayout
      /// Ask before closing the window while more than one PDF is open.
      ConfirmCloseMultiple: bool
      /// Reopen the previous session's PDFs at startup.
      RestoreSession: bool
      MaxOpenFiles: int
      Theme: Theme
      DefaultZoom: DefaultZoom }

/// Hard bounds for MaxOpenFiles. Each open PDF has its own helper process.
let minOpenFiles = 1
let maxOpenFilesLimit = 100

let defaults =
    { TabLayout = Top
      ConfirmCloseMultiple = true
      RestoreSession = false
      MaxOpenFiles = 30
      Theme = FollowSystem
      DefaultZoom = ActualSize }

let normalize (s: Settings) =
    { s with MaxOpenFiles = max minOpenFiles (min maxOpenFilesLimit s.MaxOpenFiles) }

let private tabLayoutNames = [ Top, "top"; Sidebar, "sidebar" ]
let private themeNames = [ FollowSystem, "system"; Light, "light"; Dark, "dark" ]
let private zoomNames = [ ActualSize, "actual-size"; FitWidth, "fit-width"; FitPage, "fit-page" ]

let private nameOf table value = table |> List.find (fst >> (=) value) |> snd

let private ofName table (name: string) =
    table |> List.tryFind (fun (_, n) -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase)) |> Option.map fst

let private tryProperty (root: JsonElement) (name: string) =
    match root.TryGetProperty name with
    | true, v -> Some v
    | _ -> None

let private tryBool root name =
    match tryProperty root name with
    | Some v when v.ValueKind = JsonValueKind.True -> Some true
    | Some v when v.ValueKind = JsonValueKind.False -> Some false
    | _ -> None

let private tryInt root name =
    match tryProperty root name with
    | Some v when v.ValueKind = JsonValueKind.Number ->
        match v.TryGetInt32() with
        | true, i -> Some i
        | _ -> None
    | _ -> None

let private tryString root name =
    match tryProperty root name with
    | Some v when v.ValueKind = JsonValueKind.String -> Option.ofObj (v.GetString())
    | _ -> None

let private tryEnum table root name = tryString root name |> Option.bind (ofName table)

let private tryParseObject (json: string) (read: JsonElement -> 'T) : 'T option =
    try
        use doc = JsonDocument.Parse(json)
        if doc.RootElement.ValueKind = JsonValueKind.Object then Some(read doc.RootElement) else None
    with _ -> None

/// Parse settings. Unknown keys are ignored; missing or invalid values use the defaults.
let parse (json: string) : Settings =
    tryParseObject json (fun root ->
        let d = defaults
        normalize
            { TabLayout = tryEnum tabLayoutNames root "tabLayout" |> Option.defaultValue d.TabLayout
              ConfirmCloseMultiple = tryBool root "confirmCloseMultiple" |> Option.defaultValue d.ConfirmCloseMultiple
              RestoreSession = tryBool root "restoreSession" |> Option.defaultValue d.RestoreSession
              MaxOpenFiles = tryInt root "maxOpenFiles" |> Option.defaultValue d.MaxOpenFiles
              Theme = tryEnum themeNames root "theme" |> Option.defaultValue d.Theme
              DefaultZoom = tryEnum zoomNames root "defaultZoom" |> Option.defaultValue d.DefaultZoom })
    |> Option.defaultValue defaults

let private write (f: Utf8JsonWriter -> unit) =
    use stream = new MemoryStream()
    (use w = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
     f w)
    Encoding.UTF8.GetString(stream.ToArray())

let serialize (s: Settings) : string =
    let s = normalize s
    write (fun w ->
        w.WriteStartObject()
        w.WriteString("tabLayout", nameOf tabLayoutNames s.TabLayout)
        w.WriteBoolean("confirmCloseMultiple", s.ConfirmCloseMultiple)
        w.WriteBoolean("restoreSession", s.RestoreSession)
        w.WriteNumber("maxOpenFiles", s.MaxOpenFiles)
        w.WriteString("theme", nameOf themeNames s.Theme)
        w.WriteString("defaultZoom", nameOf zoomNames s.DefaultZoom)
        w.WriteEndObject())

// ---- session ------------------------------------------------------------------------------------

/// One open file: full path and the 0-based page at the top of the view. Never holds passwords.
type SessionTab = { Path: string; Page: int }

type Session =
    { Tabs: SessionTab list
      /// Index into Tabs, or -1 when nothing was selected.
      Selected: int }

let emptySession = { Tabs = []; Selected = -1 }

/// Drop blank or duplicate paths, cap the count and keep Selected pointing at a real tab (or -1).
let normalizeSession (comparison: StringComparison) (limit: int) (s: Session) : Session =
    let selectedPath = s.Tabs |> List.tryItem s.Selected |> Option.map (fun t -> t.Path)
    let tabs =
        s.Tabs
        |> List.filter (fun t -> not (String.IsNullOrWhiteSpace t.Path))
        |> List.fold (fun acc t -> if acc |> List.exists (fun a -> String.Equals(a.Path, t.Path, comparison)) then acc else t :: acc) []
        |> List.rev
        |> List.truncate (max 0 limit)
        |> List.map (fun t -> { t with Page = max 0 t.Page })
    let selected =
        match selectedPath |> Option.bind (fun p -> tabs |> List.tryFindIndex (fun t -> String.Equals(t.Path, p, comparison))) with
        | Some i -> i
        | None -> if tabs.IsEmpty then -1 else 0
    { Tabs = tabs; Selected = selected }

let parseSession (json: string) : Session =
    tryParseObject json (fun root ->
        let tabs =
            match tryProperty root "tabs" with
            | Some arr when arr.ValueKind = JsonValueKind.Array ->
                [ for e in arr.EnumerateArray() do
                      if e.ValueKind = JsonValueKind.Object then
                          match tryString e "path" with
                          | Some p -> { Path = p; Page = tryInt e "page" |> Option.defaultValue 0 }
                          | None -> () ]
            | _ -> []
        { Tabs = tabs; Selected = tryInt root "selected" |> Option.defaultValue 0 })
    |> Option.defaultValue emptySession
    |> normalizeSession StringComparison.Ordinal maxOpenFilesLimit

let serializeSession (s: Session) : string =
    write (fun w ->
        w.WriteStartObject()
        w.WriteNumber("selected", s.Selected)
        w.WriteStartArray("tabs")
        for t in s.Tabs do
            w.WriteStartObject()
            w.WriteString("path", t.Path)
            w.WriteNumber("page", t.Page)
            w.WriteEndObject()
        w.WriteEndArray()
        w.WriteEndObject())
