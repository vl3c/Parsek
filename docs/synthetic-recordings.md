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

After injecting, launch KSP and load **test career**. The save UT is read dynamically from `1.sfs`, so all recordings are scheduled in the near future. Here's what to expect as you time warp forward:

### 1. KSC Hopper (UT 127000-127056)

1. Go to any flight (launch a vessel or switch to an existing one)
2. Open Map View (M) or stay in flight view
3. Time warp forward — warp auto-stops at recording start (vessel snapshot present)
4. At UT ~127000, a semi-transparent ghost vessel appears near the launchpad
5. The ghost rises to ~500m, drifts east ~1km, then descends near the VAB
6. Ghost disappears at UT 127056
7. At EndUT, a probe vessel named **KSC Hopper** spawns at the landing point

### 2. Suborbital Arc (UT 128000-128300)

1. Continue time warping past 127056
2. At UT ~128000, a semi-transparent ghost appears at the launchpad
3. Switch to **Map View** (M) to see the trajectory climb
4. The ghost follows a gravity turn east, reaching ~71km apex
5. It descends and splashes down at UT 128300
6. At EndUT, a probe vessel named **Suborbital Arc** spawns at the splashdown point

### 3. Orbit-1 (UT 129000-132000)

1. Time warp forward — warp **auto-stops** at UT ~129000 (this recording has a vessel snapshot with Bill Kerman)
2. A ghost appears at the launchpad and follows an ascent trajectory
3. At UT ~129500, the ghost transitions to the **orbital segment** — position is computed analytically from Keplerian parameters, visible as a smooth orbit in Map View
4. The ghost orbits at ~100km (sma=700000m) with 28.5 deg inclination
5. At UT ~132000 (EndUT), the ghost despawns and a **real vessel spawns** in orbit
6. Switch to **Tracking Station** to verify "Orbit-1" appears as an orbiting vessel with Bill Kerman aboard
7. Bill Kerman should be marked as Assigned in the Astronaut Complex until spawn

### 4. Island Probe (UT 133000-133180)

1. Time warp forward — warp **auto-stops** at UT ~133000
2. A ghost appears at the launchpad and flies southeast toward the island airfield
3. The ghost cruises at ~1000m altitude, then descends
4. At UT ~133180 (EndUT), the ghost despawns and a **probe vessel spawns** landed at the island airfield (lat=-1.52, lon=-71.97)
5. Try switching to it with `[`/`]` or from the Tracking Station
6. No crew involved (unmanned probe)

### 5. Tedorf EVA Switch (UT +8 to +30)

Short recording that tests vessel-switch/EVA edge cases. Crewed vessel with Tedorf Kerman, landed trajectory near KSC. Ghost-only (no vessel spawn).

### 6. KSC Pad Destroyed (UT +900 to +912)

Edge case: vessel destroyed near KSC. No vessel snapshot — exercises the destroyed/no-snapshot fallback path. Ghost sphere appears briefly (no vessel geometry available).

### 7. EVA Walk Test (UT +980 to +992)

EVA-type vessel snapshot with kerbal part model. Tests ghost rendering for EVA kerbals.

### 8. Close Spawn Conflict (UT +1060 to +1072)

Landed vessel very near KSC to exercise spawn offset logic. Vessel spawns ~250m from the nearest vessel to prevent physics collisions.

### Timeline of Events

```
UT base+8     ─── Tedorf EVA Switch ghost starts ─────────
UT base+30    ─── Tedorf EVA Switch ghost ends ───────────
UT base+120   ─── KSC Hopper ghost starts ────────────────
UT base+176   ─── KSC Hopper ghost ends ──────────────────
UT base+210   ─── Suborbital Arc ghost starts ────────────
UT base+510   ─── Suborbital Arc ghost ends ──────────────
UT base+560   ─── Orbit-1 ghost starts (warp stops) ─────
UT base+1060  ─── Orbit-1 enters orbital segment ────────
UT base+900   ─── KSC Pad Destroyed ghost starts ────────
UT base+912   ─── KSC Pad Destroyed ghost ends ──────────
UT base+980   ─── EVA Walk Test ghost starts ─────────────
UT base+992   ─── EVA Walk Test ghost ends ───────────────
UT base+1060  ─── Close Spawn Conflict ghost starts ─────
UT base+1072  ─── Close Spawn Conflict vessel spawns ────
UT base+3560  ─── Orbit-1 vessel spawns ──────────────────
UT base+3610  ─── Island Probe ghost starts (warp stops) ─
UT base+3790  ─── Island Probe vessel spawns ─────────────
```

## Architecture

Three builder classes in `Source/Parsek.Tests/Generators/`:

### RecordingBuilder

Fluent API that produces a `ConfigNode("RECORDING")` matching `ParsekScenario.OnSave()` format exactly.

```csharp
var rec = new RecordingBuilder("My Recording")
    .AddPoint(127000, -0.0972, -74.5575, 77)
    .AddPoint(127010, -0.0972, -74.5575, 500)
    .AddOrbitSegment(129500, 132000, sma: 700000, ecc: 0.001, inc: 28.5)
    .AddPartEvent(127005, 42, (int)PartEventType.Decoupled, "fuelTank")
    .WithParentRecordingId("abc123")        // EVA child linkage
    .WithEvaCrewName("Jebediah Kerman")     // EVA child linkage
    .WithVesselSnapshot(vesselBuilder)
    .Build();   // returns ConfigNode
```

