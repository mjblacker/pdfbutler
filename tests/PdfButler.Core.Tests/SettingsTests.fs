module PdfButler.Core.Tests.SettingsTests

open System
open Xunit
open FsCheck
open FsCheck.FSharp
open PdfButler.Core
open PdfButler.Core.Settings

let private genSettings =
    gen {
        let! layout = Gen.elements [ Top; Sidebar ]
        let! confirm = Gen.elements [ true; false ]
        let! restore = Gen.elements [ true; false ]
        let! maxOpen = Gen.choose (-50, 500)
        let! theme = Gen.elements [ FollowSystem; Light; Dark ]
        let! zoom = Gen.elements [ ActualSize; FitWidth; FitPage ]
        return
            { TabLayout = layout; ConfirmCloseMultiple = confirm; RestoreSession = restore
              MaxOpenFiles = maxOpen; Theme = theme; DefaultZoom = zoom }
    }

[<Fact>]
let ``defaults allow thirty open files and keep the close prompt`` () =
    Assert.Equal(30, defaults.MaxOpenFiles)
    Assert.True(defaults.ConfirmCloseMultiple)
    Assert.False(defaults.RestoreSession)
    Assert.Equal(Top, defaults.TabLayout)

[<Fact>]
let ``settings round-trip through JSON with the open-file count clamped`` () =
    Check.One(Config.QuickThrowOnFailure, Prop.forAll (Arb.fromGen genSettings) (fun s ->
        let back = parse (serialize s)
        back = normalize s && back.MaxOpenFiles >= minOpenFiles && back.MaxOpenFiles <= maxOpenFilesLimit))

[<Fact>]
let ``missing, corrupt or wrongly typed settings fall back field by field`` () =
    for bad in [ ""; "not json"; "[]"; "null"; "42"; "{"; "\"text\"" ] do
        Assert.Equal(defaults, parse bad)
    let partial = parse """{ "tabLayout": "SIDEBAR", "maxOpenFiles": "lots", "theme": 3, "restoreSession": true, "extra": {} }"""
    Assert.Equal({ defaults with TabLayout = Sidebar; RestoreSession = true }, partial)
    Assert.Equal(maxOpenFilesLimit, (parse """{ "maxOpenFiles": 99999 }""").MaxOpenFiles)
    Assert.Equal(defaults.MaxOpenFiles, (parse """{ "maxOpenFiles": 1e40 }""").MaxOpenFiles)

[<Fact>]
let ``parse never throws on arbitrary text`` () =
    Check.One(Config.QuickThrowOnFailure, Prop.forAll (Arb.fromGen (ArbMap.defaults |> ArbMap.generate<string>)) (fun text ->
        let text = if isNull text then "" else text
        parse text |> ignore
        parseSession text |> ignore
        true))

[<Fact>]
let ``session round-trips paths with unicode and quotes`` () =
    let s =
        { Tabs = [ { Path = @"C:\Docs\Résumé ""final"".pdf"; Page = 4 }; { Path = "/home/u/b.pdf"; Page = 0 } ]
          Selected = 1 }
    Assert.Equal(s, parseSession (serializeSession s))

[<Fact>]
let ``session normalisation drops blanks and duplicates and follows the selected path`` () =
    let s =
        { Tabs =
            [ { Path = "a.pdf"; Page = 1 }; { Path = " "; Page = 0 }; { Path = "A.PDF"; Page = 9 }
              { Path = "b.pdf"; Page = -5 }; { Path = "c.pdf"; Page = 2 } ]
          Selected = 3 }
    let n = normalizeSession StringComparison.OrdinalIgnoreCase 10 s
    Assert.Equal<SessionTab list>([ { Path = "a.pdf"; Page = 1 }; { Path = "b.pdf"; Page = 0 }; { Path = "c.pdf"; Page = 2 } ], n.Tabs)
    Assert.Equal(1, n.Selected)
    // The selected tab falls past the cap: select the first remaining tab.
    let capped = normalizeSession StringComparison.OrdinalIgnoreCase 1 s
    Assert.Equal(1, capped.Tabs.Length)
    Assert.Equal(0, capped.Selected)
    Assert.Equal(emptySession, normalizeSession StringComparison.Ordinal 10 { Tabs = []; Selected = 4 })

[<Fact>]
let ``session selection is always a valid index or -1 when empty`` () =
    let genTab = Gen.zip (Gen.elements [ "a"; "b"; "c"; ""; "A" ]) (Gen.choose (-3, 3)) |> Gen.map (fun (p, n) -> { Path = p; Page = n })
    let gen = Gen.zip3 (Gen.listOf genTab) (Gen.choose (-3, 8)) (Gen.choose (0, 4))
    Check.One(Config.QuickThrowOnFailure, Prop.forAll (Arb.fromGen gen) (fun (tabs, sel, limit) ->
        let n = normalizeSession StringComparison.Ordinal limit { Tabs = tabs; Selected = sel }
        n.Tabs.Length <= limit
        && n.Tabs |> List.forall (fun t -> t.Page >= 0 && t.Path.Trim() <> "")
        && (if n.Tabs.IsEmpty then n.Selected = -1 else n.Selected >= 0 && n.Selected < n.Tabs.Length)))

// ---- tab strip -------------------------------------------------------------------------------------

let private p = TabStrip.defaultParams

[<Fact>]
let ``tabs shrink to fit then scroll, and reveal shows the whole tab`` () =
    let gen = Gen.zip3 (Gen.choose (0, 60)) (Gen.choose (0, 3000)) (Gen.choose (-500, 10000))
    Check.One(Config.QuickThrowOnFailure.WithMaxTest 500, Prop.forAll (Arb.fromGen gen) (fun (count, avail, offset) ->
        let avail = float avail
        let w = TabStrip.tabWidth p count avail
        let ext = TabStrip.extent p count avail
        let clamped = TabStrip.clampOffset p count avail (float offset)
        let sized = w >= p.MinWidth && w <= p.MaxWidth
        // Either everything fits (no scrolling) or tabs are already at their minimum width.
        let fitsOrMinimal = ext <= avail + 1e-6 || abs (w - p.MinWidth) < 1e-9
        let inRange = clamped >= 0.0 && clamped <= max 0.0 (ext - avail) + 1e-9
        let revealOk =
            count = 0
            || (let i = abs offset % count
                let o = TabStrip.reveal p count avail (float offset) i
                let left = TabStrip.tabLeft p count avail i
                o >= 0.0 && (w > avail || (left >= o - 1e-6 && left + w <= o + avail + 1e-6)))
        sized && fitsOrMinimal && inRange && revealOk))

[<Fact>]
let ``tabs that fit use their maximum width and never scroll`` () =
    Assert.Equal(p.MaxWidth, TabStrip.tabWidth p 3 2000.0)
    Assert.False(TabStrip.overflows p 3 2000.0)
    Assert.Equal(0.0, TabStrip.clampOffset p 3 2000.0 250.0)
    Assert.True(TabStrip.overflows p 30 1000.0)
    let last = TabStrip.reveal p 30 1000.0 0.0 29
    Assert.Equal(TabStrip.extent p 30 1000.0 - 1000.0, last, 6)
    Assert.Equal(0.0, TabStrip.scrollBy p 30 1000.0 last -1000.0)
