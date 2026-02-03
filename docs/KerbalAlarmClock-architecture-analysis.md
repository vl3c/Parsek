# Kerbal Alarm Clock Architecture Analysis

**For Parsek Project - Timeline Event Management Reference**

Based on thorough exploration of the Kerbal Alarm Clock project, this document provides detailed analysis for building Parsek's timeline-based mission execution system.

---

## 1. OVERALL PROJECT STRUCTURE AND ORGANIZATION

The project is organized into several key areas:

**Core Files:**
- `KerbalAlarmClock.cs` - Main behavior class with multiple scene-specific subclasses
- `AlarmObjects.cs` - Alarm data model (KACAlarm class)
- `AlarmActions.cs` - Action configuration for alarms
- `KerbalAlarmClock_ScenarioModule.cs` - Persistence layer

**Supporting Files:**
- `KerbalAlarmClock_GameState.cs` - Game state tracking and vessel monitoring
- `WarpTransitionCalculator.cs` - Time warp rate transition calculations
- `TimeObjects.cs` - Time manipulation utilities
- `API.cs` & `API/KACWrapper.cs` - Public API for mod integration

**Framework:**
- `FrameworkExt/KSPDateTime.cs` - Custom date/time handling
- `FrameworkExt/KSPTimeSpan.cs` - Custom time interval handling

---

## 2. ALARM/EVENT SCHEDULING SYSTEM - STORAGE AND MANAGEMENT

**KACAlarm Class (AlarmObjects.cs lines 13-588):**

```csharp
public class KACAlarm : ConfigNodeStorage
{
    // Core properties
    [Persistent] public String VesselID = "";
    [Persistent] public String ID = "";  // Unique GUID
    [Persistent] public String Name = "";
    public String Notes = "";
    [Persistent] public AlarmTypeEnum TypeOfAlarm;

    public KSPDateTime AlarmTime = new KSPDateTime(0);  // Event time
    [Persistent] public Double AlarmMarginSecs = 0;     // Margin before event
    [Persistent] public Boolean Enabled = true;

    // State flags
    [Persistent] internal Boolean Triggered = false;
    [Persistent] internal Boolean Actioned = false;

    // Dynamic properties
    public KSPTimeSpan Remaining = new KSPTimeSpan(0);
    public Boolean WarpInfluence = false;
}
```

**Alarm Types (lines 15-36):**
- Raw (manual time)
- Maneuver/ManeuverAuto
- Apoapsis/Periapsis
- AscendingNode/DescendingNode
- SOIChange/SOIChangeAuto
- Transfer/TransferModelled
- Contract/ContractAuto
- Crew, Distance, EarthTime, ScienceLab

**Storage Pattern:**
- Uses `KACAlarmList` which extends `List<KACAlarm>` (lines 646-748)
- Custom Add/Remove methods trigger API events
- Stored as static global list: `public static KACAlarmList alarms`

---

## 3. TIME TRACKING USING UNIVERSAL TIME (UT)

**KSPDateTime Class (KSPDateTime.cs lines 11-677):**

Key features:
- Root property: `public Double UT` - seconds since game epoch
- Calculated properties: Year, DayOfYear, Day, Month, Hour, Minute, Second
- Supports both Kerbin and Earth calendars
- Static `Now` property: `new KSPDateTime(Planetarium.GetUniversalTime())`

**Time Representation:**
```csharp
// Current time tracking in game state
internal static KSPDateTime CurrentTime = new KSPDateTime(0);
internal static KSPDateTime LastTime = new KSPDateTime(0);

// Update pattern (KerbalAlarmClock_GameState.cs line 199)
KACWorkerGameState.CurrentTime.UT = Planetarium.GetUniversalTime();
```