**AddPoint** parameters: `(ut, lat, lon, alt, body, rotX/Y/Z/W, funds, science, rep)` — all optional after alt, defaults to Kerbin with identity rotation and zero resources.

**AddOrbitSegment** parameters: `(startUT, endUT, inc, ecc, sma, lan, argPe, mna, epoch, body)` — Keplerian elements matching OrbitSegment struct.

**AddPartEvent** parameters: `(ut, pid, type, partName)` — records a part event at the given UT. Type is a `PartEventType` enum cast to int (Decoupled=0, Destroyed=1, ParachuteDeployed=2, ParachuteCut=3).

**WithParentRecordingId / WithEvaCrewName** — link a child recording to its parent (used for EVA child recordings). The parent recording ID is the `RecordingId` of the parent recording.

All numeric values are serialized with `CultureInfo.InvariantCulture` for locale safety.

### VesselSnapshotBuilder

Builds a minimal `ConfigNode("VESSEL")` that KSP's ProtoVessel can load.

```csharp
// Crewed vessel in orbit
VesselSnapshotBuilder.CrewedShip("Orbit-1", "Bill Kerman", pid: 12345678)
    .AsOrbiting(sma: 700000, ecc: 0.001, inc: 28.5)
    .Build();

// Unmanned probe landed
VesselSnapshotBuilder.ProbeShip("Island Probe", pid: 87654321)
    .AsLanded(-1.52, -71.97, 40)
    .Build();
```

Static factories `CrewedShip` and `ProbeShip` add a single part (mk1pod.v2 or probeCoreSphere) with required KSP fields. Use `.AddPart(name, crew)` to add more parts.

The VESSEL `pid` field is deterministically derived from `persistentId` (hex-encoded, zero-padded to 32 chars). This means repeated builds with the same persistentId produce identical output, making the injection content-stable for diffing and debugging.

Required VESSEL sub-nodes (ORBIT, PART, ACTIONGROUPS, DISCOVERY, FLIGHTPLAN, CTRLSTATE) are generated automatically.

### ScenarioWriter

Assembles recordings into a `SCENARIO` node and injects into `.sfs` save files.

```csharp
var writer = new ScenarioWriter();
writer.AddRecording(rec1);
writer.AddRecording(rec2);
writer.AddCrewReplacement("Jeb Kerman", "Bob Kerman");  // optional
writer.InjectIntoSaveFile("input.sfs", "output.sfs");
```

**Injection algorithm:**
1. Remove any existing `ParsekScenario` SCENARIO block using brace-counting (finds the SCENARIO line, checks for `name = ParsekScenario` inside, then tracks brace depth to find the matching close brace — robust against varying whitespace and nested content)
2. Find the `FLIGHTSTATE` line
3. Insert the new SCENARIO block before it with correct tab indentation

This makes the operation idempotent — safe to run repeatedly.

## Adding New Test Recordings

To add a new recording, create a builder method in `SyntheticRecordingTests.cs`:

```csharp
internal static RecordingBuilder MyNewRecording()
{
    var b = new RecordingBuilder("My New Recording");
    // Add trajectory points (UT must be after save's UT of ~126682)
    b.AddPoint(140000, -0.0972, -74.5575, 77);
    b.AddPoint(140060, -0.0972, -74.5575, 500);
    // Optionally add orbit segments and vessel snapshots
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
| KscHopper_BuildsValidRecording | 15 points, correct vesselName, no orbit segments |
| SuborbitalArc_BuildsValidRecording | 25 points, ascending UT order (InvariantCulture parse) |
| Orbit1_HasOrbitSegmentAndSnapshot | Orbit segment present, vessel snapshot with crew |
| IslandProbe_HasSnapshotNoCrew | Landed probe snapshot, no crew values |
| TedorfEvaSwitch_BuildsVesselSnapshotNotEVA | Vessel snapshot points to ship, not EVA kerbal |
| KscPadDestroyed_HasNoSnapshot | No vessel snapshot (destroyed vessel) |
| EvaWalkSkinned_HasEvaTypeSnapshot | EVA-type vessel snapshot with kerbal part |
| CloseSpawnConflict_HasLandedSnapshotNearKsc | Landed snapshot near KSC for spawn offset test |
| ScenarioWriter_SerializesCorrectly | ConfigNode to text serialization |
| ScenarioWriter_InjectIntoSave_InsertsBeforeFlightstate | Correct insertion point |
| ScenarioWriter_InjectIntoSave_ReplacesExistingParsekScenario | Idempotent replacement |
| ScenarioWriter_InjectIntoSave_Idempotent | Double-injection produces single block |
| ScenarioWriter_InjectIntoSave_HandlesVariousWhitespace | CRLF, nested nodes, extra values |
| VesselSnapshotBuilder_DeterministicPid | Same pid across builds with same persistentId |
| InjectAllRecordings | End-to-end injection into real save file (Manual) |
