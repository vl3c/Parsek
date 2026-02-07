# FMRS Architecture Analysis - Comprehensive Report

**For Parsek Project - Mission Recording System Reference**

Based on thorough exploration of the FMRS (Flight Manager for Reusable Stages) project, this document provides detailed architectural analysis focused on aspects critical for Parsek's mission recording system.

---

## 1. OVERALL PROJECT STRUCTURE AND ORGANIZATION

### File Organization
The project consists of 19 C# files organized as partial classes:

**Core Files:**
- `FMRS_Core.cs` - Main core lifecycle and initialization
- `FMRS_Core_Handler.cs` - Event detection and handling
- `FMRS_Core_Vessels.cs` - Vessel state management and time jumping
- `FMRS_Core_Recovery.cs` - Resource recovery and calculation
- `FMRS_Core_API.cs` - Public interface for external mods
- `FMRS_Core_GUI.cs` - User interface

**Support Files:**
- `FMRS_Util.cs` - Save/load utilities and data structures
- `FMRS_THL.cs` - Throttle logging and replay
- `FMRS_PM.cs` - Part module for vessel tracking
- `FMRS.cs` - Main addon initialization

### Architectural Pattern
FMRS uses a **partial class architecture** where `FMRS_Core` is split across multiple files by functional concern. The base class `FMRS_Util` provides common utilities and data structures.

---

## 2. CORE COMPONENTS AND THEIR RESPONSIBILITIES

### A. FMRS_Core (Main Controller)
**Location:** `FMRS_Core.cs`

**Key Responsibilities:**
- Plugin lifecycle management (activation, deactivation)
- Timer management for staging delays
- Scene state coordination
- Integration with KSP addon system

**Critical Methods:**
- `FMRS_core_awake()` - Initializes save file system
- `flight_scene_start_routine()` - Sets up flight scene, handles pre-launch saves
- `flight_scene_update_routine()` - Main update loop, processes timers and state
- `save_game_pre_flight()` - Creates "before_launch" and "FMRS_main_save" snapshots

**Key State Variables:**
```csharp
bool plugin_active
double Time_Trigger_Staging, Time_Trigger_Start_Delay
bool timer_staging_active
string quicksave_file_name
Guid _SAVE_Main_Vessel  // The primary vessel ID
bool _SAVE_Switched_To_Dropped  // Whether we're controlling a dropped stage
```

### B. FMRS_Core_Handler (Event Detection System)
**Location:** `FMRS_Core_Handler.cs`

**Event Handlers Attached:**
1. **Main Mission Events:**
   - `onStageSeparation` → `staging_routine()` - Detects staging
   - `onUndock` → `staging_routine()` - Detects undocking (optional)
   - `onVesselCreate` → `vessel_create_routine()` - Detects vessel creation
   - `onLaunch` → `launch_routine()` - Detects launch

2. **Dropped Vessel Events:**
   - `OnVesselRecoveryRequested` → `recovery_requested_handler()`
   - `onCollision/onCrash/onCrashSplashdown` → `crash_handler()`
   - `onCrewKilled` → `crew_killed_handler()`
   - `onVesselGoOnRails/onVesselGoOffRails` - Rails state management

3. **Career Mode Events:**
   - `Contract.onCompleted` → `contract_routine()`
   - `OnScienceRecieved` → `science_sent_routine()`
   - `OnReputationChanged` → `rep_changed()`
   - `OnKSCStructureCollapsing` → `building_destroyed()`

**Key Pattern:**
```csharp
// Staging detection with delay timer
public void staging_routine(EventReport event_input) {
    Time_Trigger_Staging = Planetarium.GetUniversalTime();
    timer_staging_active = true;
    staged_vessel = true;
    timer_cuto_active = true;  // Auto-throttle cutoff
}
```

### C. FMRS_Core_Vessels (State Management & Time Travel)
**Location:** `FMRS_Core_Vessels.cs`

