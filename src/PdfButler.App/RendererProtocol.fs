/// Versioned, length-bounded binary messages over the helper's private standard streams.
/// No object deserialisation, executable payloads, document paths or passwords in command lines.
module internal PdfButler.App.RendererProtocol

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open PdfButler.Core
open PdfButler.Pdfium

let version = 1
let maxFrameBytes = 80 * 1024 * 1024
let maxStringBytes = 1024 * 1024
let maxPages = 250_000

type Command = Open = 1 | Render = 2 | Text = 3 | Links = 4 | Outline = 5 | Resolve = 6

let invalid () = raise (InvalidDataException "Invalid renderer message.")
let count maximum (r: BinaryReader) =
    let n = r.ReadInt32()
    if n < 0 || n > maximum then invalid ()
    n

let writeString (w: BinaryWriter) (s: string) =
    let bytes = Encoding.UTF8.GetBytes s
    if bytes.Length > maxStringBytes then invalid ()
    w.Write bytes.Length
    w.Write bytes

let readString (r: BinaryReader) =
    let n = count maxStringBytes r
    let b = r.ReadBytes n
    if b.Length <> n then invalid ()
    Encoding.UTF8.GetString b

let writeOption write (w: BinaryWriter) value =
    w.Write(Option.isSome value)
    value |> Option.iter (write w)

let readOption read (r: BinaryReader) = if r.ReadBoolean() then Some(read r) else None
let writeInt (w: BinaryWriter) (v: int) = w.Write v
let writeFloat (w: BinaryWriter) (v: float) = w.Write v
let readFloat (r: BinaryReader) =
    let v = r.ReadDouble()
    if not (Double.IsFinite v) then invalid ()
    v

