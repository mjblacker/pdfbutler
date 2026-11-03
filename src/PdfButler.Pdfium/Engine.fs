namespace PdfButler.Pdfium

#nowarn "9"
#nowarn "51"

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.NativeInterop
open Microsoft.Win32.SafeHandles
open PdfButler.Core

/// Owned, tightly packed 32-bit BGRx pixels (byte order B,G,R,X; X is 0xFF). Rendered on opaque white.
type RenderedImage =
    { Width: int
      Height: int
      Stride: int
      Pixels: byte[] }

/// A region of the whole rendered page in device pixels.
[<Struct>]
type PixelRect = { X: int; Y: int; W: int; H: int }

type RenderRequest =
    { Page: int
      Rotation: Rotation
      /// Device pixels per point.
      Scale: float
      /// Render only this part of the whole page (tiles). None = whole page.
      Clip: PixelRect option }

exception PageLoadException of page: int

module Limits =
    /// Largest single bitmap the engine will allocate (64 MiB of BGRx).
    let maxBitmapPixels = 16_777_216L
    /// Largest whole-page coordinate space PDFium can address (its device coordinates are int).
    let maxDeviceExtent = 1_000_000
    let maxCharsPerPage = 200_000
    let maxLinksPerPage = 5_000
    let maxOutlineItems = 20_000
    let maxOutlineDepth = 64

/// File state handed to PDFium's read callback via a GCHandle.
type internal FileState(handle: SafeFileHandle, length: int64) =
    member _.Handle = handle
    member _.Length = length

module internal FileAccessCallback =
    let private read (param: nativeint) (position: CULong) (buf: nativeint) (size: CULong) : int =
        try
            let st = GCHandle.FromIntPtr(param).Target :?> FileState
            let pos = int64 (uint64 position.Value)
            let n = int64 (uint64 size.Value)

            if pos < 0L || n < 0L || pos + n > st.Length || n > int64 Int32.MaxValue then
                0
            else
                let span = Span<byte>(NativePtr.toVoidPtr (NativePtr.ofNativeInt<byte> buf), int n)
                let mutable got = 0
                let mutable ok = true

                while ok && got < int n do
                    let r = RandomAccess.Read(st.Handle, span.Slice got, pos + int64 got)
                    if r <= 0 then ok <- false else got <- got + r

                if ok then 1 else 0
        with _ ->
            0 // never let a managed exception unwind through native frames

    // Rooted for the lifetime of the process: PDFium keeps the function pointer.
    let delegateInstance = Native.GetBlockDelegate(fun p pos buf size -> read p pos buf size)
    let pointer = Marshal.GetFunctionPointerForDelegate<Native.GetBlockDelegate> delegateInstance

type internal LoadedPage =
    { Handle: nativeint
      /// PDF user space -> display space (points, y-down).
      Matrix: Affine
      mutable Text: nativeint }

