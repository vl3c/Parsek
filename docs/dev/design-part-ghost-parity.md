# Design: part ghost parity (`PartParitySweep`, the parity viewer)

Status: DRAFT rev 2, 2026-11-21, not built; the P0 capture spike flew green on both installs the
same day (Appendix B). Rev 1 was reviewed by two clean-context reviewers
(Opus 5.5 high, Fable 5.2 high); rev 2 folds their findings in (disposition table in Appendix A).
Third picture system, beside the GUI census (`design-gui-tree-dump.md`, `design-gui-mirror.md`) and
the map pictures (`design-map-geometry-dump.md`, `design-map-mirror.md`).

## 1. The problem

Parsek's ghost is not a clone of the part prefab. `GhostVisualBuilder.AddPartVisuals` builds new
GameObjects that mirror the transform chain of each renderer under the prefab's `model` subtree and
attach the prefab's `sharedMesh` / `sharedMaterials`. No PartModule ever runs on a ghost, so every
visual change a module makes at OnStart or at runtime (variant switch, shroud pick, node-set toggle,
fairing build, mesh hider, material swap, deploy pose) is either re-implemented by Parsek from config
plus the snapshot, or missing on the ghost. Things the mirror does not copy at all: the source
`activeSelf`, `renderer.enabled`, MeshRenderer shadow flags, the layer, MaterialPropertyBlocks. Each
new mod adds modules Parsek has never seen (ReStock alone ships ten runtime `ModuleRestock*` modules
on about 50 parts). Today these differences are found one at a time, in play (the ReStock LV-T45
skirt, the small Mk16-XL canopy, the engine-plate skirts of T25).

`FairingGhostVisualInGameTests` (lane FGV-1, about 130 s wall per run) proved the method for six
hand-built configurations: spawn the real vessel co-orbital with an orbiting host, take its
production snapshot (`FlightRecorder.TryBackupSnapshot`), build the ghost from it
(`GhostVisualBuilder.BuildTimelineGhostFromSnapshot`), photograph both. This design turns that into
a sweep over EVERY part of an install, in every skin, from fixed part-local views, with measured
numbers per image, a structural diff per renderer, and a web page to read it.

Goals, in order:

1. For each part x skin of an install, two pictures per view (real part, ghost) from the same
   camera and pose, so any difference is the ghost's.
2. Views fixed in the PART's own frame (two sides, top, bottom), so a wrong orientation reads as a
   different picture, not a different camera.
3. Numbers per view and a renderer-level structural diff, so 1500 cases sort worst-first, a missing
   or extra mesh is named, and a later run compares with an earlier one.
4. A static offline web page: grid of every case, filters, and a detail view with real / ghost /
   difference side by side.
5. Room to grow without a new system: deploy states, attach-node context, animation strips (GIF),
   then plumes and other FX.

Non-goals: trajectories, ghost placement in playback, LOD / soft caps, map rendering.

## 2. Decisions (summary)

