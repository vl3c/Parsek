# StageRecovery Architecture Analysis
**For Parsek Project - Background Vessel Processing Reference**

Based on thorough exploration of the StageRecovery mod, this document provides detailed architectural analysis focused on how the mod intercepts vessel destruction events, simulates background vessel physics (parachute drag, powered landing), and calculates recovery outcomes -- all without the vessel being in the active scene. These patterns are directly relevant to Parsek's need to handle vessels that aren't currently loaded.

---

## 1. OVERALL PROJECT STRUCTURE AND ORGANIZATION

### File Organization
The project consists of 15 C# files in a single namespace (`StageRecovery`), targeting .NET Framework 4.7.2:

**Core Logic:**
- `StageRecovery.cs` - Main addon class, event hooks, vessel destruction/unload interception, physics estimation
- `RecoveryItem.cs` - Per-vessel recovery calculation (terminal velocity, funds, science, crew, powered landing)
- `APIManager.cs` - Custom event system for inter-mod communication

**Settings & Configuration:**
- `StockSettings.cs` - KSP stock settings integration (SR1/SR2 GameParameters classes)
- `Settings.cs` - Singleton settings holder, blacklist/ignore list management

**GUI:**
- `FlightGUI.cs` - In-flight recovery results display
- `EditorGUI.cs` - VAB/SPH stage breakdown and recovery prediction
- `SettingsGUI.cs` - Window management and toolbar integration
- `RegisterToolbar.cs` - Toolbar registration at MainMenu

**Inter-Mod Integration:**
- `RecoveryControllerWrapper.cs` - Reflection-based soft dependency on RecoveryController mod
- `StageRecoveryWrapper.cs` - Wrapper for other mods to soft-depend on StageRecovery

**Data:**
- `CrewWithSeat.cs` - Crew member paired with seat info for proper restoration
- `InstallChecker.cs` - Installation path verification
- `AssemblyVersion.cs` - Auto-generated version info

### Architectural Pattern
StageRecovery uses a **singleton MonoBehaviour** pattern. The main `StageRecovery` class is a `KSPAddon` that runs in Flight, Editor, and KSC scenes. It intercepts KSP's vessel lifecycle events and, when a vessel is about to be destroyed or goes on rails, processes it through a `RecoveryItem` that performs all calculations without needing the vessel to remain loaded.

### Key External Dependencies
- `ToolbarControl_NS` - Unified toolbar API
- `ClickThroughFix` - GUI click-through prevention
- `RealFuels` - Optional engine support (ModuleEnginesRF)
- `KSP_Log` - Logging library

---

## 2. VESSEL DESTRUCTION AND RECOVERY EVENT DETECTION

### Event Registration
The mod hooks into three critical GameEvents in `Start()`:

```csharp
GameEvents.onVesselWillDestroy.Add(VesselDestroyEvent);   // Primary: vessel about to die
GameEvents.onVesselGoOnRails.Add(VesselUnloadEvent);       // Secondary: vessel leaving physics
GameEvents.onVesselRecovered.Add(onVesselRecovered);       // Monitoring: stock recovery
GameEvents.onVesselTerminated.Add(onVesselTerminated);     // Monitoring: manual termination
```

### Primary Detection: VesselDestroyEvent
This is the main entry point. KSP fires `onVesselWillDestroy` when it is about to destroy a vessel (typically when a non-active vessel goes out of range while in atmosphere). StageRecovery intercepts this to recover the vessel before KSP destroys it.

**Recovery criteria (all must be true):**
1. Vessel is not null and has a valid protoVessel
2. Recovery has not already been attempted for this vessel ID
3. Vessel is NOT the active vessel
4. Vessel is around the home body (Kerbin)
5. Vessel is not loaded, or is packed (on rails)
6. Altitude is below atmosphere depth
7. Situation is FLYING, SUB_ORBITAL, or ORBITING
8. Vessel is not an EVA kerbal

```csharp
if (v != null && !RecoverAttemptLog.ContainsKey(v.id)
    && !(HighLogic.LoadedSceneIsFlight && v.isActiveVessel)
    && (v.mainBody == Planetarium.fetch.Home)
    && (!v.loaded || v.packed)
    && (v.altitude < v.mainBody.atmosphereDepth)
    && (v.situation == Vessel.Situations.FLYING
        || v.situation == Vessel.Situations.SUB_ORBITAL
        || v.situation == Vessel.Situations.ORBITING)
    && !v.isEVA)
```

