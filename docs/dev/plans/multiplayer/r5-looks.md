# Looks: suits, ghost kerbals and player colors - implementation plan

Status line: PLAN (2026-11-14), not started. Design sections covered: 7.4 in full; 7.2 and
7.3 where a look is picked, changed or moved by the clash rule; edge cases 4, 6, 7a and 29
of section 8; the look rows of section 12; additions A4 and the style half of A5 (15.11);
spike S3. Phase R5 (with the A4 and A5 seams landing earlier, see section 5).

## 1. Goal and scope

Built here:

- **Rival ghost kerbals in the rival's look (A4).** Any kerbal EVA part in a rival's ghost
  snapshot resolves to a part this machine has, dressed in the rival's look or its
  replacement. No rival kerbal falls back to the placeholder sphere because a suit's part is
  missing here.
- **Player colors (A5, style half).** A neutral per-trajectory style (color and label
  prefix) the ghost and map code read without naming the race. Rival ghost map vessels carry
  the color on their orbit line and stock map icon, and the prefix in their name. The fallback
  sphere takes the color. Parsek-drawn map overlays and the watch title take it only if the
  rival lane uses them (R3 question 2; watching is optional).
- **The look table's visual half.** Per look: the suit style, the expansion it needs, the
  EVA part per gender, the replacement set, and the four UI colors (S3 picks the values).
- **Own roster dressing (`RaceLooks`).** A kerbal that joins the roster of a race save (the
  starting crew, a hire, a rescue, a Parsek stand-in) is dressed in the player's look. The
  stock suit picker stays the player's own: a suit the player picked is never forced back. A
  look changed in Settings or by the clash rule re-dresses, once, at the next load, only the
  kerbals that still wear the old look's dress.
- **The two replacement suit sets** for a machine without Making History or Breaking Ground,
  and their packaging (or a runtime recolor; question 10.2).

Not built here:

- Look assignment, the clash rule's choice of the free look and its screen message, the
  refusal of a fifth player, `player.cfg` / `local.cfg`, the Settings control that changes
  the look: R1.
- The rival lane, `RivalMapPresence`, the rival style source's mapping from a flight to its
  player, floating labels over rival ghosts in flight (R3W-16): R3. R5 supplies the look and
  the color they read.
- The Space Race window and its use of the colors (R4-09 to R4-10): R4 reads the colors from
  the look table this plan fills.