**Core Functionality:**
1. **Vessel State Preservation** - `save_landed_vessel()`
2. **Time Jump Implementation** - `jump_to_vessel()` methods
3. **Vessel Discovery** - `search_for_new_vessels()`
4. **Rails State Management** - `vessel_on_rails()`, `vessel_off_rails()`

**Critical for Parsek:** This component shows how to merge vessel states back into the main timeline.

### D. FMRS_Util (Persistence Layer)
**Location:** `FMRS_Util.cs`

**Core Data Structures:**
```csharp
Dictionary<Guid, string> Vessels_dropped           // vessel_id → save_file_name
Dictionary<Guid, string> Vessels_dropped_names     // vessel_id → vessel_name
Dictionary<Guid, vesselstate> Vessel_State         // vessel_id → state (FLY/LANDED/DESTROYED/RECOVERED)
Dictionary<String, Guid> Kerbal_dropped            // kerbal_name → vessel_id
List<recover_value> recover_values                 // Recovery data to merge back
```

**Save System:**
- Uses text-based save files: `save.txt` and `recover.txt`
- Save categories: SETTING, SAVE, SAVEFILE, DROPPED, NAME, STATE, KERBAL_DROPPED
- Key-value pair format: `CATEGORY=KEY=VALUE`

---

## 3. SAVE POINT GENERATION MECHANISM

### Save Point Trigger Events

**1. Pre-Flight Save:**
```csharp
// Creates baseline save before launch
IEnumerator save_game_pre_flight() {
    while (!FlightGlobals.ActiveVessel.protoVessel.wasControllable)
        yield return 0;

    SaveGame("before_launch", HighLogic.SaveFolder + "/FMRS", SaveMode.OVERWRITE);
    SaveGame("FMRS_main_save", HighLogic.SaveFolder, SaveMode.OVERWRITE);
}
```

**2. Staging/Separation Save:**
```csharp
// Timer-based delayed save after staging
if ((Time_Trigger_Staging + Timer_Stage_Delay) <= Planetarium.GetUniversalTime()) {
    quicksave_file_name = gamesave_name + FlightGlobals.ActiveVessel.currentStage.ToString();

    if (search_for_new_vessels(quicksave_file_name)) {
        FMRS_SAVE_Util.Instance.SaveGame(
            quicksave_file_name,
            HighLogic.SaveFolder + "/FMRS",
            SaveMode.OVERWRITE
        );
    }
}
```

**Save File Naming Convention:**
- Format: `FMRS_save_{stage_number}` or `FMRS_save_separated_{number}`
- Stored in: `{SaveFolder}/FMRS/`
- Main save: `FMRS_main_save` (in main save folder)

### Staging Delay Pattern
```csharp
public float Timer_Stage_Delay = 0.2f;  // Configurable delay
```
This delay ensures physics settle before saving, preventing state corruption.

---

## 4. TIME REVERT IMPLEMENTATION

### Jump-to-Vessel Mechanism

**Three jump methods:**

**A. Jump to Dropped Vessel (by GUID):**
```csharp
public void jump_to_vessel(Guid vessel_id, bool save_landed) {
    // 1. Save current state if needed
    if (save_landed) {
        if (FlightGlobals.ActiveVessel.id == _SAVE_Main_Vessel)
            SaveGame("FMRS_main_save", HighLogic.SaveFolder, SaveMode.OVERWRITE);
        save_landed_vessel(true, false);
    }

    // 2. Load the save file containing the target vessel
    Game loadgame = GamePersistence.LoadGame(
        get_save_value(save_cat.DROPPED, vessel_id.ToString()),
        HighLogic.SaveFolder + "/FMRS",
        false, false
    );

    // 3. Find vessel index in save
    for (load_vessel = 0;
         load_vessel < loadgame.flightState.protoVessels.Count &&
         loadgame.flightState.protoVessels[load_vessel].vesselID != vessel_id;
         load_vessel++);

    // 4. Jump to vessel with delay to prevent save conflicts
    if (vessel_id != _SAVE_Main_Vessel) {
        _SAVE_Switched_To_Savefile = get_save_value(save_cat.DROPPED, vessel_id.ToString());
        _SAVE_Switched_To_Dropped = true;
    }

    FMRS_SAVE_Util.Instance.StartAndFocusVessel(loadgame, load_vessel);
}
```

