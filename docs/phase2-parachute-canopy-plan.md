# Phase 2: Real Parachute Canopy on Ghost Vessels

## Goal

Replace the fake orange sphere (`CreateFakeCanopy`) with the actual canopy mesh from the part prefab. When a ParachuteDeployed event fires, the ghost should show a realistic deployed parachute canopy with correct textures and proportions.

## Background

### How KSP parachutes work

Stock parachute parts have this transform hierarchy under `model`:
```
model (localScale ~10x due to config `scale = 0.1`)
  +-- base        (parachute housing — the cylindrical part you see in VAB)
  +-- cap         (small cap on top — ejected on deploy)
  +-- canopy      (the fabric dome — stowed at scale 0,0,0 in prefab)
```

`ModuleParachute` config specifies:
- `capName = cap` — transform to hide when chute deploys
- `canopyName = canopy` — transform to scale up during deployment
- `semiDeployedAnimation` / `fullyDeployedAnimation` — Unity AnimationClips that animate canopy scale/position

All stock parachutes use `cap` and `canopy`. EVA kerbals have `canopy` but no `cap`.

**Deploy sequence:** STOWED → SEMIDEPLOYED (drogue-like) → DEPLOYED (full canopy).
Our `FlightRecorder.CheckParachuteTransition` fires `ParachuteDeployed` when state first becomes
SEMIDEPLOYED or DEPLOYED. It does **not** distinguish between the two phases — only one deploy
event is emitted per chute.

### Current state (Phase 1)

`MirrorTransformChain` copies the entire prefab mesh hierarchy including the canopy — but it copies the **stowed** state where canopy scale is (0,0,0). So the canopy exists in the ghost but is invisible.

On `ParachuteDeployed`, we currently create a fake orange sphere via `CreateFakeCanopy` as a placeholder.

### What Phase 2 changes

Instead of creating a fake sphere, we find the real canopy transform already in the ghost and scale it to deployed size. We also hide the cap (it gets ejected in the real game).

## Implementation Plan

### Step 1: ParachuteGhostInfo class

Use a **class** (not struct) to avoid value-copy issues when stored in dictionaries and mutated during playback:

```csharp
// In GhostVisualBuilder.cs
internal class ParachuteGhostInfo
{
    public uint partPersistentId;
    public Transform canopyTransform;   // the "canopy" node in ghost hierarchy
    public Transform capTransform;      // the "cap" node in ghost hierarchy (may be null)
    public Vector3 deployedCanopyScale; // scale to apply on ParachuteDeployed
    public Vector3 deployedCanopyPos;   // position to apply on ParachuteDeployed
}
```

### Step 2: Detect parachute parts during ghost build

In `AddPartVisuals`, after building the mesh hierarchy with `cloneMap`:

1. Check if the prefab has `ModuleParachute`
2. Read `capName` and `canopyName` from the module (guard both null **and** empty string)
3. Find source transforms on the **prefab** via `prefab.FindModelTransform(name)`
4. Look up their ghost clones via **`cloneMap`** (not recursive name search — deterministic, handles duplicate names)
5. Sample the deployed canopy transform from a temporary clone (see Step 3)
6. Return the `ParachuteGhostInfo` alongside the visual result

**Modified signature for `AddPartVisuals`:**
```csharp
private static bool AddPartVisuals(Transform root, ConfigNode partNode, Part prefab,
    uint persistentId, string partName, out int meshCount,
    out ParachuteGhostInfo parachuteInfo)
```

