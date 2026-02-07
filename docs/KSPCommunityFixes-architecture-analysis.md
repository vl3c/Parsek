# KSPCommunityFixes Architecture Analysis
**For Parsek Project - Harmony Patching Patterns Reference**

Based on thorough exploration of the KSPCommunityFixes (KSPCF) project, this document provides detailed architectural analysis focused on Harmony patching patterns, KSP internal knowledge, and lessons applicable to Parsek's mission recording and replay system.

---

## 1. OVERALL PROJECT STRUCTURE AND ORGANIZATION

### File Organization
The project contains approximately 100+ C# files organized by functional category:

```
KSPCommunityFixes/
├── KSPCommunityFixes.cs       # Main addon entry point, patch loading orchestrator
├── BasePatch.cs               # Abstract base class for all patches
├── BugFixes/                  # ~55 bug fix patches
│   ├── FlightSceneLoadKraken.cs
│   ├── TimeWarpOrbitShift.cs
│   ├── TimeWarpBodyCollision.cs
│   ├── PackedPartsRotation.cs
│   ├── PartStartStability.cs       # Transpiler on compiler-generated code
│   ├── DockingPortConserveMomentum.cs  # Transpiler on closures
│   ├── RoboticsDrift.cs            # Complex multi-patch with state tracking
│   ├── AutoStrutDrift.cs
│   ├── StickySplashedFixer.cs
│   ├── FixGetUnivseralTime.cs
│   ├── ModuleIndexingMismatch.cs   # Transpiler replacing entire code sections
│   └── ... (many more)
├── Performance/               # ~37 performance optimization patches
│   ├── FlightIntegratorPerf.cs     # Override patch type for whole methods
│   ├── FewerSaves.cs
│   ├── FloatingOriginPerf.cs
│   └── ...
├── QoL/                       # ~17 quality of life patches
├── Modding/                   # ~8 modding API patches
├── Internal/
│   └── PatchSettings.cs       # In-game settings UI integration
├── Library/                   # Shared utility code
│   ├── UnityObjectExtensions.cs    # Fast null checks for Unity objects
│   ├── KSPObjectsExtensions.cs     # Part/Module lookup optimizations
│   ├── Extensions.cs
│   ├── Numerics.cs
│   ├── StaticHelpers.cs
│   └── ...
└── lib/                       # External dependencies
```

### Architectural Pattern
KSPCF uses a **plugin-per-class architecture** where each fix/patch is a self-contained class deriving from `BasePatch`. The system discovers, validates, and applies patches automatically through reflection at startup.

---

## 2. THE PATCH REGISTRATION AND LOADING SYSTEM

### Entry Point: KSPCommunityFixes.cs

The main class loads at `KSPAddon.Startup.Instantly` with `DontDestroyOnLoad`:

```csharp
[KSPAddon(KSPAddon.Startup.Instantly, true)]
public class KSPCommunityFixes : MonoBehaviour
{
    public static Harmony Harmony { get; private set; }
    public static HashSet<string> enabledPatches = new HashSet<string>();
    public static Dictionary<Type, BasePatch> patchInstances = new Dictionary<Type, BasePatch>();

    static KSPCommunityFixes()
    {
        Harmony = new Harmony("KSPCommunityFixes");
    }
}
```

**Key design decisions:**
- Single `Harmony` instance shared across all patches (identified by string `"KSPCommunityFixes"`)
- Static constructor creates the Harmony instance before anything else
- `DontDestroyOnLoad` ensures the singleton persists across scene loads

### Loading Sequence

1. **ModuleManager integration**: KSPCF inserts itself as the first post-patch callback in ModuleManager's callback list. This ensures patches are applied after MM has processed all configs but before part compilation.

2. **Config loading**: Reads a `KSP_COMMUNITY_FIXES` ConfigNode from `Settings.cfg` to determine which patches are enabled/disabled.

3. **Patch discovery**: Reflects over all types in the assembly to find non-abstract subclasses of `BasePatch` that lack `[ManualPatch]`:

```csharp
foreach (Type type in Assembly.GetAssembly(basePatchType).GetTypes())
{
    if (!type.IsAbstract && type.IsSubclassOf(basePatchType)
        && type.GetCustomAttribute<ManualPatchAttribute>() == null)
    {
        patchesTypes.Add(type);
    }
}
```

