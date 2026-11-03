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
        | "Scroll left" -> "M15,4 L17,6 L11,12 L17,18 L15,20 L7,12 Z"
        | "Scroll right" -> "M9,4 L7,6 L13,12 L7,18 L9,20 L17,12 Z"
        | "All tabs" -> "M4,8 L6,6 L12,12 L18,6 L20,8 L12,16 Z"
        | "Settings" ->
            "M10.06,3.93 L10.11,1.16 L13.89,1.16 L13.94,3.93 L16.34,4.92 L18.33,3 L21,5.67 L19.08,7.66 L20.07,10.06 L22.84,10.11 \
             L22.84,13.89 L20.07,13.94 L19.08,16.34 L21,18.33 L18.33,21 L16.34,19.08 L13.94,20.07 L13.89,22.84 L10.11,22.84 L10.06,20.07 \
             L7.66,19.08 L5.67,21 L3,18.33 L4.92,16.34 L3.93,13.94 L1.16,13.89 L1.16,10.11 L3.93,10.06 L4.92,7.66 L3,5.67 L5.67,3 L7.66,4.92 Z \
             M12,8.5 A3.5,3.5 0 1 0 12,15.5 A3.5,3.5 0 1 0 12,8.5 Z"
        | _ -> "M5,3 L12,10 L19,3 L21,5 L14,12 L21,19 L19,21 L12,14 L5,21 L3,19 L10,12 L3,5 Z"
    PathIcon(Data = Geometry.Parse data, Width = 18.0, Height = 18.0)
