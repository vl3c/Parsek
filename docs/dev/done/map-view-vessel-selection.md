# Map View Vessel Selection Bug

## Status: FIXED

Spawned vessels were not selectable in map view or tracking station. Root causes identified and fixed.

## Root Causes Found

### 1. PID collision (HIGH impact) - FIXED
The snapshot from `vessel.BackupVessel()` preserved the original vessel's `persistentId`. After revert, that same vessel is back on the pad with the same PID. Spawning a copy created a duplicate PID, which broke KSP's PID-indexed map view and tracking station.

**Fix:** `RegenerateVesselIdentity()` generates a fresh vessel GUID (`pid` field) and sets `persistentId` to `"0"` so `ProtoVessel` assigns a fresh PID on construction.

### 2. ORBIT not rebuilt after proximity relocation (HIGH impact) - FIXED
`SpawnOrRecoverIfTooClose` mutated `lat/lon/alt` but did not rebuild the ORBIT node. Map view uses ORBIT data for icon placement, causing mismatch.

**Fix:** After mutating lat/lon/alt, rebuild ORBIT using `SaveOrbitToNode` with position computed from the new coordinates.

### 3. Discovery state hardcoded as "31" (MEDIUM impact) - FIXED
`EnsureOwnedDiscovery` hardcoded `state = "31"`. KSP's `DiscoveryLevels.Owned` may map to a different integer (29). Extra bits could confuse tracking.

**Fix:** Replaced `"31"` with `((int)DiscoveryLevels.Owned).ToString()`. Method renamed to `EnsureSpawnReadiness`.

### 4. Missing defensive ConfigNode checks (LOW impact) - FIXED
Added ensures for ACTIONGROUPS, FLIGHTPLAN, CTRLSTATE, VESSELMODULES sub-nodes.

### 5. No post-load validation (diagnostic) - FIXED
Added logging when `pv.vesselRef` is null or `orbitDriver` is missing after `Load()`.

## Investigation Leads (resolved)

### Vessel Type
Not a factor - snapshot preserves the original vessel type from recording. No code changes needed.

### OrbitDriver Initialization
Spawn sequence (Add → Load → Fire event) matches reference mods. The PID collision was preventing proper initialization.

### Spawn Sequence
`GameEvents.onNewVesselCreated` was already fired correctly, but only when `vesselRef != null`. Now validated explicitly with early return on null.

## Verification

1. Build + tests pass
2. In-game: spawn vessel, verify map view icon is clickable, tracking station lists it
3. Proximity relocation: verify relocated vessel also appears correctly
4. Check KSP.log for new PID (must differ from pad vessel)

## Reference Docs

- `docs/reference/LazySpawner-architecture-analysis.md` - identity regeneration pattern, required empty ConfigNodes
- `docs/reference/ContractConfigurator-architecture-analysis.md` - ProtoVessel creation pipeline
