module PdfButler.Core.Tests.MiscTests

open Xunit
open FsCheck
open FsCheck.FSharp
open PdfButler.Core

[<Fact>]
let ``LRU cache evicts least recently used entries by byte budget`` () =
    let evicted = ResizeArray<string>()
    let c = LruCache<int, string>(10L, (fun s -> int64 s.Length), evicted.Add)
    c.Add(1, "aaaa")
    c.Add(2, "bbbb")
    c.TryGet 1 |> ignore // 1 is now most recent
    c.Add(3, "cccc") // over budget: evict 2
    Assert.Equal<string list>([ "bbbb" ], List.ofSeq evicted)
    Assert.True(c.Contains 1 && c.Contains 3 && not (c.Contains 2))
    Assert.Equal(8L, c.Used)

[<Fact>]
let ``LRU cache never evicts the entry just added, even if it alone exceeds the budget`` () =
    let c = LruCache<int, string>(3L, (fun s -> int64 s.Length), ignore)
    c.Add(1, "a")
    c.Add(2, "huge-entry")
    Assert.True(c.Contains 2)
    Assert.False(c.Contains 1)

[<Fact>]
let ``LRU cache replacing a key disposes the old value and keeps accounting exact`` () =
    let evicted = ResizeArray<string>()
    let c = LruCache<int, string>(100L, (fun s -> int64 s.Length), evicted.Add)
    c.Add(1, "old")
    c.Add(1, "newer")
    Assert.Equal<string list>([ "old" ], List.ofSeq evicted)
    Assert.Equal(5L, c.Used)
    c.RemoveWhere(fun k -> k = 1)
    Assert.Equal(0L, c.Used)

[<Fact>]
let ``LRU cache used bytes always equals the sum of live entries`` () =
    let ops = Gen.listOf (Gen.zip (Gen.choose (0, 20)) (Gen.choose (0, 30)))
    Check.One(Config.QuickThrowOnFailure, Prop.forAll (Arb.fromGen ops) (fun ops ->
        let c = LruCache<int, int>(100L, int64, ignore)
        for (k, size) in ops do
            if size = 0 then c.Remove k else c.Add(k, size)
        let sum = c.Keys |> List.sumBy (fun k -> int64 (c.Peek k).Value)
        c.Used = sum && (c.Used <= 100L || c.Count = 1)))

[<Fact>]
let ``history records jumps and supports back and forward`` () =
    let l p = { History.Page = p; History.Top = 0.0 }
    let h = History.empty |> History.record (l 1) |> History.record (l 2)
    let back = History.back (l 3) h
    Assert.Equal(Some(l 2), back |> Option.map fst)
    let fwd = History.forward (l 2) (snd back.Value)
    Assert.Equal(Some(l 3), fwd |> Option.map fst)
    // Recording the same location twice is not a new entry.
    let dup = History.empty |> History.record (l 1) |> History.record (l 1)
    Assert.Equal(1, dup.Back.Length)

[<Fact>]
let ``history is bounded`` () =
    let h = Seq.fold (fun h i -> History.record { Page = i; Top = 0.0 } h) History.empty [ 1..1000 ]
    Assert.Equal(History.maxEntries, h.Back.Length)

[<Fact>]
let ``SASLprep maps and removes characters as RFC 4013 requires`` () =
    Assert.Equal("SaSLprep", Password.saslPrep "SªSL­prep") // pdf.js saslprep-r6 fixture
    Assert.Equal("a b", Password.saslPrep "a b")
    Assert.Equal("IX", Password.saslPrep "Ⅸ")
    Assert.Equal("plain", Password.saslPrep "plain")
    Assert.Equal("", Password.saslPrep null)

[<Fact>]
let ``open errors have user-facing descriptions`` () =
    for e in [ FileNotFound "x"; FileUnreadable "y"; FileTooLarge 5L; PasswordRequired; WrongPassword; UnsupportedSecurity; Malformed; EngineError 9 ] do
        Assert.False(System.String.IsNullOrWhiteSpace(OpenError.describe e))

[<Fact>]
let ``page box accepts labels first, then numbers, then labels ignoring case`` () =
    // Front matter i..iii, then 1..3; page 6 has label "A-1"; page 7 has no label.
    let labels = [| Some "i"; Some "ii"; Some "iii"; Some "1"; Some "2"; Some "3"; Some "A-1"; None |]
    let parse = PageInput.parsePage labels
    Assert.Equal(Some 1, parse "ii")
    Assert.Equal(Some 1, parse "  ii ")
    // "2" is a label (page index 4), which wins over page number 2.
    Assert.Equal(Some 4, parse "2")
    Assert.Equal(Some 7, parse "8")
    Assert.Equal(Some 6, parse "a-1")
    Assert.Equal(Some 2, parse "III")
    Assert.Equal(None, parse "9")
    Assert.Equal(None, parse "0")
    Assert.Equal(None, parse "-1")
    Assert.Equal(None, parse "")
    Assert.Equal(None, parse null)
    Assert.Equal(None, PageInput.parsePage [||] "1")

[<Fact>]
let ``zoom box accepts percentages with or without the sign`` () =
    Assert.Equal(Some 1.5, PageInput.parseZoom "150")
    Assert.Equal(Some 1.5, PageInput.parseZoom " 150 % ")
    Assert.Equal(Some 0.875, PageInput.parseZoom "87.5%")
    Assert.Equal(None, PageInput.parseZoom "0")
    Assert.Equal(None, PageInput.parseZoom "-50")
    Assert.Equal(None, PageInput.parseZoom "abc")
    Assert.Equal(None, PageInput.parseZoom "")
    Assert.Equal("150%", PageInput.zoomText 1.5)
    Assert.Equal("106%", PageInput.zoomText 1.0612)

[<Fact>]
let ``page status shows a label only when it differs from the number`` () =
    let labels = [| Some "iv"; Some "2"; None; Some "" |]
    Assert.Equal("Page iv (1 of 4)", PageInput.pageStatus labels 0)
    Assert.Equal("Page 2 of 4", PageInput.pageStatus labels 1)
    Assert.Equal("Page 3 of 4", PageInput.pageStatus labels 2)
    Assert.Equal("Page 4 of 4", PageInput.pageStatus labels 3)
    Assert.Equal("iv", PageInput.pageBoxText labels 0)
    Assert.Equal("3", PageInput.pageBoxText labels 2)
    Assert.Equal("", PageInput.pageStatus labels 9)

[<Fact>]
let ``a typed page number round-trips through the page box`` () =
    let labelsGen = Gen.elements [ None; Some "i"; Some "x"; Some "A-1"; Some "cover" ] |> Gen.arrayOf |> Gen.filter (fun a -> a.Length > 0)
    Prop.forAll (Arb.fromGen labelsGen) (fun labels ->
        // Whatever the box shows for a page leads back to that page (labels may repeat: first wins).
        [ 0 .. labels.Length - 1 ]
        |> List.forall (fun p ->
            match PageInput.parsePage labels (PageInput.pageBoxText labels p) with
            | Some q -> q = p || PageInput.pageBoxText labels q = PageInput.pageBoxText labels p
            | None -> false))
    |> Check.QuickThrowOnFailure