**B. Jump Back to Main Mission:**
```csharp
public void jump_to_vessel(string main) {
    save_landed_vessel(true, false);  // Merge current state

    loadgame = GamePersistence.LoadGame("FMRS_main_save", HighLogic.SaveFolder, false, false);

    // Find main vessel
    for (load_vessel = 0;
         load_vessel < loadgame.flightState.protoVessels.Count &&
         loadgame.flightState.protoVessels[load_vessel].vesselID != _SAVE_Main_Vessel;
         load_vessel++);

    _SAVE_Switched_To_Dropped = false;
    StartAndFocusVessel(loadgame, load_vessel);
}
```

### Critical Wrapper: FMRS_SAVE_Util

This utility prevents save/load race conditions:

```csharp
public class FMRS_SAVE_Util : MonoBehaviour {
    bool saveInProgress = false;
    bool readyToLoad = false;
    Game gameToLoad = null;
    int vesselToFocus = 0;

    public void StartAndFocusVessel(Game stateToLoad, int vesselToFocusIdx) {
        if (saveInProgress) {
            // Queue the load operation
            readyToLoad = true;
            gameToLoad = stateToLoad;
            vesselToFocus = vesselToFocusIdx;
        } else {
            // Wait 1 second to ensure scene stability
            Wait(1, () => {
                FlightDriver.StartAndFocusVessel(gameToLoad, vesselToFocus);
            });
        }
    }

    void OnGameStateSaved(Game game) {
        saveInProgress = false;
        if (gameToLoad != null && readyToLoad) {
            Wait(1, doStartAndFocusVessel);
        }
    }
}
```

**Key Insight:** Uses Unity coroutines and GameEvents to coordinate async operations.

---

## 5. VESSEL STATE PRESERVATION

### State Tracking Enum
```csharp
public enum vesselstate : int {
    NONE = 1,
    FLY,        // In flight
    LANDED,     // Landed/splashed but not recovered
    DESTROYED,  // Crashed/destroyed
    RECOVERED   // Successfully recovered
}
```

### State Preservation Method: `save_landed_vessel()`

**Complex Multi-Step Process:**

```csharp
public void save_landed_vessel(bool auto_recover_allowed, bool ForceRecover) {
    // 1. Save current scene state
    SaveGame("FMRS_quicksave", HighLogic.SaveFolder + "/FMRS", SaveMode.OVERWRITE);

    // 2. Load both current state and main save
    loadgame = GamePersistence.LoadGame("FMRS_quicksave", HighLogic.SaveFolder + "/FMRS", false, false);
    savegame = GamePersistence.LoadGame("FMRS_main_save", HighLogic.SaveFolder, false, false);

    // 3. Identify damaged vessels (went off rails without proper shutdown)
    foreach (Guid id in loaded_vessels) {
        if (FlightGlobals.Vessels.Find(v => v.id == id) == null)
            if (!damaged_vessels.Contains(id))
                damaged_vessels.Add(id);
    }

    // 4. Build vessel hierarchy using FMRS_PM parent tracking
    foreach (ProtoVessel pv in vessel_list) {
        foreach (ProtoPartSnapshot ps in pv.protoPartSnapshots) {
            foreach (ProtoPartModuleSnapshot ppms in ps.modules) {
                if (ppms.moduleName == "FMRS_PM") {
                    temp_guid = new Guid(ppms.moduleValues.GetValue("parent_vessel"));
                    if (loaded_vessels.Contains(temp_guid))
                        vessel_dict[temp_guid].Add(pv);
                }
            }
        }
    }

    // 5. Merge vessels back into main save
    foreach (KeyValuePair<Guid, List<ProtoVessel>> kvp in vessel_dict) {
        if ((_SETTING_Auto_Recover || ForceRecover) && auto_recover_allowed && ReferenceBodyIndex == 1) {
            savegame = recover_vessel(kvp.Key, kvp.Value, loadgame, savegame);
        } else {
            // Remove old version from main save
            ProtoVessel temp_proto_del2 = savegame.flightState.protoVessels.Find(
                prtv => prtv.vesselID == kvp.Key
            );
            if (temp_proto_del2 != null)
                savegame.flightState.protoVessels.Remove(temp_proto_del2);

            // Add updated versions
            foreach (ProtoVessel pv in kvp.Value) {
                if (pv.landed || pv.splashed) {
                    savegame.flightState.protoVessels.Add(pv);
                    set_vessel_state(kvp.Key, vesselstate.LANDED);
                }
            }
        }
    }

    // 6. Save updated main timeline
    SaveGame(savegame, "FMRS_main_save", HighLogic.SaveFolder, SaveMode.OVERWRITE);
}
```

