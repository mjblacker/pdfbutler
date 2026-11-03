/// Space bar as a temporary pan tool: hold Space and drag to pan. A tap (no drag while held)
/// still pages down, or up with Shift, when the key is released.
module PdfButler.Core.SpacePan

type State =
    | Idle
    /// Space is down. `pageUp` remembers Shift at press time; `panned` is set once a drag started.
    | Held of pageUp: bool * panned: bool

/// What to do when Space is released.
type Action =
    | NoAction
    | PageDown
    | PageUp

/// Space pressed. Auto-repeat key-downs while held change nothing.
let press (shift: bool) (s: State) =
    match s with
    | Idle -> Held(shift, false)
    | held -> held

/// A pan drag started while Space was held: the release will no longer page.
let panned (s: State) =
    match s with
    | Held(up, _) -> Held(up, true)
    | Idle -> Idle

/// Space released: back to idle, paging only if it was a tap.
let release (s: State) =
    match s with
    | Held(true, false) -> Idle, PageUp
    | Held(false, false) -> Idle, PageDown
    | _ -> Idle, NoAction

/// Focus or the window was lost while held (the release may never arrive): drop it without paging.
let cancel (_: State) = Idle

let isHeld (s: State) = s <> Idle
