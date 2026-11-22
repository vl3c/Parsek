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
reason recorded in the manifest (never silently dropped): `kerbalEVA*` (`eva-kerbal`), `flag`,
space objects (`PotatoRoid`, `PotatoComet`: `space-object`), and a part whose prefab carries a
GROUND-ONLY module (`PartParityCaseRules.GroundOnlyModules`, today `LaunchClamp`: reason
`ground-only`). A launch clamp only exists anchored to the ground: spawned in orbit it packs and
unloads about 0.2 s after it unpacks (P3 flight, `launchclamp1`), so the orbital sweep could only
ever record it `died-settling`. It needs a ground context, a later axis (9.1).

Hidden parts (`TechHidden`, `category = none`, ReStock's deprecated parts) are captured and tagged
`hidden` (an old craft can still fly them). Ground-anchored parts other than the clamps, wheels and
landing legs (which extend or settle in free fall) exist in orbit, so they are captured and tagged
`context-dependent`; the ground-only modules are a subset of `ContextDependentModules`. The prefab's module list includes each module's base types up to `PartModule`, so a mod's subclass of a ground-only module (ReStock's `ModuleRestockLaunchClamp : LaunchClamp`) is excluded too.

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

Under `skins=all` every skin a part offers is its own case (P3, `PartParitySkinRules`, pure over
the part's `partConfig`; `skins=default` is the base case of each part only, exactly as P2):

- **Base case** (skin ordinal 0): the P2 default case unchanged, same key and case id
  (`PartParityCaseRules.DefaultCaseKey`): a part with a live `ModulePartVariants` names its base
  variant (`prefab.baseVariant.Name`), any other part `default`. It authors the base variant in
  both keys and no B9 subtype.
- **Stock `ModulePartVariants`**: then every other `VARIANT` of the part's first
  `ModulePartVariants` MODULE node, in config order (the base matched case-insensitively and
  skipped; empty and duplicate names dropped; none when the prefab has no live variant module).
  Authored on the real side in both places KSP reads: the module node's `selectedVariant` (what
  `ModulePartVariants.OnLoad` reads at a flight load) and the PART-level `moduleVariantName` (what
  `ProtoPartSnapshot` parses and what Parsek's own resolver falls back to,
  `GhostVisualBuilder.Variants.cs` `ResolveVariantNameFromSnapshot`). FGV config 4 already sets both
  (`FairingGhostVisualInGameTests.cs` 975, 1040-1041). Case skin: the variant name.
- **`ModuleB9PartSwitch`**: then, per B9 MODULE node in prefab order, EVERY one of its `SUBTYPE`s,
  the default included (the base case authors no subtype, so the default's own case is the one
  that proves the authoring), with every other module of the part at its base: one module at a
  time, never the cross product. Case skin: `<moduleID>=<subtype>` (`b9=<subtype>` for an unnamed
  module; B9 removes every unnamed module of a part that has several, so only a lone module is
  unnamed). The case id maps `=` to `-` (`liquidenginelv-t91--plumeswitch-hypergoliccheetah--<hash>`);
  the hash keeps the unmapped tuple apart. Authored into the k-th `ModuleB9PartSwitch` MODULE
  node of the authored PART (the module nodes are in prefab order): `moduleID = <id>` (B9's
  `CustomPartModule.OnLoad` routes a node by it) and `currentSubtype = <subtype>`. The persisted key
  is `currentSubtype`, NOT `currentSubtypeName`: decompiled B9PartSwitch 2.21.0.4,
  `[NodeData(name = "currentSubtype", persistent = true)] public string CurrentSubtypeName`, so the
  key its `OnLoad` reads and its `OnSave` writes is `currentSubtype`, and `CurrentSubtypeName` is the
  C# property. The live subtype is read by reflection, no compile-time reference: a field or
  property named `currentSubtypeName` ignoring case (the installed DLL: property
  `CurrentSubtypeName`), else `CurrentSubtype`'s `Name`; the member read is logged. In the
  modded-compat install B9 is used only by the Waterfall engine configs (Bobcat, Cheetah, Vernier,
  Vector), each one `plumeSwitch` module whose subtypes switch plumes: those cases are kept but
  carry little in the stowed state, and become real in phase 7.
- Other switchers: enumerated only through a known adapter; otherwise tagged and captured in the
  default skin.

Skin proof per case, the sidecar's `skin` block, all recorded (`PartParitySkinProof`):
`module` / `moduleId` (the option's; a base case of a variant part names `ModulePartVariants`, of a
B9-only part its first B9 module with no request, of any other part nothing), `requested` (the
authored value), `real` (the live module after OnStart: `ModulePartVariants.SelectedVariant.Name`,
or the B9 module whose `moduleID` is the proof's by reflection), `snapshot` (what
`TryBackupSnapshot` wrote: the PART's `moduleVariantName`, or that B9 MODULE node's
`currentSubtype`), `ghost` (what the builder applied, through the mandatory read accessor
`GhostBuildResult.TryGetAppliedSkin(persistentId, out GhostAppliedSkin)`, keyed by the snapshot
PART's persistentId; the builder's `LogPartVariantResolution` line is rate-limited per part name and
fires only for GAMEOBJECTS rules, so it cannot serve). `AddPartVisuals` records, for every part with
a `ModulePartVariants` whose visuals it built, the `variantName` and `variantResolutionPath` it
resolved (`snapshot` / `runtime` / `base` / `first-fallback` / `base-implicit`), a read-only record
nothing in the build or playback reads (IL proof: insertions only, 12.P3 row). The builder applies
no B9 subtype (nothing in `Source/Parsek/Ghost/` reads B9), so a B9 proof's `ghost` is null and
the case is flagged `skin-not-modelled:b9`: a finding (the ghost of a B9 part always shows the
prefab's own look), not a setup fault.

requested != real (exact) is the SETUP fault `skin-not-applied`, reported apart. real != ghost
(case-insensitive, the way the builder matches a VARIANT node) is the ghost-defect flag
`skin-mismatch`, raised even when the pictures look alike; the flags are computed only for a case
that built a ghost. Every case writes one Verbose `skin proof` line (5.2).

Case-count estimate (measured on the first run): stock-minimal about 400 parts / 900 cases;
modded-compat about 700 parts / 1500 cases. Each step logs the counts it planned (`skin cases:`
line, 7).

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

A case vessel can die after it settled, while the cases before it in the wave capture (the
mirrored `largepropeller` variant exploded on unpack in P3, a negative-scale collider). The capture
rechecks the real vessel and its root part right before `Prepare` (pure
`PartParityHostIntegrity.IsCaseVesselLost`, the same rule the settle wait and the per-view check
read): a lost vessel is `died-settling` (detail `vessel lost before prepare`, one Verbose line),
never a prepare exception recorded `crashed`.

Host integrity (`PartParityHostIntegrity`, pure; built after the P3 flights, where a broken host
let PP-1 run on until its next step was refused `host-not-orbiting` with 125 cases never run, and
PP-2's exploded pod froze the clock so 496 later cases recorded `spawn-timeout` across three
steps). The label's first `op=run` step records the host in `host.txt` (persistent id, part count,
situation; Info `host baseline:`), and every later step compares against that FIRST host, so a host
damaged between two steps still trips. The guard checks at a step's start (later steps), before
each wave, every frame of the spawn and settle waits, and after each case's sidecar. Damage, in
decision order: `gone` (no live vessel with the baseline pid, or it is not the active vessel),
`parts` (fewer parts than the baseline), `situation` (no longer `ORBITING`). On the first trip the
sweep stops at once: `hostdamage.txt` (reason, detail) is written to the label directory, one Warn
`PartParity host damaged label= reason= detail=<what changed and where>: the sweep stops; <n>
case(s) left unrun ...`, the live wave's uncaptured cases are left UNRUN (no sidecar, not a setup
fault: their spawn or settle read the broken host), its `inflight.txt` is deleted (no process
died), the wave is torn down, and the step answers OK with `done=false hostDamaged=true
hostDamage=<reason>` (7). Every later `op=run` step of the label reads `hostdamage.txt` BEFORE the
host admission and answers the same at once (Info `host damage recorded by an earlier step`), so a
broken host never turns into a refused step or a run of setup faults. The manifest carries it as
`progress.hostDamaged` / `hostDamage` / `hostDamageDetail`; `op=reset` clears both files.

### 4.2 One case, in order

