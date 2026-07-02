# Architecture

A file-by-file tour of the codebase, oriented at someone modifying it.

---

## High-level shape

The add-in follows the standard Revit pattern with four `IExternalCommand`
entries fanning out from one `IExternalApplication`:

```
SpoolToolsApp (IExternalApplication)         ← Revit entry, registers the ribbon panel
        │
        │  user clicks one of the four buttons
        ▼
┌───────────────────┬───────────────────┬───────────────────┬───────────────────┐
│                   │                   │                   │                   │
SpoolCommand   SpoolerCommand    DeSpoolerCommand    SpoolConfigCommand
  (Create Spool)  (The Spooler)     (revert)         (project defaults)
    │                 │                 │                 │
    ▼                 ▼                 ▼                 ▼
SpoolDialog     SpoolerDialog    (TaskDialog only)   SpoolConfigDialog
    │                 │                                   │
    ▼                 ▼                                   │
SpoolService    SpoolerService                            │
                                                          │
              All four share ──────────────────► SpoolSettings (ES on doc.ProjectInformation)
```

All dialogs are **modeless** — they don't block Revit. To call back into
the Revit API safely (Revit's API is single-threaded), they post actions
through `ExternalEvent`. That pattern is encapsulated in
`RevitEventHandler`.

---

## File-by-file

### Entry points

#### `src/SpoolToolsApp.cs`

`IExternalApplication`. Runs when Revit starts.

- Creates the **Spool Tools** ribbon tab and **Spooling** panel.
- Adds the four `PushButtonData`s (Create Spool, The Spooler,
  DeSpooler, Spool Config) with runtime-generated icons from
  `RibbonIconFactory`.
- Constructs the static `SpoolHandler` + `SpoolEvent` pair that the
  dialogs use to call back into the Revit API thread.

#### `src/SpoolCommand.cs` — Create Spool

`IExternalCommand`. Runs when the user clicks **Create Spool**.

- Reads the current selection, filters to `FabricationPart`s.
- Runs the safety-net check (`SpoolNumberRegistry.GroupByExistingSpool`)
  — if any parts already belong to a spool, shows
  `SpoolMembershipWarningDialog` before proceeding.
- Constructs and shows the modeless `SpoolDialog`.

#### `src/SpoolerCommand.cs` — The Spooler

`IExternalCommand`. Same safety-net + selection pattern, opens
`SpoolerDialog`.

#### `src/DeSpoolerCommand.cs` — DeSpooler

`IExternalCommand`. No dialog — uses `TaskDialog` for confirmation and
result summary. Reads Spool Numbers off the selection, delegates plan
building + execution to `DeSpoolerService`.

#### `src/SpoolConfigCommand.cs` — Spool Config

`IExternalCommand`. Loads project resources (titleblocks, schedules,
tag families, view templates, dimension styles, text params, titleblock
regions) and opens `SpoolConfigDialog` seeded from
`SpoolSettings.Load(doc)`.

---

### Business logic (`src/Revit/Spooling/`)

#### `SpoolService.cs` — the Create Spool engine

The largest file in the repo. Steps for one spool:

1. **Validate + open transaction group.** Everything downstream is one
   Ctrl+Z step.
2. **Renumber Item Numbers** (`RenumberService`) if the run enabled it.
3. **Write Spool Number + status parameter** on every part.
4. **Pin the parts** — spooled parts shouldn't drift.
5. **Compute view basis** (`SpoolDirection`) + build the requested
   ortho / iso 3D views (`SpoolViewBuilder`).
6. **Lay out the sheet** — `SpoolSheetLayout` picks positions in a 3×3
   grid inside the titleblock's drawable region.
7. **Place tags** — Enhanced Tag Placement algorithm (24 directions ×
   3 tiers, shape-aware, leader-crossing check) OR the historical
   "1 × Tag Offset above the part" behaviour.
8. **Place dimensions** (feature-flagged off in v1.0.0) via
   `SpoolDimensioner`.
9. **Add the schedule to the sheet.**
10. **Optional Assembly branch** — if Use Assemblies is on,
    `SpoolAssemblyBuilder` builds an `AssemblyInstance` + assembly
    views + assembly sheet instead.
11. **Commit the transaction group.**

#### `SpoolerService.cs` — the batch engine

Same overall shape, wrapped in a walk + split:

1. **Walk the network** from the Start element
   (`SpoolerNetworkWalker`) capped at the selection pool.
2. **Split at breaks + tee branches** into candidate spools.
3. **Evaluate auto-split rules** (`SpoolerRuleEvaluator`) — Max Weight,
   Max Length, At Field Welds — refining candidates.
4. **Assign Spool Numbers + Sheet Numbers + Names** by running each
   candidate through `SpoolerTemplateEngine`.
5. **For each candidate**, call the same downstream pipeline as
   `SpoolService` (renumber → views → sheet → tags → dims → schedule).
6. **Optional Field-Weld conversion** post-pass
   (`SpoolerWeldPostProcessor`) — welds at spool boundaries become
   Field Welds on the model.

#### `DeSpoolerService.cs` — reversal engine

Two-phase design (`BuildPlan` → `Execute`) so the confirmation dialog
can show accurate counts before touching the model.

- `BuildPlan(spoolNumbers)` — collects the fabrication parts,
  assemblies, sheets (matched by substring on sheet name), and views
  (placed on those sheets, excluding schedules).
- `Execute(plan, statusParamName)` — one transaction: unpin parts,
  clear Spool Number + status, delete assemblies (cascades to sheets +
  views), delete standalone spool sheets + their views.

#### `SpoolSettings.cs`

ExtensibleStorage wrapper. Persists a single JSON blob on
`doc.ProjectInformation` covering everything in Spool Config:
titleblock id, schedule id, view template id, tag family id, tag
placement toggle, leader defaults, dimension defaults, spool limits,
custom status param, renumbering defaults, spooler templates.

Schema GUID is unique to Spool Tools — independent of the parent PCF
Exporter add-in so both can coexist on the same project.

Format is a single delimited key/value string (`k=v;k=v;…`) to keep the
storage compact and forward-compatible: unknown keys are ignored on
read, so newer versions don't lose older projects' data.

#### `SpoolNumberRegistry.cs`

Utility helpers for the `Spool Number` and status parameters:

- `CurrentValuesOn(doc, ids)` — collects the Spool Numbers written to a
  set of elements.
- `GroupByExistingSpool(doc, ids)` — for safety-net checks: returns
  spool-number → element-ids for any element in the input that already
  has a Spool Number.

#### `SpoolTitleblockRegions.cs`

Per-titleblock drawable-region persistence. A project with an 11×17
spool sheet and a 24×36 spool sheet keeps distinct regions for each.

#### `SpoolSheetLayout.cs`

3×3 grid layout math for the sheet. Third-angle projection convention
so views appear in the reader's expected positions (Top above Front,
Right beside Front, isos in the corners).

