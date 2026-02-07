# ClickThroughBlocker Architecture Analysis
**For Parsek Project - UI Click-Through Prevention Reference**

Based on thorough exploration of the ClickThroughBlocker (CTB) mod source code (v2.1.10.21), this document provides architectural analysis focused on how the mod prevents UI click-through and how Parsek should integrate with it.

---

## 1. PROJECT STRUCTURE AND ORGANIZATION

### File Organization
The project consists of 15 C# files in a single namespace (`ClickThroughFix`):

**Core Files (the mechanism):**
- `ClickThroughBlocker.cs` - Main public API: static class `ClickThruBlocker` with drop-in replacement methods for `GUI.Window`, `GUILayout.Window`, and `GUI.ModalWindow`
- `FocusLock.cs` - Manages KSP input locks (the actual blocking mechanism)
- `CBTMonitor.cs` - Editor-specific workarounds: prevents part selection click-through in the VAB/SPH
- `CBTGlobalMonitor.cs` - Global tick counter and stale window cleanup
- `OnGUILoopCount.cs` - Periodic cleanup of orphaned locks (for Focus-Follows-Mouse mode)

**Settings and UI:**
- `Settings.cs` - `CTB` class: KSP stock settings integration (`GameParameters.CustomParameterNode`)
- `ClearInputLocks.cs` - Toolbar buttons: "Clear All Locks" emergency button and focus mode toggle
- `OneTimePopup.cs` - First-run popup for choosing focus mode
- `RegisterToolbar.cs` - Toolbar registration and global settings propagation

**Infrastructure:**
- `InstallChecker.cs` - Validates correct installation path (`000_ClickThroughBlocker/Plugins`)
- `SceneChangeCleanup.cs` - Clears all input locks on scene transitions (prevents stuck locks)
- `Log.cs` - Internal logging with level support (OFF/ERROR/WARNING/INFO/DETAIL/TRACE)
- `GlobalFlagStorage.cs` - Disabled (`#if false`), unused config storage
- `AssemblyVersion.cs` - Auto-generated version info
- `Properties/AssemblyInfo.cs` - Assembly metadata

### Build Configuration
- Namespace: `ClickThroughFix`
- Assembly name: `ClickThroughBlocker`
- Target: .NET Framework 4.7.2
- Dependencies: KSP assemblies, Unity assemblies, `ToolbarControl`
- Has a `DUMMY` build configuration that compiles out all blocking logic (for distribution as a no-op stub)
- Installed at: `GameData/000_ClickThroughBlocker/` (the `000_` prefix ensures early load order)

---

## 2. CORE MECHANISM: HOW CLICK-THROUGH BLOCKING WORKS

### The Problem
In KSP, Unity IMGUI windows (the old `OnGUI` system) do not block mouse input from reaching the game underneath. When a player clicks a button in a mod's window, that click also registers as a game input -- selecting parts in the editor, controlling the vessel in flight, etc.

### The Solution: Drop-In Window Wrappers

CTB provides static methods on `ClickThruBlocker` that are 1:1 replacements for Unity's `GUI.Window()` and `GUILayout.Window()`. These wrappers:

1. **Call the real Unity window method** to draw the window
2. **Track the window** in an internal dictionary (`winList`) keyed by window ID
3. **Check if the mouse is over the window** on every frame
4. **Set KSP input locks** when the mouse is over a mod window
5. **Release the locks** when the mouse leaves

### The Input Lock Mechanism

The actual blocking is done through two KSP systems, chosen based on current scene:

**In the Editor (VAB/SPH):**
```csharp
EditorLogic.fetch.Lock(true, true, true, lockName);
// Locks: soft lock on parts, soft lock on rotation, soft lock on place
```

**In Flight / Map View:**
```csharp
InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, lockName);
// Blocks all input except camera movement
```

This is handled by `FocusLock.SetLock()` and `FocusLock.FreeLock()`, which centralize the lock/unlock logic and maintain a dictionary of active locks.

### Two Focus Modes

CTB supports two modes, toggled via the settings or toolbar button:

**Focus-Follows-Mouse (default):**
- Locks engage when the mouse cursor enters the window rect
- Locks release when the mouse cursor leaves the window rect
- No click required -- just hovering over a window blocks game input

**Focus-Follows-Click:**
- Locks engage only when the user clicks inside the window
- Locks release only when the user clicks outside the window
- More traditional desktop-style focus behavior

The mode is stored in `ClearInputLocks.focusFollowsclick` (static bool) and persisted through `HighLogic.CurrentGame.Parameters.CustomParams<CTB>().focusFollowsclick`.

### Mouse Hit Testing