4. **Priority sorting**: Patches are sorted by `[PatchPriority(Order = N)]` attribute (default 0).

5. **Sequential application**: Each patch is instantiated and applied through `BasePatch.Patch(patchType)`.

### BasePatch: The Patch Framework

Each patch class implements these lifecycle methods:

```csharp
public abstract class BasePatch
{
    // Required: define what methods to patch
    protected abstract void ApplyPatches();

    // Optional: version gating (defaults to KSP 1.12.0 - 1.12.99)
    protected virtual Version VersionMin => new Version(1, 12, 0);
    protected virtual Version VersionMax => new Version(1, 12, 99);

    // Optional: skip the enabled/disabled config check
    protected virtual bool IgnoreConfig => false;

    // Optional: custom applicability check
    protected virtual bool CanApplyPatch(out string reason) { ... }

    // Optional: called after patch is applied
    protected virtual void OnPatchApplied() { }

    // Optional: load persisted data
    protected virtual void OnLoadData(ConfigNode node) { }
}
```

**Patch application pipeline:**
1. `CanApplyPatch()` - custom eligibility check
2. Config check - is the patch name present and enabled in Settings.cfg?
3. `IsVersionValid` - KSP version range check
4. `ApplyHarmonyPatch()` - calls `ApplyPatches()` then applies all registered patches
5. `LoadData()` - loads persisted patch-specific data from PluginData
6. `OnPatchApplied()` - post-application hook

### Patch Registration API

Inside `ApplyPatches()`, patches register their Harmony patches using convenience methods:

```csharp
// Simple: type + method name
AddPatch(PatchType.Prefix, typeof(TimeWarp), nameof(TimeWarp.setRate));

// With argument types for overload resolution
AddPatch(PatchType.Postfix, typeof(Part), "SecureAutoStrut",
    new Type[]{typeof(Part), typeof(AttachNode), typeof(AttachNode), typeof(bool)});

// With explicit patch method name (overrides naming convention)
AddPatch(PatchType.Transpiler, partStartEnumerator, "MoveNext",
    nameof(Part_StartEnumerator_MoveNext_Transpiler));

// With priority
AddPatch(PatchType.Postfix, typeof(Vessel), nameof(Vessel.GoOnRails),
    patchPriority: Priority.Normal);
```

**Naming convention for patch methods** (when not explicitly specified):
`{TargetType}_{TargetMethod}_{PatchType}`

Example: `TimeWarp_setRate_Prefix`, `Vessel_GoOnRails_Postfix`

### Supported Patch Types

- **Prefix** - runs before the original method
- **Postfix** - runs after the original method
- **Transpiler** - modifies the IL of the original method
- **Finalizer** - runs after the method even if it throws
- **ReversePatch** - creates a callable copy of the original method
- **Override** - KSPCF custom: transpiles the patch method's body directly into the target, replacing it entirely (more reliable than `return false` prefixes)

---

## 3. HARMONY PATCH PATTERNS (WITH EXAMPLES)

### Pattern 1: Simple Prefix (Skip Original)

The most common pattern. Return `false` to skip the original method entirely.

**Example: FixGetUnivseralTime** - replaces `Planetarium.GetUniversalTime()`:
```csharp
static bool Planetarium_GetUniversalTime_Prefix(ref double __result)
{
    if (HighLogic.LoadedSceneIsEditor || Planetarium.fetch == null)
        __result = HighLogic.CurrentGame?.UniversalTime ?? 0d;
    else
        __result = Planetarium.fetch.time;

    return false;  // Skip original
}
```

**Key points for Parsek:**
- `ref __result` lets you set the return value
- `return false` skips the original method
- Check `HighLogic.LoadedSceneIsEditor`, `HighLogic.LoadedSceneIsFlight`, etc. for scene-specific behavior

### Pattern 2: Simple Prefix (Conditional Skip)