### Vessel Part Tracking: FMRS_PM

**Part Module for Parent Tracking:**
```csharp
class FMRS_PM : PartModule {
    public string parent_vessel;  // Tracks which original vessel this part belonged to

    public override void OnSave(ConfigNode node) {
        node.AddValue("MM_DYNAMIC", "true");
        node.AddValue("parent_vessel", parent_vessel);
    }

    public void setid() {
        parent_vessel = this.vessel.id.ToString();
    }
}
```

This module is dynamically added to all parts and persists across vessel splits, enabling reconstruction of "which parts came from which vessel."

---

## 6. SAVE FILE MERGING

### Recovery Value System

**Data Structure:**
```csharp
public struct recover_value {
    public string cat;    // Category: fund, science, contract, kerbal, building, message
    public string key;    // Action or identifier
    public string value;  // Data to apply
}

List<recover_value> recover_values = new List<recover_value>();
```

**Setting Recovery Values:**
```csharp
public void set_recoverd_value(string cat, string key, string value) {
    recover_value temp_value;
    temp_value.cat = cat;
    temp_value.key = key;
    temp_value.value = value;
    recover_values.Add(temp_value);
}
```

### Merge Process: `recover_vessel()`

**Comprehensive Resource Recovery:**

```csharp
private Game recover_vessel(Guid parent_vessel, List<ProtoVessel> recover_vessels,
                            Game recover_save, Game savegame) {
    float total_cost = 0, total_rec_fac = 0, science = 0;

    foreach (ProtoVessel pv in recover_vessels) {
        if (pv.landed || pv.splashed) {
            // 1. Recover Kerbals
            if (pv.GetVesselCrew().Count > 0) {
                foreach (ProtoCrewMember crew_member in pv.GetVesselCrew()) {
                    foreach (ProtoCrewMember member in savegame.CrewRoster.Crew) {
                        if (member.name == crew_member.name) {
                            member.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                        }
                    }
                }
            }

            // 2. Calculate Funds (Career Mode)
            if (HighLogic.CurrentGame.Mode == Game.Modes.CAREER) {
                cost = vessels_cost(pv);                  // Calculate part values
                rec_fact = calc_recovery_factor(pv);      // Distance-based multiplier
                cost *= rec_fact * strat_rec_fact;        // Apply multipliers
                total_cost += cost;
                set_recoverd_value("fund", "add", cost.ToString());
            }

            // 3. Recover Science
            if (HighLogic.CurrentGame.Mode == Game.Modes.CAREER ||
                HighLogic.CurrentGame.Mode == Game.Modes.SCIENCE_SANDBOX) {
                foreach (ScienceData recovered_data in recover_science(pv)) {
                    ScienceSubject temp_sub = ResearchAndDevelopment.GetSubjectByID(
                        recovered_data.subjectID
                    );
                    string temp_string = temp_sub.id + "@" + temp_sub.dataScale.ToString() +
                                       "@" + temp_sub.subjectValue.ToString() +
                                       "@" + temp_sub.scienceCap.ToString();
                    set_recoverd_value("science", temp_string,
                                      Math.Round(recovered_data.dataAmount, 2).ToString());
                    science += recovered_data.dataAmount;
                }
            }

            // 4. Contracts
            if (contract_complete.ContainsKey(pv.vesselID)) {
                foreach (Contract c in contract_complete[pv.vesselID]) {
                    set_recoverd_value("contract", "complete", c.ContractID.ToString());
                }
            }

            // 5. Remove from save (it's been recovered)
            ProtoVessel temp_proto = savegame.flightState.protoVessels.Find(
                p => p.vesselID == pv.vesselID
            );
            if (temp_proto != null)
                savegame.flightState.protoVessels.Remove(temp_proto);

            set_vessel_state(parent_vessel, vesselstate.RECOVERED);
        }
    }

    return savegame;
}
```