```csharp
public static bool MouseIsOverWindow(Rect rect)
{
    mousePos.x = Input.mousePosition.x;
    mousePos.y = Screen.height - Input.mousePosition.y;  // Unity Y is bottom-up, GUI Y is top-down
    return rect.Contains(mousePos);
}
```

### Window Lifecycle and Cleanup

Windows are tracked by their Unity window ID in `winList`. Several cleanup mechanisms prevent stale locks:

1. **`CBTGlobalMonitor.FixedUpdate()`** - Increments a global tick counter. In Focus-Follows-Click mode, destroys windows that have not been updated for 4+ ticks.
2. **`CBTMonitor.LateUpdate()`** - In Focus-Follows-Mouse mode, removes windows that have not been updated for 4+ ticks.
3. **`OnGUILoopCount`** - Periodic check (every 0.25s) that unlocks windows whose `lastLockCycle` has fallen behind, handling cases where `OnGUI` stops being called for a window.
4. **`SceneChangeCleanup`** - Clears all input locks on scene transitions with a configurable delay (default 0.5s).
5. **`CTBWin.OnDestroy()`** - Removes the window from the list and releases any locks it held.

---

## 3. KEY CLASSES AND PUBLIC API

### `ClickThruBlocker` (public static class)
**Location:** `ClickThroughBlocker.cs`
**Namespace:** `ClickThroughFix`

This is the only class consuming mods need to interact with. It provides static drop-in replacements:

**GUILayout.Window replacements:**
```csharp
// All signatures match GUILayout.Window exactly
public static Rect GUILayoutWindow(int id, Rect screenRect, GUI.WindowFunction func, string text, params GUILayoutOption[] options)
public static Rect GUILayoutWindow(int id, Rect screenRect, GUI.WindowFunction func, string text, GUIStyle style, params GUILayoutOption[] options)
public static Rect GUILayoutWindow(int id, Rect screenRect, GUI.WindowFunction func, GUIContent content, params GUILayoutOption[] options)
public static Rect GUILayoutWindow(int id, Rect screenRect, GUI.WindowFunction func, Texture image, params GUILayoutOption[] options)
public static Rect GUILayoutWindow(int id, Rect screenRect, GUI.WindowFunction func, GUIContent content, GUIStyle style, params GUILayoutOption[] options)
public static Rect GUILayoutWindow(int id, Rect screenRect, GUI.WindowFunction func, Texture image, GUIStyle style, params GUILayoutOption[] options)
```

**GUI.Window replacements:**
```csharp
public static Rect GUIWindow(int id, Rect clientRect, GUI.WindowFunction func, string text)
public static Rect GUIWindow(int id, Rect clientRect, GUI.WindowFunction func, string text, GUIStyle style)
public static Rect GUIWindow(int id, Rect clientRect, GUI.WindowFunction func, GUIContent content)
public static Rect GUIWindow(int id, Rect clientRect, GUI.WindowFunction func, Texture image)
public static Rect GUIWindow(int id, Rect clientRect, GUI.WindowFunction func, Texture image, GUIStyle style)
public static Rect GUIWindow(int id, Rect clientRect, GUI.WindowFunction func, GUIContent title, GUIStyle style)
```

**GUI.ModalWindow replacements:**
```csharp
public static Rect GUIModalWindow(int id, Rect clientRect, GUI.WindowFunction func, string text)
public static Rect GUIModalWindow(int id, Rect clientRect, GUI.WindowFunction func, Texture image)
public static Rect GUIModalWindow(int id, Rect clientRect, GUI.WindowFunction func, GUIContent content)
public static Rect GUIModalWindow(int id, Rect clientRect, GUI.WindowFunction func, string text, GUIStyle style)
public static Rect GUIModalWindow(int id, Rect clientRect, GUI.WindowFunction func, Texture image, GUIStyle style)
public static Rect GUIModalWindow(int id, Rect clientRect, GUI.WindowFunction func, GUIContent content, GUIStyle style)
```

### `CTBWin` (public nested class)
**Location:** Inside `ClickThruBlocker`

Internal tracking object for each window. One instance per unique window ID. Contains:
- `id` - The Unity window ID
- `windowName` / `lockName` - Used for the KSP input lock identifier
- `weLockedEditorInputs` / `weLockedFlightInputs` - Whether this window currently holds a lock
- `lastUpdated` - Tick count of last frame this window was drawn
- `lastLockCycle` - OnGUI loop count of last lock operation

### `FocusLock` (internal class)
**Location:** `FocusLock.cs`

Centralizes the actual KSP lock/unlock operations. Two static methods:
- `SetLock(lockName, win, debugId)` - Sets `EditorLogic.Lock` or `InputLockManager.SetControlLock`
- `FreeLock(lockName, debugId)` - Removes the lock

### `CTB` (public class, GameParameters.CustomParameterNode)
**Location:** `Settings.cs`