**Example: TimeWarpBodyCollision** - patches `TimeWarp.ClampRateToOrbitTransitions`:
```csharp
static bool TimeWarp_ClampRateToOrbitTransitions_Prefix(
    TimeWarp __instance, int rate, int maxAllowedSOITransitionRate,
    int secondsBeforeSOItransition, Orbit obt, out int __result)
{
    __result = rate;
    if (obt.patchEndTransition != Orbit.PatchTransitionType.FINAL
        && rate > maxAllowedSOITransitionRate)
    {
        double warpDeltaTime = obt.EndUT - Planetarium.GetUniversalTime()
            - secondsBeforeSOItransition;
        __instance.getMaxWarpRateForTravel(warpDeltaTime, 1, 4, out var rateIdx);
        __result = rate < rateIdx ? rate : Math.Max(rateIdx, maxAllowedSOITransitionRate);
    }
    return false;
}
```

**Key points for Parsek:**
- `__instance` gives you the object the method was called on
- `out __result` is equivalent to `ref __result` for setting return values
- Access to `Orbit` properties: `patchEndTransition`, `EndUT`, `ApR`, `PeA`, `eccentricity`

### Pattern 3: Prefix with Coroutine Kickoff

**Example: TimeWarpOrbitShift** - fixes orbit drift when entering time warp:
```csharp
static void TimeWarp_setRate_Prefix(TimeWarp __instance, int rateIdx)
{
    if (HighLogic.LoadedSceneIsFlight
        && __instance.Mode == TimeWarp.Modes.HIGH
        && rateIdx > __instance.maxPhysicsRate_index
        && __instance.current_rate_index <= __instance.maxPhysicsRate_index)
    {
        __instance.StartCoroutine(FixMaxDTPerFrameOnEngageHighWarpCoroutine());
    }
}

static IEnumerator FixMaxDTPerFrameOnEngageHighWarpCoroutine()
{
    Time.maximumDeltaTime = 0.02f;
    yield return null;
    Time.maximumDeltaTime = GameSettings.PHYSICS_FRAME_DT_LIMIT;
}
```

**Key insight for Parsek:** You can start coroutines from Harmony patches by using `__instance.StartCoroutine()`. This is essential for delayed operations that need to span frames.

### Pattern 4: Simple Postfix

**Example: PackedPartsRotation** - fixes part rotation when vessel goes on rails:
```csharp
static void Vessel_GoOnRails_Postfix(Vessel __instance)
{
    if (__instance.orbitDriver.updateMode != OrbitDriver.UpdateMode.UPDATE)
        return;

    Quaternion vesselRotation = __instance.vesselTransform.rotation;
    int partCount = __instance.parts.Count;
    for (int i = 0; i < partCount; i++)
    {
        __instance.parts[i].transform.rotation =
            vesselRotation * __instance.parts[i].orgRot;
    }
}
```

**Key insight for Parsek:**
- `Vessel.GoOnRails()` is called when a vessel enters the "packed" (on-rails) state
- `Part.orgPos` and `Part.orgRot` are the "pristine" (editor-defined) positions/rotations
- `OrbitDriver.UpdateMode` tells you how the vessel position is being computed

### Pattern 5: Prefix + Postfix with State Passing

**Example: StickySplashedFixer** - uses `__state` to pass data from prefix to postfix:
```csharp
static void Part_Die_Prefix(Part __instance, out Vessel __state)
{
    __state = __instance.vessel;
}

static void Part_Die_Postfix(Vessel __state)
{
    if (__state.IsNotNullOrDestroyed() && __state.state != Vessel.State.DEAD)
        __state.UpdateLandedSplashed();
}
```

**Key points for Parsek:**
- `out Vessel __state` in prefix creates state available to the postfix
- The `__state` type can be any type you need
- Use `IsNotNullOrDestroyed()` extension (see Section 6) for safe Unity null checks

### Pattern 6: Transpiler - Simple Instruction Replacement

**Example: PartStartStability** - changes `yield return null` to `yield return new WaitForFixedUpdate()`:
```csharp
static IEnumerable<CodeInstruction> Part_StartEnumerator_MoveNext_Transpiler(
    IEnumerable<CodeInstruction> instructions)
{
    List<CodeInstruction> code = new List<CodeInstruction>(instructions);

    for (int i = 1; i < code.Count - 1; i++)
    {
        if (code[i - 1].opcode == OpCodes.Ldnull
            && code[i].opcode == OpCodes.Stfld
            && (FieldInfo)code[i].operand == current)
        {
            code[i - 1].opcode = OpCodes.Newobj;
            code[i - 1].operand = waitForFixedUpdateCtor;
        }
    }

    return code;
}
```