/// An open PDF document. All members are safe to call from any thread; work is queued on the
/// engine's owner thread. Dispose (or DisposeAsync) to release native resources: there is
/// deliberately no finalizer, because PDFium must never be called from the finalizer thread.
[<AllowNullLiteral>]
type PdfDocument
    internal
    (
        worker: Worker,
        id: int,
        path: string,
        doc: nativeint,
        form: nativeint,
        formInfo: nativeint,
        access: nativeint,
        fileHandleRef: GCHandle,
        file: SafeFileHandle,
        info: DocumentInfo
    ) =

    // Owner-thread-only state.
    let pages = Dictionary<int, LoadedPage>()
    let pageOrder = LinkedList<int>()
    let matrices = Dictionary<int, Affine>()
    let textCache = LruCache<int, PageText>(64L, (fun _ -> 1L), ignore)
    let maxLoadedPages = 8
    let mutable closed = false
    // Any thread.
    let mutable closing = 0

    let ensureOpen () =
        if closed then raise (ObjectDisposedException $"PdfDocument {id}")

    let checkPage i =
        if i < 0 || i >= info.PageCount then
            raise (ArgumentOutOfRangeException("page", $"page {i} outside 0..{info.PageCount - 1}"))

    let closePage (lp: LoadedPage) =
        if lp.Text <> 0n then Native.FPDFText_ClosePage lp.Text
        if form <> 0n then Native.FORM_OnBeforeClosePage(lp.Handle, form)
        Native.FPDF_ClosePage lp.Handle

    /// Page space -> display space, measured from PDFium itself so it always agrees with rendering.
    let computeMatrix (page: nativeint) (size: Size) =
        let k = 64.0
        let sx = int (min 1.0e8 (max 1.0 (round (size.W * k))))
        let sy = int (min 1.0e8 (max 1.0 (round (size.H * k))))
        let probe (px: float) (py: float) =
            let mutable dx = 0
            let mutable dy = 0
            Native.FPDF_PageToDevice(page, 0, 0, sx, sy, 0, px, py, &dx, &dy) |> ignore
            float dx, float dy

        let d = 10000.0
        let x0, y0 = probe 0.0 0.0
        let x1, y1 = probe d 0.0
        let x2, y2 = probe 0.0 d

        { A = (x1 - x0) / d / k
          B = (y1 - y0) / d / k
          C = (x2 - x0) / d / k
          D = (y2 - y0) / d / k
          E = x0 / k
          F = y0 / k }

    let loadPage (i: int) : LoadedPage =
        ensureOpen ()
        checkPage i

        match pages.TryGetValue i with
        | true, lp ->
            pageOrder.Remove i |> ignore
            pageOrder.AddFirst i |> ignore
            lp
        | _ ->
            let h = Native.FPDF_LoadPage(doc, i)
            if h = 0n then raise (PageLoadException i)
            if form <> 0n then Native.FORM_OnAfterLoadPage(h, form)

            let m =
                match matrices.TryGetValue i with
                | true, m -> m
                | _ ->
                    let m = computeMatrix h info.Pages[i].Size
                    matrices[i] <- m
                    m

            let lp = { Handle = h; Matrix = m; Text = 0n }
            pages[i] <- lp
            pageOrder.AddFirst i |> ignore

            while pageOrder.Count > maxLoadedPages do
                let victim = pageOrder.Last.Value
                pageOrder.RemoveLast()
                closePage pages[victim]
                pages.Remove victim |> ignore

            lp

    let rectOfPdf (m: Affine) (r: Native.FS_RECTF) =
        Affine.applyRect m (Rect.ofLTRB (float r.left) (float r.bottom) (float r.right) (float r.top))

    let readDest (dest: nativeint) : Destination option =
        if dest = 0n then
            None
        else
            let page = Native.FPDFDest_GetDestPageIndex(doc, dest)

            if page < 0 || page >= info.PageCount then
                None
            else
                let mutable hasX = 0
                let mutable hasY = 0
                let mutable hasZ = 0
                let mutable x = 0.0f
                let mutable y = 0.0f
                let mutable z = 0.0f

                let ok = Native.FPDFDest_GetLocationInPage(dest, &hasX, &hasY, &hasZ, &x, &y, &z) <> 0
                let finite (v: float32) = not (Single.IsNaN v || Single.IsInfinity v)

                Some
                    { Page = page
                      X = (if ok && hasX <> 0 && finite x then Some(float x) else None)
                      Y = (if ok && hasY <> 0 && finite y then Some(float y) else None) }

    let readAction (action: nativeint) : LinkTarget option =
        if action = 0n then
            None
        else
            let t = uint32 (Native.FPDFAction_GetType action).Value

            if t = Native.PDFACTION_GOTO then
                match readDest (Native.FPDFAction_GetDest(doc, action)) with
                | Some d -> Some(GoTo d)
                | None -> Some(Unsupported "invalid destination")
            elif t = Native.PDFACTION_URI then
                Native.readBytes (fun b n -> Native.FPDFAction_GetURIPath(doc, action, b, n))
                |> Option.map (fun s -> Uri(s.Trim()))
            elif t = Native.PDFACTION_REMOTEGOTO then Some(Unsupported "remote document")
            elif t = Native.PDFACTION_LAUNCH then Some(Unsupported "launch application")
            elif t = Native.PDFACTION_EMBEDDEDGOTO then Some(Unsupported "embedded document")
            else Some(Unsupported "action")

    let readText (i: int) : PageText =
        match textCache.TryGet i with
        | Some t -> t
        | None ->
            let lp = loadPage i

            if lp.Text = 0n then
                lp.Text <- Native.FPDFText_LoadPage lp.Handle

            let raw =
                if lp.Text = 0n then
                    [||]
                else
                    let n = min Limits.maxCharsPerPage (Native.FPDFText_CountChars lp.Text)

                    Array.init (max 0 n) (fun c ->
                        let code = int (Native.FPDFText_GetUnicode(lp.Text, c))
                        let generated = Native.FPDFText_IsGenerated(lp.Text, c) = 1
                        let mutable r = Native.FS_RECTF()

                        let box =
                            if Native.FPDFText_GetLooseCharBox(lp.Text, c, &r) <> 0 then rectOfPdf lp.Matrix r
                            else Rect.make 0.0 0.0 0.0 0.0

                        { Code = code; Box = box; Generated = generated })

            let chars = TextLayer.visibleOnly info.Pages[i].Size raw

            let t = { Page = i; Chars = chars }
            textCache.Add(i, t)
            t

    let readLinks (i: int) : Link[] =
        let lp = loadPage i
        let result = ResizeArray<Link>()
        let mutable pos = 0
        let mutable link = 0n

        while result.Count < Limits.maxLinksPerPage && Native.FPDFLink_Enumerate(lp.Handle, &pos, &link) <> 0 do
            let mutable r = Native.FS_RECTF()

            if Native.FPDFLink_GetAnnotRect(link, &r) <> 0 then
                let target =
                    match readDest (Native.FPDFLink_GetDest(doc, link)) with
                    | Some d -> GoTo d
                    | None ->
                        readAction (Native.FPDFLink_GetAction link)
                        |> Option.defaultValue (Unsupported "no action")

                // Keep links with unusable targets so the UI can explain them instead of ignoring clicks.
                result.Add { Bounds = rectOfPdf lp.Matrix r; Target = target }

        result.ToArray()

    let readOutline () : OutlineItem list =
        let visited = HashSet<nativeint>()
        let mutable count = 0

        let rec children (parent: nativeint) depth : OutlineItem list =
            if depth > Limits.maxOutlineDepth then
                []
            else
                let items = ResizeArray()
                let mutable bm = Native.FPDFBookmark_GetFirstChild(doc, parent)

                while bm <> 0n && count < Limits.maxOutlineItems && visited.Add bm do
                    count <- count + 1
                    let title = Native.readUtf16 (fun b n -> Native.FPDFBookmark_GetTitle(bm, b, n))

                    let target =
                        match readDest (Native.FPDFBookmark_GetDest(doc, bm)) with
                        | Some d -> Some(GoTo d)
                        | None -> readAction (Native.FPDFBookmark_GetAction bm)

                    let kids = children bm (depth + 1)

                    items.Add
                        { Title = (defaultArg title "").Replace('\r', ' ').Replace('\n', ' ').Trim()
                          Target = target
                          StartsOpen = Native.FPDFBookmark_GetCount bm > 0
                          Children = kids }

                    bm <- Native.FPDFBookmark_GetNextSibling(doc, bm)

                List.ofSeq items

        children 0n 0

    let render (req: RenderRequest) : RenderedImage =
        let lp = loadPage req.Page

        if Double.IsNaN req.Scale || req.Scale <= 0.0 then
            raise (ArgumentOutOfRangeException("Scale"))

        let fw, fh = Geometry.devicePageSize req.Rotation info.Pages[req.Page].Size req.Scale

        if fw > Limits.maxDeviceExtent || fh > Limits.maxDeviceExtent then
            raise (ArgumentOutOfRangeException("Scale", "page too large at this zoom"))

        let clip = defaultArg req.Clip { X = 0; Y = 0; W = fw; H = fh }

        // Clamp the clip to the page; reject degenerate or oversized requests before allocating.
        let x0 = max 0 clip.X
        let y0 = max 0 clip.Y
        let x1 = min fw (clip.X + clip.W)
        let y1 = min fh (clip.Y + clip.H)
        let w = x1 - x0
        let h = y1 - y0

        if w <= 0 || h <= 0 then
            raise (ArgumentOutOfRangeException("Clip", "empty render region"))

        if int64 w * int64 h > Limits.maxBitmapPixels then
            raise (ArgumentOutOfRangeException("Clip", $"render region {w}x{h} exceeds limit"))

        let stride = w * 4
        let pixels = GC.AllocateUninitializedArray<byte>(stride * h)
        Array.Fill(pixels, 0xFFuy)
        let pin = GCHandle.Alloc(pixels, GCHandleType.Pinned)

        try
            // BGRx: opaque, no alpha blending against the bitmap.
            let bmp = Native.FPDFBitmap_CreateEx(w, h, Native.FPDFBitmap_BGRx, pin.AddrOfPinnedObject(), stride)

            if bmp = 0n then
                raise (OutOfMemoryException "FPDFBitmap_CreateEx failed")

            try
                let rot = Rotation.quarterTurns req.Rotation
                Native.FPDF_RenderPageBitmap(bmp, lp.Handle, -x0, -y0, fw, fh, rot, Native.FPDF_ANNOT)

                // Widgets (form fields) are drawn by the form layer, not by FPDF_ANNOT.
                if form <> 0n then
                    Native.FPDF_FFLDraw(form, bmp, lp.Handle, -x0, -y0, fw, fh, rot, Native.FPDF_ANNOT)
            finally
                Native.FPDFBitmap_Destroy bmp
        finally
            pin.Free()

        { Width = w; Height = h; Stride = stride; Pixels = pixels }

    let resolve (d: Destination) : DisplayDestination =
        match d.X, d.Y with
        | None, None -> { Page = d.Page; Left = None; Top = None }
        | x, y ->
            let lp = loadPage d.Page
            // A missing coordinate keeps the current scroll position on that axis; probe with 0.
            let p = Affine.apply lp.Matrix { X = defaultArg x 0.0; Y = defaultArg y 0.0 }
            let size = info.Pages[d.Page].Size
            let clamp hi v = max 0.0 (min hi v)

            { Page = d.Page
              Left = x |> Option.map (fun _ -> clamp size.W p.X)
              Top = y |> Option.map (fun _ -> clamp size.H p.Y) }

    let close () =
        if not closed then
            closed <- true

            for lp in pages.Values do
                closePage lp

            pages.Clear()
            pageOrder.Clear()
            textCache.Clear()
            if form <> 0n then Native.FPDFDOC_ExitFormFillEnvironment form
            Native.FPDF_CloseDocument doc
            // Native memory/handles PDFium referenced are released only after the document is closed.
            Marshal.FreeHGlobal formInfo
            Marshal.FreeHGlobal access
            fileHandleRef.Free()
            file.Dispose()

    let run priority (ct: CancellationToken) f =
        if Volatile.Read(&closing) <> 0 then
            Task.FromException<_>(ObjectDisposedException $"PdfDocument {id}")
        else
            worker.Invoke(priority, ct, fun () -> ensureOpen (); f ())

    /// Process-unique id; changes for every open, so stale results can be recognised.
    member _.Id = id
    member _.Path = path
    member _.Info = info
    member _.IsClosed = Volatile.Read(&closing) <> 0

    member _.RenderAsync(req: RenderRequest, priority: Priority, ct: CancellationToken) =
        run priority ct (fun () -> render req)

    member _.GetTextAsync(page: int, priority: Priority, ct: CancellationToken) =
        run priority ct (fun () -> readText page)

    member _.GetLinksAsync(page: int, priority: Priority, ct: CancellationToken) =
        run priority ct (fun () -> readLinks page)

    member _.GetOutlineAsync(ct: CancellationToken) =
        run Priority.Prefetch ct readOutline

    member _.ResolveAsync(dest: Destination, ct: CancellationToken) =
        run Priority.Interactive ct (fun () -> resolve dest)

    /// PDF user space -> display space matrix for a page (diagnostics and tests).
    member _.GetPageMatrixAsync(page: int) =
        run Priority.Interactive CancellationToken.None (fun () -> (loadPage page).Matrix)

    member _.CloseAsync() : Task =
        if Interlocked.Exchange(&closing, 1) = 1 then
            Task.CompletedTask
        else
            worker.Invoke(Priority.Interactive, close) :> Task

    interface IDisposable with
        member this.Dispose() =
            let t = this.CloseAsync()
            if not worker.IsOwnerThread then t.Wait()

    interface IAsyncDisposable with
        member this.DisposeAsync() = ValueTask(this.CloseAsync())

