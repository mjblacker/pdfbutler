# Publishing and Windows integration

## Build a deployment folder

From the repository root, using the pinned .NET 10 SDK:

```powershell
dotnet test PdfButler.slnx
pwsh tools/Publish.ps1
# Default: artifacts/publish/win-x64 (must be new or empty).
# Alternative output / JIT comparison:
pwsh tools/Publish.ps1 -OutputDirectory artifacts/publish/win-x64-jit -NoReadyToRun
# Linux glibc x64 (build/test on Linux before distributing):
pwsh tools/Publish.ps1 -Runtime linux-x64
```

Equivalent IDE/CLI profiles: `dotnet publish src/PdfButler.App -p:PublishProfile=win-x64` (or `linux-x64`).
Profiles are Release, self-contained (users need no .NET install), ReadyToRun, untrimmed, folder-based.
Deploy the **whole folder**, not just the executable. PDFium and other native libraries must remain beside it.
ReadyToRun is a reversible publishing option, not a NativeAOT commitment. In 20-launch tests per document across three PDFs,
it reduced median launch-to-first-page from 1.14–1.16 s (JIT) to 0.56–0.58 s on the Windows dev machine; details are in `PLAN.md`.
The script refuses a nonempty destination rather than deleting user files or leaving stale DLLs.

No corpus, PDFs, test artifacts or passwords are packaged. Third-party notices are included.
Windows x64 is the tested target. Linux still needs full distro/font verification; its pinned PDFium notice texts have been compared and match the bundle.
Linux requires the usual Avalonia X11/font/GL platform dependencies; self-contained does not bundle the desktop OS libraries.

To reproduce the startup measurements (PowerShell 7; one discarded warm-up, then at least 20 fresh processes per document):

```powershell
pwsh tools/Measure-Startup.ps1 -Executable artifacts/publish/win-x64/PdfButler.exe -Documents corpus/cache/pdfjs/test/pdfs/tracemonkey.pdf
```

Pass multiple paths to `-Documents` from PowerShell as an array to cover several documents. Results include raw samples and p50/p95 in `artifacts/startup.json`.

## Windows: portable or per-user installation

Copy/extract the folder to its final location and run `PdfButler.exe` for portable use. No registry changes happen at launch.
For a per-user installation, run from the extracted folder:

```powershell
.\Install-PdfButler.ps1 -WhatIf        # preview; no changes
.\Install-PdfButler.ps1               # copies to %LOCALAPPDATA%\Programs\PdfButler
```

This creates a Start menu shortcut and registers the app for PDFs. No administrator rights are needed.
The destination must be new or empty. For updates, close the app, unregister the old installation,
move the old folder aside, then install the new build. Never copy a partial publish over a running app.

If you keep the portable folder where it is, register it **in place** instead of installing a copy:

```powershell
.\Register-PdfButler.ps1 -WhatIf
.\Register-PdfButler.ps1
.\Register-PdfButler.ps1 -NoContextMenu  # Open with / Default Apps only
.\Register-PdfButler.ps1 -OpenDefaultApps
```

Registration writes only HKCU: `Software\Classes\PdfButler.Pdf`, the PDF `OpenWithProgids` entry,
`Applications\PdfButler.exe`, `Software\PdfButler\Capabilities`, `RegisteredApplications`,
and the optional `SystemFileAssociations\.pdf\shell\PdfButler` verb. Executable and PDF paths are quoted.
Re-run registration if you move the folder; one registration per user is supported.

The classic right-click entry is **Open in PDF Butler**. On Windows 11 it normally lives under **Show more options**;
top-level modern menu integration needs packaged identity / an IExplorerCommand extension (not implemented).
Explorer's **Open with** menu is also supported. Windows controls when it refreshes its app suggestions.

**Registering does not change the default PDF application.** Choose PDF Butler in Settings → Apps → Default apps,
or Open with → Choose another app → Always. Scripts never modify the protected `UserChoice` association.
Files opened from Explorer are forwarded over a current-user-only bounded local pipe into the running Windows instance.
Opening an existing path selects its tab; up to 10 PDFs can be open. Ctrl+Tab / Ctrl+Shift+Tab switch tabs; Ctrl+W closes one.
`--measure` deliberately bypasses single-instance forwarding. Linux currently starts a separate instance per launch.

PowerShell may block downloaded unsigned scripts. Review them, then use `Unblock-File` on the extracted scripts if appropriate;
no script changes your execution policy. Builds are unsigned: SmartScreen/reputation warnings are possible.
An MSI/MSIX, automatic updates and code signing are not included.

## Remove integration / uninstall

Run `Unregister-PdfButler.ps1` from the installed folder (`-WhatIf` previews it).
It removes only PdfButler's current-user registration and Start menu shortcut; it leaves application files intact.
If PDF Butler was the default, select another PDF app in Windows Settings. Close PDF Butler, then remove its install folder manually.

## Release blockers

These folders are suitable for private deployment, **not a completed public-release security gate**:
PDFium still runs in-process without an OS sandbox. A restartable restricted helper process is required by `docs/PLAN.md`
before distributing to others. Desktop DPI/accessibility/Explorer integration still need real-machine hand testing.
Review the application's own redistribution license and all dependency notices before public distribution.

## Branding

`assets/pdfbutler-icon.svg` is the editable original: a folded document wearing a bow tie, navy and mint.
`assets/pdfbutler-wordmark.svg` contains the wordmark. `pdfbutler.ico` has 16–256 px variants;
the executable and window use it. Rebuild the PNG/ICO on Windows with `pwsh tools/Build-Icon.ps1`.
