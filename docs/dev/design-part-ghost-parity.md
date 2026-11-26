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

0. Depth masks on the real part (`PartParityDepthMaskRepair`, todo
   PARITY-RESTOCK-MASK-ROOT-PART). Every case vessel is one part, so the case part is its vessel's
   ROOT part, and stock `ProtoPartSnapshot.Load(loadAsRootPart: true)` (decompiled, KSP 1.12.5)
   does not keep the instantiated clone: it moves the clone's children under the vessel's
   GameObject, adds a fresh `Part` and fresh modules there, copies every public field across by
   reflection, and `ConfigurePart` then `DestroyImmediate`s the clone. ReStock's
   `ModuleRestockDepthMask` resolves `bodyTransformObject` (the part's own transform when
   `bodyTransform` is empty, which is every ReStock patch) only at part compilation, so the copied
   reference names the destroyed clone root: its `OnStart` throws a NullReferenceException and the
   mask mesh keeps its authored placeholder material, an opaque cyan disc. Before anything reads the
   real renderers, every module whose name contains `DepthMask` is checked by reflection (no
   compile-time reference to any mod): when its body or a mask transform is not a live transform
   under the part, or its shader is dead, the transforms are resolved on the live part exactly as
   the module's own prefab-time `OnLoad` does and its `UpdateAllMaterials` re-runs, so the mask is
   depth-only at queue 1999 and the body renderers draw ahead of it, the look a player sees on a
   non-root part. A module is `repaired` when the re-run returns (the update sets the shader on
   every mask renderer or throws, so no separate check follows), and unrepaired when it lacks a
   member of that shape (`unsupported`), the live part has no mask transform or no shader
   (`unresolved`), or the re-run throws (`threw`). One `[PartParity]` line per case with such a
   module names each module's live and prefab reference states and the outcome: Warn when a
   module stayed unrepaired, Info when one was repaired, Verbose when every module was intact. The
   sidecar carries a count per kind, `depth-mask-repaired:<count>` and
   `depth-mask-unrepaired:<count>`. A case with an unrepaired module gets the setup status
   `depth-mask-unrepaired` (a setup fault, so the case is an `error`, never a ghost `fail`), and
   its setup detail names each such module. A part with no such module (every stock part) is not
   touched. The ghost still skips masks (owner ruling 2026-11-22). The module's own
   `onVariantApplied` / `onPartRepaired` handlers would re-run the update over every renderer
   under the part, the ghost's included once step 3 parents it there, but they cannot fire during
   capture: a module repaired for a dead transform threw in `OnStart` before subscribing them, the repair subscribes
   nothing, and `PartParityDepthMaskEventGateTests` reds on any capture-path file (the sweep, its
   rig, the ghost builder; comments stripped) that names either event, `SetVariant` or
   `ApplyVariant`.
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
| `ctl-real-vs-real` | `fuelTankSmallFlat`, real captured as both sides | identical masks, and at most 8 overlap pixels per view over the colour threshold (determinism; `RealVsRealColorNoisePx`, P5: two renders of one part read 1 px apart in 1 of 216 archived views, todo PARTPARITY-REAL-VS-REAL-ONE-PIXEL) |
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
  "setup": {"status": "ok|skin-not-applied|spawn-timeout|still-animating|died-settling|crashed|depth-mask-unrepaired", "detail": "..."},
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
inflight rule of 5.3), `skin-not-applied` (3.2), `depth-mask-unrepaired` (4.2 step 0). A case a damaged host left unrun has NO sidecar
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
| case `fail` | the worst view, raised to `fail` by a RENDERED entry in `structure.missingOnGhost` / `extraOnGhost` that is not non-visual (each entry's `nonVisual`: `depth-mask` for a depth-only shader, `fx` for a `Waterfall/` shader, both left out of the ghost by design; a sidecar without the key falls back to its path: a `Waterfall` segment or a leaf name containing `mask`), A case tagged `context-dependent` (wheels, suspension, legs, ground parts) caps a picture `fail` at `warn` with `context-pose`, since its orbital pose (suspension at full droop) is not the landed pose the ghost keeps by design (owner ruling 2026-11-22: no suspension modelling, and ReStock depth masks stay skipped on the ghost); a part whose real side carries a depth mask the ghost skips is judged like any other part, since the capture repairs the real mask to its depth-only look first (4.2 step 0; the `depth-mask` cap of 2026-11-22 was dropped once the repair flew, Appendix C); a structural or skin failure still fails. or by the skin proof's `skin-mismatch` (`partparity.skin_flags`, the exact rule of `PartParitySkinRules.GhostFlags`: a skin module with real != ghost case-insensitively, a null on one side only counting as a difference); a B9 case's unread ghost is `skin-not-modelled:b9`, a `warn`. The row also re-derives every part case's skin flags and reds `INVALID(parity-instrument)` when they differ from the sidecar's own `flags` |
| case `error` | a setup fault (`TestCommandPartParitySweep.IsSetupFault`: any status but `ok` and `still-animating`), the skin proof's `requested != real` (`skin-not-applied`), a real depth mask the capture could not repair (`depth-mask-unrepaired`, 4.2 step 0), or a non-fault case with no measured view; `still-animating` is judged on its measures |

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
- **Pinned regression mode** (`--pinned [<dir>]`, P5; default `harness/partparity`): the same
  filter and chips, but each sweep is compared with the COMMITTED baseline section of its
  manifest's profile and label and the accepted list, through `partparity.gate_sweep` itself: a
  case is `moved` when the armed gate would red on it, `new` when the baseline lacks it, else
  `same`; its baseline row lists every gate finding (accepted fails, near-line fails and moves to
  the better side as notes). `--pinned` and `--baseline` are exclusive.
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
blocks the pixel reads an encoder needs. The concrete P7 plan is Appendix G: it keeps the real
side's clip live (a sampled real side cannot show a ghost that starts late or snaps) and poses the
ghost afterwards at each real frame's exact UT.

### 9.3 Plumes and other FX

Real engine: activated on its independent throttle, `CheatOptions.InfinitePropellant` for the
step, held on a test stand so thrust never moves it. Ghost: built through the D2 seam, primed
hidden, then brought onto the screen exactly as production's first visible frame restores its
engine and RCS effects. Particles are stochastic: each effect is compared as coverage and colour
accumulated over many frames on every side after its warm-up, plus the live particle statistics
and a deterministic FX inventory, never single frames. On Waterfall installs the real plume is
`ModuleWaterfallFX` and the ghost falls back to pristine stock / ReStock particles, so those cases
are `reference` (shown, measured, never gated; owner ruling 2026-11-21), and a Waterfall-free
ReStock profile judges ReStock's own plumes (owner ruling 2026-11-22). Heat glow
(`ModuleAnimateHeat`) and the light a lamp casts are deterministic state cases; stock reentry and
blackbody glow have no real side the capture camera can see and get a later flight-camera
reference lane. The concrete P8 plan is Appendix E.

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
  `harness/lib/test_hlib.py`: empty as shipped, PP-1 to PP-4 from P5 (Appendix F).
- `[Parsek][ERROR]` stays forbidden by the log validator; an ERROR a modded part provokes is a finding
  to file, never a suppression.
- Verifier row `partParity` (`harness/lib/partparity.py`, pure): controls, completeness, verdicts,
  the Python recompute sample, and from P5 the baseline compare against
  `harness/partparity/baseline-<profile>.json` (raw measures only) and
  `harness/partparity/accepted.toml` (case / view / measure, reason, todo id). Once armed
  (`gating = true`, the saveparse arming rule), a newly failing case or a measure moved past its
  tolerance reds `PARSEK-FAIL(part-parity)`.
- Each ghost-visual defect the sweep finds is filed in `docs/dev/todo-and-known-bugs.md` with its
  case id; the fix removes the accepted entry and re-pins the baseline in the same commit.
- As built (P5, 2026-11-23). The baseline is `harness/partparity/baseline-<profile>.json`, one
  section per sweep label (`pp1` / `pp3` / `pp5` on stock-minimal, `pp2` / `pp4` / `pp6` on
  modded-compat). A section names its reading runs and their DLL hashes and holds, per planned part
  case, the setup statuses seen and, per judged view (a state case's routes as `event:<view>` /
  `start:<view>`), the band `[lo, hi]` over those runs of four raw measures: `realOnlyBlobPx`,
  `ghostOnlyBlobPx`, `colorBlobPx`, `iou` (a band equal to the identical-sides value, 0 px or
  IoU 1, is left out). No verdict is stored: verdicts are recomputed from the sidecars, so a
  threshold re-pin needs no new baseline. Controls are not baselined (their recorded outcomes are
  the instrument gate). `harness/partparity/accepted.toml` (`schema = 1`, `[[accepted]]` rows:
  `case`, optional `profile`, `view` (`*`, a view, or `event:` / `start:` + view), `measure`
  (`shape-blob`, `color-blob`, `iou`, `structure`, `skin`, `hidden`, `drawn`, `flag:<flag>` with an optional
  trailing `*`, or `*` for every picture measure and nothing else), `reason`, `todo`, optional `noisy`) names every known difference; a
  placeholder reason or todo is refused, and a cell checks every todo id against the todo headers.
  The gate (`partparity.gate_sweep`, run by the row on every sweep, gating only when armed) reds
  `PARSEK-FAIL(part-parity)` on: a measure that left its band past its tolerance on the WORSE side
  (`drift`; more blob pixels, a lower IoU), unless a `noisy` entry covers it; a fail reason no
  entry covers when the reason is qualitative (a missing / extra rendered mesh, a skin mismatch,
  a fail-floor state flag) or the case has no baseline (`unaccepted-fail`); a baselined case the
  sweep no longer plans (`gone`); no section for the label or an unreadable file (`no-baseline`).
  A numeric fail on a baselined case inside its band's tolerance is a `near-line-fail` note: a
  case that sits near a fail line in every run is judged by the drift rule, so colour noise near
  the 256 px line cannot flake the gate. A non-noisy entry waives only the fail verdict, so an
  accepted difference that grows still reds. Hidden renderers, unbuilt ghosts and `state-not-*` flags red whatever the verdict, and a
  baselined case that could not be measured makes the armed row INVALID(parity-incomplete)
  (Appendix F). Moves to the better side,
  new passing cases and entries that matched nothing are notes, written to
  `partparity-verdicts.json` (`gate`) and the summary line (`gate=ok|red reds= drift= ...`).
  Tolerances and their measured basis: Appendix F. The writer and checker is
  `harness/tools/part_parity_baseline.py` (`write` refuses while a reading run holds an unaccepted
  fail, printing a TOML block per reason with the reason and todo left empty; `check`, `spread`,
  `accepted`).

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
| `Source/Parsek/TestCommands/PartParityDepthMaskRepair.cs` | Pure decisions (module rule, reference states, needs-repair, the prefab-time body rule, outcome, flag, summary line) + the live reflection repair of 4.2 step 0. |
| `Source/Parsek/TestCommands/PartParityHostIntegrity.cs` | Pure: the host-integrity guard of 4.1 (the damage decision, its detail and Warn line, `host.txt` / `hostdamage.txt`) and the case-vessel-lost rule. |
| `Source/Parsek/TestCommands/PartParitySweepRunner.cs` | Live: the step and wave coroutine (`RunStep(args, onDone)`), shared by the seam verb and the in-game cell. |
| `Source/Parsek/TestCommands/ParsekTestCommandAddon.PartParitySweep.cs` | Thin applier: starts `PartParitySweepRunner.RunStep` and answers the seam. |
| `Source/Parsek/Ghost/GhostPlaybackEngine.*` | The extracted build-and-initialise seam (D2), `GhostPlaybackEngine.InitializeBuiltGhostVisuals(state, buildResult, traj)`, the engine's own load path (`TryPopulateGhostVisuals`) rewired through it. Proof of no behaviour change: `il_compare.py` before / after reports only the new method and the caller's body, and inlining the seam's body back at the call site gives the caller's old body exactly (`--fold` models moves between types, not an extract-method). The loop-reuse re-init (`SpawnDestroy`) and the KSC spawn run other subsets (no pivot; KSC passes no trajectory) and stay as they are. Activation stays the engine's deferred `ActivateGhostVisualsIfNeeded`. P2 adds the seed-prefix prime (4.2 step 3): `ApplyPartStateForFrame` and `StopVisualFxForFrame` extracted from `ApplyFrameVisuals` (proved the same way: the multi-seam inline fold of both gives the old body), composed by `PrimeHiddenPartStateAtUT`. |
| `Source/Parsek/Ghost/GhostVisualBuilder*`, `GhostTypes.cs` | The applied-skin record (P3): `AddPartVisuals` hands out a `GhostAppliedSkin` (variant name + resolution path) for each part with a `ModulePartVariants`, `AdvanceTimelineGhostBuild` keys it by persistentId on the pending build, `CompleteTimelineGhostBuild` moves it onto `GhostBuildResult.appliedSkins`, read through `TryGetAppliedSkin`. Read-only: nothing in the build or playback reads it. Proof of no behaviour change: `il_compare.py` before / after reports only the new type, the two fields, the accessor and the bodies of `AddPartVisuals`, `AdvanceTimelineGhostBuild`, `CompleteTimelineGhostBuild` and the pending build's constructor, and each of those bodies is the old one with instructions INSERTED only (locals mapped back, every branch target checked through the instruction map; the one substitution is the call's new `out` parameter). |
| `Source/Parsek/InGameTests/GhostCoOrbitalRig.cs` | `SpawnCoOrbital` (log tag passed in), `UniqueFlightIds`, `AuthorPartNode` (writes `moduleVariantName` and the `ModulePartVariants` `selectedVariant`), `TryBuildGhostFromLiveVessel` (snapshot, throwaway recording, timeline build, the D2 seam) and `NeutralisePhysics`: moved out of `FairingGhostVisualInGameTests`, used by both, FGV's nodes and log lines unchanged. |
| `Source/Parsek/InGameTests/PartParityInGameTests.cs` | Ctrl+Shift+T entry. |
| `harness/lib/partparity.py` + tests | Pure manifest reader, verdicts, recompute, verifier row, baseline compare. |
| `harness/tools/part_parity_view.py` + `harness/lib/test_part_parity_view.py` | The viewer (8); the GIF / APNG writer later (9.2). |
| `harness/partparity/baseline-<profile>.json`, `harness/partparity/accepted.toml` | P5: the committed baseline and accepted list (10, Appendix F). |
| `harness/tools/part_parity_baseline.py` + `harness/lib/test_partparity_gate.py` | P5: the baseline writer / checker / spread tool; the gate's tests and the committed-file cells. |

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
| P5 (DONE 2026-11-23, Appendix F) | Baseline + accepted list; gate armed after a second matching reading run. BUILT: `partparity.gate_sweep` (10), `harness/partparity/baseline-<profile>.json` for all six labels, `accepted.toml` (72 entries), `part_parity_baseline.py`, the viewer's `--pinned` mode (8), the real-vs-real control's noise allowance (4.5). PP-1 to PP-4 armed; PP-5 / PP-6 report-only (canopy airflow). | A deliberately broken ghost (mutation) reds the armed lane. |
| P6 | States and contexts (9.1); FGV configurations migrated. PLANNED 2026-11-22 (Appendix D, rev 2): 12 transformation families, two ghost routes per state case, the jettison node contexts, chutes deployed for real in the air near KSC, the FGV fairing contexts, new report-only lanes PP-3 / PP-4 (orbit) and PP-5 / PP-6 (air). | Chute and deployables covered: every in-scope state case on both installs has a sidecar or a recorded setup fault, the state controls behave, the press gate holds, the owner reads stowed and deployed side by side. |
| P7 | Animation strips + GIF / APNG (9.2). PLANNED 2026-11-26 (Appendix G, rev 1): motion cases for the P6 families reached by motion plus servos and control surfaces; the real clip live, frames every T / 8 from the press, the ghost primed afterwards at each real frame's UT (same time) and at the same progress; timing measures (lag, duration ratio, curve shape, snap, steps) judged, per-frame pictures report-only; timing controls (real-vs-real, slowed and late ghosts); new report-only lanes PP-11 / PP-12 and an air label in PP-5 / PP-6; four motion defects read from the code (G.6). | Antennas, panels, robotics scrubbed in lock-step. |
| P8 | Plumes and FX (9.3). PLANNED 2026-11-22 (Appendix E, rev 2): engine plumes and smoke, RCS puffs (the ghost's all-nozzle firing a known difference, owner ruling R4), engine and RCS glow (body atlas), `ModuleAnimateHeat` glow, the light a lamp casts on a plate; stochastic effects compared as accumulated coverage and colour from 8-bit tiles with split-half convergence and broken-plume controls; Waterfall cases reference-only; a Waterfall-free `modded-restock` profile; new report-only lanes PP-7 / PP-8 / PP-9 (orbit), PP-10 and a jet label in PP-5 / PP-6 (air); a later flight-camera reentry reference lane (R1). | Engine plumes compared on stock and on ReStock without Waterfall; reference-only on Waterfall: every in-scope FX case on every install has a sidecar or a recorded setup fault, the FX controls behave, the owner reads real and ghost plumes side by side. |

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
| - / `2026-11-22_1157_PP-2-part-parity-modded_run2` | not flown (no stock part has a depth-mask module) | PASS, 1510 cases: pass 1458, warn 50, fail 2, error 0 (depth-mask repair) |

The first pair broke its host: the clone the ghost builder instantiates to sample a part's heat
animation kept its collider into the next physics step and pushed the host apart (fixed, PR #2744;
the host guard of 4.1 followed). The reading pair `_2111` / `_2123` traced to 10 ghost defects
(PR #2745), the later pairs to 6 more (PR #2749), plus the detector rulings: depth masks and
Waterfall plumes are non-visual, parts whose look depends on context and (until the repair below)
depth-mask parts cap at warn, and the colour line threshold is re-pinned 64 -> 256 px. The one `error` (`launchclamp1`,
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

Known non-defects: the ghost skipping masks stays (owner ruling 2026-11-22); suspension droop is left
by ruling.

The depth-mask repair (4.2 step 0, todo PARITY-RESTOCK-MASK-ROOT-PART), PP-2 `2026-11-22_1157_PP-2-part-parity-modded_run2`
(PASS attempt 1, automation DLL sha256 prefix 5b83e100 on both installs). ReStock's
`ModuleRestockDepthMask` still throws in `OnStart` on every one-part case vessel (86 modules on 74
cases, the same count as `_0045`): stock's root-part rebuild hands it a destroyed body transform.
The repair line fired for every one of the 74 cases, each module reading `live: body=destroyed`
with its masks and shader live and the prefab module intact (`prefab: body=in-part`), and the
re-run gave every mask its shader (the flown build re-checked the mask shader by NAME, so `airScoop`,
whose patch names it `Depth Mask`, logged `not-depth-shaded` and flagged `depth-mask-unrepaired:1`
although its pictures pass; that post-run check is gone, see 4.2 step 0). Of the 59 cases `_0045` capped at warn with `depth-mask`, all 59
now pass on their pictures alone (the colour blobs of up to 82624 px and the real-only shape blobs
of up to 11394 px were the opaque cyan masks); with the 15 that already passed, 72 of the 74 pass
and 2 warn (`vernierengine`, `skin-not-modelled:b9`). So the `depth-mask` cap is dropped from
`partparity.py`: a mask the repair cannot fix makes the case the setup fault
`depth-mask-unrepaired`, an `error`. The review follow-ups (the outcome set, the setup fault,
both flag counts, the Warn line, the event gate) are unflown: they change no verdict of this run's
74 repaired cases. Run to run, the non-mask cases moved inside the colour-line noise (`heatshield3--red` and
`rotor.01--gray` fail -> pass, `roverbody.v2--white` warn -> fail at 289 px `top`); the 2 fails are
`mediumdishantenna--default` (1018 px `top`, GHOST-PARITY-RESIDUALS-2026-11-22) and that rover body.

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
| Animation group, in-orbit parts | `moduleName == "ModuleAnimationGroup"`, every module (`PollAnimationGroups`), not on a `ModuleGroundPart` part, not `autoDeploy` | `deployed` | KSPEvent `DeployModule` | `TryClassifyAnimationGroupState` deployed AND the module's own deploy clip finished (D.3) | `DeployableExtended` midx | 4 / 4 |
| Cargo and service bay | `FindModuleImplementing<ModuleCargoBay>` whose `DeployModuleIndex` (default 0) is a `ModuleAnimateGeneric` (`PollCargoBays`) | `open` | the linked module's KSPEvent `Toggle` | `ClassifyCargoBayState` open | `CargoBayOpened` | 8 / 9 |
| Standalone animate generic (doors, inflatables, port shields, Klaw, science doors) | `as ModuleAnimateGeneric` with an `animationName` (default `default`), on a part where `FlightRecorder.HasDedicatedAnimateHandler` is false (`PollAnimateGeneric`) | `deployed` | KSPEvent `Toggle` (a hidden event too; the proof records `eventVisible`) | `TryClassifyAnimateGenericState` deployed | `DeployableExtended` midx | 12 / 17 |
| Airbrake | `moduleName == "ModuleAeroSurface"`, every module (`PollAeroSurfaces`) | `deployed` | KSPAction `ActionToggleBrakes` (Activate) | `TryClassifyAeroSurfaceState` deployed, then the surface transform still for 0.5 s (D.3) | `DeployableExtended` midx | 1 / 1 |
| Light | `FindModuleImplementing<ModuleLight>` (the recorder's light check, D.10 S2) | `lit` | KSPEvent `LightsOn`, with electric charge authored (D.3) | `isOn`, then the lens fade: `lightBrightenSpeed` + 0.5 s in game time (3 s at stock's 2.5 s; at least 1 s, cap 5 s) | `LightOn` | 15 / 18 |
| Cabin light (colour changer) | `as ModuleColorChanger` with `toggleInFlight` (default true), the first on a part with no `ModuleLight` (same check) | `lit` | KSPEvent `ToggleEvent` | `animState` true, then `1 / animRate` s plus 0.5 s (cap 5 s) | `LightOn` | 26 / 37 |
| Parachute | `FindModuleImplementing<ModuleParachute>` (`PollParachutes`) | `semi`, `full`, context `air` | KSPEvent `Deploy` in the air, then the in-flight `deployAltitude` tweak for `full` (D.4) | `deploymentState` SEMIDEPLOYED / DEPLOYED (`ClassifyParachuteState`) and the deploy animation finished | `ParachuteSemiDeployed`, then `ParachuteDeployed` | 10 / 10 |
| Engine shroud and other jettisons | every `as ModuleJettison` module (`PollJettisons`), one case set per part | `jettisoned`, context by node rule (D.3) | KSPEvent `Jettison` on every jettison module of the part | `isJettisoned` on every module | `ShroudJettisoned` | 37 / 60 parts, 66 / 108 cases |
| Procedural fairing | `FindModuleImplementing<ModuleProceduralFairing>` (`PollFairings`) | FGV's states (`stowed` = intact, `deployed`, `separated`) in the four FGV fairing contexts | KSPEvent `DeployFairing`; FGV's own separation | `GetScalar >= 0.5`; the separated part off the vessel | `FairingJettisoned` | 9 / 9 (S8) |

The counts were measured by S1 (2026-11-22) and are pinned by xUnit
(`StockMinimal_EnumeratesTheD1Counts`, `ModdedCompat_EnumeratesTheD1Counts`) over committed
fixtures (`Source/Parsek.Tests/Fixtures/PartParity/state-parts-<profile>.txt`: every part of the
archived PP-1 / PP-2 manifests that carries a module the rules read, with its modules in prefab
order, their reflected type chains and the config keys the rules read, derived once from each
install's ConfigCache). Rev 2's planning estimate differed in three rows: the deployable row adds
the Magnetometer Boom (its module is `ModuleDeployablePart` itself, 18 / 21 -> 19 / 22); the cargo
row loses the three / five service modules, whose `ModuleCargoBay` deploys a `ModuleServiceModule`
the poller skips (their shells are the jettison family's ignore-nodes cases; 11 / 14 -> 8 / 9); the
standalone animate generic row lost the two thrust reversers (`JetEngine`, `turboFanSize2`), whose
parts carry `ModuleAnimateHeat`, which `HasDedicatedAnimateHandler` then counted as a dedicated
handler, so the recorder never polled them (12 / 17 -> 10 / 15). Todo THRUST-REVERSER-NOT-RECORDED
took `ModuleAnimateHeat` out of that list (it drives only its own heat clip and owns no
`ModuleAnimateGeneric`), so the reversers are cases again (10 / 15 -> 12 / 17). The fairing row's cases arrived with
the FGV context table (D.10 S8, below); the plan's "18" was FGV's six configurations times three
states, but FGV photographs only 16 phases and three of them pair up as the two routes of one
case, so the migration is 13 cases.

FGV contexts (BUILT, S8, 2026-11-22). FGV-1's six configurations are the contexts
`fgv-<n>-<name>` (`PartParityStateRules.FairingContexts`, mirrored as `partparity.FAIRING_CONTEXTS`;
the per-context table is `FgvContextTable`), each bound to the one part it is the subject of, with
FGV's phases as state words: `stowed` (FGV's intact: no press, two atlas rows), `deployed` (FGV's
jettisoned: KSPEvent `DeployFairing`) and the new word `separated` (FGV's separation: the engine
plate's own `ModuleDecouple.Decouple`, else the leaving part's `Part.decouple`, exactly FGV's
calls; the interstage deploys its fairing first, as FGV does). A separated case's two ghost routes
are FGV's two separation images: the event route (the stowed snapshot plus the polled events) is
`fgv-n-separated`, the start route (the post-separation snapshot) `fgv-n-separated-branch`. Done
for `separated` is the leaving part no longer on the case vessel; the new vessel is killed then
(and at teardown).

| Context | Subject | Family | States | FGV images covered |
| --- | --- | --- | --- | --- |
| `fgv-1-plain-capped` | `fairingSize1` | fairing | stowed, deployed | intact, jettisoned |
| `fgv-2-interstage-truss` | `fairingSize2` | fairing | stowed, deployed, separated | intact, jettisoned, separated, separated-branch |
| `fgv-3-open-top` | `fairingSize2` | fairing | stowed, deployed | intact, jettisoned |
| `fgv-4-colour-variant` | `fairingSize1` | fairing | stowed, deployed | intact, jettisoned |
| `fgv-5-engine-plate` | `EnginePlate2` | jettison | stowed, separated | intact, separated, separated-branch |
| `fgv-6-engine-shroud` | `liquidEngine2.v2` | jettison | stowed, separated | intact, separated, separated-branch |

The two skirts are jettison-family cases (their state is the shroud leaving with the part below),
so the fairing row counts 9 and the jettison row 66 + 4 / 108 + 4. The craft are FGV's own
(`Parsek.InGameTests.FairingGhostAuthoring`, moved out of the FGV test file, which now calls it):
the subject is the vessel's root (the engine shroud configuration is authored engine-rooted, the
same craft), every other part is the context, hidden on every side like a stacked filler, and the
colour-variant configuration's chosen variant becomes the case's real variant (the skin proof
requests it; the id keeps the base skin, ruling R2). A deployed fairing's panels join the jettison
transforms (hidden once thrown, destroyed at teardown). `contexts=fgv` selects all six
(`PartParityStateRules.ContextClass`), and an orbit run with `contexts=none` plans them. A fairing
part no context names (`fairingSize1p5`, `fairingSize3`, `fairingSize4`, the three ReStock bases)
is excluded `fairing-no-context`; its stowed case still runs in PP-1 / PP-2. The structural diff
drops the ghost's `fairing_panels` container from a ghost path (`PartParityStructure.NormalizePath`),
so an intact fairing's stock panels pair with the live ones, which sit directly under the model.

Live (S8, 2026-11-22, stock-minimal, one automation DLL, sha256 prefix 4282a6db, on both installs):
two reading runs of a local `contexts=fgv states=all controls=true` spec copied from PP-1
(`2026-11-22_1758` and `_1806`, both PASS, partParity REPORT, controls ok, complete, recompute 10 / 0
mismatches, press ok) read the 13 cases 12 `pass` and 1 `warn` each, identically: every deployed
and separated case reached its state with its recorder event (`FairingJettisoned`,
`ShroudJettisoned`) and both ghost routes `applied`; the warn is
`fairingsize2--white--stowed--fgv-3-open-top` (a 140 px colour seam on side-a, todo
GHOST-FAIRING-OPEN-TOP-SEAM). FGV-1 on the same DLL (`2026-11-22_1800`, PASS, `total=2 passed=2`)
matched every one of its 16 phases. FGV-1 is retired in its own PR (D.10 S8).

Same-word tiebreak (general, in the id rule, D.2): when two or more of one part's cases share a
state word and a context (two standalone animate generics, two animation groups, or any future
pair), EVERY one of them writes `<word>-m<moduleIndex>`, so no id depends on which module was found
first. No part of either install hits it today.

Totals (each step logs the exact ones, D.2):

| Lane | Single-part states | Shroud cases | FGV contexts | Chute cases (air) | Controls | Cases |
| --- | --- | --- | --- | --- | --- | --- |
| PP-3 (stock, orbit) | 95 | 66 | 13 (S8) | - | 9 | 183 |
| PP-4 (modded, orbit) | 119 | 108 | 13 (S8) | - | 9 | 249 |
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
  cargo bay's own deploy module (no part of either install since todo THRUST-REVERSER-NOT-RECORDED,
  which made the two thrust reversers cases).
- `no-animation`: a `ModuleAnimateGeneric` whose config sets an empty `animationName`.
- `p7-robotics`: servos, rotors, pistons (`BaseServo`, 22 parts) and `ModuleRobotArmScanner` (3; a
  `ModuleDeployablePart` that extends only while scanning a surface feature): continuous positions,
  the P7 strips (9.2).
- `p8-heat`: `ModuleAnimateHeat` glow (16 / 13) and `ModuleAblator` char: temperature-driven
  material properties, P8 (9.3). `p8-fx`: engine plumes and RCS (each `ModuleEngines` /
  `ModuleRCS` module); light blink is part of the light case.
- `fairing-no-context`: a fairing part no FGV context names (3 stock, 6 modded; S8 replaced the
  interim `fairing-contexts-pending`).
- `auto-deploy`: an animation group with `autoDeploy = true` (the stock ISRU, 1 / 0: ReStock's ISRU
  carries no `ModuleAnimationGroup`). Stock deploys it itself and hides both events and the three
  actions (decompiled `ModuleAnimationGroup.OnLoad`: `isDeployed = true`), so the player never
  sees it stowed and there is nothing to press; its stowed case judges the only pose it has
  (todo PARTPARITY-ANIMATION-GROUP-PRESS; the first flight read it `action-failed`).
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
  full, jettisoned, separated>` (refused `states-arg-invalid`) and `contexts=none|<csv of alone,
  stacked, air, fgv>` (refused `contexts-arg-invalid`; `air` stands alone, since the air cases need
  a host at the pad and the orbit cases an orbiting one; `fgv` selects every FGV context, D.1,
  added by S8 with the word `separated`). Both default to `none`. An air run (`contexts=air`) is
  admitted only with a host PRELAUNCH or LANDED within 500 m of the pad, else REJECTED
  `host-not-at-pad`; every other run keeps `host-not-orbiting`. S1's interim `state-runs-not-built`
  refusal was dropped with its hlib mirror when S3 built the live orbit engine, and the interim
  `air-runs-not-built` refusal (`AirRunsBuilt`, hlib `PARTPARITY_AIR_RUNS_BUILT`) when S7 built the
  air context: a well-formed `contexts=air` step parses like any other and meets the pad rule at
  the admission, so no chute is pressed in orbit; the pure planning rules read `TryParseGrammar`.
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
    `wobbleDeg`: air cases, airflow tilt and wobble at capture, D.4; `transform`: the module's
    `canopyName`, added by S7), `nodes` (`node`, `part`: the
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

- Sun trackers (the ghost's own rule, `GhostVisualBuilder.TryBuildSunTrackingInfo`: any
  `ModuleDeployablePart` with `isTracking` whose `pivotName` transform resolves, which is also when
  stock tracks, `hasPivot`; the 9 tracking solar panels and the 3 folding radiators per install,
  whose `isTracking` is the `ModuleDeployablePart` default): the done rule adds "pivot rotation
  changed by less than the tracker's still threshold over 0.5 s", with a cap from the part's
  slowest tracker's own `trackingSpeed`. The threshold is the turn the slew makes in 0.5 s with
  0.85 deg still to go, `0.85 (1 - exp(-0.5 trackingSpeed))` (`SunTrackerStillDegFor`): 0.1 deg
  at 0.25, 0.04 deg at 0.1, so every tracker is captured within 0.85 deg of its aim. The window
  angle is computed exactly (`PreciseAngleDeg`, `2 atan2(|v|, |w|)` in double): Unity's
  `Quaternion.Angle` reads 0 below about 0.16 deg (its `IsEqualUsingDot`), which let a radiator
  read still while 3 deg short of its aim (PP-3 `2026-11-22_1946`: real 104.6 deg, ghost 107.9). Stock slews by `Lerp(current, aim, deltaTime * trackingSpeed)` (decompiled
  `CalculateTracking`), an exponential approach with time constant `1 / trackingSpeed`, so the
  cap is `25 s x 0.25 / trackingSpeed` (`SunTrackerCapSecondsFor`, at most 90 s): 25 s for a
  solar panel at 0.25 (re-pinned from 10 s by S6: the worst 180 deg needs about 21.4 s to meet the
  still rule; at 10 s every tracking panel of PP-3's first full flight was 1 to 14 deg short),
  62.5 s for a radiator at 0.1 (todo PARTPARITY-RADIATOR-TRACKING: before, only
  `ModuleDeployableSolarPanel` counted, so a radiator was captured about 1.3 s after its done
  signal with the real pivot mid-slew). A tracker still moving at the cap is captured and tagged
  `still-tracking` (judged, like `still-animating`). Both sides see the same Sun. The ghost side is
  D.5 (the stepped primes cover 180 deg at the ghost's 20 deg/s, so a radiator's event route ends
  at the aim too).
- Animation groups: the classifier reads `isDeployed`, which `DeployModule` sets before its clip
  plays, so the done signal also waits for the module's own deploy clip (`DeployAnimation`, not
  playing or at its end; `PartParityStateObservation.DeployClipDone`), and the timeout reads that
  clip at speed 1 (the part's longest clip was parked at the snap speed -1000 on the drills, which
  gave a 2 s timeout). ReStock's radial drill (10.7 s clip) was captured half out before (todo
  PARTPARITY-ANIMATION-GROUP-PRESS). An `autoDeploy` group is excluded (D.1).
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
  a `lit` case compares the lens and window emission, not the beam. Stock fades the lens in over
  `lightBrightenSpeed` GAME seconds (decompiled `ModuleLight.FixedUpdate`: `_EmissiveColor =
  Lerp(black, lightColor, currentTime / lightBrightenSpeed)`; 2.5 on every stock and ReStock light
  cfg), so a light settles for its own `lightBrightenSpeed` + 0.5 s, at least 1 s and at most 5 s
  (`LightSettleFor`), measured in game time as well as real time. The old 1 s settle photographed
  the real lens mid-fade (PP-3 `_1656`: the ghost / real lens ratio 1.87 on `spotlight1` captured
  1.34 s after the press, 2.35 on `spotlight2`, 1.0 on lights captured 5 to 7 s after).
- Colour-changer leak on the real part (`PartParityColorChangerLeakRepair`, todo
  PARTPARITY-STACKED-SHROUD-TOP-COLOR): stock `ModuleColorChanger.UpdateColor` writes its colour
  into the part's shared `Part.mpb` and applies it to its own renderer list only (the one its
  `excludedRenderer` / `includedRenderer` keys filter), but `Part.SetOpacity` and the part
  highlight apply that same block to EVERY renderer of the part (decompiled), so an excluded
  renderer shows the colour once one of those writes runs after the colour changer's, and not
  before, and which came last differs run to run. ReStock's heat shields exclude their jettison
  fairing from the ablation char; the stacked stowed case (the fairing shown) photographed the
  real fairing at the full-ablator char (0.85 of its texture, the top face 153k to 161k px) in some
  runs and plain in others, while the ghost honours the exclusion every time. Before the real
  renderers are read (both preparation branches), every real mesh renderer (the set the capture
  records) outside a colour changer's
  list whose property block holds exactly that module's current colour (the value `Part.mpb`
  holds) and whose material's own value differs gets the property set back to its material's
  value, the look the module's own filter gives; the case carries `colorchanger-leak-repaired:<n>`
  (report-only) and one Info line names the renderers.
- Slots: a deployed part reaches past its stowed bounds and the slot plan reads stowed bounds, so
  an orbit state wave uses every other slot (up to 4 cases, 80 m apart, 60 m or more from the host).
- Step time box: a wave starts only when `elapsed + worstCase(bucket) <= maxSeconds` (480, under the
  540 s seam cap), where `worstCase` is the wave's bound, not the last wave's wall: spawn cap 20 s +
  the bucket's state timeout (20 s long, 5 s quick) + the longest settle cap of the wave's families
  (0.5 s, an airbrake, a light or a cabin light 5 s) + the slowest tracker's cap of the wave (25 s
  for a solar panel, 62.5 s for a radiator, none without a tracker) + capture (4 rows x 4 views x 2
  clears, about 0.3 s per case, x 4) + teardown (5 s). Long bucket about 47 s (about 72 s with a
  solar panel, about 109 s with a radiator), quick about 32 s (about 36 s with an airbrake, a light
  or a cabin light), air (D.4) about 60 s (spawn 20 s, settle 0.5 s, semi 10 s and full 15 s each
  followed by the 4 s canopy window, capture, teardown). Pure,
  `PartParitySweepPlan.WaveWorstCaseSeconds`.

BUILT (S3, 2026-11-22): `Source/Parsek/TestCommands/PartParityStateDriver.cs` (live) and
`PartParityStateWave.cs` (pure, xUnit `PartParityStateWaveTests`). A state run's waves hold one
bucket each: `PartParityStateWave.PickWave` takes the case at the cursor and the next cases of the
same `state:<bucket>` kind further down the queue (stable partition, `BringToCursor`), a stowed wave
is today's leading run unchanged; slots `PlanStateWave` (+60, -60, +140, -140); admission
`AdmitWave` (a stowed wave keeps `ShouldStartWave`; the first state wave of a step always
starts, since the elapsed time already counts the planning). The phase runs after the stowed settle, per
case under the runner's guard: stowed inputs (snapshot, `PartTrackingSets` seeded, seeds at `t0`,
the part's `ModuleJettison.jettisonTransform`s, the pressed modules' KSPField signature), the press
(`BaseEvent.Invoke` / `BaseAction.Invoke(None, Activate)`, every jettison module; `eventVisible` is
the event's `guiActive && active`), then every frame the pollers (every `PartStatePoller` family
into `PartParityPollSink`), the done signal, refused (signature unchanged and no Animation playing
two frames on), the timeout, the settle (no Animation playing for the family's minimum, capped at
the 5 s stowed cap and tagged still-animating; a sun tracker's `panelRotationTransform` and an
airbrake's `transformName` transform sampled each frame for the stillness window). A throw in any
of these stages is the case's `action-failed` (`actionResult = threw`). The stacked context authors
the subject as the root and `fuelTankSmallFlat` on each node by its opposite stack node
(`FillerAttachNode`: a node above the origin meets the filler's `bottom`). Stock reparents an
`isFairing` shroud under the part on its bottom node (decompiled `SetupJettisonState`), and its
`Jettison` sets `isJettisoned` with no visual change (the shroud leaves with the stage below), so
the real side is the subject part's own renderers plus its jettison transforms wherever they live,
shown before the jettison and hidden after; every filler renderer is hidden on every side. Teardown
(`DestroyStateParts`, from `DestroyCase`, so both the wave teardown and `StepContext.Cleanup`)
destroys the start and stowed ghosts and every jettison object no longer under a `Part`, one Verbose
`debris destroyed n=` line per case. A crashed state case's sidecar (stale `inflight.txt`) carries
`stateProof` with its family, state and `reached = false` (`CrashedStateProof`).
The manifest's `profile` is now the install directory's name (`ProfileNameOf` resolves the
`KSP_x64_Data/../` root with `Path.GetFullPath` first; it read `..` before, section 8), its
`settings` gain `states`, `contexts` and `controlSet` on every run, and a state run's exclusions
join `exclusions[]` with their D.1 reasons.

Stowed pose and the pre-press check (found on the first live run): a lone part authored with no
persistent module values starts at each field's initial value, so a `ModuleWheelDeployment`
starts Deployed and a bay whose `closedPosition` is 1 (Mk2, Mk3) starts open, and the toggle the
sweep presses would leave the target. The state authoring writes the stowed pose into the
subject's MODULE node (`stateString = Retracted`; `animTime = closedPosition` with the
`animSwitch` that matches it, decompiled `UpdateAnimSwitch`), and a case whose done signal already
reads reached before the press is not pressed: `action-failed`, `actionResult = refused`, the
detail naming the field (todo PARTPARITY-STOWED-POSE-NOT-STOWED covers the stowed cases). A
reparented shroud's real records are read at the transform's prefab path
(`RehomeJettisonPaths`), so the structural diff pairs a stacked shroud with its ghost.
A state case gets the ReStock depth-mask repair (`PartParityDepthMaskRepair`, PR #2760) at the
top of its preparation, before the state branch reads the real renderers, with the stowed case's
summary line, flags and `depth-mask-unrepaired` setup fault (source gate
`PartParityStateDepthMaskGateTests`).

Live (2026-11-22, stock-minimal, harness runs `2026-11-22_1411`, `_1419` and `_1428` of a local
uncommitted spec: one `states=all` step per acceptance part plus a 60 s stowed step): on the last
build every acceptance case reached its state and wrote its rows (four rows for a state case, two
for the stacked stowed shroud; `turboFanSize2`'s jettisoned case is excluded
`jettison-blocked-by-node`), the nine controls read ok, the S4 judge's Python recompute of every
captured state atlas matched its sidecar, no debris or case vessel was left, no `[ERROR]`. The
stowed step planned the same 792 case ids in the same order as the P3 run `2026-11-22_0033`, the
same eight controls and exclusions, its sidecars within that run's run-to-run noise (max IoU
delta 0.0029 against 0.0028 between `_0004` and `_0033`). Findings: todo
GHOST-AIRBRAKE-START-NOT-DEPLOYED, GHOST-LIT-SPOTLIGHT-LENS, PARTPARITY-SUN-TRACKER-CAP.

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
   the module node authored with `deployAltitude = 500` (a persistent field the player sets in the
   editor; the stock range is 50 to 5000; S7 lowered it from the planned 1000 because stock also
   opens fully once a raycast of `deployAltitude` metres down meets the terrain,
   `ModuleParachute.ShouldDeploy`, and a semi case must keep its margin while it falls). Settle 0.5 s.
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
`ModuleParachute.FixedUpdate`). The ghost aims its open canopy by the same rule from its own
surface velocity, or straight against gravity when it has none (the static parity ghosts), without
the noise (todo GHOST-CHUTE-CANOPY-AIM). The comparison handles the noise in two steps:

- Capture timing: after done, the case measures each frame the real canopy's airflow tilt (the
  angle between the part's +Y and the drag direction) and its wobble (the whole rotation between
  the canopy and stock's un-wobbled pose `LookRotation(aim, up)`, its roll about the aim included),
  and captures at the first frame where their sum is
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

BUILT (S7, 2026-11-22): `Source/Parsek/TestCommands/PartParityAirContext.cs`, the pure rules in
`PartParityAirPlan` (xUnit `PartParityAirContextTests`) and the live half in
`PartParityAirContext`.

- Admission. The runner and the seam applier both call `PartParityAirContext.DecideAdmission`,
  which reads the active vessel's situation and its surface distance to the pad (on the home body
  only) into `TestCommandPartParitySweep.DecideHostAdmission(args, ...)`; one Info `air
  admission:` line per air step. The applier admits EVERY step by its run kind, the reset
  included, so an air lane's reset carries `contexts=air`. The first run step writes the host
  baseline with `Air = true` (`host.txt` `mode=air`).
- Dispatch. One hook in `PartParitySweepRunner.RunWaves`: every wave of an air run, its stowed
  controls included, runs `PartParityAirContext.RunWave`; an air state wave holds up to
  `MaxAirWaveCases` (5). The air run's `ctl-real-vs-real` photographs the Mk16, armed by stock's
  Deploy in the capture frame; `ctl-empty` / `ctl-ghost-absent` keep the small tank.
- Spawn. `GhostCoOrbitalRig.SpawnInAir` (beside `SpawnCoOrbital`, so the production materializer
  pin is unchanged) through `VesselSpawner.SpawnAtPosition`: 1600 m above sea level, slots 40 m
  apart along the band 200 m east of the pad, upright (+Y up, +Z north), the inertial velocity
  the surface's 10 m/s down plus `body.getRFrmVel`. Every `ModuleParachute` MODULE node gets
  `deployAltitude = 500` (see step 1 above).
- Chute sequence. The state driver's press and wait, unchanged; a full case's wait calls
  `PartParityAirContext.StepFull` once the chute is SEMIDEPLOYED and still: `deployAltitude`
  -> 5000 and the timeout restarted at the 15 s full wait. Both air states start with the 10 s
  semi timeout.
- Capture. Once done (after the settle), each frame at its end the case measures the canopy
  (`tilt` = angle between the part's +Y and `(invertCanopy ? -1 : 1) * dragVectorDir`, `wobble`
  = the whole rotation between the canopy and `LookRotation(aim, up)`, `up` stock's: the part's
  +Z, or `vessel.CurrentCoM - part.transform.localPosition` in a symmetry group) and captures at the first frame under 3 deg, or
  at 4 s tagged `canopy-wobble:<deg>` (a state flag), recording `stateProof.canopy` (`tiltDeg`,
  `wobbleDeg`, `transform` = `canopyName`). The prepare (depth-mask repair included) and every
  view run in that ONE frame, so the structural records, the pictures and the measured angles
  are of one pose; the sidecar is written and the case destroyed at once, so no chute falls
  longer than it must. Before the capture the state is re-read: a chute that left its state
  (a semi stock opened fully) is `state-timeout`.
- Guards. A case under 300 m above the terrain is ended `died-settling` and destroyed; a wave
  running past 120 s ends its remaining cases `state-timeout`. Neither fired in the first flights.
- Live (2026-11-22, harness runs `2026-11-22_1713_PP-5-part-parity-chutes-stock` and
  `_1715_PP-6-part-parity-chutes-modded`, PASS attempt 1, about 30 s of sweep each): all five
  chutes of each install reached semi (1.7 to 3.5 s after the press) and full (4.9 to 13 s) for
  real with `ParachuteSemiDeployed` / `ParachuteDeployed` recorded, every capture inside the
  canopy window (tilt + wobble 0.5 to 2.2 deg, none timed out, 1205 m or more above the
  terrain), the four air controls ok, no case vessel left. Every air case reads `fail`
  (report-only) because the ghost canopy keeps the model's pose while stock re-aims the live
  one (stock: a 90 or 180 deg turn about the canopy axis; ReStock: the Mk16's canopy upside
  down), far beyond the canopy cap: todo GHOST-CHUTE-CANOPY-AIM; the ReStock semi read-back:
  todo PARTPARITY-RESTOCK-SEMI-READBACK. The 3 deg window caught every case at its first done
  frame, so it needs no re-pin (D.12).

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

BUILT (S3, 2026-11-22): `GhostCoOrbitalRig.TryBuildGhostFromSnapshot(snapshot, recId, name, events,
vesselName, pid, startUT)`, with `TryBuildGhostFromLiveVessel` a wrapper that backs up the live
snapshot and calls it (the stowed path builds exactly as before). The three ghosts are built in
the state branch of the case preparation (`PartParityStateDriver.Prepare`), each parented at zero
offset under the real part and read back through `GhostStateReached` over the engine's own info
(deployable infos by part pid for every deploy family, `lightInfos` / cabin-light colour changers
with `lightPlaybackStates.isOn`, `jettisonInfos` with each transform's `activeSelf`, the
parachute canopy scale against its sampled poses). The prime schedules are pure
(`PartParityStateWave.EventRoutePrimeUTs` / `StartRoutePrimeUTs`, one prime, or eleven 1 s apart
for a sun tracker); the event route's clip is its own deployable info's clamped clip (3 s when it
has none). `startFirstFrame.atTarget` is the start route's outcome right after its first prime;
the sun angles are each pivot's angle from the stowed reference's neutral rotation. The capture
is `PartParityCapture.CaptureSides` (the two-side `CaptureView` calls it with the same two sides
in the same order); `start` measures real against side 3, `stowedRef` real against side 4, and
the atlas composes rows from `wc.RealTiles`, `wc.GhostTiles`, `wc.GhostStartTiles`,
`wc.GhostStowedTiles` (the S1 guard `TheAtlasTileOrder_IsTheAtlasRowLabelOrder` reads that
composition). The stacked stowed context case builds only its event-route ghost (two rows).

### D.6 Verdicts, flags, setup faults, controls, gates

- Thresholds: the 6 thresholds, unchanged. Each judged route is judged like a stowed case (the
  view rules, the structural raise, the context cap (no depth-mask cap: 4.2 step 0), plus the air case's canopy
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

BUILT (S4, `harness/lib/partparity.py`; Python only, no flight). What the judge reads and how:

- Routes. A case is a state case when its sidecar `state` is not `stowed` (`is_state_case`; a
  sidecar with no `state` reads as stowed). `judge_route` judges one route: the event route is
  the view measures plus `structure`, the start route each view's `start` block (named after its
  view) plus `structureStart`. Caps per route, in this order: `context-dependent` tag, a skipped
  depth mask in that route's `missingOnGhost`, then the canopy cap. The case takes the worse
  route, the event route on a tie; `worstView` reads `<route>:<view>`, every reason
  `<route>:<reason>`, and `maxShapeBlobPx` / `maxShapeBlobPct` / `maxColorBlobPx` are the max
  over both routes. A state case with views but a view whose `start` is not an object is
  `error` (`start:no-views`). The skin proof and the setup-fault rule stay case-level. A stowed
  case's verdict and its verdicts-file entry are byte-identical to before P6.
- Canopy cap (D.4): only for an air case (sidecar `context == "air"`) whose capture found its
  window: numeric `stateProof.canopy.tiltDeg` and `wobbleDeg` with `|tilt| + |wobble| < 3`
  (`CanopyWindowDeg`, source-synced) and no `canopy-wobble:<deg>` marker in `tags` or `flags` (both mark the 4 s
  timeout capture, which is never capped), and a non-empty `stateProof.canopy.transform`, the
  module's `canopyName` (an ADDITIVE key S7 writes, listed in `CanopyKeys` / `CANOPY_KEYS`; a
  null or empty value is never capped). A canopy renderer is a structure key one of whose `/`
  segments (the `#k` suffix dropped) equals that name: the canopy transform or a renderer under
  it. With the bound `b = min(|tilt| + |wobble| + 1, 4)`, a route's picture fail caps at `warn`
  (`canopy-airflow`) when its structure has no `materialDiffers` / `meshDiffers` /
  `renderedDiffers` entry and no rendered missing or extra renderer, and either (1) every
  `transformDiffers` entry is a canopy renderer with `|rotationDeltaDeg| <= b`, `scaleDeltaPct <=
  1.0` and `positionDeltaM <= 0.001 + 2 d sin(t / 2)`, with `t = min(|rotationDeltaDeg| + 0.5, b)`
  (the entry's own turn plus the structure's rotation tolerance: the records are part-relative,
  so a rigid turn shows on every canopy renderer as that rotation, and a canopy moved without
  turning gets no arc) and `d` the entry's
  `canopyPivotDistanceM` (the real renderer's distance from the canopy transform's pivot, an
  ADDITIVE `TransformDiffKeys` key the air context writes on canopy renderers only,
  `PartParityStructure.StampCanopyPivotDistances`; null elsewhere and on older sidecars, which
  leaves the 1 mm tolerance alone): a rigid turn of the canopy subtree about its pivot
  moves a renderer at distance `d` by at most the chord of its turn, and ReStock's canopy renderers sit metres
  from the pivot (0.1 to 0.6 m of travel at 0.6 to 3 deg, todo PARTPARITY-CANOPY-CAP-LEVER-ARM);
  or (2) the route's structure was measured (`transformDiffers` is a list; a route whose ghost was
  not built writes null and is never capped) and no transform differs at all, so every canopy
  renderer turned under the structure's own
  0.5 deg rotation tolerance, and the measured turn `|tilt| + |wobble|` is at least 0.1 deg
  (`CANOPY_MIN_TURN_DEG`: below it even a 5 m lever arm sweeps under 1 cm, under a pixel at the
  air tiles' scale, so a canopy that did not turn explains no picture difference). The diff
  compares LOCAL transforms, so S7 must make the airflow rotation visible as a `transformDiffers`
  entry keyed at or under the canopy transform (a wobble on an ancestor only shows in the
  pictures and is never capped). The C# tolerances, the key table and the key test are
  source-synced (`test_the_structure_tolerances_are_the_csharp_ones`, the JSON key table cell).
- Press proof (a writer contradiction, design decision of S4): on a case whose setup is not a
  fault (`ok`, `still-animating`), a state case must carry a `stateProof` with a non-null
  `action`, and any proof with a non-null `action` must read `actionResult == "invoked"` and
  `reached == true` (a failed press is `action-failed`, a run-out wait `state-timeout`, by the
  C# contract). A contradiction makes the case `error` (reason `press-proof:...` first) AND is a
  recompute mismatch, INVALID(parity-instrument), checked over every part case. A proof with a
  null `action` on a stowed-state case is the stacked stowed context case (no press required:
  `actionResult` null, `reached` true, endpoint `context` / `stowed`, two atlas rows, no `start` /
  `stowedRef`): it is judged as a stowed case; any other stowed-state proof with a null `action`
  (context not `stacked`, or `reached` not true) is the contradiction above.
- Writer cross-checks, each INVALID(parity-instrument): for every planned row that is a state or
  context case on either side (state not `stowed`, or context not `alone`), the row's and the
  sidecar's `state` and `context` must be equal (null on one side included); a sidecar whose
  `stateProof.action` is non-null while its `state` is stowed or null; a `stateProof.state` that
  differs from the sidecar's `state`; a run that holds any such
  case while its `settings` recompute the stowed control set (`states` / `contexts` absent or
  `none`). A stowed sweep compares nothing.
- Flags. The judge reads the union of the sidecar's `flags` and the flags it derives from
  `stateProof`, so a flag the writer drops cannot hide: `ghostEvent` / `ghostStart` a string
  other than `applied` -> `state-not-modelled:event|start:<outcome>`; `reached == true` with
  `events == []` -> `state-not-recorded`; `startFirstFrame.atTarget == false` ->
  `start-pose-not-first-frame`. A null value derives nothing. `state-invisible` is derived when
  every view has a `stowedRef` and none reaches a warn line on its shape keys. Each raising flag
  adds the reason `flag:<flag>`. `sun-tracker-first-frame:<deg>` passes through as a report-only
  flag; tags never become flags.
- Recompute of a state case: `atlas.rows` must be the four state rows and the atlas `4 x tile`
  high. Row 1 is compared with the view, row 2 with its `start` block (diffs named
  `<view>/start.<key>`), both with today's recomputed key set; row 3 with the `stowedRef` counts
  exactly, and its two blob percentages are null exactly when the view's are, 0 for a zero blob,
  else equal to `100 x blob / area` for the box area the view's other non-zero blobs imply (the
  sidecar carries no area, so a view whose only non-zero blob is the `stowedRef` one checks only
  that the percentage is a number). Block shape (`start` with every `StartKeys` key, `stowedRef`
  with every `StowedRefKeys` key) is checked on EVERY state case with views, sampled or not.
- Press gate: every planned state case counts (row or sidecar `state` not `stowed`; the
  stacked stowed case and the controls do not). Its family is the manifest row's `family`, else
  the sidecar's `stateProof.family`, else `unknown` (a crashed case's sidecar may carry no
  proof); its status the sidecar's `setup.status`, else the row's `setupStatus`. `failed x 2 > cases` trips it; the problem
  reads `family=<f> <k>/<n> case(s) action-failed|state-timeout: first <caseId> <status>:
  <detail>`. A run with no planned state case has no press check.
- Controls. `check_controls` requires the set `control_set(settings)` recomputes, and with the
  sidecars re-judges each state control the manifest records `ok` (`judge_state_control`, the
  mirror of `PartParitySweepPlan.JudgeStateControl` over the sidecar's `setup.status`,
  `stateProof.reached`, `len(stateProof.events)`, the view shape lines and each `stowedRef`); a
  disagreement is INVALID(parity-controls). A written `settings.controlSet` (comma-joined ids)
  that differs from the recomputed set is a recompute mismatch, INVALID(parity-instrument); a
  manifest without the key is not compared.
- Order: host-damaged > controls > state-press > instrument > incomplete
  (`partparity._SUBKIND_ORDER`, mirrored as `hlib.PARTPARITY_INVALID_SUBKIND_ORDER`);
  `parity-state-press` is terminal and not flake-exempt like the other four.
- Verdicts file: a state case adds `state`, `family`, `context`, `routes` (`event` / `start`, each
  `verdict`, `reasons`, `worstView`, the three max measures) and `stateFlags`; a sweep with a press
  check adds `statePress` (`ok`, `families: {<f>: {cases, pressFailed}}`, `problems`) and
  `stateFlags` (cases per flag kind). The summary line gains ` press=ok|bad` only then.
- Proof that stowed sweeps judge as before: the archived PP-1 `2026-11-22_0033` and PP-2
  `2026-11-22_1157_run2` sweeps (manifests and sidecars from the results archive, and the same two
  harvested label dirs with their atlases) re-judged with this code and with `origin/main`'s and
  S1's `partparity.py`: per-case verdicts, verdicts document, summary line, detail record,
  controls and completeness are byte-identical (800 / 1518 sidecars; recompute 24 / 39 cases, 0
  mismatches).

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

BUILT (S5, `harness/tools/part_parity_view.py`; Python only, no flight). Every verdict, route,
flag and gate on the page is partparity's (`judge_case`, `judge_view`, `start_views`,
`check_controls` with the sidecars, `check_state_press`, `case_row_problems`,
`run_shape_problem`, `control_set_disagreement`, `state_flag_counts`); the viewer derives none.

