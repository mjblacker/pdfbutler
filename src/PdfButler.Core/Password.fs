module PdfButler.Core.Password

open System
open System.Text

/// RFC 3454 table B.1: characters "commonly mapped to nothing".
let private mappedToNothing (c: char) =
    match int c with
    | 0x00AD | 0x034F | 0x1806 | 0x180B | 0x180C | 0x180D | 0x200B | 0x200C | 0x200D | 0x2060 | 0xFEFF -> true
    | v when v >= 0xFE00 && v <= 0xFE0F -> true
    | _ -> false

/// SASLprep (RFC 4013) as required by PDF 2.0 for AES-256 (revision 6) passwords:
/// drop "mapped to nothing" characters, map non-ASCII spaces to U+0020, then NFKC.
/// (Prohibited-character and bidi checks are not needed for password comparison here.)
let saslPrep (password: string) : string =
    if String.IsNullOrEmpty password then
        ""
    else
        let sb = StringBuilder(password.Length)

        for c in password do
            if not (mappedToNothing c) then
                if c <> ' ' && Char.IsWhiteSpace c && int c > 0x7F then sb.Append ' ' |> ignore
                else sb.Append c |> ignore

        sb.ToString().Normalize(NormalizationForm.FormKC)