### Applying Recovered Values: `write_recovered_values_to_save()`

**Executed on Main Timeline:**
```csharp
private void write_recovered_values_to_save() {
    foreach (recover_value recover_data in recover_values) {
        switch (recover_data.cat) {
            case "fund":
                Funding.Instance.AddFunds(float.Parse(recover_data.value),
                                         TransactionReasons.VesselRecovery);
                break;

            case "science":
                string[] line = recover_data.key.Split('@');
                ScienceSubject subject = ResearchAndDevelopment.GetSubjectByID(line[0].Trim());
                if (subject == null) {
                    subject = new ScienceSubject(line[0].Trim(), line[1].Trim(),
                                                float.Parse(line[2].Trim()),
                                                float.Parse(line[3].Trim()),
                                                float.Parse(line[4].Trim()));
                }
                ResearchAndDevelopment.Instance.SubmitScienceData(
                    float.Parse(recover_data.value), subject, 1f
                );
                break;

            case "contract":
                Contract temp_contract = ContractSystem.Instance.Contracts.Find(
                    c => c.ContractID == long.Parse(recover_data.value)
                );
                if (temp_contract != null && temp_contract.ContractState != Contract.State.Completed) {
                    temp_contract.Complete();
                }
                break;

            case "kerbal":
                Reputation.Instance.AddReputation(float.Parse(recover_data.value),
                                                 TransactionReasons.VesselLoss);
                break;

            case "message":
                MessageSystem.Instance.AddMessage(new MessageSystem.Message(
                    recover_data.key,
                    recover_data.value.Replace("@", System.Environment.NewLine),
                    MessageSystemButton.MessageButtonColor.GREEN,
                    MessageSystemButton.ButtonIcons.MESSAGE
                ));
                break;
        }
    }
}
```

---

## 7. KEY ARCHITECTURAL INSIGHTS FOR PARSEK

### Critical Lessons from FMRS

1. **Save File Architecture:**
   - Use separate save files for different timeline branches
   - Maintain a "main timeline" that gets updated with merged results
   - Store save file references in dictionaries keyed by vessel GUID

2. **Async Operation Handling:**
   - Always wait for save operations to complete before loading
   - Use GameEvents.onGameStateSaved for coordination
   - Add 1-second delays before scene transitions for stability

3. **Part-Level Tracking:**
   - Use dynamic part modules to track vessel ancestry
   - Module data persists through vessel splits
   - Essential for reconstructing which parts belong to which original vessel

4. **Event Detection:**
   - Use delayed processing (0.2s timer) after staging events
   - Physics needs time to settle before saving
   - Auto-throttle cutoff prevents separated stages from continuing to burn

5. **State Merging:**
   - Load both current state and main timeline
   - Group vessels by original parent using part modules
   - Remove old versions before adding updated ones
   - Apply accumulated changes (funds, science, contracts) after merge

