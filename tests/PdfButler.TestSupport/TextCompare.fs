/// Engine-independent text comparison: tolerant of whitespace, ordering inside lines,
/// punctuation and line-break differences, but sensitive to missing or garbled words.
module PdfButler.TestSupport.TextCompare

open System
open System.Text

/// NFKC, lower-case, letters and digits only.
let canonical (s: string) =
    if String.IsNullOrEmpty s then ""
    else
        let n = s.Normalize(NormalizationForm.FormKC).ToLowerInvariant()
        let sb = StringBuilder(n.Length)
        for c in n do
            if Char.IsLetterOrDigit c then sb.Append c |> ignore
        sb.ToString()

let private bigrams (s: string) =
    let d = Collections.Generic.Dictionary<struct (char * char), int>()
    for i in 0 .. s.Length - 2 do
        let k = struct (s[i], s[i + 1])
        d[k] <- (match d.TryGetValue k with | true, v -> v + 1 | _ -> 1)
    d

/// Sørensen–Dice similarity of character bigram multisets (1.0 = identical content).
let similarity (a: string) (b: string) =
    let a = canonical a
    let b = canonical b
    if a.Length < 2 && b.Length < 2 then (if a = b then 1.0 else 0.0)
    elif a.Length < 2 || b.Length < 2 then 0.0
    else
        let ba = bigrams a
        let bb = bigrams b
        let mutable inter = 0
        for kv in ba do
            match bb.TryGetValue kv.Key with
            | true, v -> inter <- inter + min v kv.Value
            | _ -> ()
        2.0 * float inter / float ((a.Length - 1) + (b.Length - 1))