| # | Decision | Why |
| --- | --- | --- |
| D1 | Real side = a one-part vessel authored from the prefab (FGV's `AuthorPartNode` path), skin set in BOTH places KSP reads (module `selectedVariant` and PART `moduleVariantName`), spawned co-orbital with the host in FLIGHT. | Only a vessel part in FLIGHT has run its modules' OnStart, which is where the real look is made. A bare `Instantiate(partPrefab)` NREs without a vessel; the editor runs editor-only logic. |
| D2 | Ghost side = ONE engine-owned "build and initialise" seam extracted from `GhostPlaybackEngine`'s load path (today `PopulateGhostInfoDictionaries`, `InitializeInventoryPlacementVisibility`, `RefreshCompoundPartVisibility`, `RecalculateCameraPivot`, then activation), called by both the engine and the sweep; the throwaway recording carries the real vessel's start seed events (`PartStateSeeder.SeedPartStates` / `EmitSeedEvents`) replayed at t0. | The tool must not drift from what playback shows. FGV calls only part of that sequence and has no seed events. |
| D3 | Ghost CO-LOCATED with the real part (parented to `rootPart.transform`, zero offset, identity rotation). Each side is rendered with the other side's renderers `forceRenderingOff`. | Same pixels, same light, same perspective: directly differenceable. `forceRenderingOff` hides without touching module-owned `enabled` flags and without re-firing OnEnable on particle emitters (no `SetActive` toggling). |
| D4 | A dedicated capture `Camera` rendering into a `RenderTexture` with `Camera.Render()`. One view's hide / render / read / restore runs synchronously in one call. | No frame passes between the sides; fixed resolution; no UI, sky or other vessels. Known-working in KSP (stock part thumbnails, Kronal Vessel Viewer). |
| D5 | A neutral light rig: every enabled scene `Light` (sun, Kopernicus stars, host part lights) is switched off for the render and restored; a camera-relative key light and a fixed fill light (no shadows) plus a fixed ambient replace them; `QualitySettings.pixelLightCount` pinned for the capture. | The bottom of a part in orbit is otherwise black and the sun moves per case. Both sides get the same light. |
| D6 | Coverage by double clear: each side is rendered on black and on white; the per-pixel difference gives alpha. | Exact silhouette with no replacement shader and no trust in KSP shaders' alpha output; RGBA output on any backdrop. |
| D7 | The sidecar carries RAW measurements (masks summarised, blobs, colour statistics, per-renderer structure); verdicts are computed in Python (`harness/lib/partparity.py`). | Thresholds re-pin with no flight; the C# side stays a measuring instrument. |
| D8 | Per case: one atlas PNG (rows real / ghost, columns views, RGBA), one small thumbnail PNG, one `.part.json`. No diff image stored: the viewer differences with CSS `mix-blend-mode: difference`. | About 1500 cases per install: three files per case; CSS blend works on `file://`, where canvas pixel reads are blocked. |
| D9 | A CHUNKED, RESUMABLE seam verb `PartParitySweep` (each step bounded to 480 s, well under the 540 s deferred-step cap), writing to `<instance>/parsek-partparity/<label>/`, moved into `results/<runId>_partparity/` by its own harvest leg. | `hlib.MAX_DEFERRED_STEP_BUDGET_SECONDS = 540` (S8); the `Screenshots/` harvest is top-level only with a 1024-file cap. |
| D10 | Report-only first; regression gating later against a committed per-profile metrics baseline plus an accepted-differences list. | House rule: arm a gate only after a reading run. Images are never committed. |

## 3. Cases

A CASE is one (install profile, part, skin, state, context) tuple. Phase 1 has state = `stowed` (the
spawn default, reached when no real-part `Animation` is still playing) and context = `alone`.

### 3.1 Part enumeration

`PartLoader.LoadedPartsList`, every `AvailablePart` with a `partPrefab`. Excluded, each with a
reason recorded in the manifest (never silently dropped): `kerbalEVA*`, `flag`, space objects
(`PotatoRoid`, `PotatoComet`).

Hidden parts (`TechHidden`, `category = none`, ReStock's deprecated parts) are captured and tagged
`hidden` (an old craft can still fly them). Launch clamps, ground-anchored parts, wheels and landing
legs (which extend or settle in free fall) are captured and tagged `context-dependent`.

Per part the manifest records: `name`, `title`, `partUrl`, `modelUrls` (every MODEL node's `model` /
`mesh` from `partConfig`), `source` (first path segment of the first model url: `Squad`,
`SquadExpansion`, `ReStock`, `ReStockPlus`, ...; ReStock keeps stock part names but swaps models, so
the cfg url alone would attribute a ReStock model to Squad), `category`, the prefab's module names,
and tags:

- `restock-module:<name>` for the ten `ModuleRestock*` modules (the ghost runs none of them).
- `b9partswitch`, `texture-switch:<module>` for known switchers.
- `fx-module:<name>` for `ModuleWaterfallFX` and other FX-only modules (relevant from phase 7).
- `unknown-module:<name>` for any module outside stock, the lists above, and a committed list of
  visually inert modules (`MechJebCore`, kRPC modules, ...), so pods and engines are not all tagged.

### 3.2 Skins

Every skin a part offers is its own case:

- Stock `ModulePartVariants`: every `VARIANT` of the prefab module, base first. Authored on the
  real side in both places KSP reads: the module node's `selectedVariant` (what
  `ModulePartVariants.OnLoad` reads at a flight load) and the PART-level `moduleVariantName` (what
  `ProtoPartSnapshot` parses and what Parsek's own resolver falls back to,
  `GhostVisualBuilder.Variants.cs` `ResolveVariantNameFromSnapshot`). FGV config 4 already sets both
  (`FairingGhostVisualInGameTests.cs` 975, 1040-1041).
- `ModuleB9PartSwitch`: every `SUBTYPE` of each B9 module (selected by `moduleID` when a part has
  several), one module at a time from the base combination (not the cross product). The persisted
  field is `currentSubtypeName` (present in the installed `B9PartSwitch.dll`; `currentSubtype` is
  not). The live subtype is read by reflection, no compile-time reference. In the modded-compat
  install B9 is used only by the Waterfall engine configs (Bobcat, Cheetah, Vernier, Vector), whose
  subtypes switch plumes: those cases are kept but carry little in the stowed state, and become real
  in phase 7.
- Other switchers: enumerated only through a known adapter; otherwise tagged and captured in the
  default skin.

Skin proof per case, all recorded: `requested` (authored), `real` (the live module after OnStart:
`ModulePartVariants.SelectedVariant.Name`, B9's current subtype), `snapshot` (what `TryBackupSnapshot`
wrote), `ghost` (what the builder applied, through a NEW mandatory read accessor on
`GhostBuildResult`; the builder's `LogPartVariantResolution` line is rate-limited per part name and
fires only for GAMEOBJECTS rules, so it cannot serve). requested != real is a SETUP fault
(`skin-not-applied`), reported apart. real != ghost is a ghost defect even when the pictures look
alike.

Case-count estimate (measured on the first run): stock-minimal about 400 parts / 900 cases;
modded-compat about 700 parts / 1500 cases.

### 3.3 Later axes (designed in, built later)

`state` (deployed, chute semi / full, jettisoned, retracted, broken), `context` (attach-node
dependent looks), `anim` (animation strips), `fx` (plumes, RCS, lights, heat). Section 9.

## 4. In-game capture

### 4.1 Host and waves

FLIGHT, active vessel in a stable orbit: fixture `eva2-lko-crewed` (FGV's, the Kerbal X Probe at
about 100 km), with FGV's `LoadGame` / `WarpToUT` setup and `verboseLogging = true`. Auto-record
settings off (`SetSetting autoRecordOnLaunch=false` and siblings) so no spawned vessel is recorded.
Nothing is staged on the host, nothing is saved.

Stock orbit vessel ranges unpack at 200 m and pack at 350 m (decompiled `VesselRanges`: orbit
`Situation(2250, 2500, 350, 200)`), so every case of a wave sits inside +-180 m along-track of the
host: slots alternate both signs (+-60, +-100, +-140, +-180 m; FGV uses +-70 / +-140), a large part
takes two slots, and the wave size adapts to part bounds (up to 8). No vessel-range override.
Spawns use `TrajectoryMath.TryComputeCoOrbitalMeanAnomalyShift` (FGV `SpawnCoOrbital`), flight ids
from `ShipConstruction.GetUniqueFlightID`, and vessel names `PartParity <caseId>` (never the
`Part Showcase -` prefix, which switches on showcase-only ghost tweaks in `GhostVisualBuilder`).

Per wave: spawn all; wait until each is `loaded && !packed` (20 s per case, else `spawn-timeout`);
settle until no `Animation` on the real part is playing and 0.5 s has passed (cap 5 s, else
`still-animating`, captured anyway and tagged); capture each case; destroy every ghost and its
cloned materials; `Die()` every spawned vessel and wait until gone. Every K = 10 waves,
`Resources.UnloadUnusedAssets()`, and one memory line in the wave log.

### 4.2 One case, in order

1. Collect the REAL renderer list from the part's model subtree and its framing box (each renderer's
   local bounds corners into part space; SkinnedMeshRenderers from their world `bounds`, since
   their local bounds are often stale) BEFORE any ghost exists. Record the real per-renderer
   structure (4.6). The framing box takes `MeshRenderer` / `SkinnedMeshRenderer` on the part's own
   model layers only: on the modded install a ReStock LV-T45 carries 18 extra mesh renderers (most
   likely Waterfall plume meshes, invisible at zero throttle; not yet enumerated) that blew the
   real box to 10 m against a 2 m part
   (Appendix B).