### Secondary Detection: VesselUnloadEvent (Pre-Recovery)
When a vessel goes on rails (`onVesselGoOnRails`), StageRecovery checks for two things:

**1. Launch Clamp Recovery:** If the vessel root part has a `LaunchClamp` module, it recovers costs at 100% and destroys it immediately.

**2. Crew Pre-Recovery:** If a crewed vessel is about to be destroyed (going out of physics range while in atmosphere), the mod pre-recovers the kerbals before KSP can kill them. This addresses a KSP bug/feature where kerbals die when their vessel is destroyed in the background.

The key distance check uses the vessel's pack range:
```csharp
(FlightGlobals.ActiveVessel.transform.position - vessel.transform.position).sqrMagnitude
    > Math.Pow(vessel.vesselRanges.GetSituationRanges(Vessel.Situations.FLYING).pack, 2) - 250
```

### StageWatchList: FixedUpdate Polling
For crewed vessels that don't trigger pre-recovery on unload, the mod maintains a `StageWatchList` of vessel GUIDs. In `FixedUpdate()`, it checks each watched vessel to see if it has become unloaded/packed and meets destruction criteria:

```csharp
if ((!vessel.loaded || vessel.packed)
    && vessel.mainBody == Planetarium.fetch.Home
    && vessel.altitude < cutoffAlt
    && vessel.altitude > 0
    && /* distance check */)
```

**Watch list criteria for adding:**
- Vessel is not the active vessel
- Not landed, prelaunch, or splashed
- Has crew
- Has an orbit with periapsis below cutoff altitude
- Is around home body
- Is not EVA
- Altitude is above 0

### Cutoff Altitude Calculation
Rather than using the full atmosphere depth, the mod computes a cutoff altitude where atmospheric pressure drops below 1.0 (approximately 23km for Kerbin):

```csharp
public static float ComputeCutoffAlt(CelestialBody body, float stepSize = 100)
{
    float alt = (float)body.atmosphereDepth;
    while (alt > 0)
    {
        if (body.GetPressure(alt) < 1.0)
            alt -= stepSize;
        else
            break;
    }
    return alt;
}
```

### Duplicate Prevention
A static `RecoverAttemptLog` (Dictionary of Guid to UT) persists across scene changes within a session. On scene load, entries from the future are purged (handles reverts).

---

## 3. BACKGROUND VESSEL PHYSICS SIMULATION

StageRecovery does NOT simulate trajectory or position. Instead, it **estimates the outcome** of what would happen if the vessel descended through atmosphere. This is the key insight for Parsek: you don't need to simulate the full physics, just calculate the end result.

### Terminal Velocity Estimation
The core physics calculation determines what the vessel's terminal velocity would be at sea level, given its mass and total drag area:

```csharp
public static double VelocityEstimate(double mass, double chuteAreaTimesCd)
{
    CelestialBody home = Planetarium.fetch.Home;
    return Math.Sqrt((2000 * mass * 9.81) /
        (home.GetDensity(home.GetPressure(0), home.GetTemperature(0)) * chuteAreaTimesCd));
}
```

This uses the standard terminal velocity formula: Vt = sqrt(2mg / (rho * Cd * A)), evaluated at sea-level density on the home body.

### Drag Area Calculation: Three Parachute Systems
The mod computes total `chuteAreaTimesCd` differently depending on which parachute system is present:

**1. RealChute Module:** Uses reflection to access the RealChute materials library. For each parachute node, it extracts `deployedDiameter` and material `DragCoefficient`, computing: `Cd * pi * d^2 / 4`.

**2. RealChuteFAR:** Similar to RealChute but for the FAR-compatible version. Falls back to part prefab defaults if `moduleRef` is null (common for unloaded vessels).

