/// Visual regression comparison between a golden image and a fresh render.
module PdfButler.TestSupport.ImageCompare

open PdfButler.TestSupport.Png

type Tolerance =
    { /// A pixel counts as different when any channel differs by more than this.
      ChannelThreshold: int
      /// Fail when more than this fraction of pixels differ.
      MaxDiffFraction: float }

let defaultTolerance = { ChannelThreshold = 48; MaxDiffFraction = 0.002 }

type Result =
    | SizeMismatch of expected: (int * int) * actual: (int * int)
    | Compared of diffPixels: int * fraction: float * maxChannelDiff: int

let compare (expected: RgbImage) (actual: RgbImage) (tol: Tolerance) : Result * RgbImage option =
    if expected.Width <> actual.Width || expected.Height <> actual.Height then
        SizeMismatch((expected.Width, expected.Height), (actual.Width, actual.Height)), None
    else
        let n = expected.Width * expected.Height
        let diff = Array.zeroCreate<byte> (n * 3)
        let mutable count = 0
        let mutable maxD = 0
        for p in 0 .. n - 1 do
            let o = p * 3
            let d0 = abs (int expected.Rgb[o] - int actual.Rgb[o])
            let d1 = abs (int expected.Rgb[o + 1] - int actual.Rgb[o + 1])
            let d2 = abs (int expected.Rgb[o + 2] - int actual.Rgb[o + 2])
            let d = max d0 (max d1 d2)
            if d > maxD then maxD <- d
            if d > tol.ChannelThreshold then
                count <- count + 1
                diff[o] <- 255uy; diff[o + 1] <- 0uy; diff[o + 2] <- 0uy
            else
                // Faded grey of the expected image for context.
                let g = (int expected.Rgb[o] + int expected.Rgb[o + 1] + int expected.Rgb[o + 2]) / 3
                let v = byte (192 + g / 4)
                diff[o] <- v; diff[o + 1] <- v; diff[o + 2] <- v
        Compared(count, float count / float (max 1 n), maxD),
        Some { Width = expected.Width; Height = expected.Height; Rgb = diff }

let passes (tol: Tolerance) (r: Result) =
    match r with
    | SizeMismatch _ -> false
    | Compared(_, fraction, _) -> fraction <= tol.MaxDiffFraction

/// Fraction of pixels that are not (near) white. Used to spot blank renders.
let inkFraction (img: RgbImage) =
    let n = img.Width * img.Height
    let mutable ink = 0
    for p in 0 .. n - 1 do
        let o = p * 3
        if int img.Rgb[o] + int img.Rgb[o + 1] + int img.Rgb[o + 2] < 3 * 235 then ink <- ink + 1
    float ink / float (max 1 n)