1. Collect the REAL renderer records (4.6) BEFORE any ghost exists, each with its part-local
   bounds (a `MeshRenderer`'s mesh bounds corners into part space; a `SkinnedMeshRenderer` from its
   world `bounds`, since its local bounds are often stale). Only rendered `MeshRenderer` /
   `SkinnedMeshRenderer` records frame. The real box is NOT the union of every real renderer: on the
   modded install a ReStock LV-T45 carries 18 extra mesh renderers (most likely Waterfall plume
   meshes, invisible at zero throttle) that blew the real box to 10 m against a 2 m part
   (Appendix B). Rule (`PartParityFramingRule`, applied once the ghost records exist, step 4):
   pair the two sides' records first (`PartParityStructure.Diff`); the reference box is the union
   of the rendered PAIRED real renderers (when none paired, the ghost box; when that is empty too,
   no real renderer is dropped); an UNPAIRED real renderer joins the real box only when its bounds
   lie inside the reference box grown to twice its extent about its centre (each half-extent at
   least the reference's largest, so a flat plate does not drop everything off its plane). Every
   other unpaired real renderer is dropped from framing only: it stays in the structural diff,
   the case's Verbose line names it with its size, and the sidecar carries one flag
   `framing-dropped:<key>=<sx>x<sy>x<sz>` (metres) per dropped renderer.
2. Snapshot: `TryBackupSnapshot(real)`; seed events: `PartStateSeeder.SeedPartStates` on the real
   vessel (the recorder's arguments: its engine / RCS caches, colour-changer lights seeded) and
   `EmitSeedEvents` at t0 into the throwaway recording's `PartEvents` before the build (D2; the
   rig's `TryBuildGhostFromLiveVessel` overload takes them).
3. Build the ghost through the D2 seam, replay the t0 prefix of the seed events the way the
   engine primes a loaded ghost (`PrimeLoadedGhostForPlaybackUT`, whose `ApplyFrameVisuals` runs
   under `HiddenPrimeVisualPolicy`): `GhostCoOrbitalRig.ApplySeedPrefix` calls the engine-owned
   `GhostPlaybackEngine.PrimeHiddenPartStateAtUT`, which resets `partEventIndex` and runs the two
   blocks `ApplyFrameVisuals` itself calls, extracted as statics: `ApplyPartStateForFrame`
   (`ApplyPartEvents`, `UpdateActiveRobotics`, `UpdateActiveDeployables`,
   `UpdateSynthesizedMotion`) and, under the policy's FX suppression, `StopVisualFxForFrame`
   (engine / RCS FX and emissions stopped, reentry FX reset, launch dust to zero, audio muted),
   then `UpdateAudioAtmosphere`. So no transient FX start, and no part state differs from a
   playback ghost's first frame. The engine path is unchanged: inlining both statics back at
   their call sites in `ApplyFrameVisuals` gives its old IL body exactly (ILSpy dumps, the
   multi-seam inline fold of P1's proof; every other `GhostPlaybackEngine` method byte-equal after
   normalization). A ghost whose recording carries no part events (FGV's overload passes none) is
   left exactly as built. Then disable ghost colliders, freeze rigidbodies (FGV). The emitted and
   applied counts are the sidecar flags `seed-events:<n>` / `seed-applied:<n>`. Parent the ghost root to `real.rootPart.transform` with zero
   offset / identity rotation / unit scale. Record the world offset between `ghost_part_<pid>` and
   the real part transform (expected 0; else `ghost-root-offset`).
4. Ghost framing box from the ghost's own rendered renderers; the real box by the rule of step 1;
   the SHARED box is the union of the two (one box for both sides, nothing re-centred).
   `ghost-outside-real-bounds` when the ghost box exceeds the real one by more than 2 % of the
   real box's largest dimension.
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
        Graphics.Blit to a non-MSAA target (forces the resolve); ReadPixels; GetPixels32 (rows flipped top-down)
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
| `ctl-ghost-missing-mesh` | `fuelTankSmallFlat` with a small cube planted on the REAL part's +X face (the ghost lacks it) | fails on the local-defect measures (6) while whole-image IoU stays above 0.99, and the structure lists a rendered mesh missing on the ghost |
| `ctl-wrong-skin` | a stock part with a colour-only variant (chosen at run time, FGV's `ChooseColourVariant`), ghost built from a snapshot naming another variant | fails on colour with IoU near 1 |

The missing-mesh control plants a DETERMINISTIC defect. Four flights on 2026-11-21 tried to hide a
small mesh of a stock part's ghost and showed that stock still parts carry no separable small
mesh: hiding the LV-T45's bell read IoU 0.68; an estimate over the shared box chose the animated
telescopic ladder, whose real and ghost already differed; the Mk1 pod's only other mesh is its flag
decal, which lies on the hull (0 / 331 / 283 / 0 px drawn alone, contribution 0); and measured
silhouette contributions over eight still parts found nothing between 100 px and 0.6 %: mk1pod body
147k / flag 0, landerCabinSmall base 144k / flag 0, probeCoreHex and probeCoreSphere one mesh each,
probeCoreOcto body 112k / tapas 8.7k, ladder1 three rungs at about 13.5k each, mk1-3pod flag 0 /
74 px, cupola flag 0. So the control adds the mesh to the REAL side instead:

- Subject `fuelTankSmallFlat` (the tank controls' part). After the spawn and settle, BEFORE the real
  renderer records and box are read, `PartParityCaseCapture.PlantCube` parents a
  `GameObject.CreatePrimitive(PrimitiveType.Cube)` named `PartParityPlantedCube` to the real part:
  its collider destroyed at once (`DestroyImmediate`, so it never joins the part's physics), its
  `sharedMaterial` the part's own first rendered material (it lights like the part, and nothing is
  cloned), the layer of that renderer, centred on the +X face of the real renderer box at the box
  centre, wholly outside the face (centre x = box max x + edge / 2).
- The edge (`PartParitySweepPlan.PlantedCubeEdge`, pure): the cube's `side-a` face covers 0.3 % of
  the real box's `side-a` projection (size x times size y), and is at least 12 px across at the
  ortho scale of the box grown by the cube (`PartParityCaptureMath.OrthoSize`).
- The ghost is built from the unchanged snapshot, so it lacks the cube: the real side has a small
  rendered mesh the ghost does not, exactly a ghost missing a mesh. The required outcome is
  unchanged (a view fails on a local measure, every covered view keeps IoU >= 0.99), and the judge
  also needs the structure to list at least one RENDERED real renderer missing on the ghost
  (`PartParitySweepPlan.MissingRendered`).
- The cube dies with the case: `DestroyCase` destroys it first, in the wave teardown and in the
  step's `Cleanup`, so no exit path leaves it behind.

Log lines: Info `case ctl-ghost-missing-mesh: planted cube edge=<m>m local=(<x>,<y>,<z>) box=<sx>x
<sy>x<sz> material=<name> layer=<n> tile=<px>` (or Warn `planted cube not made (...)`), Verbose
`case ctl-ghost-missing-mesh: planted cube destroyed`, and the control line `control
ctl-ghost-missing-mesh: outcome= subject=fuelTankSmallFlat setup= detail=`. The sidecar carries the
flag `control-planted:edge=<m>;local=<x>,<y>,<z>` and lists `PartParityPlantedCube` under
`structure.missingOnGhost`.

`still-animating` is never a setup fault for the judge (`TestCommandPartParitySweep.IsSetupFault`):
the views (and, for the missing-mesh control, its structure) decide every control's outcome, with
or without it.

### 4.6 Structural diff

Per side, per renderer: transform path relative to the part (with `(Clone)` stripped and the
ghost's mirror prefix mapped back), mesh name, vertex count, material / shader / main-texture names,
rendered or not, and the part-local matrix. The sidecar pairs them by path (records sharing a
path pair first on mesh and rendered state, then on mesh, then in hierarchy order) and lists: missing on
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
  progress (`complete`, `cases`, `completeCases`, `setupFaults`, `controls`, `crashed`, and the
  host guard's `hostDamaged`, `hostDamage` (`gone|parts|situation` or null), `hostDamageDetail`).
- `<caseId>.png` - the atlas; `<caseId>.thumb.png` - 2 x 128 px `side-a` pair for the grid;
  `<caseId>.part.json` - the sidecar.
- `inflight.txt` - the case(s) in flight, one id per line, plus a `#solo` line on a solo rerun
  (5.3).
- `isolate.txt` - the cases still owed a solo run, one id per line (5.3); absent when none.
- `host.txt` - the label's host baseline (`pid=`, `parts=`, `situation=`), written by its first
  `op=run` step (4.1).
- `hostdamage.txt` - the host damage record (`reason=`, `detail=`); once present every later run
  step stops at once (4.1). A torn record still stops the sweep (it reads as `gone`).

Harvest (built in P3, `run.py` `_harvest_part_parity`, pure decisions in `hlib`): right after the
drive returns (the lane's KSP has exited) and while the instance lock is still held, before the
pipelined post-quit copy or the serial verifier chain, the leg MOVES
`<instance>/parsek-partparity/<label>/` for every label the spec's `PartParitySweep` steps name
(`partparity.sweep_labels`: literal labels in the label alphabet, first-use order) into
`results/<runId>_partparity/<label>/`, instead of copying up to 1 GB into the verifier's private
copy; the verifier reads it in place.

- The move is `os.rename`; a rename that fails only because the two paths are on different volumes
  (`hlib.partparity_move_needs_copy`: errno EXDEV, Windows ERROR_NOT_SAME_DEVICE) falls back to
  copy then delete. Any other failure is recorded on its label (`result=failed`, the exception) and
  never retried by a copy; a label the instance does not hold is recorded `absent`
  (`hlib.plan_part_parity_harvest`). Either way the row reads the label as not harvested, so the
  lane reds INVALID(parity-incomplete). The record rides the result JSON at
  `verifiers.partParity.harvest`; the log line is Info `[PartParity] harvest run= label= result=
  files= bytes= wall=`.
- Its own retention pass (`hlib.select_partparity_dirs_to_prune`: the `_shots` rule, newest first,
  stop on the first budget trip, the current run's dir always kept, over `PARTPARITY_KEEP_DIRS = 6`
  and `PARTPARITY_MAX_TOTAL_BYTES = 8 GiB`) runs after every harvest, skipped in a `--slot` child
  like the shots pass, and once in `run.py --sweep-results`.
- Only `manifest.json` and the `*.part.json` sidecars go to the results archive
  (`hlib.select_partparity_archive_files` into `plan_results_archive`), under
  `<runId>/partparity/<label>/` (the archive already owns `<runId>/manifest.json`); the atlases,
  thumbnails and `partparity-verdicts.json` stay in `results/` (the verdicts are also summarised in
  the archived result JSON).

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
              "colorOverPx": .., "colorBlobPx": .., "colorP99": .., "colorBlockMax": ..   // over the overlap; T and the 16x16 block grid in "measure"
            } ],
  "structure": {"pairs": .., "missingOnGhost": [..], "extraOnGhost": [..], "transformDiffers": [..], "materialDiffers": [..], "meshDiffers": [..], "renderedDiffers": [..]},
  "setup": {"status": "ok|skin-not-applied|spawn-timeout|still-animating|died-settling|crashed", "detail": "..."},
  "log": ["the Parsek lines emitted while this case was built, via ParsekLog.TestObserverForTesting"]
}
```

The P1 writer (`PartParityJson`) is the exact key set: its key tables list every key in
order, and every key is always written (an unmeasured value is `null`, a NaN or infinity too).
Beyond the sketch above it adds a `measure` block (coverage alpha, tolerance, connectivity,
colour metric, T, grid), per view `width` / `height`, the raw `realOnlyPx` / `ghostOnlyPx`,
`realOnlyBlobPct` / `ghostOnlyBlobPct`, `colorOverlapPx` and the per-side `matte` counts, and in
`structure` the renderer counts plus `meshDiffers` (kind, mesh, vertex count) and
`renderedDiffers` (a renderer both sides have that draws on one side only: the P0 shroud
finding). Diff entries are objects keyed by part path (`#k` for the k-th further renderer of a
shared path); missing / extra entries carry `mesh` and `rendered`.

The `skin` block (3.2) has the same six keys under every skin kind; a B9 case names
`ModuleB9PartSwitch`, its `moduleID` and the subtype in `requested` / `real` / `snapshot`, with
`ghost` null. Its flags join `flags`: `skin-mismatch` (real != ghost) and `skin-not-modelled:b9`
(the builder applies no B9 subtype); `skin-not-applied` is the `setup.status`. A crash record
(5.3) of a skin case carries the option's `module`, `moduleId` and `requested`. The manifest's
`settings.skinsNote` says what the step's `skins=` covered.

Setup statuses: `spawn-timeout` (no vessel, or never loaded and unpacked in 20 s),
`still-animating` (captured anyway), `died-settling` (the vessel died or lost its root part while
settling, before `Prepare`, or between two views), `crashed` (a prepare that threw, or the stale
inflight rule of 5.3), `skin-not-applied` (3.2). A case a damaged host left unrun has NO sidecar
and no status (4.1).

Per case the KSP.log carries, under `[PartParity]`: for a B9 case one Verbose `case <id>: authored
B9 currentSubtype=<subtype> moduleID=<id> b9Index=<k> written=true|false` at spawn; for every case
that built a ghost one Verbose `case <id>: skin proof module= moduleId= requested= real= snapshot=
ghost= realMember= ghostPid= ghostRecord= ghostPath= flags=` (`realMember` names the live member
read, `ModulePartVariants.SelectedVariant` or `property CurrentSubtypeName`); the per-case
Verbose summary line (`skin=<requested>/<real> snapshotSkin= ghostSkin= skinModule=`); and a Warn
`setup fault skin-not-applied` when the request did not take.

Additive keys only after the first flight; a renamed key bumps the schema. As in the other two
systems, the C# `SchemaId` / key table and hlib's reader keys are pinned by a source-sync cell, and
`.part.json` is NOT added to `ARTIFACT_SHOTS_SUFFIXES` (it never lands in `Screenshots/`).

`caseId`: the `CaptureScreenshot` label rule (`[A-Za-z0-9._-]`, max 96), as
`<part>--<skin>[--<state>][--<context>]` with any other character mapped to `-`, lower-cased, plus an
8-hex hash of the unmapped tuple, so two skins never collide (Windows is case-insensitive). The full
names live in the sidecar.

### 5.3 Resume and crashes

A step processes cases in the stable order until its 480 s time box ends, then answers OK with
`done=false next=<index>`. The next step skips every case with a complete sidecar. So a retried
attempt never restarts from zero, and one bad part cannot stop a sweep.

`inflight.txt` (written atomically) names what is in flight. During a wave's spawn, unpack and
settle, when a bad modded part is most likely to kill KSP and every case of the wave is live, it
names EVERY case of the wave, one per line. During a capture it names only the case being
captured, and it is deleted after that case's sidecar is written. A wave that is a solo rerun adds
a `#solo` line (outside the case-id alphabet). A one-line file of the first format reads as
before. One found at step start means a previous process died with those cases in flight; the
rule (pure: `TestCommandPartParitySweep.DecideInflight` / `MergeIsolate`):

- Exactly one name: that case is recorded `crashed` (a crashed sidecar) and skipped. This covers a
  capture, a single-case wave and the solo rerun of an isolated case, so a second crash of a solo
  rerun blames the culprit.
- Several names: no case is recorded crashed. Each is owed a solo run: it joins `isolate.txt` and
  the queue runs every case owed a solo run first, one per wave (a solo case never joins a normal
  wave, `PartParitySweepPlan.WaveCandidateCount`), so the next crash names one case. A case leaves
  `isolate.txt` when its sidecar is written (including its crash record). The list lives in the
  label directory, so the isolation survives any number of further KSP restarts and time-box ends.
- A named case with a complete sidecar wins over both: the process died between its sidecar write
  and the inflight delete, so it finished.

The rule runs over the WHOLE install's case ids, not only the step's selection: the crash record
and the isolation list are written BEFORE `inflight.txt` is deleted, so a stale case outside the
next step's limit, filter, shard or `controls=false` keeps its record (a write that throws fails
the step and leaves `inflight.txt` for the next one). Lines: Warn `case <id>: setup fault crashed
(recorded from the stale inflight.txt, solo=<bool>, skipped)`, Warn `stale inflight.txt names <n>
cases (...): ... re-queued to run alone`, Info `stale inflight.txt resolved: names= solo= crashed=
isolated= alreadyComplete=`, the step-start line's `staleInflight=` and `isolate=<n>`, Info `queue:
<n> case(s) owed a solo run go first`, the wave line's `solo=`, Info `case <id>: solo rerun
finished`.

A step stopped by the seam's 540 s timeout (7) is the in-process cousin of a crash: its cleanup
runs, but `inflight.txt` stays, so the next step reads the case(s) it names under the same rule.

## 6. Measures and verdicts

The C# `PartParityMetrics` (pure, `internal static`, over `byte[]`) measures; Python decides.

Shape, per view: coverage masks at alpha >= 0.5; a real-only pixel counts only when no ghost pixel
lies within 1 px (and the mirror for ghost-only), so a 1 px MSAA edge on a thin antenna is not a
defect; the largest connected real-only and ghost-only blob (4-connected) in px and as % of the
shared box's projected area; bboxes, centroids, border touch. Whole-image IoU is kept for sorting
only: a missing skirt of 400 px on a 150 000 px tank is IoU 0.997.

Colour, over the overlap after alpha premultiply: per-pixel RGB distance; the count over T (default
24), its largest blob, the p99, and the max mean over a 16 x 16 block grid, so a wrong decal or a
dark patch is not diluted by the rest of the part. As built (P1): the distance is Euclidean over
premultiplied 0..255 RGB (one channel off by d reads d); the p99 is nearest-rank; the grid is 16 x
16 CELLS whatever the tile, and a cell counts only when the overlap fills a quarter of it; the
blob of over-T pixels is 4-connected like the shape blobs. Every colour measure reads the overlap
INTERIOR only (a pixel covered on both sides across its whole 8-neighbourhood): antialiased edge
pixels blend with the clear colour, so a sub-pixel silhouette offset would otherwise read as a
full-edge colour blob (review of P1); the shape measures own the edge, and a part thinner than 3 px
has no colour measure.

Verdicts (`harness/lib/partparity.py`, built in P3; every threshold is a constant at the top of
the module, starting guesses re-pinned after the first reading run against the controls and a
hand-checked sample, a re-pin needs no flight):

| Level | Rule |
| --- | --- |
| view `fail` | either largest shape blob (real-only or ghost-only, tolerance applied) exceeds 0.2 % of the projected box area (`SHAPE_BLOB_FAIL_PCT`) or 64 px (`SHAPE_BLOB_FAIL_PX`), or the largest colour blob exceeds 256 px (`COLOR_BLOB_FAIL_PX`, re-pinned from 64 after the 2026-11-21 reading runs: one-pixel texture-seam lines on identical parts reach 68-200 px, every diagnosed colour defect was 2000 px or more); "exceeds" is strict, and a percentage the C# side wrote as null is not judged |
| view `warn` | the same lines at a quarter (`WARN_FRACTION`) |
| case `fail` | the worst view, raised to `fail` by a RENDERED entry in `structure.missingOnGhost` / `extraOnGhost` that is not non-visual (each entry's `nonVisual`: `depth-mask` for a depth-only shader, `fx` for a `Waterfall/` shader, both left out of the ghost by design; a sidecar without the key falls back to its path: a `Waterfall` segment or a leaf name containing `mask`), A case tagged `context-dependent` (wheels, suspension, legs, ground parts) caps a picture `fail` at `warn` with `context-pose`, since its orbital pose (suspension at full droop) is not the landed pose the ghost keeps by design (owner ruling 2026-11-22: no suspension modelling, and ReStock depth masks stay skipped on the ghost); a part whose real side carries a rendered depth mask the ghost skips (`nonVisual` `depth-mask`) caps the same way with `depth-mask`; a structural or skin failure still fails. or by the skin proof's `skin-mismatch` (`partparity.skin_flags`, the exact rule of `PartParitySkinRules.GhostFlags`: a skin module with real != ghost case-insensitively, a null on one side only counting as a difference); a B9 case's unread ghost is `skin-not-modelled:b9`, a `warn`. The row also re-derives every part case's skin flags and reds `INVALID(parity-instrument)` when they differ from the sidecar's own `flags` |
| case `error` | a setup fault (`TestCommandPartParitySweep.IsSetupFault`: any status but `ok` and `still-animating`), the skin proof's `requested != real` (`skin-not-applied`), or a non-fault case with no measured view; `still-animating` is judged on its measures |

The control cases are not judged as parts: the C# judge's recorded outcomes are the controls'
verdicts (below). Four INSTRUMENT gates run on every sweeping lane from the start, armed or not:

- Host: `progress.hostDamaged = true` (the C# guard of 4.1 stopped the sweep) is
  INVALID(parity-host-damaged), ahead of every other gate: the controls still pending and the
  sweep left incomplete are its symptoms (they stay listed among the problems), and nothing the
  sweep did not run is a part finding.

- Controls: every id of 4.5 recorded with outcome `ok`, and `progress.controls = ok` (a sweep run
  with `controls=false` proves nothing and fails this too). Else INVALID(parity-controls).
- Completeness: `progress.complete = true`; every planned case (the manifest's `cases[]`, which
  leaves the exclusions out) and every control has a complete sidecar (schema, matching `caseId`, a
  known `setup.status`, at least one view unless the status is a setup fault, and a case with views
  names an `atlas.file` that was harvested) or a recorded setup fault; `isolate.txt` owes nothing and no `inflight.txt` is left. Else INVALID(parity-incomplete).
- Python recompute: every control with an atlas plus `ceil(2 %)` of the part cases with an atlas
  (at least one), drawn by `random.Random(<runId>)` from the sorted ids, so a run's sample is
  reproducible from its id. The atlas is decoded with `harness/tools/gui_mirror.py`'s `read_png`;
  per view the two tiles (row 0 real, row 1 ghost, column = the view's index in `atlas.cols`) give
  coverage at alpha byte >= 128, the tolerant mismatch (a real-only pixel counts only with no
  ghost-covered pixel in its 8-neighbourhood, and the mirror), the largest 4-connected blob of each,
  the bboxes, the centroids at pixel centres, the border touch and IoU (1 when both sides are
  empty), exactly `PartParityMetrics.Measure`'s definitions. Every count, bbox and border flag must
  equal the sidecar's; centroids and IoU agree within 1e-9. Rows are Python integers, so the
  dilation and the counts are whole-row bit operations and the blobs are found over row runs
  (about 0.02 s per 4-view 512 px case). Any difference, an unreadable atlas or an atlas whose
  layout disagrees with the sidecar is INVALID(parity-instrument). Measured on the green P2 flight's
  `pp1` directory: all 28 sidecars (8 controls, 20 parts) recompute with zero differences.

The row's status: INVALID when any gate fails (the host first, then the controls, then the
recompute, then completeness; across labels the same order); else REPORT, or, armed, FAIL on any `fail` case and PASS otherwise. An `error` case
never fails an armed lane (it is a setup fault, not a ghost finding). The row logs one Info line
`verify partParity status= gating= subkind= label= status= cases= pass= warn= fail= error=
controls= complete= recompute= mismatches= worst=[<id>(<verdict>:<first reason>),...]` (the five
worst cases), one Warn per problem, and writes `partparity-verdicts.json`
(`parsek-part-parity-verdicts/1`: thresholds, counts, the three gates, the recompute sample and
mismatches, every part case's verdict and reasons) beside each sweep.

## 7. The seam verb

`PartParitySweep label=<label> [op=reset|run] [filter=<glob>] [source=<csv>] [skins=default|all]
[views=required|all|<csv>] [tile=256..1024] [shard=<k>/<n>] [limit=<n>] [controls=true|false]
[maxSeconds=<60..480>]`

- Scene `RequiresFlight` with an orbiting active vessel (refusals for a landed or suborbital host).
- In `DEFERRED_SEAM_VERBS`; the spec's step budget is at most 540 s (S8). A step answers OK
  `label done=true|false next= cases= complete= crashed= setupFaults= controls=ok|bad|pending wall=
  hostDamaged=true|false hostDamage=none|gone|parts|situation`. The two host keys are last, so a
  pin written for the first nine keys still matches; a step the host guard stopped (4.1), and every
  later step of that label, answers OK with `done=false hostDamaged=true`, never a refusal.
- Timeout: the applier keeps the runner's step iterator and, on the 540 s timeout, DISPOSES it
  before `StopCoroutine` (which only abandons an iterator, so no `finally` would run). `RunStep`
  releases everything in a `finally` (`StepContext.Cleanup`: idempotent, safe between waves,
  mid-spawn / settle, between two views of a case and in teardown; it kills the spawned vessels,
  destroys the ghosts with their cloned materials, releases the capture camera, render targets and
  readback texture, and restores the case-log observer that held the tile arrays), after disposing
  the nested iterators: every iterator the runner drives by hand is disposed in its own `finally`,
  so the dispose reaches the innermost one (the case's observer `finally` included). A stopped step
  logs Warn `PartParity step abandoned label= op=: stopped before its end, cleanup ran
  (observerRestored= casesDestroyed= captureReleased= faults=) ...`; a step that ends normally logs
  the same tokens on a Verbose `step cleanup:` line. The in-game cell disposes the steps it drives
  the same way.
  A spec lists enough `op=run` steps for the measured case count; a step after completion answers
  `done=true` at once. `skins=all` is how a lane covers every skin of the full part list on an
  install (3.2); each `op=run` step logs one Info `skin cases: skins=<all|default>
  install=<n> (base= variant= b9=) planned=<n> (base= variant= b9=)` line after enumeration, the
  install's cases by kind and the ones its selection planned, so the chunk size can be re-read
  from a flight.
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

Built in P4: `harness/tools/part_parity_view.py <dir> [<dir> ...] [--baseline <dir>] [--out <dir>]
[--images auto|copy|in-place]`, stdlib only, tests in `harness/lib/test_part_parity_view.py` (CI
discovers `lib/` only). Inputs are sweep directories (shards, or two profiles such as stock and
ReStock); a `NAME=<dir>` argument names a sweep's profile. Output is a static site: `index.html`
(vanilla JS and CSS inline, no CDN, offline from `file://`) and `data.js`, every manifest and
sidecar as one JS literal (`window.PARITY_DATA = {...};`), because `file://` cannot `fetch` JSON.
Images are never re-encoded: they are referenced in place by relative path (the default, output
`<first dir>/viewer/`), or copied under `<out>/img/<k>/` (the default when `--out` is given, and
the automatic fallback for a sweep on another drive, where no relative path exists). The tool
refuses to write into the repository tree, except the gitignored `harness/results/`.

Layout: a pure core (`build_site_data` and its helpers: case rows, trigger lines, part and skin
grouping, filter values, regression diff, cross-profile links, the `data.js` literal) and a thin
I/O shell (`load_sweep`, `plan_images`, `main`). Every verdict comes from `partparity.py`
(`judge_case` / `judge_view`), and the sweep header's gates are its `check_controls` /
`check_completeness`. Nothing on the page records or overrides a verdict: the tool detects, the
owner verifies.

The page is ONE continuous scroll, with no click per part (owner rulings, 2026-11-21):

- **Sticky header**: the counter of part cases by verdict (fail / warn / error / pass) and the
  controls ok count; three filters, verdict (with "flagged": fail, warn, error), source and a
  search box; sort flagged-first or by name; a toggle for the difference tile. In regression
  mode a fourth filter, baseline (moved / new / same). Filters hide whole part groups.
- **Legend**: the two identity colours, blue `REAL part (stock KSP)` and magenta `GHOST (Parsek
  reconstruction)`, chosen apart from the verdict colours (green pass, amber warn, red fail,
  grey error). Every tile carries its side's caption bar and frame in its identity colour.
- **Sweeps** (collapsed unless a sweep is incomplete or its manifest is bad): profile, label,
  directory, DLL hash, GameData mods, tile and views, settings, completeness, exclusions, the
  verifier row's recompute when `partparity-verdicts.json` sits in the directory, the view
  limits of 4.3, and the verdict lines.
- **Controls**: their own section, collapsed when all are ok, open with a red border when any
  is not. Each control shows its views like a skin, with its subject and required outcome. A
  control's chip is its recorded outcome, never a part verdict (6).
- **Parts**: one block per (profile, part), its border the part's worst verdict, ordered fail,
  warn, error, then pass (an error is a setup fault, not a ghost finding, so it ranks below
  warn). The header holds the title, name, source, the skin count and the trigger line: what
  fired, in plain words from `partparity.py`'s reasons ("ghost missing mesh X", "shape blob 312
  px in side-b", "skin mismatch: real=Gold ghost=Bare", "colour blob 98 px in top"; a part with
  several skins names the skin). It also links to the same part in another loaded profile.
  Inside the block, one sub-block per skin in the order base, `ModulePartVariants` variants, B9
  subtypes (the C# sorts a part's cases by skin ordinal, so manifest index order is that order),
  each with its own thinner border in its own verdict colour and its own trigger line. Inside a
  skin, one row per view: REAL | GHOST | DIFFERENCE, at the same size and crop, then the view's
  verdict chip, its shape blob, colour blob and IoU, and its trigger. A collapsed "details"
  disclosure under each skin holds the per-view measure table, the structural diff, the skin
  proof (mismatched names highlighted), tags, flags, part and model urls, modules (when a
  sidecar carries them), setup detail, the baseline changes and the captured log lines.
- **Tiles** are CSS crops of the atlas (`background-size` of cols x 100 % by rows x 100 %,
  `background-position` at the view's column and row). The difference tile stacks the two crops
  on black with `mix-blend-mode: difference` inside `isolation: isolate`, so an identical pair
  reads black. No canvas (pixel reads are blocked on `file://`). Clicking a tile opens that view
  at 1:1 in a dialog. Per-tile PNG crops were not used: they would need a stdlib PNG decode and
  re-encode of every atlas (minutes over a sweep) and multiply the files on disk, while one
  atlas per case is one request for all of its tiles.
- **Scale**: each part block has `content-visibility: auto` and a `contain-intrinsic-size`
  estimate. An `IntersectionObserver` builds its body and sets its atlas as the tiles'
  `background-image` 1500 px ahead of the viewport, and drops the images again when the block
  scrolls away, so 1500 atlases never decode at once. Measured on a synthetic 1508-case sweep
  cloned from the P2 directory: `data.js` 9.5 MB (about 6.3 KB per case, mostly the captured
  log lines), written in 6 s; at load the page builds 3 of 500 part bodies (4573 DOM nodes) and
  sets 48 tile layers.
- **Navigation**: `#case=<profile>/<caseId>` scrolls to that skin (to the part header when it
  is the part's first skin), clearing any filter that would hide it; `j` / `k` step part to
  part through the visible groups and update the anchor; `/` focuses the search. Light and dark
  follow `prefers-color-scheme`.
- **Regression mode** (`--baseline <dir>`, repeatable): cases pair by case id within paired
  profiles (same name, else the one unmatched profile on each side; two against one stays
  unpaired, so nothing is compared by guess). A case is `moved` when its verdict, setup status,
  rendered missing / extra counts or live skin names changed, or its worst shape blob or colour
  blob moved by more than 8 px, or its minimum IoU by more than 0.002; else `same`; `new` when
  the baseline lacks it. Baseline-only cases are listed as gone in the sweeps box.
- **Profile name**: the manifest's `profile` when it is a plain name, else the sweep's `label`,
  else the directory name, with a note. Finding (P4): the P2 / P3 writer records `..` on the
  harness installs (`PartParityCaseCapture.ProfileName` takes the last segment of
  `KSPUtil.ApplicationRootPath`, which ends in `KSP_x64_Data/../`), so today every sweep is
  named after its label.
- GIF / APNG strips (9.2) are not built: `--gif` is reserved and refuses, and a case row's
  `strips` slot is where the page will read them from.

Images are not published as an Artifact (a sweep is hundreds of MB; 16 MB page limit).

## 9. Extensions

### 9.1 States and contexts

A state case drives the stock action on the real part (the module's own KSPEvent / KSPAction, as
the `PartAction` seam presses one), waits for the module's done signal, then builds the ghost two
ways a real recording plays it: from the stowed snapshot plus the transition events the recorder's
own pollers emit for the action (a deploy during a recording), and from the deployed snapshot plus
its start seeds (a recording that starts deployed), so the event path, the seed path and the
snapshot baseline (`SnapshotPartBaseline` / `ApplySnapshotBaselines`) are all tested. The Mk16-XL
canopy (semi, full) lives here, deployed by stock in the air near KSC (owner ruling 2026-11-22). Context cases author small assemblies on named attach nodes (FGV's
`AuthorPartNode`) and frame on the subject part; FGV's six configurations become context cases.
The concrete P6 plan (families, actions, routes, case ids, verdicts, viewer, budget, work
breakdown) is Appendix D.

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
- As built (P3): PP-1 sweeps `skins=all` in TWO `op=run maxSeconds=480` steps (budget 540 each,
  `runtime.budgetSeconds = 1800`), sized from Appendix C (about 0.7 s per case, 468 parts after the
  11 exclusions, about 940 cases with every skin: about 660 s against 960 s of steps). PP-2 is the
  same shape with label `pp2` and THREE run steps (`budgetSeconds = 2400`; about 1500 cases, about
  1050 s against 1440 s). Both declare `[expectations.partParity] gating = false` and keep the P2
  log pins (the run-step line with `crashed=0 controls=ok`, the applier's OK line) as a second
  guard; `[retry] policy = "none"` (a retry's `op=reset` would discard the sweep). A last step that
  still answers `done=false` reads INVALID(parity-incomplete): add a step. A sweep the host guard
  stopped (4.1) reads INVALID(parity-host-damaged): re-fly it, nothing about the unrun parts is
  known. The verdict classes are terminal INVALID(`parity-controls` / `parity-instrument` /
  `parity-incomplete` / `parity-host-damaged`), placed after the
  analyzer and ahead of the contract verifiers in `hlib.classify_verdict` (a bad control also
  misses the step-line pin, and must not read as a log-contract red), and PARSEK-FAIL(`part-parity`)
  for an armed failing case. The armed set is pinned by `PARTPARITY_ARMED_SPECS` in
  `harness/lib/test_hlib.py`, empty as shipped.
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
| `Source/Parsek/TestCommands/TestCommandPartParitySweep.cs` | Pure: args, refusals, case order, sharding, case ids, resume decision (the inflight / isolation rule of 5.3), payload. |
| `Source/Parsek/TestCommands/PartParityMetrics.cs` | Pure: mask from two clears, tolerance mismatch, blobs, bbox, centroid, colour statistics. |
| `Source/Parsek/TestCommands/PartParityStructure.cs` | Pure: renderer record pairing and the structural diff. |
| `Source/Parsek/TestCommands/PartParityJson.cs` | Pure: manifest and sidecar model + invariant writer (`MapGeometryJson` pattern), `SchemaId`s. |
| `Source/Parsek/TestCommands/PartParityFramingRule.cs` | Pure: the framing rule of 4.2 (paired box, the 2x reach for unpaired real renderers, dropped list, `ghost-outside-real-bounds`). |
| `Source/Parsek/TestCommands/PartParityImaging.cs` | Pure: the PNG encoder (`PartParityPng`), atlas and thumbnail (`PartParityImage`). |
| `Source/Parsek/TestCommands/PartParityJsonReader.cs` | Pure: a minimal JSON reader, so each step rebuilds the manifest from the sidecars on disk. |
| `Source/Parsek/TestCommands/PartParitySweepPlan.cs` | Pure: wave slots and the solo-wave cut, the settle rule, the controls' required outcomes and their judge, manifest summaries, the cleanup tokens. |
| `Source/Parsek/TestCommands/PartParityCaseAuthoring.cs` | Pure rules (`PartParityCaseRules`: exclusions, model urls, source, tags) + live enumeration, part node authoring (the variant in both keys, a B9 option's subtype) and the live skin read (`LiveSkinName`). |
| `Source/Parsek/TestCommands/PartParitySkinRules.cs` | Pure (P3): the `skins=all` enumeration over ConfigNode part configs (`EnumerateSkinOptions`, `CaseKeys`), B9 authoring into the PART node, the snapshot read-back, the skin proof's request and flags (`skin-mismatch`, `skin-not-modelled:b9`, `skin-not-applied`), the reflection read of a B9 module's subtype, the per-step counts. |
| `Source/Parsek/TestCommands/PartParityCapture.cs` | Live: camera, light rig, hide / render / read / restore (`PartParityCaptureMath` is its pure camera math). |
| `Source/Parsek/TestCommands/PartParityCaseCapture.cs` | Live: one case in the order of 4.2 (records, seeds, ghost, framing, views, files) and the manifest writer. |
| `Source/Parsek/TestCommands/PartParityHostIntegrity.cs` | Pure: the host-integrity guard of 4.1 (the damage decision, its detail and Warn line, `host.txt` / `hostdamage.txt`) and the case-vessel-lost rule. |
| `Source/Parsek/TestCommands/PartParitySweepRunner.cs` | Live: the step and wave coroutine (`RunStep(args, onDone)`), shared by the seam verb and the in-game cell. |
| `Source/Parsek/TestCommands/ParsekTestCommandAddon.PartParitySweep.cs` | Thin applier: starts `PartParitySweepRunner.RunStep` and answers the seam. |
| `Source/Parsek/Ghost/GhostPlaybackEngine.*` | The extracted build-and-initialise seam (D2), `GhostPlaybackEngine.InitializeBuiltGhostVisuals(state, buildResult, traj)`, the engine's own load path (`TryPopulateGhostVisuals`) rewired through it. Proof of no behaviour change: `il_compare.py` before / after reports only the new method and the caller's body, and inlining the seam's body back at the call site gives the caller's old body exactly (`--fold` models moves between types, not an extract-method). The loop-reuse re-init (`SpawnDestroy`) and the KSC spawn run other subsets (no pivot; KSC passes no trajectory) and stay as they are. Activation stays the engine's deferred `ActivateGhostVisualsIfNeeded`. P2 adds the seed-prefix prime (4.2 step 3): `ApplyPartStateForFrame` and `StopVisualFxForFrame` extracted from `ApplyFrameVisuals` (proved the same way: the multi-seam inline fold of both gives the old body), composed by `PrimeHiddenPartStateAtUT`. |
| `Source/Parsek/Ghost/GhostVisualBuilder*`, `GhostTypes.cs` | The applied-skin record (P3): `AddPartVisuals` hands out a `GhostAppliedSkin` (variant name + resolution path) for each part with a `ModulePartVariants`, `AdvanceTimelineGhostBuild` keys it by persistentId on the pending build, `CompleteTimelineGhostBuild` moves it onto `GhostBuildResult.appliedSkins`, read through `TryGetAppliedSkin`. Read-only: nothing in the build or playback reads it. Proof of no behaviour change: `il_compare.py` before / after reports only the new type, the two fields, the accessor and the bodies of `AddPartVisuals`, `AdvanceTimelineGhostBuild`, `CompleteTimelineGhostBuild` and the pending build's constructor, and each of those bodies is the old one with instructions INSERTED only (locals mapped back, every branch target checked through the instruction map; the one substitution is the call's new `out` parameter). |
| `Source/Parsek/InGameTests/GhostCoOrbitalRig.cs` | `SpawnCoOrbital` (log tag passed in), `UniqueFlightIds`, `AuthorPartNode` (writes `moduleVariantName` and the `ModulePartVariants` `selectedVariant`), `TryBuildGhostFromLiveVessel` (snapshot, throwaway recording, timeline build, the D2 seam) and `NeutralisePhysics`: moved out of `FairingGhostVisualInGameTests`, used by both, FGV's nodes and log lines unchanged. |
| `Source/Parsek/InGameTests/PartParityInGameTests.cs` | Ctrl+Shift+T entry. |
| `harness/lib/partparity.py` + tests | Pure manifest reader, verdicts, recompute, verifier row, baseline compare. |
| `harness/tools/part_parity_view.py` + `harness/lib/test_part_parity_view.py` | The viewer (8); the GIF / APNG writer later (9.2). |

`Parsek.csproj` gains NO Unity module reference: PNGs are written by the P0 spike's own encoder
(`PartParityPng`, zlib over `System.IO.Compression.DeflateStream`, filter 0, colour type 6; xUnit
round-trips it against a spec decode), so `UnityEngine.ImageConversionModule` and `EncodeToPNG` are
not used. No new mutable statics (sweep state lives on the
coroutine and in the label directory). Everything is automation-only and inert unless the seam is
armed or the in-game test is launched: no player-facing surface.

Logging: one `[PartParity]` Info line per wave (cases, setup faults, wall, memory) and one per step
summary; per-case Verbose lines; Warn for a setup fault.

## 12. Plan

| Phase | Content | Done when |
| --- | --- | --- |
| P0 (DONE 2026-11-21, Appendix B) | Capture spike on both harness installs, stock and ReStock / ReStock+ parts: `Camera.Render` into an MSAA target, resolve, double clear, ortho with KSP shaders, light rig, `pixelLightCount`, `masterTextureLimit`, `forceRenderingOff`. | Pictures read by eye; every unknown in 4.4 answered and written into this doc. |
| P1 | Pure halves + xUnit (measures on synthetic masks incl. shifted / rotated / missing-blob shapes, structural diff, sharding, case ids, resume decision, JSON writer, args); `GhostCoOrbitalRig` extraction; D2 seam extraction with the IL fold proof. | `dotnet test` green; FGV-1 flown green on the refactor. |
| P2 (DONE 2026-11-21, Appendix C) | Capture engine + verb + in-game test + files; controls; default skins; a 20-part dev run. | Controls behave; per-wave wall time measured; chunk size set from it. |
| P3 (DONE 2026-11-22, Appendix C) | All skins, tags, skin proof, structural diff; harvest leg; `partparity.py` row; lanes PP-1 / PP-2 report-only. C# half BUILT (not flown): `skins=all` enumerates every `ModulePartVariants` VARIANT and every `ModuleB9PartSwitch` SUBTYPE one module at a time (3.2), B9 authored as `moduleID` + `currentSubtype`, the full skin proof (`requested` / `real` / `snapshot` / `ghost`) through the new `GhostBuildResult.TryGetAppliedSkin` record (IL proof: insertions only, 11), flags `skin-mismatch` / `skin-not-modelled:b9`, per-step `skin cases:` counts. Finding: the ghost builder applies no B9 subtype, so every B9 case reads `skin-not-modelled:b9`. Harness half (verifier row, lanes) separate. Harness half BUILT 2026-11-21 (5.1, 6, 10: the harvest leg, the row with its three instrument gates and the Python recompute, PP-1 widened and PP-2 authored); status in `autotest-status.md`. Flown 2026-11-22: PP-1 `2026-11-22_0033` and PP-2 `2026-11-22_0045` PASS attempt 1 after two reading pairs; 16 ghost defects fixed (PRs #2745, #2749), the detector re-ruled, 4 report-only fails left (todo GHOST-PARITY-RESIDUALS-2026-11-22). | First full reading run on both installs; thresholds re-pinned; findings filed. |
| P4 | Viewer (8). BUILT 2026-11-21, not yet read by the owner on a full sweep: one continuous page grouped part > skin > view, REAL / GHOST / DIFFERENCE per view with fixed identity colours, verdicts and trigger lines from `partparity.py` only, flagged parts first, filters verdict / source / search, controls in their own section, the sweep header with partparity's gates, regression mode, cross-profile links, a lazy build for about 1500 cases; run on the P2 directory `pp1-2026-11-21-p2`. Finding: the manifest's `profile` reads `..` (8). The full sweeps it is meant for exist since 2026-11-22 (`2026-11-22_0033` / `_0045`, Appendix C). | Owner reads the full stock + ReStock site. |
| P5 | Baseline + accepted list; gate armed after a second matching reading run. | A deliberately broken ghost (mutation) reds the armed lane. |
| P6 | States and contexts (9.1); FGV configurations migrated. PLANNED 2026-11-22 (Appendix D, rev 2): 12 transformation families, two ghost routes per state case, the jettison node contexts, chutes deployed for real in the air near KSC, the FGV fairing contexts, new report-only lanes PP-3 / PP-4 (orbit) and PP-5 / PP-6 (air). | Chute and deployables covered: every in-scope state case on both installs has a sidecar or a recorded setup fault, the state controls behave, the press gate holds, the owner reads stowed and deployed side by side. |
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
| B9 field is `currentSubtypeName`, select by `moduleID`; B9 here only switches plumes | Opus | Fixed (3.2). Verified in the installed DLL. P3 correction: `CurrentSubtypeName` is the C# property; the key it persists under is `currentSubtype` (3.2). |
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

## Appendix C. Flights: P2 (2026-11-21), P3 / P4 (2026-11-21 to 2026-11-22)

Lane `PP-1-part-parity-stock` on stock-minimal, branch `part-parity-p2`. Five flights, the fifth
green (`2026-11-21_1739`, PASS attempt 1, 134 s run wall, automation DLL sha256 prefix a46e8133):

| Measure | Value |
| --- | --- |
| Install | 479 loaded parts, 11 excluded (3.1), 20 planned (`limit=20`), default skins |
| Sweep wall | 19.0 s for 8 controls plus 20 parts in 4 waves (5.3 / 7.8 / 3.6 / 2.2 s); flights 1-4 read 18.5 to 23.9 s |
| Per case | about 0.7 s; settle and spawn dominate, the 4 views x 2 sides x 2 clears take about 0.15 s (Appendix B) |
| Setup | 0 setup faults, 0 crashed; 1 to 3 cases per run `still-animating` (CircularIntake, Clydesdale, LV-T45), captured anyway |
| Controls | all 8 ok on the fifth flight |
| Parts | every part case iou 1.0000, colour blobs at most 44 px (deployed science parts), structure diffs empty |

Chunk size: the full stock list (about 470 cases with default skins) runs in about 330 s, under one
480 s step; `skins=all` roughly doubles it, so P3 plans two `op=run` steps per install and measures.

The missing-mesh control took four flights to get right, and each failure was the instrument
telling the truth:

1. Flight 1: "smallest renderer whose diagonal is >= 4 % of the box" hid the LV-T45's whole engine
   bell (IoU 0.68).
2. Flight 2: a projected-box-area pick chose an animated telescopic ladder whose real and ghost
   sides already differed (real retracted, ghost extended). That difference is a real ghost defect,
   later confirmed by the owner from play and fixed separately (PR #2743).
3. Flight 3: a measured solo-coverage pick chose the Mk1 pod's flag decal, which lies flat on the
   hull: removing it changes nothing visible (shape blob 0, colour blob 35 px).
4. Flight 4: a silhouette-contribution pick over 8 still stock parts found none in the window
   (bodies of 100k+ px, flag decals of 0 px, ladder rungs of about 13.5k px each).

The control now plants a small cube (0.3 % of the `side-a` box projection, at least 12 px) on the
real `fuelTankSmallFlat` only (4.5). On flight 5 it failed a local measure, kept IoU >= 0.99 and
listed the cube as missing on the ghost, as required.

### P3 / P4 flights (2026-11-21 to 2026-11-22)

PP-1 (stock-minimal, 2 run steps) and PP-2 (modded-compat, 3 run steps), `skins=all`, branches
`part-parity-p3` then `part-parity-p4`, flown as pairs:

| Pair | PP-1 | PP-2 |
| --- | --- | --- |
| `2026-11-21_2011` / `_2021` | INVALID(parity-incomplete), 668 cases | INVALID(parity-incomplete), 671 of 1511, 496 `spawn-timeout` |
| `2026-11-21_2111` / `_2123` | PASS, 792 cases, fail 116 | PASS, 1511 cases, fail 422, error 1 |
| `2026-11-21_2306` / `_2317` | PASS, fail 19 | PASS, fail 218, error 1 |
| `2026-11-22_0004` / `_0015` | PASS, fail 10 | PASS, fail 146, error 1 |
| `2026-11-22_0033` / `_0045` | PASS, 792 cases: pass 766, warn 25, fail 1, error 0 | PASS, 1510 cases: pass 1392, warn 115, fail 3, error 0 |

The first pair broke its host: the clone the ghost builder instantiates to sample a part's heat
animation kept its collider into the next physics step and pushed the host apart (fixed, PR #2744;
the host guard of 4.1 followed). The reading pair `_2111` / `_2123` traced to 10 ghost defects
(PR #2745), the later pairs to 6 more (PR #2749), plus the detector rulings: depth masks and
Waterfall plumes are non-visual, parts whose look depends on context and depth-mask parts cap at
warn, and the colour line threshold is re-pinned 64 -> 256 px. The one `error` (`launchclamp1`,
`died-settling`) went when the `ground-only` exclusion learned to match a mod's subclass of the
launch clamp module.

Both final runs: PASS attempt 1 (660 s / 963 s run wall), automation DLL sha256 prefix 0da811c9,
controls ok, complete, recompute 24 / 39 with 0 mismatches. Every pair from `_2111` on read controls
ok and complete.

The 4 fails left are colour-only differences, accepted as cosmetic by owner ruling 2026-11-22
(todo GHOST-PARITY-RESIDUALS-2026-11-22, closed); they are the first entries of P5's accepted list:

- `grapplingdevice--basic` (stock): colour blob 477 px `side-a` (479 `side-b`), thin outline lines.
- `heatshield3--red` (modded): colour blob 158017 px `top`, a uniform tint offset over the whole top
  face, though it looks the same to the eye; its black and classic variants pass.
- `mediumdishantenna--default` (modded): colour blob 756 px `top`, seam lines.
- `rotor.01--gray` (modded): colour blob 329 px `side-b`, one line.

Known non-defects: ReStock depth masks throw a NullReferenceException in `OnStart` on the real part
(InfraredTelescope's cyan lens), and the ghost skipping masks stays (owner ruling 2026-11-22);
suspension droop is left by ruling.

## Appendix D. P6 plan: deployed state cases (rev 2, 2026-11-22)

Status: PLAN, not built. Rev 1 was reviewed by one clean-context reviewer; rev 2 folds in its ten
merge-blocking findings and three omissions (D.11) and the owner rulings below. P6 adds the
`state` axis of 3.3: every part that transforms is compared with its ghost in the transformed state
(deployed, open, lit, semi / full canopy, jettisoned), beside the existing stowed case. Stowed stays
the existing rest case, unchanged: same case ids, same lanes, same order. Counts were measured on
2026-11-22 from the post-patch part configs of both harness installs
(`<umbrella>/automation/<profile>/GameData/ModuleManager.ConfigCache`: stock-minimal 478 parts,
modded-compat 602, after the 3.1 exclusions).

Owner rulings, 2026-11-22:

- R1. Parachutes are NOT posed in vacuum. Chute cases spawn in the air near KSC, the way Parsek's
  KSC showcase places its parts, and stock deploys semi and full for real (D.4).
- R2. State cases run at the base skin only.
- R3. State cases run in new report-only lanes (PP-3 / PP-4 in orbit, PP-5 / PP-6 for the chutes
  in the air), never in PP-1 / PP-2.

### D.1 Transformations in P6

One state case per (part, family, module). A part with two families (the LY-35 gear also carries a
light) gets one case per family, never the cross product (the B9 rule of 3.2). Detection is pure,
in `PartParityStateRules` (BUILT, S1), over the prefab's module list (each module's `moduleName`
and its type chain up to `PartModule`) and its `partConfig` MODULE nodes. Each family matches with
the SAME predicate its recorder poller uses, so the sweep never finds a family the recorder would
not poll, and a subclass lands in one family only. The xUnit cell
`EveryFamilyPredicate_IsTheRecordersOwn` pins each predicate inside its own poller's body
(comments stripped), with the first-per-part `break` where a family depends on it;
`TheLightFamilies_AreTheRecordersLightCheck` does the same for the light check, and
`TheDedicatedHandlerMirror_IsTheRecordersList` compares the types and names
`FlightRecorder.HasDedicatedAnimateHandler` reads with the rules' mirror of it. Family ids, in
this order: `deployable`, `gear`, `ladder`, `animation-group`, `cargo-bay`, `animate-generic`,
`airbrake`, `light`, `cabin-light`, `parachute`, `jettison`, `fairing`.

| Family | Predicate (the poller's own) | State word | Stock action pressed | Done signal (the stock field the recorder classifies) | Recorder event (poller) | Stock / modded cases |
| --- | --- | --- | --- | --- | --- | --- |
| Deployable panel, antenna, radiator | `FindModuleImplementing<ModuleDeployablePart>` and `DeployableHasNoPoseAnimation` false (`PollDeployables`) | `deployed` | KSPEvent `Extend` | `deployState == EXTENDED`; sun trackers also settle (D.3) | `DeployableExtended` | 19 / 22 |
| Landing gear, legs, deploying wheels | `as ModuleWheels.ModuleWheelDeployment`, first per part (`PollGear`) | `deployed` | KSPEvent `EventToggle` (when retracted) | `FlightRecorder.ClassifyGearState(stateString)` deployed | `GearDeployed` | 8 / 9 |
| Ladder | `moduleName == "RetractableLadder"`, first per part (`PollLadders`) | `deployed` | KSPEvent `Extend` | `TryClassifyRetractableLadderState` deployed | `DeployableExtended` midx | 2 / 2 |
| Animation group, in-orbit parts | `moduleName == "ModuleAnimationGroup"`, every module (`PollAnimationGroups`), not on a `ModuleGroundPart` part | `deployed` | KSPEvent `DeployModule` | `TryClassifyAnimationGroupState` deployed | `DeployableExtended` midx | 5 / 4 |
| Cargo and service bay | `FindModuleImplementing<ModuleCargoBay>` whose `DeployModuleIndex` (default 0) is a `ModuleAnimateGeneric` (`PollCargoBays`) | `open` | the linked module's KSPEvent `Toggle` | `ClassifyCargoBayState` open | `CargoBayOpened` | 8 / 9 |
| Standalone animate generic (doors, inflatables, port shields, Klaw, science doors) | `as ModuleAnimateGeneric` with an `animationName` (default `default`), on a part where `FlightRecorder.HasDedicatedAnimateHandler` is false (`PollAnimateGeneric`) | `deployed` | KSPEvent `Toggle` (a hidden event too; the proof records `eventVisible`) | `TryClassifyAnimateGenericState` deployed | `DeployableExtended` midx | 10 / 15 |
| Airbrake | `moduleName == "ModuleAeroSurface"`, every module (`PollAeroSurfaces`) | `deployed` | KSPAction `ActionToggleBrakes` (Activate) | `TryClassifyAeroSurfaceState` deployed, then the surface transform still for 0.5 s (D.3) | `DeployableExtended` midx | 1 / 1 |
| Light | `FindModuleImplementing<ModuleLight>` (the recorder's light check, D.10 S2) | `lit` | KSPEvent `LightsOn`, with electric charge authored (D.3) | `isOn`, then 1 s | `LightOn` | 15 / 18 |
| Cabin light (colour changer) | `as ModuleColorChanger` with `toggleInFlight` (default true), the first on a part with no `ModuleLight` (same check) | `lit` | KSPEvent `ToggleEvent` | `animState` true, then `1 / animRate` s plus 0.5 s (cap 5 s) | `LightOn` | 26 / 37 |
| Parachute | `FindModuleImplementing<ModuleParachute>` (`PollParachutes`) | `semi`, `full`, context `air` | KSPEvent `Deploy` in the air, then the in-flight `deployAltitude` tweak for `full` (D.4) | `deploymentState` SEMIDEPLOYED / DEPLOYED (`ClassifyParachuteState`) and the deploy animation finished | `ParachuteSemiDeployed`, then `ParachuteDeployed` | 10 / 10 |
| Engine shroud and other jettisons | every `as ModuleJettison` module (`PollJettisons`), one case set per part | `jettisoned`, context by node rule (D.3) | KSPEvent `Jettison` on every jettison module of the part | `isJettisoned` on every module | `ShroudJettisoned` | 37 / 60 parts, 66 / 108 cases |
| Procedural fairing | `FindModuleImplementing<ModuleProceduralFairing>` (`PollFairings`) | FGV's states (intact, `deployed`, separated / rebuilt) in FGV's six contexts | KSPEvent `DeployFairing`, FGV's own separation | `GetScalar >= 0.5` and FGV's existing checks | `FairingJettisoned` | 18 / 18 (S8) |

The counts were measured by S1 (2026-11-22) and are pinned by xUnit
(`StockMinimal_EnumeratesTheD1Counts`, `ModdedCompat_EnumeratesTheD1Counts`) over committed
fixtures (`Source/Parsek.Tests/Fixtures/PartParity/state-parts-<profile>.txt`: every part of the
archived PP-1 / PP-2 manifests that carries a module the rules read, with its modules in prefab
order, their reflected type chains and the config keys the rules read, derived once from each
install's ConfigCache). Rev 2's planning estimate differed in three rows: the deployable row adds
the Magnetometer Boom (its module is `ModuleDeployablePart` itself, 18 / 21 -> 19 / 22); the cargo
row loses the three / five service modules, whose `ModuleCargoBay` deploys a `ModuleServiceModule`
the poller skips (their shells are the jettison family's ignore-nodes cases; 11 / 14 -> 8 / 9); the
standalone animate generic row loses the two thrust reversers (`JetEngine`, `turboFanSize2`), whose
parts carry `ModuleAnimateHeat`, so `HasDedicatedAnimateHandler` is true and the recorder never polls
them (12 / 17 -> 10 / 15; todo THRUST-REVERSER-NOT-RECORDED). The fairing row's 18 cases arrive with
the FGV context table (D.10 S8); until then each fairing part is excluded
`fairing-contexts-pending`.

Same-word tiebreak (general, in the id rule, D.2): when two or more of one part's cases share a
state word and a context (two standalone animate generics, two animation groups, or any future
pair), EVERY one of them writes `<word>-m<moduleIndex>`, so no id depends on which module was found
first. No part of either install hits it today.

Totals (each step logs the exact ones, D.2):

| Lane | Single-part states | Shroud cases | Fairing contexts | Chute cases (air) | Controls | Cases |
| --- | --- | --- | --- | --- | --- | --- |
| PP-3 (stock, orbit) | 94 | 66 | 18 (S8) | - | 9 | 187 |
| PP-4 (modded, orbit) | 117 | 108 | 18 (S8) | - | 9 | 252 |
| PP-5 (stock, air) | - | - | - | 10 | 4 | 14 |
| PP-6 (modded, air) | - | - | - | 10 | 4 | 14 |

Excluded, each named in the manifest's exclusions with a reason (never silently dropped; reasons
in `PartParityStateRules.ExclusionReasons`, mirrored as `partparity.STATE_EXCLUSION_REASONS`):

- `ground-deploy`: the animation group of a part carrying a `ModuleGroundPart` (the Breaking Ground
  deployed science parts, `DeployedCentralStation` and its seven siblings, and `groundAnchor`: 9
  modules on both installs), which deploys only when placed on a surface. Their other families
  (lights) keep their cases.
- `no-pose`: fixed solar panels (`solarPanels5`, `LgRadialSolarPanel`); the recorder emits nothing
  for them by design.
- `fairing-bay`: a `ModuleCargoBay` whose deploy module is a `ModuleProceduralFairing` (every
  fairing base); the fairing family owns it.
- `bay-deploy-not-animated`: a `ModuleCargoBay` whose deploy module is not a `ModuleAnimateGeneric`
  (the service modules' `ModuleServiceModule`): `PollCargoBays` skips it.
- `cs-deploy-sign`: control-surface deploy (`moduleName == "ModuleControlSurface"`, 24 parts,
  blades included). Its deflection has no Animation: it moves by `Mathf.MoveTowards` at
  `actuatorSpeed`, and on a lone part its direction is `deflectionDirection`, the sign of the
  surface's offset from the vessel's centre of mass (decompiled `ModuleControlSurface`), a float
  accident on a one-part vessel. Left to P7, with an authored mirror context that pins the sign.
  The airbrake keeps its case: its deploy angle sign comes from `brakeDeployInvert` in config.
- `jettison-blocked-by-node`: the jettisoned state of a part whose jettison modules include one
  with `checkBottomNode = true`, in the stacked context (stock's `Jettison` refuses while a part is
  on that node; the stacked stowed case still runs). 5 / 7 parts.
- `jettison-node-missing`: a jettison node the prefab lacks; no part of either install.
- `not-polled`: a `ModuleAnimateGeneric` on a part with a dedicated animate handler that is not the
  cargo bay's own deploy module (the two thrust reversers).
- `no-animation`: a `ModuleAnimateGeneric` whose config sets an empty `animationName`.
- `p7-robotics`: servos, rotors, pistons (`BaseServo`, 22 parts) and `ModuleRobotArmScanner` (3; a
  `ModuleDeployablePart` that extends only while scanning a surface feature): continuous positions,
  the P7 strips (9.2).
- `p8-heat`: `ModuleAnimateHeat` glow (16 / 13) and `ModuleAblator` char: temperature-driven
  material properties, P8 (9.3). `p8-fx`: engine plumes and RCS (each `ModuleEngines` /
  `ModuleRCS` module); light blink is part of the light case.
- `fairing-contexts-pending`: a fairing part while the FGV context table is empty (until S8).
- Not module exclusions, named here only: `broken` (a broken solar panel or wheel: stock breaks a
  panel into free debris and a wheel only under load; not a pose the part keeps) and decouple (a
  decoupler looks the same after it fires; the split is a recording-tree matter).

### D.2 Case identity and what a state run plans

- The case key gains a `StateOption` beside `SkinOption` (family, module name, module index, every
  module index the press acts on, state word, action and whether it is a KSPAction, node names):
  not part of the identity; the id reads `State` and `Context`.
- The id rule (5.2) is unchanged for every existing case: `<part>--<skin>--<state>[--<context>]--
  <hash>`, the state written because it is not `stowed`, plus the same-word tiebreak of D.1.
  Examples: `longantenna--default--deployed--<hash>`,
  `parachutesingle--default--semi--air--<hash>`,
  `liquidengine2--default--jettisoned--stacked--<hash>`,
  `liquidengine2--default--stowed--stacked--<hash>`. State cases run at the part's base skin (R2).
- Stowed case ids cannot move: `stowed` and `alone` are the defaults and are already in the hashed
  tuple (`CaseTuple`). The xUnit pin is a COMMITTED literal: 19 ids copied into
  `PartParityStateContractTests.StowedIds` (base, variant and B9 cases of both installs, among them
  `advancedcanard--default--9301994f` and a variant name with a space), each recomputed from its
  key; the archived manifests stay outside the repo and CI never reads them.
- The args (`TestCommandPartParitySweep.TryParseArgs`, mirrored by hlib's
  `validate_part_parity_sweep_step`): `states=none|all|<csv of stowed, deployed, open, lit, semi,
  full, jettisoned>` (refused `states-arg-invalid`) and `contexts=none|<csv of alone, stacked,
  air>` (refused `contexts-arg-invalid`; `air` stands alone, since the air cases need a host at the
  pad and the orbit cases an orbiting one). Both default to `none`. An air run (`contexts=air`) is
  admitted only with a host PRELAUNCH or LANDED within 500 m of the pad, else REJECTED
  `host-not-at-pad`; every other run keeps `host-not-orbiting`. Until the live state engine lands
  (S3 flips `TestCommandPartParitySweep.StateRunsBuilt` and drops the refusal, with its hlib
  mirror), a well-formed step naming either arg other than `none` is REJECTED
  `state-runs-not-built` (after the grammar, so a bad value keeps its own reason), never run as a
  stowed sweep; the pure planning rules read `TryParseGrammar`.
- What a run plans (pure, `TestCommandPartParitySweep.PlanCases(stowedKeys, stateKeys, args)`):
  - Without `states=` and `contexts=` (both `none`): exactly `SelectCases` over the stowed keys,
    today's list in today's order, so PP-1 / PP-2, their shards, resume `next=` and recompute sample
    are untouched.
  - With either: ONLY the state and context keys whose state matches `states=` (any when `none`;
    the tiebreak is ignored, `deployed` selects `deployed-m3`) and whose context matches
    `contexts=` (`alone` and `stacked` when `none`, so an orbit run never plans the air chutes),
    then the same filter / source / shard / limit selection. A state run plans no `stowed` /
    `alone` case at all, so it never re-runs the stowed sweep (the stacked stowed shroud case is a
    context case and does run).
  - `skins=` governs stowed cases only.
- The control set is a function of the args, the same rule on both sides
  (`TestCommandPartParitySweep.ControlSetFor(args)`, mirrored by `partparity.control_set(settings)`):
  no `states=` / `contexts=` (or `controls=true` with neither): the existing eight, unchanged, so
  PP-1 / PP-2 stay exactly as they are; an orbit state run: the eight plus `ctl-state-events-dropped`;
  an air run: `ctl-empty`, `ctl-ghost-absent`, `ctl-real-vs-real` (subject `parachuteSingle`,
  armed, falling) and `ctl-state-events-dropped-air`. `ControlsState(enabled, outcomes,
  controlSet)`, the judge (`PartParitySweepPlan.JudgeStateControl` for the two state controls) and
  the manifest read that set instead of the fixed `ControlIds` array; the manifest writes it as
  `settings.controlSet` (`TestCommandPartParitySweep.StateSettings`: `states`, `contexts`,
  `controlSet`; a manifest without them reads as `none`), and `check_controls` requires every id of
  the set it recomputes from the manifest's `settings` (a manifest whose written set differs from
  the recomputed one is INVALID(parity-instrument)).
- Sidecar (`parsek-part-parity-case/1`, additive keys only, every key always written, null when
  not measured, stowed cases included; tables in `PartParityJson`, mirrored in `partparity.py`):
  - `stateProof` (`StateProofKeys`): `family`, `module`, `moduleIndex`, `state`, `action`,
    `eventVisible`, `actionResult` (`invoked|missing|threw|refused`), `reached`, `waitS`, `endpoint`
    (`field`, `value`: the stock field and value read), `events` (the event route's recorded events:
    `type`, `ut`, `pid`, `midx`), `ghostEvent` / `ghostStart` (`applied|no-info|pose-not-sampled|
    not-reached`, D.5), `startFirstFrame` (`atTarget`, `trackerDeg`: the start route at its first
    frame, D.5), `sunTracker` (`targetDeg`, `eventDeg`, `startFirstFrameDeg`), `canopy` (`tiltDeg`,
    `wobbleDeg`: air cases, airflow tilt and wobble at capture, D.4), `nodes` (`node`, `part`: the
    context's filler parts by node).
  - `atlas.rows`: `["real", "ghost", "ghost-start", "ghost-stowed"]` for a state case (any state
    but `stowed`), `["real", "ghost"]` for a stowed case as today, the stacked stowed shroud
    included. The row table lives in ONE pure literal (`PartParityJson.AtlasRowsFor(isStateCase)`,
    BUILT), moved there from `PartParityCaseCapture` in S1, so the live capture code holds no
    literal the Python mirror reads.
  - Per view, `start` (real vs ghost-start, the full measure key set of a view, `StartKeys`) and
    `stowedRef` (real vs ghost-stowed, only the shape keys, `StowedRefKeys`: `realPx`, `ghostPx`,
    `realOnlyPx`, `ghostOnlyPx`, `realOnlyTolPx`, `ghostOnlyTolPx`, `realOnlyBlobPx`,
    `ghostOnlyBlobPx`, `realOnlyBlobPct`, `ghostOnlyBlobPct`). `structureStart`: the structural diff
    of the start-route ghost. `stowedCaseId`: the id of the same part / skin at `stowed` / `alone`.
- Manifest: each case row adds `family`, `actionResult` and `reached` (the setup summary; null for
  a stowed case); `settings` records `states=`, `contexts=` and `controlSet`; one Info
  `state cases: states=<...> contexts=<...> install=<n> (<family>=<n> ...) planned=<n>
  excluded=<n> (<reason>=<n> ...)` per step (`PartParityStateRules.FormatCounts`), the
  `skin cases:` pattern of 7. An air run's `host.txt` adds `mode=air`, and the host guard then
  reads PRELAUNCH / LANDED as intact (an orbit baseline writes no `mode=` and is unchanged).
- New setup statuses `action-failed` and `state-timeout` (both setup faults, the press gate's two,
  D.6) join `SetupStatuses` and `partparity.SETUP_STATUSES`.
- Regression keys (P5 baseline, viewer regression mode): `<caseId>/<view>` for the event route, as
  today, plus `<caseId>/<view>/start`; `accepted.toml` entries name the route.

### D.3 The real side in orbit: reaching the state

One case, in order (the 4.2 steps keep their order; new steps marked):

1. Spawn and settle as today (4.1), with the context's extra parts (below) authored by
   `GhostCoOrbitalRig.AuthorPartNode`. A light case's part carries an authored `RESOURCE` node
   `ElectricCharge` (amount = maxAmount = 50): stock `ModuleLight` with `useResources` draws charge
   every `FixedUpdate` and switches itself off when the draw returns under 0.99 of the demand
   (decompiled), and a lone light part holds none. The cheat flag `CheatOptions.InfiniteElectricity`
   was rejected: it is global and would also feed the host. The resource has no visual.
2. NEW: stowed inputs, before any press: `TryBackupSnapshot` (the stowed snapshot),
   `PartStateSeeder.SeedPartStates` into the case's own `PartTrackingSets`, the stowed seed events
   at `t0`.
3. NEW: press. The module's own `BaseEvent.Invoke()` or `BaseAction.Invoke(new KSPActionParam(
   KSPActionGroup.None, KSPActionType.Activate))`, what a PAW click or an action group does (the
   `PartAction` seam's mechanism; that seam's file is not touched). Missing event: `action-failed`
   (`missing`); exception: `action-failed` (`threw`); module state unchanged two frames later:
   `action-failed` (`refused`).
4. NEW: wait for done, in real time. Every frame the case runs the recorder's shared pollers over the
   case vessel into a `PartParityPollSink : PartPollSink` (the case's own event list, tag
   `PartParity`) with the tracking sets of step 2, at the live UT, so the events are the ones
   `FlightRecorder` would append. Done = the family's done signal (D.1), then the existing settle
   rule (no `Animation` on the part playing, 0.5 s). Timeout = `min(20 s, 2 x clip / |speed| + 2 s)`
   from the part's own Animation (5 s for the airbrake and the lights), then `state-timeout`. A done
   signal that reached the target while the poll emitted no event is the flag `state-not-recorded`.
5. Records, ghosts (D.5), framing and views as 4.2, records read AFTER the state is reached.
6. NEW: teardown destroys everything the press threw off (below), in the wave teardown and in
   `StepContext.Cleanup` alike.

Per family:

- Sun-tracking solar panels (9 per install): the done rule adds "pivot rotation changed by less
  than 0.1 deg over 0.5 s", cap 10 s, else captured and tagged `still-tracking` (judged, like
  `still-animating`). Both sides see the same Sun. The ghost side is D.5.
- Airbrake: no Animation (`Mathf.MoveTowards` at `actuatorSpeed`), so done = the classifier reads
  deployed AND the surface transform's local rotation moved less than 0.05 deg over 0.5 s, cap 5 s.
- Jettison context, by each module's own config (decompiled `ModuleJettison.SetupJettisonState`
  and `Jettison`): a module with `ignoreNodes = true` shows its shroud alone, so its `jettisoned`
  case runs in context `alone` (3 / 5 parts). Every other module shows it only with a part on ITS
  `bottomNodeName` (default `bottom`; `top` on `turboFanSize2` and `InflatableHeatShield`,
  `bottom02` on ReStock's `HeatShield0`), so the context `stacked` puts a filler part (`fuelTankSmallFlat`)
  on that node, one filler per distinct node name a part's jettison modules name. A node the
  prefab lacks excludes the case (`jettison-node-missing`). With `checkBottomNode = true` (5 / 7
  parts) stock refuses to jettison while that node is occupied: those run only the stacked stowed
  case (D.1 exclusion). The filler is hidden on both sides (`forceRenderingOff`) and left out of the
  records and framing (flag `context-hidden:<n>`).
- Thrown parts: `Jettison` and `DeployFairing` turn the shroud or panels into free physical objects
  with live colliders (`physicalObject`, which destroys itself only beyond `maxDistance = 1000 m`),
  so they would pile up at the slots. The case keeps the jettison transforms (and the fairing's
  panel roots) read before the press, sets their renderers `forceRenderingOff` for the capture
  (flag `jettison-debris-hidden:<n>`), and destroys their GameObjects at teardown; `Cleanup` does
  the same for any case it tears down mid-wave. A Verbose `debris destroyed n=` line per case.
- Gear and legs: deployed in free fall at full suspension droop, already `context-dependent`, so
  the 6 cap at `warn` (`context-pose`) applies unchanged.
- Lights: the light rig (D5) switches every scene light off, the part's own `Light` included, so
  a `lit` case compares the lens and window emission, not the beam.
- Slots: a deployed part reaches past its stowed bounds and the slot plan reads stowed bounds, so
  an orbit state wave uses every other slot (up to 4 cases, 80 m apart, 60 m or more from the host).
- Step time box: a wave starts only when `elapsed + worstCase(bucket) <= maxSeconds` (480, under the
  540 s seam cap), where `worstCase` is the wave's bound, not the last wave's wall: spawn cap 20 s +
  the bucket's state timeout (20 s long, 5 s quick) + the tracking cap (10 s, sun trackers only) +
  capture (4 rows x 4 views x 2 clears, about 0.3 s per case, x 4) + teardown (5 s). Long bucket
  about 57 s, quick about 32 s, air (D.4) about 60 s (spawn 20 s, settle 0.5 s, semi 10 s and full
  15 s each followed by the 4 s canopy window, capture, teardown). Pure,
  `PartParitySweepPlan.WaveWorstCaseSeconds`.

### D.4 Parachutes in the air (owner ruling R1)

Where: in the FLIGHT scene with the host vessel sitting on the KSC launch pad (a pad fixture, the
`b1-pad-craft` kind; the step refuses a host that is not PRELAUNCH or LANDED within 500 m of the pad
with `host-not-at-pad`). The chute vessels spawn over the showcase band, the KSC strip Parsek's
synthetic showcase recordings use (`SyntheticRecordingTests.ShowcasePosition`: latitude -0.0972 +
row offset, about 200 m east of the pad), but 1600 m above sea level instead of the showcase's
116 m, through `VesselSpawner.SpawnAtPosition` (position, velocity, upright surface-relative
rotation). A wave is the 5 chutes of one state, 40 m apart along the band.

Why it unpacks: stock's flying vessel ranges are load 2250 m / unpack 2000 m (decompiled
`VesselRanges.flying`), and 1600 m up over a point 200 m from the pad is about 1.6 km from the host,
so no range override. The chutes never come near the host (they end the case over 1 km up).

Deterministic semi then full, with stock doing both:

1. Spawn at 1600 m ASL moving 10 m/s down (stock needs more than 1 m/s, `IsMovingFastEnoughToDeploy`),
   the module node authored with `deployAltitude = 1000` (a persistent field the player sets in the
   editor; the stock range is 50 to 5000). Settle 0.5 s.
2. Press `Deploy`. Stock arms the chute; at 1600 m the pressure (about 0.8 atm) passes
   `minAirPressureToOpen`, and the altitude is above `deployAltitude`, so stock goes SEMIDEPLOYED at
   once and plays `semiDeployedAnimation`. Done for `semi`: SEMIDEPLOYED and the animation finished
   (`Animation.isPlaying` false), then the canopy rule below; timeout 10 s.
3. For `full` only (its own case and spawn): after step 2's done, set `deployAltitude = 5000`, the
   same slider a player drags in flight (`guiActive = true`); the next `FixedUpdate` finds the part
   under it and stock goes DEPLOYED and plays `fullyDeployedAnimation`. Done: DEPLOYED, the animation
   finished, the canopy rule; timeout 15 s. The fall over a case is a few hundred metres.

The recorder's poll emits `ParachuteSemiDeployed` (and `ParachuteDeployed`) from the live
`deploymentState`, so the event route carries what a real descent records.

Ghost placement and camera: as in orbit (D3), the ghosts are parented to the falling real part's
root at zero offset and every capture is synchronous inside one frame, so the fall moves both sides
together. The capture camera uses the same part-local views (4.3) from the shared box of the real
part, its canopy and the ghosts; the culling mask is the parts' layers only, so the terrain, KSC
buildings and sky never enter a view (the backdrop stays the double clear). The light rig (D5) is
the same.

Wind and wobble: stock has no wind. The canopy follows the airflow and wobbles: each `FixedUpdate`
stock aims it at `LookRotation(+-dragVectorDir, upwards)`, rotates it by Perlin noise of
`Time.time` (up to +-5 deg on each axis) and slews toward that at `rotationSpeedDPS` (decompiled
`ModuleParachute.FixedUpdate`), while the ghost's canopy holds its sampled part-local pose. The
comparison handles it in two steps:

- Capture timing: after done, the case measures each frame the real canopy's airflow tilt (the
  angle between the part's +Y and the drag direction) and its wobble (the angle between
  `canopy.forward` and the un-wobbled aim), and captures at the first frame where their sum is
  under 3 deg, within 4 s; otherwise at 4 s, tagged `canopy-wobble:<deg>`. Both angles go into
  `stateProof.canopy`.
- Verdict tolerance: on an air case the structural diff reports the canopy renderers' rotation
  difference; when it is within the measured tilt plus wobble plus 1 deg, and the canopy's position,
  scale, mesh and rendered state match, the case's picture fails are capped at `warn`
  (`canopy-airflow`). A larger rotation, a wrong scale (the Mk16-XL's small canopy), a missing or
  extra canopy or cap still fail.

Own lanes: PP-5 (stock-minimal, label `pp5`) and PP-6 (modded-compat, label `pp6`), tier
`operator`, report-only, one `op=run contexts=air states=all` step each (about 1 minute of sweep).
They cannot share PP-3 / PP-4's flight: the orbit sweep needs an orbiting host, the air cases a
host at the pad, and a lane stages one fixture.

### D.5 The ghost side: three ghosts per state case

All three are built through the D2 seam (`GhostCoOrbitalRig`, `GhostPlaybackEngine.
InitializeBuiltGhostVisuals`) and primed through `GhostPlaybackEngine.PrimeHiddenPartStateAtUT`,
so no shortcut sets a pose:

| Row | Route | Snapshot | Part events | Primed | Plays like |
| --- | --- | --- | --- | --- | --- |
| `ghost` | event (judged) | stowed (step 2) | the stowed seeds at `t0` + the polled transition events at their UTs | at `tE` = the last event's UT + the clamped clip (`GhostPlaybackLogic.ClampDeployableClipSeconds`) + 1 s, so the animated deploy path (`UpdateActiveDeployables`) completes; then, for a part with a sun tracker, 10 more primes at `tE + 1 s, tE + 2 s, ... tE + 10 s` | a deploy recorded during a flight, at the end of its clip |
| `ghost-start` | start (judged) | deployed (after step 4) | the deployed vessel's start seeds at `t1` | at `t1` (the first frame a player sees); for a sun tracker, then 10 primes 1 s apart | a recording that starts deployed (a station, a split TIP, a Re-Fly) |
| `ghost-stowed` | reference (not judged) | stowed | stowed seeds only | at `t0` | the stowed ghost: whether the state is visible at all |

- Sun tracking: `GhostPlaybackLogic.UpdateSynthesizedMotion` seeds `prevSynthUT` on its first call
  and re-seeds and skips any step longer than `MaxSynthSampleSeconds` (1.0 s), so one late prime
  never tracks and a first-frame prime never does either. The primes therefore step 1 s at a time
  (10 steps cover 180 deg at the 20 deg/s slew); each repeat re-applies the already-applied events,
  which answer `AlreadyInState`. The start route is judged on the picture after its steps, and its
  first frame is recorded separately: `stateProof.startFirstFrame` reads, before any step, whether
  every deploy family is already at its target (the snapshot baseline applied) and the tracker's
  first-frame angle. A start route not at its pose at the first frame is the flag
  `start-pose-not-first-frame` (a recording that starts deployed would show its panels unfolding),
  a finding at `fail`; a tracker that starts away from its target is report-only
  (`sun-tracker-first-frame:<deg>`), since playback slews it in the first seconds by design.
- The rig gains `TryBuildGhostFromSnapshot(snapshot, recId, name, events)` beside
  `TryBuildGhostFromLiveVessel` (which becomes a thin wrapper), so the event route builds from the
  saved stowed snapshot after the real part has moved.
- A ghost that cannot represent the state is read from its own playback state after the prime,
  read-only (`PartParityStateRules.GhostStateReached` over the engine's info dictionaries:
  deployable fraction 1 and `currentDeployed`, parachute semi / deployed pose sampled and applied,
  cargo bay open, ladder extended, light on, jettison transforms hidden): `applied`, `no-info`,
  `pose-not-sampled`, `not-reached`. Anything but `applied` on a route is the flag
  `state-not-modelled:<route>:<outcome>`. The engine's per-family tally line
  (`GhostPartEventApplyTally.Flush`) is captured in the case log for the detail.
- The capture primitive (4.4) renders N sides instead of two, each with every other side's
  renderers `forceRenderingOff`, two clears each: about 0.3 s per case instead of 0.15 s.

### D.6 Verdicts, flags, setup faults, controls, gates

- Thresholds: the 6 thresholds, unchanged. Each judged route is judged like a stowed case (the
  view rules, the structural raise, the context and depth-mask caps, plus the air case's canopy
  cap of D.4); the case verdict is the worse of the two routes, each reason naming its route.
- Flags: `state-not-modelled:<route>:<outcome>` and `state-not-recorded` raise the case to at
  least `warn` (the B9 rule; the pictures still fail it when they differ).
  `start-pose-not-first-frame` raises to `fail`. `state-invisible` (real vs `ghost-stowed` has no
  view blob at the warn level: the state does not change the picture) is report-only. Tags:
  `still-tracking`, `canopy-wobble:<deg>`, `context-hidden:<n>`, `jettison-debris-hidden:<n>`.
- Setup faults (case `error`, never a ghost finding, complete for the completeness gate):
  `action-failed`, `state-timeout`, plus the existing ones; they join
  `TestCommandPartParitySweep.SetupStatuses` / `IsSetupFault` and the Python mirror in the same
  commit (S1).
- State controls. `ctl-state-events-dropped` (orbit; subject `longAntenna`, the Communotron 16): the
  full state case with the event-route ghost built from the stowed snapshot WITHOUT the polled
  events. Required: the press reached `deployed`, at least one event was recorded, the event-route
  views fail on shape, `stowedRef` fails too. `ctl-state-events-dropped-air`: the same on
  `parachuteSingle` at `semi` in the air. A misbehaving control is INVALID(parity-controls).
- Press gate (fail-open guard, new): per family with planned cases, when more than half of its
  cases (or its only case) end `action-failed` or `state-timeout`, the row is
  INVALID(parity-state-press), naming the family and its first detail. So a broken press, wait or
  poll path for one family can never read as a sweep of `error` cases; the state controls prove
  only the deployable and chute paths, the gate covers the rest. Ordered after the controls gate
  and before the recompute in `hlib.classify_verdict`.
- Recompute: the sample decodes rows 1 and 2 of a state case's atlas against row 0 with the full
  key set, and row 3 against row 0 for exactly the keys `stowedRef` carries (shape counts, the
  tolerant mismatch counts, the blobs and their percentages); nothing `stowedRef` does not carry
  is checked, and a missing `stowedRef` key is INVALID(parity-instrument).
- The other gates (host, completeness) are unchanged; the host guard reads the run's host baseline,
  ORBITING for an orbit run, PRELAUNCH / LANDED for an air run (`PartParityHostIntegrity`, S1).

### D.7 Viewer

- Grouping part > skin > state > view. Inside a skin, one sub-block per state in the order stowed,
  then the D.1 table order, each with its own verdict border and trigger line.
- Stowed and deployed side by side: each state row shows REAL | GHOST | GHOST-START | DIFFERENCE per
  view; the stowed column beside it comes from the paired stowed case (`stowedCaseId`) when its
  sweep is loaded, else from the case's own `ghost-stowed` row (labelled as a reference).
- Sweeps of one profile merge (`stock=<pp1 dir> stock=<pp3 dir> stock=<pp5 dir>`, or by the
  manifest's own profile once S3 fixes the `..` name, 8): a stowed sweep and its state sweeps join
  into one part list. Controls are exempt from the merge: each sweep's controls stay in that
  sweep's controls section, and no control id is ever a duplicate. A part case id present in two
  merged sweeps is an error line in the sweeps box; by D.2 a state sweep holds no stowed / alone
  case, so a correct pair never raises it.
- Filters: state (stowed / each state word), family, and "state flags" (`state-not-modelled`,
  `state-not-recorded`, `start-pose-not-first-frame`, `state-invisible`), beside verdict, source and
  search. Regression mode pairs by case id and route.

### D.8 Wall-time budget

Per case today: about 0.7 s at waves of up to 8 (spawn and settle dominate, Appendix C). Orbit state
waves hold 4, wait for the longest clip in the wave, and capture four rows:

| Lane | Waves and wait | Sweep estimate | Steps (480 s, worst-case wave admission) | `budgetSeconds` |
| --- | --- | --- | --- | --- |
| PP-3 stock | 57 animated cases in 15 long waves (about 9.5 s each); 41 lights in 11 quick waves (about 5 s); 66 shroud cases in 17 waves (about 4.5 s); 18 fairing cases (about 3 s each); 9 controls (about 25 s) | about 350 s | 2 | 1500 |
| PP-4 modded | 68 animated in 17 long waves; 55 lights in 14 quick waves; 108 shroud cases in 27 waves; 18 fairing; 9 controls | about 430 s | 3 (two hold it even at +50 %; the third answers `done=true` at once when not needed) | 2100 |
| PP-5 / PP-6 air | 2 waves of 5 chutes (semi about 12 s, full about 22 s); 4 controls (about 15 s) | about 50 s | 1 | 600 |

No state run re-plans a stowed case (D.2), so nothing of PP-1 / PP-2's 660 s / 963 s is repeated.
Uncertainty about +-50 % (clip lengths not measured): each lane's first flight is a `limit=20`
reading flight, and the step count is set from its `wall=`, as P2 did. The ids PP-3 to PP-6 are free
on `origin/main` and on every open PR branch (checked 2026-11-22). The four lanes need no parallel
slot: they run on the installs PP-1 / PP-2 use, one slot each, and two installs can fly at once.

### D.9 What P6 leaves ready for P7

The P7 strips (9.2) reuse D.1's table (family, module, done signal as t = 1), the event route's
stepped prime (a strip frame is a prime at a fraction of the clip), and the air context for the
canopy strip; the atlas keeps `rows` as a list and gains its frame axis as columns.

### D.10 Work breakdown

Rule for parallel work: two pieces in flight at once never touch the same file, and each one's
suites are green on its own. The Python suite mirrors C# literals (`harness/lib/test_partparity.py`
`PartParitySourceSyncTests` reads `PartParityJson.cs`, `TestCommandPartParitySweep.cs`,
`PartParityHostIntegrity.cs`, `PartParityMetrics.cs` and, today, `PartParityCaseCapture.cs` for the
atlas rows), so every mirrored literal and its Python mirror change together in S1, and no later
piece edits a mirrored file. Docs: each piece edits only its own D subsection (S1 D.1 / D.2, S3 D.3
to D.5, S4 D.6, S5 D.7, S6 D.8), so concurrent PRs never touch neighbouring lines.

| # | PR | Files | Runs alongside | Acceptance |
| --- | --- | --- | --- | --- |
| S1 | The contract, both sides: `PartParityStateRules` (D.1 families with the pollers' predicates, exclusions, words, the tiebreak, actions, done-signal readers over plain values, timeouts, `WaveWorstCaseSeconds`, `GhostStateReached` over plain inputs), the `states=` / `contexts=` args and refusals, `PlanCases`, `ControlSetFor`, the air host rule, the key tables, `AtlasRowsFor`, the setup statuses; AND the Python mirror of every one of them: `partparity.py` constants and readers (`control_set`, setup statuses, keys, rows, the press gate's family table), the source-sync cells pointed at the moved literal, hlib's arg tables and refusal reasons; the committed stowed-id literal pin | `Source/Parsek/TestCommands/PartParityStateRules.cs` (new), `TestCommandPartParitySweep.cs`, `PartParityJson.cs`, `PartParitySweepPlan.cs`, `PartParityHostIntegrity.cs`, `PartParityCaseCapture.cs` (the literal moves out), xUnit, `harness/lib/partparity.py` (constants and readers only), `harness/lib/test_partparity.py`, `harness/lib/hlib.py` (arg / reason tables) | S2 | `dotnet test` and the harness lib suite green; the id pin and a no-arg plan equal to today's; enumeration counts on fixture part configs match D.1 |
| S2 | Light poll extraction: the active recorder's per-part light body (ModuleLight and the toggle colour changer) moved to `PartStatePoller.PollLights`, called by `FlightRecorder.CheckLightState`; the background recorder untouched | `Source/Parsek/Recording/PartStatePoller.cs`, `FlightRecorder.PartModuleStates.cs` | S1 | `scripts/refactor/il_compare.py --fold` shows the moved body only; recorder tests green |
| S3 | Live orbit engine: `PartParityStateDriver` (press, poll into `PartParityPollSink`, done / timeout, sun-tracking settle, airbrake settle, EC authoring, debris hide and destroy), the three ghosts with the stepped primes, the N-side capture, `GhostCoOrbitalRig.TryBuildGhostFromSnapshot`, the jettison node contexts, the slot rule, worst-case wave admission, the manifest profile fix (8), the in-game `PartParity` cell's state filter | `Source/Parsek/TestCommands/PartParityStateDriver.cs` (new), `PartParityCaseCapture.cs`, `PartParityCapture.cs`, `PartParitySweepRunner.cs`, `Source/Parsek/InGameTests/GhostCoOrbitalRig.cs`, `PartParityInGameTests.cs`; no mirrored file | S4, then S5 | a dev-install Ctrl+Shift+T run over `longAntenna`, `solarPanels4`, `GearSmall`, `mk2CargoBayS`, `telescopicLadder`, `spotLight1`, `mk1pod.v2`, `airbrake1`, `liquidEngine2` (stacked) and `turboFanSize2` (top node): every case reaches its state, both routes write rows, no debris left after teardown, sidecars parse; FGV-1 still green |
| S4 | Harness row logic: route-aware `judge_case`, the D.6 flags and caps, the 4-row recompute with the `stowedRef` key subset, the press gate, the state controls' outcomes, verdicts JSON; the new INVALID class in `hlib.classify_verdict` | `harness/lib/partparity.py` (logic only), `harness/lib/test_partparity.py` (new cells, not the sync class), `harness/lib/hlib.py` (classify order) | S3 | lib suite green on synthetic sidecars: worse route, each flag, the recompute of rows 1 to 3, a mutated row reds INVALID(parity-instrument), a family of all `action-failed` reds INVALID(parity-state-press) |
| S5 | Viewer: D.7 grouping, merge by profile with controls exempt, the stowed column, filters, route-aware regression | `harness/tools/part_parity_view.py`, `harness/lib/test_part_parity_view.py` | S3 (after S4 merges: it calls S4's judge) | viewer tests green; a site from a synthetic stowed + orbit state + air state sweep set shows each part's states side by side with no duplicate-id line |
| S6 | Orbit lanes PP-3 / PP-4: specs, registry, lane catalog regen, hlib wiring cells, `autotest-status.md` rows | `harness/scenarios/PP-3-*.toml`, `PP-4-*.toml`, `harness/coverage/registry.toml`, `docs/dev/flight-profiles.md`, `docs/dev/autotest-status.md` | S7 | harness suites green; first flights on request (`limit=20`, then full), findings filed in the todo list |
| S7 | Air context: the pad host admission, the showcase-band spawn through `SpawnAtPosition`, the chute sequence, the canopy capture timing and its proof keys; lanes PP-5 / PP-6 | `Source/Parsek/TestCommands/PartParityAirContext.cs` (new), `PartParitySweepRunner.cs` (one dispatch hook), `harness/scenarios/PP-5-*.toml`, `PP-6-*.toml` and the S6 doc / registry rows after S6 merges | S6 (C# half); lane half after S6 | a dev-install run of `parachuteSingle` and `parachuteLarge` at semi and full: stock reaches each state, `ParachuteSemiDeployed` / `ParachuteDeployed` recorded, captured within the canopy window |
| S8 | FGV migration: FGV's six configurations as `fgv-<n>-<name>` contexts of the fairing family (and the engine plate / shroud skirts), with FGV's states; FGV-1 retired in its own PR after the sweep's fairing cases have flown green twice | `PartParityStateRules.cs` (context table: mirrored, so with its Python mirror), `PartParityCaseAuthoring.cs`, the FGV test file | after S3 | the 18 fairing cases match FGV-1's verdicts on the same DLL |

Order: S1 and S2 together; then S3 and S4 together; S5 once S4 merges (S3 may still run); S6 after
S3 and S4; S7 after S3 (its lane half after S6); S8 after S3. The C# PRs get the end-of-task
review; S2 carries its IL fold proof as its review evidence.

### D.11 Review disposition (rev 1 -> rev 2)

One clean-context review of rev 1; each finding was checked against the code or the decompile
before it was accepted.

| Finding | Check | Disposition |
| --- | --- | --- |
| A lone light switches itself off: `useResources`, no charge | decompiled `ModuleLight.FixedUpdate` (`SetLightState(false)` under 0.99) | Fixed: authored `ElectricCharge` on light cases (D.3) |
| Sun tracking never converges: first call seeds, steps over 1 s re-seed | `GhostPlaybackLogic.SynthesizedMotion.cs` (`MaxSynthSampleSeconds = 1.0`, the seed branch) | Fixed: 1 s stepped primes on both routes; the start route's first frame recorded apart (D.5) |
| A state run would re-plan every stowed case; the merge would flag every stowed id and control | the plan of rev 1 had no such rule | Fixed: a state run plans no stowed / alone case; the merge exempts controls; D.8 redone (D.2, D.7, D.8) |
| A new control breaks PP-1 / PP-2 (`ControlIds` fixed, `check_controls` needs every id) | `TestCommandPartParitySweep.cs` `ControlIds`, `ControlsState`; `partparity.py` `CONTROL_IDS`, `check_controls` | Fixed: the control set is a function of the args on both sides (D.2) |
| The parallel split was not disjoint (the Python sync mirrors C# literals; S5 needs S4) | `test_partparity.py` `PartParitySourceSyncTests` | Fixed: S1 carries every mirrored literal and its mirror, the rows literal moves to a pure file, S5 after S4 (D.10) |
| Thrown shrouds / panels stay as physical objects with colliders | decompiled `ModuleJettison.Jettison`, `physicalObject.maxDistance = 1000` | Fixed: hidden for the capture, destroyed at teardown and in `Cleanup` (D.3) |
| `ModuleAeroSurface : ModuleControlSurface` puts the airbrake in two families with one id | decompiled class header; `PartStatePoller` matches exact `moduleName` | Fixed: each family uses its poller's predicate; general same-word tiebreak (D.1, D.2) |
| Control surfaces: no Animation, sign from the CoM on a lone part | decompiled `ModuleControlSurface` (`MoveTowards`, `deflectionDirection`) | Dropped with reason `cs-deploy-sign`, left to P7 with an authored mirror context; airbrake kept with a settle rule (D.1, D.3) |
| The stacked context must use each module's `bottomNodeName` | decompiled `ModuleJettison` (`bottomNodeName`, `ignoreNodes`, `checkBottomNode`); configs: `top` on `turboFanSize2` / `InflatableHeatShield`, `bottom02` on ReStock's `HeatShield0` | Fixed, plus the `ignoreNodes` (alone) and `checkBottomNode` (no jettison when stacked) rules (D.3) |
| `ShouldStartWave` estimates from the last wave | `TestCommandPartParitySweep.ShouldStartWave` | Fixed: worst-case bucket admission (D.3) |
| Omission: the id pin must be a committed literal | CI has no archive | Fixed (D.2) |
| Omission: no fail-open guard on the press path | - | Fixed: the press gate INVALID(parity-state-press) (D.6) |
| Omission: what the recompute checks for `stowedRef` | - | Fixed: exactly the keys it carries (D.6) |

### D.12 Open questions for the owner

None blocking. The three questions of rev 1 are ruled (R1 to R3). One choice is left to the first
flights, not the owner: if the chute canopy window of D.4 (3 deg, 4 s) captures most cases tagged
`canopy-wobble`, the threshold is re-pinned from that reading run, like every other threshold.
