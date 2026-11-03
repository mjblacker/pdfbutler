// Ad-hoc inspection of a corpus file with the real engine.
// usage: dotnet fsi tools/inspect.fsx <corpus-id> [page]
#r "../tests/PdfButler.Engine.Tests/bin/Debug/net10.0/PdfButler.Core.dll"
#r "../tests/PdfButler.Engine.Tests/bin/Debug/net10.0/PdfButler.Pdfium.dll"
#r "../tests/PdfButler.Engine.Tests/bin/Debug/net10.0/PdfButler.TestSupport.dll"

open System
open System.IO
open System.Threading
open PdfButler.Core
open PdfButler.Pdfium
open PdfButler.TestSupport

// Load native pdfium from the test output's runtimes folder.
let nativeDir = Path.Combine(__SOURCE_DIRECTORY__, "../tests/PdfButler.Engine.Tests/bin/Debug/net10.0/runtimes/win-x64/native")
Runtime.InteropServices.NativeLibrary.Load(Path.Combine(nativeDir, "pdfium.dll")) |> ignore

let args = fsi.CommandLineArgs |> Array.skip 1
let doc = Corpus.find args[0]
let page = if args.Length > 1 then int args[1] else 0
let path = Corpus.cachePath doc
printfn "file %s (%d bytes)" path (FileInfo(path).Length)
match PdfEngine.Shared.OpenAsync(path, doc.Password, CancellationToken.None).Result with
| Error e -> printfn "open error: %A" e
| Ok d ->
    printfn "pages=%d forms=%A sigs=%d title=%A" d.Info.PageCount d.Info.Forms d.Info.SignatureCount d.Info.Title
    for p in d.Info.Pages |> Array.truncate 8 do printfn "  page %d size %.1fx%.1f label %A" p.Index p.Size.W p.Size.H p.Label
    let t = d.GetTextAsync(page, Priority.Interactive, CancellationToken.None).Result
    printfn "--- PDFium text p%d (%d chars)\n%s" page t.Chars.Length (TextLayer.fullText t)
    let links = d.GetLinksAsync(page, Priority.Interactive, CancellationToken.None).Result
    printfn "--- links: %A" links
    match Corpus.loadOracle doc with
    | Some o when o.Pages.Length > page -> printfn "--- pdf.js text p%d\n%s\n--- pdf.js links %d" page o.Pages[page].Text o.Pages[page].Links
    | _ -> ()
    (d :> IDisposable).Dispose()