2. Snapshot: `TryBackupSnapshot(real)`; seed events: `PartStateSeeder.SeedPartStates` on the real
   vessel and `EmitSeedEvents` into the throwaway recording's `PartEvents` (D2).
3. Build the ghost through the D2 seam, replay the t0 prefix of the seed events, disable ghost
   colliders, freeze rigidbodies (FGV). Parent the ghost root to `real.rootPart.transform` with zero
   offset / identity rotation / unit scale. Record the world offset between `ghost_part_<pid>` and
   the real part transform (expected 0; else `ghost-root-offset`).
4. Ghost framing box from the ghost's own renderers; the SHARED box is the union of the two (one box
   for both sides, nothing re-centred). `ghost-outside-real-bounds` when the ghost box exceeds the
   real one by more than 2 %.
5. For each view (4.3): capture (4.4).
6. Write the thumbnail, the atlas, and LAST the sidecar, atomically (`FileIOUtils.SafeWriteBytes`):
   a case with a complete sidecar is complete (resume rule, 7). One `[PartParity]` Verbose line per
   case.

### 4.3 Views

Fixed in the part's local frame (KSP part space: +Y is the stack axis).

| View | Camera position (part local) | Camera up |
| --- | --- | --- |
| `side-a` | -Z | +Y |
| `side-b` | +X | +Y |
| `top` | +Y | +Z |
| `bottom` | -Y | -Z |
| `side-c`, `side-d` (opt-in, `views=all`) | +Z, -X | +Y |
| `hero` (opt-in) | (+1, +0.6, -1) normalised | +Y |

The four required views are orthographic (`orthographicSize` from the shared box plus a 10 %
margin) so silhouettes compare pixel for pixel; `hero` is perspective 30 deg. Clip planes come from
the SHARED box with a margin, so neither a ghost-only mesh nor anything outside the cases can be cut
or enter. Coverage touching the tile border is flagged `clipped-at-border`.

Limits stated in the viewer: `side-a` / `side-b` are 90 deg apart, so the +Z and -X faces are only
seen with `views=all`; a rotationally symmetric part cannot prove its yaw from any view.

### 4.4 The capture primitive

One synchronous method per (case, view), run in a coroutine step after `WaitForEndOfFrame`:

```
pose capture camera (pos, rot, ortho size, clip planes, culling mask)
light rig on (scene lights off, saved; key + fill on; ambient and pixelLightCount pinned)
for side in (real, ghost):
    forceRenderingOff = true on the OTHER side's renderers
    for clear in (black, white):
        camera.backgroundColor = clear; camera.Render() into the MSAA target
        Graphics.Blit to a non-MSAA target (forces the resolve); ReadPixels; GetRawTextureData
    forceRenderingOff restored
light rig off (every saved light restored), camera disabled
compose RGBA per side (D6); measure (6); blit into the atlas
```

Culling mask: the union of the layers of both sides' renderers (logged), never "everything". The
ghost does not copy source layers; a ghost layer set that differs from the real one is recorded
`ghost-layer-differs`.

Render target `RenderTexture(tile, tile, 24, ARGB32)`, MSAA 4, `tile` default 512; one target pair
reused for the whole sweep; every temporary `Texture2D` destroyed after use.

Textures: the profiles pin `TEXTURE_QUALITY = 3` and `QUALITY_PRESET = 0`. Both sides share the
textures, so parity is unaffected, but the pictures are blurry. The sweep sets
`QualitySettings.masterTextureLimit = 0` in process for its duration and restores it (no
`settings.cfg` key: the closed `kspSettings` set keeps render keys out of reach on purpose,
`hlib.py` near 9474). P0 measured that the runtime change does NOT take effect: both installs run
`masterTextureLimit = 3`, and setting 0 left `Texture.currentTextureMemory` unchanged and the
pictures at 1/8 resolution (Appendix B). Parity is unaffected (both sides share the textures), so
the gated sweep runs at the pinned quality; full-resolution pictures for the owner's eyes need an
install-level `TEXTURE_QUALITY = 0`, which is a P2 decision (a lane-scoped key in the closed set, or
a parity-only profile). Full-resolution ReStock textures raise VRAM above the measured ~1 GB per
copy, so such a lane runs on one slot until measured.

### 4.5 Control cases (self-test)

