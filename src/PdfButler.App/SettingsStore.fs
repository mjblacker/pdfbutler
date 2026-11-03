/// Reads and writes settings.json and session.json next to window.json.
/// Failures are swallowed: preferences must never stop the viewer from starting or closing.
module PdfButler.App.SettingsStore

open System
open System.IO
open PdfButler.Core

let directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfButler")
let settingsPath = Path.Combine(directory, "settings.json")
let sessionPath = Path.Combine(directory, "session.json")

let pathComparison = if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal

let private read (path: string) =
    try if File.Exists path then Some(File.ReadAllText path) else None
    with _ -> None

/// Write via a unique temp file and rename, so a crash never leaves a half-written file.
let private writeAtomic (path: string) (text: string) =
    try
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let temp = $"{path}.{Guid.NewGuid():N}.tmp"
        File.WriteAllText(temp, text)
        File.Move(temp, path, true)
    with _ -> ()

let loadSettings () = read settingsPath |> Option.map Settings.parse |> Option.defaultValue Settings.defaults

let saveSettings (s: Settings.Settings) = writeAtomic settingsPath (Settings.serialize s)

let loadSession () =
    read sessionPath
    |> Option.map Settings.parseSession
    |> Option.defaultValue Settings.emptySession
    |> Settings.normalizeSession pathComparison Settings.maxOpenFilesLimit

/// An empty session deletes the file, so no file list is kept when restoring is off.
let saveSession (s: Settings.Session) =
    if s.Tabs.IsEmpty then
        try File.Delete sessionPath with _ -> ()
    else writeAtomic sessionPath (Settings.serializeSession s)
