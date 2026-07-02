# Spool Tools for Revit — Claude Code orientation

Standalone Revit add-in (builds for Revit 2025 and Revit 2026 from one
source tree via `-p:RevitVersion=...`). Ships four ribbon buttons on a
single **Spool Tools** tab / **Spooling** panel:

- **Create Spool** — one-spool workflow (pick parts → sheet).
- **The Spooler** — batch workflow (walk a network, split into spools).
- **DeSpooler** — revert a spool (delete sheet/views, clear params, unpin).
- **Spool Config** — project-level defaults shared by both tools.

## Project layout

```
src/
├── SpoolTools.csproj                       ← .NET 8 + Revit refs (RevitVersion-parameterized, defaults 2026), AfterBuild deploy
├── SpoolTools.addin                        ← Revit add-in manifest (drops into Addins\<version>\ matching build)
├── SpoolToolsApp.cs                        ← IExternalApplication: ribbon panel + ExternalEvent plumbing
├── SpoolCommand.cs                         ← Create Spool entry
├── SpoolerCommand.cs                       ← The Spooler entry
├── DeSpoolerCommand.cs                     ← DeSpooler entry
├── SpoolConfigCommand.cs                   ← Spool Config entry
├── Models/                                 ← Small data-only types (SpoolRequest, RenumberOptions, tokens)
├── Revit/
│   ├── Spooling/
│   │   ├── SpoolService.cs                 ← Create Spool orchestrator: views + sheet + tags + dims
│   │   ├── SpoolerService.cs               ← The Spooler orchestrator: walk + split + per-spool build
│   │   ├── DeSpoolerService.cs             ← DeSpooler orchestrator: plan + execute in one transaction
│   │   ├── SpoolSettings.cs                ← ExtensibleStorage store (JSON blob on ProjectInformation)
│   │   ├── SpoolNumberRegistry.cs          ← Spool Number / status parameter helpers
│   │   ├── SpoolTitleblockRegions.cs       ← Per-titleblock drawable-region persistence
│   │   ├── SpoolSheetLayout.cs             ← 3×3 grid, third-angle projection layout math
│   │   ├── SpoolViewBuilder.cs             ← 3D view / iso / section factory + view-template apply
│   │   ├── SpoolAssemblyBuilder.cs         ← Optional AssemblyInstance branch (parts + assembly views)
│   │   ├── SpoolDimensioner.cs             ← First-cut dimension placement engine
│   │   ├── SpoolDirection.cs               ← Named view direction enum + view basis helpers
│   │   ├── SpoolerNetworkWalker.cs         ← Connected-network BFS from Start element
│   │   ├── SpoolerTemplateEngine.cs        ← {Service}/{ID}/{N:00}/{Number} token substitution
│   │   ├── SpoolerPreviewPainter.cs        ← Colours the preview 3D view per spool partition
│   │   ├── SpoolerRuleEvaluator.cs         ← Max Weight / Max Length auto-split rules
│   │   ├── SpoolerWeldPostProcessor.cs     ← "Convert Spool Joining Welds to Field Welds"
│   │   ├── RenumberService.cs              ← Item Number renumbering with identical-parts collapse
│   │   └── FabricationServiceLookup.cs     ← Fab service tree lookup helpers
│   ├── RevitEventHandler.cs                ← Generic IExternalEventHandler for modeless dialogs
│   ├── RibbonIconFactory.cs                ← Runtime StreamGeometry icons (spool, spooler, despool, config)
│   ├── ConnectorHelper.cs                  ← Physical-connector snapshotting
│   └── PartTypeClassifier.cs               ← PCF-type + straight-pipe classification (SKEY methods harmless dead code)
└── UI/                                     ← WPF dialogs (all modeless)
    ├── SpoolDialog.xaml(.cs)               ← Create Spool
    ├── SpoolerDialog.xaml(.cs)             ← The Spooler
    ├── SpoolConfigDialog.xaml(.cs)         ← Spool Config
    ├── LeaderSettingsDialog.xaml(.cs)      ← Attached / Free End + length overrides (popover)
    ├── SpoolMembershipWarningDialog.xaml(.cs) ← Safety-net "parts already on a spool" warning
    └── SpoolPreviewWindow.xaml(.cs)        ← Preview 3D view before Accept/Discard

docs/                                       ← User-facing + developer documentation
```

## Build and deploy

```
cd src
dotnet build -c Debug                       # default = Revit 2026
dotnet build -c Debug -p:RevitVersion=2025  # Revit 2025
```