- Rows. A case row carries `isState` / `isRest` (`stowed` / `alone`), `stateLabel` (the state,
  with the context when it is not `alone`), `family`, `routes` (`event` / `start`: each
  `RouteJudgement` plus its trigger line and minimum IoU), `stateFlags`, `stateProof`,
  `stowedCaseId`, and per view the `start` block and the `stowedRef` counts, each with its
  `judge_view` verdict. A reason `<route>:<reason>` reads `<route> route: <phrase>`, a flag
  reason in words; a shape blob's px and % collapse per view and per route.
- Grouping. A part's skins are its stowed (`stowed` / `alone`) cases, ordered as before; a
  state or context case joins the skin of its paired stowed case, else the skin of the same
  name, else a skin of its own (a state sweep loaded alone). Inside a skin the stowed case
  comes first, then the context cases, then the families in the D.1 order, each family's
  states in the `KNOWN_STATES` order. A skin holding only its stowed case renders exactly as
  before P6, so a stowed sweep's page is unchanged.
- The view row of a state case: STOWED | REAL | GHOST event route | GHOST start route |
  DIFFERENCE (real vs the event ghost), each caption naming its row. STOWED is the paired
  stowed case's REAL tile (`stowedPair`, same profile) when its sweep is loaded, else the
  case's own `ghost-stowed` row with a dashed border and the caption "reference, not judged".
  The 1:1 dialog adds the start-route difference and, when paired, the stowed ghost too.