**Remaining Time Calculation (AlarmObjects.cs lines 163-173):**
```csharp
internal void UpdateRemaining(double remaining)
{
    Remaining.UT = remaining;

    // Only update string representation when value changes by 1 second
    if (Math.Floor(remaining) != _lastRemainingUTStringUpdate)
    {
        _remainingTimeStamp3 = Remaining.ToStringStandard(
            KerbalAlarmClock.settings.TimeSpanFormat, 3);
        _lastRemainingUTStringUpdate = Math.Floor(remaining);
    }
}
```

---

## 4. WARP CONTROL INTEGRATION

**WarpTransitionCalculator (WarpTransitionCalculator.cs lines 12-157):**

Calculates smooth warp rate transitions:
```csharp
internal static List<WarpTransition> WarpRateTransitionPeriods;

internal static void CalcWarpRateTransitions()
{
    // For each warp rate, calculate UT needed to transition up/down
    for (int i = 0; i < TimeWarp.fetch.warpRates.Length; i++)
    {
        WarpTransition newRate = new WarpTransition(i, TimeWarp.fetch.warpRates[i]);

        if (i>0)
            newRate.UTToRateDown = (warpRates[i] + warpRates[i-1]) / 2;
        if (i<warpRates.Length-1)
            newRate.UTToRateUp = (warpRates[i] + warpRates[i+1]) / 2;

        WarpRateTransitionPeriods.Add(newRate);
    }
}
```

**Warp Control Logic (KerbalAlarmClock.cs lines 1787-1833):**

```csharp
// Gradual warp reduction before alarm
if (!tmpAlarm.Actioned && tmpAlarm.Enabled &&
    (tmpAlarm.HaltWarp || tmpAlarm.PauseGame))
{
    if (settings.WarpTransitions_Instant)
    {
        // Check if alarm will be passed in next 2 updates
        Double TimeNext = CurrentTime.UT + SecondsTillNextUpdate * 2;
        if (TimeNext > tmpAlarm.AlarmTime.UT)
        {
            tmpAlarm.WarpInfluence = true;
            TimeWarp.SetRate(w.current_rate_index - 1, true);
        }
    }
    else
    {
        // Use transition calculator for smooth slowdown
        if (WarpTransitionCalculator.UTToRateTimesOne *
            settings.WarpTransitions_UTToRateTimesOneTenths / 10 >
            tmpAlarm.AlarmTime.UT - CurrentTime.UT)
        {
            tmpAlarm.WarpInfluence = true;
            TimeWarp.SetRate(w.current_rate_index - 1, false);
        }
    }
}
```

---

## 5. EVENT TRIGGERING MECHANISM

**Main Loop (KerbalAlarmClock.cs lines 508-542):**

```csharp
internal override void RepeatingWorker()
{
    UpdateDetails();  // Called at configured rate (default 0.1s)

    if (Contracts.ContractSystem.Instance)
        UpdateContractDetails();

    // Check warp rate transitions periodically
    if (!settings.WarpTransitions_Instant)
        WarpTransitionCalculator.CheckForTransitionChanges();
}
```

**Alarm Parsing and Triggering (lines 1688-1887):**