let writeArray write (w: BinaryWriter) (values: 'T[]) =
    w.Write values.Length
    for v in values do write w v

let readArray maximum read (r: BinaryReader) = Array.init (count maximum r) (fun _ -> read r)

let writeRect (w: BinaryWriter) (v: Rect) =
    w.Write v.X; w.Write v.Y; w.Write v.W; w.Write v.H

let readRect (r: BinaryReader) : Rect =
    { X = readFloat r; Y = readFloat r; W = readFloat r; H = readFloat r }

let writeDestination (w: BinaryWriter) (v: Destination) =
    w.Write v.Page
    writeOption writeFloat w v.X
    writeOption writeFloat w v.Y

let readDestination (r: BinaryReader) : Destination =
    { Page = r.ReadInt32(); X = readOption readFloat r; Y = readOption readFloat r }

let writeDisplayDestination (w: BinaryWriter) (v: DisplayDestination) =
    w.Write v.Page
    writeOption writeFloat w v.Left
    writeOption writeFloat w v.Top

let readDisplayDestination (r: BinaryReader) : DisplayDestination =
    { Page = r.ReadInt32(); Left = readOption readFloat r; Top = readOption readFloat r }

let writeTarget (w: BinaryWriter) = function
    | GoTo d -> w.Write 0; writeDestination w d
    | Uri s -> w.Write 1; writeString w s
    | Unsupported s -> w.Write 2; writeString w s

let readTarget (r: BinaryReader) =
    match r.ReadInt32() with
    | 0 -> GoTo(readDestination r)
    | 1 -> Uri(readString r)
    | 2 -> Unsupported(readString r)
    | _ -> invalid ()

let writeInfo (w: BinaryWriter) (v: DocumentInfo) =
    writeArray (fun w (p: PageInfo) ->
        writeInt w p.Index; writeFloat w p.Size.W; writeFloat w p.Size.H
        writeOption writeString w p.Label) w v.Pages
    writeOption writeString w v.Title
    w.Write(match v.Forms with NoForms -> 0 | AcroForm -> 1 | XfaFull -> 2 | XfaForeground -> 3)
    w.Write v.SignatureCount
    w.Write v.Permissions

let readInfo (r: BinaryReader) : DocumentInfo =
    let pages = readArray maxPages (fun r ->
        let i = r.ReadInt32()
        let w, h = readFloat r, readFloat r
        if w <= 0.0 || h <= 0.0 then invalid ()
        { Index = i; Size = { W = w; H = h }; Label = readOption readString r }) r
    if pages.Length = 0 || pages |> Array.mapi (fun i p -> i <> p.Index) |> Array.exists id then invalid ()
    let title = readOption readString r
    let forms =
        match r.ReadInt32() with
        | 0 -> NoForms | 1 -> AcroForm | 2 -> XfaFull | 3 -> XfaForeground | _ -> invalid ()
    { PageCount = pages.Length; Pages = pages; Title = title; Forms = forms
      SignatureCount = count Int32.MaxValue r; Permissions = r.ReadUInt32() }

let writeOpenError (w: BinaryWriter) = function
    | FileNotFound s -> w.Write 0; writeString w s
    | FileUnreadable s -> w.Write 1; writeString w s
    | FileTooLarge n -> w.Write 2; w.Write n
    | PasswordRequired -> w.Write 3
    | WrongPassword -> w.Write 4
    | UnsupportedSecurity -> w.Write 5
    | Malformed -> w.Write 6
    | EngineError n -> w.Write 7; w.Write n

let readOpenError (r: BinaryReader) =
    match r.ReadInt32() with
    | 0 -> FileNotFound(readString r) | 1 -> FileUnreadable(readString r)
    | 2 -> FileTooLarge(r.ReadInt64()) | 3 -> PasswordRequired | 4 -> WrongPassword
    | 5 -> UnsupportedSecurity | 6 -> Malformed | 7 -> EngineError(r.ReadInt32())
    | _ -> invalid ()

let writeText (w: BinaryWriter) (v: PageText) =
    w.Write v.Page
    writeArray (fun w (c: TextChar) -> writeInt w c.Code; writeRect w c.Box; w.Write c.Generated) w v.Chars

let readText (r: BinaryReader) : PageText =
    { Page = r.ReadInt32()
      Chars = readArray Limits.maxCharsPerPage (fun r ->
          { Code = r.ReadInt32(); Box = readRect r; Generated = r.ReadBoolean() }) r }

let writeLinks (w: BinaryWriter) values = writeArray (fun w (v: Link) -> writeRect w v.Bounds; writeTarget w v.Target) w values
let readLinks (r: BinaryReader) = readArray Limits.maxLinksPerPage (fun r -> { Bounds = readRect r; Target = readTarget r }) r

let writeOutline (w: BinaryWriter) (items: OutlineItem list) =
    let rec write (w: BinaryWriter) (v: OutlineItem) =
        writeString w v.Title
        writeOption writeTarget w v.Target
        w.Write v.StartsOpen
        writeArray write w (List.toArray v.Children)
    writeArray write w (List.toArray items)

let readOutline (r: BinaryReader) =
    let mutable total = 0
    let rec read depth (r: BinaryReader) : OutlineItem =
        total <- total + 1
        if depth > Limits.maxOutlineDepth || total > Limits.maxOutlineItems then invalid ()
        { Title = readString r; Target = readOption readTarget r; StartsOpen = r.ReadBoolean()
          Children = readArray Limits.maxOutlineItems (read (depth + 1)) r |> List.ofArray }
    readArray Limits.maxOutlineItems (read 1) r |> List.ofArray

let writeRenderRequest (w: BinaryWriter) (v: RenderRequest) =
    w.Write v.Page
    w.Write(Rotation.quarterTurns v.Rotation)
    w.Write v.Scale
    writeOption (fun (w: BinaryWriter) (p: PixelRect) -> w.Write p.X; w.Write p.Y; w.Write p.W; w.Write p.H) w v.Clip

let readRenderRequest (r: BinaryReader) : RenderRequest =
    let page = r.ReadInt32()
    let rot = count 3 r |> Rotation.ofQuarterTurns
    { Page = page; Rotation = rot; Scale = readFloat r
      Clip = readOption (fun r -> { X = r.ReadInt32(); Y = r.ReadInt32(); W = r.ReadInt32(); H = r.ReadInt32() }) r }

let writeImage (w: BinaryWriter) (v: RenderedImage) =
    w.Write v.Width; w.Write v.Height; w.Write v.Stride
    w.Write v.Pixels

let readImage (r: BinaryReader) : RenderedImage =
    let w, h, stride = r.ReadInt32(), r.ReadInt32(), r.ReadInt32()
    if w <= 0 || h <= 0 || int64 w * int64 h > Limits.maxBitmapPixels || int64 stride <> int64 w * 4L then invalid ()
    let n = stride * h
    let pixels = r.ReadBytes n
    if pixels.Length <> n then invalid ()
    { Width = w; Height = h; Stride = stride; Pixels = pixels }

let encode write =
    use stream = new MemoryStream()
    use writer = new BinaryWriter(stream, Encoding.UTF8, true)
    write writer
    writer.Flush()
    if stream.Length > int64 maxFrameBytes then invalid ()
    stream.ToArray()

let decode read (bytes: byte[]) =
    use stream = new MemoryStream(bytes, false)
    use reader = new BinaryReader(stream, Encoding.UTF8, true)
    let value = read reader
    if stream.Position <> stream.Length then invalid ()
    value

let readFrameAsync (stream: Stream) (ct: CancellationToken) = task {
    let header = Array.zeroCreate<byte> 4
    do! stream.ReadExactlyAsync(header.AsMemory(), ct)
    let n = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian header
    if n < 1 || n > maxFrameBytes then invalid ()
    let bytes = GC.AllocateUninitializedArray<byte> n
    do! stream.ReadExactlyAsync(bytes.AsMemory(), ct)
    return bytes
}

let writeFrameAsync (stream: Stream) (bytes: byte[]) (ct: CancellationToken) = task {
    if bytes.Length < 1 || bytes.Length > maxFrameBytes then invalid ()
    let header = Array.zeroCreate<byte> 4
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length)
    do! stream.WriteAsync(header.AsMemory(), ct)
    do! stream.WriteAsync(bytes.AsMemory(), ct)
    do! stream.FlushAsync ct
}
