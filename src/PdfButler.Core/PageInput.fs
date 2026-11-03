/// Parsing of what the user types into the page and zoom boxes, and the matching display text.
module PdfButler.Core.PageInput

open System
open System.Globalization

/// A page from the page box: an exact page label first ("iv", "A-3"), then a case-insensitive label,
/// then a 1-based page number. Result is a 0-based index, or None if nothing matches.
let parsePage (labels: string option[]) (input: string) : int option =
    let s = if isNull input then "" else input.Trim()
    let count = labels.Length

    if s = "" || count = 0 then
        None
    else
        let byLabel (cmp: StringComparison) =
            labels |> Array.tryFindIndex (fun l -> match l with Some l -> String.Equals(l.Trim(), s, cmp) | None -> false)

        match byLabel StringComparison.Ordinal with
        | Some i -> Some i
        | None ->
            match Int32.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, n when n >= 1 && n <= count -> Some(n - 1)
            | _ -> byLabel StringComparison.OrdinalIgnoreCase

/// A zoom factor from the zoom box: "150", "150%", " 87.5 % ". None if not a positive number.
let parseZoom (input: string) : float option =
    let s = (if isNull input then "" else input.Trim()).TrimEnd('%').Trim()

    match Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, v when v > 0.0 && not (Double.IsInfinity v) -> Some(v / 100.0)
    | _ -> None

let zoomText (zoom: float) = $"{Math.Round(zoom * 100.0):F0}%%"

/// A label is worth showing only when it differs from the plain page number.
let private usefulLabel (labels: string option[]) (page: int) =
    match labels[page] with
    | Some l when l.Trim() <> "" && l.Trim() <> string (page + 1) -> Some(l.Trim())
    | _ -> None

/// What the page box shows for a page: its label when it has a useful one, else its number.
let pageBoxText (labels: string option[]) (page: int) =
    if page < 0 || page >= labels.Length then ""
    else defaultArg (usefulLabel labels page) (string (page + 1))

/// Status bar text: "Page iv (4 of 300)" or "Page 4 of 300".
let pageStatus (labels: string option[]) (page: int) =
    let n = labels.Length

    if page < 0 || page >= n then ""
    else
        match usefulLabel labels page with
        | Some l -> $"Page {l} ({page + 1} of {n})"
        | None -> $"Page {page + 1} of {n}"