**Key points for Parsek:**
- Transpilers modify IL instructions, which is fragile but powerful
- Always convert to a `List<CodeInstruction>` for easier manipulation
- Match instruction patterns by checking `opcode` and `operand`
- Can target compiler-generated types (e.g., coroutine state machines)

### Pattern 7: Transpiler - Method Call Replacement

**Example: LostSoundAfterSceneSwitch** - replaces `Transform.SetParent(Transform)` with `Transform.SetParent(Transform, bool)`:
```csharp
static IEnumerable<CodeInstruction> FlightCamera_EnableCamera_Transpiler(
    IEnumerable<CodeInstruction> instructions)
{
    MethodInfo setParentOriginal = AccessTools.Method(typeof(Transform),
        nameof(Transform.SetParent), new[] {typeof(Transform)});
    MethodInfo setParentReplacement = AccessTools.Method(typeof(Transform),
        nameof(Transform.SetParent), new[] {typeof(Transform), typeof(bool)});

    List<CodeInstruction> code = new List<CodeInstruction>(instructions);

    for (int i = 0; i < code.Count; i++)
    {
        if (code[i].opcode == OpCodes.Callvirt
            && ReferenceEquals(code[i].operand, setParentOriginal))
        {
            code[i].operand = setParentReplacement;
            code.Insert(i, new CodeInstruction(OpCodes.Ldc_I4_0));  // Push false
        }
    }

    return code;
}
```

### Pattern 8: Transpiler - Code Section Removal

**Example: FewerSaves** - removes a `GamePersistence.SaveGame()` call from `SpaceTracking.OnVesselDeleteConfirm`:
```csharp
static IEnumerable<CodeInstruction> SpaceTracking_OnVesselDeleteConfirm_Transpiler(
    IEnumerable<CodeInstruction> instructions)
{
    var codes = new List<CodeInstruction>(instructions);

    for (int i = 0; i < codes.Count; i++)
    {
        if (codes[i].opcode == OpCodes.Ldstr
            && codes[i].operand as string == "persistent")
        {
            startIndex = i;
            // Find the end of the section to remove
            for (int j = startIndex; j < codes.Count; j++)
            {
                if (codes[j].opcode == OpCodes.Ldarg_0)
                {
                    endIndex = j;
                    break;
                }
            }
            break;
        }
    }

    if (startIndex > -1 && endIndex > -1)
        codes.RemoveRange(startIndex, endIndex - startIndex);

    return codes;
}
```

### Pattern 9: Transpiler on Closures/Generated Code

**Example: DockingPortConserveMomentum** - patches closures generated by `SetupFSM`:
```csharp
protected override void ApplyPatches()
{
    // Closures don't have stable method names, so patch all candidates
    Traverse dockingNodeTraverse = Traverse.Create<ModuleDockingNode>();
    foreach (string methodName in dockingNodeTraverse.Methods())
    {
        if (methodName.StartsWith("<SetupFSM>") && !methodName.Contains("_Patch"))
        {
            AddPatch(PatchType.Transpiler, typeof(ModuleDockingNode), methodName,
                "ModuleDockingNode_SetupFSMClosure_Transpiler");
        }
    }
}

// In the transpiler: verify it's the right closure before modifying
static IEnumerable<CodeInstruction> ModuleDockingNode_SetupFSMClosure_Transpiler(...)
{
    List<CodeInstruction> instrList = instructions.ToList();
    if (IsTargetClosure(instrList))
    {
        // Apply modifications only to the correct closure
    }
    else
    {
        // Pass through unmodified
        foreach (CodeInstruction instr in instrList)
            yield return instr;
    }
}
```

**Key insight for Parsek:** When patching lambda/closure methods, enumerate all possible methods and use content inspection to find the right one. Always pass through non-matching methods unmodified.

### Pattern 10: Override (KSPCF Custom)

