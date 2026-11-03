# Cross-platform building, publishing and Windows integration

## Build a beta deployment

From the repository root, using the pinned .NET 10 SDK:

```text
dotnet build PdfButler.slnx
dotnet test PdfButler.slnx
dotnet publish src/PdfButler.App -p:PublishProfile=win-x64
# Output: artifacts/publish/win-x64 plus artifacts/publish/win-x64.zip.
# Self-contained JIT build (no AOT), for comparison or diagnostics:
dotnet publish src/PdfButler.App -p:PublishProfile=win-x64 -o artifacts/publish/win-x64-jit -p:PublishAot=false
# The previous framework-dependent ReadyToRun build, if ever needed again:
dotnet publish src/PdfButler.App -p:PublishProfile=win-x64 -o artifacts/publish/win-x64-r2r -p:PublishAot=false -p:SelfContained=false -p:PublishSingleFile=true -p:PublishReadyToRun=true
# Linux glibc x64 (must be published on Linux; see prerequisites):
dotnet publish src/PdfButler.App -p:PublishProfile=linux-x64
# Clean a custom output directory too (deletes its entire contents):
dotnet publish src/PdfButler.App -p:PublishProfile=win-x64 -o artifacts/publish/custom -p:CleanPublishDirectory=true
```

Building needs only the .NET SDK, not PowerShell, FAKE or a platform-specific shell. The publish profiles also need
a native toolchain, because they use **Native AOT**:

- **Windows:** Visual Studio 2022 (or Build Tools) with the **Desktop development with C++** workload (MSVC linker and Windows SDK).
- **Linux:** `clang` and `zlib1g-dev` (Debian/Ubuntu names). AOT cannot cross-compile between operating systems, so publish the
  Linux folder on Linux. The executable needs the build machine's glibc version or newer: publish on the oldest distro you support.

These commands work from the repository root on Windows and Linux; quote paths containing spaces.
Both profiles are also available in IDE publishing. Default profile destinations are anchored to the repository;
an explicit `-o` path is relative to your current directory, as with ordinary `dotnet publish`.
Profiles are Release, **Native AOT** and therefore self-contained and trimmed. **Testers install nothing**: there is no .NET runtime prerequisite.
Ordinary `dotnet build`/`dotnet run` builds stay JIT; only publishing compiles ahead of time (about 30-40 s on the dev machine).

`PdfButler.exe` (`PdfButler` on Linux) is one native executable containing the app, its .NET dependencies and the runtime.
PDFium, Skia, HarfBuzz and ANGLE remain alongside it as native libraries; they are needed, not spare DLLs.
The per-document renderer helper is the same executable. **Extract and deploy the whole ZIP/folder**, not just the executable.
Keep `licenses/` and notices intact (`licenses/dotnet/` holds the .NET runtime's license, now that the runtime ships inside the app).
The ZIP includes a short tester-facing `README.md` with launch and update instructions.
The Windows folder is about 48 MB (20 MB zipped). The ReadyToRun build it replaced needed a separately installed runtime.

AOT rules for the code:

- No reflection-based `JsonSerializer`: under AOT it throws at runtime. Use `Utf8JsonWriter`/`JsonDocument` as in
  `Core/Settings.fs`, `WindowPlacement.fs`, `SingleInstance.fs` and `Diagnostics.fs`. F# cannot run the System.Text.Json source generator.
- Use the generic `Marshal` overloads (`GetFunctionPointerForDelegate<T>`, `StructureToPtr<T>`).
- Avoid `printf`-family formatting (`sprintf`, `%A`, `failwithf`) in the app. Use interpolated strings without format specifiers, or `String.Format`.
- The profiles turn `IL2026` and `IL3050` (reflection or runtime code generation the compiler can't prove safe) into **build errors**, so these regressions
  stop the publish. The remaining `IL2104`/`IL3053` warnings summarise FSharp.Core's own printf/reflection code;
  FSharp.Core can't be fixed from here. Run with `-p:TrimmerSingleWarn=false` to see the details.

`tools/Deployment.targets` rejects option combinations that don't apply: `SelfContained=false`, `PublishSingleFile` or `PublishReadyToRun`
together with AOT, and trimming without AOT (not validated). No manual DLL pruning is used.

Every successful profile publish also creates/replaces a **sibling ZIP**, including for a custom `-o` path:
`-o artifacts/publish/custom` produces `artifacts/publish/custom.zip`. ZIP entries are the folder contents,
without an extra enclosing directory, so extract into a new dedicated folder. The ZIP is built only after
the native-library check, then moved into place after compression completes. Failed publishing does not
replace the last good ZIP. Use `-p:CreatePublishArchive=false` to skip ZIP creation (an older ZIP, if any,
is left unchanged; do not mistake it for a new build).
In 20-launch tests per document across three PDFs with per-document renderer helpers, Native AOT reduced median launch-to-first-page
from 608-624 ms (framework-dependent ReadyToRun) to 178-190 ms on the Windows dev machine, and UI working set by about 37 MiB; details are in `PLAN.md`.
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
Linux also requires the usual Avalonia X11/font/GL platform dependencies, and ICU (`libicu`) because globalization is not invariant.
The Linux AOT folder has not been published or run on Linux yet (CI publishes it on Ubuntu 24.04, so testers then need glibc 2.39 or newer).
Extract with a tool that keeps executable permissions, or run `chmod +x PdfButler`.

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

Extract the ZIP to its final location, then run `PdfButler.exe` for portable use. No .NET installation is needed.
No registry changes happen at launch.
For a per-user installation, run from the extracted folder:

```powershell
.\Install-PdfButler.ps1 -WhatIf        # preview; no changes
.\Install-PdfButler.ps1               # copies to %LOCALAPPDATA%\Programs\PdfButler
```

This creates a Start menu shortcut and registers the app for PDFs. No administrator rights are needed.
The destination must be new, empty, or an existing PDF Butler installation. To update, close the app and
run the new build's installer: it asks before deleting the old installation folder and copying the new one
(`-Force` skips the question). It refuses non-empty folders that don't contain `PdfButler.exe`, and refuses
while PDF Butler is running from the install folder.

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