- Merge. Sweeps of one profile merge into one part list. Each loaded sweep has a key (its
  label, `-<n>` on a repeat); a control's row id is `<profile>/<sweep>/<control>`, so the
  controls of a stowed sweep and its state sweeps never collide, and the controls section
  lists them per sweep with the set kind (`stowed`, `orbit`, `air`). A part case id loaded
  twice in one profile keeps the first and is the sweep box's duplicates line (bad). Until
  S3's profile fix (8) every manifest records the profile `..`, which the viewer replaces by
  the sweep's label (pp1, pp3, ...), so a stowed sweep and its state sweeps merge only when
  passed as `NAME=DIR` with one name (`stock=<pp1> stock=<pp3> stock=<pp5>`); the sweep box
  and the console say so for every sweep named after its label.
- Header per sweep: the control set and its gate (state controls re-judged from their
  sidecars), the press gate per family, the state flag counts, the writer cross-checks. A
  state control recorded `ok` that `judge_state_control` judges bad reads `control BAD`
  (chip, counter, controls box) with the judged detail as its trigger, and a failed controls
  gate marks the sweep box bad.
- Filters: State (the word, the tiebreak dropped), Family and State flags (the four kinds),
  shown only when a state case is loaded.
- Regression: rows pair by profile and key (the case id; for a control the set kind and
  id); a state case adds `event.` / `start.` changes (verdict, shape and colour blob, IoU)
  and the state flags added or dropped.
- Proof: the PP-1 `2026-11-22_0033` and PP-2 `2026-11-22_1157_run2` stowed sites rebuilt
  with this viewer equal the S4-branch viewer's on every verdict count, the part list, each
  group's verdict, trigger and case order, and each case's verdict, reasons, trigger,
  measures and view verdicts (2318 cases, 1080 parts, 16 controls); only the control row ids
  gain the sweep key. A synthetic stowed + orbit state + air state set (the S4 test helpers)
  shows each part's states side by side with no duplicate line (`test_part_parity_view`).

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

As flown (S6, 2026-11-22, branch `part-parity-p6-s6`): PP-3 plans 160 orbit cases (94
single-part states plus 66 shroud cases; the 10 chute cases are air, the 5 fairing parts are
excluded `fairing-contexts-pending`) and PP-4 225 (117 plus 108), each plus the 9 controls. A
`limit=20` reading flight per lane (`2026-11-22_1627`, `_1631`: about 65 s for 20 cases and the
controls) and then the full lanes: PP-3 `2026-11-22_1634` (426 s + 86 s of sweep, 622 s wall) and,
after the sun-tracker cap re-pin (D.3), `2026-11-22_1656` (414 s + 123 s, 638 s wall); PP-4
`2026-11-22_1644` (449 s + 87 s + 0.2 s, 649 s wall) and `2026-11-22_1718` at the 25 s cap (452 s +
106 s + 0.2 s, 652 s wall). About 3.3 s per case, against the planning
estimate of about 2.2 s (350 s for PP-3), so the sweeps run about 50 % over the estimate and still
fit: PP-3 keeps its 2 steps (960 s against about 540 s), PP-4 its 3 (the third answers
`done=true` at once). `budgetSeconds` is 1800 / 2400, PP-1's and PP-2's, not the table's 1500 /
2100: the run budget must cover every step budget, LoadGame's 300 s included (1620 / 2160 s). All four full flights PASS attempt 1:
controls ok, press ok, complete, recompute 0 mismatches. Findings: `docs/dev/todo-and-known-bugs.md`
(PARTPARITY-STATE-SWEEP-FINDINGS-2026-11-22 and the entries it names). S8 (D.1 "FGV contexts")
adds the 13 FGV cases to both orbit lanes' plans (`contexts=none` includes the `fgv` class), about
45 s more per lane at the measured rate; PP-3 / PP-4 have not flown with them yet.

Instrument round (2026-11-22, branch `parity-instrument-fixes`: the tracker cap and still rule per
`trackingSpeed`, the animation-group clip wait and `auto-deploy`, the light fade settle, the canopy
lever arm, the colour-changer leak repair): PP-3 plans 172 cases (93 single-part states, 66
shrouds, 13 FGV; the ISRU is excluded) and PP-4 238 (117, 108, 13), each plus the 9 controls. The
final flights `2026-11-22_2014` (PP-3, 457 s + 176 s of sweep, 727 s wall; the radiators'
tracking settle adds about 80 s) and `_2026` (PP-4, 444 s + 246 s + 0.2 s, 784 s wall), PP-5 `_2039` and
PP-6 `_2041`, all PASS attempt 1, controls ok, complete: PP-3 139 pass / 21 warn / 12 fail / 0
error, PP-4 193 / 24 / 21 / 0, PP-5 and PP-6 10 warn each. The first FGV flight in the orbit lanes
reads 12 pass and 1 warn on each install (`fairingsize2--white--stowed--fgv-3-open-top`, colour
blobs of 140 px). Per-fix readings: the todo entries the findings entry names.

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
| S8 | FGV migration: FGV's six configurations as `fgv-<n>-<name>` contexts of the fairing family (and the engine plate / shroud skirts), with FGV's states; FGV-1 retired in its own PR after the sweep's fairing cases have flown green twice | `PartParityStateRules.cs` (context table: mirrored, so with its Python mirror), `PartParityCaseAuthoring.cs`, the FGV test file | after S3 | the 18 fairing cases match FGV-1's verdicts on the same DLL. BUILT 2026-11-22: 13 cases (D.1 "FGV contexts"), read twice against FGV-1 on one DLL; with the stowed pose of the stowed cases (todo PARTPARITY-STOWED-POSE-NOT-STOWED) |

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

## Appendix E. P8 plan: visual effects (rev 2, 2026-11-22)

Status: PLAN, not built. Rev 1 was reviewed by one clean-context reviewer; rev 2 folds in its
nine blocking findings and four omissions (E.11) and the owner rulings below. The E0 spike
(E.12, flown 2026-11-22) answered U1 to U10; the plan text below already carries the changes it
forced, each marked "(E0 Un)". P8 compares the
visual effects a part shows while it works (engine plumes and smoke, RCS puffs, engine throttle
glow, thermal glow, the light a lamp casts) on the real part and on its ghosts, the way P6
compares a deployed pose. Every stock claim below was read from the decompiled KSP 1.12.5
`Assembly-CSharp.dll` of the harness install (dumps in `<umbrella>/logs/decompile/p8-plan/`); every
Parsek claim from the code on `origin/main`; every count from the post-patch part configs of both
harness installs (`<umbrella>/automation/<profile>/GameData/ModuleManager.ConfigCache`, read
2026-11-22, after the 3.1 exclusions: stock-minimal 478 parts, modded-compat 602) and, for the
Waterfall split, from the `@PART[...]` patches of the four Waterfall folders of modded-compat.

Owner rulings, 2026-11-22:

- R4 (RCS). The ghost firing every nozzle of an RCS module at the module's mean power is a KNOWN
  DIFFERENCE for now: reported, never failed. Per-nozzle RCS recording is a separate improvement
  (todo RCS-PER-NOZZLE-RECORDING). The RCS input is chosen so the judged comparison is not decided
  by that gap (E.2), and the difference is shown apart (E.7).
- R5 (reentry). Yes to a vessel-scale reentry reference lane photographed from the flight
  camera, AFTER the P8 core (E.9 piece R1).
- R6 (Waterfall-free profile). Yes to a modded profile without Waterfall and its config packs,
  so ReStock's own plumes are judged (E.6). The Waterfall install's cases stay reference-only.

Three facts shape the plan:

- Particles are stochastic. `KSPParticleEmitter.Update` / `EmitParticle` draw every count,
  position, velocity, size and lifetime from `UnityEngine.Random`, shared by every emitter in the
  scene, and the real and ghost emitter sets differ in count, so no seed makes two frames match.
  A plume is compared as a DISTRIBUTION over time (E.4), never frame against frame.
- A working engine moves its vessel. A lone LV-T45 accelerates at about 140 m/s2 and leaves the
  +-180 m wave band (4.1) in under 2 s. The real side is held on a test stand (E.2).
- Stock reentry and Mach effects cannot be seen by the sweep's camera. `AerodynamicsFX` reads
  `FlightGlobals.ActiveVessel`'s surface velocity and Mach number in its `Update` and draws through
  its own `FXCamera`, parented to `FlightCamera.fetch.mainCamera`, with replacement shaders. A
  case vessel is never the active vessel and the capture camera (D4) is not the flight camera, so
  the part sweep has no real side to compare; R1 photographs them from the flight camera instead.

### E.1 Effects in P8, and how each is found per part

P8 adds four families, all defined in a new pure `PartParityFxRules` and planned only by the new
`fx=` argument (E.5). Two are stochastic (`plume`, `rcs`: the E.4 instrument); two are
deterministic state cases (`heat`, and lamps in the new context `plate`) captured and judged by
P6's machinery. Each family matches with the SAME predicate as its recorder poller (the P6 rule),
over the prefab's module list (type chain to `PartModule`) and its `partConfig` MODULE / EFFECTS
nodes.

