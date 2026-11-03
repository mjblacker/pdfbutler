/// Turns "what is visible" into engine render requests, and engine results into cached bitmaps.
/// Lives on the UI thread. Results for a closed/replaced document are discarded.
module PdfButler.App.PageRenderer

open System
open System.Collections.Generic
open System.Runtime.InteropServices
open System.Threading
open Avalonia
open Avalonia.Media.Imaging
open Avalonia.Platform
open Avalonia.Threading
open PdfButler.Core
open PdfButler.Pdfium
open PdfButler.App.RendererProcess

/// Pages up to this many device pixels are rendered as one bitmap; bigger ones as tiles.
let wholePageMaxPixels = 6_000_000L
let wholePageMaxSide = 4096
let tileSize = 1024
/// Raster cache budget (bytes of decoded BGRx pixels).
let cacheBudgetBytes = 128L * 1024L * 1024L

[<Struct>]
type TileKey =
    { Doc: int
      Page: int
      Rot: Rotation
      /// Device pixels per point x 1000, rounded.
      ScaleKey: int
      /// Tile column/row; -1 = the whole page.
      TX: int
      TY: int }

type Tile =
    { Key: TileKey
      Bitmap: Bitmap
      /// Region of the whole page this tile covers, in the page's device pixels at `Scale`.
      Rect: PixelRect
      Scale: float }

let scaleKey (scale: float) = int (Math.Round(scale * 1000.0))

/// Which tiles cover `visible` (page device pixels) for a page at `scale`.
let plan (doc: int) (page: int) (rot: Rotation) (pageSize: Size) (scale: float) (visible: PixelRect) : (TileKey * PixelRect) list =
    let fw, fh = Geometry.devicePageSize rot pageSize scale
    let sk = scaleKey scale
    if int64 fw * int64 fh <= wholePageMaxPixels && fw <= wholePageMaxSide && fh <= wholePageMaxSide then
        [ { Doc = doc; Page = page; Rot = rot; ScaleKey = sk; TX = -1; TY = -1 }, { X = 0; Y = 0; W = fw; H = fh } ]
    elif fw > Limits.maxDeviceExtent || fh > Limits.maxDeviceExtent then
        [] // beyond what the engine can address; the low-zoom fallback stays on screen
    else
        let x0 = max 0 (visible.X / tileSize)
        let y0 = max 0 (visible.Y / tileSize)
        let x1 = min ((fw - 1) / tileSize) ((visible.X + visible.W - 1) / tileSize)
        let y1 = min ((fh - 1) / tileSize) ((visible.Y + visible.H - 1) / tileSize)
        [ for ty in y0..y1 do
              for tx in x0..x1 do
                  let x = tx * tileSize
                  let y = ty * tileSize
                  { Doc = doc; Page = page; Rot = rot; ScaleKey = sk; TX = tx; TY = ty },
                  { X = x; Y = y; W = min tileSize (fw - x); H = min tileSize (fh - y) } ]

let private toBitmap (img: RenderedImage) =
    let pin = GCHandle.Alloc(img.Pixels, GCHandleType.Pinned)
    try
        new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, pin.AddrOfPinnedObject(), PixelSize(img.Width, img.Height), Vector(96.0, 96.0), img.Stride)
    finally
        pin.Free()

type Renderer(doc: RemoteDocument, onChanged: unit -> unit) =
    let cache =
        LruCache<TileKey, Tile>(cacheBudgetBytes, (fun t -> int64 t.Rect.W * int64 t.Rect.H * 4L), (fun t -> t.Bitmap.Dispose()))
    let inFlight = Dictionary<TileKey, CancellationTokenSource>()
    let pageErrors = Dictionary<int, string>()
    let mutable disposed = false
    let mutable firstTileDelivered = false
    let mutable suspended = false

    member _.Document = doc
    member _.PageError(page: int) = match pageErrors.TryGetValue page with | true, m -> Some m | _ -> None
    member _.CachedBytes = cache.Used
    member _.CachedKeys = cache.Keys
    member _.InFlightCount = inFlight.Count
    member val FirstTile: (unit -> unit) = ignore with get, set

    /// Inactive tabs retain their document/state, not their expensive raster cache.
    member _.SetSuspended(value: bool) =
        suspended <- value
        if value then
            for cts in inFlight.Values do cts.Cancel()
            inFlight.Clear()
            cache.Clear()

    member _.TryGet(key: TileKey) = cache.TryGet key

    /// Tiles of the same page at the nearest other scale, drawn stretched while sharp tiles load.
    member _.Fallback(page: int, rot: Rotation, wantScaleKey: int) : Tile list =
        let candidates =
            cache.Keys |> List.filter (fun k -> k.Page = page && k.Rot = rot && k.Doc = doc.Id && k.ScaleKey <> wantScaleKey)
        match candidates with
        | [] -> []
        | ks ->
            let best = ks |> List.minBy (fun k -> abs (log (float k.ScaleKey / float wantScaleKey))) |> fun k -> k.ScaleKey
            ks |> List.filter (fun k -> k.ScaleKey = best) |> List.choose (fun k -> cache.Peek k)

    /// Make the set of wanted tiles current: cancel what is no longer wanted, start what is missing.
    member this.Update(wanted: (TileKey * PixelRect * Priority) list) =
        if not disposed && not suspended then
            let wantedKeys = HashSet<TileKey>(wanted |> List.map (fun (k, _, _) -> k))
            for KeyValue(k, cts) in List.ofSeq inFlight do
                if not (wantedKeys.Contains k) then
                    cts.Cancel()
                    inFlight.Remove k |> ignore
            for (key, rect, priority) in wanted do
                if not (cache.Contains key) && not (inFlight.ContainsKey key) && not (pageErrors.ContainsKey key.Page) then
                    this.Start(key, rect, priority)

    member private _.Start(key: TileKey, rect: PixelRect, priority: Priority) =
        let cts = new CancellationTokenSource()
        inFlight[key] <- cts
        let scale = float key.ScaleKey / 1000.0
        let clip = if key.TX < 0 then None else Some rect
        let req = { Page = key.Page; Rotation = key.Rot; Scale = scale; Clip = clip }
        let task = doc.RenderAsync(req, priority, cts.Token)
        task.ContinueWith(fun (t: Threading.Tasks.Task<RenderedImage>) ->
            Dispatcher.UIThread.Post(fun () ->
                match inFlight.TryGetValue key with
                | true, c when obj.ReferenceEquals(c, cts) -> inFlight.Remove key |> ignore
                | _ -> ()
                let cancelled = cts.IsCancellationRequested
                cts.Dispose()
                if not disposed && not suspended && not cancelled then
                    if t.IsCompletedSuccessfully then
                        let img = t.Result
                        cache.Add(key, { Key = key; Bitmap = toBitmap img; Rect = { rect with W = img.Width; H = img.Height }; Scale = scale })
                        if not firstTileDelivered then
                            firstTileDelivered <- true
                            Diagnostics.mark "first-page-bitmap"
                        onChanged ()
                    elif t.IsFaulted then
                        match t.Exception.GetBaseException() with
                        | :? PageLoadException -> pageErrors[key.Page] <- "This page could not be read."
                        | :? ObjectDisposedException -> ()
                        | :? ArgumentOutOfRangeException -> () // over a size limit; fallback stays visible
                        | e -> pageErrors[key.Page] <- $"This page could not be displayed ({e.Message})."
                        onChanged ()))
        |> ignore

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                for cts in inFlight.Values do cts.Cancel()
                inFlight.Clear()
                cache.Clear()