**3. Stock ModuleParachute:** Uses KSP's DragCube system. Sets the deployed cube weight to 1.0 and computes the effective drag coefficient from the cube's AreaDrag values:
```csharp
dragCubes.SetCubeWeight("DEPLOYED", 1);
dragCubes.SetCubeWeight("SEMIDEPLOYED", 0);
dragCubes.SetCubeWeight("PACKED", 0);
dragCubes.SetDrag(dir, 0.03f); // mach 0.03
double dragCoeff = dragCubes.AreaDrag * PhysicsGlobals.DragCubeMultiplier;
RCParameter += (dragCoeff * PhysicsGlobals.DragMultiplier);
```

**Important: Handling unloaded parts.** For on-rails vessels, `moduleRef` may be null. The mod falls back to `partInfo.partPrefab` (the template part) to get default values.

### Reentry Burn-Up Simulation
The mod simulates whether the vessel would burn up during reentry:

1. Check if reentry heating is enabled in difficulty settings
2. If surface speed exceeds `DeadlyReentryMaxVelocity` (default 2000 m/s), calculate a burn chance: `2 * (speed/maxSpeed - 1)`
3. Try powered speed reduction first (if enabled)
4. Check for heat shield ablator remaining and reduce burn chance proportionally
5. Roll a random number; if below burn chance, the vessel is destroyed

### Powered Landing Simulation
If terminal velocity is too high for parachute-only recovery, the mod simulates a propulsive landing:

1. **Controllability check** - requires a pilot kerbal, probe core with SAS, or MechJeb core
2. **Engine enumeration** - loops through all ModuleEngines, ModuleEnginesFX, and ModuleEnginesRF, summing thrust and computing net ISP (excludes SRBs)
3. **TWR check** - total thrust must exceed `MinTWR * mass * 9.81`
4. **Fuel calculation** - uses the Tsiolkovsky rocket equation to determine fuel needed:
   ```csharp
   double finalMassRequired = totalMass * Math.Exp(-(1.5 * (Vt - targetSpeed)) / (9.81 * netISP));
   double massRequired = totalMass - finalMassRequired;
   ```
   The 1.5x multiplier is a gravity loss penalty.
5. **Fuel consumption** - actually drains propellant from protoVessel part snapshots (the only vessel modification)
6. **Delta-V application** - converts fuel consumed back to delta-V and reduces velocity

---

## 4. FUND / SCIENCE / KERBAL RECOVERY CALCULATIONS

### Recovery Process Flow
When `RecoverVessel()` is called, the flow is:

```
RecoverVessel(vessel, preRecovery)
  -> Check blacklist (skip if ALL parts are blacklisted)
  -> Fire OnRecoveryProcessingStart event
  -> Create RecoveryItem(vessel)
  -> RecoveryItem.Process(preRecovery)
       -> DetermineIfBurnedUp()
       -> DetermineTerminalVelocity()
       -> TryPoweredRecovery() (if needed)
       -> SetRecoveryPercentages()
       -> SetPartsAndFunds()
       -> RecoverScience()
       -> RecoverKerbals()
  -> FireEvent() (success or failure)
  -> AddToList() (for GUI display)
  -> PostStockMessage()
  -> RemoveCrew()
  -> If pre-recovery: vessel.Die()
  -> Fire OnRecoveryProcessingFinish event
```

### Recovery Percentage Calculation
The total recovery percentage is: `SpeedPercent * DistancePercent * GlobalModifier`

**Speed Percent (two models):**

- **Flat Rate Model:** If Vt < CutoffVelocity, SpeedPercent = 1.0 (controllable) or RecoveryModifier (uncontrollable). Otherwise 0.
- **Variable Rate Model:** Uses a downward-opening quadratic curve from 100% at LowCut to 0% at HighCut, with the vertex at LowCut.

**Distance Percent:**
Distance from KSC is calculated using great circle distance. The percent is linearly interpolated from ~98% at KSC to ~10% at the antipode (half the planet circumference), incorporating strategy modifiers via `ValueModifierQuery`.

### Fund Recovery
For each part on the vessel, uses the stock `ShipConstruction.GetPartCosts()` to get dry cost and fuel cost. Both are multiplied by the combined RecoveryPercent. Funds are added via `Funding.Instance.AddFunds()` with `TransactionReasons.VesselRecovery`.

### Science Recovery
Iterates through all parts and modules, looking for `ScienceData` ConfigNodes. Extracts subject IDs and data amounts for display. (Note: the actual science submission happens through the stock `onVesselRecovered` event fired in `FireEvent()`.)

