namespace PdfButler.Core

open System

/// A point in some 2D space. Which space is always stated by the API that returns it.
[<Struct>]
type Point = { X: float; Y: float }

[<Struct>]
type Size = { W: float; H: float }

/// Axis-aligned rectangle, y-down (top-left origin).
[<Struct>]
type Rect =
    { X: float; Y: float; W: float; H: float }
    member r.Right = r.X + r.W
    member r.Bottom = r.Y + r.H
    member r.IsEmpty = r.W <= 0.0 || r.H <= 0.0

module Point =
    let inline make x y : Point = { X = x; Y = y }

module Size =
    let inline make w h : Size = { W = w; H = h }

module Rect =
    let inline make x y w h : Rect = { X = x; Y = y; W = w; H = h }

    let ofLTRB l t r b : Rect =
        let x0, x1 = min l r, max l r
        let y0, y1 = min t b, max t b
        { X = x0; Y = y0; W = x1 - x0; H = y1 - y0 }

    let contains (p: Point) (r: Rect) =
        p.X >= r.X && p.X <= r.Right && p.Y >= r.Y && p.Y <= r.Bottom

    let intersects (a: Rect) (b: Rect) =
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom

    let intersect (a: Rect) (b: Rect) : Rect option =
        let x0 = max a.X b.X
        let y0 = max a.Y b.Y
        let x1 = min a.Right b.Right
        let y1 = min a.Bottom b.Bottom
        if x1 > x0 && y1 > y0 then Some(ofLTRB x0 y0 x1 y1) else None

    let union (a: Rect) (b: Rect) : Rect =
        ofLTRB (min a.X b.X) (min a.Y b.Y) (max a.Right b.Right) (max a.Bottom b.Bottom)

    let inflate d (r: Rect) : Rect = { X = r.X - d; Y = r.Y - d; W = r.W + 2.0 * d; H = r.H + 2.0 * d }

    let offset dx dy (r: Rect) : Rect = { r with X = r.X + dx; Y = r.Y + dy }

    let center (r: Rect) : Point = { X = r.X + r.W / 2.0; Y = r.Y + r.H / 2.0 }

    /// Distance from a point to the rectangle (0 when inside).
    let distance (p: Point) (r: Rect) =
        let dx = max 0.0 (max (r.X - p.X) (p.X - r.Right))
        let dy = max 0.0 (max (r.Y - p.Y) (p.Y - r.Bottom))
        sqrt (dx * dx + dy * dy)

/// View rotation in clockwise quarter turns. This is applied on top of the page's own /Rotate.
type Rotation =
    | R0
    | R90
    | R180
    | R270

module Rotation =
    let quarterTurns =
        function
        | R0 -> 0
        | R90 -> 1
        | R180 -> 2
        | R270 -> 3

    let ofQuarterTurns n =
        match ((n % 4) + 4) % 4 with
        | 0 -> R0
        | 1 -> R90
        | 2 -> R180
        | _ -> R270

    let clockwise r = ofQuarterTurns (quarterTurns r + 1)
    let counterClockwise r = ofQuarterTurns (quarterTurns r - 1)
    let swapsAxes r = quarterTurns r % 2 = 1

/// 2D affine transform: x' = A*x + C*y + E ; y' = B*x + D*y + F (PDF / PDFium convention).
[<Struct>]
type Affine =
    { A: float; B: float; C: float; D: float; E: float; F: float }

module Affine =
    let identity = { A = 1.0; B = 0.0; C = 0.0; D = 1.0; E = 0.0; F = 0.0 }

    let apply (m: Affine) (p: Point) : Point =
        { X = m.A * p.X + m.C * p.Y + m.E; Y = m.B * p.X + m.D * p.Y + m.F }

    /// Bounding box of the transformed rectangle.
    let applyRect (m: Affine) (r: Rect) : Rect =
        let pts =
            [| apply m { X = r.X; Y = r.Y }
               apply m { X = r.Right; Y = r.Y }
               apply m { X = r.X; Y = r.Bottom }
               apply m { X = r.Right; Y = r.Bottom } |]

        let xs = pts |> Array.map (fun p -> p.X)
        let ys = pts |> Array.map (fun p -> p.Y)
        Rect.ofLTRB (Array.min xs) (Array.min ys) (Array.max xs) (Array.max ys)

    /// Apply `first`, then `second`.
    let andThen (second: Affine) (first: Affine) : Affine =
        { A = second.A * first.A + second.C * first.B
          B = second.B * first.A + second.D * first.B
          C = second.A * first.C + second.C * first.D
          D = second.B * first.C + second.D * first.D
          E = second.A * first.E + second.C * first.F + second.E
          F = second.B * first.E + second.D * first.F + second.F }

    let determinant (m: Affine) = m.A * m.D - m.B * m.C

    let invert (m: Affine) : Affine option =
        let det = determinant m

        if abs det < 1e-12 then
            None
        else
            Some
                { A = m.D / det
                  B = -m.B / det
                  C = -m.C / det
                  D = m.A / det
                  E = (m.C * m.F - m.D * m.E) / det
                  F = (m.B * m.E - m.A * m.F) / det }

/// Where a link or outline entry points inside the same document, exactly as stored in the PDF:
/// a page index plus an optional position in that page's PDF user space (points, y-up).
/// Converting to display space needs the target page loaded, so the engine does it on demand.
type Destination =
    { Page: int
      X: float option
      Y: float option }

/// A destination converted to display space of the target page (points, y-down, view rotation 0).
type DisplayDestination =
    { Page: int
      Left: float option
      Top: float option }

type LinkTarget =
    | GoTo of Destination
    | Uri of string
    /// Remote documents, launch actions, named actions, embedded go-to, ... Never followed.
    | Unsupported of kind: string

/// A clickable link area on a page, in display space.
type Link = { Bounds: Rect; Target: LinkTarget }

type OutlineItem =
    { Title: string
      Target: LinkTarget option
      StartsOpen: bool
      Children: OutlineItem list }

/// One character from the engine's text layer. `Box` is in display space.
[<Struct>]
type TextChar =
    { Code: int
      Box: Rect
      /// Characters synthesised by the engine (e.g. line breaks, inferred spaces).
      Generated: bool }

type PageText = { Page: int; Chars: TextChar[] }

type FormKind =
    | NoForms
    | AcroForm
    | XfaFull
    | XfaForeground

type PageInfo =
    { Index: int
      /// Size in points after the page's own /Rotate, before any view rotation.
      Size: Size
      Label: string option }

type DocumentInfo =
    { PageCount: int
      Pages: PageInfo[]
      Title: string option
      Forms: FormKind
      SignatureCount: int
      /// Raw PDF permission bits (informational only; not a security boundary).
      Permissions: uint32 }

type OpenError =
    | FileNotFound of path: string
    | FileUnreadable of reason: string
    | FileTooLarge of bytes: int64
    | PasswordRequired
    | WrongPassword
    | UnsupportedSecurity
    | Malformed
    | EngineError of code: int

module OpenError =
    let describe =
        function
        | FileNotFound p -> $"The file could not be found: {p}"
        | FileUnreadable r -> $"The file could not be read: {r}"
        | FileTooLarge b -> $"The file is too large to open ({b:N0} bytes)."
        | PasswordRequired -> "This document is password protected."
        | WrongPassword -> "The password was not correct."
        | UnsupportedSecurity -> "This document uses an encryption or security scheme that isn't supported."
        | Malformed -> "This file is damaged or is not a PDF document."
        | EngineError code -> $"The PDF engine could not open this file (error {code})."