Every sweep starts with controls, recorded in the manifest and shown first in the viewer. Each has a
required outcome; a run whose controls do not behave is `INVALID(parity-controls)`, the mechanical
proof that the instrument sees what it claims to.

| Control | Subject | Required |
| --- | --- | --- |
| `ctl-empty` | both sides hidden | zero coverage in every view (nothing else is in frame) |
| `ctl-ghost-absent` | ghost not built | ghost side zero coverage (the hide of the real side works) |
| `ctl-real-vs-real` | `fuelTankSmallFlat`, real captured as both sides | identical masks and colours (determinism) |
| `ctl-ghost-scale90` | same tank, ghost scaled 0.9 | fails every view on shape |
| `ctl-ghost-rot90` | `mk1pod.v2` (hatch and ladder: no rotational symmetry), ghost rotated 90 deg about +Y | fails `side-a` / `side-b` on shape |
| `ctl-ghost-flip` | `mk1pod.v2`, ghost rotated 180 deg about +X | fails every view |
| `ctl-ghost-missing-mesh` | a multi-renderer stock part, one small ghost renderer forced off | fails on the local-defect measures (6) while whole-image IoU stays above 0.99 |
| `ctl-wrong-skin` | a stock part with a colour-only variant (chosen at run time, FGV's `ChooseColourVariant`), ghost built from a snapshot naming another variant | fails on colour with IoU near 1 |

### 4.6 Structural diff

Per side, per renderer: transform path relative to the part (with `(Clone)` stripped and the
ghost's mirror prefix mapped back), mesh name, vertex count, material / shader / main-texture names,
rendered or not, and the part-local matrix. The sidecar pairs them by path and lists: missing on
the ghost, extra on the ghost, transform differs (position > 1 mm, rotation > 0.5 deg, scale > 1 %),
material / shader / texture differs. This names the missing or extra mesh directly, is independent
of lighting, and sees differences the four views cannot (an inner mesh, a mesh behind another).

## 5. Files and contract

### 5.1 Location, harvest, archive

`<KSP root>/parsek-partparity/<label>/`. The lane's first step clears the label directory
(`op=reset`), so no earlier run's files mix in; later steps only resume.

Per sweep directory:

- `manifest.json` - schema `parsek-part-parity/1`: header (profile, `GameData` mod list, DLL hash,
  tile, views, settings), controls, every case with its raw summary, exclusions with reasons,
  progress (`complete`, `cases`, `crashed`).
- `<caseId>.png` - the atlas; `<caseId>.thumb.png` - 2 x 128 px `side-a` pair for the grid;
  `<caseId>.part.json` - the sidecar.
- `inflight.txt` - the case being captured (5.3).

Harvest: a new `run.py` leg MOVES (same-volume rename, while the instance lock is still held) the
label directory into `results/<runId>_partparity/` instead of copying 1 GB into the verifier's
private copy; the verifier reads it in place. It has its own retention pass (the `_shots` pass only
prunes names ending `_shots`, `run.py` near 5327). Only `manifest.json` and the sidecars go to the
results archive, under `<runId>/partparity/` (the archive already owns `<runId>/manifest.json`).

### 5.2 Case sidecar (`parsek-part-parity-case/1`)

```
{
  "schema": "parsek-part-parity-case/1",
  "caseId": "...", "part": "...", "title": "...", "source": "ReStock",
  "partUrl": "...", "modelUrls": ["..."], "category": "...", "tags": ["..."],
  "skin": {"module": "ModulePartVariants", "moduleId": "...", "requested": "...", "real": "...", "snapshot": "...", "ghost": "..."},
  "state": "stowed", "context": "alone",
  "atlas": {"file": "...", "thumb": "...", "tile": 512, "rows": ["real", "ghost"], "cols": ["side-a", "side-b", "top", "bottom"]},
  "framing": {"real": {"center": [..], "extents": [..]}, "ghost": {..}, "shared": {..}, "orthoSize": ..},
  "ghostRootOffsetM": .., "layers": {"real": [..], "ghost": [..]}, "flags": ["..."],
  "views": [ {"view": "side-a",
              "realPx": .., "ghostPx": .., "andPx": .., "orPx": ..,
              "realOnlyTolPx": .., "ghostOnlyTolPx": ..,          // mismatch with no other-side pixel within 1 px
              "realOnlyBlobPx": .., "ghostOnlyBlobPx": ..,        // largest connected component, tolerance applied
              "bbox": {"real": [x,y,w,h], "ghost": [..]}, "centroid": {"real": [x,y], "ghost": [..]},
              "touchesBorder": {"real": false, "ghost": false},
              "colorOverPx": .., "colorBlobPx": .., "colorP99": .., "colorBlockMax": ..   // over the overlap; T and the 16x16 block grid in the header
            } ],
  "structure": {"pairs": .., "missingOnGhost": [..], "extraOnGhost": [..], "transformDiffers": [..], "materialDiffers": [..]},
  "setup": {"status": "ok|skin-not-applied|spawn-timeout|still-animating|died-settling|crashed", "detail": "..."},
  "log": ["the Parsek lines emitted while this case was built, via ParsekLog.TestObserverForTesting"]
}
```

Additive keys only after the first flight; a renamed key bumps the schema. As in the other two
systems, the C# `SchemaId` / key table and hlib's reader keys are pinned by a source-sync cell, and
`.part.json` is NOT added to `ARTIFACT_SHOTS_SUFFIXES` (it never lands in `Screenshots/`).

`caseId`: the `CaptureScreenshot` label rule (`[A-Za-z0-9._-]`, max 96), as
`<part>--<skin>[--<state>][--<context>]` with any other character mapped to `-`, lower-cased, plus an
8-hex hash of the unmapped tuple, so two skins never collide (Windows is case-insensitive). The full
names live in the sidecar.

### 5.3 Resume and crashes

A step processes cases in the stable order until its 480 s time box ends, then answers OK with
`done=false next=<index>`. The next step skips every case with a complete sidecar. Before a case
starts, its id is written to `inflight.txt`; if KSP crashes on a bad modded part, the next boot's
sweep finds the stale `inflight.txt`, records that case `crashed` and skips it. So a retried
attempt never restarts from zero, and one bad part cannot stop a sweep.

## 6. Measures and verdicts

The C# `PartParityMetrics` (pure, `internal static`, over `byte[]`) measures; Python decides.

Shape, per view: coverage masks at alpha >= 0.5; a real-only pixel counts only when no ghost pixel
lies within 1 px (and the mirror for ghost-only), so a 1 px MSAA edge on a thin antenna is not a
defect; the largest connected real-only and ghost-only blob (4-connected) in px and as % of the
shared box's projected area; bboxes, centroids, border touch. Whole-image IoU is kept for sorting
only: a missing skirt of 400 px on a 150 000 px tank is IoU 0.997.

Colour, over the overlap after alpha premultiply: per-pixel RGB distance; the count over T (default
24), its largest blob, the p99, and the max mean over a 16 x 16 block grid, so a wrong decal or a
dark patch is not diluted by the rest of the part.

Verdicts (`harness/lib/partparity.py`, constants in one place, starting guesses re-pinned after the
first reading run against the controls and a hand-checked sample): `fail` when either largest
shape blob exceeds 0.2 % of the box area or 64 px, or the largest colour blob exceeds 64 px, or the
structural diff lists a missing / extra rendered mesh; `warn` at a quarter of those; else `pass`.
Case verdict = worst view; `error` for a setup fault. The verifier also recomputes the shape
measures in Python from the atlas for the controls and a random 2 % sample (the `gui_mirror.py`
PNG codec), as an independent check of the C# instrument.

## 7. The seam verb

`PartParitySweep label=<label> [op=reset|run] [filter=<glob>] [source=<csv>] [skins=default|all]
[views=required|all|<csv>] [tile=256..1024] [shard=<k>/<n>] [limit=<n>] [controls=true|false]
[maxSeconds=<60..480>]`

- Scene `RequiresFlight` with an orbiting active vessel (refusals for a landed or suborbital host).
- In `DEFERRED_SEAM_VERBS`; the spec's step budget is at most 540 s (S8). A step answers OK
  `label done=true|false next= cases= complete= crashed= setupFaults= controls=ok|bad|pending wall=`.
  A spec lists enough `op=run` steps for the measured case count; a step after completion answers
  `done=true` at once.
- `shard=k/n` takes every case whose stable index mod n == k-1. The parallel runner dispatches
  distinct lanes, so a shard is its own committed spec (`PP-1a`, `PP-1b`, ...), added only after the
  single-slot runtime is measured.
- Role: not inert (it spawns and removes vessels); the lane runs on a staged throwaway save and
  quits without saving.
- Wiring: the standard additive-verb list (`TestCommandVerbs.ImplementedVerbs`, the dispatcher
  scene table, the executor method, the addon case and completion routing, hlib
  `IMPLEMENTED_SEAM_VERBS` / `SEAM_VERB_TAIL_ROLE` / validate hook / reason tables, their source-sync
  cells). Pure half `TestCommandPartParitySweep.cs`; applier `ParsekTestCommandAddon.PartParitySweep.cs`.

A Ctrl+Shift+T in-game category `PartParity` (single-run, `AllowBatchExecution = false`) drives the
same engine with a small default filter for dev-install runs; adding it re-runs
`harness/tools/lane_catalog.py` in the same commit.

## 8. The viewer (web app)

`harness/tools/part_parity_view.py <dir> [<dir> ...] [--baseline <dir>] [--out <dir>] [--gif]`,
stdlib only. Inputs are sweep directories (shards, profiles). Output is a static site: `index.html`
+ `data.js` (all sidecars as one JS literal, because `file://` cannot `fetch` JSON) + the images
referenced in place (or copied with `--out`, e.g. to `<umbrella>/logs/partparity/<label>/`).
Vanilla JS, no CDN, offline.

Grid page: one card per case from the thumbnail (real and ghost side by side), title, name, source,
skin, verdict chip, worst blob, tags. Filters: profile, source, category, verdict, setup status,
tag, has skins; search; sort by worst blob, colour, IoU, name. Lazy-loaded thumbnails, so 1500 cards
stay light. Regression mode (`--baseline`): moved cases marked and filterable.

Detail page (`#case=<id>`, j / k to step): one row per view with real | ghost | difference (the two
tiles stacked with `mix-blend-mode: difference`) | blink / slider overlay; backdrop selector
(checker / black / white / grey); zoom to 1:1; the measures; the structural diff table; the skin
proof; tags, model urls, modules, the captured log lines, the accepted-difference note; a link to the
same part in another loaded profile (stock vs ReStock).

Images are not published as an Artifact (a sweep is hundreds of MB; 16 MB page limit).

## 9. Extensions

### 9.1 States and contexts

A state case drives the stock action on the real part (the `PartAction` seam's module-event path:
deploy, extend, arm-and-deploy chute, jettison, decouple), waits for the module's done signal, then
seeds, snapshots and builds as in 4.2, so both the snapshot baseline (`SnapshotPartBaseline` /
`ApplySnapshotBaselines`) and the seed-event path are tested. The Mk16-XL canopy (semi, full) lives
here. Context cases author small assemblies on named attach nodes (FGV's `AuthorPartNode`) and frame
on the subject part; FGV's six configurations become context cases.

### 9.2 Animation strips and GIFs

For parts that move (antennas, solar panels, radiators, gear, cargo bays, ladders, robotics): K
frames (default 12) at the same NORMALISED time t in [0, 1] on both sides instead of racing two
real-time animations. Real side: the module's `AnimationState.normalizedTime = t`, `speed = 0`,
`Animation.Sample()`. Ghost side: the same t through the ghost's own deploy-pose application, so an
animation the ghost cannot follow shows as frames that drift apart. Robotics: the servo target swept
over its range in K steps, the ghost driven through its robotic playback with the same values.
The atlas gains a frame axis; per-frame measures give a curve. The viewer plays real and ghost in
lock-step with a scrubber. GIF / APNG files are written by `part_parity_view.py --gif` (stdlib, the
`gui_mirror.py` PNG codec plus a small LZW / palette writer), not in the browser, where `file://`
blocks the pixel reads an encoder needs.

### 9.3 Plumes and other FX

Real engine: stage / activate, throttle > 0, `CheatOptions.InfinitePropellant` set for the capture
and restored. Thrust moves the vessel; the ghost is parented to it and the capture is synchronous.
Ghost: `GhostPlaybackLogic.SetEngineEmission(state, event, power)` on the active ghost (an inactive
hierarchy makes `ParticleSystem.Play` a silent no-op). Particles are stochastic: compare coverage
accumulated over about 1 s of frames plus the existing `[FxFingerprint]` lines, never single frames.
On Waterfall installs the real plume is `ModuleWaterfallFX` and the ghost falls back to pristine
stock / ReStock particles, so those cases are `reference-only` (shown, measured, never gated). The
B9 plume subtypes of 3.2 become meaningful here. Lights, heat glow (`_TemperatureColor` / rim via
MaterialPropertyBlock, which the ghost does not copy) and RCS follow the same pattern.

## 10. Harness lanes

- `PP-1-part-parity-stock` on `stock-minimal`, `PP-2-part-parity-modded` on `modded-compat`, tier
  `operator`, one slot. Fixture `eva2-lko-crewed`. Steps: FGV's setup, settings off,
  `PartParitySweep op=reset`, N x `op=run skins=all controls=true`, quit without saving.
- Phase-1 gating: every step answered OK, the controls behaved, the sweep reached `done=true`, every
  non-excluded case has a complete sidecar or a recorded setup fault. Parity verdicts are report-only.
- `[Parsek][ERROR]` stays forbidden by the log validator; an ERROR a modded part provokes is a finding
  to file, never a suppression.
- Verifier row `partParity` (`harness/lib/partparity.py`, pure): controls, completeness, verdicts,
  the Python recompute sample, and in phase 4 the baseline compare against
  `harness/partparity/baseline-<profile>.json` (raw measures only) and
  `harness/partparity/accepted.toml` (case / view / measure, reason, todo id). Once armed
  (`gating = true`, the saveparse arming rule), a newly failing case or a measure moved past its
  tolerance reds `PARSEK-FAIL(part-parity)`.
- Each ghost-visual defect the sweep finds is filed in `docs/dev/todo-and-known-bugs.md` with its
  case id; the fix removes the accepted entry and re-pins the baseline in the same commit.

## 11. Code layout

| File | Role |
| --- | --- |
| `Source/Parsek/TestCommands/TestCommandPartParitySweep.cs` | Pure: args, refusals, case order, sharding, case ids, resume decision, payload. |
| `Source/Parsek/TestCommands/PartParityMetrics.cs` | Pure: mask from two clears, tolerance mismatch, blobs, bbox, centroid, colour statistics. |
| `Source/Parsek/TestCommands/PartParityStructure.cs` | Pure: renderer record pairing and the structural diff. |
| `Source/Parsek/TestCommands/PartParityJson.cs` | Pure: manifest and sidecar model + invariant writer (`MapGeometryJson` pattern), `SchemaId`s. |
| `Source/Parsek/TestCommands/PartParityCaseAuthoring.cs` | Live: enumeration, skins, part node authoring, tags. |
| `Source/Parsek/TestCommands/PartParityCapture.cs` | Live: camera, light rig, hide / render / read / restore, atlas, PNG encode. |
| `Source/Parsek/TestCommands/ParsekTestCommandAddon.PartParitySweep.cs` | Thin applier and the wave coroutine on the seam addon. |
| `Source/Parsek/Ghost/GhostPlaybackEngine.*` | The extracted build-and-initialise seam (D2), the engine's own load path rewired through it (`il_compare.py --fold` proof of no behaviour change). |
| `Source/Parsek/Ghost/GhostVisualBuilder*` | The `GhostBuildResult` applied-skin accessor. |
| `Source/Parsek/InGameTests/GhostCoOrbitalRig.cs` | `SpawnCoOrbital`, part node authoring with skins, collider neutralising: moved out of `FairingGhostVisualInGameTests`, used by both, FGV unchanged. |
| `Source/Parsek/InGameTests/PartParityInGameTests.cs` | Ctrl+Shift+T entry. |
| `harness/lib/partparity.py` + tests | Pure manifest reader, verdicts, recompute, verifier row, baseline compare. |
| `harness/tools/part_parity_view.py` + tests | The viewer and the GIF / APNG writer. |

`Parsek.csproj` gains a thirteenth Unity module reference, `UnityEngine.ImageConversionModule.dll`
(present in `vl3c/ksp-refs`), for `EncodeToPNG`. No new mutable statics (sweep state lives on the
coroutine and in the label directory). Everything is automation-only and inert unless the seam is
armed or the in-game test is launched: no player-facing surface.

Logging: one `[PartParity]` Info line per wave (cases, setup faults, wall, memory) and one per step
summary; per-case Verbose lines; Warn for a setup fault.

## 12. Plan

| Phase | Content | Done when |
| --- | --- | --- |
| P0 (DONE 2026-11-21, Appendix B) | Capture spike on both harness installs, stock and ReStock / ReStock+ parts: `Camera.Render` into an MSAA target, resolve, double clear, ortho with KSP shaders, light rig, `pixelLightCount`, `masterTextureLimit`, `forceRenderingOff`. | Pictures read by eye; every unknown in 4.4 answered and written into this doc. |
| P1 | Pure halves + xUnit (measures on synthetic masks incl. shifted / rotated / missing-blob shapes, structural diff, sharding, case ids, resume decision, JSON writer, args); `GhostCoOrbitalRig` extraction; D2 seam extraction with the IL fold proof. | `dotnet test` green; FGV-1 flown green on the refactor. |
| P2 | Capture engine + verb + in-game test + files; controls; default skins; a 20-part dev run. | Controls behave; per-wave wall time measured; chunk size set from it. |
| P3 | All skins, tags, skin proof, structural diff; harvest leg; `partparity.py` row; lanes PP-1 / PP-2 report-only. | First full reading run on both installs; thresholds re-pinned; findings filed. |
| P4 | Viewer (grid, filters, detail, blend difference, backdrop, structure table, regression mode). | Owner reads the full stock + ReStock site. |
| P5 | Baseline + accepted list; gate armed after a second matching reading run. | A deliberately broken ghost (mutation) reds the armed lane. |
| P6 | States and contexts (9.1); FGV configurations migrated. | Chute and deployables covered. |
| P7 | Animation strips + GIF / APNG (9.2). | Antennas, panels, robotics scrubbed in lock-step. |
| P8 | Plumes and FX (9.3). | Engine plumes compared on stock; reference-only on Waterfall. |

## 13. Open questions

- Is a one-part vessel a fair reference for parts whose look depends on neighbours (shrouds,
  node-set plates, fairings)? Phase 1 compares what a lone part looks like on both sides; the context
  axis covers the rest. P0 shows the lone case is itself a real flight state: a bottom engine with
  nothing below hides its shroud in stock and the ghost draws it (Appendix B, finding 1).
- Does every variant apply at a flight spawn when authored in both keys, or do some apply only in the
  editor (`onEditorVariantApplied`)? The skin proof measures it; a part that fails needs another
  route.
- ~~Does `masterTextureLimit` take effect at runtime for already-loaded part textures (P0)?~~ No
  (Appendix B); the install-level route is the P2 decision in 4.4.

## Appendix A. Review disposition (rev 1 -> rev 2)

Two clean-context reviews of rev 1. "Both" = raised independently by both reviewers.

| Finding | By | Disposition |
| --- | --- | --- |
| 60 min deferred verb breaks the 540 s S8 cap (`hlib.py` 1154-1155, 8258) | Both | Fixed: chunked, resumable 480 s steps (D9, 5.3, 7). Verified in `hlib.py`. |
| Waves out to 2 km stay packed (orbit unpack 200 m, pack 350 m) | Both | Fixed: every case within +-180 m, both signs, adaptive wave size (4.1). |
| Framing from the real part clips ghost-only meshes; ghost renderers leak into the real list | Both | Fixed: real list and box taken before the ghost exists; shared union box; border flag (4.2, 4.3). |
| `ctl-ghost-rot90` on an axisymmetric tank cannot fail | Both | Fixed: asymmetric `mk1pod.v2`, plus flip, missing-mesh, wrong-skin, empty, ghost-absent controls (4.5). |
| D2 is not the production path (missing visibility initialisers, no seed events; the activation seam is a bare `SetActive`) | Both | Fixed: extracted engine-owned seam + seed events (D2, 4.2). Verified in `GhostPlaybackEngine.GhostVisuals.cs` 205-208, 574-589. |
| Whole-image IoU and mean colour dilute local defects; thin parts false-fail | Both | Fixed: 1 px tolerance, largest blobs, colour blob / p99 / block max; verdicts in Python (6, D7). |
| "Scoped ParsekLog sink" would empty KSP.log | Both | Fixed: `TestObserverForTesting` (5.2). Verified. |
| Variant key: author both module `selectedVariant` and PART `moduleVariantName` | Both | Fixed (3.2). The Opus claim that FGV config 4 set only the PART key is refuted: `FairingGhostVisualInGameTests.cs` 975 sets the module key too. |
| B9 field is `currentSubtypeName`, select by `moduleID`; B9 here only switches plumes | Opus | Fixed (3.2). Verified in the installed DLL. |
| Builder variant log line is rate-limited and incomplete | Opus | Fixed: mandatory `GhostBuildResult` accessor (3.2). |
| `textureQuality` settings key fights the closed `kspSettings` set; VRAM | Both | Fixed: in-process `masterTextureLimit`, P0 spike, one slot (4.4). |
| Lights: Kopernicus stars and host lights; `pixelLightCount` at preset 0; rig shadows | Both | Fixed: all scene lights off and restored, pinned count, no rig shadows (D5). |
| MSAA readback needs a resolve | Opus | Fixed: blit to a non-MSAA target (4.4). |
| `SetActive` toggling re-fires OnEnable | Both | Fixed: `forceRenderingOff` both sides (D3). |
| Sharding via parallel runner needs per-shard specs | Fable | Fixed: one committed spec per shard, later (7). |
| Resume undefined; crash on a bad part restarts from zero | Fable | Fixed: sidecar-last atomic write, `inflight.txt`, `crashed` (5.3). |
| Harvest: archive `manifest.json` collision, run scoping, 1.5 GB private copy, `_shots`-only retention | Both | Fixed: `op=reset`, same-volume move, own retention, archive subfolder (5.1). |
| In-browser GIF export impossible on `file://` | Opus | Fixed: GIF / APNG written by the viewer tool (9.2). |
| Case id `~`, collisions, case-insensitivity, 96 chars | Opus | Fixed (5.2). |
| Memory / material leaks over 1500 builds | Both | Fixed: destroy cloned materials and textures, `UnloadUnusedAssets` every 10 waves (4.1). |
| `unknown-module` noise (MechJeb, Waterfall, kRPC) | Opus | Fixed: inert-module list, `fx-module` tag (3.1). |
| Fixed settle catches start-up animations | Opus | Fixed: settle until no real `Animation` plays (4.1). |
| Lane omits FGV setup / `verboseLogging`; ERROR policy | Opus | Fixed (10). |
| Untested faces (+Z, -X), symmetric yaw | Fable | Fixed: opt-in `side-c` / `side-d`; limits stated in the viewer (4.3). |
| Structural per-renderer diff (suggestion) | Fable | Adopted (4.6). |
| Thumbnails for the grid (suggestion) | Opus | Adopted (D8). |
| Python recompute of C# measures (suggestion) | Both | Adopted for controls + 2 % sample (6). |
| Capture spike before pure code (suggestion) | Fable | Adopted as P0 (12). |
| csproj count, lane catalog, rig module folder | Both | Fixed (7, 11). |

## Appendix B. P0 capture spike results (2026-11-21)

Throwaway branch `part-parity-spike` (commit c403783d4, never merged): one in-game cell
(`PartParitySpikeInGameTests`) and two operator lanes on `eva2-lko-crewed`, provisioned from that
branch. Both PASS attempt 1: `PPS-0a-capture-spike-stock` on stock-minimal (`2026-11-21_1242`, 68 s
wall, 6 cases, 48 images) and `PPS-0b-capture-spike-modded` on modded-compat (`2026-11-21_1244`,
10 cases, 80 images; ReStock, ReStock+, Waterfall, B9PartSwitch installed). Per case: a one-part
vessel spawned 60 m along-track, the ghost built from its own `TryBackupSnapshot` and parented to
the real root at zero offset (no seed events), then four ortho views x two sides x two clears into a
512 px tile. Images and KSP.log in the runs' `_shots` folders.

What 4.4 assumed, and what was measured:

| Unknown | Result |
| --- | --- |
| `Camera.Render` inside KSP's camera stack | Works. A disabled camera of our own, `RenderingPath.Forward`, rendered on demand after `WaitForEndOfFrame`; the flight cameras are untouched. |
| MSAA target + resolve | Works on Direct3D11 (`supportsMultisampledTextures=1`): MSAA 4 target, `Graphics.Blit` to a non-MSAA target, `ReadPixels`. |
| Ortho with KSP shaders | Works: stock and ReStock part shaders render lit and textured in orthographic projection. |
| Double-clear matte | Clean: about 500-900 edge pixels per 512 px view, 0 inverted pixels in every view except 9 on one real-side Mk1 pod view (white variant), so the measures need a small noise floor. |
| Light rig | Works: 22-26 scene lights disabled and restored per view, key + fill directional, flat ambient pinned, `pixelLightCount` pinned. No exception in the capture window (the only exceptions are stock's `KnowledgeBase` shutdown noise after the summary). |
| `forceRenderingOff` hiding | Works: co-located sides separate cleanly, and the one real difference (finding 1) shows on one side only. |
| Determinism | Identical sides measure iou 1.0000 and mean colour delta 0.00-0.04 on every matching view, so the measures are not noise-bound. |
| Culling mask from renderer layers | Layers 0 and 1 (Default, TransparentFX); mask 0x1 or 0x3. Nothing outside the box entered a frame. |
| `masterTextureLimit = 0` at runtime | Not effective (4.4). |
| Throughput | About 2 s per case (spawn + 1.5 s settle); the 4 views x 2 sides x 2 clears take about 0.15 s. Settle, not capture, sizes the waves. |
| Skins | Every requested variant reached the snapshot (`moduleVariantName`) and the ghost: stock FL-T400 BlackAndWhite, Mk1 pod White; ReStock FL-T400 Dark, OKTO Gold, LV-T45 Boattail, ReStock+ nosecone Metal. All match pixel for pixel. |

Findings:

1. **Ghost draws the shroud of an engine with nothing on its bottom node.** Stock LV-T45 and ReStock
   LV-T45 (Size1): real side shows the bare engine, ghost side a solid shroud (side views iou
   0.40-0.54). Stock `ModuleJettison.SetupJettisonState` (decompiled, KSP 1.12.5) deactivates the
   jettison transform when the bottom node has no attached part, without setting `isJettisoned`;
   `GhostVisualBuilder` forces every jettison transform visible and relies on `ShroudJettisoned`
   events, and `PartStateSeeder.SeedJettisons` seeds only `isJettisoned = True`. So any recorded
   vessel whose bottom engine has nothing below plays back with a shroud the real craft never had.
   The ReStock Boattail variant carries no shroud and matches. Filed as todo
   GHOST-SHROUD-EMPTY-BOTTOM-NODE; fixed and live-proven on the spike lanes in PR #2737.
2. **Extra real-side renderers (most likely Waterfall plume meshes) inflate the real framing box.** ReStock LV-T45: 28 real mesh renderers
   against 10 on the ghost; the real box was 10 m, so the part filled a corner of the tile. Fixed in
   4.2 step 1 (frame from the part's model renderers only); P2 logs which renderers it dropped.
3. **Installs run at 1/8 texture resolution** (finding behind 4.4).

