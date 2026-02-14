# Synthetic Recording Generator

A reusable system for generating synthetic recordings and injecting them into KSP save files. Enables testing ghost playback, map view markers, orbit segments, vessel spawning, and crew reservation without manually flying missions.

## Quick Start

```bash
pwsh -File scripts/inject-recordings.ps1 --clean-start
```

This injects synthetic recordings into `Kerbal Space Program/saves/test career/1.sfs` and `persistent.sfs`.
`--clean-start` removes top-level world `VESSEL` blocks and stale `spawnedPid` values before injection so replay starts from a clean baseline.
If the target save is missing, it is created from `persistent.sfs`.
The operation is idempotent: existing `ParsekScenario` is replaced.

Optional script flags:

```bash
# Inject into a different save slot without cleaning
pwsh -File scripts/inject-recordings.ps1 -TargetSave 3.sfs

# Different save game folder
pwsh -File scripts/inject-recordings.ps1 -SaveName "career hard" --clean-start

# Force a rebuild (use when code changed and KSP is closed)
pwsh -File scripts/inject-recordings.ps1 --clean-start --build
```

By default the script runs `dotnet test --no-build` to avoid plugin DLL copy-lock issues while KSP is running.

`InjectAllRecordings` is tagged `[Trait("Category", "Manual")]` since it mutates a real save file. It silently skips if the save file doesn't exist (CI-safe). To exclude it from automated runs:

```bash
dotnet test --filter "Category!=Manual"
```

Advanced: the manual injector test also respects environment variables:
- `PARSEK_INJECT_SAVE_NAME`
- `PARSEK_INJECT_TARGET_SAVE`
- `PARSEK_INJECT_CLEAN_START` (`1`/`true`)

## In-Game Walkthrough

After injecting, launch KSP and load **test career**. The save UT is set to 17000 (KSC mid-morning, good lighting) during clean-start, and all recordings are scheduled starting 30s after that. Enter any flight and time warp forward to see ghosts appear.

All crewed recordings use the **FleaRocket** craft (mk1pod.v2 + solidBooster.sm.v2 + parachuteSingle) unless noted otherwise.

### 1. Pad Walk (+30 to +60) — Ghost EVA

Jeb walks ~200m east from the launchpad at ground level. Ghost-only (EVA type, no vessel spawn). Tests EVA ghost rendering.

### 2. KSC Hopper (+60 to +116) — Ghost Ship

Val's FleaRocket hops to ~500m, drifts east ~600m, then descends under parachute. Ghost-only (no vessel spawn). Part events: SRB decouple + parachute deploy.

### 3. Flea Flight (+90 to +180) — Vessel Spawn

Bob's FleaRocket launches to 620m apex, deploys parachute, lands ~2km east of the pad. Based on real flight data with resource deltas (funds +5760, rep +1.2). **Vessel spawns** at EndUT. Part events: SRB decouple + parachute deploy.

### 4. Suborbital Arc (+120 to +420) — Ghost Ship

Bill's FleaRocket follows a gravity turn east, reaching ~71km apex, then descends under parachute. Ghost-only (no vessel spawn). Uses explicit per-point rotation (gravity turn pitch-over). Part event: SRB decouple.

### 5. KSC Pad Destroyed (+150 to +162) — Ghost Sphere

Vessel destroyed near KSC. No snapshot — exercises the destroyed/no-snapshot fallback path. Ghost sphere appears briefly.

### 6. Orbit-1 (+180 to +3180) — Vessel Spawn (orbit)

Bill's crewed vessel (pod+tank+engine) ascends to orbit. At +680, the ghost transitions to the **orbital segment** — position computed analytically from Keplerian parameters. **Vessel spawns** in orbit at EndUT. Bill is marked Assigned in Astronaut Complex until spawn.

### 7. Close Spawn Conflict (+210 to +222) — Vessel Spawn (landed)

Jeb's FleaRocket landed very near KSC. **Vessel spawns** at EndUT, offset ~250m from nearest vessel to prevent physics collisions.

### 8. Island Probe (+240 to +420) — Vessel Spawn (landed)

Val's FleaRocket flies southeast to the island airfield, cruising at ~1000m. **Vessel spawns** landed at the island (lat=-1.52, lon=-71.97).

### Crew Assignment

- **Ghost-only** recordings (PadWalk, KSC Hopper, Suborbital Arc) reuse stock kerbal names safely — `WithGhostVisualSnapshot` triggers no crew reservation.
- **Vessel-spawn** recordings each use a unique stock kerbal: Bob (Flea Flight), Bill (Orbit-1), Jeb (Close Spawn), Val (Island Probe).

### Timeline of Events

