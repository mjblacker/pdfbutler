// Rebuild the PNG and multi-resolution ICO from assets/pdfbutler-icon.svg's geometry.
// Optional asset maintenance, not a build prerequisite. Skia versions match Avalonia's
// transitive dependency; keep these pins aligned when upgrading Avalonia.
// dotnet fsi tools/Build-Icon.fsx [--output <directory>]
#r "nuget: SkiaSharp, 3.119.4"
#r "nuget: SkiaSharp.NativeAssets.Linux, 3.119.4"

open System.IO
open SkiaSharp

let output =
    match fsi.CommandLineArgs |> Array.skip 1 |> Array.toList with
    | [] -> Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../assets"))
    | ["--output"; path] -> Path.GetFullPath path
    | _ -> failwith "Usage: dotnet fsi tools/Build-Icon.fsx [--output <directory>]"
Directory.CreateDirectory output |> ignore

let sizes = [| 16; 20; 24; 32; 48; 64; 128; 256 |]
let render size =
    use bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul)
    use canvas = new SKCanvas(bitmap)
    canvas.Clear SKColors.Transparent
    canvas.Scale(float32 size / 256.0f)
    use navy = new SKPaint(Color = SKColor.Parse "#163342", IsAntialias = true)
    use paper = new SKPaint(Color = SKColor.Parse "#f7f5ef", IsAntialias = true)
    use mint = new SKPaint(Color = SKColor.Parse "#65c9bc", IsAntialias = true)
    use line = new SKPaint(Color = SKColor.Parse "#b4c5c9", IsAntialias = true,
                           Style = SKPaintStyle.Stroke, StrokeWidth = 8.0f, StrokeCap = SKStrokeCap.Round)
    let polygon paint (points: (float32 * float32) list) =
        use path = new SKPath()
        let x, y = points.Head
        path.MoveTo(x, y)
        for x, y in points.Tail do path.LineTo(x, y)
        path.Close()
        canvas.DrawPath(path, paint)
    canvas.DrawRoundRect(SKRect(8.0f, 8.0f, 248.0f, 248.0f), 52.0f, 52.0f, navy)
    polygon paper [66.0f, 38.0f; 150.0f, 38.0f; 190.0f, 78.0f; 190.0f, 218.0f; 66.0f, 218.0f]
    polygon mint [150.0f, 38.0f; 150.0f, 78.0f; 190.0f, 78.0f]
    canvas.DrawLine(94.0f, 100.0f, 162.0f, 100.0f, line)
    canvas.DrawLine(94.0f, 118.0f, 148.0f, 118.0f, line)
    polygon navy [89.0f, 150.0f; 122.0f, 166.0f; 89.0f, 182.0f]
    polygon navy [167.0f, 150.0f; 134.0f, 166.0f; 167.0f, 182.0f]
    canvas.DrawCircle(128.0f, 166.0f, 9.0f, mint)
    canvas.DrawCircle(128.0f, 199.0f, 4.0f, navy)
    use image = SKImage.FromBitmap bitmap
    use data = image.Encode(SKEncodedImageFormat.Png, 100)
    data.ToArray()

let images = sizes |> Array.map render
File.WriteAllBytes(Path.Combine(output, "pdfbutler.png"), Array.last images)
let writeIcon () =
    use file = File.Create(Path.Combine(output, "pdfbutler.ico"))
    use writer = new BinaryWriter(file)
    writer.Write 0us
    writer.Write 1us
    writer.Write(uint16 sizes.Length)
    let mutable offset = 6 + 16 * sizes.Length
    for i in 0 .. sizes.Length - 1 do
        let dimension = if sizes[i] = 256 then 0uy else byte sizes[i]
        writer.Write dimension
        writer.Write dimension
        writer.Write 0uy
        writer.Write 0uy
        writer.Write 1us
        writer.Write 32us
        writer.Write(uint32 images[i].Length)
        writer.Write(uint32 offset)
        offset <- offset + images[i].Length
    for image in images do writer.Write image
writeIcon ()
printfn "Wrote PNG and ICO to %s" output