```csharp
private void ParseAlarmsAndAffectWarpAndPause(double SecondsTillNextUpdate)
{
    for (int i = 0; i < alarms.Count; i++)
    {
        KACAlarm tmpAlarm = alarms[i];

        // Update remaining time for each alarm
        if (tmpAlarm.TypeOfAlarm != KACAlarm.AlarmTypeEnum.EarthTime)
            tmpAlarm.UpdateRemaining(tmpAlarm.AlarmTime.UT - CurrentTime.UT);
        else
            tmpAlarm.UpdateRemaining((EarthTimeDecode(tmpAlarm.AlarmTime.UT) -
                                     DateTime.Now).TotalSeconds);

        // Trigger alarm when remaining time <= 0
        if ((tmpAlarm.Remaining.UT <= 0) && tmpAlarm.Enabled && !tmpAlarm.Triggered)
        {
            LogFormatted("Triggering Alarm - " + tmpAlarm.Name);
            tmpAlarm.Triggered = true;

            // Create repeat alarm if configured
            if (CreateAlarmRepeats(tmpAlarm, out alarmAddTemp))
                alarmsToAdd.Add(alarmAddTemp);

            // Raise API event
            APIInstance_AlarmStateChanged(tmpAlarm, AlarmStateEventsEnum.Triggered);

            // Execute actions: pause game or halt warp
            if (tmpAlarm.PauseGame)
            {
                TimeWarp.fetch.CancelAutoWarp();
                TimeWarp.SetRate(0, true);
                FlightDriver.SetPause(true);
            }
            else if (tmpAlarm.HaltWarp)
            {
                TimeWarp.fetch.CancelAutoWarp();
                TimeWarp.SetRate(0, true);
            }
        }

        // Mark as actioned if triggered (for UI display)
        if (tmpAlarm.Triggered && !tmpAlarm.Actioned)
        {
            tmpAlarm.Actioned = true;

            // Play sound if configured
            if (tmpAlarm.Actions.PlaySound)
                audioController.Play(clipAlarms[soundName], repeatCount);
        }
    }
}
```

---

## 6. SOI CHANGE DETECTION AND AUTOMATIC ALARMS

**MonitorSOIOnPath (lines 1269-1357):**

```csharp
private void MonitorSOIOnPath()
{
    // Check orbit patch transition type
    if (settings.SOITransitions.Contains(CurrentVessel.orbit.patchEndTransition))
    {
        timeSOIChange = CurrentVessel.orbit.UTsoi;

        strSOIAlarmNotes = CurrentVessel.vesselName + " - Nearing SOI Change\r\n" +
            "     Old SOI: " + CurrentVessel.orbit.referenceBody.bodyName + "\r\n" +
            "     New SOI: " + CurrentVessel.orbit.nextPatch.referenceBody.bodyName;
    }

    // Find existing SOI alarm for this vessel
    KACAlarm tmpSOIAlarm = alarms.Find(a =>
        a.VesselID == CurrentVessel.id.ToString() &&
        (a.TypeOfAlarm == AlarmTypeEnum.SOIChangeAuto ||
         a.TypeOfAlarm == AlarmTypeEnum.SOIChange) &&
        !a.Triggered
    );

    // Update or create alarm
    if (timeSOIChange != 0)
    {
        timeSOIAlarm = timeSOIChange - settings.AlarmAutoSOIMargin;

        if (tmpSOIAlarm != null)
        {
            // Update existing alarm if still far enough away
            if ((timeSOIAlarm - CurrentTime.UT) > settings.AlarmAddSOIAutoThreshold)
                tmpSOIAlarm.AlarmTime.UT = timeSOIAlarm;
        }
        else if (timeSOIAlarm > CurrentTime.UT)
        {
            // Create new auto alarm
            alarms.Add(new KACAlarm(CurrentVessel.id.ToString(),
                strSOIAlarmName, strSOIAlarmNotes, timeSOIAlarm,
                settings.AlarmAutoSOIMargin,
                AlarmTypeEnum.SOIChangeAuto,
                settings.AlarmOnSOIChange_Action));
        }
    }
}
```

**Similar patterns for:**
- `MonitorManNodeOnPath()` (lines 1520-1561) - Automatic maneuver node alarms
- `MonitorContracts()` (lines 1563-1617) - Contract deadline/expiry alarms

---

## 7. PERSISTENCE SYSTEM

**KerbalAlarmClockScenario (KerbalAlarmClock_ScenarioModule.cs lines 13-65):**