| Family | Predicate (the recorder's own) | State words | What is compared | stock-minimal / modded-compat / modded-restock cases |
| --- | --- | --- | --- | --- |
| `plume` | every `as ModuleEngines` module, engine index = the `EncodeEngineKey` module index (`FlightRecorder.CheckEngineState`) | `burn-full`; `burn-low` (30 %) unless throttle-locked | flame and smoke particles (E.4); the body atlas carries the throttle glow (`FXModuleAnimateThrottle` on 33 / 13 parts, ReStock's `heatColor` / `throttleColor` `ModuleColorChanger`s on 52 modded parts) and the exhaust `Light`s (structural) | orbit 43 + 31 / 60 + 42 / 60 + 42; air 7 + 7 on each |
| `rcs` | every `as ModuleRCS` module (`FlightRecorder` RCS cache) | `rcs-fire` | puffs of the nozzles stock fires (E.2); the body atlas carries `FXModuleAnimateRCS` glow | 7 / 19 / 19 |
| `heat` | `ModuleAnimateHeat` (`PartStatePoller.PollAnimateHeat`) | `heat-medium`, `heat-hot` | the heat clip's colour and pose on the body, P6 rows and judge | 32 / 26 / 26 |
| `light` in context `plate` | the recorder's light check (P6 S2, `PartStatePoller.PollLights`) | `lit` | the light the lamp casts on a matte plate (E.2), plus the part's `Light` components structurally | 15 / 18 / 18 |

modded-restock (E.6) is provisioned (PR #2783, 2026-11-22; ModuleManager cache has no
`ModuleWaterfallFX`, ReStock's own `restock-fx-*` particle FX are live on both sides); its counts
are modded-compat's with the Waterfall patches gone, and its first step logs the exact ones.

Engines by type, stock (modded): 28 (39) liquid, 12 (18) solid (throttle-locked, no `burn-low`),
7 (7) air-breathing modules on 6 parts (`JetEngine`, `RAPIER`, `miniJetEngine`, `turboFanEngine`,
`turboFanSize2`, `turboJet`), 1 nuclear, 1 ion, 1 monopropellant; 2 multi-mode parts (`RAPIER`,
`turboJet`). EFFECTS on stock parts: `MODEL_MULTI_PARTICLE` (21 parts), `PREFAB_PARTICLE` (19) or
legacy `fx_*` keys (27 engine / RCS parts: `fx_exhaustFlame_*`, `fx_smokeTrail_*`,
`fx_exhaustSparks_*`, and `fx_exhaustLight_*`, a Unity `Light`). On modded-compat the Waterfall
packs patch every liquid, nuclear, ion and monopropellant engine, every jet and every RCS part
checked (46 engine and 13 RCS parts carry `ModuleWaterfallFX`), and 7 lamps (`spotLight1..3`,
`spotLight1.v2`, `spotLight2.v2`, `groundLight1`, `groundLight2`); the solid boosters
(`solidBooster.sm.v2`, `solidBooster.v2`, `Shrimp`, `Mite`, `sepMotor1`, `LaunchEscapeSystem`)
and the nav, dome and strip lights are not patched. `LaunchEscapeSystem`'s stock ghost skipped its
particles until GHOST-LES-NO-PLUME; it is judged like any other case.

Out of P8, each an exclusion reason (`PartParityFxRules.ExclusionReasons`, mirrored as
`partparity.FX_EXCLUSION_REASONS`):

- `fx-reentry-camera`: stock reentry fire and Mach effects (above; R1 covers them).
- `fx-blackbody`: stock's blackbody glow of every hot part (`Part` updates a
  `MaterialColorUpdater` on `_TemperatureColor` from `PhysicsGlobals.GetBlackBodyRadiation(
  skinTemperature)` above 798 K). The ghost carries no part temperature by design (3-state heat
  levels, never temperatures; its reentry glow is a trajectory effect), and nothing in
  `Source/Parsek/Ghost/` writes `_TemperatureColor`. P8 SUPPRESSES it on every real FX case
  (`part.blackBodyRadiationAlphaMult = 0`, the public field its alpha is multiplied by) so it can
  never leak into a heat or plume picture; R1 shows it at vessel scale.
- `fx-ablator-char`: `ModuleAblator`'s char (8 / 12 parts), driven by reentry heating (R1).
- `fx-needs-air` (orbit runs): an air-breathing module; its cases run in the air context (E.2).
- `fx-throttle-locked`: `burn-low` of a throttle-locked engine (every solid booster).
- `fx-no-light`: the plate case of a `ModuleLight` part whose prefab has no `Light` component
  (ReStock's `telescopicLadderBay`, since 2026-11-23); read live, so the config part lists keep it.

P6 does not change. The FX families and the `plate` context live in `PartParityFxRules`, not in
`PartParityStateRules`: `Families`, `OrbitContexts` (`alone`, `stacked`, `fgv`) and the D.1
exclusions `p8-heat` / `p8-fx` stay as they are, so `states=all` (with or without `contexts=`)
plans exactly what PP-3 to PP-6 plan today. Pinned by the existing D.1 count cells
(`StockMinimal_EnumeratesTheD1Counts`, `ModdedCompat_EnumeratesTheD1Counts`), the committed
stowed-id literal, Python's `FAMILIES` sync cell, and two new cells in F1: a `states=all` plan
holds no FX key, and with `fx=none` every plan, control set and manifest `settings` equals
today's for the PP-1 to PP-6 arguments.

Not separate cases: a lit lamp's lens and flare (P6's `lit` case, with the `lightBrightenSpeed`
settle handed to the state driver by todo GHOST-LIT-SPOTLIGHT-LENS), engine and RCS glow (the
plume / rcs case's body atlas), the exhaust `Light` of legacy engines (the case's structural
`Light` diff). ReStock's engine glow is a `ModuleColorChanger` an `FXModuleThrottleEffects` sets by
name, ramping at `animRate`; the ghost plays it from the recorded power (todo
GHOST-RESTOCK-NOZZLE-GLOW), which the sweep brings to the capture's UT with the clip glows
(`AdvanceNozzleGlows`). The start route cannot match a bell mid-ramp: stock does not persist where
the ramp stood and starts it at `animState`'s end when it loads the part, so the start-route ghost
shows what a load would and the real part, which was not loaded, does not
(PARTPARITY-START-ROUTE-RAMP-NOT-RELOADED: the instrument's case, not the ghost's).

### E.2 Producing each effect on the real side, and the same state on the ghost

Every working case runs P6's sequence (D.3: spawn, settle, stowed inputs, press, wait for done
while the recorder's own pollers run into the case's `PartParityPollSink`, records, ghosts,
capture, teardown) with the additions below.

Recorder side. The engine and RCS pieces of the recorder the sweep needs are already
`internal static` and already reused outside `FlightRecorder`: `CheckEngineTransition`,
`ShouldRecordEngineAsIgnited`, `ComputeRecordedEnginePower`, `ProcessRcsDebounce`,
`ComputeRcsPower` are called by `ParsekFlightPostSwitchAutoRecord` today. So no extraction: the
sweep calls them with the recorder's own live reads (`ShouldRecordEngineAsIgnited(
engine.EngineIgnited, engine.isOperational, engine.finalThrust)`,
`ComputeRecordedEnginePower(engine.currentThrottle, engine.finalThrust, engine.maxThrust)`,
`active = rcs.rcs_active && rcs.rcsEnabled`, `RcsDebounceFrameThreshold = 8` frames), and an
xUnit source cell pins those three expressions in the sweep's poll AND in
`FlightRecorder.EngineAndRcs.cs` (comments stripped), the P6 `EveryFamilyPredicate_IsTheRecordersOwn`
pattern, so the two cannot drift.

Engines (orbit, the PP-1 host `eva2-lko-crewed`):

- Authoring, into the subject's MODULE node: `independentThrottle = True`,
  `independentThrottlePercentage = 100` (30 for `burn-low`), `thrustPercentage = 100`. A lone
  engine has no command part, and `ModuleEngines.UpdateThrottle` reads
  `vessel.ctrlState.mainThrottle` only when `part.isControllable`; the independent throttle branch
  (and `throttleLocked` for a solid booster) needs no control. `exhaustDamage = False` (no visual,
  no raycast heating of a neighbour). A `MultiModeEngine` part gets `autoSwitch = False` and the
  `runningPrimary` of the module under test (the RAPIER's closed cycle in orbit, its air mode in
  the air context).
- Propellant: `CheatOptions.InfinitePropellant` for the step, restored in `StepContext.Cleanup`,
  one Info line each way. In `ModuleEngines` the cheat sets the fuel fraction to 1 before
  `RequestPropellant` is reached, so a lone engine with no tank (and the ion engine's charge)
  burns. Global, but the host fires nothing.
- Press: KSPEvent `Activate` (P6's `BaseEvent.Invoke`). Real done: the recorder's ignition
  predicate holds, `currentThrottle` is within 0.01 of the request (jets spool,
  `useEngineResponseTime`), and the event route has recorded `EngineIgnited`. Timeout 10 s (45 s or more, "F7 as built",
  for a module with `useEngineResponseTime`: a jet's spool has a time constant of about 8 s, so
  it reaches 0.99 of the request after about 38 s, E0 U1) -> `state-timeout`.
- Stock FX inputs, for the record: `ModuleEnginesFX.FXUpdate` drives `running` with
  `currentThrottle`, `power` with `finalThrust / maxThrust`, `spool` with a lerp of the throttle;
  the legacy `ModuleEngines.FXUpdate` drives its `power` group at
  `Lerp(0.45, 1.2, currentThrottle)` and `running` at `Clamp(finalThrust / (0.5 maxThrust), 0.5,
  1.75)`. Every stock EFFECTS curve keys on power alone (`ModelMultiParticleFX.OnEvent(float
  power, ...)`), so a vacuum burn shows a rocket's whole stock look; only Waterfall reads
  atmosphere depth.
- Test-stand hold: every physics step, after the simulation (a `WaitForFixedUpdate` loop, which
  resumes after the physics step), the case vessel's root position, rotation, velocity and
  angular velocity are written back to its slot pose relative to the host (the VesselMover
  pattern), so thrust and torque never move it. The look compared is the held engine's, the look
  a player sees on the pad under the clamps. Proof: `fx.hold.maxDriftM`, the drift at RENDER
  time (read in `LateUpdate`, after the write), under 0.001 m, and one Verbose `hold drift` line
  per case. The displacement the simulation makes inside one step, before the write, is a dt^2
  (0.057 m for a lone LV-T45, 0.077 m for an LV-T30) and is logged as `fx.hold.stepM`, never
  gated: nothing renders between the step and the write (E0 U3).
- Determinism guards: the part's internal and skin temperatures are written back to their spawn
  values every step (engines heat their own part; `ModuleAnimateHeat` on the jets and the
  nuclear engine would drift hot during a burn), and blackbody is suppressed (E.1).
- Orientation: each plume case spawns with its thrust axis along the orbit normal, so no plume
  points along the slot line at a neighbour or the host.

Air-breathing engines: in the air context of D.4 (pad host `b1-pad-craft`, `host-not-at-pad`,
the showcase band 1600 m above sea level) through `PartParityAirContext`'s admission and spawn,
but HELD (the hold above, against the surface frame) instead of falling: a static jet at 1600 m.
With the cheat the intake air is met; the thrust is the static thrust (`velCurve` at Mach 0),
spike U1. Waves of 4.

RCS (orbit):

- Control: a lone RCS part is not controllable, but `ModuleRCS.Update` builds its input from
  `vessel.ctrlState` (X / Y / Z, pitch / roll / yaw) with no control check in the decompiled body,
  and `FixedUpdate` fires only with the vessel's `RCS` action group on. So the press turns the
  group on (`vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, true)`) and feeds the input
  through the case vessel's own `OnPreAutopilotUpdate` callback, which `Vessel.FeedInputFeed`
  runs on its `ctrlState` every physics step; the callback is removed at teardown. A value written
  into a non-active vessel's `ctrlState` from a MonoBehaviour is reset to zero before every
  `FixedUpdate`, so it fires only when the writer happens to run after the reset (E0 U2).
- The input, chosen so R4's gap does not decide the verdict. Stock fires nozzle i at
  `Max(Dot(axis_i, inputLin), 0)` (decompiled `ModuleRCS.FixedUpdate`; `axis_i` the thruster's
  `up`, or `forward` with `useZaxis`). No input fires every nozzle (opposed nozzles cancel), so the
  input is the one that fires the most nozzles EVENLY: a pure rule over the prefab's thruster axes
  in part space (`PartParityFxRules.ChooseRcsInput`) scores the 26 directions of the unit cube
  (6 faces, 12 edges, 8 corners, normalised) by, in order, the number of nozzles firing at 0.5 of
  the strongest or more, then the smallest spread between the fired nozzles' forces, then the
  largest mean; ties go to the earlier direction in a fixed order. It runs on the prefab, before
  the spawn, so the search fires NOTHING: no `RCSActivated` / `RCSThrottle` from a trial input can
  reach the event route (with the group on, `rcs_active` is true even at zero input and the
  8-frame debounce would book it). The press sets the chosen input with the group; the stowed
  inputs (snapshot, seeds) are taken before it, the poll sink runs from it. The rule transcribes
  stock exactly (E0 U2): it skips the thruster transforms stock skips (inactive, or at the world
  origin: 5 of the 9 on `RCSBlock.v2`), maps the direction through the vessel's
  `ReferenceTransform` as `ModuleRCS.Update` does (`inputLin` = (X, Z, Y) of `ctrlState`), and
  the press compares the nozzles stock fired with the prediction; a difference is the setup
  status `rcs-input-mismatch` (an `error`). The spike's rule matched stock on 3 of 4 parts and
  predicted 3 fired nozzles on stock `RCSblock.01.small` where stock fired none.
- What is judged and what is shown. The recording keeps one power per module (`ComputeRcsPower`:
  the sum of the forces over `thrusterPower` x the nozzle count) and the ghost fires every nozzle
  of the info at it (`SetRcsEmissionWithOutcome` plays every particle system). So the judged
  comparison is the fired nozzles only: the ghost's particle renderers of the other nozzles are
  hidden for the judged capture (by thruster index, a read-only record F2 adds), and of the fired
  nozzles' puffs the judged measures are presence, origin, lateral offset, direction and colour
  (E.4). Length, width and brightness depend on the power, which R4's gap sets on the ghost, so for
  RCS they are reported, never judged, beside the expected ratio `rcsPowerRatio` = the recorded
  mean power / the mean fired-nozzle force. The known difference itself is a flag
  `rcs-known-difference:nozzles=<fired>/<total>,power=<ratio>` and a third FX row group,
  `ghost-all` (the event-route ghost with every nozzle shown), captured and measured, never judged.
  The viewer shows it apart (E.7).
- Done: `RCSActivated` recorded (after the debounce), then the plateau below. Held as above.

Heat (orbit, deterministic): the press writes the part's temperature every physics step after the
simulation (the hold's step order), so `ModuleAnimateHeat.Update` (which reads
`part.temperature`, or `skinTemperature` with `useSkinTemp`) sees it in the same frame. Stock's
scalar is `(T + lerpOffset) * lerpDivRecip`, clamped, with the defaults `lerpOffset =
-draperPoint`, `lerpMin = 0`, `lerpMax = maxTemp`, so `(T - draperPoint) / (maxTemp -
draperPoint)` (decompiled `HeatEffectStartup` / `UpdateHeatEffect`); the driver inverts the
module's LIVE lerp fields, never the defaults. The ghost samples its heat clip at t = 0.5 (medium)
and t = 1.0 (hot) (`SampleHeatAnimation3State`), so `heat-medium` writes the T of scalar 0.5 and
`heat-hot` the T of scalar 0.999 (T = maxTemp overheats the part). Blackbody suppressed. Done: the
recorder's heat reader (`NormalizeAnimateHeatScalar`) reads the target within 0.01 and
`PollAnimateHeat` emitted the level's event; then P6's settle. The ghost routes are P6's three
(D.5).

Light on a plate (orbit, deterministic): P6's `lit` press, settled for the module's
`lightBrightenSpeed` + 0.5 s. A plate (a `CreatePrimitive` quad, collider destroyed, a neutral
lit material, the planted cube's lifetime rules of 4.5) faces the light: 3 m along a spot light's
forward, 1 m along a point light's lens axis (the light transform's forward), on the layer of the
part's first rendered renderer. The plate layer is rendered per side with the rig lights and
every scene light off EXCEPT that side's own `Light` components (the other side's disabled for the
render and restored, inside the one synchronous capture call). The picture is the footprint on the
plate: P2's shape and colour measures, deterministic. A floor makes a dark plate unmeasurable
rather than a pass: when the REAL side lights fewer than `PlateLitMinPct` (2 %) of the plate's
pixels above `PlateLitMinLum`, the case is the setup status `plate-not-lit` (an `error`, with the
light's type, range, spot angle and `cullingMask` in the detail), never judged. Spike U9 proves a
part `Light`'s `cullingMask` reaches the plate's layer. The part's `Light` components are also
diffed structurally (type, colour, intensity, range, spot angle, part-local pose:
`lightsDiffer`).

The ghost side of a plume or rcs case is built through the D2 seam on P6's two judged routes
(D.5): the event route (the snapshot before the press plus the polled `EngineIgnited` /
`EngineThrottle` or `RCSActivated` events) and the start route (the snapshot while burning plus
its seeds: a recording that starts mid-burn, a chain split, a Re-Fly). The stowed reference ghost
stays (engine off: it proves the glow is visible at all). Each ghost is primed EXACTLY as
production brings a ghost onto the screen:

1. `GhostPlaybackEngine.PrimeHiddenPartStateAtUT` as today (the part-event cursor reset, the
   events, robotics, deployables, synthesized motion, then `StopVisualFxForFrame` under the
   hidden-prime policy, which stops every engine and RCS effect, then `UpdateAudioAtmosphere`).
2. Activation as in production's watch-sync path: `ActivateGhostVisualsIfNeeded`, and when it
   activated a deferred state, `ShouldRestoreDeferredRuntimeFxState` ->
   `GhostPlaybackLogic.RestoreDeferredRuntimeFxState`.
3. The first unsuppressed frame of `ApplyFrameVisuals`, without its two instance calls
   (`UpdateReentryFx`, which can call `TryPerformLazyReentryBuild`, and `UpdateLaunchDust`: both
   read the engine's body and trajectory state, and a part case has neither): the rest of that
   branch, in its order, is `UpdateEvaJetpackPlumeForFrame`, `RestoreAllRcsEmissions`,
   `UnmuteAllAudio` (or `MuteAllAudio` for a boundary-overlap secondary),
   `RestoreActiveEngineFx` when `ShouldRestartEngineFxAfterSuppression(false, priorSuppressed)`
   (true after step 1, so the engine restarts from the last recorded power), then
   `UpdateEngineSmokeTrailFades` (stock's `SmokeTrailControl` air-density fade, which reads
   `state.lastInterpolatedBodyName` / `lastInterpolatedAltitude`: a part case sets both from the
   real vessel, or its smoke is left as built), then `UpdateAudioAtmosphere`. F2 extracts that tail up
   to and including the smoke fade as an engine-owned static,
   `GhostPlaybackEngine.ApplyUnsuppressedFxRestore(state, priorVisualFxSuppressed)`, called by
   `ApplyFrameVisuals` after `UpdateLaunchDust`; inlining it back gives the old body (P2's inline
   fold proof). The audio update stays outside it, because `ApplyFrameVisuals` runs it after BOTH
   branches; the part case calls it after the static. `state.visualFxSuppressed` is set false
   first, as `ApplyFrameVisuals` does.

So the ghost's effects start as on a playing ghost's first visible frame, and its particles then
simulate in real time beside the real ones (the ghost is active, so `ParticleSystem.Play` is not a
no-op, 9.3). The ghost's reach is read back read-only per route (`fx.ghostEvent` /
`fx.ghostStart`: the engine / RCS info holds the power, at least one emitter emits, the
`GhostPartEventApplyTally` outcome for the `EngineFx` surface), anything else being
`state-not-modelled:<route>:<outcome>` as in P6. E0 U2 found this priming left every RCS ghost
dark (todo GHOST-RCS-DARK-AFTER-FX-SUPPRESSION); since that fix `RestoreAllRcsEmissions`
re-lights every module with recorded power, so an rcs case reaches its ghost on both routes.

Warm-up and plateau, on EVERY side. The event-route ghost can only be built once the real side
has recorded its events, so its particles start from nothing seconds after the real ones. After
the last ghost of a case is primed, the case waits until every side (real, ghost event, ghost
start) has (a) run for at least the longest particle lifetime among its own systems
(`ParticleSystem.main.startLifetime` maximum, or `KSPParticleEmitter.maxEnergy`) since its prime,
and (b) a stable signal over the last 0.5 s (change under 10 %); cap 8 s, or 1 s past the longest
lifetime when that is later, at most 20 s ("FX instrument fixes" in E.4), then captured and
tagged `fx-not-plateaued:<side>`. The signal is the live particle count only for a side whose
systems report their particles; it is the side's `flame` coverage (one 128 px render per 0.25 s)
for a side with no particle system (every Waterfall side: the real LV-T45 on modded-compat has
none) or whose flame systems read 0 particles while they draw (the stock legacy
`fx_exhaustFlame_*` systems, E0 U5). Measured (E0): the real side plateaus 5.1 to 7.3 s after the
press (the 7 s smoke lifetime dominates), the event-route ghost 4.1 to 7.7 s after its prime. Each side's time is recorded (`fx.warmupS.real` /
`.ghost` / `.ghostStart`). A wave builds and primes all its ghosts first, so the warm-ups overlap,
then accumulates case by case.


F2 as built (2026-11-23, no behaviour change, IL proofs in `<umbrella>/logs/refactor/p8-f2/`):

- `GhostPlaybackEngine.ApplyUnsuppressedFxRestore(state, priorVisualFxSuppressed)`
  (`GhostPlaybackEngine.InRangeRender.cs`, `internal static`): `UpdateEvaJetpackPlumeForFrame`,
  `RestoreAllRcsEmissions`, `MuteAllAudio` for a boundary-overlap secondary else
  `UnmuteAllAudio`, `RestoreActiveEngineFx` when `ShouldRestartEngineFxAfterSuppression(false,
  prior)`, then `UpdateEngineThrustFractions` (ghost-fx-3, 2026-11-23) and
  `UpdateEngineSmokeTrailFades`. The smoke fade belongs IN it: it is part of the
  same unsuppressed branch, it runs after every emission write of the frame (the restore
  re-enables smoke renderers, and the fade re-hides a vacuum trail), and leaving it out would
  give a part case a smoke look production never shows. It reads the ghost's
  `lastInterpolatedBodyName` / `lastInterpolatedAltitude`, so F3 sets both from the held real
  vessel before the call (absent, the fade returns without a write and the smoke stays as built).
  So F3's step 3 is: `state.visualFxSuppressed = false`, `ApplyUnsuppressedFxRestore(state,
  true)`, `GhostPlaybackLogic.UpdateAudioAtmosphere(state)`. Proof: the inline fold of the
  static back into `ApplyFrameVisuals` equals the old body instruction for instruction and local
  for local, with one documented operand change (the restart predicate's first argument,
  `ldarg suppressVisualFx` -> `ldc.i4.0`, which is false on that branch).
- `GhostFxFingerprint.BuildEntries(EngineGhostInfo)` / `BuildEntries(RcsGhostInfo)`: one
  `FormatEntry` line per live particle system, in build order, read straight from the info and
  never rate-limited. `LogInfo` reads its lines through the same loop
  (`BuildEntries(list, reader, out nullSystems)`, the Unity reads in a `NoInlining`
  `ReadSystem`). Proof: every operand of the old `LogInfo` body is in the new three bodies; the
  rest is the reading struct and the cached delegate.
- `RcsGhostInfo.particleThrusters` (`RcsParticleThruster { transformName, thrusterIndex }`),
  index-parallel to `particleSystems` as built, read through `TryGetParticleThruster` (false once
  teardown clears the systems). `thrusterIndex` is the transform's position among every
  transform of that name under the part prefab, depth-first pre-order, BEFORE the variant
  filter: stock's `ModuleRCS.thrusterTransforms` walk (`Part.FindHeirarchyTransforms` from the
  part's `model` child), so it is stock's nozzle index when the EFFECTS transform name equals
  the module's `thrusterTransformName` and every match sits under `model`. F3 is to check both
  per case (a mismatch is a setup fault, not a hide). Nothing in the build or playback reads it.
  Proof: `TryBuildRcsFX` and the `RcsGhostInfo` constructor are their old bodies with
  instructions inserted only (P3's check).
- The recorder source pin, `PartParityFxRecorderPinTests`: at every call site of
  `ShouldRecordEngineAsIgnited`, `ComputeRecordedEnginePower`, `CheckEngineTransition` and
  `ProcessRcsDebounce` in `FlightRecorder.EngineAndRcs.cs`, `BackgroundRecorder.PartEventPolling.cs`
  and every `*PartParity*.cs` under `TestCommands/` and `InGameTests/`, the arguments are the
  recorder's live reads (receiver name free), and `RcsDebounceFrameThreshold` is 8. No sweep file
  polls yet, so the sweep half binds when F3's driver lands. Correction to the paragraph above:
  `ParsekFlightPostSwitchAutoRecord` calls only `CheckEngineTransition` and `ProcessRcsDebounce`,
  and feeds the first `EngineIgnited && isOperational` and the raw throttle (an activity
  detector), so it is not the sweep's reference; the recorders are.

F3 as built (2026-11-23). The FX half of the sweep: `PartParityFxDriver` (three partials: the
press and the poll, the capture, the plate), `PartParityFxRun` (one case's state),
`PartParityFxHold` (the test stand), and the pure `PartParityFxFraming`, `PartParityFxControls`
(evidence flag, judge) and `PartParityFxStructure` (inventory diff). An FX case is a P6 wave case
with `WaveCase.Fx` laid over `WaveCase.State`: `TryPress` / `TryStep` hook P6's press and step, its
ghosts are P6's three routes, and an `fx:<bucket>` wave kind admits up to 4 of them. The
air run waited for F7 ("F7 as built" below). Decisions:

- Prime. `PrimeVisible` sets `lastInterpolatedBodyName` / `lastInterpolatedAltitude` from the held
  vessel, then E.2 step 3. Since ghost-fx-3 (2026-11-23) it also sets
  `lastInterpolatedSurfaceVelocity` (the held vessel's `srf_velocity`, source `Positions`), as
  playback's `SetInterpolated` does for a playing ghost: a jet's thrust share follows mach, and a
  ghost whose ground speed is unknown keeps its share. `ctl-fx-smoke-dropped` leaves the body name unset on both ghosts, so
  their smoke draws as built (F4 noted the control could not be met otherwise), and since 2026-11-24
  switches `GhostWorldSpaceEmitter` off on them, or the smoke is left behind in orbit as a real
  part's is and there is none to drop (PP-9 `2026-11-24_2040` read the control bad). A start ghost's
  "first frame at the target" is read again after the visible prime, which is the frame that lights
  its engine.
- A renderer counts as drawn only when its system plays or has particles: stock leaves idle systems
  enabled (the Hammer's `fx_exhaustSparks_yellow`). An undrawn entry is `{key, rendered: false}` in
  `fx.structure`. Particle reads and the plateau use drawn systems only, so a vacuum-hidden smoke
  trail's lifetime no longer sets the wait (LV-T45 real side: 1.3 s, where E0 measured 5.1 to 7.3 s).
- Controls write the `event` route only: 8 entries, real-vs-real with 8 samples a side (16 split).
  `warmupS` and `plateau` are written for a plume or rcs case only (a heat or lamp case waits for no
  plateau, so none reads as missed). `n/a` controls write no sidecar. `fx.plate.litPct` is a
  fraction; the plate atlas is P6's four rows with one view, `plate`; the ghost-all route is `all`.
- `ctl-fx-plume-short` shrinks the ghost's FX roots to 0.4 with
  `ParticleSystemScalingMode.Hierarchy`. E.5's recipe (start speed, lifetime and energy scaled) does
  not shorten a stock legacy flame, which has no start speed and is a stretched billboard whose
  length is its size and its velocity over lifetime: scaled by 0.5 the LV-T45 flame measured 0.77 of
  the intact one, by 0.35 0.73 with a hue failure on the saturated bottom view; shrunk, it measures
  0.42 with the colour intact.
- `ctl-fx-plume-shift` moves the roots, across and along, by the larger of 10 % of the body and 12 %
  of the near box (0.594 m each on the LV-T45). 10 % of the body (0.13 m across, 0.17 m along, about
  12 and 16 px) is under both lines (5 % and 10 % of a 359 px plume); as built it measures 56 px
  lateral and 59 px origin.
- `ctl-heat-events-dropped` skips a subject whose model lacks the clip its `ModuleAnimateHeat` names
  (`HasHeatClip`, `Part.FindModelAnimators`). Stock's two listed nose cones name `Cone_Heat` and
  neither carries it, so the real part never glows there. Subject on stock: `noseConeAdapter`
  (`HeatControlParts` gained it and `avionicsNoseCone`).
- The nozzle hide maps by index, not by name. Stock plays nozzle i's puff on emitter i of the
  EFFECTS node (`ModuleRCSFX.UpdatePowerFX(running, idx, power)` -> `Part.Effect(name, power, idx)` ->
  `ModelMultiParticleFX.OnEvent(power, idx)` over `FindModelTransforms(transformName)`, decompiled),
  so F2's `thrusterIndex` is the nozzle index whatever the transforms are called. Checked per case:
  every transform of that name sits under the prefab's model and the module has a nozzle of that
  index, else `rcs-input-mismatch`. This replaces F2's equal-names condition: on `RCSBlock.v2` the
  EFFECTS transform is `RCSjet` and the module's `RCSthruster` (9 and 9; 2 fired, 2 hidden), so a
  name check would fault the case, and a 0.1 m position match faulted it (the jet sits 0.22 m out,
  the nozzle near the block).
- A case that throws in warm-up, sample or measure is `crashed`; a control that captured no view
  writes no evidence flag and is judged `bad`. Strip and coverage tiles are made opaque before the
  alpha-weighted downsample. `LogHeat` writes a heat case's scalar, animation states and emissive
  colours at the capture (`states=0` on a part with no clip).

Acceptance (2026-11-23, stock-minimal, throwaway spec, fourth flight `2026-11-23_0728`, a DLL from
before #2792; evidence `<umbrella>/logs/flights/p8-f3/`). The 11 orbit controls are `ok` and the
merged F4 judge reads the label REPORT with no problem: every control and the sampled cases
recompute bit-identical. 0 setup faults, every case complete: `liquidEngine2.v2`, `ionEngine`
and `RAPIER` (closed cycle) at burn-full and burn-low, `solidBooster.sm.v2` burn-full, `RCSBlock.v2`,
`vernierEngine`, `standardNoseCone` and `noseConeAdapter` heat-medium and heat-hot, and `spotLight1`
on the plate (a point light by the optional control: `telescopicLadderBay`). Hold drift at render
time at most 0.00003 m. Per-side warm-up lines are logged for every plume and rcs case; one side
(ion burn-low, start route) missed the plateau at the 8 s cap and carries its tag. Teardown:
`step cleanup` reads `casesDestroyed=0 faults=0` on all 18 steps and the propellant cheat reads
`restored -> False` after each. Wall: 92 s for the LV-T45 with its 11 controls, 26 s for a pair of
engine cases, 3 to 20 s elsewhere. U9 re-measured after the light settle: the intensity and colour
gap is real (todo GHOST-LAMP-LIGHT-INTENSITY). The first readings of the ghosts are todo
PARTPARITY-FX-FIRST-READINGS-2026-11-23.

Follow-up (2026-11-23, branch `ghost-fx-2`, flight `2026-11-23_1007`): the lamp light, the engine
and one-nozzle RCS glow and the nose cones without their heat clip now read 0 colour px against the
real part on both routes (at most 3 px on a view; todo PARTPARITY-FX-FIRST-READINGS-2026-11-23).
`CaptureCase` brings the ghosts' nozzle glow to the current UT before every body view
(`AdvanceNozzleGlows`), as a playback frame does: the real nozzle keeps heating through the warm-up
while a primed ghost stands still. A lamp's `lightBrightenSpeed` is a rate, so a lamp at 0.3 (the
ladder's) has not settled after the case's `lightBrightenSpeed` + 0.5 s wait; its real intensity
reads low for about 10 s.

F7 as built, C# half (2026-11-23). An `fx=` run with `contexts=air` is an ordinary air run: its
reset and run steps are admitted by `host-not-at-pad`, and its FX waves keep F3's flow. The
`fx-runs-not-built` refusal, its token and its mirrors are gone. Any label works, so the second
label of PP-5 / PP-6 needs spec steps only (F7's lane half, after F6). Decisions:

- Spawn. `PartParityAirContext.SpawnHeld`: the chutes' band slots (up to 4, 40 m apart), 1600 m
  above sea level, at rest (0 m/s, `HeldDescentMps`) and not falling at 10 m/s, the exhaust axis
  straight down, so no plume points along the band.
- Hold. `HoldReadyCases` attaches the hold on each frame of the spawn wait, so a case never falls
  while the wave's others load; the first physics step snaps it to its slot. The air hold is a
  place of the SURFACE frame (`PartParityHeldSlot`: latitude, longitude, altitude and the
  surface-relative rotation, read through `GetWorldSurfacePosition` in doubles), not a pose
  relative to the host: a host on its gear sways, and 1.6 km away a 0.01 deg turn is 0.28 m.
- Air controls. Their subject, the Flea, breathes no air, so its only plume key is an orbit one
  and F3's `ControlKey` found none in an air run. `PartParityFxRules.ControlPlumeKey` takes that
  key into the air context.
- Spool timeout. `SpoolPressTimeoutSeconds`: 45 s, or 1.1 times the time stock's own spool needs to
  come within 0.01 of the request when that is longer, capped at 120 s. Stock closes on the request
  at `engineAccelerationSpeed` (decompiled `ModuleEngines.UpdateThrottle`): 0.12 /s is 38 s, but the
  Goliath's 0.06 /s is 77 s full and 57 s low, so 45 s would always end its two cases
  `state-timeout`. Every stock orbit engine keeps the 45 s floor.

Acceptance (2026-11-23, stock-minimal, throwaway spec, one flight
`2026-11-23_1014_PPS-10a-fx-f7-air-stock`, PASS attempt 1, DLL sha256 prefix 5059a943; evidence
`<umbrella>/logs/flights/p8-f7/`). Label `f7chute` (the Mk16 at semi and full, the P6 air controls)
ran first and label `f7fx` second on the same host, the PP-5 shape: both controls=ok. `f7fx`: all 14
jet cases reached, 0 setup faults, both ghost routes `applied` on every case, every side of every
part case plateaued inside the 8 s cap, and the four air controls `ok` in C# and in F4's judge
(recompute 10 cases, 0 mismatches, problems none). Spool to within 0.01 of the request: JetEngine
39.4 s full and 29.0 s low, RAPIER in air mode 23.5 and 17.5 s, Juno 38.8 and 28.8 s, Whiplash 23.5
and 17.5 s, Panther dry 9.2 and 6.8 s and wet 5.7 and 4.2 s, Goliath 77.0 and 56.9 s (both past 45 s).
Render-time drift 0.00000 m and 0.000 deg on all 18 cases, controls included; `stepM` at most 0.041 m
(the Flea). A case fell 1.0 to 3.7 m before its hold took it. The RAPIER's own trail is in the flame
layer on both sides: `burn-full` smoke core and envelope are 0 on the real, event and start sides in
all four views, flame core real / event 39344 / 37148 px on `fx-side-a` and 19447 / 27515 on `fx-far`
(todo GHOST-RAPIER-TRAIL-EXTENT). Teardown: cheat `restored -> False`, `casesDestroyed=0 faults=0`,
no Parsek ERROR, and the produced save holds the fixture's two vessels with the host's RCS group off,
as before. Wall: `f7fx` 336 s (waves 29, 84, 77, 114 and 32 s: the slowest spool of a wave sets it),
over E.8's 210 s estimate, inside one 480 s step. First readings: todo
PARTPARITY-FX-AIR-FIRST-READINGS-2026-11-23.

### E.3 Capture changes

- Layers. Each side's renderers are split by kind (pure `PartParityFxRules.ClassifyFxRenderer`
  over renderer type and shader name, the classification logged per case): `body` (mesh and
  skinned mesh renderers, the P2 / P6 capture unchanged, effects hidden), `flame` (particle
  renderers with an additive shader, and `Waterfall/` mesh renderers except the
  `Waterfall/Distortion` ones, which refract the frame behind them and emit nothing: 1 or 2 per
  Waterfall engine, E0 U7), `smoke` (particle renderers with an alpha-blended shader; a trail with
  an additive shader, the RAPIER's `fx_smokeTrail_aeroSpike`, is in `flame`, so a smoke core of 0
  does not mean no trail). A renderer
  that is disabled (`enabled` false) stays in its layer and draws nothing, as stock's vacuum
  smoke does (E0 U8). Each FX layer is rendered alone, so a body difference (judged by
  P2 / P6) never moves an effect measure; the body does not occlude the plume (stated in the
  viewer).
- Background and glow. Additive light is measured over black only: `flame` renders once over
  black and its RGB is the emitted light; `smoke` renders over black and white (D6) for coverage.
  The target stays `ARGB32`: every FX measure is computed in C# from the 8-bit atlas bytes it
  writes (E.4), so Python recomputes it exactly; additive overlaps that clip at 255 are counted
  per tile (`saturatedPx`), and a half-float target is not used (its values could not be
  recomputed from the file). Clipping is common (E0 U4): 25 % of the real LV-T45's core pixels
  and 64 % of the real Hammer's have a channel at 255, so colour and brightness are measured over
  the core's unsaturated pixels only (E.4). An additive render leaves alpha at 0, so flame
  coverage comes from RGB alone and the strip PNG writes its alpha as 255.
- Culling mask. P6 builds it from the layers of the mesh records (`PartParityCaseCapture`); an FX
  capture's mask is the union of the layers of every renderer that capture shows (body, FX
  layers, the plate), logged, so an effect object on its own layer (`TransparentFX` and others)
  is never culled.
- Time accumulation. After the plateau, N samples (default 16) per FX view, taken round robin
  over the views (one view per frame), each sample rendering every side's FX layers: about 9
  renders per sample frame for 3 sides (12 for an rcs case, with `ghost-all`). Per side, view and
  layer the case counts, per pixel, the samples in which it was covered (alpha byte 128 or more;
  for `flame`, the max channel at `FlameValueMin` or more) and sums their colour.
  Each sample also reads every side's live particles (`ParticleSystem.GetParticles`): count,
  speed, direction, size and the 95th percentile distance along and across the exhaust axis in
  part space. Odd and even samples are also kept apart (split halves, E.4).
- Framing. The FX box is NOT built from renderer bounds (E0 U8: in orbit the real side's
  world-space systems hold particles kilometres from the part, so their renderer bounds were
  centred 6 to 9 km away and an 80 m clamp along the axis still framed empty space). It is the
  body box stretched along the exhaust axis to the 95th percentile along-axis distance, over the
  plateau wait of every side, of the particles within 200 m of the nozzle, clamped to 80 m (flag
  `fx-framing-clamped`), and widened to the same percentile across the axis, at least the body's
  width. Views: `fx-side-a`, `fx-side-b` framed on the body box stretched along the exhaust axis
  to three body lengths (the flame at the nozzle), `fx-bottom` on that box twice as wide (the
  flame's cross-section is wider than the body), and `fx-far` (the `side-a` direction) on the FX
  box (the smoke trail). Ortho; clip planes from the box used.
- Hide everything else. A 50 m smoke trail can reach a neighbour slot and the host, and the
  culling mask is layers only. The FX capture's hide set is every renderer of every loaded vessel
  and every live ghost (gathered at the wave start, refreshed per case), not only the case's
  sides. `ctl-empty` runs inside a burning wave to prove it.
- Strips. For the viewer, K = 12 frames of `fx-side-a` per side (body and effects together, as a
  player sees them, over dark grey), one frame every second sample frame, in one PNG per case.

### E.4 Comparing stochastic effects

C# measures and Python decides (D7). Every FX measure is computed in C# from the 8-bit FX atlas
tile it writes, never from the float accumulators, so Python recomputes it to the same value:

- The tile: alpha = `floor(255 k / N + 0.5)` (k = covered samples), RGB = the covered samples'
  mean colour, rounded the same way; the sidecar stores N, so Python recovers k exactly
  (`round(alpha N / 255)`, a bijection for N up to 255).
- CORE = pixels with k at least N / 2 (`FxCoreFraction` 0.5); ENVELOPE = k at least 1.
- Luminance uses the Rec. 709 weights (0.2126, 0.7152, 0.0722); hue is the HSV hue of the
  8-bit mean colour in 12 bins (`FxHueBins`) weighted by luminance; chromaticity is (r, g) over
  r + g + b.
- Every constant (`FxCoreFraction`, `FlameValueMin`, `FxAlphaCovered = 128`, `FxHueBins`, the
  weights, the thresholds below as defaults) is declared in `PartParityFxRules` and mirrored in
  `partparity.py` by F1, so the measure code (F1b) adds no mirrored literal.

The sidecar carries each view's projected nozzle point and exhaust axis (`axis.originPx`,
`axis.dirPx`), so every derived measure is recomputable from the atlas and the sidecar alone.
Per route (event, start), FX view and layer:

| Measure | Definition | Views | Fail (starting guess) | Warn |
| --- | --- | --- | --- | --- |
| presence | core px on each side; `fx-missing` / `fx-extra` when one side has at least `FxPresenceMinPx` = 64 and the other 0 | all | either | - |
| origin | where the envelope starts along the axis, minus the nozzle point | side, far | more than max(4 px, 10 % of the real length) | half that |
| lateral offset | distance of the core centroid from the axis line, ghost minus real | side, far | more than max(4 px, 5 % of the real length) | half that |
| length | envelope extent along the axis from its origin | side, far | differs by more than 25 % of the real | 12.5 % |
| width | largest envelope extent across the axis | side, far | 30 % | 15 % |
| direction | angle between the axis and the line through the centroids of the core's upstream and downstream halves, ghost minus real (F1b as built; the rev 2 wording was the origin-to-core-centroid line) | side, far | 10 deg | 5 deg |
| cross-section | core area ratio ghost / real, and the core centroid's offset from the nozzle point over the nozzle box diameter | bottom (the axis view: the axis projects to a point, so length, width and direction are not defined there) | area outside [0.6, 1.6]; offset over 0.1 | area outside [0.8, 1.25] |
| colour | chromaticity distance of the core's mean colour; L1 distance of the hue histograms over the envelope; both over unsaturated pixels only (no channel at 255, E0 U4) | all | 0.06; 0.5 | a quarter of each |
| brightness | mean luminance ratio ghost / real over the core's unsaturated pixels, beside each side's saturated share | all | - | outside [0.5, 2.0] |
| smoke | `smoke` layer core px on each side (`smoke-missing` / `smoke-extra` at `SmokeMinPx` = 256); an additive trail is judged in the flame layer | all | either | - |
| core IoU | of the two cores | all | - | under 0.5 (sorting) |

Weak cores (F6, 2026-11-23). Where neither side's core reaches `PresenceMinPx` (64), on either
layer, only presence is judged: origin, length and width read the envelope, and the envelope of a
layer with no core is stray particles. Found by PP-8's first reading flight (`2026-11-23_1112`):
`ctl-fx-real-vs-real` compared the ReStock Flea's smoke with itself, cores 0 and 0, envelopes 1570
and 3680 px, and failed origin (-29 px) and length (1.37). One weak core is not enough: there every
measure stays judged and presence keeps its rule above. Nor do the split halves of such an entry
tag `fx-unsettled` (its stray particles would cap a fail elsewhere at `warn`). A weak core never
passes silently: the
Python flag `fx-weak-core:<route>:<view>:<layer>:real=<n>,ghost=<n>` raises the case to at least
`warn` on every entry where both cores are weak and anything is drawn (an envelope pixel on either
side), and on every entry where exactly one is weak and presence does not decide (the weak core is
1 to 63 px, or a smoke core is under `SmokeMinPx`). `PartParityFxMetrics.BothCoresWeak` is the C#
half (the control judges read `FailingMeasures`, so they follow the same rule), mirrored by
`fx_both_cores_weak`. Re-judged offline: PP-7 (`2026-11-23_1027`) carries no such entry and moved no
verdict; PP-9 (`2026-11-23_1050`) carries it on 18 of 165 cases and moved three from `warn` to `fail`
(`liquidengine--size1--burn-low`, `liquidengine.v2--trussmount--burn-low`,
`radialliquidengine1-2--shrouded--burn-full`: only weak smoke halves had capped them).

FX instrument fixes (2026-11-23, todo PARTPARITY-FX-INSTRUMENT-2026-11-23), decided offline on the
archived F6 sweeps; no fail line and no threshold moved.

- The unsettled cap. The whole-case cap hid 17 fails on PP-7, 67 on PP-9 and 17 judged (53 would-be)
  on PP-8, and not one of those 154 cases had its only fail in an entry that did not settle: 121
  failed on the body routes or a missing / extra asset, where no sample is read (PP-9: 54 of 67, the
  ReStock nozzle glow), the other 33 on entries that had settled. So the cap is per entry. In a case
  with an unsettled entry a fail stands only if its own entry settled, both sample halves fail on the
  same side of the same line (`FxFinding.side`), and neither side fails that measure against itself,
  even against odd samples (`fx_self_failing`); otherwise it counts as `warn` and its finding says
  `capped`. The smoke of a route is one world-space trail, so smoke unsettled in one view is
  unsettled in all (PP-9's three boosters whose only fail is the orbit smoke noise stay capped). Body
  routes, structure and settled cases are judged exactly as before. `REASON_FX_UNSETTLED` is written
  when only capped fails keep a case from `fail`.
- Its noise. One thing captured twice (the event ghost against the start ghost at full thrust; the
  real side, and the ghost, of a reading flight against the full one; PP-8's real side against
  PP-9's on the parts without Waterfall) must not fail. Cases failing: plumes, 237 pairs: 12 with no
  cap, 4 under the old cap, 5 under this one; rcs and cross-install, 71 pairs: 24, 8, 11. What is
  left is the cross-section offset of small RCS puffs and `liquidEngineLV-T91`'s orbit smoke, at
  fail lines that the old cap let settled cases fail on too.
- More samples were rejected. A case's 64 sample frames take 5.0 s (PP-7) and 5.3 s (PP-8, PP-9), so
  32 samples add 442, 694 and 691 s (44 to 52 % of the lane), and two independent captures of the
  same ghost still disagree past 10 % at 16 against 16 samples on 2.6, 5.3 and 3.7 % of the entries
  (8 against 8: 2.8, 7.8 and 6.2 %): 4, 16 and 9 cases. Length and width are envelope extents, an
  extreme of sparse particles, which samples settle slowly.
- Re-judged: PP-7 (`2026-11-23_1027`) 34 pass / 33 warn / 60 fail / 1 error to 34 / 16 / 77 / 1, PP-9
  (`_1050`) 28 / 71 / 64 / 2 to 28 / 7 / 128 / 2, PP-8 (`_1232`) 27 / 21 / 20 / 2 to 27 / 7 / 34 / 2,
  its 95 references from would-be 42 fail / 53 warn to 95 fail. Every move is `warn` to `fail`; no
  control outcome moved on any archived run.
- The real-vs-real controls take 32 samples, 16 a side (`SampleRounds`, 5 s more per sweep). They
  compared 8 against 8 at lines set for sides of 16, and the air one is a coin toss there: the Flea's
  `fx-far` flame width against itself read 1.264 (`2026-11-23_1014`, ok) and 1.345 (`_1201`, bad,
  INVALID(parity-controls)), line 1.30, both entries unsettled. Not flown.
- A skipped entry (weak cores, above) reads `skipped`, not `pass`. It ranks as `pass`; the
  `fx-weak-core` flag still raises the case.
- `fx.layers`, the inventories and `fxDiffers` are read when the case is captured, after the
  warm-up (`DescribeLayers`): in the frame the ghosts are primed their KSPParticleEmitters have no
  material and no power's size or speed yet. Report-only. `fxMissingOnGhost` / `fxExtraOnGhost`,
  which are judged, keep their read beside the prime (`ReadStructure`).
- The plateau cap follows the longest particle lifetime (`PlateauCapFor`: 1 s past it, 8 s at least,
  20 s at most). Rule (a) of E.2 waits for that lifetime, which a flat 8 s could not meet: 17.5 s on
  stock's Clydesdale, Pollux and Thoroughbred, 15 s on the Twin-Boar family and the Mammoth, 10 s on
  ReStock's Pollux and Thoroughbred; 7.5 s left one window. Cost, from the logged waits: 45 s on
  PP-7 (6 of 23 waves), 4 s on PP-9 (3 of 33), 6 s on PP-8 (7 of 33). The wave bound
  (`WaveWorstCaseSeconds`) counts the 20 s. The other `fx-not-plateaued` cases (13 to 50 particles,
  lifetimes of 5 s or less) never hold 10 % for a second: no cap fixes those, and their tag stays.
- The plate is the light's own footprint (`PlateSizeM`): the smaller of the cone's radius and the
  range sphere's, not the cone's alone. `stripLight1` (170 degrees, range 15 m stock, 10 m ReStock)
  goes from an 89 m plate to 38 m and 25 m, so its lit share goes from 1.1 and 0.5 % to 6.0 and
  6.8 %, over the 2 % floor; ReStock's `domeLight1` from 26.3 to 24.8 m; every other lamp's plate on
  the three installs is unchanged.

For an rcs case, length, width and brightness are reported only (R4, E.2). Particle statistics
(count, speed, direction, length and width percentiles; the two-sided form of the existing
ghost-only `[FxEmissionProbe] measured:` line) are written and shown, report-only in P8: they are
not recomputable from the files, and become judged only after a reading run shows them as tight
as the picture measures.

How the instrument proves itself:

- Noise floor. `ctl-fx-real-vs-real` compares the real plume with ITSELF in alternate samples
  (even samples one side, odd the other) and must pass every FX measure; the starting thresholds
  are re-pinned from it and the controls after the first reading run, with no flight.
- Broken plumes, against an INTACT GHOST. Each broken-plume control builds two event-route ghosts
  of one case, breaks one after the visible prime, and compares the intact ghost with the broken
  one, so a control's outcome never depends on whether that part's ghost matches its real part
  (E.5).
- Convergence per case. Every measure is also computed on each half of the samples (from tiles
  of the same 8-bit rule, numbers only, block shape checked by Python). When the halves disagree
  on presence, or by more than `FxSplitHalfPct` (10 %) on length or width, the case is tagged
  `fx-unsettled` and the ENTRY (route, view, layer) is capped at `warn`: a measure that did not
  converge never fails ("FX instrument fixes" below; until then the whole case was capped).
- Python recompute (6): the sample of FX cases is recomputed from the FX atlas to exact equality
  of every count, extent, origin, offset and histogram bin count (centroids, ratios and
  distances within 1e-9, as P1's centroids).
- Structure, deterministic. Each side's FX inventory formatted by the existing pure
  `GhostFxFingerprint.FormatEntry` (canonical asset name, parent, local placement, emission
  direction, size and speed multipliers): the real side from its live particle systems, every
  ghost from its own `state.engineInfos` / `state.rcsInfos` directly (F2 adds the pure
  `GhostFxFingerprint.BuildEntries(info)` the builder's `LogInfo` then calls; the
  `[FxFingerprint]` log line is `VerboseRateLimited` on `fp-<kind>-<part>-<midx>` for 5 s, so the
  second and third ghost of a case, and the `burn-low` case of the same part, would be swallowed).
  Paired by canonical name and parent: `fxMissingOnGhost` / `fxExtraOnGhost` / `fxDiffers`. A
  missing asset is named even when the picture hides it.

A case's verdict is the worst of its body routes judged by the P6 judge (D.6: the throttle glow,
the stowed reference and every P6 flag apply), its FX measures per route, and a rendered FX
asset missing or extra on the ghost (the structural raise of 6). Each reason names its route,
view and layer (`event:fx-far:smoke:smoke-missing`).

F1b as built (2026-11-23). The pure measures live in `PartParityFxMetrics.cs`
(`Source/Parsek/TestCommands/`); they hold no constant of their own (every one is
`PartParityFxRules`'). The only contract change is one key, `nozzleDiameterPx`, added to the
existing `axis` table (`PartParityJson.FxAxisKeys`, mirrored by `partparity.FX_AXIS_KEYS`):
the cross-section offset needs the nozzle box diameter in the view's pixels and nothing else in
the sidecar carried it. Definitions, which a Python recompute transcribes:

- Accumulation (E.3). `PartParityFxAccumulator` per side, view and layer: a sample is RGBA8,
  rows top-down, the tile's size (a flame sample the render over black, a smoke sample the
  straight-alpha matte of its two renders); `SampleCovered` decides each pixel; covered samples'
  channels are summed as integers; `ToTile` writes `TileChannel` RGB and `TileAlpha(k, N)`,
  (0, 0, 0, 0) where k = 0. More than `MaxSamples` refuses. `PartParityFxSampleSet` keeps the
  whole set and the 0-based even (0, 2, ...) and odd samples, so 16 samples split 8 / 8.
- Coordinates. x is the column, y the row, a pixel's centre (x + 0.5, y + 0.5). With
  u = dirPx / |dirPx| and o = originPx: `t = (cx - ox) * ux + (cy - oy) * uy` (along the axis
  from the nozzle), `s = (cy - oy) * ux - (cx - ox) * uy` (across it). The axis view
  (`fx-bottom`), a null origin or a zero direction has no axis.
- One side (`MeasureTile`, every key of `FxSideMeasureKeys`), k = `CoveredSamples(alpha, N)`:
  `corePx` (`IsCore`), `envelopePx` (k at least 1), `saturatedPx` (core pixels whose TILE colour
  has a channel at 255: the mean of the covered samples, the only colour the file holds);
  `originPx` = min t, `lengthPx` = max t - min t, `widthPx` = max s - min s, all over the
  envelope (null with no axis or no envelope); `coreCentroid` = `((Sx + 0.5 n) / n, (Sy + 0.5 n) / n)`
  over the core's integer index sums; `meanColor`, `luminance` (`Luminance` of the integer channel
  sums, divided by the count) and `chromaticity` (`[R, G] / (R + G + B)` of the sums) over the
  core's unsaturated pixels; `hueHistogram`: 12 bins over the envelope's unsaturated, non-grey
  pixels, each bin `Luminance(sum r, sum g, sum b)` of its pixels. The hue bin is integer
  arithmetic (`HueBin`): with d = max - min, `hn = g - b` (plus 6d when negative) when r is the
  max, `b - r + 2d` when g is, else `r - g + 4d`; bin = `12 hn / (6d)` (floor), a grey pixel has
  none.
- Compare (`Compare`, every key of `FxCompareKeys`, ghost minus real; null where undefined): on
  a side view `originDeltaPx`, `lateralOffsetPx` (s of the ghost core centroid minus s of the
  real one), `lengthRatio`, `widthRatio` (null when the real extent is 0), and `directionDeg`:
  each core is split at its centroid's t (upstream t < tc, downstream the rest), each half's
  centroid is taken from its integer index sums, the side's direction is
  `atan2(s_down - s_up, t_down - t_up)`, and `directionDeg` is ghost minus real wrapped into
  (-180, 180] (null when a half is empty). That replaces the E.4 table's
  "origin-to-core-centroid line": a line drawn from a point on the axis moves with a lateral
  shift (a shifted plume would also fail direction), a line from the plume's first pixels
  starts at a corner when the plume turns, and the core's principal axis (tried first, rejected
  in review) is ill-conditioned for a near-round core (a perfect 11 x 10 px puff read up to
  90 deg between its own sample halves). The half-centroid line is unchanged by a translation or
  a length change, moved by a turn, and stays well conditioned (worst 6.5 deg, under the 10 deg
  fail line, for a 9 x 8 px core against itself over 40 seeds; `ARoundCore_AgainstItself_*`),
  so a compact core can still warn on direction from noise alone, which the reading runs
  re-pin. On the axis view: `crossSectionAreaRatio` (ghost core / real core) and
  `crossSectionOffset` (the distance between the two core centroids, which is the difference of
  their offsets from the nozzle point, over `axis.nozzleDiameterPx`). On every view:
  `chromaDistance` (Euclidean, chromaticity), `hueL1` (the histograms each normalized to sum 1,
  0 to 2; null when either sums to 0), `brightnessRatio` (luminance) and `coreIou` (1 when both
  cores are empty).
- Fail lines (`FailingMeasures`), for the C# FX control judges only (case verdicts stay
  Python's, F4): the flame layer judges `presence` and the smoke layer `smoke`
  (`PresenceReason`: one side's core at `PresenceMinPx` / `SmokeMinPx` or more and the other's
  0, giving `fx-missing` / `fx-extra` / `smoke-missing` / `smoke-extra`); both layers judge
  origin, lateral offset, length, width, direction, cross-section and colour at the E.4 fail
  defaults; brightness and core IoU are warn-only and never listed; an rcs case drops
  `RcsReportOnlyMeasures`; a null measure never fails.
- Split halves. `PartParityFxViewResult.Measure` measures the whole set, and each half (the real
  half against the ghost half), from tiles of the same 8-bit rule; the halves are written as
  numbers only (`splitHalf.even` / `odd`, `FxHalfKeys`). `SplitHalfDisagreement`: the halves of
  one side disagree on presence (the presence rule above between the halves) or their lengths or
  widths differ by more than `SplitHalfPct` of the larger.
- Particles (`ReduceParticles`, report-only): `count` = mean particles per sample, every
  particle; over the particles within the near distance of the nozzle (all samples): mean speed,
  mean velocity angle from the axis (moving particles), mean size, and the nearest-rank
  `FramingPercentile` of the along-axis position and the across-axis distance. F3's framing can
  call it with `FramingNearParticleM`.
- RCS (R4): `FormatRcsReportedOnly` writes the reported-only length, width and brightness ratios
  beside `rcsPowerRatio`; `AtlasRowFor(side, layer)` names a side's FX atlas row.
- Exact recompute. Counts and bin sums are integers; t and s (so origin, length and width) are
  whole or half pixels only along an axis-aligned `dirPx` and otherwise floats; every value is
  computed from the written tile alone, in one order of operations, so a Python transcription
  reproduces it (exactly where integral, within 1e-9 otherwise). The committed fixture
  `Source/Parsek.Tests/Fixtures/PartParity/fx/` (`fxfixtureplume--default--burn-full--aae6c591`:
  `.fx.png`, 6 rows x 4 views of 64 px, 16 samples, and its `.part.json`, whose `fx.views[]`
  hold 16 entries) is rebuilt by `PARSEK_WRITE_PART_PARITY_FX_FIXTURE=1 dotnet test --filter
  WriteFxFixture` and pinned by pixels and by numbers (`Fixture_CommittedFilesMatchTheBuilder`;
  the PNG's deflate bytes are not the contract); its event ghost is short, its start ghost
  hue-rotated, shifted and smoke-less, its real core clips. A stdlib transcription of the
  definitions above reproduced all 528 side and compare values of the fixture bit for bit
  (2026-11-23); F4's recompute cells are that transcription.

### E.5 Case identity, controls, sidecar, gates

- Ids: the D.2 rule, `<part>--<skin>--<state>[--<context>]--<hash>`, base skin only (ruling R2
  carried over; a B9 `plumeSwitch` subtype only switches Waterfall templates, which the ghost does
  not apply). `liquidengine2.v2--default--burn-full--<hash>`,
  `turbojet--default--burn-low-m1--air--<hash>` (the D.1 same-word tiebreak),
  `spotlight1--default--lit--plate--<hash>`. Stowed, P6 state and P6 context ids cannot move.
- Args: `fx=none|all|<csv of plume, rcs, heat, plate>` (refused `fx-arg-invalid`), mirrored in
  hlib's `validate_part_parity_sweep_step`. With `fx=none` (the default) every run plans exactly
  what it plans today. A run with `fx=` plans ONLY the FX keys of its families (no stowed, no P6
  state case), filtered by `contexts=` (`air` plans the air-breathing plume cases, `none` the
  orbit ones), then filter / source / shard / limit.
- Every site that asks "is this a state run" learns `fx=`: `TestCommandPartParitySweep.IsStateRun`
  (read by `ControlSetFor`, `PlanCases` and `PartParitySweepRunner`'s key enumeration) gains
  `IsFxRun`; `StateSettings` writes `settings.fx` (`FormatFx`); Python's `control_set` reads it
  (a manifest without the key reads `none`), the writer cross-check (an FX case planned while the
  settings recompute a non-FX control set is INVALID(parity-instrument)) and the viewer's
  `control_set_kind` (`fx-orbit` / `fx-air`). F1 changes the C# and Python sides together.
- Controls (`ControlSetFor(args)`, mirrored by `partparity.control_set`). Every subject is picked
  by a pure, logged rule (`PartParityFxRules.ChooseControlSubject`): the first part of a
  preference list present on the install whose prefab carries no `ModuleWaterfallFX` and, for a
  plate control, whose prefab `Light` has the wanted type; a control with no eligible subject is
  recorded `bad` (INVALID(parity-controls)). Orbit FX run:

  | Control | Subject (preference) | Required |
  | --- | --- | --- |
  | `ctl-empty` | inside a burning wave, both sides hidden | zero coverage on every layer |
  | `ctl-ghost-absent` | ghost not built | ghost layers zero |
  | `ctl-fx-real-vs-real` | `liquidEngine2.v2`, `liquidEngine`, `sepMotor1`, `solidBooster.sm.v2` (stock-minimal and modded-restock: the LV-T45; modded-compat: the separation motor, "F6 as built" in E.8) | passes every FX measure (the noise floor) |
  | `ctl-fx-plume-off` | the same | real vs an event-route ghost built without the polled events: fails presence, while the real core is present and `EngineIgnited` was recorded |
  | `ctl-fx-plume-short` | the same | intact ghost vs a ghost whose FX roots are scaled to 0.4 with Hierarchy particle scaling AFTER the visible prime ("F3 as built" in E.2 says why not the particle fields): fails length, passes presence and colour |
  | `ctl-fx-plume-hue` | the same | intact ghost vs a ghost whose rendered `flame` pixels have their channels rotated (r, g, b) -> (b, r, g) in the readback, before accumulation. That leaves max-channel coverage exactly unchanged, so it fails colour and passes presence and length exactly. A material edit cannot do this (E0 U10): every stock flame tint is grey (0.537, 0.779, 1.0 or 0.5 in all channels), so rotating its channels changes nothing, and a (0, max, 0) tint changes coverage (by 46 % on the ion engine, whose colour comes from its particles) |
  | `ctl-fx-plume-shift` | the same | intact ghost vs a ghost whose FX roots are moved across and along the axis by the larger of 10 % of the body and 12 % of the near box: fails origin and lateral offset, passes length and colour |
  | `ctl-fx-smoke-dropped` | `solidBooster.sm.v2` | intact ghost vs a ghost with its smoke renderers hidden: fails smoke, passes the flame |
  | `ctl-heat-events-dropped` | `standardNoseCone`, `pointyNoseConeA`, `noseConeAdapter`, `avionicsNoseCone` (the first whose model carries the heat clip) | P6's state control at `heat-hot` without the polled event, with a COLOUR requirement instead of shape |
  | `ctl-plate-light-off` | a spot light: `spotLight1`, `spotLight2`, ... | the ghost's `Light`s disabled: fails on the plate, and the real side passes the plate floor. |
  | `ctl-plate-light-off-point` | a part whose prefab `Light` is a point light | the same for a point light; recorded `n/a`, never `bad`, when no part on the install has one: the seven light parts E0 U9 checked on both installs (`spotLight1`, `spotLight3`, `domeLight1`, `navLight1`, `stripLight1`, `groundLight1`, `groundLight2`) are all spot lights |

  Air FX run: `ctl-empty`, `ctl-ghost-absent`, `ctl-fx-real-vs-real-air` and
  `ctl-fx-plume-off-air`, subject `solidBooster.sm.v2` held in the air (every jet carries
  Waterfall on modded-compat, and the air controls only need to prove the instrument there).
- Sidecar (`parsek-part-parity-case/1`, additive, every key always written, null when not
  measured, key tables in `PartParityJson`, mirrored): an `fx` block with `family`, `module`,
  `moduleIndex`, `engineMode`, `throttle`, `drive` (`ignited`, `finalThrust`, `currentThrottle`,
  `recordedPower`, `cheat`), `warmupS` (`real`, `ghost`, `ghostStart`), `plateau` per side, `hold`
  (`maxDriftM`, `steps`), `rcs` (`input`, `fired`, `nozzles`, `ghostHidden`, `powerRatio`),
  `samples`, `framing` (`near`, `far`, `clamped`), `layers` (renderer keys per layer per side),
  `cullingMask`, per FX view and route the `flame` / `smoke` measure blocks with `axis` and
  `splitHalf`, `particles` per side, `structure` (the FX inventory diff and `lightsDiffer`),
  `inventory` (each side's `FormatEntry` lines), `plate` (`litPct`, the light's type, range, spot
  angle, `cullingMask`), `reference` (`waterfall` or null). `fxAtlas` (`file`, `rows`, `cols`,
  `tile`) and `strip` (`file`, `frames`). `stateProof` (D.2) holds the press, done signal, events
  and ghost reach, with `family` in {`plume`, `rcs`, `heat`, `light`}.
- Files per FX case: the body atlas (P6's four state rows), `<caseId>.fx.png` (rows from ONE pure
  literal, `PartParityJson.FxAtlasRows`: `real-flame`, `ghost-flame`, `ghost-start-flame`,
  `real-smoke`, `ghost-smoke`, `ghost-start-smoke`, plus `ghost-all-flame` / `ghost-all-smoke` for
  an rcs case; columns the four FX views), `<caseId>.strip.png` (rows real, ghost event; K
  columns). The harvest moves the label directory whole (5.1); only sidecars and the manifest go
  to the archive.
- Setup statuses: P6's `action-failed` and `state-timeout`, and the new `plate-not-lit` and
  `rcs-input-mismatch` (both `error`s, joining `SetupStatuses` / `partparity.SETUP_STATUSES` in
  F1); `fx-not-plateaued` is a
  tag (judged), `fx-unsettled` a cap.
- Gates: the press gate (D.6) counts the four families, so an engine that never ignites, an RCS
  that never fires, a heat write that never reaches its level or a lamp that never lights reads
  INVALID(parity-state-press) when more than half a family fails. No new INVALID class.
- F1 as built (2026-11-23). The contract lives in `PartParityFxRules.cs` (families and
  predicates, words, the `plate` context, exclusions, the RCS input rule, the control subjects,
  the renderer layer rule, the timeouts and the wave bound, every E.4 constant and threshold
  default, the 8-bit tile helpers, the FX control judges), the FX control ids and sets, setup
  statuses, `fx=` parse and `settings.fx` in `TestCommandPartParitySweep.cs`, and the FX key
  tables and `FxAtlasRows` in `PartParityJson.cs`; every literal is mirrored in
  `harness/lib/partparity.py` and hlib's arg table, pinned by `PartParitySourceSyncTests`'
  `test_the_fx_*` cells and `PartParitySweepSourceSyncTests.test_the_fx_arg_mirrors_the_c_sharp`.
  Decisions it took:
  - `fx=` names no `states=` and at most `contexts=air` beside it, else `fx-arg-invalid`
    (`FxCombinationValid`: a P6 state word or an orbit context would be silently ignored).
  - Until F3, a well-formed FX run was REJECTED `fx-runs-not-built` (`FxRunsBuilt`, hlib
    `PARTPARITY_FX_RUNS_BUILT`), the S1 pattern; F3 built the orbit run and F7 the air one, and
    F7 removed the refusal, its token and its mirrors.
  - `settings.fx` is written by an FX run only, so every PP-1 to PP-6 manifest keeps its
    settings byte for byte; `partparity.control_set` reads a missing `fx` as `none`.
  - The orbit FX run plans the `alone` and `plate` cases, the air FX run the `air` plume cases
    (`PartParityFxRules.KeySelected`). An air-breathing engine is detected by a `PROPELLANT`
    named `IntakeAir`; `fx-needs-air` is listed by an orbit run only (`ExclusionApplies`).
    `fx-reentry-camera` and `fx-blackbody` are in the reason table but no part writes them.
  - Each FX option carries two indices: `ModuleIndex`, the prefab index the same-word
    tiebreak and the case id read (`turbojet--default--burn-low-m2--air`), and
    `RecorderKeyIndex`, the index the recorder keys the case's events by: a plume or rcs
    module's position among the part's `as ModuleEngines` / `as ModuleRCS` modules (the
    recorder's engine and RCS caches, `EncodeEngineKey`'s `midx`; `RCSBlock.v2` is prefab 1,
    key 0), the prefab index for heat and lamps. F3 matches recorded events and builds keys
    from `RecorderKeyIndex`, never from `ModuleIndex`.
  - `ChooseRcsInput` ranks by the nozzles at half the strongest or more, then the smallest
    spread and the largest mean over EVERY nozzle that fires (above 0.01), so a weak extra
    nozzle counts against a direction. E0 U2 saw the spike's rule predict 3 nozzles on stock
    `RCSblock.01.small` where stock fired none; the rule does not explain that case, so F3's
    press check (`rcs-input-mismatch`) stays the authority.
  - A Waterfall part's plume and rcs cases carry `reference = waterfall`; its heat and plate
    cases do not (the plate case's flame layer is F4's to treat as reference).
  - The point-light control is optional: no eligible subject records `n/a`, which
    `ControlsState` counts as met (`OptionalControlIds`, `ControlsNotApplicable`). The Python
    controls check learns `n/a` in F4.
  - The sidecar gains `fx`, `fxAtlas` and `strip` (null on every non-FX case), written through
    `PartParityJson.KeyedBlock` from per-key JSON fragments, so F3 fills them without editing a
    mirrored table.
  - Committed part lists `Source/Parsek.Tests/Fixtures/PartParity/fx-parts-<profile>.txt`
    (derived from the state-parts fixtures and each install's ConfigCache) pin the E.1 counts:
    stock-minimal 43 + 31 orbit plume, 14 air plume, 7 rcs, 32 heat, 15 plate, 12
    throttle-locked, 7 needs-air, 8 ablator; modded-compat 60 + 42, 14, 19, 26, 18, 18, 7, 12,
    with 109 reference cases.
- F4 as built (2026-11-23). The judge is one block of `harness/lib/partparity.py` ("P8 F4: the
  FX judge"), called from the shared functions by name; `run.py` did not change.
  - Measures and recompute. `fx_measure_tile` / `fx_compare` transcribe "F1b as built"
    (stdlib); `FxMeasureTests` recomputes the committed fixture: all 528 values are
    bit-identical, and every tampered one reds. `recompute_fx_case` holds `corePx`,
    `envelopePx` and `saturatedPx` to exact equality and every other value to 1e-9, checks
    the atlas layout against `FxAtlasRowsFor` / `Views`, and refuses a pixel the tile rule
    cannot write (an alpha that is no sample count, a colour on an uncovered pixel). It runs on
    every control with an FX atlas and on its own seeded 2 % sample of the part cases with one
    (a run's mixed sample could draw only heat cases); the ids are listed as `<caseId>.fx`. The
    block shape is checked on EVERY captured FX case, halves included: every key, each
    expected entry exactly once at the atlas's tile size, a usable axis on every side and far
    view of a plume or rcs case (without one five measures are undefined, so unjudged), each
    value null exactly where the C# side defines it null, and every compare value the side
    blocks determine (all but `coreIou` and `directionDeg`) recomputed from them. So are
    `fx.family` against the state word, `stateProof.family` and the row family (it decides
    what is judged), an `ok` rcs case that did not fire exactly the predicted nozzles, an `ok`
    light case under the plate floor (both also `error`), and an rcs family whose every
    captured case drew nothing on its real side (no control has an rcs subject). Any of it is
    INVALID(parity-instrument).
  - Entries a stochastic case must write, else the case is `error` (`fx-no-entries:`) and the
    sweep INVALID: the `event` and `start` routes x 4 views x 2 layers (a control: `event`
    only), and on an rcs part case the route `all` (the `ghost-all` rows, never judged).
    `fx.samples` is one side's sample count (16 for a real-vs-real control too, which splits 32
    since 2026-11-23; 8 before).
  - Verdict. `judge_fx` gives each `event` / `start` entry its E.4 measures at warn and fail,
    the worst per route, reasons `<route>:<view>:<layer>:<measure>=<value>`; a layer one side
    lacks is named by its presence reason alone. A null measure never moves a verdict. The case
    is the worse of its body routes and its FX half; a rendered FX asset in
    `fxMissingOnGhost` / `fxExtraOnGhost` (a key string, or `{key, rendered}` unless `rendered`
    is false) raises it to fail. `fxDiffers` and `lightsDiffer` are counted, report-only.
  - `fx-unsettled` capped the WHOLE case at `warn`, its body routes included; since 2026-11-23 it
    caps its own entry ("FX instrument fixes" in E.4). Only judged entries of plume and rcs cases
    can unsettle.
  - Reference (E.6). A plume or rcs case with `fx.reference = waterfall` reads the verdict
    `reference` with its `wouldBe` beside it, body routes included, unless it is an `error`; it
    is counted under `counts.reference`, printed as ` reference=<n>` and never gates. Any other
    value, or a reference on a heat or light case, is INVALID(parity-instrument) and the case
    stays judged. The FX entries of a light case (a lamp's `flame` layer) are always reference.
  - RCS (R4). Length, width and brightness leave the verdict and land in
    `fx.rcs.reportedOnly` beside `powerRatio`; the flag `rcs-known-difference:*` is the written
    one, else derived from `fx.rcs`. Flags (`fx.flags`, none moves a verdict but the cap):
    that one, `fx-not-plateaued:<side>`, `fx-framing-clamped`, `fx-unsettled`, and four that
    raise to at least `warn` because a check did not run: `fx-structure-not-measured` (a null
    `fxMissingOnGhost` or `fxExtraOnGhost`), `fx-nozzle-not-measured` (no `nozzleDiameterPx`
    on the bottom view), `fx-no-real-effect` (an empty real side, as the static `JetEngine`
    draws) and, since F6, `fx-weak-core:*` (E.4, weak cores).
  - Controls. In an FX run every control recorded `ok` is re-judged from its sidecar
    (`judge_fx_control`): evidence = the `event` entries, a measure fails when any entry fails
    it. Stricter than `JudgeFxControl` in two ways: a plume control whose intact side has no
    flame core is `bad` (real-vs-real would pass on two empty pictures), and
    `ctl-fx-plume-off` needs an event of type `EngineIgnited`. `n/a` is met, and owes no
    sidecar, only for `ctl-plate-light-off-point`.
  - Press gate. The four families count as P6's do (family from the row, `stateProof`, else
    `fx.family`). An rcs press that fired NOTHING is `rcs-input-mismatch`, not a timeout (the
    RCS group books `RCSActivated` at zero input), so that status with an empty `fired` counts
    too (other fired nozzles than predicted do not), and so does `plate-not-lit` (a lamp that
    never lights).
  - Verdicts JSON: each FX case's `fx` block (family, reference, wouldBe, routes, entries,
    unsettled, flags, structure, rcs) and the document's `fx` (cases per family, reference and
    would-be counts, flag counts, thresholds). A sweep with no FX case writes what it wrote
    before.
  - Open for the first flights: `directionDeg` is noisy on a flat or compact core (2 to 4 px
    across: the halves' centroids differ by fractions of a pixel); measures on cores below the
    presence minimum got their guard in F6 (E.4, weak cores). Two
    orbit controls look unmeetable as E.5 states them: `ctl-fx-smoke-dropped`, because since
    GHOST-SMOKE-IN-VACUUM the intact ghost draws no smoke in vacuum, unless its two ghosts
    leave `lastInterpolatedBodyName` unset (smoke as built); and `ctl-fx-plume-shift`, whose
    shift (10 % of the body) stays under the fail lines (10 % / 5 % of the real PLUME's length)
    whenever the plume is longer than the body.

### E.6 Waterfall installs and the Waterfall-free profile

Owner ruling 2026-11-21 (todo RESTOCK-GHOST-SKIRT-AND-CHUTE): no Waterfall on ghosts. On
modded-compat the real plume of every Waterfall-patched engine and RCS part is Waterfall meshes,
and the ghost shows the particles `WaterfallCompat` / `PristinePartFxResolver` /
`ReStockPatchFxIndex` recover from the pristine configs. Those cases are REFERENCE-ONLY: captured,
measured and shown exactly like a judged case, the would-be verdict computed and displayed, but
the verdict class is `reference`: never `fail`, never gated, counted apart (` reference=<n>` on
the row's summary line, a viewer badge "Waterfall reference, not judged"). On modded-compat that is
nearly every liquid, ion, nuclear and monopropellant engine, every jet, every RCS part and the 7
Waterfall lamps' `flame` layer; the solid boosters, the heat cases and the plate pictures of the
other lamps stay judged. Comparing against Waterfall (it fails by construction) and building
Waterfall on ghosts (it reverses the ruling) were rejected in rev 1.

GHOST-WATERFALL-LIGHT-FX-MISSING (open, filed 2026-11-22 on branch `ghost-light-airbrake`: a lit
lamp's Waterfall flare billboard and beam cylinder are not drawn on its ghost) folds in here: the 7
Waterfall lamps' `plate` cases carry the Waterfall renderers in their `flame` layer as reference,
so P8 measures the missing flare and beam on every such lamp without judging it; if that ghost
feature is ever built, those cases are its acceptance test.

The Waterfall-free profile (R6), so ReStock's own particle plumes are judged on ReStock parts:

- `harness/provision/profiles/modded-restock.toml`: modded-compat's profile with the four
  Waterfall folders removed from `devSourcedMods` (`Waterfall`, `WaterfallRestock`,
  `RestockWaterfallExpansion`, `StockWaterfallEffects`); everything else identical: ReStock,
  ReStockPlus, B9PartSwitch, Kopernicus, ModularFlightIntegrator, KSPCommunityFixes, the other
  dev-sourced mods, the same stack components, the same pinned optional mods
  (`PersistentRotation`, `SpaceTuxLibrary`), the same `[settings]`. `instanceDir =
  "automation/modded-restock"`. No new pin: every folder comes from the dev install or the
  existing pins.
- Wiring: `harness/lib/slots.py` `PROFILE_PORT_BASE["modded-restock"] = 50060` (the next free
  20-port block); `hlib.INSTANCE_PROFILES` and `FULL_RUN_PROFILE_ORDER` (after modded-compat);
  `harness/tools/tier_runner.py`'s profile lists; the provisioner's profile cells
  (`test_provlib.RealProfileFileTests` and the unattended-settings / vessel-budget cells read
  every profile file, so the new file is checked by them at once) plus one new cell: the profile
  equals modded-compat's except the four Waterfall folders and `name` / `instanceDir`, so the two
  can only drift on purpose. The D17 registry dimension stays modded-compat's.
- What it does NOT change: the Waterfall pristine fallback stays closed on it (`WaterfallCompat`
  finds no Waterfall assembly), so its ghosts take the stock / ReStock EFFECTS path, the same one
  the stock install takes. It also serves the `[FxFingerprint]` A/B of `MC-1-waterfall-compat`
  ("config pack disabled vs enabled") as a standing pair, a side benefit.
- Provisioning (`provision.py --profile modded-restock`; done 2026-11-22 on owner ruling R6) is about one
  more modded-size install (about 4.3 GB RAM and 1 GB VRAM per running copy, D.8's measure).
- Lanes on it (E.8): PP-9 (orbit FX) and PP-10 (air jets). Nothing else is required there: a
  ReStock part's models do not change without Waterfall (Waterfall adds only its FX meshes, which
  P3 already treats as non-visual), so the PP-2 / PP-4 / PP-6 sweeps are not repeated on it.

### E.7 Viewer

- An FX case joins its part's skin like a P6 state case (D.7), its family after the P6 families.
  Its body rows are P6's (STOWED | REAL | GHOST event | GHOST start | DIFFERENCE). A profile
  name `modded-restock` is a profile like any other (cross-profile links reach the same part on
  modded-compat and stock).
- FX rows: per FX view and layer, REAL | GHOST event | GHOST start | DIFFERENCE of the accumulated
  tiles (mean colour, coverage frequency as alpha, over black for `flame`, over the page's matte
  for `smoke`), at the same crop, with the view's chips and trigger.
- Plumes side by side in motion: the strip PNG's rows play as a CSS `steps(K)` animation over
  `background-position` (no canvas, so it works on `file://`), REAL and GHOST in lock step from
  one shared animation start, with a play / pause toggle and `prefers-reduced-motion` honoured.
  GIF / APNG stay with P7 (9.2).
