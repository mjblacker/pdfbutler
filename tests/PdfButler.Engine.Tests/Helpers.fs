module PdfButler.Engine.Tests.Helpers

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open PdfButler.Core
open PdfButler.Pdfium
open PdfButler.TestSupport

let engine = PdfEngine.Shared

/// Generous upper bound for any single engine call in tests. A hang is a test failure.
let timeout = TimeSpan.FromSeconds 60.0

let await' (t: Task<'T>) : 'T =
    if not (t.Wait timeout) then failwith $"engine call did not finish within {timeout}"
    t.Result

let tempDir =
    lazy
        (let d = Path.Combine(Path.GetTempPath(), "pdfbutler-tests", string Environment.ProcessId)
         Directory.CreateDirectory d |> ignore
         d)

let tempFile (name: string) = Path.Combine(tempDir.Value, name)

let openDoc (path: string) (password: string option) =
    await' (engine.OpenAsync(path, password, CancellationToken.None))

let openOk path =
    match openDoc path None with
    | Ok d -> d
    | Error e -> failwith $"expected {path} to open, got {e}"

let render (doc: PdfDocument) page rotation scale clip =
    await' (doc.RenderAsync({ Page = page; Rotation = rotation; Scale = scale; Clip = clip }, Priority.Interactive, CancellationToken.None))

let toRgb (img: RenderedImage) = Png.ofBgrx img.Width img.Height img.Stride img.Pixels

let text (doc: PdfDocument) page = await' (doc.GetTextAsync(page, Priority.Interactive, CancellationToken.None))

/// Luminance (0..255) of a pixel in a rendered image.
let lum (img: RenderedImage) (x: int) (y: int) =
    if x < 0 || y < 0 || x >= img.Width || y >= img.Height then
        failwith $"pixel ({x},{y}) outside {img.Width}x{img.Height}"
    let o = y * img.Stride + x * 4
    (int img.Pixels[o] + int img.Pixels[o + 1] + int img.Pixels[o + 2]) / 3

let synthetic (name: string) (spec: SyntheticPdf.DocSpec) = SyntheticPdf.writeFile (tempFile name) spec