Debug builds auto-deploy `SpoolTools.dll` + `SpoolTools.addin` to
`%APPDATA%\Autodesk\Revit\Addins\<RevitVersion>\`. Output goes into
`bin/Debug-Revit<RevitVersion>/` so the two versions don't overwrite
each other. A `REVIT<version>` compile-time symbol (e.g. `REVIT2026`)
is also defined for any source that needs to branch on the API surface.

If Revit is open, the DLL is locked — close Revit and rebuild, or skip
the deploy step and copy manually.

Override the Revit install path at the command line if your install
isn't the default:

```
dotnet build -c Debug -p:RevitInstallPath="D:\Revit 2026"
```

## Critical Revit API gotchas

These have bitten the original author multiple times — keep them in mind
when modifying view / tag / assembly code.

- **`ConnectorManager.Connectors` enumeration is unstable across calls.**
  Snapshot to a list before iterating multiple times.
- **`Connector` wrappers are never `ReferenceEquals` between calls** — even
  for the "same" physical connector. Compare by `Origin` (with
  `DistanceTo` tolerance) or by `(Owner.Id, connector index)`.
- **`XYZ.IsAlmostEqualTo` is NOT plain Euclidean distance.** It's a
  component-wise tolerance. Use `a.DistanceTo(b) < tol` when you mean
  "within X feet".
- **`ExtensibleStorage` schema names have character rules.** No spaces,
  no leading digit, ASCII only. Enforce on the `Schema.Builder` call.
- **WPF `Topmost = true` hides `TaskDialog.Show(...)` behind the dialog.**
  Set `Topmost = false` (or hide the dialog) before showing a
  TaskDialog inside a modeless popup.
- **IndependentTag leader anchor** — for Attached-End leaders, zero
  `LEADER_OFFSET_SHEET` explicitly to suppress Revit's type-default
  shoulder segment. For Free-End on pipes/elbows,
  `SetLeaderEnd(partRef, partCentre)` is required or Revit auto-picks
  the closest surface (often a weld / connector face).
- **`AssemblyInstance` view creation is one-transaction** — you can't
  create assembly + assembly views in separate transactions on the
  same AssemblyInstance without Revit rejecting the second one.

## Key design decisions worth knowing

- **Two orchestrators, one settings store.** `SpoolService` and
  `SpoolerService` are entirely separate control-flow paths (one part
  set vs a network walk), but both read and write the same
  `SpoolSettings` — so Spool Config changes apply to both.
- **Drawable region is per-titleblock**, not per-project. Persisted in
  `SpoolTitleblockRegions` keyed by titleblock ElementId so a project
  with an 11×17 spool sheet and a 24×36 spool sheet keeps regions for
  each.
- **Enhanced Tag Placement** — 24 compass directions at 15° increments
  × 3 tiers (1×, 1.5×, 2× the user's Tag Offset), tier-major
  ordering, shape-aware direction tried first at each tier
  (elbow-inside, tee-bisector, pipe-perpendicular), 10 % overlap
  threshold for both tag/part and tag/tag, Liang–Barsky leader-crossing
  penalty, priority-by-Item-Number with least-bad fallback. Off ⇒
  historical "1 × Tag Offset above the part" behaviour.
- **Item Number renumbering can collapse identical parts** — CID + size
  + Item Description + Item Code determines identity. Toggleable per
  run. "Use length as a separator" further divides pipes of different
  centreline lengths (rounded to 1/16″) when the collapse rule is on.
- **The Spooler's Start + Break model** — Start seeds the walk; each
  Break element caps a spool at that element and starts a new spool on
  the far side. Branches off tees always split.
- **ExtensibleStorage schema is independent of the parent PCF Exporter.**
  Fresh GUID in `SpoolSettings.cs` so both add-ins can coexist on the
  same project.

## When you change the placement algorithm

Test cases that matter:
- Single straight pipe with tags on 4 iso views — verify Enhanced Tag
  Placement doesn't produce false overlaps.
- Elbow with two tags nearby — the elbow tag should sit on the concave
  (inside) side.
- Tee with a branch tag — should land in the bisector wedge, not on
  top of a leg.
- Selection with parts already on another spool — safety-net dialog
  fires before the main dialog opens.
- Free-End leader on a pipe/elbow — endpoint anchors at part centre,
  not on a weld/connector face.

## When you change the UI

The dialogs use a hand-rolled INPC pattern (no MVVM framework). Look at
the ViewModel classes at the bottom of each `*Dialog.xaml.cs` for the
data binding contract. Notable patterns:

- **Required-field marker** — every required binding gets an `*` prefix
  in its label; the footer legend `*Required setting` explains it.
- **Modeless + ExternalEvent** — dialogs never call the Revit API
  directly. `SpoolToolsApp.SpoolHandler.SetAction(...)` + `.Raise()` is
  how work reaches the API thread.
- **Hide parent while picker is up** — `Owner.Hide()` before
  `PickObject`, restore on return. Prevents WPF dialogs from stealing
  focus during Revit picks.