- The RCS known difference (R4) has its own row under the judged ones, captioned "known
  difference (owner ruling 2026-11-22): the ghost fires all <total> nozzles at the module's mean
  power <p>; stock fired <fired>", showing REAL | GHOST all nozzles with a dashed border, its
  measures greyed and never coloured as a verdict, and the reported-only length / width /
  brightness of the judged row with `rcsPowerRatio` beside them. The trigger line of a case never
  names it as a failure; a filter chip "known difference" lists every such case.
- Details: the FX measure table per route, view and layer (with the split halves), the particle
  statistics (report-only), the FX inventory diff, `lightsDiffer`, the plate floor, the drive,
  hold and warm-up proof.
- Filters: Family gains `plume`, `rcs`, `heat`; a Reference filter (`judged` / `reference`); FX
  flags (`fx-unsettled`, `fx-not-plateaued`, `rcs-known-difference`). Regression keys
  `<caseId>/fx/<view>/<layer>[/start]`.

F5 as built (2026-11-23, `harness/tools/part_parity_view.py`; Python only, no flight). Every FX
verdict, reason, flag and reported-only value on the page is `cv.fx`'s; the viewer decides none.

- Case rows. A row's `fx` (null on every other case) is `fx_row`: the judgement, every captured
  entry with both halves (the hue histogram left out, its comparison is `hueL1`; values at six
  significant digits), the FX atlas and strip, the proof, the inventory diff and the `rcs` block.
  An FX case joins its skin in `FAMILY_ORDER` / `STATE_ORDER` / `CONTEXT_ORDER`: P6's, then `plume`,
  `rcs`, `heat`; a lamp keeps P6's family `light`, context `plate`.