**Example: FlightIntegratorPerf** - completely replaces performance-critical methods:
```csharp
AddPatch(PatchType.Override, typeof(VesselPrecalculate),
    nameof(VesselPrecalculate.CalculatePhysicsStats));
AddPatch(PatchType.Override, typeof(FlightIntegrator),
    nameof(FlightIntegrator.Integrate));
```

The Override patch method must have the same signature as the original, with `__instance` as the first argument for instance methods:
```csharp
static void VesselPrecalculate_CalculatePhysicsStats_Override(VesselPrecalculate vp)
{
    // Complete reimplementation
}
```

**Key insight for Parsek:** The Override type is specific to KSPCF's framework - it transpiles the replacement method body directly into the target. For Parsek, a `return false` prefix achieves a similar effect but with slightly more overhead.

### Pattern 11: Multi-Patch with External State

**Example: RoboticsDrift** - patches 6 methods on `BaseServo` with shared state:
```csharp
class RoboticsDrift : BasePatch
{
    private static readonly Dictionary<Part, ServoInfo> servoInfos = new Dictionary<Part, ServoInfo>();

    protected override void ApplyPatches()
    {
        AddPatch(PatchType.Postfix, typeof(BaseServo), nameof(BaseServo.OnStart));
        AddPatch(PatchType.Postfix, typeof(BaseServo), nameof(BaseServo.OnDestroy));
        AddPatch(PatchType.Prefix, typeof(BaseServo), nameof(BaseServo.RecurseCoordUpdate));
        AddPatch(PatchType.Prefix, typeof(BaseServo), nameof(BaseServo.OnSave));
        AddPatch(PatchType.Prefix, typeof(BaseServo), nameof(BaseServo.ModifyLocked));
        AddPatch(PatchType.Prefix, typeof(BaseServo), nameof(BaseServo.OnPartPack));

        GameEvents.onGameSceneLoadRequested.Add(OnSceneSwitch);
    }

    private void OnSceneSwitch(GameScenes data)
    {
        servoInfos.Clear();  // Clean up on scene switch
    }
}
```

**Key insights for Parsek:**
- Use `static` dictionaries to track state across multiple patches
- Subscribe to `GameEvents.onGameSceneLoadRequested` to clean up on scene switches
- `CanApplyPatch()` can check for DLC presence before applying
- `OnPatchApplied()` can subscribe to GameEvents after the patch is live

---

## 4. PATCHES RELEVANT TO PARSEK

### Vessel State and Physics

| Patch | What It Patches | Relevance to Parsek |
|-------|----------------|---------------------|
| **PackedPartsRotation** | `Vessel.GoOnRails()` postfix | Shows how part positions/rotations reset when going on-rails. Parsek needs to understand the packed/unpacked vessel state cycle. |
| **PartStartStability** | `Part.Start()` coroutine MoveNext transpiler | Reveals the Part initialization sequence (rigidbodies, then joints, across multiple frames). Critical for replay fidelity. |
| **FlightSceneLoadKraken** | `HighLogic.LoadScene` postfix + `FlightDriver.Start` prefix | Shows how to manipulate `Time.maximumDeltaTime` for physics stability during scene loads. |
| **AutoStrutDrift** | `Part.SecureAutoStrut()` postfix | Shows how to work with `Part.orgPos`/`Part.orgRot` (pristine positions) vs in-physics positions. |
| **RoboticsDrift** | Multiple `BaseServo` methods | Demonstrates maintaining pristine coordinate tracking alongside physics coordinates - exactly what Parsek needs for position recording. |
| **StickySplashedFixer** | `Vessel.updateSituation()` prefix | Complete reimplementation of vessel situation detection logic. Shows how `Vessel.Situations` enum works. |
| **DockingPortConserveMomentum** | `ModuleDockingNode.SetupFSM` closure transpiler | Pattern for patching FSM closures - relevant if Parsek needs to intercept docking events. |

### Time Warp and Orbit

