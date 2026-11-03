/// Back/forward navigation history, like a browser.
module PdfButler.Core.History

/// A reading position: page plus distance from the top of the page in display points.
type Location = { Page: int; Top: float }

type State =
    { Back: Location list
      Forward: Location list }

let empty = { Back = []; Forward = [] }
let maxEntries = 100

let private near (a: Location) (b: Location) = a.Page = b.Page && abs (a.Top - b.Top) < 1.0

/// Remember `current` before jumping somewhere else. Clears the forward stack.
let record (current: Location) (s: State) : State =
    let back =
        match s.Back with
        | h :: _ when near h current -> s.Back
        | _ -> current :: s.Back |> List.truncate maxEntries

    { Back = back; Forward = [] }

let back (current: Location) (s: State) : (Location * State) option =
    match s.Back with
    | [] -> None
    | h :: t -> Some(h, { Back = t; Forward = current :: s.Forward |> List.truncate maxEntries })

let forward (current: Location) (s: State) : (Location * State) option =
    match s.Forward with
    | [] -> None
    | h :: t -> Some(h, { Back = current :: s.Back |> List.truncate maxEntries; Forward = t })

let canGoBack s = not (List.isEmpty s.Back)
let canGoForward s = not (List.isEmpty s.Forward)