- Rows. Under the body rows: the strip, then one row per layer and FX view. A layer nothing draws on
  any side (the smoke of most orbit plumes) has no row; one line names those. A control's rows are
  its `event` pair with the measures that fail, no verdict chip (an altered side is meant to fail).
  Every FX crop paints its own opaque ground under the picture (black for flame, the matte for
  smoke), so the difference of two equal, partly covered tiles is black.
- Strips. The two rows are two layers of one image with the same `steps(K)` keyframes, duration and
  step count, inserted together and run by one class on `<body>`, so they start, pause and resume in
  one style change. The Play / Pause button is in the header; with `prefers-reduced-motion` the
  strips start paused. A click opens a strip enlarged, any FX picture at 1:1 (an rcs case adds
  its `ghost-all` tile).
- RCS. The dashed block reads `rcs-known-difference` with E.7's caption, per layer and view REAL |
  GHOST all nozzles, the `all` entry's measures and the judged routes' reported-only values beside
  `rcsPowerRatio`, all greyed; the chip and the caption hang on the case's flag. A module whose every nozzle fired at the recorded power (the
  vernier, whose written flag is `nozzles=1/1,power=1.000`) says so and stays in the filter, which
  lists every case that carries the flag.
- Verdicts outside the judged four. `reference` has its own counter and filter entry, a dashed
  border, and ranks below every judged verdict (a part reads `reference` only when all its cases do;
  "flagged" leaves it out); its trigger reads `reference, would be <verdict>: ...`. An optional
  control's `n/a` is `control-na`: neither ok nor bad, no sidecar expected. A required control's
  `n/a` stays bad, and a control recorded `ok` in an FX run follows `judge_fx_control`.
- A trigger names what decided: fail-level FX reasons before warn-level ones, the FX half first only
  when it is worse than everything else that judged the case (`judge_case` without the `fx` block:
  routes, skin, state flags, setup).
- A damaged sidecar never costs the page: odd types read as absent, a NaN or an infinity as null, and
  a writer check that cannot read a value is listed as a problem. A 52k-mutation fuzz of seven real
  sidecars raises nowhere the pre-F5 viewer did not.
- Header. The Family and State filters gain the FX words, `Reference` and `FX flags` appear with an
  FX case; the writer checks include `fx_run_problems` and `fx_block_problems`; the sweep box counts
  the effect cases and prints the FX lines. `control_set_kind` knows `fx-orbit` and `fx-air`.
- Regression. Per case: the FX verdict and would-be verdict, the FX flags, the rendered effects the
  ghost lacks or adds. Per entry key: the verdict, a layer that came or went on either side, and each measure
  that moved by more than its own warn line (`REG_FX_MOVES`; a smaller step is re-run noise). The
  `all` route has no key.
- Tests run the page's own script under node against a small DOM (`FxPageTests`, skipped without
  node) and read back the strips, rows, crops, chips, block, details, filters and 1:1 dialogs.
- Seen on F3's acceptance evidence (`2026-11-23_0728`, nine labels as one profile, 25 cases): plumes
  and puffs on every engine and RCS row, both strip rows at one frame in every reading (equal start
  times and background positions), the `RCSBlock.v2` block (9 nozzles at 0.157, stock fired 2,
  ratio 0.222), controls 11/11 ok with each broken plume failing its own measure. The labels after
  the first ran with `controls=false`, so the sweep box is red for them. Screenshots in
  `<umbrella>/logs/flights/p8-f5/`.
- Open (the writer's, report-only): `fx.layers` lists some ghosts' drawn flame emitters under
  `smoke` (`ionEngine`, `RAPIER`, `RCSBlock.v2`, `vernierEngine`: flame 0), while the atlas draws
  them in the flame rows and leaves the smoke rows empty. Nothing is judged from `fx.layers`.
  (Read after the warm-up since 2026-11-23, "FX instrument fixes" in E.4.)

### E.8 Lanes and wall time

Measured basis: P6 orbit state cases ran about 3.3 s per case (D.8); P0 measured about 9.4 ms per
render. Measured by E0 (U6, 512 px tiles): one layer render costs 0.16 to 0.20 ms and its
readback (resolve blit, `ReadPixels`, `GetPixels32`) 1.7 to 2.5 ms, so a sample frame of 6 to
9 renders takes 25 to 35 ms and 64 sample frames about 2 s. The warm-up dominates: the real side
plateaus 5 to 7 s after the press and the event-route ghost 4 to 8 s after its prime (E.2), and a
lone orbit plume case took 11.5 to 12.2 s from spawn to teardown in the spike. Four held cases
share one spawn, settle, plateau wait and teardown, then capture one after another: about 18 s
per wave of 4, about 5 s per case, under the 10 s per plume or rcs case the table keeps as its
margin. Jets wait for their spool (up to 45 s, E.2), about 15 s per case in a wave of 4. Heat
and plate cases run at the P6 rate.

| Lane | Install | Cases (stochastic + deterministic + controls) | Sweep estimate | Steps (480 s, +50 %) | `budgetSeconds` |
| --- | --- | --- | --- | --- | --- |
| PP-7 (new, label `pp7`), orbit | stock-minimal | 74 plume + 7 rcs; 32 heat + 15 plate; 11 | about 1080 s | 4 | 2700 |
| PP-8 (new, `pp8`), orbit, mostly reference | modded-compat | 102 plume + 19 rcs; 26 heat + 18 plate; 11 | about 1470 s | 5 | 3300 |
| PP-9 (new, `pp9`), orbit, all judged | modded-restock | about 102 plume + 19 rcs; 26 heat + 18 plate; 11 | about 1470 s | 5 | 3300 |
| PP-5 / PP-6 (one more label, `pp5fx` / `pp6fx`), air | stock-minimal / modded-compat | 14 jet cases (all reference on modded-compat); 4 | about 210 s | 1 | 1800 (from 1020) |
| PP-10 (new, `pp10`), air | modded-restock | 14 jet cases; 4 | about 210 s | 1 | 1200 |

Orbit FX gets its own lanes, not a third label in PP-3 / PP-4: the cheat, the holds and the
burning waves are a new risk to a host the state sweep shares, and PP-3 / PP-4 would pass 2000 s
of wall. The jets join PP-5 / PP-6 as a second label (its own reset and run step, manifest and
control set), since a 210 s sweep does not earn its own boot and `LoadGame` (fast-lane rule);
modded-restock has no air lane to host one, hence PP-10. Each lane's first flight is a `limit=20`
reading flight, the step count set from its `wall=` (D.8). The ids PP-7 to PP-10 are free on
`origin/main` and every remote branch (checked 2026-11-22). Tier `operator`, report-only,
`[retry] policy = "none"`, one slot each; modded-restock flies on its own slot (port block 50060),
so it can fly beside the other two installs.

F6 as built (2026-11-23). The three orbit lanes, specs `PP-7-part-parity-fx-stock`,
`PP-8-part-parity-fx-modded` and `PP-9-part-parity-fx-restock`, and the hlib wiring cells
(`PartParityFxLaneShapeTests`). No registry value: like PP-3 / PP-4 they claim D14 only.

- As flown: PP-7 `2026-11-23_1027` (1002 s wall, 906 s of sweep: 451.8 + 404.6 + 49.8 + 0.3), PP-9
  `2026-11-23_1050` (1338 s, 1243 s: 445.5 + 443.5 + 353.1 + 0.8) and PP-8 `2026-11-23_1232` (1350 s,
  1254 s: 453.7 + 443.4 + 355.8 + 1.2), each PASS attempt 1 after its `limit=20` reading flight (192,
  203 and 216 s). About 6.5 s per case plus 80 to 90 s of controls, under the table's 1080 / 1470 s.
  Every lane needs three steps; the step counts of the table stay as margin, at no cost.
- Verdicts (report-only): PP-7 34 pass / 33 warn / 60 fail / 1 error of 128; PP-9 28 / 74 / 61 / 2 of
  165 (28 / 71 / 64 / 2 under the weak-core rule); PP-8 27 / 21 / 20 / 2 and 95 reference (would be 42
  fail, 53 warn) of 165. Triage: todo PARTPARITY-FX-ORBIT-FINDINGS-2026-11-23.
- Two instrument changes the reading flights forced: weak cores (E.4), and the plume controls'
  subject on modded-compat. The Flea's smoke is noise there against itself: a ReStock booster's
  world-space puffs are sheared 130 to 160 m away in orbit and land in the near box now and then
  (E.12 U8). `ctl-fx-real-vs-real` on it read 0 / 0 px smoke cores (`2026-11-23_1112`, failing origin
  and length) and then 0 / 754 px (`_1217`, failing smoke). Measured over PP-9's own split halves
  (real even samples against real odd ones): `solidBooster.sm.v2`, `solidBooster.v2` and `Shrimp`
  jump (519 / 0, 53 / 542 and 3976 / 0 px); `sepMotor1`, both escape towers, `restock-srb-anvil-1`,
  `restock-srb-castor-1`, `Pollux`, `Mite`, `Clydesdale`, `MassiveBooster`, `solidBooster1-1` and
  the two separation-motor tanks fail nothing measured there. So `PlumeControlParts`
  is `liquidEngine2.v2`, `liquidEngine`, `sepMotor1`, `solidBooster.sm.v2`, in that order, each
  unmeetable one skipped; stock-minimal and modded-restock keep the LV-T45. All 11 controls (the
  point-light one `n/a` on both modded installs) read ok on all three full flights. The real side was
  right: stock leaves that smoke behind, and the ghost's `smoke-extra` was a defect (todo
  GHOST-SMOKE-RIDES-ACTIVE-VESSEL, fixed 2026-11-24). What is left is a puff or two of the newest
  frame on either side, which is why the Flea's smoke stays a poor real-vs-real control.

F7 lanes as authored (2026-11-23): the second labels `pp5fx` /
`pp6fx` of PP-5 / PP-6 after their chute step (reset and runs carry `contexts=air fx=all`) and
`PP-10-part-parity-fx-air-restock`, wiring cells `PartParityAirFxLaneShapeTests`. Two steps and not
the table's one: F7's acceptance sweep took 336 s on stock (the Goliath's 77 s spool sets its wave),
so 480 s is under D.8's 50 % margin. `budgetSeconds` 2400 for PP-5 / PP-6 and 1800 for PP-10 (the
step budgets sum to 2160 and 1500). No registry value, as the orbit lanes.

Reading runs after the ghost FX fixes (2026-11-23, main with #2804, DLL 8b67133f; all report-only,
every control ok, recompute 0 mismatches, no `[Parsek][ERROR]`; evidence `<umbrella>/logs/flights/fx-fly/`):

| Lane | Run | Wall | Sweep steps (s) | Cases: pass / warn / fail / error / reference |
| --- | --- | --- | --- | --- |
| PP-7 | `2026-11-23_1732` | 1007 s | 452 + 399 + 46 + 0.3 | 128: 105 / 17 / 6 / 0 |
| PP-9 | `2026-11-23_1749` | 1317 s | 454 + 421 + 322 + 0.4 + 0.4 | 164: 68 / 31 / 65 / 0 |
| PP-8 | `2026-11-23_1811` | 1332 s | 453 + 422 + 342 + 0.4 + 0.6 | 164: 46 / 10 / 13 / 0, 95 reference |
| PP-5 | `2026-11-23_1718` | 409 s | `pp5` 29.4; `pp5fx` 312.4 + 0.1 | `pp5fx` 14: 2 / 2 / 10 / 0 |
| PP-6 | `2026-11-23_1725` | 423 s | `pp6` 32.6; `pp6fx` 312.8 + 0.1 | `pp6fx` 14 reference |
| PP-10 | `2026-11-23_1712` | 397 s | 315.8 + 0.2 | 14: 0 / 4 / 10 / 0 |

One air step does the work on every install (about 313 s of the 480 s box), so the second stays as
margin; the orbit lanes still use three. One case fewer on the modded installs (164) since
`telescopicLadderBay` is excluded (`fx-no-light`). PP-9 read 128 fails before the ghost fixes and 65
after; what they are: todo PARTPARITY-FX-ORBIT-FINDINGS-2026-11-23.

### E.9 Work breakdown

Rule (D.10): two pieces in flight never touch one file, each is green on its own, and every C#
literal a Python cell mirrors changes in ONE piece together with its mirror. Mirrored today:
`PartParityJson.cs`, `TestCommandPartParitySweep.cs`, `PartParityHostIntegrity.cs`,
`PartParityMetrics.cs`, `PartParityStateRules.cs` (`PartParitySourceSyncTests`), plus
`PartParityCaseCapture.cs`, whose cell requires that it reads its rows through
`PartParityJson.AtlasRowsFor(` and holds no row literal, and `test_hlib`'s cell that reads
`PartParityAirContext.DecideAdmission(args, active,` in the applier. F1 adds
`PartParityFxRules.cs`, holding every FX constant, so F1b's new measure file holds none. Docs:
each piece edits only its own E subsection.

