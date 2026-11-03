module PdfButler.App.Icons

open Avalonia.Controls
open Avalonia.Media

// Filled vector icons, inheriting the button foreground in either theme.
let create name =
    let data =
        match name with
        | "Open" -> "M2,4 H9 L11,6 H22 V20 H2 Z M4,8 V18 H20 V8 Z"
        | "Find" -> "M10,2 A8,8 0 1 0 10,18 A8,8 0 0 0 10,2 M10,4 A6,6 0 1 1 10,16 A6,6 0 0 1 10,4 M16,15 L23,22 L21,24 L14,17 Z"
        | "Fit width" -> "M3,3 H5 V21 H3 Z M19,3 H21 V21 H19 Z M6,12 L10,8 V11 H14 V8 L18,12 L14,16 V13 H10 V16 Z"
        | "Fit page" -> "M5,2 H19 V22 H5 Z M7,4 V20 H17 V4 Z M0,0 H4 V2 H2 V6 H0 Z M20,0 H24 V6 H22 V2 H20 Z"
        | "Rotate clockwise" -> "M12,3 A9,9 0 1 0 21,12 H19 A7,7 0 1 1 12,5 H16 V9 L22,4 L16,0 V3 Z"
        | "Outline" -> "M3,2 H19 V22 L11,17 L3,22 Z M5,4 V18 L11,14 L17,18 V4 Z"
        | _ -> "M5,3 L12,10 L19,3 L21,5 L14,12 L21,19 L19,21 L12,14 L5,21 L3,19 L10,12 L3,5 Z"
    PathIcon(Data = Geometry.Parse data, Width = 18.0, Height = 18.0)