/// Entry point: one engine (one PDFium owner thread) per process.
type PdfEngine private () =
    static let instance = lazy (new PdfEngine())
    static let mutable nextId = 0

    let init () =
        Native.installResolver ()
        let mutable cfg = Native.FPDF_LIBRARY_CONFIG()
        cfg.version <- 2
        Native.FPDF_InitLibraryWithConfig(&cfg)

    let worker = new Worker("pdfium-owner", init)

    let mapError (code: int) (hadPassword: bool) =
        if code = Native.FPDF_ERR_PASSWORD then (if hadPassword then WrongPassword else PasswordRequired)
        elif code = Native.FPDF_ERR_SECURITY then UnsupportedSecurity
        elif code = Native.FPDF_ERR_FORMAT then Malformed
        elif code = Native.FPDF_ERR_FILE then FileUnreadable "read error"
        else EngineError code

    let openOnOwner (path: string) (password: string option) : Result<PdfDocument, OpenError> =
        if not (File.Exists path) then
            Error(FileNotFound path)
        else
            let fileResult =
                try
                    Ok(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete))
                with e ->
                    Error(FileUnreadable e.Message)

            match fileResult with
            | Error e -> Error e
            | Ok file ->
                let length = RandomAccess.GetLength file
                let maxLen = if OperatingSystem.IsWindows() then int64 UInt32.MaxValue else Int64.MaxValue

                if length > maxLen then
                    file.Dispose()
                    Error(FileTooLarge length)
                elif length = 0L then
                    file.Dispose()
                    Error Malformed
                else
                    let state = FileState(file, length)
                    let gch = GCHandle.Alloc state
                    let mutable fa = Native.FPDF_FILEACCESS()
                    fa.m_FileLen <- Native.culong length
                    fa.m_GetBlock <- FileAccessCallback.pointer
                    fa.m_Param <- GCHandle.ToIntPtr gch
                    let access = Marshal.AllocHGlobal(Marshal.SizeOf<Native.FPDF_FILEACCESS>())
                    Marshal.StructureToPtr<Native.FPDF_FILEACCESS>(fa, access, false)

                    let tryOpen (bytes: byte[]) = Native.FPDF_LoadCustomDocument(access, bytes)
                    let zstr (enc: Encoding) (s: string) = Array.append (enc.GetBytes s) [| 0uy |]

                    let mutable doc =
                        match password with
                        | None -> tryOpen null
                        | Some p -> tryOpen (zstr Encoding.UTF8 p)

                    let mutable err = if doc = 0n then int (Native.FPDF_GetLastError()).Value else 0

                    // Other spellings of the same password, tried only after a password failure:
                    //  * SASLprep (RFC 4013), required for AES-256 (R6) and not applied by PDFium;
                    //  * Latin-1 bytes, used by legacy (R2-R4) encryption for non-ASCII passwords.
                    match password with
                    | Some p when doc = 0n && err = Native.FPDF_ERR_PASSWORD ->
                        let candidates =
                            [ zstr Encoding.UTF8 (Password.saslPrep p)
                              if Encoding.UTF8.GetByteCount p <> p.Length then zstr Encoding.Latin1 p ]
                            |> List.distinct
                            |> List.filter (fun b -> b <> zstr Encoding.UTF8 p)

                        for bytes in candidates do
                            if doc = 0n then
                                doc <- tryOpen bytes
                                err <- if doc = 0n then int (Native.FPDF_GetLastError()).Value else 0
                    | _ -> ()

                    if doc = 0n then
                        Marshal.FreeHGlobal access
                        gch.Free()
                        file.Dispose()
                        Error(mapError err password.IsSome)
                    else
                        let count = Native.FPDF_GetPageCount doc

                        if count <= 0 then
                            Native.FPDF_CloseDocument doc
                            Marshal.FreeHGlobal access
                            gch.Free()
                            file.Dispose()
                            Error Malformed
                        else
                            let pageInfos =
                                Array.init count (fun i ->
                                    let mutable sz = Native.FS_SIZEF()
                                    let ok = Native.FPDF_GetPageSizeByIndexF(doc, i, &sz) <> 0
                                    let valid (v: float32) = ok && v > 0.0f && not (Single.IsNaN v || Single.IsInfinity v)

                                    let size =
                                        if valid sz.width && valid sz.height then
                                            { W = float sz.width; H = float sz.height }
                                        else
                                            { W = 612.0; H = 792.0 }

                                    let label = Native.readUtf16 (fun b n -> Native.FPDF_GetPageLabel(doc, i, b, n))
                                    { Index = i; Size = size; Label = label })

                            // FPDF_FORMFILLINFO version 1 with every callback NULL: PDFium checks each
                            // callback before use, so the document cannot reach timers, URLs, files or
                            // JavaScript (the pinned build has no V8/XFA). Kept until the form env closes.
                            let formInfo = Marshal.AllocHGlobal 1024
                            Span<byte>(NativePtr.toVoidPtr (NativePtr.ofNativeInt<byte> formInfo), 1024).Clear()
                            Marshal.WriteInt32(formInfo, 1)
                            let form = Native.FPDFDOC_InitFormFillEnvironment(doc, formInfo)

                            let forms =
                                match Native.FPDF_GetFormType doc with
                                | 1 -> AcroForm
                                | 2 -> XfaFull
                                | 3 -> XfaForeground
                                | _ -> NoForms

                            let title =
                                Native.readUtf16 (fun b n -> Native.FPDF_GetMetaText(doc, zstr Encoding.ASCII "Title", b, n))
                                |> Option.map (fun s -> s.Trim())
                                |> Option.filter (fun s -> s <> "")

                            let info =
                                { PageCount = count
                                  Pages = pageInfos
                                  Title = title
                                  Forms = forms
                                  SignatureCount = max 0 (Native.FPDF_GetSignatureCount doc)
                                  Permissions = uint32 (Native.FPDF_GetDocPermissions doc).Value }

                            let id = Interlocked.Increment(&nextId)
                            Ok(new PdfDocument(worker, id, path, doc, form, formInfo, access, gch, file, info))

    static member Shared = instance.Value

    member internal _.Worker = worker

    /// Throws if the native library cannot be loaded or initialised.
    member _.EnsureReady() =
        worker.EnsureInitialized()
        worker.Run(fun () -> ())

    member _.OpenAsync(path: string, password: string option, ct: CancellationToken) : Task<Result<PdfDocument, OpenError>> =
        let full = try Path.GetFullPath path with _ -> path
        worker.Invoke(Priority.Interactive, ct, fun () -> openOnOwner full password)

    member this.OpenAsync(path: string, ?password: string) =
        this.OpenAsync(path, password, CancellationToken.None)

    /// Approximate number of queued engine jobs (diagnostics).
    member _.QueueLength = worker.QueueLength