### Kerbal Recovery
**If stage is recovered:** Sets each crew member's rosterStatus to Available. Handles a KSP bug where Squad adds two "Die" entries to the career log -- the mod removes those and adds proper "Land" and "Recover" entries instead.

**If stage is NOT recovered:** Kills crew by setting rosterStatus to Dead and calling `pcm.Die()`.

**Pre-recovery pattern:** For crewed stages about to be destroyed, the mod removes crew from the vessel BEFORE destruction, storing them as `CrewWithSeat` objects (crew member + their part snapshot). This preserves the crew even though the vessel itself will be destroyed by KSP.

### Firing Stock Events
After processing, the mod fires stock KSP events to ensure contract completion and other mod compatibility:

```csharp
// Temporarily disable stock recovery handler to avoid double-processing
VesselRecovery recovery = FindObjectOfType<VesselRecovery>();
recovery.OnDestroy();
GameEvents.onVesselRecovered.Fire(vessel.protoVessel, false);
recovery.OnAwake();
GameEvents.onVesselRecoveryProcessing.Fire(vessel.protoVessel, null, 0);
```

---

## 5. INTEGRATION WITH OTHER MODS

### FMRS Integration
StageRecovery has a complex handoff protocol with FMRS (Flight Manager for Reusable Stages). The decision logic in `SRShouldRecover()`:

1. Check `RecoveryController` first for explicit mod assignment
2. If RecoveryController says "auto" or is absent:
   - If FMRS is active and handling parachutes: SR does nothing
   - If FMRS is active but NOT handling parachutes (or deferred to SR):
     - If the vessel has control or crew: let FMRS handle it
     - Otherwise: SR handles it (uncontrolled debris)
   - If FMRS is not active: SR handles everything

**FMRS detection uses reflection** -- no hard dependency:
```csharp
static readonly Type FMRSType = GetFMRSType(); // via assembly scanning
static readonly MemberInfo FMRSSettingEnabled = FMRSType?.GetMember("_SETTING_Enabled")?.FirstOrDefault();
static readonly MemberInfo FMRSSettingArmed = ...;
static readonly MemberInfo FMRSSettingParachutes = ...;
static readonly MemberInfo FMRSSettingDeferParachutesToStageRecovery = ...;
```

The FMRS check also prevents adding vessels to the StageWatchList when FMRS is active.

### RecoveryController Integration
RecoveryController is a mediator mod that lets multiple recovery mods coordinate. StageRecovery:

1. Registers itself at startup: `RecoveryControllerWrapper.RegisterModWithRecoveryController("StageRecovery")`
2. Before processing any vessel, queries: `RecoveryControllerWrapper.ControllingMod(vessel)`
3. If the result is "StageRecovery" or "auto"/null: proceed
4. If the result is another mod name: skip

The wrapper uses reflection throughout to avoid a hard dependency.

### StageRecoveryWrapper (API for other mods)
Other mods can listen to StageRecovery events without a hard dependency by copying `StageRecoveryWrapper.cs` into their project. The wrapper provides:

- `AddRecoverySuccessEvent(Action<Vessel, float[], string>)` - listen for successful recoveries
- `AddRecoveryFailureEvent(Action<Vessel, float[], string>)` - listen for failures
- `AddRecoveryProcessingStartListener(Action<Vessel>)` - before processing begins
- `AddRecoveryProcessingFinishListener(Action<Vessel>)` - after processing ends
- `ComputeTerminalVelocity(List<ProtoPartSnapshot>)` - calculate Vt for arbitrary parts

---

## 6. KEY ARCHITECTURAL PATTERNS FOR PARSEK

### Pattern 1: Intercepting Vessel Lifecycle Without Active Scene
StageRecovery demonstrates that you can fully process a vessel using only its `ProtoVessel` (the serialized representation), without needing the vessel to be loaded in the physics scene. This is the most important takeaway for Parsek.

**Key APIs used:**
- `vessel.protoVessel.protoPartSnapshots` - iterate all parts
- `ProtoPartSnapshot.modules` / `ProtoPartModuleSnapshot` - access module data
- `ProtoPartSnapshot.resources` / `ProtoPartResourceSnapshot` - access resources
- `pps.partInfo.partPrefab` - get default values when moduleRef is null
- `vessel.protoVessel.GetVesselCrew()` - access crew