6. **Rails State Management:**
   - Remove crew from unloaded dropped vessels to prevent duplication
   - Track which vessels are in physics range (loaded_vessels list)
   - Handle vessel on-rails/off-rails events to maintain consistency

7. **Recovery Value System:**
   - Accumulate changes in a list during branch execution
   - Apply all changes atomically when merging back
   - Use structured data (category, key, value) for flexibility

---

## 8. THROTTLE LOGGING AND REPLAY (FMRS_THL.cs)

**Not covered in initial analysis.** FMRS includes a full throttle recording and replay system that is directly relevant to Parsek's flight input recording.

### Throttle Logger (FMRS_THL_Log)

Records throttle values keyed by Universal Time:

```csharp
public void LogThrottle(float in_throttle)
{
    if (started)
    {
        if (!writing)
            Throttle_Log_Buffer.Add(new entry(Planetarium.GetUniversalTime(), in_throttle));
        else
            temp_buffer.Add(new entry(Planetarium.GetUniversalTime(), in_throttle));
    }
}
```

**Key patterns:**
- **Double-buffering**: Uses `temp_buffer` during writes to prevent data loss
- **Batch writing**: Flushes to file when buffer exceeds 1000 entries
- **Sorted output**: Sorts entries by time before writing
- **File format**: Simple `time@value` text format with `####EOF####` terminator

### Throttle Replayer (FMRS_THL_Rep)

Replays throttle values via KSP's `FlightCtrlState` callback:

```csharp
public void flybywire(FlightCtrlState state)
{
    // Reads from queue, skips entries in the past
    while (true)
    {
        if (Throttle_Replay.Count < 10 && !EOF)
            read_throttle_values();
        temp_entry = Throttle_Replay.Dequeue();
        if (temp_entry.time < Planetarium.GetUniversalTime())
            continue;  // Skip past entries
        state.mainThrottle = temp_entry.value;
        break;
    }
}
```

**Key patterns:**
- **Queue-based replay**: Uses `Queue<entry>` for efficient dequeue
- **Streaming file reads**: Reads 1000 entries at a time, not entire file
- **Time-based seeking**: Skips entries until reaching current UT
- **FlightCtrlState injection**: Hooks into KSP's fly-by-wire system to set throttle

**Relevance for Parsek:** This pattern can be extended to record/replay ALL flight inputs (pitch, yaw, roll, throttle, SAS, RCS) for more accurate mission replay than position-only recording.

---

## 9. STOCK SETTINGS INTEGRATION (Settings.cs)

**Not covered in initial analysis.** FMRS integrates settings into KSP's built-in settings menu using `GameParameters.CustomParameterNode`.

```csharp
public class FMRS_Settings : GameParameters.CustomParameterNode
{
    public override string Title { get { return ""; } }
    public override GameParameters.GameMode GameMode { get { return GameParameters.GameMode.ANY; } }
    public override string Section { get { return "FMRS"; } }
    public override string DisplaySection { get { return "FMRS"; } }
    public override int SectionOrder { get { return 1; } }
    public override bool HasPresets { get { return false; } }

    [GameParameters.CustomParameterUI("#FMRS_Local_027")]  // FMRS Enabled
    public bool enabled = true;

    [GameParameters.CustomFloatParameterUI("#FMRS_Local_031", minValue = 0.2f, maxValue = 5.0f,
        asPercentage = false, displayFormat = "0.0",
        toolTip = "#FMRS_Local_032")]  // Stage Delay
    public float Timer_Stage_Delay = 0.2f;
}
```

**Key patterns:**
- Uses `[GameParameters.CustomParameterUI]` for booleans
- Uses `[GameParameters.CustomFloatParameterUI]` for sliders with min/max/format
- `Enabled()` method controls conditional visibility of settings
- Localization keys used for all UI strings

**Relevance for Parsek:** Use this pattern instead of custom settings windows. Gives users a familiar interface and persists settings per-save automatically.