| Patch | What It Patches | Relevance to Parsek |
|-------|----------------|---------------------|
| **TimeWarpOrbitShift** | `TimeWarp.setRate()` prefix | Reveals that entering non-physical time warp can shift orbits due to Update/FixedUpdate desynchronization. Parsek should record time warp transitions. |
| **TimeWarpBodyCollision** | `TimeWarp.ClampRateToOrbitTransitions()` prefix | Shows how to work with `Orbit.patchEndTransition`, `Orbit.EndUT`, and time warp rate clamping. |
| **RestoreMaxPhysicsDT** | `TimeWarp.updateRate()` postfix | Reveals that physics warp increases `Time.fixedDeltaTime` and `Time.maximumDeltaTime` may not be restored. Critical for replay timing accuracy. |
| **FixGetUnivseralTime** | `Planetarium.GetUniversalTime()` prefix | Shows that `Planetarium.GetUniversalTime()` can return wrong values in certain scenes. Use `Planetarium.fetch.time` in flight, `HighLogic.CurrentGame.UniversalTime` in editor. |

### Saves and Persistence

| Patch | What It Patches | Relevance to Parsek |
|-------|----------------|---------------------|
| **FewerSaves** | Various scene spawner `onDespawn` methods | Shows which stock operations trigger unnecessary saves. |
| **ModuleIndexingMismatch** | `ProtoPartSnapshot.Load`/`ConfigurePart` transpiler | Shows the complete module loading pipeline and how persisted state maps to runtime modules. Critical if Parsek stores per-module data. |
| **KerbalInventoryPersistence** | `ProtoCrewMember.kerbalModule` getter transpiler | Pattern for patching property getters. |

### Scene Management

| Patch | What It Patches | Relevance to Parsek |
|-------|----------------|---------------------|
| **LostSoundAfterSceneSwitch** | `FlightCamera.EnableCamera`/`DisableCamera` transpiler | Scene switch camera handling - relevant for replay camera management. |
| **FlightSceneLoadKraken** | `HighLogic.LoadScene` + `FlightDriver.Start` | Shows the flight scene initialization timing (50 frames of reduced physics delta before normal). |

### Physics Integration

| Patch | What It Patches | Relevance to Parsek |
|-------|----------------|---------------------|
| **FlightIntegratorPerf** | `VesselPrecalculate.CalculatePhysicsStats`, `FlightIntegrator.Integrate`, `FlightIntegrator.UpdateAerodynamics`, etc. | Complete reimplementation of physics stats calculation. Shows exactly how vessel mass, CoM, velocity, angular velocity, and MoI are computed from parts. Essential reference for what data to record. |

---

## 5. KSP INTERNALS KNOWLEDGE (FROM KSPCF SOURCE)

### Vessel State Machine

From StickySplashedFixer, the vessel situation progression:
```
PRELAUNCH -> LANDED/SPLASHED (on movement)
LANDED -> FLYING (on atmosphere entry) -> ORBITING/SUB_ORBITAL/ESCAPING
SPLASHED -> FLYING -> ORBITING/SUB_ORBITAL/ESCAPING
```

Key properties: `Vessel.Landed`, `Vessel.Splashed`, `Vessel.staticPressurekPa`, `Vessel.orbit.eccentricity`, `Vessel.orbit.ApR`, `Vessel.orbit.PeA`, `Vessel.mainBody.sphereOfInfluence`.

### Packed vs Unpacked Vessels

- **Unpacked (off-rails)**: Full physics simulation, rigidbodies active, FixedUpdate runs
- **Packed (on-rails)**: Orbit-driven position updates, no physics, positions from OrbitDriver
- `Vessel.GoOnRails()` / `Vessel.GoOffRails()` are the transitions
- When packing: `Part.orgPos`/`Part.orgRot` are the pristine positions
- When unpacking: rigidbodies are re-enabled, joints reconnected

### Time Systems

- `Planetarium.GetUniversalTime()` - game time in seconds (but buggy in editor)
- `Planetarium.fetch.time` - reliable in flight
- `HighLogic.CurrentGame.UniversalTime` - reliable fallback
- `Time.maximumDeltaTime` - Unity's cap on physics frame duration
- `Time.fixedDeltaTime` - physics timestep (changes with physics warp)
- `GameSettings.PHYSICS_FRAME_DT_LIMIT` - user setting for max dt

### Key GameEvents for Parsek

From analyzing KSPCF's event subscriptions:
- `GameEvents.onGameSceneLoadRequested` - scene switch cleanup
- `GameEvents.OnPartLoaderLoaded` - after parts are compiled
- `GameEvents.onProtoPartModuleSnapshotLoad` - module state loading
- `GameEvents.onVesselSituationChange` - vessel state transitions
- `GameEvents.onRoboticPartLockChanging/Changed` - servo lock events