```csharp
[KSPScenario(ScenarioCreationOptions.AddToAllGames,
    GameScenes.SPACECENTER, GameScenes.EDITOR,
    GameScenes.FLIGHT, GameScenes.TRACKSTATION)]
internal class KerbalAlarmClockScenario : ScenarioModule
{
    public override void OnLoad(ConfigNode gameNode)
    {
        // Clear existing alarms
        KerbalAlarmClock.alarms.RemoveRange(0, alarms.Count);

        // Load from config node
        if(gameNode.HasNode("KACAlarmListStorage"))
        {
            KerbalAlarmClock.alarms.DecodeFromCN(
                gameNode.GetNode("KACAlarmListStorage"));

            // Convert old alarm action format
            foreach (KACAlarm a in alarms)
            {
                if (!a.AlarmActionConverted)
                {
                    a.AlarmActionConvert = a.AlarmAction;
                    a.AlarmAction = AlarmActionEnum.Converted;
                    a.AlarmActionConverted = true;
                }
            }
        }
    }

    public override void OnSave(ConfigNode gameNode)
    {
        // Encode alarm list to config node
        gameNode.AddNode(KerbalAlarmClock.alarms.EncodeToCN());
    }
}
```

**Serialization Pattern (AlarmObjects.cs lines 424-547):**

```csharp
public override void OnEncodeToConfigNode()
{
    NotesStorage = KACUtils.EncodeVarStrings(Notes);
    AlarmTimeStorage = AlarmTime.UT;
    RepeatAlarmPeriodStorage = RepeatAlarmPeriod.UT;
    ContractGUIDStorage = ContractGUID.ToString();
    TargetObjectStorage = TargetSerialize(TargetObject);
    ManNodesStorage = ManNodeSerializeList(ManNodes);
}

public override void OnDecodeFromConfigNode()
{
    Notes = KACUtils.DecodeVarStrings(NotesStorage);
    AlarmTime = new KSPDateTime(AlarmTimeStorage);

    if (RepeatAlarmPeriodStorage != 0)
        RepeatAlarmPeriod = new KSPTimeSpan(RepeatAlarmPeriodStorage);

    if (ContractGUIDStorage != null && ContractGUIDStorage != "")
        ContractGUID = new Guid(ContractGUIDStorage);

    _TargetObject = TargetDeserialize(TargetObjectStorage);
    ManNodes = ManNodeDeserializeList(ManNodesStorage);
}
```

---

## 8. CROSS-VESSEL ALARM TRACKING

**VesselID Property:**
Each alarm has a `VesselID` field (string representation of Guid) for vessel association.

**Vessel Tracking (KerbalAlarmClock_GameState.cs lines 14-262):**

```csharp
internal static class KACWorkerGameState
{
    internal static Vessel CurrentVessel = null;
    internal static Vessel LastVessel = null;

    internal static Boolean ChangedVessel
    {
        get
        {
            if (LastVessel == null) return true;
            return (LastVessel != CurrentVessel);
        }
    }

    // Vessel changed event
    internal delegate void VesselChangedHandler(Vessel OldVessel, Vessel NewVessel);
    internal static event VesselChangedHandler VesselChanged;
}
```

**Filtering by Vessel:**
```csharp
// Get alarms for specific vessel
var vesselAlarms = alarms.Where(a =>
    a.VesselID == KACWorkerGameState.CurrentVessel.id.ToString());
```

---

## 9. API FOR OTHER MODS TO INTEGRATE

**Public API (API.cs lines 8-141):**

```csharp
public partial class KerbalAlarmClock
{
    public static KerbalAlarmClock APIInstance;
    public static Boolean APIReady = false;

    // Event system
    public event AlarmStateChangedHandler onAlarmStateChanged;
    public delegate void AlarmStateChangedHandler(AlarmStateChangedEventArgs e);

    public enum AlarmStateEventsEnum
    {
        Created,
        Triggered,
        Closed,
        Deleted,
    }

    // Create alarm via API
    public String CreateAlarm(AlarmTypeEnum AlarmType, String Name, Double UT)
    {
        KACAlarm tmpAlarm = new KACAlarm(UT);
        tmpAlarm.TypeOfAlarm = AlarmType;
        tmpAlarm.Name = Name;

        alarms.Add(tmpAlarm);

        return tmpAlarm.ID;
    }

    // Delete alarm via API
    public Boolean DeleteAlarm(String AlarmID)
    {
        KACAlarm tmpAlarm = alarms.FirstOrDefault(a => a.ID == AlarmID);
        if (tmpAlarm != null)
        {
            alarms.Remove(tmpAlarm);
            return true;
        }
        return false;
    }
}
```

