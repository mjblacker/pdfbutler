# Cross-platform building, publishing and Windows integration

## Build a beta deployment

From the repository root, using the pinned .NET 10 SDK:

```text
dotnet build PdfButler.slnx
dotnet test PdfButler.slnx
dotnet publish src/PdfButler.App -p:PublishProfile=win-x64
# Output: artifacts/publish/win-x64 plus artifacts/publish/win-x64.zip.
# Alternative output / JIT comparison:
dotnet publish src/PdfButler.App -p:PublishProfile=win-x64 -o artifacts/publish/win-x64-jit -p:PublishReadyToRun=false
# Linux glibc x64 (build/test on Linux before distributing):
dotnet publish src/PdfButler.App -p:PublishProfile=linux-x64
# Clean a custom output directory too (deletes its entire contents):
dotnet publish src/PdfButler.App -p:PublishProfile=win-x64 -o artifacts/publish/custom -p:CleanPublishDirectory=true
```

Building and publishing need only the .NET SDK, not PowerShell, FAKE or a platform-specific shell.
These commands work from the repository root on Windows and Linux; quote paths containing spaces.
Both profiles are also available in IDE publishing. Default profile destinations are anchored to the repository;
an explicit `-o` path is relative to your current directory, as with ordinary `dotnet publish`.
Profiles are Release, **framework-dependent**, ReadyToRun and untrimmed. Testers must install the
**.NET 10 x64 Runtime** first (the SDK, ASP.NET Core and Windows Desktop runtime are not required).
Download it from [Microsoft's .NET 10 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0);
on Linux use the distro's .NET installation instructions for the `dotnet-runtime-10.0` package.
`dotnet --list-runtimes` should include `Microsoft.NETCore.App 10.0.x` for x64.

The managed app and its managed dependencies are bundled into `PdfButler.exe` (`PdfButler` on Linux).
PDFium, Skia, HarfBuzz and platform-native libraries remain alongside it; they are needed, not spare DLLs.
This avoids temporary extraction and keeps the per-document renderer working from the same executable.
**Extract and deploy the whole ZIP/folder**, not just the executable. Keep `licenses/` and notices intact.
The ZIP includes a short tester-facing `README.md` with prerequisites, launch and update instructions.

The previous self-contained output included the full .NET runtime, including Visual Basic compatibility
assemblies even though the app is F#. Requiring the runtime separately removes those assemblies rather
than guessing which runtime DLLs can be deleted. Trimming is still disabled: Avalonia/F# reflection paths
need separate validation before trimming is safe. Bundling reduces file clutter; the ZIP compresses transfers.
Use `-p:PublishSingleFile=false` for a diagnostic loose-file build, or `-p:SelfContained=true` if a tester
cannot install .NET (larger, but still bundled). No manual DLL pruning is used.

Every successful profile publish also creates/replaces a **sibling ZIP**, including for a custom `-o` path:
`-o artifacts/publish/custom` produces `artifacts/publish/custom.zip`. ZIP entries are the folder contents,
without an extra enclosing directory, so extract into a new dedicated folder. The ZIP is built only after
the native-library check, then moved into place after compression completes. Failed publishing does not
replace the last good ZIP. Use `-p:CreatePublishArchive=false` to skip ZIP creation (an older ZIP, if any,
is left unchanged; do not mistake it for a new build).
ReadyToRun is a reversible publishing option, not a NativeAOT commitment. In 20-launch tests per document across three PDFs,
it reduced median launch-to-first-page from 1.14–1.16 s (JIT) to 0.56–0.58 s on the Windows dev machine; details are in `PLAN.md`.
Those measurements predate per-document renderer processes and must be repeated for this architecture.
The profiles import MSBuild deployment checks (`tools/Deployment.targets`), including a check that PDFium
is beside the executable after publishing. The standard `artifacts/publish/<runtime>` directory is **deleted
before copying the new publish**, preventing stale DLLs. Keep only generated deployment files there.
Custom `-o` directories are not cleaned unless you pass `-p:CleanPublishDirectory=true`; that opt-in deletes
the entire chosen directory, including unrelated files. Use `-p:CleanPublishDirectory=false` to disable cleanup
even for the standard output. Without cleanup, obsolete files may remain.
Cleanup rejects filesystem roots, repository ancestors, source folders, the shared `artifacts` root and
paths through symbolic links/junctions. Inside the repository, only subdirectories of `artifacts` can be cleaned;
custom directories outside the repository require explicit opt-in. This cleans publish output, not build intermediates
(`dotnet clean` is separate). Do not publish over a running application. A failed publish can leave partial output;
do not deploy it until a retry succeeds.
Run `dotnet fsi tools/Test-BuildTools.fsx` to check deployment guards/cleanup/ZIPs, notice collection, icon generation
and measurement-script behavior without a desktop. CI runs these checks on both operating systems.

No corpus, PDFs, test artifacts or passwords are packaged. Third-party notices are included.
Windows x64 is the tested target. Linux still needs full distro/font verification; its pinned PDFium notice texts have been compared and match the bundle.
Linux also requires the usual Avalonia X11/font/GL platform dependencies. A Linux publish made on Windows
may need `chmod +x PdfButler` after ZIP extraction; Linux-native publishing preserves executable permissions.

To reproduce the startup measurements (.NET F# Interactive; one discarded warm-up, then at least 20 fresh processes per document):

```text
dotnet fsi tools/Measure-Startup.fsx --executable artifacts/publish/win-x64/PdfButler.exe corpus/cache/pdfjs/test/pdfs/tracemonkey.pdf
# On a Linux desktop, use artifacts/publish/linux-x64/PdfButler instead.
```

Pass additional PDF paths as separate arguments to cover several documents. Optional flags are
`--repetitions 20..1000` and `--output <json>`. Results include raw samples and p50/p95 in `artifacts/startup.json`.
`--measure` now reports `rendererWorkingSetMB` / `rendererPrivateMB` as well as UI-process memory.
Account for both processes; the existing `workingSetMB` / `privateMB` fields describe only the UI.

## Windows: portable or per-user installation

Install the .NET 10 x64 Runtime, extract the ZIP to its final location, then run `PdfButler.exe` for portable use.
If the runtime is missing, the .NET apphost shows its standard missing-runtime/download prompt.
No registry changes happen at launch.
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
PDFium now runs in a separate helper per document for crash isolation, but **without an OS sandbox**.
The helper is the same executable in internal headless mode; deployment needs no extra file. OS restrictions
and brokered document access are still required by `docs/PLAN.md` before distributing to others (see `docs/RENDERER.md`).
Desktop DPI/accessibility/Explorer integration still need real-machine hand testing.
Review the application's own redistribution license and all dependency notices before public distribution.

## Branding

`assets/pdfbutler-icon.svg` is the editable original: a folded document wearing a bow tie, navy and mint.
`assets/pdfbutler-wordmark.svg` contains the wordmark. `pdfbutler.ico` has 16–256 px variants;
the executable and window use it. Rebuild the PNG/ICO on Windows or Linux with `dotnet fsi tools/Build-Icon.fsx`.
This optional script restores pinned SkiaSharp packages (matching Avalonia) on first use; normal builds use
the committed assets and do not regenerate them. Use `--output <directory>` to preview without replacing assets.