### Vessel Physics Stats (from FlightIntegratorPerf)

The key vessel properties computed each FixedUpdate:
- `vessel.totalMass` - sum of all part physicsMass
- `vessel.CoMD` - center of mass (world space, double precision)
- `vessel.rb_velocityD` - rigidbody velocity (double precision)
- `vessel.velocityD` - rb_velocity + Krakensbane frame velocity
- `vessel.angularVelocityD` - angular velocity in reference transform space
- `vessel.MOI` - moment of inertia (diagonal elements only)

### Krakensbane

`Krakensbane.GetFrameVelocity()` returns the floating origin velocity offset. KSP shifts the entire world to keep the active vessel near the origin, and Krakensbane tracks this shift. The true vessel velocity is `rb_velocity + Krakensbane.GetFrameVelocity()`.

---

## 6. KEY PATTERNS FOR WRITING RELIABLE HARMONY PATCHES IN KSP

### Pattern: Unity Object Null Checking

KSPCF's `UnityObjectExtensions` provides fast null checks that avoid Unity's expensive `==` operator override:

```csharp
// 4-5x faster than `obj == null`
obj.IsNullOrDestroyed()    // null reference OR destroyed Unity object
obj.IsNotNullOrDestroyed() // not null AND not destroyed

// Pure reference checks (ignore Unity destroyed state)
obj.IsNullRef()
obj.IsNotNullRef()

// Null-conditional support for Unity objects
obj.DestroyedAsNull()?.someField ?? defaultValue
```

**Recommendation for Parsek:** Implement similar extension methods. The `== null` check on Unity objects is surprisingly expensive and appears in hot paths.

### Pattern: Scene-Aware Patching

Almost every KSPCF patch checks the current scene before acting:
```csharp
if (HighLogic.LoadedScene != GameScenes.FLIGHT) return;
if (!HighLogic.LoadedSceneIsFlight) return;
if (HighLogic.LoadedSceneIsEditor) return true;  // Let original run
```

**Recommendation for Parsek:** Always gate patches to the relevant scene. Parsek should primarily operate in `GameScenes.FLIGHT`.

### Pattern: Version Gating

```csharp
protected override Version VersionMin => new Version(1, 8, 0);
protected override Version VersionMax => new Version(1, 12, 99);
```

**Recommendation for Parsek:** Gate patches to supported KSP versions. The `Versioning.version_major/version_minor/Revision` fields provide the current KSP version.

### Pattern: Conditional Patch Application

```csharp
protected override bool CanApplyPatch(out string reason)
{
    if (!Directory.Exists(Path.Combine(KSPExpansionsUtils.ExpansionsGameDataPath, "Serenity")))
    {
        reason = "Breaking Grounds DLC not installed";
        return false;
    }
    return base.CanApplyPatch(out reason);
}
```

### Pattern: Accessing Private Fields with AccessTools

```csharp
// Access private fields
AccessTools.Field(typeof(ProtoPartSnapshot), nameof(ProtoPartSnapshot.partRef));

// Access private methods
AccessTools.Method(typeof(Part), nameof(Part.OnLoad), new[] { typeof(ConfigNode) });

// Access property getters
AccessTools.PropertyGetter(typeof(ProtoCrewMember), "kerbalModule");

// Access constructors
AccessTools.Constructor(typeof(WaitForFixedUpdate));

// Traverse for exploring unknown types
Traverse.Create<ModuleDockingNode>().Methods();
```

### Pattern: Error Handling in Patches

KSPCF wraps patch application in try/catch and logs failures:
```csharp
try
{
    patchApplied = patch.ApplyHarmonyPatch();
}
catch (Exception e)
{
    patchApplied = false;
    Debug.LogException(e);
}
```

**Recommendation for Parsek:** Always wrap patch logic in try/catch. A crashing Harmony patch can break the target method entirely.

### Pattern: Transpiler Safety

KSPCF transpilers include safety checks to avoid corrupting methods:
```csharp
// Safety check: if we've gone too far, return original unmodified
if (ReferenceEquals(code[j].operand, ProtoPartSnapshot_resources))
    return instructions;  // Return original, unmodified
```