---

## 10. LOCALIZATION SUPPORT (FMRS_Core_GUI.cs)

**Not covered in initial analysis.** FMRS uses KSP's built-in localization system.

```csharp
using KSP.Localization;

private static string Local_001 = Localizer.GetStringByTag("#FMRS_Local_001");  // "Mission Time"
private static string Local_007 = Localizer.GetStringByTag("#FMRS_Local_007");  // "YES"

// In-line formatting:
GUILayout.Box(Localizer.Format("#FMRS_Local_015", get_time_string(...)), ...);
```

**Localization files at:** `GameData/FMRS/Localization/en-us.cfg` and `zh-cn.cfg`

**Relevance for Parsek:** Key all UI strings from the start using `Localizer.GetStringByTag()`. Even if you only ship English initially, it's much easier to add languages later if strings are already keyed.

---

## 11. GUI PATTERNS (FMRS_Core_GUI.cs)

**Not covered in initial analysis.** FMRS demonstrates several important KSP GUI patterns:

### ClickThroughBlocker Integration
```csharp
using ClickThroughFix;

windowPos = ClickThruBlocker.GUILayoutWindow(baseWindowID + 1, windowPos, MainGUI, "FMRS", GUILayout.MinWidth(100));
```
Drop-in replacement for `GUILayout.Window()` that prevents mouse clicks from passing through the mod window to the game world.

### Window ID Generation
```csharp
baseWindowID = UnityEngine.Random.Range(1000, 2000000) + _AssemblyName.GetHashCode();
```
Prevents window ID collisions with other mods.

### Screen Clamping
```csharp
windowPos.x = Mathf.Clamp(windowPos.x, 0, Screen.width - windowPos.width);
windowPos.y = Mathf.Clamp(windowPos.y, 0, Screen.height - windowPos.height);
```
Keeps windows on-screen after resolution changes.

### Custom GUIStyle Initialization
```csharp
private void init_skin()
{
    GUI.skin = HighLogic.Skin;  // Use KSP's built-in skin
    GUIStyle MyButton = new GUIStyle(HighLogic.Skin.button);
    MyButton.fontSize = 15;
    MyButton.normal.textColor = Color.white;
    // Color-coded styles for status: green (landed), red (destroyed), yellow (in flight)
}
```

**Relevance for Parsek:** All of these patterns are essential for Parsek's recording panel, timeline viewer, and playback controls.

---

## SUMMARY

FMRS implements a sophisticated **save-branch-merge** architecture that allows KSP players to control multiple vessels across different points in time. The key innovation is using KSP's built-in save/load system to create temporal branches, then surgically merging the results back into a main timeline.

The architecture is built on these pillars:
1. **Multiple Save Files** - Each branch point gets its own save
2. **Part-Level Tracking** - Dynamic modules track vessel parentage
3. **Async Coordination** - Careful handling of save/load timing
4. **Recovery Values** - Accumulate changes to apply later
5. **State Machines** - Track vessel lifecycle states
6. **Event-Driven** - Heavy use of GameEvents for integration
7. **Multi-Scene Support** - Persistent state across scenes
8. **Throttle Recording/Replay** - FlightCtrlState injection for input replay
9. **Stock Settings Integration** - GameParameters.CustomParameterNode for settings
10. **Localization** - KSP.Localization.Localizer for all UI strings

For Parsek's mission recording system, the most valuable patterns are:
- Save-branch-merge architecture for parallel timelines
- Part module tracking for vessel ancestry
- Recovery value accumulation for deferred state changes
- Async save/load coordination to prevent race conditions
- Delayed event processing for physics stability
- Throttle recording via double-buffered UT-keyed entries and FlightCtrlState replay
- Stock settings menu integration via GameParameters.CustomParameterNode
- ClickThroughBlocker for window rendering, random window IDs, screen clamping
- Localization keys for all UI strings from day one
