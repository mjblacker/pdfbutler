/// Raw PDFium P/Invoke declarations. Only the read-only subset the viewer needs.
///
/// Signatures follow public/*.h of the pinned PDFium build. C `unsigned long` is 32-bit on
/// Windows and 64-bit on Linux x64, so it is declared as `CULong` (never as uint64/uint32).
/// Every function here must only be called from the PDFium owner thread (see Worker.fs).
module internal PdfButler.Pdfium.Native

#nowarn "9" // NativePtr usage
#nowarn "51" // address-of operator

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices

[<Literal>]
let Lib = "pdfium"

// ---- structs -------------------------------------------------------------------------

[<Struct; StructLayout(LayoutKind.Sequential)>]
type FPDF_LIBRARY_CONFIG =
    val mutable version: int
    val mutable m_pUserFontPaths: nativeint
    val mutable m_pIsolate: nativeint
    val mutable m_v8EmbedderSlot: uint32

[<Struct; StructLayout(LayoutKind.Sequential)>]
type FS_RECTF =
    val mutable left: float32
    val mutable top: float32
    val mutable right: float32
    val mutable bottom: float32

[<Struct; StructLayout(LayoutKind.Sequential)>]
type FS_SIZEF =
    val mutable width: float32
    val mutable height: float32

/// FPDF_FILEACCESS { unsigned long m_FileLen; GetBlock*; void* m_Param; }
[<Struct; StructLayout(LayoutKind.Sequential)>]
type FPDF_FILEACCESS =
    val mutable m_FileLen: CULong
    val mutable m_GetBlock: nativeint
    val mutable m_Param: nativeint

