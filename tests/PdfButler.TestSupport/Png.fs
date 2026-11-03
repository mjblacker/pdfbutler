/// Minimal, dependency-free PNG codec for golden images: 8-bit RGB/RGBA, non-interlaced.
module PdfButler.TestSupport.Png

open System
open System.IO
open System.IO.Compression
open System.Text

/// 24-bit RGB image, rows tightly packed.
type RgbImage = { Width: int; Height: int; Rgb: byte[] }

let private crcTable =
    Array.init 256 (fun n ->
        let mutable c = uint32 n
        for _ in 0..7 do
            c <- if c &&& 1u <> 0u then 0xEDB88320u ^^^ (c >>> 1) else c >>> 1
        c)

let private crc (data: byte[]) (offset: int) (count: int) =
    let mutable c = 0xFFFFFFFFu
    for i in offset .. offset + count - 1 do
        c <- crcTable[int ((c ^^^ uint32 data[i]) &&& 0xFFu)] ^^^ (c >>> 8)
    c ^^^ 0xFFFFFFFFu

let private writeBE (s: Stream) (v: uint32) =
    s.WriteByte(byte (v >>> 24)); s.WriteByte(byte (v >>> 16)); s.WriteByte(byte (v >>> 8)); s.WriteByte(byte v)

let private writeChunk (s: Stream) (kind: string) (data: byte[]) =
    writeBE s (uint32 data.Length)
    let buf = Array.append (Encoding.ASCII.GetBytes kind) data
    s.Write(buf, 0, buf.Length)
    writeBE s (crc buf 0 buf.Length)

/// Convert engine BGRx pixels (any stride) to RGB.
let ofBgrx (width: int) (height: int) (stride: int) (bgrx: byte[]) : RgbImage =
    let rgb = Array.zeroCreate (width * height * 3)
    for y in 0 .. height - 1 do
        for x in 0 .. width - 1 do
            let s = y * stride + x * 4
            let d = (y * width + x) * 3
            rgb[d] <- bgrx[s + 2]
            rgb[d + 1] <- bgrx[s + 1]
            rgb[d + 2] <- bgrx[s]
    { Width = width; Height = height; Rgb = rgb }

let encode (img: RgbImage) : byte[] =
    use ms = new MemoryStream()
    ms.Write([| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |], 0, 8)
    let ihdr = Array.zeroCreate<byte> 13
    let put i (v: int) =
        ihdr[i] <- byte (v >>> 24); ihdr[i + 1] <- byte (v >>> 16); ihdr[i + 2] <- byte (v >>> 8); ihdr[i + 3] <- byte v
    put 0 img.Width
    put 4 img.Height
    ihdr[8] <- 8uy  // bit depth
    ihdr[9] <- 2uy  // colour type RGB
    writeChunk ms "IHDR" ihdr

    // Filter type 1 (Sub) on every row: good compression for rendered pages, trivial to code.
    let rowBytes = img.Width * 3
    let raw = Array.zeroCreate<byte> ((rowBytes + 1) * img.Height)
    for y in 0 .. img.Height - 1 do
        let o = y * (rowBytes + 1)
        raw[o] <- 1uy
        for i in 0 .. rowBytes - 1 do
            let cur = img.Rgb[y * rowBytes + i]
            let left = if i >= 3 then img.Rgb[y * rowBytes + i - 3] else 0uy
            raw[o + 1 + i] <- cur - left

    use z = new MemoryStream()
    (use zs = new ZLibStream(z, CompressionLevel.SmallestSize, true)
     zs.Write(raw, 0, raw.Length))
    writeChunk ms "IDAT" (z.ToArray())
    writeChunk ms "IEND" [||]
    ms.ToArray()

let save (path: string) (img: RgbImage) =
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path)) |> ignore
    File.WriteAllBytes(path, encode img)

let private paeth (a: int) (b: int) (c: int) =
    let p = a + b - c
    let pa = abs (p - a)
    let pb = abs (p - b)
    let pc = abs (p - c)
    if pa <= pb && pa <= pc then a elif pb <= pc then b else c

let decode (bytes: byte[]) : RgbImage =
    let readBE (i: int) = (int bytes[i] <<< 24) ||| (int bytes[i + 1] <<< 16) ||| (int bytes[i + 2] <<< 8) ||| int bytes[i + 3]
    if bytes.Length < 8 || bytes[1] <> 0x50uy || bytes[2] <> 0x4Euy || bytes[3] <> 0x47uy then
        failwith "not a PNG"
    let mutable pos = 8
    let mutable width = 0
    let mutable height = 0
    let mutable colourType = 0
    let idat = new MemoryStream()
    let mutable fin = false
    while not fin && pos + 8 <= bytes.Length do
        let len = readBE pos
        let kind = Encoding.ASCII.GetString(bytes, pos + 4, 4)
        let data = pos + 8
        match kind with
        | "IHDR" ->
            width <- readBE data
            height <- readBE (data + 4)
            if bytes[data + 8] <> 8uy then failwith "only 8-bit PNG supported"
            colourType <- int bytes[data + 9]
            if bytes[data + 12] <> 0uy then failwith "interlaced PNG not supported"
        | "IDAT" -> idat.Write(bytes, data, len)
        | "IEND" -> fin <- true
        | _ -> ()
        pos <- data + len + 4
    let bpp =
        match colourType with
        | 2 -> 3
        | 6 -> 4
        | c -> failwith $"unsupported PNG colour type {c}"
    idat.Position <- 0L
    use zs = new ZLibStream(idat, CompressionMode.Decompress)
    use out = new MemoryStream()
    zs.CopyTo out
    let raw = out.ToArray()
    let rowBytes = width * bpp
    let cur = Array.zeroCreate<byte> (rowBytes * height)
    for y in 0 .. height - 1 do
        let f = raw[y * (rowBytes + 1)]
        let src = y * (rowBytes + 1) + 1
        let dst = y * rowBytes
        for i in 0 .. rowBytes - 1 do
            let a = if i >= bpp then int cur[dst + i - bpp] else 0
            let b = if y > 0 then int cur[dst - rowBytes + i] else 0
            let c = if y > 0 && i >= bpp then int cur[dst - rowBytes + i - bpp] else 0
            let x = int raw[src + i]
            let v =
                match f with
                | 0uy -> x
                | 1uy -> x + a
                | 2uy -> x + b
                | 3uy -> x + ((a + b) >>> 1)
                | 4uy -> x + paeth a b c
                | _ -> failwith "bad PNG filter"
            cur[dst + i] <- byte (v &&& 0xFF)
    let rgb =
        if bpp = 3 then cur
        else Array.init (width * height * 3) (fun k -> cur[(k / 3) * 4 + (k % 3)])
    { Width = width; Height = height; Rgb = rgb }

let load (path: string) = decode (File.ReadAllBytes path)