**Recommendation for Parsek:** If a transpiler cannot find its expected pattern, always return the original instructions unmodified. Never partially modify a method.

### Pattern: Using Private Fields in Patches

Harmony special parameter names for accessing private fields:
```csharp
// Access private field via ___fieldName (triple underscore)
static void SomePatch(BaseServo __instance,
    GameObject ___movingPartObject,       // private field
    bool ___servoTransformPosLoaded,      // private field
    bool ___servoTransformRotLoaded)      // private field
```

### Pattern: GameEvents Cleanup on Scene Switch

```csharp
protected override void ApplyPatches()
{
    // ... add patches ...
    GameEvents.onGameSceneLoadRequested.Add(OnSceneSwitch);
}

private void OnSceneSwitch(GameScenes data)
{
    trackedData.Clear();  // Prevent stale references
}
```

**Recommendation for Parsek:** Always clean up tracked state when scenes change. Stale vessel/part references are a major source of NullReferenceExceptions and memory leaks.

---

## 7. RECOMMENDATIONS FOR PARSEK

### 1. Adopt the BasePatch Pattern

Create a similar base class for Parsek's Harmony patches:
```csharp
public abstract class ParsekPatch
{
    protected static Harmony Harmony;
    protected abstract void ApplyPatches();
    // ... version checks, error handling, logging
}
```

This provides consistent error handling, logging, and a clean separation between patches.

### 2. Use Prefix/Postfix for Event Interception

For recording vessel state, postfix patches on key methods:
- `Vessel.GoOnRails()` / `Vessel.GoOffRails()` - packed state transitions
- `VesselPrecalculate.CalculatePhysicsStats()` - per-frame physics data
- `TimeWarp.setRate()` - time warp changes
- `Part.Die()`, `Part.decouple()` - structural changes

### 3. Avoid Transpilers Unless Necessary

KSPCF uses transpilers extensively because it needs to fix internal bugs. Parsek's recording system should primarily use prefix/postfix patches, which are:
- More maintainable
- Less fragile across KSP versions
- Easier to debug

Reserve transpilers for cases where you need to intercept a value mid-method or modify compiler-generated code.

### 4. Time and Coordinate Precision

From FlightIntegratorPerf, use `double` precision (Vector3d, QuaternionD) for:
- Vessel position (`vessel.CoMD`)
- Velocity (`vessel.velocityD`, `vessel.rb_velocityD`)
- Angular velocity (`vessel.angularVelocityD`)
- Orbital parameters

Use `float` precision only for rendering.

### 5. Handle Krakensbane Frame Velocity

When recording vessel velocity, account for floating origin shifts:
```csharp
Vector3d trueVelocity = vessel.rb_velocityD + Krakensbane.GetFrameVelocity();
```

### 6. Scene Switch Safety

Subscribe to scene change events and clean up all tracked state:
```csharp
GameEvents.onGameSceneLoadRequested.Add(OnSceneSwitch);
GameEvents.onVesselDestroy.Add(OnVesselDestroyed);
```

---

## SUMMARY

KSPCommunityFixes is the most sophisticated Harmony patching project in the KSP modding ecosystem. Its architecture provides an excellent reference for:

1. **Patch organization** - one class per patch, automatic discovery, config-driven enabling
2. **All Harmony patch types** - comprehensive examples of prefix, postfix, transpiler, finalizer, and custom override patterns
3. **KSP internal knowledge** - deep understanding of vessel physics, time systems, packed/unpacked state, and scene management
4. **Defensive coding** - version gating, error handling, transpiler safety, Unity null check optimization

For Parsek's mission recording system, the most valuable takeaways are:
- **Postfix on `Vessel.GoOnRails/GoOffRails`** for tracking packed state transitions
- **Postfix on `VesselPrecalculate.CalculatePhysicsStats`** for per-frame physics data capture
- **Prefix on `TimeWarp.setRate`** for time warp change detection
- **Double precision** for all position/velocity/rotation recording
- **Krakensbane awareness** for correct velocity computation
- **Scene switch cleanup** for preventing stale reference crashes
- **Unity null check extensions** for performance in hot paths