**Parsek application:** During mission playback, when a recorded vessel is not the active vessel, Parsek can use ProtoVessel data to determine vessel state without loading it into physics.

### Pattern 2: Pre-Emptive Processing Before KSP Destroys Vessels
The `onVesselWillDestroy` and `onVesselGoOnRails` hooks let you intercept vessels before KSP removes them. StageRecovery uses this to recover vessels; Parsek could use it to preserve vessel state for playback.

**Parsek application:** Hook `onVesselWillDestroy` to snapshot vessel state before KSP cleans it up. This ensures playback data is captured even for vessels that go out of range.

### Pattern 3: Outcome Estimation vs. Full Simulation
StageRecovery does NOT simulate the actual descent trajectory. It computes the end result (terminal velocity, recovery percentage) from the vessel's properties at the moment of interception. This is dramatically simpler than simulating physics frame-by-frame.

**Parsek application:** For background mission playback, consider estimating outcomes at key waypoints rather than simulating continuous trajectories. Record the key state transitions (staging, orbit changes, landings) and interpolate between them.

### Pattern 4: ModuleRef Null Handling
When vessels are unloaded (on rails), `moduleRef` on ProtoPartModuleSnapshot is often null. StageRecovery consistently falls back to the part prefab:

```csharp
if (ppms.moduleRef != null)
    engine = (ModuleEngines)ppms.moduleRef;
else
    engine = (ModuleEngines)p.partInfo.partPrefab.Modules["ModuleEngines"];
```

**Parsek application:** Any code that reads part module data from on-rails vessels must handle null moduleRef. The partPrefab provides default values but not runtime state.

### Pattern 5: Custom Event System for Mod Interoperability
The `APIManager` + `StageRecoveryWrapper` pattern provides a clean way for mods to communicate without hard dependencies. The wrapper uses `AssemblyLoader.loadedAssemblies` to discover the target mod and reflection to invoke methods.

**Parsek application:** If Parsek needs to interact with other mods (e.g., detecting staging events from FMRS, or knowing when StageRecovery processes a vessel), use this same reflection-based wrapper pattern.

### Pattern 6: Duplicate Prevention with Recovery Attempt Log
The `RecoverAttemptLog` (Dictionary of Guid to UT) prevents processing the same vessel twice, and purges future entries on revert. This is essential because multiple events can fire for the same vessel.

**Parsek application:** When recording vessel events, maintain a similar log to prevent duplicate recordings from multiple event sources firing for the same state change.

### Pattern 7: FixedUpdate Polling as Backup
The StageWatchList + FixedUpdate polling pattern acts as a safety net for vessels that slip through event-based detection. This hybrid approach (event-driven primary, poll-based secondary) is more robust than either alone.

**Parsek application:** For critical state tracking (like detecting when a playback vessel reaches a waypoint), use event-driven detection as the primary mechanism with periodic polling as a backup.

---

## SUMMARY

StageRecovery is an elegant example of **background vessel outcome estimation**. Rather than simulating vessel physics, it:

1. **Intercepts** vessel lifecycle events (`onVesselWillDestroy`, `onVesselGoOnRails`)
2. **Reads** vessel state from ProtoVessel data (works for unloaded vessels)
3. **Estimates** the outcome using simplified physics (terminal velocity formula, rocket equation)
4. **Applies** the results to the game state (funds, science, crew roster)
5. **Coordinates** with other mods via reflection-based wrappers

The architecture is built on these pillars:
1. **ProtoVessel as the universal data source** - works for loaded and unloaded vessels
2. **Event interception** - hook into KSP's vessel lifecycle before destruction
3. **Simplified physics** - estimate outcomes rather than simulate trajectories
4. **Null-safe module access** - always fall back to partPrefab defaults
5. **Reflection-based mod integration** - soft dependencies everywhere
6. **Duplicate prevention** - log-based deduplication with revert handling

For Parsek's mission playback system, the most valuable patterns are:
- Using ProtoVessel data to process vessels that aren't in the active scene
- Intercepting vessel destruction to preserve state
- Estimating outcomes at key points rather than simulating full trajectories
- The hybrid event + polling approach for reliable state tracking
- Reflection-based wrappers for mod interoperability
