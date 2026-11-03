module PdfButler.Core.Tests.TextSearchTests

open Xunit
open PdfButler.Core
open PdfButler.Core.TextLayer

/// Lay out a string as characters 10pt wide on lines 20pt apart; '\n' becomes a generated break.
let private layout (page: int) (s: string) : PageText =
    let chars = ResizeArray<TextChar>()
    let mutable x = 0.0
    let mutable y = 0.0
    let mutable i = 0
    while i < s.Length do
        let code = System.Char.ConvertToUtf32(s, i)
        let width = if System.Char.IsSurrogatePair(s, i) then 2 else 1
        if s[i] = '\n' then
            chars.Add { Code = 0x0D; Box = Rect.make 0.0 0.0 0.0 0.0; Generated = true }
            chars.Add { Code = 0x0A; Box = Rect.make 0.0 0.0 0.0 0.0; Generated = true }
            x <- 0.0
            y <- y + 20.0
        else
            chars.Add { Code = code; Box = Rect.make x y 10.0 12.0; Generated = false }
            x <- x + 10.0
        i <- i + width
    { Page = page; Chars = chars.ToArray() }

let private hit p s e : Search.Hit = { Page = p; Start = s; End = e }

let private find q s = Search.findInPage Search.defaultOptions q (layout 0 s) |> Array.map (fun h -> h.Start, h.End)

[<Fact>]
let ``case-insensitive by default, case-sensitive on request`` () =
    Assert.Equal<(int * int)[]>([| (0, 4); (12, 16) |], find "hello" "Hello world HELLO")
    let t = layout 0 "Hello world HELLO"
    Assert.Single(Search.findInPage { MatchCase = true } "HELLO" t) |> ignore

[<Fact>]
let ``whitespace runs and line breaks match a single space`` () =
    Assert.Equal<(int * int)[]>([| (0, 12) |], find "alpha beta" "alpha  \nbeta")
    Assert.Equal<(int * int)[]>([| (0, 9) |], find "  alpha   beta " "alpha beta")

[<Fact>]
let ``ligatures and compatibility forms match their plain spelling`` () =
    Assert.NotEmpty(find "office" "oﬃce")   // ffi ligature
    Assert.NotEmpty(find "abc" "ａｂｃ") // full-width
    Assert.Empty(find "cafe" "café")               // diacritics are significant

[<Fact>]
let ``hyphenated line breaks are joined`` () =
    Assert.NotEmpty(find "example" "exam-\nple")
    Assert.NotEmpty(find "example" "exam­ple")

[<Fact>]
let ``hit ranges map back to source characters, including astral code points`` () =
    let t = layout 0 "a😀b ccc"
    let hits = Search.findInPage Search.defaultOptions "ccc" t
    Assert.Equal(1, hits.Length)
    Assert.Equal("ccc", extract t.Chars hits[0].Start hits[0].End)

[<Fact>]
let ``empty query and empty page find nothing`` () =
    Assert.Empty(find "" "anything")
    Assert.Empty(find "x" "")

[<Fact>]
let ``search state moves next and previous with wrap-around`` () =
    let s =
        Search.start "q" 3
        |> Search.addPage 2 [| { Page = 2; Start = 0; End = 0 } |] true
        |> Search.addPage 0 [| { Page = 0; Start = 5; End = 5 }; { Page = 0; Start = 9; End = 9 } |] true
        |> Search.addPage 1 [||] true
    // The first page that reported hits gets the current hit (search starts from the current page).
    Assert.Equal(Some(hit 2 0 0), s.Current)
    let n = Search.next 0 s
    Assert.Equal(Some(hit 0 5 5), n.Current) // wrapped
    Assert.Equal(Some(hit 2 0 0), (Search.previous 0 n).Current)
    Assert.Equal(Search.Found(1, 3), Search.status n)