**Wrapper for Reflection-based Access (API/KACWrapper.cs):**

Other mods use reflection to avoid hard dependencies:

```csharp
// Initialize wrapper
if (KACWrapper.InitKACWrapper())
{
    if (KACWrapper.APIReady)
    {
        // Access alarms
        KACWrapper.KACAPI.KACAlarmList alarms = KACWrapper.KAC.Alarms;

        // Subscribe to events
        KACWrapper.KAC.onAlarmStateChanged += AlarmStateChanged_Handler;

        // Create alarms
        String alarmID = KACWrapper.KAC.CreateAlarm(
            AlarmTypeEnum.Raw, "Test Alarm", ut);
    }
}
```

---

## 10. UI COMPONENTS FOR ALARM MANAGEMENT

**Window System:**
- `KerbalAlarmClock_Window.cs` - Main alarm list window
- `KerbalAlarmClock_WindowAdd.cs` - Add alarm dialog
- `KerbalAlarmClock_WindowAlarm.cs` - Individual alarm popup when triggered
- `KerbalAlarmClock_WindowSettings.cs` - Settings dialog

**Icon System:**
- App Launcher button integration
- Toolbar button support
- Status indicators (warp influence, triggered alarms)

---

## 11. KEY CLASSES AND THEIR RELATIONSHIPS

```
KerbalAlarmClock (MonoBehaviourExtended)
├── RepeatingWorker() - Main update loop
├── UpdateDetails() - Game state sync
├── ParseAlarmsAndAffectWarpAndPause() - Alarm checking
├── MonitorSOIOnPath() - Auto SOI alarm creation
├── MonitorManNodeOnPath() - Auto maneuver alarm creation
└── MonitorContracts() - Contract alarm management

KACAlarm (ConfigNodeStorage)
├── AlarmTime: KSPDateTime - Event time
├── AlarmMarginSecs: Double - Time before event
├── Remaining: KSPTimeSpan - Time until alarm
├── Triggered: Boolean - Alarm has fired
├── Actioned: Boolean - Alarm handled
└── Actions: AlarmActions - What to do when triggered

KACAlarmList : List<KACAlarm>
├── Add() - Triggers Created event
├── Remove() - Triggers Deleted event
└── EncodeToCN() / DecodeFromCN() - Persistence

KACWorkerGameState (static)
├── CurrentTime: KSPDateTime - Game time
├── CurrentVessel: Vessel - Active vessel
├── CurrentlyUnderWarpInfluence: Boolean
└── VesselChanged event

KerbalAlarmClockScenario : ScenarioModule
├── OnLoad() - Load alarms from save
└── OnSave() - Save alarms to save

WarpTransitionCalculator (static)
├── WarpRateTransitionPeriods - Transition calculations
└── CalcWarpRateTransitions() - Smooth warp changes
```

---

## 12. SPECIFIC CODE PATTERNS FOR PARSEK TIMELINE EVENT MANAGEMENT

### Pattern 1: Time-Based Event Loop
```csharp
// Run at fixed intervals (not every frame)
internal override void RepeatingWorker()
{
    UpdateGameState();

    foreach (var timelineEvent in events)
    {
        // Update remaining time
        timelineEvent.UpdateRemaining(timelineEvent.EventTime.UT - CurrentTime.UT);

        // Check for trigger
        if (timelineEvent.Remaining.UT <= 0 && !timelineEvent.Triggered)
        {
            TriggerEvent(timelineEvent);
        }

        // Gradual warp slowdown before event
        if (ShouldReduceWarp(timelineEvent))
        {
            ReduceWarpGradually();
        }
    }
}
```