KSP stock settings integration. Exposes:
- `showPopup` - Whether to show the one-time focus mode selection popup
- `focusFollowsclick` - Focus mode toggle
- `global` - Whether focus setting applies to all saves
- `cleanupDelay` - Delay before clearing locks on scene change (0.1-5.0s, default 0.5s)

---

## 4. INTEGRATION PATTERNS: HOW MODS USE CTB

### Basic Integration (What FMRS Does)

The integration is a simple find-and-replace pattern. FMRS uses CTB in `FMRS_Core_GUI.cs`:

**Step 1: Add the using directive**
```csharp
using ClickThroughFix;
```

**Step 2: Replace `GUILayout.Window` with `ClickThruBlocker.GUILayoutWindow`**

Before (vanilla Unity):
```csharp
windowPos = GUILayout.Window(windowID, windowPos, MainGUI, "FMRS", GUILayout.MinWidth(100));
```

After (with CTB):
```csharp
windowPos = ClickThruBlocker.GUILayoutWindow(windowID, windowPos, MainGUI, "FMRS", GUILayout.MinWidth(100));
```

The method signatures are identical -- same parameters, same return type. Only the class name changes.

**Step 3: Add assembly dependency in AssemblyInfo.cs** (optional but recommended)
```csharp
[assembly: KSPAssemblyDependency("ClickThroughBlocker", 1, 0)]
```

### Complete FMRS Integration Example

```csharp
using ClickThroughFix;

namespace FMRS
{
    public partial class FMRS_Core : FMRS_Util, IFMRS
    {
        int baseWindowID;

        private void Start()
        {
            // Generate a unique window ID range
            baseWindowID = UnityEngine.Random.Range(1000, 2000000) + _AssemblyName.GetHashCode();
        }

        public void drawGUI()
        {
            GUI.skin = HighLogic.Skin;

            if (main_ui_active)
            {
                // Drop-in replacement -- exact same call as GUILayout.Window
                windowPos = ClickThruBlocker.GUILayoutWindow(
                    baseWindowID + 1,
                    windowPos,
                    MainGUI,
                    "FMRS",
                    GUILayout.MinWidth(100)
                );

                // Clamp to screen bounds (standard pattern)
                windowPos.x = Mathf.Clamp(windowPos.x, 0, Screen.width - windowPos.width);
                windowPos.y = Mathf.Clamp(windowPos.y, 0, Screen.height - windowPos.height);
            }
        }

        public void MainGUI(int windowID)
        {
            // Normal IMGUI code -- no CTB-specific logic needed inside the window function
            GUILayout.BeginVertical();
            // ... window contents ...
            GUILayout.EndVertical();
            GUI.DragWindow();
        }
    }
}
```

### How Other Mods Use It

**StageRecovery** (multiple windows):
```csharp
flightWindowRect = ClickThruBlocker.GUILayoutWindow(8940, flightWindowRect, DrawFlightGUI, "StageRecovery", HighLogic.Skin.window);
blacklistRect = ClickThruBlocker.GUILayoutWindow(8941, blacklistRect, DrawBlacklistGUI, "Ignore List", HighLogic.Skin.window);
```

**ToolbarControl** (the toolbar system itself uses CTB):
```csharp
WindowRect = ClickThruBlocker.GUILayoutWindow(4946386, WindowRect, DoWindow, "Toolbar Controller", windowStyle);
```

**CTB's own OneTimePopup** (even CTB uses itself):
```csharp
popupRect = ClickThruBlocker.GUILayoutWindow(84733455, popupRect, PopUpWindow, "Click Through Blocker Focus Setting");
```

---

## 5. ARCHITECTURAL PATTERNS AND DESIGN DECISIONS

### Pattern: Transparent Wrapper with Side Effects
CTB wraps Unity API calls with identical signatures, adding lock management as a side effect. Consuming mods do not need to understand the internals -- they just change which class they call.

### Pattern: Global Tick-Based Staleness Detection
Rather than requiring mods to explicitly register/unregister windows, CTB uses a monotonically increasing tick counter (`CBTGlobalMonitor.globalTimeTics`). Each time a window is drawn, its `lastUpdated` is set to the current tick. Windows that fall behind by 4+ ticks are assumed closed and cleaned up automatically.

This is important because Unity IMGUI windows have no explicit lifecycle -- you just stop calling `GUILayout.Window()` when you want them gone. CTB needs to detect this absence to release locks.

### Pattern: Scene Transition Cleanup
`SceneChangeCleanup` listens for three events to clear all input locks:
1. `onGameSceneLoadRequested` - Immediate clear
2. `onGUIApplicationLauncherReady` - Delayed clear (coroutine with configurable wait)
3. `onLevelWasLoadedGUIReady` - Delayed clear

