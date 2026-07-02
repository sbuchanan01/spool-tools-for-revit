# Developer guide

How to set up your development environment, build, debug, and ship changes.

---

## Prerequisites

- **Windows 10/11** — Revit only runs on Windows.
- **Revit 2025 or 2026** — full install. The add-in references DLLs
  from your Revit install folder (default
  `C:\Program Files\Autodesk\Revit <version>\`). Build defaults to
  Revit 2026; pass `-p:RevitVersion=2025` to target 2025 instead.
- **.NET 8 SDK** — [download](https://dotnet.microsoft.com/download).
  The csproj targets `net8.0-windows`.
- **A C# IDE** (any will do):
  - Visual Studio 2022 / 2026 Community — open `src/SpoolTools.csproj`.
  - JetBrains Rider — open the same file.
  - VS Code + C# Dev Kit — same.
  - Claude Code — open the repo root; the included `CLAUDE.md` orients it.

---

## Clone and build

```powershell
git clone https://github.com/sbuchanan01/spool-tools-for-revit.git
cd spool-tools-for-revit/src
dotnet build -c Debug
```

Successful output ends with:

```
Build succeeded.
    0 Error(s)
Time Elapsed 00:00:04.xx
```

(Some `MSB3277` warnings about RevitAPIUI re-references are normal and
harmless.)

Debug builds auto-deploy to `%APPDATA%\Autodesk\Revit\Addins\<version>\`
via the `DeployToRevitAddins` MSBuild target — the `<version>` matches
whatever `-p:RevitVersion=...` you passed (default 2026). **If Revit is
open, the DLL is locked** — the copy step is skipped (the build itself
still succeeds).

If your Revit is installed somewhere non-default:

```powershell
dotnet build -c Debug -p:RevitInstallPath="D:\Revit 2026"
```

To build for **both** Revit 2025 and 2026 in one go (typical for
releases):

```powershell
dotnet build -c Release -p:RevitVersion=2025
dotnet build -c Release -p:RevitVersion=2026
```

Outputs land in `bin/Release-Revit2025/` and `bin/Release-Revit2026/`
respectively.

---

## Debug-and-iterate cycle

The typical inner loop:

1. **Close Revit.** This releases the DLL lock so the post-build copy can
   land.
2. Make code changes in your IDE.
3. `dotnet build -c Debug` (or hit Build in your IDE).
4. **Open Revit**, open a model that contains Fabrication parts.
5. Click the button you're iterating on (Create Spool / The Spooler /
   DeSpooler / Spool Config). Repro your test case.
6. Loop.

If you want to **attach a debugger**, the standard Revit add-in debug
workflow is:

1. Open the csproj in Visual Studio.
2. Open **Properties** on the project → Debug → "Launch Profile" → set the
   executable to your Revit binary
   (`C:\Program Files\Autodesk\Revit <version>\Revit.exe`).
3. Hit F5 — Visual Studio starts Revit with the debugger attached.
4. Set breakpoints in SpoolTools source; they hit when the relevant code
   path runs in Revit.

For Rider, it's the same idea via Run/Debug configurations → .NET
Executable.

---

## Code layout

See [architecture.md](architecture.md) for a full file-by-file tour. The
short version:

- **`src/SpoolToolsApp.cs`** — Revit's entry point. Registers the ribbon
  panel and an ExternalEvent the modeless dialogs use to call back into
  the Revit API thread.
- **`src/SpoolCommand.cs` / `SpoolerCommand.cs` / `DeSpoolerCommand.cs` /
  `SpoolConfigCommand.cs`** — one `IExternalCommand` per ribbon button.
- **`src/UI/*Dialog.xaml(.cs)`** — the six WPF windows (Create Spool,
  The Spooler, Spool Config, Leader Settings, Preview,
  Safety-net warning).
- **`src/Revit/Spooling/SpoolService.cs`** — Create Spool's orchestrator.
- **`src/Revit/Spooling/SpoolerService.cs`** — The Spooler's orchestrator.
- **`src/Revit/Spooling/SpoolSettings.cs`** — shared ExtensibleStorage
  store on `doc.ProjectInformation`.

---

## Making changes safely

A few rules of thumb that have saved the original author pain:

### Don't trust `XYZ.IsAlmostEqualTo` as a distance check

It does a component-wise comparison, not Euclidean. Always use:

```csharp
if (a.DistanceTo(b) < tolFt) { ... }
```

### Snapshot `ConnectorManager.Connectors` before iterating

The lazy enumeration isn't stable across calls — converting to a list
once avoids surprises:

```csharp
var conns = part.ConnectorManager.Connectors.Cast<Connector>().ToList();
```

### Connector wrappers don't `ReferenceEquals`

Even for the "same" connector returned twice from Revit. Compare by
`Origin` (with `DistanceTo` tolerance) or by `(Owner.Id, connector
index)`.

### `PickObject(ObjectType.PointOnElement, filter)` needs `AllowReference`

In a custom `ISelectionFilter`, `AllowReference(reference, point)` must
return `true` for the filter to accept a reference-based pick. Easy to
miss.

### WPF `Topmost = true` hides `TaskDialog.Show(...)`

When a modeless dialog with `Topmost = true` shows a `TaskDialog` (Revit
error / info popup), the TaskDialog opens **behind** the WPF window and
looks like a hang. Set `Topmost = false` (or hide the WPF window) before
calling `TaskDialog.Show`.

### `AssemblyInstance` view creation is single-transaction

You can't create the assembly, commit, then add assembly views in a
second transaction — Revit rejects the second one. `SpoolAssemblyBuilder`
does both inside one transaction.

### Test these scenarios after any placement-algorithm change

- Enhanced Tag Placement toggle **on** and **off** — both paths must
  work.
- Elbow with a tag nearby — tag should sit on the concave (inside) side.
- Tee — tag should land in the branch/run bisector wedge.
- Free-End leader on a pipe or elbow — endpoint anchored at part
  centre, not on a weld / connector face.
- Selection that overlaps an existing spool — safety-net warning
  fires before Create Spool opens.

---

## Ship a new release

For binary distribution (so non-developers can drop in the DLL without
building):

### 1. Bump the version

Update `<Version>` in `src/SpoolTools.csproj`.

### 2. Release build (both Revit versions)

```powershell
cd src
dotnet build -c Release -p:RevitVersion=2025
dotnet build -c Release -p:RevitVersion=2026
```

Output lands in `src/bin/Release-Revit2025/` and
`src/bin/Release-Revit2026/`.

### 3. Package

Make one ZIP per Revit version, each containing:

- `SpoolTools.dll` (from the matching `bin/Release-Revit<version>/`)
- `SpoolTools.addin` (from `src/`)
- `LICENSE` (from repo root)
- `README.md` (from repo root)

Name them `SpoolTools-Revit2025-v{version}.zip` and
`SpoolTools-Revit2026-v{version}.zip`.

### 4. Tag and push

```powershell
git tag v1.0.0
git push origin v1.0.0
```

### 5. Create the GitHub Release

Via the GitHub UI or:

```powershell
gh release create v1.0.0 `
  releases/SpoolTools-Revit2025-v1.0.0.zip `
  releases/SpoolTools-Revit2026-v1.0.0.zip `
  --title "v1.0.0" --notes "Release notes..."
```

Include the Autodesk evaluation disclaimer in the release notes.

---

## Code style

- **No comments unless the WHY is non-obvious.** Identifier names should
  carry the WHAT.
- **`/// <summary>` XML docs** on public types and members that aren't
  self-explanatory.
- **British vs American English** — the original author's convention
  is "British in class names + ExtensibleStorage field keys, American
  in user-facing strings" (e.g. "Labour" internally, "Labor" in
  dialogs). Spool Tools doesn't really exercise this but the convention
  is there if it matters.
- **Required-field markers** — add an `*` prefix to any dialog label
  whose binding is required for the tool to run. The footer legend
  `*Required setting` explains it.

---

## Reporting issues

[File an issue](https://github.com/sbuchanan01/spool-tools-for-revit/issues)
with:
- Revit version + build number (Help → About Revit)
- What you did, what you expected, what happened
- Stack trace if there was a TaskDialog
- A minimal sample model if the bug is data-dependent (e.g. a specific
  fitting or fabrication service configuration)
