/// Horizontal tab strip sizing and scrolling. Tabs shrink from MaxWidth down to MinWidth to fit;
/// past that the strip scrolls (arrow buttons, wheel) instead of showing a scroll bar.
module PdfButler.Core.TabStrip

type Params =
    { MinWidth: float
      MaxWidth: float
      Spacing: float }

let defaultParams = { MinWidth = 110.0; MaxWidth = 220.0; Spacing = 4.0 }

/// Width of every tab when `count` tabs share `available` DIPs.
let tabWidth (p: Params) (count: int) (available: float) =
    if count <= 0 then p.MaxWidth
    else
        let fit = (max 0.0 available - p.Spacing * float (count - 1)) / float count
        max p.MinWidth (min p.MaxWidth fit)

/// Total width of the strip's content.
let extent (p: Params) (count: int) (available: float) =
    if count <= 0 then 0.0
    else
        let w = tabWidth p count available
        w * float count + p.Spacing * float (count - 1)

let overflows (p: Params) count available = extent p count available > available + 0.5

/// Left edge of tab `index` in content coordinates.
let tabLeft (p: Params) (count: int) (available: float) (index: int) =
    float index * (tabWidth p count available + p.Spacing)

let clampOffset (p: Params) (count: int) (available: float) (offset: float) =
    let maxOffset = max 0.0 (extent p count available - available)
    max 0.0 (min maxOffset offset)

/// Smallest scroll change that shows tab `index` completely (or as much as fits).
let reveal (p: Params) (count: int) (available: float) (offset: float) (index: int) =
    if index < 0 || index >= count then clampOffset p count available offset
    else
        let left = tabLeft p count available index
        let right = left + tabWidth p count available
        let offset =
            if left < offset then left
            elif right > offset + available then right - available
            else offset
        clampOffset p count available offset

/// Scroll by about one tab in either direction (`steps` may be negative).
let scrollBy (p: Params) (count: int) (available: float) (offset: float) (steps: float) =
    clampOffset p count available (offset + steps * (tabWidth p count available + p.Spacing))