/// int GetBlock(void* param, unsigned long position, unsigned char* pBuf, unsigned long size)
[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type GetBlockDelegate = delegate of nativeint * CULong * nativeint * CULong -> int

// ---- error codes, flags -------------------------------------------------------------

let FPDF_ERR_SUCCESS = 0
let FPDF_ERR_UNKNOWN = 1
let FPDF_ERR_FILE = 2
let FPDF_ERR_FORMAT = 3
let FPDF_ERR_PASSWORD = 4
let FPDF_ERR_SECURITY = 5
let FPDF_ERR_PAGE = 6

let FPDF_ANNOT = 0x01
let FPDF_LCD_TEXT = 0x02
let FPDF_NO_NATIVETEXT = 0x04

/// Values from fpdfview.h of the pinned build (note: 2 is 24-bit BGR, 3 is 32-bit BGRx).
let FPDFBitmap_Gray = 1
let FPDFBitmap_BGR = 2
let FPDFBitmap_BGRx = 3
let FPDFBitmap_BGRA = 4

let PDFACTION_UNSUPPORTED = 0u
let PDFACTION_GOTO = 1u
let PDFACTION_REMOTEGOTO = 2u
let PDFACTION_URI = 3u
let PDFACTION_LAUNCH = 4u
let PDFACTION_EMBEDDEDGOTO = 5u

// ---- library ------------------------------------------------------------------------

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern void FPDF_InitLibraryWithConfig(FPDF_LIBRARY_CONFIG& config)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern CULong FPDF_GetLastError()

// ---- documents ----------------------------------------------------------------------

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern nativeint FPDF_LoadCustomDocument(nativeint pFileAccess, byte[] password)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern void FPDF_CloseDocument(nativeint document)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern int FPDF_GetPageCount(nativeint document)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern int FPDF_GetPageSizeByIndexF(nativeint document, int page_index, FS_SIZEF& size)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern CULong FPDF_GetPageLabel(nativeint document, int page_index, byte[] buffer, CULong buflen)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern CULong FPDF_GetMetaText(nativeint document, byte[] tag, byte[] buffer, CULong buflen)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern CULong FPDF_GetDocPermissions(nativeint document)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern int FPDF_GetSignatureCount(nativeint document)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern int FPDF_GetFormType(nativeint document)

// ---- pages & rendering --------------------------------------------------------------

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern nativeint FPDF_LoadPage(nativeint document, int page_index)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern void FPDF_ClosePage(nativeint page)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern int FPDF_PageToDevice(
    nativeint page,
    int start_x,
    int start_y,
    int size_x,
    int size_y,
    int rotate,
    double page_x,
    double page_y,
    int& device_x,
    int& device_y
)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern nativeint FPDFBitmap_CreateEx(int width, int height, int format, nativeint first_scan, int stride)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern void FPDFBitmap_Destroy(nativeint bitmap)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern void FPDF_RenderPageBitmap(
    nativeint bitmap,
    nativeint page,
    int start_x,
    int start_y,
    int size_x,
    int size_y,
    int rotate,
    int flags
)

// ---- forms (display only) -----------------------------------------------------------

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern nativeint FPDFDOC_InitFormFillEnvironment(nativeint document, nativeint formInfo)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern void FPDFDOC_ExitFormFillEnvironment(nativeint hHandle)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern void FORM_OnAfterLoadPage(nativeint page, nativeint hHandle)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern void FORM_OnBeforeClosePage(nativeint page, nativeint hHandle)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern void FPDF_FFLDraw(
    nativeint hHandle,
    nativeint bitmap,
    nativeint page,
    int start_x,
    int start_y,
    int size_x,
    int size_y,
    int rotate,
    int flags
)

// ---- text ---------------------------------------------------------------------------

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern nativeint FPDFText_LoadPage(nativeint page)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern void FPDFText_ClosePage(nativeint text_page)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern int FPDFText_CountChars(nativeint text_page)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern uint32 FPDFText_GetUnicode(nativeint text_page, int index)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern int FPDFText_IsGenerated(nativeint text_page, int index)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern int FPDFText_GetLooseCharBox(nativeint text_page, int index, FS_RECTF& rect)

// ---- links, destinations, actions, outline ------------------------------------------

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern int FPDFLink_Enumerate(nativeint page, int& start_pos, nativeint& link_annot)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern int FPDFLink_GetAnnotRect(nativeint link_annot, FS_RECTF& rect)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern nativeint FPDFLink_GetDest(nativeint document, nativeint link)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern nativeint FPDFLink_GetAction(nativeint link)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern CULong FPDFAction_GetType(nativeint action)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern nativeint FPDFAction_GetDest(nativeint document, nativeint action)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern CULong FPDFAction_GetURIPath(nativeint document, nativeint action, byte[] buffer, CULong buflen)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern int FPDFDest_GetDestPageIndex(nativeint document, nativeint dest)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern int FPDFDest_GetLocationInPage(
    nativeint dest,
    int& hasXVal,
    int& hasYVal,
    int& hasZoomVal,
    float32& x,
    float32& y,
    float32& zoom
)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern nativeint FPDFBookmark_GetFirstChild(nativeint document, nativeint bookmark)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern nativeint FPDFBookmark_GetNextSibling(nativeint document, nativeint bookmark)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern CULong FPDFBookmark_GetTitle(nativeint bookmark, byte[] buffer, CULong buflen)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern int FPDFBookmark_GetCount(nativeint bookmark)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern nativeint FPDFBookmark_GetDest(nativeint document, nativeint bookmark)

[<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
extern nativeint FPDFBookmark_GetAction(nativeint bookmark)

// ---- helpers ------------------------------------------------------------------------

let inline culong (v: int64) = CULong(unativeint v)
let inline ofCULong (v: CULong) = int64 (uint64 v.Value)

/// Calls a PDFium "fill this buffer, return required byte length" API and decodes UTF-16LE.
let readUtf16 (call: byte[] -> CULong -> CULong) : string option =
    let needed = ofCULong (call null (culong 0L))

    if needed <= 2L || needed > 1_048_576L then
        None
    else
        let buf = Array.zeroCreate<byte> (int needed)
        let got = ofCULong (call buf (culong needed))
        let len = int (min got needed) - 2 // drop the UTF-16 terminator

        if len <= 0 then
            None
        else
            let s = Text.Encoding.Unicode.GetString(buf, 0, len - (len % 2))
            if String.IsNullOrEmpty s then None else Some s

/// Same, for APIs that return 7-bit/UTF-8 byte strings (URI paths).
let readBytes (call: byte[] -> CULong -> CULong) : string option =
    let needed = ofCULong (call null (culong 0L))

    if needed <= 1L || needed > 1_048_576L then
        None
    else
        let buf = Array.zeroCreate<byte> (int needed)
        let got = ofCULong (call buf (culong needed))
        let len = int (min got needed) - 1
        if len <= 0 then None else Some(Text.Encoding.UTF8.GetString(buf, 0, len))

/// Resolve "pdfium" only from the application directory (never PATH or the working directory).
let private resolver =
    DllImportResolver(fun name asm _ ->
        if name <> Lib then
            nativeint 0
        else
            let baseDir = AppContext.BaseDirectory
            let rid = RuntimeInformation.RuntimeIdentifier

            let fileName =
                if OperatingSystem.IsWindows() then "pdfium.dll"
                elif OperatingSystem.IsMacOS() then "libpdfium.dylib"
                else "libpdfium.so"

            let arch =
                match RuntimeInformation.ProcessArchitecture with
                | Architecture.X64 -> "x64"
                | Architecture.X86 -> "x86"
                | Architecture.Arm64 -> "arm64"
                | a -> a.ToString().ToLowerInvariant()

            let os =
                if OperatingSystem.IsWindows() then "win"
                elif OperatingSystem.IsMacOS() then "osx"
                else "linux"

            let candidates =
                [ Path.Combine(baseDir, fileName)
                  Path.Combine(baseDir, "runtimes", $"{os}-{arch}", "native", fileName)
                  Path.Combine(baseDir, "runtimes", rid, "native", fileName) ]

            candidates
            |> List.tryPick (fun p ->
                match NativeLibrary.TryLoad p with
                | true, h -> Some h
                | _ -> None)
            |> Option.defaultValue (nativeint 0))

let mutable private resolverInstalled = false

let installResolver () =
    if not resolverInstalled then
        resolverInstalled <- true

        try
            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), resolver)
        with :? InvalidOperationException ->
            () // already set