[<Fact>]
let ``status distinguishes no text layer from no matches`` () =
    let scanned = Search.start "q" 2 |> Search.addPage 0 [||] false |> Search.addPage 1 [||] false
    Assert.Equal(Search.NoTextLayer, Search.status scanned)
    let withText = Search.start "q" 1 |> Search.addPage 0 [||] true
    Assert.Equal(Search.NoMatches, Search.status withText)
    Assert.Equal(Search.Searching(0, 5, 0), Search.status (Search.start "q" 5))

[<Fact>]
let ``line rectangles merge characters per line`` () =
    let t = layout 0 "abc\ndef"
    let rects = lineRects t.Chars 0 (t.Chars.Length - 1)
    Assert.Equal(2, rects.Length)
    Assert.Equal(Rect.make 0.0 0.0 30.0 12.0, rects[0])
    Assert.Equal(Rect.make 0.0 20.0 30.0 12.0, rects[1])

[<Fact>]
let ``extract turns engine line breaks into newlines`` () =
    let t = layout 0 "ab\ncd"
    Assert.Equal("ab\ncd", fullText t)

[<Fact>]
let ``hit testing and nearest character`` () =
    let t = layout 0 "abc\ndef"
    Assert.Equal(Some 1, charAt t 0.0 { X = 15.0; Y = 5.0 })
    Assert.Equal(None, charAt t 0.0 { X = 500.0; Y = 5.0 })
    // Far right of line 1 snaps to the last character of that line, not line 2.
    Assert.Equal(Some 2, nearestChar t { X = 500.0; Y = 5.0 })
    let line2First = t.Chars |> Array.findIndex (fun c -> c.Code = int 'd')
    Assert.Equal(Some line2First, nearestChar t { X = -50.0; Y = 25.0 })

[<Fact>]
let ``word selection`` () =
    let t = layout 0 "foo bar_baz qux"
    Assert.Equal((4, 10), wordAt t.Chars 6)
    Assert.Equal((3, 3), wordAt t.Chars 3)

[<Fact>]
let ``selection ranges across pages and text`` () =
    let pagesText = [| layout 0 "first page"; layout 1 "second page"; layout 2 "third" |]
    let sel = { Anchor = { Page = 2; Index = 2 }; Focus = { Page = 0; Index = 6 } } // reversed drag
    Assert.Equal(Some(6, 9), Selection.rangeOnPage sel 0 10)
    Assert.Equal(Some(0, 10), Selection.rangeOnPage sel 1 11)
    Assert.Equal(Some(0, 2), Selection.rangeOnPage sel 2 5)
    Assert.Equal(None, Selection.rangeOnPage sel 3 5)
    Assert.Equal("page\nsecond page\nthi", Selection.text sel (fun p -> pagesText[p]))

[<Fact>]
let ``text outside the visible page box is dropped`` () =
    let c x code = { Code = code; Box = Rect.make x 10.0 5.0 10.0; Generated = false }
    let br = { Code = 0x0A; Box = Rect.make 0.0 0.0 0.0 0.0; Generated = true }
    let chars = [| c -50.0 (int 'h'); c 10.0 (int 'a'); br; c 500.0 (int 'z'); br; c 20.0 (int 'b'); br |]
    let kept = visibleOnly { W = 100.0; H = 100.0 } chars
    Assert.Equal("a\nb", extract kept 0 (kept.Length - 1))

[<Fact>]
let ``engine line breaks (\r\n) between visible lines survive the visibility filter`` () =
    let c x code = { Code = code; Box = Rect.make x 10.0 5.0 10.0; Generated = false }
    let gen code = { Code = code; Box = Rect.make 0.0 0.0 0.0 0.0; Generated = true }
    let chars = [| c 10.0 (int 'a'); gen 0x0D; gen 0x0A; c 20.0 (int 'b'); c 500.0 (int 'z'); gen 0x0D; gen 0x0A; c 30.0 (int 'c') |]
    let kept = visibleOnly { W = 100.0; H = 100.0 } chars
    Assert.Equal("a\nbc", extract kept 0 (kept.Length - 1))