**Detection logic (after cloneMap is fully populated):**
```csharp
parachuteInfo = null;
ModuleParachute chute = prefab.FindModuleImplementing<ModuleParachute>();
if (chute != null)
{
    string canopyName = chute.canopyName;
    string capName = chute.capName;

    // Guard empty strings (some modded parts)
    Transform srcCanopy = !string.IsNullOrEmpty(canopyName)
        ? prefab.FindModelTransform(canopyName) : null;
    Transform srcCap = !string.IsNullOrEmpty(capName)
        ? prefab.FindModelTransform(capName) : null;

    // Look up ghost clones via cloneMap (deterministic, no name collisions).
    // NOTE: For EVA kerbals, FindModelRoot uses "model01" (body) but canopy is
    // under "model" (accessories), so srcCanopy won't be in cloneMap.
    // This is expected — EVA falls through to fake sphere fallback.
    Transform ghostCanopy = null, ghostCap = null;
    if (srcCanopy != null) cloneMap.TryGetValue(srcCanopy, out ghostCanopy);
    if (srcCap != null) cloneMap.TryGetValue(srcCap, out ghostCap);

    if (ghostCanopy != null)
    {
        var (scale, pos) = SampleDeployedCanopy(prefab, chute);
        parachuteInfo = new ParachuteGhostInfo
        {
            partPersistentId = persistentId,
            canopyTransform = ghostCanopy,
            capTransform = ghostCap,
            deployedCanopyScale = scale,
            deployedCanopyPos = pos
        };
        ParsekLog.Log($"    Parachute detected: canopy='{canopyName}' cap='{capName}' " +
            $"deployScale={scale}");
    }
    else if (srcCanopy != null)
    {
        // cloneMap miss — canopy transform not in ghost hierarchy (e.g. EVA model split)
        ParsekLog.Log($"    Parachute '{canopyName}' found on prefab but not in cloneMap — will use fake canopy");
    }
}
```

### Step 3: Sample deployed canopy transform safely

**Never sample on the shared prefab directly** — `PartLoader` prefabs are shared state. Mutating
animation on a prefab can permanently alter transforms if reset is incomplete.

**Clone only the model subtree, not the full prefab.** Cloning `prefab.gameObject` would
instantiate all `Part`/`PartModule` components, triggering `Awake()` before we can call
`SetActive(false)`. Cloning just the `model` transform gets us meshes + animations without
any KSP component lifecycle side effects.

```csharp
// Cache: partName → (scale, pos) — sample once per part type, reuse across ghosts
private static readonly Dictionary<string, (Vector3 scale, Vector3 pos)> deployedCanopyCache =
    new Dictionary<string, (Vector3, Vector3)>();

private static (Vector3 scale, Vector3 pos) SampleDeployedCanopy(Part prefab, ModuleParachute chute)
{
    string key = prefab.partInfo?.name ?? prefab.name;
    if (deployedCanopyCache.TryGetValue(key, out var cached))
        return cached;

    // Use semiDeployedAnimation — matches our ParachuteDeployed event timing
    // (fires when state enters SEMIDEPLOYED, not DEPLOYED)
    string animName = chute.semiDeployedAnimation;
    if (string.IsNullOrEmpty(animName))
        animName = chute.fullyDeployedAnimation;  // fallback
    if (string.IsNullOrEmpty(animName))
    {
        var fallback = (Vector3.one, Vector3.zero);
        deployedCanopyCache[key] = fallback;
        return fallback;
    }

    // Clone ONLY the model subtree — avoids Part/PartModule Awake() side effects
    Transform prefabModel = prefab.transform.Find("model") ?? prefab.transform;
    GameObject tempClone = Object.Instantiate(prefabModel.gameObject);

    Vector3 scale = Vector3.one;
    Vector3 pos = Vector3.zero;

    try
    {
        // Animation component lives on model or its children — search including inactive
        Animation anim = tempClone.GetComponentInChildren<Animation>(true);
        if (anim != null)
        {
            AnimationState state = anim[animName];
            if (state != null)
            {
                state.enabled = true;
                state.normalizedTime = 1f;
                state.weight = 1f;
                anim.Sample();

                string canopyName = chute.canopyName;
                Transform canopy = !string.IsNullOrEmpty(canopyName)
                    ? FindTransformRecursive(tempClone.transform, canopyName) : null;
                if (canopy != null)
                {
                    scale = canopy.localScale;
                    pos = canopy.localPosition;
                }
            }
        }
    }
    finally
    {
        Object.Destroy(tempClone);
    }

    var result = (scale, pos);
    deployedCanopyCache[key] = result;
    ParsekLog.Log($"  Sampled deployed canopy for '{key}': scale={scale} pos={pos}");
    return result;
}
```

