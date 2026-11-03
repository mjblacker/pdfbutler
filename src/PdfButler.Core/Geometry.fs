/// Coordinate spaces used throughout the app:
///  * page space    - PDF user space of a page (points, y-up). Only the engine sees this.
///  * display space - points, y-down, origin top-left of the visible page box, after the page's
///                    own /Rotate. View rotation is *not* applied. Engine outputs use this space.
///  * view space    - device-independent pixels (DIPs) inside one page's on-screen box, after
///                    zoom and view rotation.
///  * canvas space  - DIPs in the whole continuous document strip (all pages stacked).
module PdfButler.Core.Geometry

/// DIPs per point at a given zoom (1.0 = 100% = physical size on a 96-DPI-normalised display).
let dipsPerPoint (zoom: float) = zoom * 96.0 / 72.0

let rotateSize (r: Rotation) (s: Size) : Size =
    if Rotation.swapsAxes r then { W = s.H; H = s.W } else s

/// Display space (points, rotation 0) -> view space for a page of `pageSize` points,
/// rotated clockwise by `r` and scaled by `scale` units per point.
let displayToView (r: Rotation) (pageSize: Size) (scale: float) : Affine =
    let s = scale
    let w = pageSize.W
    let h = pageSize.H

    match r with
    | R0 -> { A = s; B = 0.0; C = 0.0; D = s; E = 0.0; F = 0.0 }
    // (x, y) -> (h - y, x)
    | R90 -> { A = 0.0; B = s; C = -s; D = 0.0; E = h * s; F = 0.0 }
    // (x, y) -> (w - x, h - y)
    | R180 -> { A = -s; B = 0.0; C = 0.0; D = -s; E = w * s; F = h * s }
    // (x, y) -> (y, w - x)
    | R270 -> { A = 0.0; B = -s; C = s; D = 0.0; E = 0.0; F = w * s }

let viewToDisplay (r: Rotation) (pageSize: Size) (scale: float) : Affine =
    match Affine.invert (displayToView r pageSize scale) with
    | Some m -> m
    | None -> Affine.identity

/// Integer pixel size of a whole page rendered at `pixelsPerPoint`, after view rotation.
/// Always at least 1x1 so a zero-sized page box cannot produce an invalid bitmap request.
let devicePageSize (r: Rotation) (pageSize: Size) (pixelsPerPoint: float) : int * int =
    let s = rotateSize r pageSize
    let w = max 1.0 (round (s.W * pixelsPerPoint))
    let h = max 1.0 (round (s.H * pixelsPerPoint))
    let clampInt v = int (min v 1.0e9)
    clampInt w, clampInt h