This prevents locks from persisting across scene changes, which would leave the player unable to interact with the game.

### Pattern: Editor Bug Workaround
`CBTMonitor` works around a KSP stock bug where the Action Groups pane in the editor ignores input locks. It does this by:
1. Caching `EditorActionPartSelector` components for all parts
2. When a window has an active lock, forcibly deselecting all parts and clearing the action group selection
3. Re-selecting only the parts that were selected before the lock

### Conditional Compilation
CTB uses `#if !DUMMY` guards around nearly all functional code. The `DUMMY` build configuration produces a DLL with all the same public method signatures but no implementation. This allows mods that depend on CTB to compile against a stub when the real CTB is not available.

---

## 6. PATTERNS USEFUL FOR PARSEK'S UI LAYER

### Recommendation 1: Use CTB for All Parsek Windows
Since CTB is already installed in our test environment and is a standard KSP mod dependency, Parsek should use `ClickThruBlocker.GUILayoutWindow` for all IMGUI windows. The integration cost is essentially zero -- just change the class name in the window call.

**For Parsek, this means:**
```csharp
using ClickThroughFix;

// In Parsek's UI class:
windowRect = ClickThruBlocker.GUILayoutWindow(
    parsekWindowId,
    windowRect,
    DrawParsekWindow,
    "Parsek - Mission Recorder"
);
```

### Recommendation 2: Window ID Generation
Follow the FMRS pattern for generating unique window IDs that will not collide with other mods:
```csharp
int windowId = UnityEngine.Random.Range(1000, 2000000) + assemblyName.GetHashCode();
```

If Parsek has multiple windows, use offsets from a base ID:
```csharp
int baseId = UnityEngine.Random.Range(1000, 2000000) + "Parsek".GetHashCode();
int mainWindowId = baseId + 1;
int settingsWindowId = baseId + 2;
int playbackWindowId = baseId + 3;
```

### Recommendation 3: Declare the Dependency
Add to Parsek's assembly attributes:
```csharp
[assembly: KSPAssemblyDependency("ClickThroughBlocker", 1, 0)]
```

And reference the DLL from our test KSP installation:
```xml
<Reference Include="ClickThroughBlocker">
    <HintPath>..\..\Kerbal Space Program\GameData\000_ClickThroughBlocker\Plugins\ClickThroughBlocker.dll</HintPath>
    <Private>false</Private>
</Reference>
```

### Recommendation 4: No Additional Lock Management Needed
CTB handles everything automatically. Parsek does NOT need to:
- Call `InputLockManager` directly for UI windows
- Track window open/close state for lock purposes
- Handle scene transitions for lock cleanup
- Implement mouse-over detection

Just use `ClickThruBlocker.GUILayoutWindow` instead of `GUILayout.Window` and the blocking works.

### Recommendation 5: Screen Boundary Clamping
Follow the standard pattern after the window call to prevent windows from being dragged off-screen:
```csharp
windowRect.x = Mathf.Clamp(windowRect.x, 0, Screen.width - windowRect.width);
windowRect.y = Mathf.Clamp(windowRect.y, 0, Screen.height - windowRect.height);
```

### Recommendation 6: Know the ControlTypes Lock
In flight mode, CTB locks `ControlTypes.ALLBUTCAMERAS`. This means:
- Vessel throttle, staging, SAS, RCS -- all blocked while mouse is over a Parsek window
- Camera rotation/zoom still works -- players can look around while interacting with UI
- This is the correct behavior for Parsek's recording/playback controls

---

## SUMMARY

ClickThroughBlocker is a community infrastructure mod that solves the universal KSP IMGUI click-through problem. Its architecture is a transparent wrapper pattern: mods replace `GUILayout.Window()` calls with `ClickThruBlocker.GUILayoutWindow()` (identical signatures), and CTB automatically manages KSP input locks based on mouse position.

The key components are:
1. **`ClickThruBlocker`** - Public static API with drop-in window method replacements
2. **`FocusLock`** - Centralized KSP input lock management (EditorLogic / InputLockManager)
3. **`CTBWin`** - Per-window tracking: lock state, staleness detection, cleanup
4. **`CBTMonitor` / `CBTGlobalMonitor`** - Background cleanup of stale windows and locks
5. **`SceneChangeCleanup`** - Prevents locks from persisting across scene transitions

For Parsek integration:
- Add `using ClickThroughFix;`
- Replace `GUILayout.Window` with `ClickThruBlocker.GUILayoutWindow`
- Add `[assembly: KSPAssemblyDependency("ClickThroughBlocker", 1, 0)]`
- Reference `ClickThroughBlocker.dll` in the project
- No other changes needed -- the API is intentionally invisible