**Notes:**
- `FindTransformRecursive` is used here only on the **temporary clone** (where name
  uniqueness doesn't matter since we just need the canopy). For the ghost hierarchy, we use
  `cloneMap` (Step 2).
- `GetComponentInChildren<Animation>(true)` passes `includeInactive` flag as safety — the
  clone inherits source active state but child objects may vary.

### Step 4: Store parachute info in ParsekFlight

Use a nested dictionary for O(1) lookup by recIdx + partPersistentId:

```csharp
// recIdx → (partPersistentId → info)
private Dictionary<int, Dictionary<uint, ParachuteGhostInfo>> ghostParachuteInfos =
    new Dictionary<int, Dictionary<uint, ParachuteGhostInfo>>();
```

Populated when the ghost is built. `BuildTimelineGhostFromSnapshot` returns the ghost
`GameObject` plus a `List<ParachuteGhostInfo>` (or null). `ParsekFlight` converts the list
to a dictionary and stores it.

### Step 5: Deploy real canopy on ParachuteDeployed event

In `ApplyPartEvents`, replace the `ParachuteDeployed` case:

```csharp
case PartEventType.ParachuteDeployed:
    bool usedRealCanopy = false;

    // Try real canopy first
    Dictionary<uint, ParachuteGhostInfo> infoMap;
    if (ghostParachuteInfos.TryGetValue(recIdx, out infoMap))
    {
        ParachuteGhostInfo info;
        if (infoMap.TryGetValue(evt.partPersistentId, out info) && info.canopyTransform != null)
        {
            info.canopyTransform.localScale = info.deployedCanopyScale;
            info.canopyTransform.localPosition = info.deployedCanopyPos;
            if (info.capTransform != null)
                info.capTransform.gameObject.SetActive(false);
            usedRealCanopy = true;
            Log($"Part event: ParachuteDeployed '{evt.partName}' — real canopy activated");
        }
    }

    // Fallback to fake canopy
    if (!usedRealCanopy)
    {
        var canopy = GhostVisualBuilder.CreateFakeCanopy(ghost, evt.partPersistentId);
        if (canopy != null)
        {
            TrackFakeCanopy(recIdx, evt.partPersistentId, canopy);
            Log($"Part event: ParachuteDeployed '{evt.partName}' — fake canopy (fallback)");
        }
    }
    break;
```

### Step 6: Handle ParachuteCut — canopy only, not whole part

**Codex correction:** don't hide the whole part on cut — radial chutes stay attached after
canopy is cut. Only hide canopy + cap. A separate Destroyed/Decoupled event handles full
part removal if applicable.

```csharp
case PartEventType.ParachuteCut:
    // Hide real canopy if present
    Dictionary<uint, ParachuteGhostInfo> cutMap;
    if (ghostParachuteInfos.TryGetValue(recIdx, out cutMap))
    {
        ParachuteGhostInfo info;
        if (cutMap.TryGetValue(evt.partPersistentId, out info))
        {
            if (info.canopyTransform != null)
                info.canopyTransform.localScale = Vector3.zero;
            if (info.capTransform != null)
                info.capTransform.gameObject.SetActive(false);
        }
    }
    // Also clean up fake canopy if one was used as fallback
    DestroyFakeCanopy(recIdx, evt.partPersistentId);
    // NOTE: Do NOT call HideGhostPart here — housing stays visible
    Log($"Part event: ParachuteCut '{evt.partName}' — canopy hidden, housing remains");
    break;
```

### Step 7: Helper — FindTransformRecursive

Only used for the temporary sampling clone (Step 3). Ghost hierarchy uses `cloneMap`.

```csharp
// In GhostVisualBuilder.cs
internal static Transform FindTransformRecursive(Transform parent, string name)
{
    if (parent.name == name) return parent;
    for (int i = 0; i < parent.childCount; i++)
    {
        Transform found = FindTransformRecursive(parent.GetChild(i), name);
        if (found != null) return found;
    }
    return null;
}
```

### Step 8: Cleanup on ghost destroy

In `DestroyTimelineGhost`:
```csharp
ghostParachuteInfos.Remove(index);
```

In `DestroyAllTimelineGhosts`:
```csharp
ghostParachuteInfos.Clear();
```

Also clear the static sampling cache if needed (unlikely — part prefabs don't change at runtime):
```csharp
// Optional: GhostVisualBuilder.ClearDeployedCanopyCache() for scene changes
```

## Files Modified

| File | Changes |
|------|---------|
| `GhostVisualBuilder.cs` | Add `ParachuteGhostInfo` class, `FindTransformRecursive`, `SampleDeployedCanopy` (with temp clone + cache). Modify `AddPartVisuals` to detect parachutes via cloneMap and return info. Modify `BuildTimelineGhostFromSnapshot` to collect and return parachute infos. |
| `ParsekFlight.cs` | Add `ghostParachuteInfos` nested dictionary. Update `ApplyPartEvents` ParachuteDeployed/Cut cases. Cleanup in destroy methods. |

## What stays unchanged

- `FlightRecorder.cs` — parachute event detection is already correct
- `PartEvent.cs` — event types are sufficient
- `CreateFakeCanopy` — kept as fallback, not deleted

## Codex Review Corrections Applied

### Round 1
1. **Semi-deployed timing** — use `semiDeployedAnimation` (not fully deployed) since our event fires at SEMIDEPLOYED state
2. **Safe sampling** — instantiate temp clone instead of sampling on shared prefab
3. **cloneMap lookup** — use deterministic source→clone mapping instead of recursive name search for ghost hierarchy
4. **Class + dictionary** — `ParachuteGhostInfo` as class, stored in `Dictionary<uint, ...>` for O(1) lookup
5. **Empty string guards** — check `string.IsNullOrEmpty` on capName/canopyName (not just null)
6. **ParachuteCut keeps housing** — only hide canopy+cap, don't auto-hide the whole part

### Round 2
7. **Clone model subtree only** — `Object.Instantiate(prefab.transform.Find("model").gameObject)` instead of `prefab.gameObject` to avoid `Part`/`PartModule` `Awake()` side effects (fires before `SetActive(false)`)
8. **includeInactive flag** — `GetComponentInChildren<Animation>(true)` to find Animation on potentially inactive child objects
9. **EVA cloneMap miss documented** — EVA `FindModelRoot` returns `model01` but canopy is under `model`, so cloneMap lookup correctly fails → fake sphere fallback with diagnostic log

## Testing

1. `dotnet build` — clean compile
2. `dotnet test` — existing tests pass (no test changes needed — parachute ghost info is runtime-only)
3. Manual KSP test:
   - Launch with Mk16 parachute → record → deploy chute → stop → revert → merge
   - Ghost should show real textured canopy (semi-deployed shape, not orange sphere)
   - Cap should disappear when canopy deploys
   - ParachuteCut should hide canopy but leave housing visible
4. Edge cases:
   - Vessel with no parachute — no change in behavior
   - Multiple parachutes on one vessel — each tracked independently by persistentId
   - Missing canopy transform (modded part) — falls back to fake sphere
   - EVA kerbal — has canopy but no cap (null capTransform handled)
   - Empty `capName`/`canopyName` strings — guarded, falls through to fallback

## Risks

- **Temp clone overhead:** `Object.Instantiate(prefab.gameObject)` allocates. Mitigated by per-part-name caching — each parachute type sampled only once.
- **Canopy mesh detail:** The stowed canopy mesh in the prefab might be low-poly or have UV issues at scale. If so, the fake sphere fallback remains available.
- **`semiDeployedAnimation` visual mismatch:** Semi-deployed canopy is a narrow streamer, not a full dome. Visually less dramatic but temporally correct. Could add a second phase later if we add SEMIDEPLOYED→DEPLOYED transition events.
- **Multiple `ModuleParachute` per part:** `FindModuleImplementing` only gets the first. Unlikely on stock parts, but modded parts could have multiples. Acceptable limitation for Phase 2.