```
UT base+30    ─── Pad Walk ghost starts (EVA) ──────────
UT base+60    ─── Pad Walk ghost ends / KSC Hopper starts
UT base+90    ─── Flea Flight ghost starts ─────────────
UT base+116   ─── KSC Hopper ghost ends ────────────────
UT base+120   ─── Suborbital Arc ghost starts ──────────
UT base+150   ─── KSC Pad Destroyed ghost starts ───────
UT base+162   ─── KSC Pad Destroyed ghost ends ─────────
UT base+180   ─── Flea Flight vessel spawns (Bob) / Orbit-1 starts
UT base+210   ─── Close Spawn Conflict ghost starts ────
UT base+222   ─── Close Spawn Conflict spawns (Jeb) ────
UT base+240   ─── Island Probe ghost starts ────────────
UT base+420   ─── Suborbital Arc ends / Island Probe spawns (Val)
UT base+680   ─── Orbit-1 enters orbital segment ───────
UT base+3180  ─── Orbit-1 vessel spawns (Bill) ─────────
```

## Architecture

Three builder classes in `Source/Parsek.Tests/Generators/`:

### RecordingBuilder

Fluent API that produces a `ConfigNode("RECORDING")` matching `ParsekScenario.OnSave()` format exactly. Supports both v2 (inline) and v3 (external files) formats.

```csharp
var rec = new RecordingBuilder("My Recording")
    .WithDefaultRotation(0.33f, -0.63f, -0.63f, -0.33f)  // upright at KSC
    .AddPoint(127000, -0.0972, -74.5575, 77)
    .AddPoint(127010, -0.0972, -74.5575, 500)
    .AddOrbitSegment(129500, 132000, sma: 700000, ecc: 0.001, inc: 28.5)
    .AddPartEvent(127005, 42, (int)PartEventType.Decoupled, "fuelTank")
    .WithParentRecordingId("abc123")        // EVA child linkage
    .WithEvaCrewName("Jebediah Kerman")     // EVA child linkage
    .WithVesselSnapshot(vesselBuilder)
    .Build();   // returns ConfigNode (v2 inline format)

// For v3 external files:
rec.WithRecordingId("my-id");
rec.BuildV3Metadata();       // metadata-only RECORDING node for .sfs
rec.BuildTrajectoryNode();   // PARSEK_RECORDING node for .prec file
rec.GetVesselSnapshot();     // vessel snapshot ConfigNode
rec.GetGhostVisualSnapshot(); // ghost snapshot ConfigNode
```

**WithDefaultRotation(x, y, z, w)** — sets a default rotation quaternion applied to all subsequent `AddPoint` calls that don't specify an explicit rotation. Use this for surface-aligned recordings (e.g. KSC upright ≈ 0.33, -0.63, -0.63, -0.33). Points with explicit non-identity rotation are unaffected.

**AddPoint** parameters: `(ut, lat, lon, alt, body, rotX/Y/Z/W, funds, science, rep)` — all optional after alt, defaults to Kerbin with identity rotation (or default rotation if set) and zero resources.

**AddOrbitSegment** parameters: `(startUT, endUT, inc, ecc, sma, lan, argPe, mna, epoch, body)` — Keplerian elements matching OrbitSegment struct.

**AddPartEvent** parameters: `(ut, pid, type, partName)` — records a part event at the given UT. Type is a `PartEventType` enum cast to int (Decoupled=0, Destroyed=1, ParachuteDeployed=2, ParachuteCut=3).

**WithParentRecordingId / WithEvaCrewName** — link a child recording to its parent (used for EVA child recordings). The parent recording ID is the `RecordingId` of the parent recording.

All numeric values are serialized with `CultureInfo.InvariantCulture` for locale safety.

### VesselSnapshotBuilder

Builds a minimal `ConfigNode("VESSEL")` that KSP's ProtoVessel can load.

```csharp
// Standard 3-part rocket (mk1pod.v2 + solidBooster.sm.v2 + parachuteSingle)
VesselSnapshotBuilder.FleaRocket("Flea Flight", "Bob Kerman", pid: 22222222)
    .AsLanded(-0.0972, -74.5575, 77)
    .Build();

// Crewed vessel in orbit
VesselSnapshotBuilder.CrewedShip("Orbit-1", "Bill Kerman", pid: 12345678)
    .AsOrbiting(sma: 700000, ecc: 0.001, inc: 28.5)
    .Build();

// Unmanned probe landed
VesselSnapshotBuilder.ProbeShip("Island Probe", pid: 87654321)
    .AsLanded(-1.52, -71.97, 40)
    .Build();
```

Static factories: `FleaRocket` (3-part crewed rocket with real KSP part positions and attachment nodes), `CrewedShip` (single pod with crew), `ProbeShip` (single probe core). Use `.AddPart(name, crew, position, parentIndex)` to add more parts — `position` is a Y-up vessel-local coordinate string (e.g. `"0,-1.163,0"`).