- Own ghosts (the player's own recordings) keep today's look: no suit color, default grey.
  A single-player change is out of scope.
- No new player-facing surface. No co-op behavior.

## 2. Starts after

- **Day one (base Parsek, no race code):** R5-01, R5-02. R5-03 after P2c-04 and P2a-02 (it
  changes the engine constructor they also change; order P2c-02, P2c-04, P2a-02, R5-03,
  R3W-09, R3W-14, ruling A3), before R3W-15 (OV-43) and before P2b-02 opens its window
  (task index C.1). R5-04 after P3-04 (the extracted `GhostMapVessels` service); its
  orbit-color helper lands in R3W-18's pull request, its first production caller, before
  R3W-19 (OV-44, OV-48, ruling A1). R5-05 only if R3 question 2
  is answered (b). R5-06 after P3b-03 and only if watching a rival (R3W-26) is built; P3b is
  deferred out of R0b, so R5-06 waits with P3b and R3W-26.
- **After R1:** R1-01 (the RaceCore / RaceWorld module rules, layers, headless list) for
  R5-07 and every later task; R1-06 (the `race-no-writer` gate whose allowlist reserves the
  `RaceLooks` entry) before R5-11 fills it; for R5-11: R1-03 (`ParsekRaceScenario` and its
  child Save / Load hooks, OV-28), R1-08 (`RaceSession`, the look field of `RaceLocal` /
  `player.cfg`) and R1-09 (look assignment and the clash rule). R5-11 does not wait for
  R1-10 or R1-11 (task index E.3.7).
- **After R3:** R3W-09 (flight composition), R3W-15 (rival style source; see 9.2), R3W-18
  (`RivalMapPresence`), R3W-20 (Tracking Station composition), R3W-21 (missing-part strip;
  see 9.3) for R5-09.
- **Spike S3 must be done before R5-08, R5-10, R5-11 and R5-12.** R5-01 to R5-07 do not
  wait for it (R5-07 ships placeholder colors). S3 has to answer, on the owner's PC:
  - **S3a** Each look in game: the four suits on a live EVA kerbal, the four UI colors
    (RGB), the stock default combo id per suit and gender, and what a fresh kerbal from
    `KerbalRoster.GetNewKerbal` wears (suit, combo id, texture paths).
  - **S3b** The two replacement sets: built on the base suit or the slim suit; per gender;
    shipped art or a runtime recolor; what shows in EVA, in IVA and on the crew portrait
    when only `SuitTexturePath` / `NormalTexturePath` / `SpritePath` are set.
  - **S3c** A save made with a DLC suit, loaded on a machine without the DLC: what `suit`,
    `ComboId` and the texture paths read after load (does `ComboId` survive the stock
    fallback to Default).
  - **S3d** A ghost kerbal: does the EVA part prefab's own material already show the suit's
    look (the prefab never runs `KerbalEVA.OnStart`); does setting the main and normal
    texture on a clone of the body, helmet and neck ring materials show a replacement;
    which shader property names the suit textures use.

## 3. What the code looks like today

### 3.1 How an EVA ghost is built

- The timeline ghost build entry points are
  `GhostVisualBuilder.BuildTimelineGhostFromSnapshot` (`GhostVisualBuilder.cs:109`) and
  `TryBeginTimelineGhostBuild` (`GhostVisualBuilder.cs:125`); the work is done part by part
  in `AdvanceTimelineGhostBuild` (`GhostVisualBuilder.cs:173`).
- The part name is read from the snapshot `PART` node (`name`, else `part`) and cleaned by
  `TryExtractPartName` (`GhostVisualBuilder.cs:185-186`, `GhostVisualBuilder.Parsing.cs:63`),
  then looked up by `ResolveAvailablePart(partName)` (`GhostVisualBuilder.cs:196`), which
  tries the direct, dotted, underscored and brute-force forms
  (`GhostVisualBuilder.Parsing.cs:74-130`) and warns `ResolveAvailablePart failed`
  (`GhostVisualBuilder.Parsing.cs:121`). A miss counts `skippedPrefab`
  (`GhostVisualBuilder.cs:197-199`); a hit calls `AddPartVisuals`
  (`GhostVisualBuilder.cs:216`, declared at `:968`).
- **The sphere fallback.** `CompleteTimelineGhostBuild` (`GhostVisualBuilder.cs:334`) logs
  `Ghost built:` (`:340-344`, VerboseRateLimited; harness spec
  `harness/scenarios/FGV-1-fairing-ghost-visual.toml:131` regexes it) and, when no part
  produced a visual, warns `Ghost build produced zero visuals` and returns null
  (`:346-351`). The engine then builds `GhostVisualBuilder.CreateGhostSphere`
  (`GhostPlaybackEngine.GhostVisuals.cs:163-167`) in the fixed `ghostColor`
  (`GhostPlaybackEngine.GhostVisuals.cs:84`), a 12 m sphere
  (`GhostVisualBuilder.cs:1896-1900`). An EVA kerbal is a one-part vessel, so a missing
  kerbal part gives the sphere. A kerbal in an external seat on a larger vessel only drops
  out (the other parts still build).
- **Materials are shared, not cloned.** A cloned skinned renderer takes the prefab's
  `sharedMaterials` (`GhostVisualBuilder.PartInfoBuilders.cs:766`, inside
  `CloneSkinnedMeshRenderers` at `:685`); a mesh renderer the same
  (`GhostVisualBuilder.cs:1171`). Materials are cloned later only for variant texture rules
  (`GhostVisualBuilder.cs:1191-1199`) and for renderers that carry a color-changer shader
  property (`CollectColorChangerMaterials`, `GhostVisualBuilder.cs:1639`). So every ghost
  kerbal wears the prefab material.
- **Materials the engine owns are destroyed.** `DestroyGhostResources`
  (`GhostPlaybackEngine.SpawnDestroy.cs:455`) destroys every entry of `state.materials`
  (`:463-469`); a snapshot build leaves that list empty
  (`GhostPlaybackEngine.GhostVisuals.cs:195-203`). A cached look material must never enter
  that list.
- The engine is constructed with one positioner (`GhostPlaybackEngine.cs:362`); the one
  production construction is `ParsekFlight.cs:1210`. The build call is at
  `GhostPlaybackEngine.GhostVisuals.cs:138`. Other callers of the snapshot build:
  `ParsekFlightPlaybackHost.cs:3103`, `ParsekKSC.cs:1607`, and in-game tests.
- The canopy logic keys on `partName.StartsWith("kerbalEVA")`
  (`GhostVisualBuilder.PartInfoBuilders.cs:186`); every suit part name starts that way.
- **The eight kerbal parts** (`docs/dev/research/stock-tech-tree-part-unlocks.md:925-939`,
  `docs/dev/research/part-coverage-catalog.md:611-618`): `kerbalEVA`, `kerbalEVAfemale`,
  `kerbalEVASlimSuit`, `kerbalEVASlimSuitFemale` (base game); `kerbalEVAVintage`,
  `kerbalEVAfemaleVintage` (Making History); `kerbalEVAFuture`, `kerbalEVAfemaleFuture`
  (Breaking Ground, with `ModuleColorChanger` and `ModuleEvaChute`). Gender is the
  substring `female`, in either case (`SlimSuitFemale`).

### 3.2 Stock suit facts (decompiled `Assembly-CSharp.dll`, KSP 1.12.5, read with ikdasm)

- `ProtoCrewMember.KerbalSuit` = { Default 0, Vintage 1, Future 2, Slim 3 }. Saved per
  kerbal by `ProtoCrewMember.Save`: `suit`, `suitTexturePath`, `normalTexturePath`,
  `spritePath`, `comboId` (each when set), `lightR/G/B`, `hasHelmetOn`, `hasNeckRingOn`,
  `hasVisorDown`. Properties: `suit`, `SuitTexturePath`, `NormalTexturePath`, `SpritePath`,
  `UseStockTexture`, `ComboId`.
- `set_suit` ignores a suit whose expansion is not installed; `get_suit` and the load
  constructor reset such a suit to Default
  (`Expansions.ExpansionsLoader.IsExpansionKerbalSuitInstalled`).
  `ProtoCrewMember.GetKerbalEVAPartName()` picks the EVA part from gender and suit with the
  same check.
- `KerbalEVA.OnStart` branches on `ProtoCrewMember.SuitTexturePath != null`
  (IL_0377 to IL_041d): set, it calls `SetNewTextures` (reads the paths through
  `GameDatabase.GetTexture`, falling back to the combo default); unset,
  `SetDefaultTextures`. Both work on `helmetMesh`, `bodyMesh`, `neckringMesh` (public
  `SkinnedMeshRenderer` fields) through `Renderer.material`, so a live EVA kerbal gets
  instance materials. A dress by texture path therefore works on EVA without a combo entry.
- `SuitCombos` reads every `SUITCOMBOS` / `SUITCOMBO` node in `GameDatabase` (required:
  `name`, `displayName`, `suitType`, `gender`); the stock picker
  (`HelmetSuitPickerWindow.SetExtraCombo` / `SetDefaultCombo`) writes `suit`,
  `SuitTexturePath`, `NormalTexturePath`, `SpritePath` and `ComboId`. A `SUITCOMBOS` node
  anywhere under `GameData` is therefore listed in the stock picker of every save.
- `GameEvents.onKerbalAdded` fires only in `KerbalRoster.AddCrewMember`, which
  `GenerateInitialCrewRoster` (new game, before any scenario exists), `GetNewKerbal` and
  `AddApplicant` call. A hire from the Astronaut Complex is a type change (Applicant to
  Crew), a rescue a type change (Unowned to Crew).
- `OrbitRendererBase` has public `orbitColor`, `nodeColor` and `SetColor`;
  `OrbitRenderer.Start` assigns both, so a color set before `Start` is lost. The focused
  vessel is drawn in ElectricLime whatever the fields hold.

### 3.3 What Parsek's crew code does with kerbals

- No Parsek code reads or writes a suit field (a repository grep for `suit`,
  `SuitTexturePath`, `ComboId` in `Source/Parsek` finds none). A dress cannot collide with a
  Parsek write.
- Stand-ins are created with `roster.GetNewKerbal(Crew)` (`KerbalsModule.Roster.cs:207-221`)
  and recreated with `GetNewKerbal` then `ChangeName` (`KerbalsModule.Roster.cs:223-235`);
  both fire `onKerbalAdded`. Spawned crew and tourists come from `GetNewKerbal`
  (`VesselSpawner.CrewRoster.cs:678`, `:693`).
- A committed hire reached after a rewind is promoted by setting `pcm.type = Crew` inside
  `SuppressionGuard.Crew()` (`KerbalsModule.Roster.cs:413-438`, the write at `:436`); a
  dismissal sets `Applicant` (`KerbalsModule.Roster.cs:524-557`, the write at `:555`). Both
  fire `onKerbalTypeChange`, so a dress hook sees Parsek's own re-hires.
- `GameStateRecorder` subscribes to `onKerbalStatusChange` and `onKerbalTypeChange`
  (`GameStateRecorder.cs:768-769`); the Kerbals window to `onKerbalAdded`
  (`UI/KerbalsWindowUI.cs:319-335`). Neither reads a suit.

### 3.4 Where a ghost's color and label are set today

- Map markers: `MapMarkerRenderer` (module Controllers, `scripts/arch/modules.toml:102`).
  The stock icon is drawn in the fixed `StockVesselIconColor`
  (`MapMarkerRenderer.cs:106`, used at `:350`); the color argument only tints the fallback
  diamond (`:355`). The label color is `GetLabelColor()` (`:222`, used at `:362`) and the
  text is `"Ghost: " + label` (`:364`). Callers: `ParsekFlightMapMarkers.cs:1001`, `:1011`,
  `:1034`; `ParsekTrackingStation.cs:983/986`, `:1406/1409`.
- The trajectory polyline is styled by `ApplyStockOrbitLineStyle`
  (`Display/GhostTrajectoryPolylineRenderer.LegDraw.cs:400`) in the stock grey
  (`:417-423`).
- Ghost map vessels: the name is `GhostVesselNamePrefix + vessel name`
  (`GhostMapPresence.cs:756`, `GhostMapPresence.ProtoVesselLifecycle.cs:1974`); the orbit
  renderer's draw mode and icons are set at `GhostMapPresence.ProtoVesselLifecycle.cs:1868-1880`;
  no color is ever set. P3 moves this into `GhostMapVessels` (r0b plan, P3-02 to P3-04).
  The orbit-line patch is `Patches/GhostOrbitLinePatch.cs:600-601`; its stamp sites are
  pinned by `LineBlinkWindowExitExemptionTests` and get no new sites here.
- Floating labels in flight exist only for own chain ghosts
  (`ParsekFlightSpawnHost.cs:688`, color fixed at `:704`). R3W-16 adds the rival ones.
- The watch overlay title is `"Watching: " + vesselName`
  (`WatchModeController.cs:1102-1107`).

### 3.5 Packaging and the module map

- The release zip is an explicit file map (`scripts/release.py:82-88`: the DLL, the version
  file, the licence, two toolbar textures). The dev deploy copies the DLL, pdb and version
  file only (`Source/Parsek/Parsek.csproj:112-117`). The harness DEPLOY copies the version
  file and textures (`resolve_parsek_aux_payload`, `harness/provision/provlib.py:257`). The
  repository's `GameData/Parsek/` holds only `Parsek.version`. A new `Suits/` folder ships
  nowhere until all three learn it.
- Module rules are first-match (`scripts/arch/modules.toml:7`); a root file starting with
  `Ghost` lands in Ghost (`:297-298`); Core is an explicit list and must stay last
  (`:470-479`). Layers: floor LogIO / Config / Core / Trajectory, model, features (Ghost,
  Display, MapRender, ...), presentation, entry (Controllers, Patches)
  (`modules.toml:511-518`). Core and Trajectory are headless (`modules.toml:531-537`).
- `UnityEngine.Color` and `Color32` are inert types for the headless check
  (`scripts/arch/facts.toml:22`); `ProtoCrewMember/KerbalSuit` is not (only `RosterStatus`
  and `KerbalType` are, `facts.toml:27`). So a style value with a `Color` may sit on the
  floor, and RaceCore cannot name the stock suit enum.
- In-game category `GhostVisuals` is pinned by `harness/scenarios/H15-corpus-ghost-visuals.toml:90`
  (`total=4`); a test added there reds `CommittedBatchTallySourceSyncTests`. This plan uses
  two new categories, one scene each (OV-09): `RaceLooksFlight` (FLIGHT) and `RaceLooks`
  (SPACECENTER).

## 4. Tasks

Each task's **S3** line says which spike answer (S3a to S3d, listed in section 2) would
change it.

### R5-01 Ghost build: a part-name substitute before the part lookup (A4, first half)

- **Change:** new `Source/Parsek/GhostBuildOptions.cs` (module Ghost through the `^Ghost`
  prefix rule): `internal interface IGhostPartDresser` with
  `string SubstitutePartName(string recordedPartName)` (null or empty = keep; R5-02 adds
  `DressMaterials` with its call site); `internal sealed class GhostBuildOptions {
  IGhostPartDresser Dresser; }`. `TryBeginTimelineGhostBuild` and
  `BuildTimelineGhostFromSnapshot` take an optional `GhostBuildOptions options = null`,
  stored on `PendingGhostVisualBuild`. In `AdvanceTimelineGhostBuild`, between the name
  extraction (`:186`) and the lookup (`:196`):
  `partName = ResolveBuildPartName(partName, build.options, out bool substituted)`, an
  `internal static` pure helper (keeps the method's complexity flat). Counted per build;
  one `ParsekLog.VerboseRateLimited` `[GhostVisual]` line per (recorded, used) pair:
  `Ghost part substituted: recorded=<a> used=<b>`. The `Ghost built:` line is not reworded.
- **Proof:** xUnit `GhostBuildOptionsTests`: null options and a null dresser return the
  name unchanged and log nothing; a substitute is used and logged; an empty substitute keeps
  the recorded name. In-game `[InGameTest(Category = "RaceLooksFlight", Scene = FLIGHT)]`
  `EvaGhostWithUnknownPartBuildsWithSubstitute`: a synthetic one-part snapshot named
  `kerbalEVAParsekMissing` returns null without options and a ghost with visuals when a
  test dresser maps it to `kerbalEVA`.
- **Uncalled-method check (A1):** reached by an existing production path. The old entry
  points gain an optional `options = null`, so every existing caller reaches the new code
  with the neutral default; `ResolveBuildPartName` is called from `AdvanceTimelineGhostBuild`
  and calls `SubstitutePartName` through the interface. No member waits for a later caller.
- **Registers:** `python harness/tools/lane_catalog.py` (new category `RaceLooksFlight`); no
  CHANGELOG (no player-visible change).
- **Size:** S. **Depends on:** none. **PC:** yes, one in-game run; the xUnit cells prove
  the null path. **S3:** none.
- **Risk:** low; an optional parameter that defaults to today's path.

### R5-02 Ghost build: a material override at the clone sites (A4, second half)

- **Change:** `AddPartVisuals` (`GhostVisualBuilder.cs:968`) and `CloneSkinnedMeshRenderers`
  (`GhostVisualBuilder.PartInfoBuilders.cs:685`) take the build's options, and
  `IGhostPartDresser` gains `Material[] DressMaterials(string partName, Part prefab,
  Renderer source, Material[] materials)`. Right after each
  clone assigns `sharedMaterials` (`GhostVisualBuilder.cs:1171`,
  `GhostVisualBuilder.PartInfoBuilders.cs:766`), when a dresser is set:
  `var dressed = dresser.DressMaterials(partName, prefab, sourceRenderer,
  clone.sharedMaterials); if (dressed != null) clone.sharedMaterials = dressed;`. The
  later variant and color-changer stages then work on the dressed materials (they clone
  what they touch, as today). The dresser owns the materials it returns; they never enter
  `state.materials`. One VerboseRateLimited `[GhostVisual]` count line per build
  (`dressedRenderers=N`).
- **Proof:** xUnit: none possible beyond a source gate (Unity types).
  `GhostBuildDresserSourceGateTests` pins that the override sits after both
  `sharedMaterials` assignments and nowhere adds to `state.materials`. In-game
  `RaceLooksFlight` `DresserMaterialReachesEvaBodyRenderer`: a test dresser returns a tinted
  clone for the renderer that equals the prefab's `KerbalEVA.bodyMesh`; assert the ghost's
  body renderer holds it, and that destroying the ghost leaves the dresser's material alive.
- **Uncalled-method check (A1):** reached by an existing production path. The options reach
  the two existing clone methods through R5-01's chain with the null default, and
  `DressMaterials` is declared in the same change as its two call sites.
- **Registers:** `scripts/arch/facts_pins.toml` re-pin only if the two methods cross the
  complexity watch band.
- **Size:** S. **Depends on:** R5-01. **PC:** yes, one in-game run. **S3:** S3d (if the
  prefab material is not per-renderer as expected, or the Future suit's color changer
  clones before this point, the hook moves to a post-pass over the part's renderers).
- **Risk:** medium; `AddPartVisuals` is long and the color-changer clone of a Future suit
  must see the dressed material, which the in-game test checks.

### R5-03 Engine: per-trajectory build options and style (A4 wiring, A5 type)

- **Change:** new `Source/Parsek/PlaybackTrajectoryStyle.cs` placed in Core by a new
  explicit rule before the final Core rule (beside `IPlaybackTrajectory`): an immutable
  `internal sealed class PlaybackTrajectoryStyle { readonly Color Color; readonly string
  LabelPrefix; }` (readonly fields set by its constructor, no accessor methods) and
  `internal interface IPlaybackStyleSource { PlaybackTrajectoryStyle StyleFor(int index,
  IPlaybackTrajectory traj); }` (null = no style). In `GhostBuildOptions.cs`:
  `internal interface IGhostBuildOptionsSource { GhostBuildOptions OptionsFor(int index,
  IPlaybackTrajectory traj); }`. `GhostPlaybackEngine` takes both as optional arguments of
  the constructor P2a-02 leaves (null for the own engine; `ParsekFlight.cs:1210` unchanged;
  if the arguments go on a new overload, the old constructor chains to it with nulls, and no
  test-only overload is added, as P2c-04 and P2a-02 do), passes the options
  at the build call (`GhostPlaybackEngine.GhostVisuals.cs:138`), and colors the fallback
  sphere from the style when one is set (`:84`, `:163-167`). This task owns the type
  `PlaybackTrajectoryStyle` (a sealed class; null means no style) and the engine arguments;
  R5-04 and R5-05 own the sites. R3W-15 only supplies the rival lane's style source through
  this type, with a neutral placeholder color until R5-07 (OV-43). `LabelPrefix` has one
  format, `"<display name> - "` (OV-45); the type stores the string and does not build it.
- **Proof:** xUnit `PlaybackTrajectoryStyleTests` (immutability, null style resolves to the
  existing `ghostColor`, through an `internal static ResolveSphereColor`); the engine suites
  unchanged with null sources. In-game: none needed (the sphere color is covered by R5-09's
  rival test).
- **Uncalled-method check (A1):** reached by an existing production path. The own engine's
  construction reaches the changed constructor with the neutral null sources; the engine
  calls `StyleFor` and `OptionsFor` where a source is set and `ResolveSphereColor` at the
  sphere build; the style's constructor is kept because other types name the type (its
  first production construction is R3W-15's source). No member waits for a later caller.
- **Registers:** `modules.toml` (Core rule for `PlaybackTrajectoryStyle.cs`);
  `headless_pins.toml` re-pin if the check counts the new file (no leak expected:
  `Color` is inert); `docs/dev/arch/README.md` only if the Core list text is quoted there.
- **Size:** S. **Depends on:** R5-01, P2c-04 (the same constructor); order: after P2a-02.
  Constructor order P2c-02, P2c-04, P2a-02, R5-03, R3W-09, R3W-14 (A3); R5-03 lands before
  R3W-15 (OV-43) and before P2b-02 opens its window. **PC:** no. **S3:** none.
- **Risk:** low; null sources reproduce today's engine.

### R5-04 Ghost map vessels carry the style (A5)

- **Change:** P3-04's service takes the finished `vesselName` from its caller, so the name is
  composed by the caller (OV-44). Name: `internal static string
  ComposeGhostVesselName(string name, PlaybackTrajectoryStyle style)` in `GhostMapPresence.cs`
  beside `GhostVesselNamePrefix` (the harness `test_saveparse` reads the prefix there), giving
  `GhostVesselNamePrefix + style.LabelPrefix + name` (the `Ghost: ` prefix stays first;
  saveparse and the save strip key on it). The own composition sites
  (`GhostMapPresence.cs:756`, `GhostMapPresence.ProtoVesselLifecycle.cs:1974`, wherever P3-04
  leaves them) call it with a null style, byte-equal to today, so it has a production caller
  in this change; the rival caller (`RivalMapPresence`, R3W-18) passes its style. Color:
  `GhostMapVessels` has no per-tick pass and this task adds none; it specifies a helper the
  caller's tick calls, `GhostMapVessels.ApplyOrbitStyle(Vessel vessel,
  PlaybackTrajectoryStyle style)`, which lands in R3W-18's pull request (A1):
  it sets `orbitRenderer.SetColor(style.Color)` once `OrbitRenderer.Start` has run and re-sets
  it when `nodeColor` differs (pure decision `internal static NeedsOrbitColor(Color current,
  PlaybackTrajectoryStyle style)`), logging one `[GhostMap]` VerboseRateLimited line per pid
  when it sets the color. Null style: nothing set, today's behavior. The orbit color of a
  rival map vessel is set by `RivalMapPresence`'s tick (R3W-18) through this helper (OV-44);
  the orbit-line patch is not touched.
- **Proof:** xUnit on `ComposeGhostVesselName` (null style byte-equal to today; a style gives
  `Ghost: Ana - Kerbal X`) and on `NeedsOrbitColor`. In-game `RaceLooksFlight`
  `StyledGhostMapVesselKeepsColor` (FLIGHT, map view): build an orbit ghost through the
  service with a composed name, call the helper each frame as a tick would, assert after
  three frames and after a map toggle that `orbitRenderer.nodeColor` equals the style and the
  vessel name starts with `Ghost: `; an unstyled one keeps the stock color.
- **Uncalled-method check (A1):** `ComposeGhostVesselName` is reached by the existing own
  path (null style) in this change. `ApplyOrbitStyle` and `NeedsOrbitColor` land with their
  first production caller: R3W-18's pull request adds them, with their xUnit cells and the
  in-game cell, as specified here.
- **Registers:** none beyond the lane catalog; `LineBlinkWindowExitExemptionTests` stays
  untouched (no new stamp site).
- **Size:** S. **Depends on:** P3-04, R5-03; the helper half lands in R3W-18. Order in
  `GhostMapVessels.cs`: P3-02 to P3-05, R3W-17, R5-04, R3W-19 (OV-48). **PC:** yes.
  **S3:** S3a (colors only).
- **Risk:** medium; stock may reassign the color in a path this plan has not read (scene
  switch to map, target selection); the in-game test watches three frames and a map toggle.

### R5-05 Parsek-drawn map overlays take the style (conditional, A5)

Lands only if R3 question 2 is answered (b) (a Parsek marker for a rival inside an
atmosphere) or a rival lane draws trajectory polylines.

- **Change:** `MapMarkerRenderer.DrawMarker` / `DrawMarkerAtScreen` take an optional
  `PlaybackTrajectoryStyle style = null`: icon tint (`:350`), label color (`:362`) and label
  text (`:364`) through three `internal static` resolvers that return today's constants for
  null. `ApplyStockOrbitLineStyle` (`LegDraw.cs:400`) takes the same optional style for the
  vertex color (`:417-423`). Own callers unchanged. The rival call site is in the entry
  layer (9.4).
- **Proof:** xUnit `MapMarkerStyleTests`, `LegLineStyleTests`: null style returns the exact
  current colors and text; a style gives its color with the marker opacity.
- **Uncalled-method check (A1):** reached by an existing production path (optional
  parameters on the existing draw methods, null from the own callers; the three resolvers
  are called inside them).
- **Registers:** `docs/dev/design-gui-inventory.md` (map markers may be tinted per player).
- **Size:** S. **Depends on:** R5-03, R3 question 2. **PC:** no. **S3:** none.
- **Risk:** low.

### R5-06 Watch overlay title carries the prefix (conditional)

Lands only with R3W-26 (watching a live rival ghost).

- **Change:** the P3b watch target carries the style; the title at
  `WatchModeController.cs:1102-1107` reads `"Watching: " + prefix + vesselName`.
- **Proof:** xUnit on the title helper (null style = today's text).
- **Uncalled-method check (A1):** reached by an existing production path (the existing title
  code calls the helper, with a null style for an own target).
- **Registers:** none.
- **Size:** S. **Depends on:** P3b-03, R5-03, R3W-26 (P3b is deferred out of R0b; this task
  waits with P3b and R3W-26). **PC:** no. **S3:** none.
- **Risk:** low.

### R5-07 The look table's visual half (RaceCore)

- **Change:** `Source/Parsek/RaceCore/RaceLookTable.cs`, owned by this task (OV-41); R4-10
  reads "look index to UI color" from it and R3W-15's placeholder color gives way to it. Own
  enum
  `RaceSuitStyle { Base, Slim, Vintage, Future }` (the stock enum is not inert, 3.5). Per
  look 0..3: suit style, required expansion (`none`, Making History, Breaking Ground), EVA
  part name per gender, replacement set id (none for looks 0 and 1), UI color as RGB bytes
  (placeholders until S3a). Pure helpers: `IsKerbalEvaPartName(string)`,
  `IsFemaleEvaPartName(string)` (case-insensitive `female`), `EvaPartFor(look, female)`,
  `BasePartFor(style, female)` (the base-game part a replacement is built on). Out-of-range
  look reads as look 0 with one `[RaceLooks]` Warn.
- **Proof:** xUnit `RaceLookTableTests`: four distinct colors; the eight part names
  classified by gender and suit; round trip look -> part -> look; out-of-range look.
- **Uncalled-method check (A1):** lands with its first caller. Its only callers in its own
  change would be xUnit cells (`Parsek.Tests`), which do not count. This task is written and
  reviewed as specified here, but `RaceLookTable.cs` is created in the pull request of the
  first task that calls it from production or from an in-game cell. That host is R5-08
  (it lands before R5-11, which already carries R5-10, and a pull request holds at most
  two tasks), with only the members R5-08 calls; each later
  caller adds the members it reads, with their `RaceLookTableTests` cells (the UI colors
  with their first reader, R5-09's style source or R4-10; `IsKerbalEvaPartName` with R5-08,
  or with R3W-21 if that lands later).
- **Registers:** RaceCore headless pins (expect none); no lifetimes (constants).
- **Size:** S. **Depends on:** R1-01. **PC:** no. **S3:** S3a (colors), S3b
  (which base suit a replacement uses).
- **Risk:** low.

### R5-08 The rival ghost dresser (RaceWorld)

- **Change:** `Source/Parsek/RaceWorld/RaceLookDresser.cs`, `IGhostPartDresser` for one
  look. Substitute: a kerbal EVA part (any of the eight, or any name `IsKerbalEvaPartName`
  accepts) maps to `EvaPartFor(look, female)` when `PartLoader.getPartInfoByName` finds it
  here, else to `BasePartFor` plus the replacement. Non-kerbal parts: no substitute.
  Materials: only when the replacement is in use; for the source renderers that are the
  prefab's `KerbalEVA.bodyMesh`, `helmetMesh`, `neckringMesh`, return clones with the
  replacement's main and normal textures (property names from S3d). One clone per (look,
  source material), cached on the instance; `DestroyMaterials()` at scene exit. A pure
  `DecideSubstitute(recorded, look, installedNames)` carries the logic. Logs:
  `[RaceLooks] Ghost dresser ready: look=N resolution=stock|replacement part=<name>` once
  per scene per look; `[RaceLooks] Ghost dresser materials destroyed: count=N`.
- **Proof:** xUnit `RaceLookDresserDecideTests` (8 recorded parts x 4 looks x installed or
  not). In-game `RaceLooksFlight` `EvaGhostDressedForEachLook` (FLIGHT): for each look and each
  recorded part name, build a ghost from a synthetic snapshot with the dresser; assert a
  ghost (never the sphere), the substituted part, and the body material's main texture
  name (prefab's for a stock look, Parsek's for a replacement).
- **Uncalled-method check (A1):** live through its in-game cell in the same change (the cell
  constructs the dresser, calls `DestroyMaterials`, and the build reaches `SubstitutePartName`
  and `DressMaterials` through the interface; InGameTests is a root). Its first production
  caller is R5-09. It carries R5-07's members it calls.
- **Registers:** writer gate untouched (no save write); lifetimes none (instance state).
- **Size:** M. **Depends on:** R5-02, R5-07, R5-12, S3. **PC:** yes. **S3:** S3b, S3c, S3d
  (all three set the material path; S3d may show no material work is needed for an
  installed suit).
- **Risk:** medium; texture property names and the prefab's own texture are unknown until
  S3d.

### R5-09 The rival lane wears the looks and colors

- **Change:** in R3's flight composition (`ParsekFlightRivalLane`, R3W-09) and the Tracking
  Station composition (R3W-20): build one `RaceLookDresser` per look in use and hand the
  rival engine an `IGhostBuildOptionsSource` that maps a flight to its player's look (from
  the rival store's cached `player.cfg`); R3W-15's rival style source returns the look's
  color (from `RaceLookTable`, R5-07) and `LabelPrefix = "<display name> - "` (OV-45) in
  place of the neutral placeholder. `RivalMapPresence` (R3W-18) already composes the map name
  through `GhostMapPresence.ComposeGhostVesselName` and sets the orbit color through R5-04's
  helper from its tick (OV-44), so the map and Tracking Station show `Ghost: Ana - Kerbal X`
  and R3W-16's flight label `Ana - Kerbal X` with no change there. `Dispose` calls
  `DestroyMaterials()`. R3W-21's missing-part strip keeps every part
  `RaceLookTable.IsKerbalEvaPartName` accepts (OV-56, 9.3); if R3W-21 landed before R5-07,
  this task adds that kerbal EVA exception to the strip, with R3W-21's kept-part xUnit cell.
  If rivals are drawn at the KSC scene,
  the same source goes to that build call (`ParsekKSC.cs:1607`; 9.6).
- **Proof:** in-game `RaceLooksFlight` `RivalEvaGhostInRivalLook` (FLIGHT, race save with the
  R3 injected rival folder plus one EVA flight recorded in `kerbalEVAVintage`): on a
  machine without Making History, the ghost is a dressed base kerbal and not a sphere; with
  it, a vintage kerbal; the rival's map vessel orbit color equals the look color. Log
  contract: no `ResolveAvailablePart failed` for the kerbal part.
- **Registers:** `CHANGELOG.md` (rival kerbals and rival objects show the rival's look and
  color); todo entry; `docs/dev/design-gui-inventory.md` (rival objects are tinted).
- **Size:** M. **Depends on:** R3W-09, R3W-15, R3W-18, R3W-20, R3W-21, R5-03, R5-04, R5-08.
  **PC:** yes (both machine kinds; the owner's PC has the DLCs, so the no-DLC case is
  forced by a test dresser that reports the part as absent). **S3:** S3c.
- **Risk:** medium; the first time a rival kerbal is drawn, with two slices' code meeting.

### R5-10 The dress rules (RaceCore, pure)

- **Change:** `Source/Parsek/RaceCore/RaceLookDress.cs`. Plain values: `KerbalDress`
  (suit style, suit texture path, normal texture path, sprite path, combo id),
  `KerbalDressRow` (name, type, gender, dress), `DressMarker` (look, resolution). Rules:
  `DressFor(look, female, resolution, stockDefaultCombo)`; `IsStockDefault(dress)`;
  `Wears(dress, lookDress)`; `DecideOnAdded(type)` (Crew: dress; Applicant, Tourist,
  Unowned: no); `DecideOnTypeChange(from, to, dress)` (to Crew and wearing the stock
  default: dress; anything else: keep, so a player's pick and Parsek's re-hire after a
  rewind keep their suit); `PlanLoadPass(marker, currentLook, currentResolution, rows)`:
  marker missing (new race save): dress every Crew kerbal wearing the stock default;
  marker look or resolution differs: re-dress every Crew kerbal wearing the marker's dress;
  equal: nothing. Returns the rows to write and the new marker.
- **Proof:** xUnit `RaceLookDressTests` truth tables: starting crew dressed once; a picked
  suit kept across a look change; tourists and applicants skipped; look 0 to 2 re-dresses
  only old-look wearers; a resolution change (replacement to stock, after the DLC is
  installed) re-dresses; identical marker writes nothing.
- **Uncalled-method check (A1):** lands with its first caller. Only xUnit calls these rules
  in their own change, so `RaceLookDress.cs` is created in R5-11's pull request (its first
  production caller), as specified here, with the truth-table cells.
- **Registers:** RaceCore headless pins (expect none).
- **Size:** S. **Depends on:** R5-07, S3. **PC:** no. **S3:** S3a (the stock default a fresh
  kerbal wears, the default combo ids), S3c (whether a DLC dress is still recognisable after
  the stock fallback, through `comboId`).
- **Risk:** low; all decisions are table-tested.

### R5-11 `RaceLooks`: own roster dressing

- **Change:** `Source/Parsek/RaceWorld/RaceLooks.cs`, an instance owned by R1-08's
  `RaceSession` (constructed only in a race save). `Start()` subscribes to
  `GameEvents.onKerbalAdded` and `onKerbalTypeChange`; `Stop()` unsubscribes (session drop,
  main menu). Each hook maps the stock kerbal to a `KerbalDressRow`, asks R5-10 and writes
  the stock fields the picker writes (`suit`, `SuitTexturePath`, `NormalTexturePath`,
  `SpritePath`, `ComboId`, `UseStockTexture`), nothing else. `RunLoadPass()` is called by
  `ParsekRaceScenario.OnLoad` (R1-03) once the roster exists; it runs `PlanLoadPass` and
  writes. The starting crew is dressed by this first load pass (marker missing), not by a
  before-start step of R1-10 (OV-54). The marker is a `RACE_LOOK_DRESS { look = N
  resolution = stock|replacement }` child of the race scenario node, added through R1-03's
  child hook (the scenario keeps unknown children verbatim; OV-28): a handler implementing
  `IRaceScenarioChild` (child node name `RACE_LOOK_DRESS`, `Load(ConfigNode)`,
  `Save(ConfigNode)`) added to `ParsekRaceScenario`'s handler list (9.5). Logs:
  `[RaceLooks] Dress pass: look=N resolution=R reason=
  new-save|look-changed|resolution-changed|none dressed=N kept=N skipped=N` (Info, once per
  load); `[RaceLooks] Dressed <name> look=N on=added|hired|rescued` (Verbose, bounded by the
  hires of a session).
- **Proof:** xUnit through R5-10. In-game `RaceLooks` (SPACECENTER; each cell skips with a
  stated reason through `RaceGate` unless the save is a race save; hosted by T-29's RACE-9,
  which stages a race): `NewRaceSaveStartingCrewDressed`; `HiredApplicantDressed`;
  `PickedSuitSurvivesReload` (set a suit through the picker's fields, save, reload, still
  there); `LookChangeRedressesOnce` (edit the look in `local.cfg`, reload, old-look wearers
  re-dressed, a picked suit kept, a second reload writes nothing). Log contract in a
  single-player save: no `[RaceLooks]` line at all.
- **Uncalled-method check (A1):** reached by production: `RaceSession` constructs it, the
  `GameEvents` handlers are delegate members, `ParsekRaceScenario.OnLoad` calls
  `RunLoadPass` and the scenario calls the child handler. R5-10's rules, and R5-07's members
  they read, land in this pull request.
- **Registers:** R1-06's `race-no-writer` allowlist: the reserved `RaceLooks` entry names
  `RaceLooks.cs` for the stock roster suit fields (15.4); `LoadReconcilePolicy` gets the
  `RACE_LOOK_DRESS` child's row in this task (OV-28: "from the loaded save" for every load
  kind, like `RaceLink`'s row from R1-05); `docs/dev/design-competitive-multiplayer.md` 15.7
  gains the marker row; `CHANGELOG.md` (kerbals in a race save wear the player's look); todo
  entry; lane catalog (new category `RaceLooks`); RACE-9's pinned tally re-pinned in the same
  change if T-29 has already pinned `RaceLooks`.
- **Size:** M. **Depends on:** R1-03, R1-08, R1-09, R5-10, R5-12, S3 (not R1-10 or R1-11,
  task index E.3.7). **PC:** yes. **S3:** S3a, S3b (the replacement dress writes texture
  paths; if S3b shows IVA or the portrait ignore them, the dress also writes a combo id and
  needs question 10.1 answered (a)), S3c.
- **Risk:** medium; a wrong predicate overwrites a suit the player chose, which the
  `PickedSuitSurvivesReload` test and the truth tables guard.

### R5-12 The replacement suit sets

Shape depends on question 10.2 (art source) and 10.1 (combo entry or not).

- **Change, shipped art:** `GameData/Parsek/Suits/` with, per set and gender, a suit
  texture, a normal map and a portrait sprite, plus one config. With 10.1 (b) the config is
  a Parsek node (`PARSEK_SUIT_SET`) read by `RaceLooks` and the dresser through
  `GameDatabase.GetConfigNodes`; with 10.1 (a) it is a `SUITCOMBOS` node. Packaging in the
  same change: `scripts/release.py:82-88` lists the files (or walks `GameData/Parsek/Suits`),
  `Source/Parsek/Parsek.csproj:112-117` copies the folder on deploy,
  `harness/provision/provlib.py:257` adds it to the aux payload, with a cell in
  `harness/provision` tests. A small `RaceWorld/RaceSuitSets.cs` resolves a set id to the
  texture URLs and logs `[RaceLooks] Suit set missing: <url>` once if a file is absent.
- **Change, runtime recolor:** no files. `RaceWorld/RaceSuitSets.cs` builds the two
  replacement textures once per process from the base suit's own textures (GPU blit through
  a recolor material into a `RenderTexture`, copied to a `Texture2D`) and registers them in
  `GameDatabase` under Parsek URLs so `KerbalEVA.SetNewTextures` finds them. Lifetime
  `process` in `scripts/arch/lifetimes.toml`.
- **Proof:** in-game `RaceLooks` (SPACECENTER) `ReplacementSetsResolve` (each URL resolves
  to a texture of the base suit's size). Shipped art: a release-bundle check that the zip
  carries the folder (`python scripts/release.py` on the owner's PC) and the provision unit
  cell.
- **Uncalled-method check (A1):** `RaceSuitSets` is live through `ReplacementSetsResolve`
  (an in-game cell, a root) in the same change; its production callers are R5-08 and R5-11.
- **Registers:** packaging files above; `CHANGELOG.md` only through R5-09 / R5-11.
- **Size:** M (either shape). **Depends on:** S3b, questions 10.1 and 10.2. **PC:** yes.
  **S3:** S3b decides the shape; S3d the property names.
- **Risk:** medium; a packaging miss ships a zip whose replacement shows the base suit,
  which `ReplacementSetsResolve` catches on a zip install.

### R5-13 A harness lane pins the `RaceLooks` category

Dropped: replaced by T-29 (OV-06).

## 5. Parallel tracks

| Track | Tasks | Files |
|-------|-------|-------|
| Ghost build seam (day one) | R5-01 -> R5-02 | `GhostVisualBuilder.cs`, `GhostVisualBuilder.PartInfoBuilders.cs`, new `GhostBuildOptions.cs` |
| Engine and style (after P2c-04, P2a-02) | R5-03 | `GhostPlaybackEngine.cs`, `GhostPlaybackEngine.GhostVisuals.cs`, new `PlaybackTrajectoryStyle.cs`, `modules.toml` |
| Map vessel style (after P3-04; helper lands in R3W-18) | R5-04 | `GhostMapPresence.cs` (name composer beside the prefix), `GhostMapVessels.cs` (orbit color helper) |
| Conditional overlays | R5-05, R5-06 (with P3b, R3W-26) | `MapMarkerRenderer.cs`, `LegDraw.cs`, `WatchModeController.cs` |
| Race rules (after R1-01; merged with their first caller, R5-08 or R5-11) | R5-07 -> R5-10 | `RaceCore/RaceLookTable.cs`, `RaceCore/RaceLookDress.cs` |
| Content (after S3) | R5-12 | `GameData/Parsek/Suits/`, `release.py`, `Parsek.csproj`, `provlib.py` |
| Race runtime | R5-08, then R5-11 (after R1-03, R1-08, R1-09) and R5-09 | `RaceWorld/RaceLookDresser.cs`, `RaceWorld/RaceLooks.cs`, R3W-09's `ParsekFlightRivalLane.cs`, R3W-20's `ParsekTrackingStationRivalLane.cs`, R3W-15's rival style source |

- R5-01, R5-07 (once R1-01 is in; merged with its first caller) and R5-12's art work can
  run at the same time. R5-03 can be built beside R5-02 once R5-01 is in (different files),
  but lands after P2c-04 and P2a-02.
- **Hot files.** `GhostVisualBuilder.cs` (R5-01, R5-02; also any ghost-visual fix in
  flight); `GhostPlaybackEngine.cs` constructor, edited in the order P2c-02, P2c-04, P2a-02,
  R5-03, R3W-09, R3W-14 (ruling A3; R3W-14 updates both construction sites): R5-03 lands
  after P2a-02 and before R3W-15 (OV-43), or folds the constructor arguments into one
  options object; `GhostMapVessels.cs` in the order P3-02 to P3-05, R3W-17, R5-04, R3W-19 (OV-48);
  `modules.toml` (R5-03, R3W-14, R1-01: a rule each, rebase-trivial);
  R3W-09's `ParsekFlightRivalLane.cs` (R3W-09 onward, R5-09: R5-09 after R3W-18);
  `ParsekRaceScenario.cs` (R1-03, R1-08, R3E-15, R5-11) and `LoadReconcilePolicy.cs` (R1-05,
  R1-16, R5-11).

## 6. Tests and proof

- **xUnit (no PC):** `GhostBuildOptionsTests` (R5-01), `GhostBuildDresserSourceGateTests`
  (R5-02), `PlaybackTrajectoryStyleTests` (R5-03), `ComposeGhostVesselName` (R5-04) and
  `NeedsOrbitColor` (specified by R5-04, landing with R3W-18),
  `MapMarkerStyleTests` / `LegLineStyleTests` (R5-05), `RaceLookTableTests` (R5-07, with
  its first caller), `RaceLookDresserDecideTests` (R5-08), `RaceLookDressTests` (R5-10, in
  R5-11's pull request). Each null-input cell
  proves the single-player path is today's. Log assertions through
  `ParsekLog.TestSinkForTesting`, `[Collection("Sequential")]`.
- **In-game, two categories, one scene each (OV-09; owner's PC):** `RaceLooksFlight`
  (FLIGHT): R5-01, R5-02 and R5-04 cells run in any save (they build with test dressers and
  styles, no race), R5-08 any save, R5-09 skips with a stated reason unless the save is a
  race save with the rival fixture. `RaceLooks` (SPACECENTER): R5-11 cells skip unless the
  save is a race save; R5-12's `ReplacementSetsResolve` runs in any save.
- **Harness:** R5 adds no lane (R5-13 dropped, OV-06). T-29's host lanes run the two
  categories: RACE-9 (SPACECENTER) runs `RaceLooks`, RACE-10 (FLIGHT) runs
  `RaceLooksFlight`. The existing lanes, with no race save loaded, must stay green and
  unchanged (H15 `GhostVisuals`, FGV-1's `Ghost built:` regex, the map lanes behind P3).
- **What needs the owner's PC:** every in-game cell, spike S3, the R5-09 check on a
  no-DLC machine (or a forced absent part), and `release.py` for the zip.

## 7. Gates and registrations

- **Module map:** new `GhostBuildOptions.cs` lands in Ghost by the existing prefix rule;
  `PlaybackTrajectoryStyle.cs` needs a Core rule placed before the final Core rule; RaceCore
  and RaceWorld files by R1-01's folder rules.
- **Layers and forbidden edges:** RaceWorld -> Ghost is lateral (features) and allowed;
  RaceWorld names no Parsek `Crew` type (`RaceLooks` uses only stock roster types);
  Ghost, Display and MapRender name no race type (the seams are neutral). A rival marker
  draw, if any, sits in the entry layer (9.4).
- **Headless:** RaceCore files expect zero leaks; `PlaybackTrajectoryStyle` uses only
  `Color` (inert).
- **Code facts ratchets:** the "methods the roots do not reach" count does not rise (ruling
  A1; each task's "Uncalled-method check" line). R5-01, R5-02, R5-03, R5-05 and R5-06 are
  optional parameters on existing methods reached by today's callers with a neutral null;
  R5-08 and R5-12 are live through their in-game cells; R5-07 and R5-10 merge with their
  first caller (R5-08 or R5-11); R5-04's orbit color helper lands in R3W-18's pull request.
  `ResolveBuildPartName` keeps
  `AdvanceTimelineGhostBuild` out of the complexity watch band.
- **Lifetimes:** no new statics planned. The dresser cache and `RaceLooks` are instances;
  the runtime recolor variant of R5-12 is one `process` static, tagged.
- **Grep gates:** the `RaceLooks.cs` entry in R1-06's `race-no-writer` allowlist (R5-11); no
  ERS / ELS read; no thread.
- **Save format:** no bump. The marker is an additive child node of the race scenario node;
  single-player saves never carry it. Stock roster fields are stock's format.
- **Load policy:** one `LoadReconcilePolicy` row for the marker, added by R5-11 with the
  child it adds through R1-03's `IRaceScenarioChild` hook (OV-28).
- **GUI:** no new surface. `design-gui-inventory.md` notes the tint on rival objects (R5-09)
  and on markers if R5-05 lands. Stock picker untouched unless question 10.1 is (a).
- **Docs:** CHANGELOG at R5-09 and R5-11; todo entries per task; lane catalog regen with the
  first in-game cell of each category (R5-01 for `RaceLooksFlight`, R5-11 for `RaceLooks`);
  once T-29 pins RACE-9 and RACE-10, a cell added to either category re-pins its host's
  tally in the same change (`CommittedBatchTallySourceSyncTests`); `autotest-status.md` is
  T-29's; `scripts/arch/log_tags.json` refresh (report-only) when the `RaceLooks` tag first
  ships.

## 8. Risks

- **A player's chosen suit is overwritten.** Guarded by the "stock default only" rule on
  joins, the marker rule on loads, the truth tables and `PickedSuitSurvivesReload`.
- **A cached look material is destroyed by the engine, or leaks.** The source gate in R5-02
  and the in-game check that the dresser's material survives a ghost's destruction; the
  dresser logs the count it destroys at scene exit.
- **Single player changes.** Every seam defaults to null; the existing suites, H15 and FGV-1
  are the witness; `RaceLooks` is never constructed outside a race save.
- **Stock resets the orbit color.** `RivalMapPresence`'s tick (R3W-18) re-sets it through
  R5-04's helper; the in-game test watches several frames and a map toggle.
- **The prefab material does not show the suit (S3d).** Then an installed look needs the
  material override too, using the stock default combo texture; R5-08 grows, the seam does
  not change.
- **A save moved to a machine without the DLC.** Stock resets the suit to Default at load;
  R5-10 re-dresses through the resolution in the marker only if S3c shows the dress is
  still recognisable (combo id). Otherwise those kerbals show the base suit until the
  player picks one; the color still marks the player.
- **Two simultaneous joiners share a look** until the later one's game applies the clash
  rule and exports; their ghosts share a color for that time. R1's rule ends it; nothing
  here can.
- **Packaging.** A missing `Suits/` folder in the zip, caught by `ReplacementSetsResolve` on
  an installed zip.

## 9. Corrections and gaps

1. **The sphere is per vessel, not per kerbal.** Design 7.4 and the analysis are right for
   an EVA kerbal (a one-part vessel: `GhostVisualBuilder.cs:346-351`, then
   `GhostPlaybackEngine.GhostVisuals.cs:163-167`). A kerbal in an external seat on a larger
   vessel just drops out. The substitute covers both.
2. **R3W-15 plans the same A5 style seam.** This plan builds the type and the sites
   (R5-03 to R5-06) as base-Parsek seams that need no rival lane, with R3W-15's type names.
   Assumed: R3W-15 shrinks to the rival style source (player to color and prefix); flight
   labels stay R3W-16. One of the two must be dropped when the plans are merged.
   Settled: R5-03 owns the type and the engine arguments, R5-04 and R5-05 the sites, and
   R3W-15 shrinks to the rival style source; R5-03 lands before R3W-15 (OV-43).
3. **R3W-21 would strip a DLC suit's kerbal part before the dresser sees it.** It removes
   every part with no `PartLoader` entry, so a rival EVA in `kerbalEVAVintage` on a machine
   without Making History becomes `unplayable:no-parts`. Assumed: the strip keeps names
   `RaceLookTable.IsKerbalEvaPartName` accepts (R5-09), and R3W-21 should say so.
   Settled: R3W-21 keeps those names; if it lands first, R5-09 adds the exception (OV-56).
4. **Colors on markers need the entry layer.** `MapMarkerRenderer` is in Controllers
   (`modules.toml:102`), so `RaceWorld` cannot call it without a backward pair. Any rival
   marker is drawn by a scene controller with a style RaceWorld hands it.
5. **No slot for the "dressed with look N" marker.** 15.7 keeps `RaceLink` to never-changing
   facts and `RaceLocal` outside the save. The marker must roll back with the roster on
   every load, so it belongs in the save. Assumed: a `RACE_LOOK_DRESS` child of the race
   scenario node, save lifetime, one load-policy row.
   Settled: R1-03 adds the `IRaceScenarioChild` hook and keeps unknown children verbatim;
   R5-11 adds the child through it with its LoadReconcilePolicy row (OV-28).
6. **The starting crew cannot be caught by a hook.** `onKerbalAdded` fires for it in
   `GenerateInitialCrewRoster` before any scenario exists. Assumed: the first load pass of
   a race save dresses it (marker missing).
   Settled: this reading stands; R1's "before-start step" waiter row is fixed, and R5-11
   waits for R1-03, R1-08, R1-09 only (OV-54, task index E.3.7).
7. **`SUITCOMBOS` is not inert.** A `SUITCOMBOS` node under `GameData` is listed by the stock
   suit picker in every save, single player included, which 15.8 forbids in spirit.
   Question 10.1.
8. **The DLC fallback happens before Parsek runs.** The stock load constructor resets an
   uninstalled suit to Default, so "a machine that lacks the DLC uses the replacement on the
   player's own kerbals" needs a re-dress the design does not mention (R5-10, S3c).
9. **The stock suit enum is not inert.** RaceCore uses its own `RaceSuitStyle`
   (`facts.toml:18-28`).
10. **Packaging is not mentioned.** 15.12 names `GameData/Parsek/Suits/`, but the zip, the
    dev deploy and the harness deploy carry explicit lists (`release.py:82-88`,
    `Parsek.csproj:112-117`, `provlib.py:257`); all three change in R5-12.
11. **Rivals at the KSC scene.** `ParsekKSC.cs:1607` builds ghosts through its own call. If
    R3 shows rival ghosts there, it needs the same options source. The design is silent.
12. **"At their next sync" (8.29) is really "at the next scene after the sync".** The rival
    list is replaced only at scene load (7.6), so a rival's new look and color appear then.
13. **A focused rival map vessel shows stock lime**, not the rival's color, like any focused
    vessel (`OrbitRenderer` focus color). Accepted as stock behavior.

## 10. Questions for the owner

1. **Should the two replacement suit sets appear in the stock suit picker?** (a) Yes, as
   stock `SUITCOMBOS`: every save, single player too, lists two extra suits in the
   Astronaut Complex picker, and a race player can pick the replacement back after trying
   another suit. (b) No: Parsek applies them only to race kerbals and rival ghosts, and
   single-player saves see nothing new. Recommendation: (b), if S3b shows the texture-path
   dress shows in EVA, IVA and the portrait; else (a).
   Note: (a) puts a race item into every single-player save, which design section 0 (and
   15.8) rule out unless the owner allows it; (a) is therefore open only with the owner's
   explicit permission, not as a fallback the plan can take by itself.
2. **Where does the replacement art come from?** Squad's DLC textures cannot be shipped.
   (a) Original stock-like art drawn for Parsek (needs an artist; ships files). (b) Parsek
   recolors the player's own base suit at load, in memory (nothing shipped, no licence
   question; it can look less like the real suits). Recommendation: (b) tried first in
   S3b; (a) only if it looks wrong.
3. **What should a rival ghost's name say on the map and in the Tracking Station?**
   (a) `Ghost: Ana - Kerbal X` (the display name as prefix). (b) `Ghost: Kerbal X`, color
   only. Recommendation: (a): colors are hard to tell apart for some players, and the
   Tracking Station list has no color.
   Assumed until the owner says otherwise: (a), as a designer choice merged with r3w's label
   question (OV-45, OV-58): one prefix format `LabelPrefix = "<display name> - "`; map and
   Tracking Station name `Ghost: Ana - Kerbal X`; floating label in flight `Ana - Kerbal X`.