| # | PR | Files | Runs alongside | Acceptance |
| --- | --- | --- | --- | --- |
| E0 | Spike (throwaway branch, never merged, the P0 pattern): one in-game cell, two operator lanes. U1 a lone held engine ignites on the independent throttle with no command part, and a held jet at 1600 m makes static thrust; U2 `ctrlState` writes and the RCS group fire a non-active lone RCS part, and nothing overwrites them; U3 the hold keeps drift under 0.05 m at full thrust; U4 particle renderers draw in the capture camera, and how often additive overlaps clip at 255; U5 `GetParticles` reads `KSPParticleEmitter`-driven systems on both sides; U6 render cost per FX layer; U7 Waterfall renderers draw in the capture camera; U8 a world-space smoke trail looks the same on the real and the ghost emitter of a held engine (no Krakensbane shear between them); U9 a part `Light`'s `cullingMask` reaches the plate's layer, for a spot and a point light; U10 how far the hue rotation moves max-channel coverage | spike branch only | before everything | every unknown answered in E.2 / E.3 |
| F1 | The contract, both sides: `PartParityFxRules` (families, predicates, exclusions, words, the plate context, `ChooseRcsInput`, `ChooseControlSubject`, the renderer layer rule, timeouts, every E.4 constant and threshold default), the `fx=` arg and refusal, `IsFxRun` at every state-run site, `settings.fx`, `PlanCases`, `ControlSetFor`, the FX key tables and `FxAtlasRows`, `plate-not-lit`, the FX control judges and wave bucket; AND every Python mirror (`partparity.py` constants, readers, `control_set`, the writer cross-check, the sync cells, hlib's arg table); committed fixture part lists `Source/Parsek.Tests/Fixtures/PartParity/fx-parts-<profile>.txt` with xUnit count pins equal to E.1; the two no-change cells of E.1 | `PartParityFxRules.cs` (new), `TestCommandPartParitySweep.cs`, `PartParityJson.cs`, `PartParitySweepPlan.cs`, xUnit, `harness/lib/partparity.py` (constants, readers), `test_partparity.py` (sync class), `hlib.py` (arg table) | F2, P1 | `dotnet test` and the lib suite green; with `fx=none` every plan, control set and `settings` equals today's for PP-1 to PP-6 |
| F2 | Ghost seams, no behaviour change: `GhostPlaybackEngine.ApplyUnsuppressedFxRestore` (the tail of `ApplyFrameVisuals`'s unsuppressed branch, inline fold proof); `GhostFxFingerprint.BuildEntries(info)`, called by `LogInfo`; the read-only thruster index per RCS particle system on `RcsGhostInfo` (insertions only, the P3 applied-skin proof); the recorder source-pin cell of E.2 (test only) | `Source/Parsek/Ghost/GhostPlaybackEngine.InRangeRender.cs`, `GhostFxFingerprint.cs`, `GhostVisualBuilder.RcsFx.cs`, `GhostTypes.cs`, xUnit | F1, P1 | IL proofs; ghost and recorder tests green |
| P1 | The modded-restock profile (E.6): profile file, slots, hlib profile tuples, tier runner, provisioner cells | `harness/provision/profiles/modded-restock.toml`, `harness/lib/slots.py`, `harness/lib/hlib.py` (profile tuples only), `harness/tools/tier_runner.py`, provision and lib tests | F2 (and F1 only when F1's `hlib.py` edits are elsewhere in the file; else after F1) | harness suites green; provisioning on request |
| F1b | Pure FX measures from 8-bit tiles: the accumulation to tiles, core / envelope, origin, lateral offset, length, width, direction, cross-section, chromaticity, hue histogram, saturation, split halves, particle statistic reduction; a committed synthetic FX atlas and sidecar fixture for the Python recompute | `Source/Parsek/TestCommands/PartParityFxMetrics.cs` (new), xUnit, the fixture | F4's start (after F1) | xUnit on synthetic plumes: shifted, shortened, rotated-hue, smoke-less shapes each move exactly their own measure |
| F3 | Live FX engine: `PartParityFxDriver` (authoring, cheat scope, hold, temperature and blackbody writes, the RCS input from `ChooseRcsInput` and the nozzle hide, warm-up and plateau on every side, accumulation, particle reads, FX framing, hide-all, culling mask, strips, the plate and its floor, instanced materials for the hue control), the layered and plate capture, the FX branch of the case capture, the FX wave dispatch and the state-run key enumeration, the heat press kind and the plate context in the state driver, the spawn orientation, the in-game cell's `fx=` filter (no new `[InGameTest]`) | `PartParityFxDriver.cs` (new), `PartParityCapture.cs`, `PartParityCaseCapture.cs` (keeps `AtlasRowsFor`), `PartParitySweepRunner.cs`, `PartParityStateDriver.cs`, `GhostCoOrbitalRig.cs`, `PartParityInGameTests.cs`; no mirrored file | F4 | a dev-install run over `liquidEngine2.v2`, `solidBooster.sm.v2`, `ionEngine`, `RAPIER` (closed cycle), `RCSBlock.v2`, `vernierEngine`, `standardNoseCone` (heat), `spotLight1` and a point light (plate): every case reaches its state, every row written, controls ok, warm-up and hold logged, nothing left after teardown |
| F4 | Harness judge: FX verdicts per route, view and layer, the RCS reported-only measures and the known-difference flag, the reference class, the split-half cap, the FX recompute from the FX atlas, the FX control judges, the press gate's four families, verdicts JSON | `harness/lib/partparity.py` (logic), `test_partparity.py` (new cells, not the sync class) | F3 (its recompute cells after F1b's fixture) | lib suite green: each broken plume fixture fails its own measure, a mutated FX atlas reds INVALID(parity-instrument), a reference case never fails, an rcs case never fails on length |
| F5 | Viewer (E.7) | `harness/tools/part_parity_view.py`, `harness/lib/test_part_parity_view.py` | F3 (after F4) | viewer tests green; a synthetic FX sweep shows real and ghost strips in lock step and the RCS known-difference row |
| F6 | Orbit lanes PP-7 / PP-8 / PP-9: specs, registry, lane catalog regen, hlib wiring cells, status rows | `harness/scenarios/PP-7-*.toml`, `PP-8-*.toml`, `PP-9-*.toml`, `harness/coverage/registry.toml`, `docs/dev/flight-profiles.md`, `docs/dev/autotest-status.md` | F7 (C# half) | harness suites green; first flights (`limit=20`, then full), findings filed |
| F7 | Air jets: the held air spawn in `PartParityAirContext` (keeps the `DecideAdmission(args, active,` call the hlib cell reads), the second label in PP-5 / PP-6, PP-10 | `PartParityAirContext.cs`, then the three specs and the F6 doc rows after F6 | F6 (C# half) | a dev-install run of `JetEngine` and `RAPIER` (air mode) held at 1600 m: ignited, plateaued, captured |
| R1 | Later, after F6: the vessel-scale reentry reference lane (R5) | a new spec, a small mission shell, the viewer's reference page | - | see below |

E0 was flown 2026-11-22 (E.12). Order: F1, F2 and P1 together; then F1b and F4's start; F3 once F1b and F2 merge,
with F4; F5 after F4; F6 after F3, F4 and P1; F7 after F3 (its lanes after F6); R1 after F6. The C#
pieces get the end-of-task review; F2 carries its IL proofs as review evidence. Versus rev 1, F2a
(an engine / RCS poll extraction) is gone: the recorder statics are already shared (E.2).

R1, the reentry reference lane (later piece, R5). A probe with a heat shield (the `B4-reentry-
splashdown` craft's pod) is flown through a steep Kerbin reentry from a fixture parked just
above the atmosphere, recorded by Parsek, with `CaptureScreenshot` from the FLIGHT camera at fixed
UT marks (Mach 2.5 to 3.75, the `aeroFXStartThermalFX` / `aeroFXFullThermalFX` range of
`Physics.cfg`, and peak heating)
and a fixed camera pose relative to the vessel. Then the save is rewound and the committed
recording replayed with the camera in watch mode on the ghost, photographed at the same UT marks
and pose. The page shows the pairs (real: stock `AerodynamicsFX`, blackbody and ablator char;
ghost: Parsek's reentry fire, glow and char) side by side, reference-only: real and ghost are not
co-located and the flight is not a part case, so nothing is judged until a reading run shows which
measures are stable. Its own plan when P8's core has flown.

### E.10 Owner questions

None open. R4 to R6 answer rev 1's three. One choice is left to the first flights, not the owner:
every FX threshold (E.4) and N are starting guesses, re-pinned from the reading runs against the
controls.

### E.11 Review disposition (rev 1 -> rev 2)

One clean-context review of rev 1; each finding was checked against the code or the decompile.

| Finding | Check | Disposition |
| --- | --- | --- |
| The ghost plume is never warmed up: plateau and framing read only the real side | E.2 rev 1: the plateau read the real side before the event-route ghost exists | Fixed: plateau and framing on every side after the last prime, at least the longest particle lifetime, `warmupS` per side (E.2, E.3) |
| PP-8 control subjects carry Waterfall | the `@PART[...]` patches of the four Waterfall folders: `liquidEngine2_v2`, `nuclearEngine`, `spotLight1`, `JetEngine`, `RCSBlock_v2` patched; `solidBooster_sm_v2`, `standardNoseCone`, nav / dome / strip lights not | Fixed: `ChooseControlSubject`, per-install preference lists with no Waterfall module (E.5) |
| `PrimeVisiblePartStateAtUT` cannot be static and engine-free | `GhostPlaybackEngine.InRangeRender.cs` `ApplyFrameVisuals`: `UpdateReentryFx` / `UpdateLaunchDust` are instance; `RestoreActiveEngineFx` on the suppressed -> unsuppressed edge, `RestoreAllRcsEmissions` every unsuppressed frame | Fixed: hidden prime, activation with the deferred restore, then the extracted `ApplyUnsuppressedFxRestore` tail, no reentry or dust (E.2, F2) |
| The (0.2, 1, 0.2) tint also cuts luminance, so it fails length | luminance-threshold coverage | Fixed: flame coverage on the max channel, the control rotates the tint channels (E.4, E.5) |
| Exact recompute needs C# to measure from the 8-bit bytes; constants must not land in F1b | the half-float option; F1b and F4 in flight together | Fixed: ARGB32 only, measures from the written tiles, every constant in F1's `PartParityFxRules` (E.3, E.4, E.9) |
| The ghost inventory from the log loses lines | `GhostFxFingerprint.LogInfo`: `VerboseRateLimited("FxFingerprint", "fp-<kind>-<part>-<midx>", ..., 5 s)` | Fixed: `FormatEntry` over `state.engineInfos` / `rcsInfos` through `BuildEntries` (E.4, F2) |
| A dark plate cannot fail | no floor in rev 1 | Fixed: `PlateLitMinPct` floor -> `plate-not-lit`, U9, a point-light control (E.2, E.5) |
| A misplaced plume can pass; `fx-bottom` undefined | no origin / offset measure | Fixed: origin and lateral offset gate, the axis view judges the cross-section (E.4), `ctl-fx-plume-shift` |
| RCS input and the search's events | `ModuleRCS.FixedUpdate`: `rcs_active` follows the RCS group; the recorder reads `rcs.rcs_active && rcs.rcsEnabled` with an 8-frame debounce (`RcsDebounceFrameThreshold`) | Fixed: the input is chosen on the prefab before the spawn (no trial firing), the most nozzles evenly (R4, E.2) |
| `fx=` must reach every state-run site | `IsStateRun` (`ControlSetFor`, `PlanCases`, `PartParitySweepRunner`), `StateSettings`; Python `control_set`, the writer cross-check, the viewer's `control_set_kind` | Fixed: `IsFxRun`, `settings.fx` on both sides in F1 (E.5) |
| `plate` and heat must not change PP-3 / PP-4 | `PartParityStateRules.OrbitContexts`, the D.1 `p8-heat` exclusion | Fixed: both live in `PartParityFxRules`; P6's tables untouched; pins named (E.1) |
| The culling mask must include FX layers | `PartParityCaseCapture` builds it from mesh record layers | Fixed (E.3) |
| The tint control must instance the material | ghost particle materials are the prefab's | Fixed: `renderer.material` on the broken ghost only, destroyed at teardown (E.5) |
| F2a may be unnecessary | `ParsekFlightPostSwitchAutoRecord` already calls the five statics | Adopted: F2a dropped, a source-pin cell instead (E.2) |

### E.12 E0 findings (2026-11-22)

Throwaway branch `part-parity-p8-e0` (commits 992df1a52 and 0c5c89de9, never merged): one file,
`Source/Parsek/InGameTests/PartParityFxSpikeInGameTests.cs`, with two in-game cells, categories
`PartParityFxSpike` (orbit, host `eva2-lko-crewed`) and `PartParityFxSpikeAir` (pad host
`b1-pad-craft`, cases held 1600 m over the showcase band). Four uncommitted operator specs, PPS-8a
to PPS-8d (kept in `<umbrella>/logs/partparity/p8-e0/specs/`), flown one at a time. Both installs
were provisioned from the spike's Debug DLL (sha256 prefix 6bd0e11e; the first PPS-8a flight ran
the first commit, ea062665). Every flight is PASS attempt 1:

| Flight | Install | Run id | Wall | Cases |
| --- | --- | --- | --- | --- |
| PPS-8a, first pass | stock-minimal | `2026-11-22_2134_PPS-8a-fx-spike-orbit-stock_run2` | 157 s | 6 engines, 2 RCS, 1 plate |
| PPS-8a, second pass | stock-minimal | `2026-11-22_2144_PPS-8a-fx-spike-orbit-stock` | 167 s | the same |
| PPS-8b | modded-compat | `2026-11-22_2151_PPS-8b-fx-spike-orbit-modded` | 170 s | the same |
| PPS-8c | stock-minimal | `2026-11-22_2149_PPS-8c-fx-spike-air-stock` | 139 s | `JetEngine`, `RAPIER` (air mode), Hammer |
| PPS-8d | modded-compat | `2026-11-22_2154_PPS-8d-fx-spike-air-modded` | 141 s | the same |

Orbit engines: `liquidEngine2.v2`, `solidBooster.sm.v2`, `ionEngine`, `nuclearEngine`,
`liquidEngine` and `RAPIER` in closed cycle; RCS: `RCSBlock.v2` and `RCSblock.01.small`; plate:
`spotLight1`. Per case: spawn and hold, press, wait for ignition, build the event-route ghost
from the stowed snapshot and the recorded event, prime it as E.2 steps 1 and 3 do, wait for the
plateau, then 16 samples on each of `fx-side-a`, `fx-far` and `fx-bottom` (flame over black, smoke
over black and white), 6 strip frames, and four more 16-sample passes of `fx-side-a` for U10.
Logs, atlases, strips and the console captures are in `<umbrella>/logs/partparity/p8-e0/`.

| Unknown | Measured | Consequence |
| --- | --- | --- |
| U1 a lone held engine ignites; a held jet makes static thrust | Every orbit engine reached its requested throttle on the independent throttle with `IsControllable` false: 0.01 to 0.02 s for the liquids, the ion engine, the nuclear engine and the RAPIER in closed cycle, 0.42 s for the Hammer (throttle-locked, response time). In the air both jets ignited at Mach 0 under the cheat (`JetEngine` 84.9 kN at throttle 0.91, `RAPIER` 78.6 kN at 0.98) but spool slowly: `JetEngine` throttle 0.70 at 10 s and 0.91 at 20 s (time constant about 8 s), so the 20 s timeout expired on both jets. | Authoring and press confirmed. Jet timeout raised to 45 s (E.2), jet wall time re-estimated (E.8). |
| U2 `ctrlState` and the RCS group fire a lone non-active RCS part | The group switched `rcs_active` on. A `ctrlState` written from a MonoBehaviour was found zeroed at every `FixedUpdate` (33 of 33 checks, first overwrite `X=0 Y=0 Z=0`); it fired only because the spike wrote again in its own `FixedUpdate`, which ran after the reset. Feeding the input through `vessel.OnPreAutopilotUpdate` fired 75 of 76 physics steps on the three parts whose input was predicted correctly (the callback ran every step and `ctrlState` kept the input). `RCSBlock.v2` fired exactly the 2 predicted nozzles at 0.71 each (an edge input) once inactive thruster transforms were skipped (4 of 9 active); the first pass, without the skip, predicted 5. On stock `RCSblock.01.small` the spike's rule predicted 3 nozzles for (-1, 0, 0) and stock fired none. Ghost reach: after the production priming the ghost drew nothing (`alive=0 renderersOn=0`, both installs); a direct `SetRcsEmission` with the recorded power drew 4 (stock) or 8 (ReStock) renderers against the 2 real fired nozzles. | The input goes through the callback; `ChooseRcsInput` transcribes stock exactly and the press checks it (`rcs-input-mismatch`, E.2, E.5). New ghost defect GHOST-RCS-DARK-AFTER-FX-SUPPRESSION (todo): `StopAllRcsFx` disables the renderers and `RestoreAllRcsEmissions` restores only modules with an enabled renderer, so a ghost that enters view or leaves FX suppression mid-RCS stays dark until its next RCS event. Every rcs case fails `state-not-modelled` until it is fixed. |
| U3 the hold keeps drift under 0.05 m at full thrust | Render-time drift (read in `LateUpdate` after the write): 0.00000 m on every case; rotation drift 0 for engines, at most 0.32 deg for RCS. The displacement one physics step makes before the write is a dt^2: 0.0574 m for the LV-T45 (143 m/s2), 0.0769 m for the LV-T30 (192 m/s2), 0.051 m for the Hammer, 0.003 m for the ion engine, 0.041 m for the Hammer in the air. Temperatures held within 0.3 K. | The gate is the render-time drift, under 0.001 m; the per-step displacement is logged, never gated (E.2). The write-back after `WaitForFixedUpdate` is the hold method. |
| U4 particle renderers draw in the capture camera; how often additive overlaps clip | Every stock and ReStock particle renderer drew in the dedicated ortho camera (`Legacy Shaders/Particles/Additive`, `KSP/Particles/Additive`, `Additive (Soft)`, the alpha-blended smoke). Clipping: 25 % of the real LV-T45's core pixels (6107 of 24148 px), 64 % of the real Hammer's (35204 of 54580), none on the ion engine and the RCS puffs. Additive renders leave alpha 0 over a cleared target. | Colour and brightness over unsaturated core pixels only (E.3, E.4); coverage from RGB; strips write alpha 255. ARGB32 stays. |
| U5 `GetParticles` reads `KSPParticleEmitter`-driven systems on both sides | Yes: on the RCS puffs and the ion engine (`KSPParticleEmitter` systems) both sides read `GetParticles` equal to `particleCount` (59 and 59 on the ion engine). But the stock legacy flame systems (`fx_exhaustFlame_*`, local space) read 0 particles at every sample while they draw (24148 core px on the LV-T45), and a Waterfall side has no particle system at all. | The plateau signal is the flame coverage for those sides (E.2); particle statistics stay report-only and cover no legacy flame. |
| U6 render cost per FX layer | 512 px tile: 0.16 to 0.20 ms per layer render (flame 0.17 to 0.20, smoke 0.15 to 0.17, body 0.14 to 0.17); the readback, 1.7 to 2.5 ms per render, dominates. 48 sample frames of 6 renders took 1.2 to 1.7 s (25 to 35 ms per frame). A lone orbit engine case took 11.5 to 12.2 s, most of it the plateau wait. | E.8's basis re-measured: capture is cheap, the warm-up is the cost; 10 s per case stays as the margin. |
| U7 Waterfall renderers draw in the capture camera | Yes. Modded LV-T45: 16 `Waterfall/` renderers (12 `Additive (Dynamic)`, three `Additive Directional`, one `Distortion (Dynamic)`), real flame core 17378 px; RAPIER 50 Waterfall renderers, core 24140 px; LV-T30 13, nuclear engine 6, ion engine 3. Every Waterfall engine also carries 1 or 2 `Waterfall/Distortion` renderers. No particle system on a Waterfall side. | The shader-name rule holds; distortion renderers leave the flame layer (E.3); Waterfall sides plateau on coverage (E.2). |
| U8 a world-space smoke trail looks the same on the real and the ghost emitter (no Krakensbane shear) | In the air: yes. Hammer smoke, real vs ghost: the real particles sit on the axis (centroid 0.4 m, 153 of 255 within 200 m), along-axis median 164 m (ghost 90 m), across-axis median 9 m (ghost 4 m): the trails differ in length, not in placement. In orbit (Krakensbane frame velocity 2240 m/s): no. Every stock legacy world-space system on the real side holds a population of particles kilometres from the part (LV-T45 smoke: 3 of 304 within 200 m, across-axis median 10.2 km, centroid 10 km off the axis), while the ghost's stay put (226 of 226 within 200 m, median 2.7 m). Stock hides it: in vacuum the real `fx_smokeTrail_*` renderer is disabled (`enabled` false, no smoke coverage on all four stock legacy engines), while the ghost draws its smoke trail (`fx-far` smoke core 9832 px on the LV-T45, 27120 px on the Hammer). | Framing from near-particle percentiles, never renderer bounds (E.3). Orbit smoke reads `smoke-extra` on every stock legacy engine: new ghost defect GHOST-SMOKE-IN-VACUUM (todo; fixed 2026-11-23, the ghost now runs stock's `SmokeTrailControl` fade from its own altitude). Smoke is judged where stock draws it, in the air cases. |
| U9 a part `Light`'s `cullingMask` reaches the plate's layer | Yes. Stock `spotLight1` mask 0x828001 (layers 0, 15, 17, 23), ReStock 0xFFFFFB37; the plate on layer 0 (the part's first rendered renderer) was lit on 40.8 % of its pixels at luminance 8 or more (real side, both installs), 0 % with the lights off, and the same on layer 15. The seven light parts checked (`spotLight1`, `spotLight3`, `domeLight1`, `navLight1`, `stripLight1`, `groundLight1`, `groundLight2`) carry only spot lights on both installs. Early parity signal: the stock ghost's spot light has intensity 2.0 against the real 1.0 and a warmer colour, plate mean luminance 49.5 against 23.3; on ReStock 26.8 against 23.4. The real colour was still 0.875 grey 4 s after `LightsOn`, so the stock fade may not have finished. | The plate works with a 2 % floor. The point-light control becomes optional (`n/a` when no part has one, E.5). F3 settles past the fade and re-measures before the intensity gap is filed. |
| U10 how far the hue rotation moves max-channel coverage | Every flame tint the spike saw is grey: `_TintColor` (0.537, 0.537, 0.537), (0.779, 0.779, 0.779), (1, 1, 1) on the ion engine and the RCS, (0.5, 0.5, 0.5) on the RAPIER. So the channel rotation is an exact no-op (pass C within the intact A / B noise on every engine). A (0, max, 0) tint gives chromaticity (0, 1) but moves core coverage by -4 % (LV-T45), -3 % (Hammer), -5 % (RAPIER) and -46 % (ion engine, whose blue comes from its particle colours). Noise between two intact 16-sample passes, core at threshold 16: real 0.4 to 5.6 %, ghost 0.3 to 7.5 %. | `ctl-fx-plume-hue` rotates the channels of the broken ghost's rendered flame pixels, which keeps coverage exact (E.5). N = 16 stays: the pass-to-pass noise is well under the 25 % length and 30 % width thresholds. |

Early parity signals, not judged (P8 judges them once built): the ghost's LV-T45 flame core is
about half the real one (12140 against 24148 px) and its nozzle shows no throttle glow in the
strip; the ghost's `JetEngine` draws no flame, which matches stock (the static jet draws none
either); the ghost's air-mode RAPIER flame is a fifth of the real one on stock (6256 against
32426 px), because the real side's trail (`fx_smokeTrail_aeroSpike`) is additive and so counts as
flame here, while the ghost drew an alpha-blended substitute in the smoke layer (fixed 2026-11-23,
todo GHOST-RAPIER-AIR-SMOKE-SUBSTITUTE: with its own trail the ghost reads 38237 px against a real
32550; the smoke core of 0 px on the real RAPIER was that split, not a missing trail);
ReStock's Hammer matches to 0.3 % (68641 against 68867 px), so a matching pair reads as one.
`FlameValueMin`: the envelope at max channel 8 is 10 to 20 % larger than at 16 and the core moves
less; 16 is the starting value.

Answered 2026-11-24: the real world-space particles in orbit drift kilometres away because stock
moves them back by the Krakensbane frame velocity every physics step (45 m at orbital speed, with
drift up to twice that), so the smoke stays where it was emitted; the ghost's was moved by nothing
(todo GHOST-SMOKE-RIDES-ACTIVE-VESSEL). Answered by F3: the stock spot light's plate gap is a
ghost difference, not a fade still running (U9; todo GHOST-LAMP-LIGHT-INTENSITY, fixed
2026-11-23).

## Appendix F. P5: baselines, the accepted list and the regression gate (2026-11-23)

Built on branch `part-parity-p5-gate` (rules in section 10, viewer mode in section 8). Every
reading run below flew main's code of 2026-11-22 (`4823e3440`) or, after the branch merged
`origin/main`, the merged code (P8 F1's FX contract and the ghost FX fixes), on both installs,
provisioned from this worktree's Debug DLL and checked against each manifest's `dllHash`. The
real-vs-real control's noise allowance (4.5, todo PARTPARITY-REAL-VS-REAL-ONE-PIXEL) is in every
one of them. `c0afedca` and `206033bd` are the same source but for one C# comment (the IL was not
compared); `36eabc6d` is the merged build. PP-4 `2026-11-22_2302` flew on a sibling session's DLL (`218c795f`: the driver
re-checked the deployed DLL before the lock wait, not after it); it is in no baseline, and reads
`gate=ok` against them.

| Lane | Label | Reading runs (DLL prefix) | Cases | pass / warn / fail |
| --- | --- | --- | --- | --- |
| PP-1 | `pp1` | `2026-11-22_2159` (c0afedca), `_2317` (206033bd); merged check `2026-11-23_0035` (36eabc6d) | 792 | 775 / 14 / 3, 775 / 15 / 2; 775 / 15 / 2 |
| PP-2 | `pp2` | `2026-11-22_2236`, `_2343` (206033bd); merged check `2026-11-23_0058` (36eabc6d) | 1510 | 1462 / 46 / 2, 1461 / 48 / 1; 1461 / 48 / 1 |
| PP-3 | `pp3` | `2026-11-22_2220`, `_2328` (206033bd); merged check `2026-11-23_0046` (36eabc6d) | 174 | 144 / 17 / 13, 146 / 15 / 13; 146 / 18 / 10 |
| PP-4 | `pp4` | `2026-11-22_2359` (206033bd), `2026-11-23_0016` (36eabc6d); merged check `2026-11-23_0115` (36eabc6d) | 240 | 196 / 26 / 18, 199 / 24 / 17; 198 / 26 / 16 |
| PP-5 | `pp5` | `2026-11-22_2157` (c0afedca), `_2315` (206033bd) | 10 | 0 / 10 / 0 both |
| PP-6 | `pp6` | `2026-11-22_2234`, `_2341` (206033bd) | 10 | 0 / 10 / 0 both |

All runs PASS attempt 1, controls ok, complete, recompute 0 mismatches. The merged check runs flew
armed (`gating = true`) on the specs and read `gate=ok`, reds 0, drift 0, unaccepted 0, gone 0:
PP-1 (accepted 3), PP-3 (accepted 69, improved 25), PP-2 (accepted 1) and PP-4 (accepted 66,
improved 22), against baselines holding none of those check runs. The committed baselines are
pinned over all three runs of each armed lane (two pre-merge reading runs and the merged check).

**Tolerances.** A blob measure drifts when it exceeds `hi + max(abs, 0.5 x hi)` of its band,
with `abs` 48 px for the shape blobs and 192 px for the colour blob; an IoU drifts below
`lo - 0.01`. The numbers were picked before the flights and then checked against them, not fitted.
Run-to-run movement of every measure no `noisy` entry covers, between the first two runs of a lane
(`part_parity_baseline.py spread --skip-noisy`, largest mover in brackets):

| Lane | shape blob (real-only / ghost-only) | colour blob | IoU |
| --- | --- | --- | --- |
| PP-1 | 7 / 9 px (`gearfree` side-b) | 94 px (`foldingradsmall` top) | 0.0011 |
| PP-2 | 3 / 6 px (`gearfixed`, `gearfree`) | 120 px (`restock-fueltank-sphere-375-1--blackandwhite`) | 0.0011 |
| PP-3 | 2 / 3 px (`smallgearbay`, `landingleg1`) | 163 px (`liquidenginelv-tx87--tankbutt--stowed--stacked`) | 0.0053 |
| PP-4 | 6 / 3 px (`groundlight1` lit) | 134 px (`liquidengine3.v2--size1--stowed--stacked`) | 0.0081 |

Margins: shape 5x, IoU 1.2x, colour 1.2x. The colour movers are one-pixel rows and seam lines that
come and go, so a rare one can still red a clean run, and three runs cannot rule that out. One did:
the mutation flight's only red not caused by the mutation was `rotor.01--gray`, a 312 px row seen
in 2 of 9 archived PP-1 runs and in none of the three baseline runs (todo
PARTPARITY-ROTOR01-GRAY-EDGE-LINE, accepted on stock). The global colour tolerance was not widened
for it. Evidence that the rest holds, and its limits: each armed lane's three clean runs read
`gate=ok` leave-one-out (two pinned, the third judged) and against the committed baseline, but the
accepted list was written partly from those same runs (the ReStock wheels, the RoveMate seam, the
scanners), so only `2026-11-22_2302` is fully out of sample. Runs of older builds (before
`4823e3440`) red on cases whose ghosts or capture changed since; they were not triaged case by case.

What moves run to run by its nature is covered by `noisy` entries, which waive the picture drift
of that case and nothing else: the twelve sun trackers (the sun's direction changes per run; over
the current build's clean runs up to 2360 px of colour and 0.37 of IoU on PP-4), the running
orbital scanners (spin phase), the LV-1 stacked shroud (z-fight), the KE-1 rods (37 to 522 px), the
ReStock wheels' free-fall droop (`roverwheel1` colour 16556 to 27694 px), the stock EM-16 row above
and the owner-accepted colour residuals. A picture regression on one of those cases is NOT caught
by the gate; a missing, extra or hidden mesh, a skin mismatch, a state flag or a blank ghost on any of them is.

**A wildcard accepts pictures only.** `measure = "*"` first covered every group, so such an entry
also waived a missing mesh: with a rendered missing mesh injected into every case of one run per
lane, 45 of 2716 cases did not red (PP-2 11, PP-3 13, PP-4 21). It now covers `shape-blob`,
`color-blob` and `iou` only, and all 2716 red (`test_no_committed_entry_hides_a_missing_mesh`).

**What the review found (all fixed before the PR; no flight, proved over the archived sidecars).**

- A slot copy's sweep names its profile `stock-minimal-2`, so a clean armed run there read
  `no-baseline`. The spec's `instanceProfile` now decides, else the name without the copy suffix.
- A setup fault on a baselined case was only a note, and the ghost builder runs inside the guard
  that records `crashed`: a sweep whose every case crashed read PASS. `crashed` now reds; any other
  new fault is `unmeasured`, which makes the armed row INVALID(parity-incomplete).
- A case that reads `reference` (a Waterfall FX case, E.6) is never gated; one whose baseline was
  judged and that now reads `reference` is `unmeasured`. FX findings over their fail line (groups
  `fx:<measure>` and `fx:structure`) red unless an entry names them, and a body fail never hides
  them. No FX lane is armed.
- FX after the per-entry cap (merge of #2802, 2026-11-23). The gate reads a case's FX fails from its
  judgement (`CaseVerdict.fx`), where a fail the split-half rule caps is `warn`, never from the reason
  text, which still prints it; a body fail the old whole-case cap held at `warn` is gated like any
  other. A judged entry that reads `skipped` (two weak cores), and an FX check that did not run
  (`fx-structure-not-measured`, `fx-nozzle-not-measured`, `fx-no-real-effect`), is `unmeasured` on a
  baselined case (`fx_unmeasured`), unless the baseline's runs show the same token (its optional
  `fxUnmeasured` list): then, and on a new case, it is the note `fx-unmeasured`. A skip needs the real
  core weak too, so a baselined one hides no lost plume. To settle before any FX lane is armed: an FX
  fail has no band, so one capped in the reading runs and standing later reds; a lamp `plate` case
  reads `unmeasured` against its own baseline (`unmeasured_reason` owes every manifest view, which a
  plate case does not photograph: 14 on PP-7, 16 on PP-8 and PP-9); and a layer that went empty on
  BOTH sides, core and envelope, is judged `pass` (E.4).
- A missing view or route, a null measure, an unmeasured structure or a capture that threw was
  skipped. Each is `unmeasured` now.
- A ghost renderer that exists but is disabled, a ghost that was not built and a state the ghost did
  not play were invisible on the noisy cases. `renderedDiffers` (groups `hidden` and `drawn`),
  `ghost-not-built*` and `state-not-*` red now whatever the verdict, and a noisy case whose IoU falls
  under half its band's low end reds too.

Known to pass still (re-review): a state case whose ghost stays stowed while its read-back claims
`applied` and no flag is raised, where the picture cannot tell: 9 of 134 PP-3 and 18 of 179 PP-4
cases (both scanners, one claw, and lit cases whose lit / unlit difference is inside tolerance).

None of the seventeen clean runs of the six lanes carries any of these except the five
stowed chutes (`drawn`) and the nine lit cases (`state-not-modelled`), which are accepted by name.

**Accepted list** (`harness/partparity/accepted.toml`, 72 entries, 41 of them `noisy`, each with
its todo id; as pinned by P5): 18 for the 9 lit cases stock draws no light on
(PARTPARITY-STOCK-INVISIBLE-LIGHT-READBACK, two `flag:` entries each), 16 under
GHOST-PARITY-RESIDUALS-2026-11-22 (the four cosmetic colour residuals and 12 ReStock wheel cases
under the suspension-droop ruling it records), 12 sun trackers (PARTPARITY-SUN-TRACKER-RESIDUAL), 5
KE-1 cases (PARTPARITY-KE1-STRUT-ROLL, new), 5 stowed chutes (`drawn`,
PARTPARITY-STOWED-CANOPY-RENDERED, new), 5 small state residuals
(PARTPARITY-STATE-SWEEP-FINDINGS-2026-11-22), 4 Waterfall-lit lamps
(GHOST-WATERFALL-LIGHT-FX-MISSING), 2 scanners (PARTPARITY-SPIN-PHASE, new), 2 LV-1 shrouds
(PARTPARITY-LV1-SHROUD-ZFIGHT), 1 open-top fairing (GHOST-FAIRING-OPEN-TOP-SEAM, a warn today), 1
RoveMate seam (PARTPARITY-ROVERBODY-WHITE-TOP-SEAM, new) and 1 stock EM-16 row
(PARTPARITY-ROTOR01-GRAY-EDGE-LINE, new). One entry since, 73 in all (42 `noisy`): the truss edge row
(PARTPARITY-TRUSSPIECE-BLANKETS-EDGE-LINE), from PP-2's armed flight `2026-11-23_1846`. Some matched nothing in any clean current-build run:
owner-ruled residuals that did not recur, and cases listed because their family moves (wheels,
three sun trackers, KE-1). Every fail of every baseline run is covered (the writer refuses
otherwise); every warn is listed in todo PARTPARITY-P5-WARN-CENSUS.

**Armed:** PP-1, PP-2, PP-3, PP-4 (`gating = true`, roster `PARTPARITY_ARMED_SPECS`). **Report-only:**
PP-5 and PP-6. A canopy follows the airflow, so every picture measure of a chute case moves run to
run (PP-5: colour blob moves up to 5012 px, IoU 0.26; PP-6: 6768 px, 0.21) and no band holds; the
canopy cap of D.4 keeps them at warn, which only the viewer can judge. Their baselines are
committed for `--pinned` reading.

**Mutation proof.** A local, uncommitted line in `GhostVisualBuilder.AddPartVisuals` skipped renderer 0
of every `radialDecoupler*` ghost (DLL prefix 3f6af586, both installs). Armed PP-1
`2026-11-23_0607`: PARSEK-FAIL(part-parity), attempt 1, controls ok, 28 reds: 27 on
`radialdecoupler`, `radialdecoupler1-2` and `radialdecoupler2` (3 `missing-rendered`, 24 drift) and
the `rotor.01--gray` row above. Re-judged with the final rules and list: 27 reds, those three cases
only. The line was reverted (empty diff against HEAD) and the DLL rebuilt; the mutated DLL stayed on
both installs until the next session re-provisioned them.

## Appendix G. P7 plan: motion over time (rev 1, 2026-11-26)

Status: PLAN, not built. Owner approval to plan: 2026-11-26. Review: G.9. P6
compares a moving part's two END states (stowed, and the state after its done signal) and P8 its
effects; neither looks at the motion between them. P7 photographs the motion itself, real and ghost,
at fixed times after the press, and measures when it starts, how fast it runs, the shape of its
curve, the in-between pose, and when it ends.

Section 9.2 planned the real side as a sampled clip (`normalizedTime = t`, `speed = 0`,
`Animation.Sample()`). That is kept only as a per-frame READING of stock's own clock (G.2): a real
side posed by Parsek cannot show a ghost that starts late, runs at the wrong speed or snaps, which
is what the code already suggests (G.6). So the real part plays its motion live, in game time, and
the ghost is posed afterwards at exactly the game UT each real frame was taken.

### G.1 What "same over time" means, and which parts

A MOTION CASE is one press of one transforming part, from its stowed state to its target state
(`motion-<state>`, below), on the event route: the ghost built from the stowed snapshot plus the
events the recorder's own pollers emitted during the real motion (D.5 `ghost` row), exactly what a
recording made during that motion replays. The start route has no motion (it begins at the
target) and the stowed reference does not move, so neither has a motion row.

Per side the case reads a PROGRESS curve p(t) in [0, 1] over the time t since the press:

- Pose progress (primary). The family's moved transforms (G.2: the ghost's own deploy transform
  list, paired by part path with the real part's transforms; a servo's moving transform; a chute's
  canopy transform). For each transform, its travel since frame 0, accumulated frame to frame (so a
  turn past 180 degrees is not folded back), over its total travel on that side: position, rotation
  and scale, whichever moved most. A transform that moves less than 0.5 deg and 1 mm on a side is
  left out. The side's p(t) is the travel-weighted mean. Each side is normalised by its OWN frame 0
  and its own final pose, so an end-pose difference (P6's finding) does not bend the curve.
- Picture progress (secondary, recomputable in Python from the atlas). Per view, a(t) = tolerant
  mismatch pixels (section 6) between frame t and frame 0 of the same row, b(t) = against the row's
  last frame, p = a / (a + b) (0 when both are 0).

From each curve, by linear interpolation, the first crossing times t02 (start), t10, t50, t90 and
t98 (end): the real's between its frames (T / 8 apart, G.2), the ghost's on its 50 ms scout grid
(G.2), which the sidecar carries so Python recomputes both. Measures per case:

| Measure | Definition | What it catches |
| --- | --- | --- |
| `eventDelayS` | first polled event UT minus the press UT | when the recording says the motion happened |
| `ghostStartDelayS` | the ghost's transition start UT (read from its own playback info after the event replay) minus the press UT | the event UT on the ghost against the real press time |
| `lagS` | ghost t50 minus real t50 | a ghost that runs late or early |
| `onsetLagS`, `endLagS` | the same at t02 and at t98 | start time, end time |
| `durationRatio` | ghost (t90 - t10) over real (t90 - t10) | rate |
| `curveShapeDev` | max over the real frames with 0.05 < p < 0.95 of `|p_ghost(t + lagS) - p_real(t)|` | the curve's shape once timing is taken out (a straight line against stock's eased or staged clip) |
| `staggerDev` | the same per transform, the largest over transforms | a staged clip played all at once |
| `travelRatio` | per transform, ghost total travel over real | a rotation that takes the short way round |
| `motion-snap` | the ghost rises from under 0.1 to over 0.9 between two adjacent frames while the real takes 3 frames or more | a snap where stock animates |
| `motion-stepped` | the ghost holds still over 2 or more adjacent frames, twice or more, while the real rises more than 0.05 per frame | a staircase (servo samples) |
| `motion-not-modelled` | the real reaches 0.9 and the ghost never reaches 0.1 in the window | a motion the ghost does not show at all |
| `angleMaxDevDeg` | servos only: max over frames of the angle (or travel) difference at the same time | servo pose over time |

The in-between POSE at fixed fractions is compared in two rows (G.2): the ghost at the same time
as the real frame (timing and pose together), and the ghost at the same PROGRESS as the real frame
(pose only, timing removed). A ghost that is late but has the right in-between poses fails the
first row and passes the second; a ghost on time with straight-line poses passes on timing and
shows in the second row and in `curveShapeDev`.

Families and cases. Every P6 family whose state is reached by motion, with P6's own predicates,
presses and done signals (D.1, `PartParityStateRules`), plus the two P6 left to P7:

| Family (P6 id) | Motion case | Stock / modded cases | Notes |
| --- | --- | --- | --- |
| `deployable` (panels, antennas, radiators) | `motion-deployed` | 19 / 22 | sun trackers: the tracking pivot is left out of the progress transforms (its slew after the done signal is P6's, D.3) |
| `gear` (gear, legs, deploying wheels) | `motion-deployed` | 8 / 9 | free-fall droop as P6 (`context-dependent`), which moves no deploy transform |
| `ladder` | `motion-deployed` | 2 / 2 | |
| `animation-group` (drills, in orbit) | `motion-deployed` | 3 / 4 | the stock ISRU stays `auto-deploy` (D.1) |
| `cargo-bay` | `motion-open` | 8 / 9 | |
| `animate-generic` (doors, inflatables, port shields, Klaw, science doors, the two thrust reversers) | `motion-deployed` | 12 / 17 | the reversers are the "engine nozzle" motion stock animates on a press |
| `airbrake` | `motion-deployed` | 1 / 1 | no Animation: progress from the surface transform |
| `parachute` (air, D.4) | `motion-semi`, `motion-full` | 10 / 10 | progress from the canopy transform's scale; pictures report-only (canopy aim, D.4) |
| `robotics` (NEW; P6 `p7-robotics`: `BaseServo` hinges, rotation servos, pistons, rotors) | `motion-swept` | 22 parts (D.1), counted per install by M1 | target swept (G.2); rotors report-only (spin phase) |
| `control-surface` (NEW; P6 `cs-deploy-sign`) | `motion-deployed`, context `radial` | 24 parts (D.1), counted per install by M1 | the subject on a hidden filler's radial node pins the deflection sign (D.1); its end state has no P6 case, so P7 judges its last frame too |

Not in P7, each a manifest exclusion with its reason (never silently dropped):

- `motion-physics`: jettisons and fairings. The shroud or panels leave as free physical objects
  and the ghost hides them at the event; there is no animation on either side (P6 covers the end).
- `light-fade`: lights and cabin lights. A brightness ramp, not a motion, and the ghost snaps it on
  by design (`GhostPlaybackLogic.HeatDeployablesLights.cs:1057-1060`); selectable by name
  (`motion=light`) for a reading run, never in `motion=all`.
- `synthesized-from-trajectory`: engine gimbals and control-surface flight deflection. The ghost
  derives them from its own body rates (`GhostPlaybackLogic.SynthesizedMotion.cs:25-31`) and the
  recorder records no gimbal, so a held lone part has nothing to compare; they need a flown vessel
  (a later reference lane, like P8's R1).
- `loop-phase`: running drills, converters and spinning scanners (`ConverterLoopGhostInfo`), whose
  phase depends on the start time (PARTPARITY-SPIN-PHASE); their period is a later P7 piece.
- `ground-scan`: `ModuleRobotArmScanner` (extends only while scanning a surface feature).

Case ids: the 5.2 rule with the motion word as the state (`longantenna--default--motion-deployed--
<hash>`, `parachutesingle--default--motion-semi--air--<hash>`), so no P6 id moves. Motion cases run
at the base skin (R2).

### G.2 Capture

Host and waves: P6's (4.1, D.3): the orbit host `eva2-lko-crewed`, every other slot (4 cases a
wave, 80 m apart), the host guard, resume and `inflight.txt`; the air host and the showcase band of
D.4 for chutes. Each wave is pressed in one frame. P6's preparation runs first on every case (the
ReStock depth-mask repair, the colour-changer leak repair, stowed inputs, authored electric charge
for servos as for lights).

Holding the scene still: the P6 rig. The ghost is parented to the real part's root at zero offset,
the views are fixed in the PART's frame (4.3) and the key light is camera-relative (D5), so a ghost
tile taken after the real motion ended is the same picture it would have been at that frame: the
part's drift between the real frame and the ghost render changes nothing in part space. The real
side is the subject's own renderers; context fillers are hidden on every side (D.3).

Frame schedule (pure, `PartParityMotionRules.PlanFrameTargets`):

- `T`, the predicted motion time, read from the real part on the first frame after the press: its
  playing `AnimationState` `length / |speed|` (stock's own clock); a servo `|target - start| /
  traverseVelocity`; an airbrake or control surface `deployAngle / actuatorSpeed`; a chute the semi
  or full clip over its module speed (confirmed by M0, U6). Clamped to [0.25, 30] s.
- Targets `t_k = k T / 8` from the press (frame 0 at the press frame, before stock moves anything).
  Capture continues at that spacing until the real done signal (P6's rule, D.3) plus the GHOST's own
  expected run (its built clip length, known before the press from the framing ghost below; a
  servo's 0.25 s stop hold) plus 0.5 s, so a ghost that starts when the real ends is still in the
  window: about 17 to 25 frames for a clip (a window of about 2 T + 1 s); past 33 frames the
  spacing doubles; a 40 s cap ends the window (flag `motion-window-capped`). A control's altered
  route (G.3) extends the window by its own change (its doubled clip, its 1 s shift).
- A target is taken on the first end-of-frame at or after it; the actual offset is recorded
  (`realS`), and a frame later than T / 16 is tagged `frame-jitter:<ms>`. The ghost rows use the
  actual offsets, so the same-time pair has no time skew at all.

Real side, live: at each target frame, after `WaitForEndOfFrame`, the case renders ONLY the real
side (the ghosts do not exist yet, apart from the framing ghost, hidden) through
`PartParityCapture.CaptureSides` with one side, two clears, the motion views (below), and keeps the
composed RGBA tiles in memory (256 px: 16 frames x 2 views is 4 MB a case). Each frame also records
the game UT, stock's own clock (`realClock`: the module's `AnimationState.normalizedTime`, a
servo's position, a surface's deflection, a canopy's scale), the module's state field, and the
local pose of every progress transform. Per frame cost: 2 views x 2 clears at about 2.2 ms each
with readback (E.8, U6), about 35 ms for a wave of 4 on a target frame, nothing in between.

The recorder's pollers run every frame into the case's `PartParityPollSink` (D.3, unchanged), so
the events are those `FlightRecorder` would append. Servos poll through the shared robotic poll
(M2). The press gate, `action-failed` and `state-timeout` are P6's.

Framing box: the motion sweeps through poses neither end state shows, and the box must be known
before the first real frame. Before the press the case builds a FRAMING GHOST from the stowed
snapshot (the D2 seam) and reads its box stowed and at the motion's target (posed through the
family's own applier: the deploy infos snapped to 1 through the baseline overload, a servo at its
target value, a canopy at its stage's pose; a pose used for framing only, never measured or
shown); the shared box is the union of the real stowed box (the 4.2 rule) and both framing-ghost
boxes, grown 20 %. A frame whose real or ghost coverage touches the border is tagged
`motion-clipped-at-border` (report-only; the reading flight re-pins the margin). The framing
ghost is destroyed before the ghost rows are built.

Ghost side, after the real motion: once the window has ended,

1. A SCOUT ghost (event route: the stowed snapshot, the stowed seeds at `t0`, the polled events at
   their UTs) is primed through `GhostPlaybackEngine.PrimeHiddenPartStateAtUT` at every 50 ms over
   the window, in increasing UT, unrendered, reading its own pose progress at each prime. That gives
   p_ghost(u) on a dense grid with no shortcut.
2. For each real frame k: the same-time UT is the real frame's own UT; the matched UT is the first
   scout UT where p_ghost reaches the real frame's progress (interpolated between grid points; at
   p = 0 and 1 the ghost's own first and last pose; none when the ghost never reaches it: tile left
   empty, flag `no-match` on that frame).
3. A RENDER ghost, built the same way, is primed once at every UT of the merged, increasing list of
   same-time and matched UTs, and each primed pose is captured into its row with the real side's
   renderers `forceRenderingOff` (4.4), two clears. Its pose progress at each render must equal the
   scout's at that UT within 1e-4 (else the instrument flag `scout-render-disagree`). Primes go only
   forward in UT, because a re-applied event answers `AlreadyInState` (D.5); for a part with a sun
   tracker, uncaptured sub-primes keep every step under `MaxSynthSampleSeconds` (1 s,
   `GhostPlaybackLogic.SynthesizedMotion.cs:19`).

Views: `side-a` and `side-b` (4.3) at 256 px by default (`views=` widens them, `tile=` sets the
size); a motion row of 17 frames is a strip, not a still, and the end states at 512 px are P6's.

The motion atlas `<caseId>.motion.png`: rows `real:<view>`, `ghost:<view>` (same time),
`ghost-matched:<view>` (same progress) for each view, columns the frames in order: 6 rows x 17
columns of 256 px (4352 x 1536 px) by default. It is the case's strip: the viewer crops and plays it
(G.5) and `--gif` turns it into an animation. The P6 four-row still atlas is not written by a
motion case.

Measures per frame (C# `PartParityMotionMetrics`, pure, over the 8-bit tiles it writes, so Python
recomputes them exactly): for the same-time and the matched pair of each view, P6's shape and colour
measures (section 6: tolerant mismatch, largest blobs, IoU, colour blob), the picture progress of
every row, and the pose progress values of both sides. The timing measures of G.1 are pure functions
of the per-frame arrays (`PartParityMotionRules`, xUnit on synthetic curves) and are written beside
them.

Robotics: the real servo is unlocked and driven the way its PAW slider does (the module's
`targetAngle` / `targetExtension` field set through its `BaseField`, motor engaged, authored
electric charge), from its rest value to the nearer of +45 deg (hinge, rotation servo; half the
travel for a piston) and its range limit, at its own `traverseVelocity`. Done: the moving signal
false and the position within the module deadband of the target. A rotor is spun to a third of its
`rpmLimit` for 3 s; only its rate is compared (report-only, `rotor-phase`).

Control surfaces: context `radial`, the subject on a `fuelTankSmallFlat` filler's radial node so the
centre of mass, and with it stock's deflection sign, is fixed (D.1 `cs-deploy-sign`); the filler is
hidden and left out of the records like a stacked filler. Press: the module's deploy action
(KSPAction, Activate). Done: the deflection still for 0.5 s (the airbrake rule, D.3).

Sidecar (`parsek-part-parity-case/1`, additive keys, every key always written, null when not
measured; tables in `PartParityJson`, mirrored in `partparity.py`): a `motion` block (`family`,
`kind` `clip|servo|surface|canopy`, `pressUT`, `predictedS`, `ghostClipS`, `eventDelayS`,
`ghostStartDelayS`, `window` {`endS`, `spacingS`, `capped`}, `scout` {`stepS`, the ghost's pose
progress at every grid UT}, `frames[]` {`k`, `targetS`, `realS`,
`realClock`, `realProgress`, `ghostProgress`, `matchedS`, `imgProgress` per row and view, `views[]`
with `same` and `matched` measure blocks}, `transforms[]` {`path`, `travelReal`, `travelGhost`, the
per-frame fractions of both sides}, `timing` {`real` and `ghost` crossings, the G.1 measures and
flags}), `motionAtlas` {`file`, `tile`, `rows`, `cols`}; the manifest's `settings` gain `motion` and
`frames`. `stateProof` (D.2) keeps the press, done signal and events, so the press gate reads a
motion case like a state case.

### G.3 Controls, verdicts, report-only

Controls (`TestCommandPartParitySweep.ControlSetFor` on both sides, as D.2). An orbit motion run
records `ctl-empty`, `ctl-ghost-absent`, `ctl-real-vs-real` (the capture primitive, unchanged) and
three motion controls, each on `longAntenna` (the P6 state controls' subject) and each judged
RELATIVE to its own normal event route, so the product's own timing (G.6) cannot move a control:

| Control | Subject and change | Required |
| --- | --- | --- |
| `ctl-motion-real-vs-real` | two `longAntenna` in one wave, pressed in the same frame; copy B's live frames stand in for the ghost rows (same index; no matched row) | both copies' curves agree: `|lagS|` at most one frame interval or 0.02 T, `|durationRatio - 1| <= 0.05`, `curveShapeDev <= 0.05`; frame 0 and the last frame within the real-vs-real allowance of 4.5. This is the timing noise floor of the live capture. |
| `ctl-motion-ghost-slow` | the normal case plus a second event route whose built deploy info has its clip length doubled (the rig's own ghost instance, after the build; no production code changes) | `durationRatio(slow) / durationRatio(normal)` in [1.9, 2.1], the onset unchanged within one frame interval, the slow route failing on duration while its last frame passes the P6 end-state rules: an end-state comparison cannot see it, the motion judge does |
| `ctl-motion-ghost-late` | the normal case plus a second event route with every polled event moved 1.0 s later | `lagS(late) - lagS(normal)` in [0.98, 1.02] s (exact by construction: the ghost is posed at the real frames' UTs), `durationRatio` unchanged within 0.02, the late route failing on lag |

An air motion run records `ctl-empty`, `ctl-ghost-absent` and `ctl-motion-real-vs-real-air` (two
`parachuteSingle` pressed together, the canopy-scale curves only). A control that does not behave
is INVALID(parity-controls).

Verdicts (`harness/lib/partparity.py`, `judge_motion`; every threshold a constant at the top of the
module, starting guesses re-pinned from the reading runs against the controls, no flight needed):

| Level | Rule |
| --- | --- |
| `fail` | `motion-not-modelled`; `motion-snap`; `|lagS| > max(0.5 s, 0.25 T_real)` (T_real = real t98 - t02); `durationRatio` outside [0.6, 1.67]; `curveShapeDev > 0.30`; servos `angleMaxDevDeg > max(5 deg, 0.15 x sweep)`; on the two new families only (servos, control surfaces), the last frame judged by the P6 picture and structure rules |
| `warn` | each numeric line at half its distance from the ideal value (lag and deviations at half, ratios outside [0.8, 1.25]); `motion-stepped` |
| `error` | a P6 setup fault (`action-failed`, `state-timeout`, ...), `frames < 5`, or no real frame reaching 0.9 inside the window (the window rule failed) |

Report-only from the start, never in a verdict: the per-frame picture verdicts (a late ghost fails
every in-between frame; the timing measures carry that finding once), `staggerDev`, `travelRatio`
(`travel-differs` outside [0.9, 1.1]), picture progress and its disagreement with pose progress
(`progress-disagree`, an instrument hint, when the two t50 differ by more than two frame intervals),
`motion-clipped-at-border`, `frame-jitter`, rotors, the chute pictures (canopy aim, D.4), and the
last frame of every family P6 already judges.

Gates, as P6 / P8, in the same order: host-damaged > controls (the motion set) > state-press (the
motion families) > instrument > incomplete. The instrument gate recomputes, for every control and
`ceil(2 %)` of the motion cases (at least one; `random.Random(<runId>)`), each frame's measures and
picture progress from the motion atlas, and for EVERY motion case the G.1 crossings and measures
from the sidecar's per-frame arrays (a pure function, exact to 1e-9); `scout-render-disagree` on
any case is a mismatch. The lanes are report-only (`gating = false`); arming follows the P5 rule
(two matching reading runs, a baseline section per label with regression keys
`<caseId>/motion/<measure>`, an accepted list for the known differences of G.6).

### G.4 Lanes and wall time

Measured basis: P6 orbit state cases about 3.3 s per case at waves of 4 with one done wait (D.8);
E.8's 2.2 ms per render with readback. A motion case waits two motion times instead of one (the
window of G.2) and adds its ghost rows after the window: 17 frames x 2 rows x 2 views x 2 clears =
136 renders, about 0.3 s, plus about 200 unrendered scout primes per 10 s of window (under 0.1 s).
Planning rate 5 s per case (P6's measured rate plus the second wait, with P6's lesson that the first
estimate ran 50 % low).

| Lane | Install | Cases (motion + controls) | Sweep estimate | Steps (480 s, +50 %) | `budgetSeconds` |
| --- | --- | --- | --- | --- | --- |
| PP-11 (new, `pp11`), orbit | stock-minimal | about 53 + 22 robotics + 24 control surfaces = 99; 6 | about 555 s | 3 | 2400 |
| PP-12 (new, `pp12`), orbit | modded-compat | about 64 + 22 + 24 = 110; 6 | about 610 s | 3 | 2400 |
| PP-5 / PP-6 (third label `pp5mo` / `pp6mo`), air | stock-minimal / modded-compat | 10 chute cases (5 semi, 5 full); 3 | about 70 s (a semi wave about 15 s, a full wave about 35 s: semi done 1.7 to 3.5 s after the press, full 4.9 to 13 s, D.4) | 1 | 3000 (from 2400) |

Each orbit lane: LoadGame, the four settings, `WarpToUT`, `op=reset motion=all`, three `op=run
motion=all controls=true maxSeconds=480`, quit without saving; tier `operator`, report-only,
`[retry] policy = "none"`, one slot each, on the installs PP-1 to PP-4 use. The orbit lanes are new
lanes and not a label in PP-3 / PP-4, because those are ARMED and the P5 gate reds a label with no
baseline section (`no-baseline`, Appendix F). The chutes join PP-5 / PP-6 (report-only) as a third
label with its own reset and run step, since 70 s of sweep does not earn its own boot and LoadGame
(fast-lane rule, the `pp5fx` precedent); the budget gains that reset and run step (120 s + 540 s).
No modded-restock lane: motion does not depend on Waterfall, and modded-compat holds the ReStock
models. Each lane's first flight is a `limit=20` reading flight and its step count is set from its
`wall=` (D.8). The ids PP-11 and PP-12 are free on `origin/main`, on every open PR branch and on the
102 most recent remote branches (checked 2026-11-26). Expected wall per full pass: about 700 s
(PP-11), 760 s (PP-12), plus about 90 s on each of PP-5 / PP-6.

Air risk: a full case falls for the semi wait plus a window of up to about 27 s from 1600 m; P6's
300 m terrain guard ends a case `died-settling`. If the reading flight shows it, the motion label
spawns at 1900 m (still inside the 2000 m flying unpack range, D.4).

### G.5 Viewer

- A motion case joins its part's skin like a P6 state case (D.7), its family words after P6's and
  P8's (`motion-*` states), with its chips: lag, duration ratio, curve shape, and the flags.
- Strip block, per view: REAL | GHOST same time | GHOST same progress | DIFFERENCE (real against the
  same-time ghost), CSS crops of the motion atlas. One JavaScript clock sets a single CSS variable
  (the frame index) that every row's `background-position` reads, so all rows move in lock step;
  Play / Pause plays the frames at their REAL offsets (true speed, with a 0.25x toggle), since the
  frames are not evenly spaced in time (window extension, doubled spacing) and CSS `steps(K)` (the
  F5 strips) assumes they are; a scrubber (`input type=range`) sets the frame; with
  `prefers-reduced-motion` the strips start paused. No canvas, so it works on `file://`.
- Timing chart, inline SVG per case: real and ghost pose progress against the seconds since the
  press, the press and the event UT as ticks, t10 / t50 / t90 marks, the lag drawn between the two
  t50 marks, and the scrubber's frame as a vertical line.
- Details: the per-frame table (offset, stock clock, both progresses, the same-time and matched
  measures per view), the per-transform travel and stagger, the press and event proof.
- Filters: Family gains `robotics`, `control-surface`; a Motion flags filter (`motion-snap`,
  `motion-stepped`, `motion-not-modelled`, `travel-differs`, `progress-disagree`). Regression:
  per measure moves past its own warn distance, and flags added or dropped.
- `--gif` (the reserved flag of section 8, built here): per motion case an animated PNG
  `<caseId>.motion.apng` (default) or a GIF (`--gif-format gif`), each frame the `side-a` REAL |
  GHOST same time | GHOST same progress tiles side by side, each frame shown for its real duration
  (at least 20 ms). Stdlib only: the atlas decoded by `gui_mirror.py`'s `read_png`, APNG written as
  zlib plus the `acTL` / `fcTL` / `fdAT` chunks, GIF through a median-cut 256-colour palette and an
  LZW writer. Written under the site's `img/`, linked from the case; the case row's `strips` slot
  (`part_parity_view.py`, now `None`) carries the motion atlas and the animation paths.
  Round-trip decode tests in `harness/lib/test_part_parity_view.py`.

### G.6 Motion defects visible from the code (to be measured by P7)

Read on `origin/main` a3812a8d4. Each is a hypothesis until the reading flight measures it (M0
checks the first live); filed as todo GHOST-DEPLOY-MOTION-TIMING-2026-11-26.

1. A deploy plays one clip LATE on the ghost, for five of the seven in-orbit families. The pollers
   emit only at the END of stock's motion: deployables skip `EXTENDING` / `RETRACTING`
   (`Recording/PartStatePoller.cs:737-739`), gear skips "Deploying" / "Retracting" (`:776-777`), a
   cargo bay mid-travel reads neither open nor closed (`Recording/FlightRecorder.Classifiers.cs:261-276`),
   a standalone animate generic is deployed only at `animTime >= 0.99`
   (`FlightRecorder.Classifiers.cs:86-90`, read at `FlightRecorder.ReflectionClassifiers.cs:856`),
   a ladder's `Extending` name reads as unknown (`FlightRecorder.ReflectionClassifiers.cs:805`). The
   ghost then STARTS its transition at that event UT
   (`Ghost/GhostPlaybackLogic.HeatDeployablesLights.cs:615-618`) and runs one clip from there
   (`:201`). So a replayed panel, gear leg, bay door or ladder begins to move when the real one had
   finished, and finishes one clip after it: several seconds for a large panel or bay, and a gear
   deployed just before touchdown is still swinging out when the ghost lands. P6's own event-route
   prime at `tE = last event UT + clip + 1 s` (D.5) is consistent with this. Animation groups (their
   `isDeployed` is set before the clip, D.3), the airbrake (read from its event activity at the
   press, `FlightRecorder.ReflectionClassifiers.cs:886-905`) and chutes (state set at the start)
   emit at the start, so they do not lag.
2. The in-between pose is a straight line between two sampled ends. The ghost samples a clip at two
   times only (`Ghost/GhostVisualBuilder.AnimationSampling.cs:523-536`, the scored branch `:470-508`)
   and poses every transform at one shared fraction with `Lerp` / `Slerp`
   (`GhostPlaybackLogic.HeatDeployablesLights.cs:249-254`). Stock's keyframe curves (ease in and
   out), a staged clip (one panel segment after another) and a transform that turns more than 180
   degrees (Slerp takes the short way) are lost: `curveShapeDev`, `staggerDev`, `travelRatio`.
3. Chutes snap open. The semi and full canopy poses are written at once
   (`Ghost/GhostPlaybackLogic.PartEvents.cs:916-919`, `:1702-1705`), while stock plays its clips
   over seconds: the five stock-minimal chutes carry `semiDeploymentSpeed = 0.5` and
   `deploymentSpeed = 0.12` / `0.16667` (the install's ConfigCache; that they are the clip speeds is
   M0 U6), and P6 measured semi done 1.7 to 3.5 s after the press and full 4.9 to 13 s (D.4).
   `motion-snap`.
4. Servos move in 4 Hz steps. A hinge, rotation servo or piston ghost is posed only when an event
   is applied (`Ghost/GhostPlaybackLogic.Robotics.cs:287-300`); the per-frame pass advances only
   rotors and wheels (`:353`, `:371`, `:411`); the recorder samples a moving servo at most every
   0.25 s on a deadband change (`Recording/FlightRecorder.cs:606`, `BackgroundRecorder.cs:209`). So a
   sweep replays as a staircase up to one sample behind: `motion-stepped`, `angleMaxDevDeg`.

To measure, not claimed: an airbrake or control surface has no Animation, so its ghost runs the
3 s default clip (`GhostVisualBuilder.AnimationSampling.cs:732-740`,
`GhostPlaybackLogic.HeatDeployablesLights.cs:150`) where stock moves at `actuatorSpeed` (the
`airbrake1` config: 20 deg/s), so the duration is right only for a 60 degree deploy; a part with
two clip-driven families shares one clip length, last written wins
(`GhostVisualBuilder.AnimationSampling.cs:459`).

### G.7 Work breakdown

Rule (D.10, E.9): two pieces in flight never touch one file, each is green on its own, and every C#
literal a Python cell mirrors changes in ONE piece with its mirror. M1 adds the mirrored
`PartParityMotionRules.cs`, holding every motion constant. Docs: each piece edits only its own G
subsection.

| # | PR | Files | Runs alongside | Acceptance |
| --- | --- | --- | --- | --- |
| M0 | Spike (throwaway branch, never merged, the P0 / E0 pattern), two flights on stock-minimal and one in the air: U1 rendering the real part on target frames during its live clip (the frame time and the stock clip clock, at a wave of 4); U2 a ghost primed afterwards at increasing UTs gives the same pose as one advanced frame by frame in lock with the real (fraction within 1e-4); U3 the one-clip lag of G.6 item 1 on `solarPanels4` and `GearSmall` (`eventDelayS` against the clip length); U4 a lone servo in orbit driven through its target field with electric charge, and the recorder's robotic poll reading it; U5 a control surface deploy on the radial context, its sign; U6 the speed fields stock applies to each family's clip (decompiled `ModuleParachute`, `ModuleDeployablePart`, `ModuleAnimateGeneric`, `ModuleWheelDeployment`, `RetractableLadder`); U7 the framing ghost's box against the real motion's swept box | spike branch only | before everything | every unknown answered in G.2 |
| M1 | The contract, both sides: `PartParityMotionRules` (families, predicates reused from `PartParityStateRules`, the two new ones the pollers' own (`PollControlSurfaces`, the robotic poll), exclusions, words, the `motion=` / `frames=` args and their refusals `motion-arg-invalid` / `frames-arg-invalid`, `IsMotionRun` at every state-run site, `PlanCases`, `ControlSetFor`, `PlanFrameTargets`, the progress and timing math, the control judges' thresholds, the key tables and `MotionAtlasRows`); AND every Python mirror (`partparity.py` constants, readers, `control_set`, the sync cells, hlib's arg table); committed fixture part lists `Source/Parsek.Tests/Fixtures/PartParity/motion-parts-<profile>.txt` with xUnit count pins | `PartParityMotionRules.cs` (new), `TestCommandPartParitySweep.cs`, `PartParityJson.cs`, `PartParitySweepPlan.cs`, xUnit, `harness/lib/partparity.py` (constants, readers), `test_partparity.py` (sync class), `hlib.py` (arg table) | M2 | `dotnet test` and the lib suite green; with `motion=none` every plan, control set and `settings` equals today's for PP-1 to PP-10; synthetic curves move exactly their own measure (late, slow, eased, staged, snapped, stepped) |
| M2 | Robotic poll extraction: the active recorder's per-module robotic poll moved to `PartStatePoller.PollRobotics`, called by `FlightRecorder`; the background recorder untouched (the S2 light precedent) | `Source/Parsek/Recording/PartStatePoller.cs`, `FlightRecorder.PartEvents.cs` | M1 | `scripts/refactor/il_compare.py --fold` shows the moved body only; recorder tests green |
| M3 | Live motion engine: `PartParityMotionDriver` (P6's press, step, poll and observe reused from `PartParityStateDriver`; the framing ghost; the live real frames; the scout and render ghosts; the servo drive and the radial context; the three controls' altered routes; the motion atlas through `PartParityImage`), the motion wave dispatch in `PartParitySweepRunner`, the chute motion hook in `PartParityAirContext.RunWave` (keeps the `DecideAdmission(args, active,` call the hlib cell reads), the in-game cell's `motion=` filter (no new `[InGameTest]`) | `PartParityMotionDriver.cs` (new), `PartParityMotionMetrics.cs` (new, pure, no constants), `PartParitySweepRunner.cs`, `PartParityAirContext.cs`, `PartParityCaseCapture.cs` (keeps `AtlasRowsFor`), `PartParityInGameTests.cs`; no mirrored file | M4 | a dev-install run over `longAntenna`, `solarPanels4`, `GearSmall`, `mk2CargoBayS`, `telescopicLadder`, `airbrake1`, `hinge.01`, `StandardCtrlSrf` (radial) and the three controls: every case writes its frames, controls ok, nothing left after teardown, sidecars parse |
| M4 | Harness judge: `judge_motion`, the flags, the motion recompute (atlas frames and the timing arrays), the motion controls' judges, the press gate over the motion families, the verdicts JSON | `harness/lib/partparity.py` (logic), `test_partparity.py` (new cells) | M3 | lib suite green on synthetic sidecars: each control's perturbation is caught, a mutated frame reds INVALID(parity-instrument), a stowed / state / FX sweep judges byte-identically to before |
| M5 | Viewer and `--gif` (G.5) | `harness/tools/part_parity_view.py`, `harness/lib/test_part_parity_view.py` | M3 (after M4) | viewer tests green (the node page tests read back lock step, the scrubber and the chart); the APNG and GIF decode back to the atlas tiles |
| M6 | Orbit lanes PP-11 / PP-12: specs, lane catalog regen, hlib wiring cells (the lane shape cells and the operator-tier reason table in `test_hlib.py`), `autotest-status.md` rows | `harness/scenarios/PP-11-*.toml`, `PP-12-*.toml`, `docs/dev/flight-profiles.md`, `docs/dev/autotest-status.md` | M7 (C# half) | harness suites green; first flights on request (`limit=20`, then full), findings filed |
| M7 | Air motion labels `pp5mo` / `pp6mo` in PP-5 / PP-6 | the two specs and the M6 doc rows, after M6 | M6 | a dev-install run of `parachuteSingle` and `parachuteLarge` semi and full writes its frames |

Order: M0; then M1 and M2 together; M3 and M4 together (M4 on M1's synthetic helpers); M5 after
M4; M6 after M3 and M4; M7 after M3 (its lanes after M6). The C# pieces get the end-of-task review;
M2 carries its IL fold proof as its review evidence.

What P7 reuses from P6 and P8 unchanged: the D.1 family table, predicates, presses, done signals,
timeouts and exclusions; `PartParityPollSink` and the shared pollers; the case preparation and its
two repairs; `GhostCoOrbitalRig.TryBuildGhostFromSnapshot`, the D2 seam and
`PrimeHiddenPartStateAtUT`; the capture primitive `CaptureSides`, the light rig and the double
clear; section 6's measures and their Python recompute; the wave slots, worst-case admission, host
guard, resume, harvest and archive; the press gate and the gate order; the air context; F5's strip
crops and play button (extended to a clock).

### G.8 Owner questions

None block the plan. Three decisions follow the reading flight, each about a fix, not about P7:

- G.6 item 1 (the one-clip lag): fix on playback (for a family whose event marks the END of stock's
  clip, start the ghost's transition one clip before the event UT; a pure function of UT, so every
  old recording heals) or in the recorder (emit at the start edge; new recordings only).
- G.6 items 2 to 4 (curve, chute snap, servo steps): each is playback-only (more clip samples at
  build, the canopy clip over stock's speeds, interpolation between servo samples) and records
  nothing new; the reading flight shows how visible each is at playback speed.
- Whether the P7 lanes are armed once two reading runs match (P5 rule), or stay report-only.

### G.9 Review disposition

Rev 1 had a self-review against the code (the window for a late or slowed control route, the
ghost crossings read from the scout grid, the framing pose per family); the clean-context review
(merge-blocking findings only) is owed on the PR, since none could start when rev 1 was written.