The VESSEL `pid` field is deterministically derived from `persistentId` (hex-encoded, zero-padded to 32 chars). This means repeated builds with the same persistentId produce identical output, making the injection content-stable for diffing and debugging.

Required VESSEL sub-nodes (ORBIT, PART, ACTIONGROUPS, DISCOVERY, FLIGHTPLAN, CTRLSTATE) are generated automatically.

### ScenarioWriter

Assembles recordings into a `SCENARIO` node and injects into `.sfs` save files.

```csharp
var writer = new ScenarioWriter();
writer.WithV3Format();  // enables external sidecar file writing
writer.AddRecording(rec1);
writer.AddRecording(rec2);
writer.AddCrewReplacement("Jeb Kerman", "Bob Kerman");  // optional
writer.InjectIntoSaveFile("input.sfs", "output.sfs");
// v3: also writes .prec + .craft sidecar files alongside the save
```

**Injection algorithm:**
1. Remove any existing `ParsekScenario` SCENARIO block using brace-counting (finds the SCENARIO line, checks for `name = ParsekScenario` inside, then tracks brace depth to find the matching close brace — robust against varying whitespace and nested content)
2. Find the `FLIGHTSTATE` line
3. Insert the new SCENARIO block before it with correct tab indentation

This makes the operation idempotent — safe to run repeatedly.

## Adding New Test Recordings

To add a new recording, create a builder method in `SyntheticRecordingTests.cs`:

```csharp
internal static RecordingBuilder MyNewRecording(double baseUT = 0)
{
    double t = baseUT + 300;  // offset from save UT (30s apart from other recordings)
    var b = new RecordingBuilder("My New Recording");
    b.WithDefaultRotation(KscRotX, KscRotY, KscRotZ, KscRotW);  // upright at KSC
    b.AddPoint(t,    -0.0972, -74.5575, 77);
    b.AddPoint(t+60, -0.0972, -74.5575, 500);
    // Optionally add orbit segments, part events, and vessel/ghost snapshots
    return b;
}
```

Then add it to `InjectAllRecordings()`:

```csharp
writer.AddRecording(MyNewRecording());
```

### Coordinate Reference

| Location | Latitude | Longitude | Alt (m) |
|----------|----------|-----------|---------|
| KSC Launchpad | -0.0972 | -74.5575 | 77 |
| KSC Runway | -0.0486 | -74.7244 | 69 |
| Island Airfield | -1.52 | -71.97 | 40 |
| Mun surface | 0 | 0 | 0 |

### Persistent ID Guidelines

Use unique `pid` values for vessel snapshots to avoid collision with existing vessels in the save. The save's existing vessels have pids in the range of ~200M-4B. Use small values (1M-100M) for test recordings.

## Tests

```bash
# Run all synthetic recording tests (excluding Manual)
dotnet test --filter "FullyQualifiedName~SyntheticRecordingTests&Category!=Manual"

# Run only the save file injection (Manual test)
dotnet test --filter InjectAllRecordings

# Run full test suite (includes all synthetic tests + Manual)
dotnet test
```

### Test Coverage

| Test | What it verifies |
|------|-----------------|
| PadWalk_HasEvaGhostSnapshot | EVA-type GhostVisualSnapshot, no VesselSnapshot |
| KscHopper_BuildsValidRecording | 15 points, GhostVisualSnapshot with FleaRocket parts, part events |
| FleaFlight_HasVesselSnapshotAndPartEvents | VesselSnapshot (Ship), FleaRocket parts, part events, resource delta |
| SuborbitalArc_BuildsValidRecording | 25 points, GhostVisualSnapshot, ascending UT order, SRB decouple event |
| KscPadDestroyed_HasNoSnapshot | No VesselSnapshot or GhostVisualSnapshot (destroyed vessel) |
| Orbit1_HasOrbitSegmentAndSnapshot | Orbit segment present, vessel snapshot with Bill Kerman |
| CloseSpawnConflict_HasLandedSnapshotNearKsc | FleaRocket with Jeb, landed near KSC |
| IslandProbe_HasFleaRocketWithCrew | FleaRocket with Val, landed at island airfield |
| ScenarioWriter_SerializesCorrectly | ConfigNode to text serialization |
| ScenarioWriter_InjectIntoSave_InsertsBeforeFlightstate | Correct insertion point |
| ScenarioWriter_InjectIntoSave_ReplacesExistingParsekScenario | Idempotent replacement |
| ScenarioWriter_InjectIntoSave_Idempotent | Double-injection produces single block |
| ScenarioWriter_InjectIntoSave_HandlesVariousWhitespace | CRLF, nested nodes, extra values |
| VesselSnapshotBuilder_DeterministicPid | Same pid across builds with same persistentId |
| InjectAllRecordings | End-to-end injection of 8 recordings into real save file (Manual) |