### Pattern 2: Lazy String Update
```csharp
// Only update expensive string operations when floor value changes
private double _lastUpdateFloor;
private string _cachedString;

internal void UpdateRemaining(double remaining)
{
    Remaining = remaining;

    if (Math.Floor(remaining) != _lastUpdateFloor)
    {
        _cachedString = FormatTimeSpan(remaining);
        _lastUpdateFloor = Math.Floor(remaining);
    }
}
```

### Pattern 3: ConfigNode Persistence
```csharp
[KSPScenario(ScenarioCreationOptions.AddToAllGames, ...)]
public class TimelineScenario : ScenarioModule
{
    public override void OnSave(ConfigNode node)
    {
        node.AddNode(timeline.EncodeToCN());
    }

    public override void OnLoad(ConfigNode node)
    {
        timeline.DecodeFromCN(node.GetNode("Timeline"));
    }
}
```

### Pattern 4: Event-Driven API
```csharp
public class TimelineAPI
{
    public static TimelineAPI APIInstance;
    public static bool APIReady = false;

    public event EventStateChangedHandler onEventStateChanged;

    public string CreateEvent(EventType type, string name, double ut)
    {
        var evt = new TimelineEvent(ut) { Type = type, Name = name };
        events.Add(evt);
        RaiseEventStateChanged(evt, EventState.Created);
        return evt.ID;
    }
}
```

### Pattern 5: Smooth Warp Transitions
```csharp
// Calculate transition time based on warp rate physics
internal static void CalcWarpTransitions()
{
    for (int i = 0; i < warpRates.Length; i++)
    {
        double utToTransitionDown =
            (warpRates[i] + warpRates[i-1]) / 2;
        transitions[i].UTToRateDown = utToTransitionDown;
    }
}

// Use transition times to smoothly reduce warp
if (eventTime - currentTime < transitionThreshold)
{
    TimeWarp.SetRate(currentRate - 1, false); // false = gradual
}
```

### Pattern 6: Automatic Event Detection
```csharp
private void MonitorForAutoEvents()
{
    // Check game state for events to track
    if (orbit.patchEndTransition == ENCOUNTER)
    {
        double eventTime = orbit.UTsoi;

        // Find or create auto-tracking event
        var existingEvent = FindAutoEvent(vesselId, EventType.SOIChange);

        if (existingEvent != null)
        {
            // Update time if changed significantly
            if (Math.Abs(existingEvent.EventTime - eventTime) > threshold)
                existingEvent.EventTime = eventTime;
        }
        else if (eventTime > currentTime)
        {
            // Create new auto event
            CreateAutoEvent(EventType.SOIChange, eventTime);
        }
    }
}
```

---

## SUMMARY

Kerbal Alarm Clock provides excellent patterns for Parsek's timeline event management, particularly:

1. **UT-based time system** - All events keyed by Universal Time (seconds since epoch)

2. **Efficient update loop** - Fixed-rate checking (0.1s) rather than every frame

3. **Lazy evaluation** - String formatting and UI updates only when values change

4. **Smooth warp control** - Gradual slowdown based on transition calculations

5. **Automatic event detection** - Monitoring game state to create/update events

6. **Event-driven API** - Clean integration pattern for other systems

7. **ScenarioModule persistence** - ConfigNode serialization for save integration

8. **Multi-vessel tracking** - VesselID associations with event filtering

9. **State machine** - Enabled → Triggered → Actioned lifecycle

10. **Margin system** - Events trigger before actual time (configurable margins)

The separation of event storage, monitoring, triggering, and warp control systems provides a robust architecture for deterministic timeline management in Parsek.