#### `SpoolViewBuilder.cs`

Creates the 3D views, applies the view template, sets crop / far clip
to the union bounding box of the spool's parts. Locks iso views so
they can't be accidentally rotated.

#### `SpoolAssemblyBuilder.cs`

Optional AssemblyInstance branch. Same view-set + sheet layout as the
standard path, but wrapped in a Revit `AssemblyInstance` — parts
become assembly members and views become assembly views.

Critical single-transaction constraint here: `Assembly.Create` +
`AssemblyViewUtils.CreateXxxView` must happen in one transaction or
Revit rejects the view creation.

#### `SpoolDimensioner.cs`

First-cut dimension placement engine. Feature-flagged off in Spool
Config v1.0.0 — the engine exists and runs cleanly, but placement
heuristics need more work before it can be recommended for production.

#### `SpoolDirection.cs`

Named view direction enum (Top / Bottom / Left / Right / Front / Back
plus NW / NE / SW / SE isos) + view basis helpers.

#### `SpoolerNetworkWalker.cs`

BFS from the Start element through the connector graph, bounded by
the selection pool. Records the walk order + branching structure so
downstream splitting is deterministic.

#### `SpoolerTemplateEngine.cs`

Token substitution for `{Service}`, `{ServiceName}`, `{ID}`, `{N}`,
`{N:00}`, `{N:000}`, `{Number}` in the spool number / name / sheet
number templates.

#### `SpoolerPreviewPainter.cs`

Colours the preview 3D view per spool partition. One colour per
resulting spool so the user can eyeball the split before creating.

#### `SpoolerRuleEvaluator.cs`

Applies Max Weight / Max Length / At Field Welds auto-split rules to
candidate spools coming out of the walker.

#### `SpoolerWeldPostProcessor.cs`

Converts welds sitting at spool boundaries to Field Welds after the
batch runs. Optional; behind the "Convert Spool Joining Welds to Field
Welds" checkbox.

#### `RenumberService.cs`

Item Number renumbering. Sequential by default; with the
identical-parts collapse rule it groups by CID + size + Item
Description + Item Code and shares one number across the group.
Optional "Use length as a separator" further divides pipes by
centreline length (rounded to 1/16″).

#### `FabricationServiceLookup.cs`

Fab service tree lookup helpers — finds the service a part belongs
to, iterates service buttons, etc. Used for service-aware tokens in
the Spooler templates.

---

### Cross-cutting Revit helpers

#### `src/Revit/RevitEventHandler.cs`

Generic `IExternalEventHandler` — modeless dialogs call `.SetAction(...)`
+ `.Raise()` and the handler posts the action onto the Revit API thread.

#### `src/Revit/RibbonIconFactory.cs`

Runtime StreamGeometry icons for the four ribbon buttons. Draws the
elbow-with-pipes glyph for Create Spool, the two-elbow variant for The
Spooler, the same glyph with a red "prohibited" ring for DeSpooler, and
a gear tinted to match for Spool Config. No PNG assets shipped — the
icons are drawn at load time at 16 × 16 and 32 × 32.

#### `src/Revit/ConnectorHelper.cs`

Snapshotting + tolerance-based connector matching (see the CLAUDE.md
gotchas about `ConnectorManager.Connectors` instability and Connector
wrapper `!ReferenceEquals`).

#### `src/Revit/PartTypeClassifier.cs`

Straight-pipe classification + PCF-type derivation. Includes SKEY
methods inherited from the parent PCF Exporter — they're dead code in
Spool Tools but harmless (used by nothing here).

---

### UI (`src/UI/`)

All dialogs are hand-rolled WPF with an INPC ViewModel at the bottom
of each `.xaml.cs`. No MVVM framework.

- **`SpoolDialog.xaml(.cs)`** — Create Spool.
- **`SpoolerDialog.xaml(.cs)`** — The Spooler.
- **`SpoolConfigDialog.xaml(.cs)`** — Spool Config.
- **`LeaderSettingsDialog.xaml(.cs)`** — Attached / Free End radios
  with visual diagrams, leader length, Tag Offset, Enhanced Tag
  Placement toggle. Opens from either per-run dialog as a popover.
- **`SpoolPreviewWindow.xaml(.cs)`** — the Accept / Discard preview
  after Create Spool builds a spool.
- **`SpoolMembershipWarningDialog.xaml(.cs)`** — the safety-net
  warning for selections that overlap existing spools.

---

## Data flow at Create Spool time

```
User selects parts → clicks Create Spool
    │
    ▼
SpoolCommand: safety-net check → SpoolDialog opens
    │
    ▼
User adjusts number / views / tagging → clicks Create Spool
    │
    ▼
SpoolDialog builds a SpoolRequest (immutable snapshot of every input)
    │
    ▼
ExternalEvent → Revit API thread → SpoolService.Build(request)
    │
    ├─ RenumberService (if enabled)
    ├─ SpoolNumberRegistry.Write (Spool Number + status)
    ├─ Pin parts
    ├─ SpoolViewBuilder (create 3D views, apply template)
    ├─ SpoolSheetLayout (position viewports in drawable region)
    ├─ Tag placement (Enhanced or historical)
    ├─ SpoolDimensioner (if enabled)
    ├─ Schedule placement
    └─ (Optional) SpoolAssemblyBuilder branch
    │
    ▼
Commit transaction group → dialog reports success
```

---

## Data flow at The Spooler time

```
User selects starting parts → clicks The Spooler
    │
    ▼
SpoolerCommand: safety-net check → SpoolerDialog opens
    │
    ▼
User picks Start + Breaks + rules → clicks Create Spools
    │
    ▼
SpoolerNetworkWalker walks the network → SpoolerRuleEvaluator refines
    │
    ▼
SpoolerTemplateEngine assigns numbers/names/sheet numbers
    │
    ▼
For each candidate spool:
    └─ same downstream pipeline as SpoolService.Build
```

---

## Persistence surfaces

- **`SpoolSettings`** — one blob per project (ExtensibleStorage on
  `doc.ProjectInformation`). Everything in Spool Config lives here.
- **`SpoolTitleblockRegions`** — per-titleblock drawable region
  polygons. Keyed by titleblock ElementId.
- **`Spool Number` parameter** — one text parameter on
  `FabricationPart`. The tool creates it as a project parameter on
  first use if missing.
- **Configured status parameter** — user-picked text parameter on
  `FabricationPart` (default `Fabrication Status`). The tool writes
  the configured value on create and clears it on DeSpool.
- **Assembly / sheet / view names** — derived from the spool number
  template. Not stored separately.
