# ToolbarControl Architecture Analysis
**For Parsek Project - Toolbar Button Integration Reference**

Based on thorough exploration of the ToolbarControl mod (v0.1.9.14), this document provides detailed architectural analysis focused on how to register and manage toolbar buttons in KSP1 mods. ToolbarControl is a unified abstraction layer that lets mods support both the Stock ApplicationLauncher toolbar and the Blizzy Toolbar with a single API.

---

## 1. PROJECT STRUCTURE AND ORGANIZATION

### File Organization
The project consists of 12 C# source files in a single namespace `ToolbarControl_NS`:

**Core Files:**
- `ToolbarControl.cs` - Main public API class (partial) - button creation, lifecycle, rendering
- `RegisterUsage.cs` - Mod registration system and persistence (partial class of ToolbarControl)
- `ToolbarWrapper.cs` - Blizzy Toolbar reflection wrapper (avoids hard dependency)

**UI/Configuration Files:**
- `BlizzyOptions.cs` - Options GUI window for per-mod toolbar selection
- `Settings.cs` - KSP GameParameters integration (tooltip settings, debug mode)
- `IntroWindow.cs` - First-run help/intro window
- `ConfigInfo.cs` - Debug configuration persistence

**Support Files:**
- `RegisterToolbar.cs` - Self-registration of ToolbarControl's own button
- `InstallChecker.cs` - Installation validation + version logging
- `Log.cs` - Internal logging utility
- `AssemblyVersion.cs` - Auto-generated version info
- `Properties/AssemblyInfo.cs` - Assembly metadata + KSP assembly declarations

### Dependencies
- **ClickThroughBlocker** - Required dependency (declared via `KSPAssemblyDependency`)
- **Blizzy Toolbar** - Optional (accessed via reflection, no hard dependency)
- **KSP Stock API** - `ApplicationLauncher`, `GameEvents`, Unity `MonoBehaviour`

### Architectural Pattern
ToolbarControl uses a **partial class architecture** where the `ToolbarControl` class is split across two files:
1. `ToolbarControl.cs` - Public API, button lifecycle, icon management, tooltip rendering
2. `RegisterUsage.cs` - Static mod registry, config file persistence, registration API

The mod acts as a **facade pattern** over two toolbar implementations, abstracting away the complexity of supporting both toolbars.

---

## 2. HOW TOOLBAR BUTTON REGISTRATION WORKS

### Two-Phase Registration

Button creation happens in two distinct phases that must both be completed:

**Phase 1: Static Mod Registration (at startup)**
```csharp
// Called from a KSPAddon(Startup.Instantly) or similar early addon
ToolbarControl.RegisterMod(MODID, MODNAME, useBlizzy: false, useStock: true, NoneAllowed: true);
```

This registers the mod's metadata into a global `Dictionary<string, Mod>` called `registeredMods`. The registration:
- Loads any saved user preferences from `GameData/001_ToolbarControl/PluginData/ToolbarControl.cfg`
- If the mod was previously registered, restores the user's toolbar preference (stock/blizzy/both/none)
- If new, uses the provided defaults
- Saves the updated registry immediately

**Phase 2: Button Instance Creation (at scene load)**
```csharp
// Called from your mod's Start() or scene-appropriate lifecycle method
toolbarControl = gameObject.AddComponent<ToolbarControl>();
toolbarControl.AddToAllToolbars(
    onTrue, onFalse,
    ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW,
    MODID,           // Must match Phase 1 registration
    "myButtonId",    // Unique button ID
    "MyMod/Textures/icon_38",   // Large icon (stock toolbar, 38x38)
    "MyMod/Textures/icon_24",   // Small icon (Blizzy toolbar, 24x24)
    "My Button Tooltip"
);
```

This creates the actual button instance and links it back to the registered mod via the `nameSpace` (MODID) key.

### Registration Timing Warning
The mod includes a warning if `RegisterMod` is called too late:
```csharp
if (BlizzyOptions.startupCompleted && ConfigInfo.debugMode) {
    Log.Error("WARNING: RegisterMod called too late for: " + NameSpace);
}
```
Registration should happen in `KSPAddon.Startup.Instantly` to ensure it runs before the options UI starts.

---

## 3. KEY CLASSES AND PUBLIC API

### A. ToolbarControl (Main Class)
**Location:** `ToolbarControl.cs` + `RegisterUsage.cs`
**Base Class:** `MonoBehaviour` (added as a component to your mod's GameObject)

**Primary API Methods:**

| Method | Purpose |
|--------|---------|
| `RegisterMod(nameSpace, displayName, useBlizzy, useStock, noneAllowed)` | Static. Phase 1 registration. |
| `AddToAllToolbars(onTrue, onFalse, scenes, nameSpace, toolbarId, largeIcon, smallIcon, toolTip)` | Phase 2 instance creation. Multiple overloads available. |
| `OnDestroy()` | Cleanup. Must be called when your mod is destroyed. |
| `SetTexture(large, small)` | Change button icon at runtime. |
| `SetTrue(makeCall)` | Programmatically set button to active state. |
| `SetFalse(makeCall)` | Programmatically set button to inactive state. |
| `AddLeftRightClickCallbacks(onLeftClick, onRightClick)` | Add separate left/right click handlers. |
| `EnableMutuallyExclusive()` / `DisableMutuallyExclusive()` | Toggle mutual exclusivity with other stock buttons. |

**Key Properties:**

| Property | Type | Purpose |
|----------|------|---------|
| `Enabled` | bool | Enable/disable button clickability |
| `ToolTip` | string | Get/set tooltip text |
| `buttonActive` | bool | Current toggle state (public field) |
| `buttonClickedMousePos` | Vector2 | Mouse position at last click |
| `StockPosition` | Rect? | Screen position of stock button |

**Static Query Methods:**

| Method | Purpose |
|--------|---------|
| `BlizzyActive(nameSpace)` | Check if a mod is using Blizzy toolbar |
| `StockActive(nameSpace)` | Check if a mod is using stock toolbar |
| `ButtonsActive(nameSpace, useStock, useBlizzy)` | Set which toolbar(s) to use |
| `IsStockButtonManaged(button, out ns, out id, out tip)` | Check if a stock button belongs to ToolbarControl |

### B. ToolbarManager (Blizzy Wrapper)
**Location:** `ToolbarWrapper.cs`

A reflection-based wrapper that avoids a hard compile-time dependency on Blizzy's Toolbar mod. Uses `AssemblyLoader.loadedAssemblies.TypeOperation` to discover types at runtime.

**Key Static Members:**
```csharp
ToolbarManager.ToolbarAvailable  // bool - Is Blizzy's toolbar installed?
ToolbarManager.Instance          // IToolbarManager - The Blizzy toolbar manager
```

### C. IButton Interface (Blizzy Button)
**Location:** `ToolbarWrapper.cs`

Defines the Blizzy button interface. Properties include: `Text`, `TextColor`, `TexturePath`, `BigTexturePath`, `ToolTip`, `Visible`, `Visibility`, `Enabled`, `Important`, `Drawable`. Events: `OnClick`, `OnMouseEnter`, `OnMouseLeave`.

### D. TC (Settings)
**Location:** `Settings.cs`

KSP `GameParameters.CustomParameterNode` that adds "Toolbar Control" settings to KSP's difficulty options:
- Show tooltips for stock & Blizzy toolbar
- Tooltip timeout (0.5-5.0 seconds)
- Allow Toolbar Control button to be hidden
- Debug mode

### E. Internal Mod Class
**Location:** `RegisterUsage.cs`

```csharp
internal class Mod {
    public string modId;            // Unique namespace identifier
    public string displayName;      // Human-readable name for options GUI
    public bool useBlizzy;          // User preference: show on Blizzy toolbar
    public bool useStock;           // User preference: show on stock toolbar
    public bool noneAllowed;        // Whether hiding the button is allowed
    public ToolbarControl modToolbarControl;  // Reference to live button instance
    public bool registered;         // Whether Phase 1 registration completed
}
```

---

## 4. HOW TO REGISTER A BUTTON FOR BOTH STOCK AND BLIZZY TOOLBARS

### Complete Integration Pattern

**Step 1: Create a registration addon (runs once at startup):**

```csharp
[KSPAddon(KSPAddon.Startup.Instantly, true)]
public class ParsekToolbarRegistration : MonoBehaviour
{
    void Start()
    {
        ToolbarControl.RegisterMod(
            ParsekMainMod.MODID,      // Unique mod identifier string
            ParsekMainMod.MODNAME,    // Display name in options
            useBlizzy: false,         // Default: don't use Blizzy
            useStock: true,           // Default: use stock toolbar
            NoneAllowed: true         // Allow user to hide button
        );
    }
}
```

**Step 2: Create and manage the button in your main mod:**

```csharp
[KSPAddon(KSPAddon.Startup.Flight, false)]
public class ParsekMainMod : MonoBehaviour
{
    internal const string MODID = "Parsek_NS";
    internal const string MODNAME = "Parsek";

    ToolbarControl toolbarControl;

    void Start()
    {
        toolbarControl = gameObject.AddComponent<ToolbarControl>();
        toolbarControl.AddToAllToolbars(
            OnToolbarOn,              // Called when button toggled ON
            OnToolbarOff,             // Called when button toggled OFF
            ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW,
            MODID,                    // Must match RegisterMod call
            "parsekButton",           // Unique button ID within namespace
            "Parsek/Textures/parsek_38",    // 38x38 icon for stock toolbar
            "Parsek/Textures/parsek_24",    // 24x24 icon for Blizzy toolbar
            MODNAME                   // Tooltip text
        );
    }

    void OnToolbarOn()
    {
        // Show your GUI / start recording
    }

    void OnToolbarOff()
    {
        // Hide your GUI / stop recording
    }

    void OnDestroy()
    {
        toolbarControl.OnDestroy();
        Destroy(toolbarControl);
    }
}
```

### AddToAllToolbars Overloads

There are four overloads with increasing specificity:

**1. Simple (same icon for active/inactive):**
```csharp
AddToAllToolbars(onTrue, onFalse, scenes, nameSpace, toolbarId,
    largeIcon,           // Stock (38x38)
    smallIcon,           // Blizzy (24x24)
    toolTip)
```

**2. Separate active/inactive icons:**
```csharp
AddToAllToolbars(onTrue, onFalse, scenes, nameSpace, toolbarId,
    largeIconActive,     // Stock active
    largeIconInactive,   // Stock inactive
    smallIconActive,     // Blizzy active
    smallIconInactive,   // Blizzy inactive
    toolTip)
```

**3. All callbacks, simple icons:**
```csharp
AddToAllToolbars(onTrue, onFalse, onHover, onHoverOut, onEnable, onDisable,
    scenes, nameSpace, toolbarId, largeIcon, smallIcon, toolTip)
```

**4. Full (all callbacks + separate active/inactive icons):**
```csharp
AddToAllToolbars(onTrue, onFalse, onHover, onHoverOut, onEnable, onDisable,
    scenes, nameSpace, toolbarId,
    largeIconActive, largeIconInactive,
    smallIconActive, smallIconInactive,
    toolTip)
```

### Icon Requirements

- **Stock toolbar icons**: 38x38 pixels (loaded from `GameData/` relative path)
- **Blizzy toolbar icons**: 24x24 pixels
- **File formats**: PNG, JPG, GIF, or DDS (the mod tries multiple suffixes automatically)
- **Path format**: Relative to `GameData/`, no file extension needed
  - Example: `"Parsek/Textures/icon_38"` resolves to `GameData/Parsek/Textures/icon_38.png`
- The texture loader first tries loading from the filesystem directly, then falls back to `GameDatabase.Instance.GetTexture()`

### AppScenes Flags (Bitmask)

```csharp
ApplicationLauncher.AppScenes.FLIGHT        // In-flight view
ApplicationLauncher.AppScenes.MAPVIEW       // Map view
ApplicationLauncher.AppScenes.SPACECENTER   // Space center
ApplicationLauncher.AppScenes.VAB           // Vehicle Assembly Building
ApplicationLauncher.AppScenes.SPH           // Spaceplane Hangar
ApplicationLauncher.AppScenes.TRACKSTATION  // Tracking station
ApplicationLauncher.AppScenes.MAINMENU      // Main menu
ApplicationLauncher.AppScenes.NEVER         // Never visible
```

Combine with `|` for multiple scenes.

---

## 5. INTERNAL MECHANICS

### Button State Machine
The button operates as a toggle with two states:

```
buttonActive = false (inactive)
    |
    | User clicks -> onTrue() fires, icon changes to Active variant
    v
buttonActive = true (active)
    |
    | User clicks -> onFalse() fires, icon changes to Inactive variant
    v
buttonActive = false (inactive)
```

For Blizzy toolbar, clicks are handled via `button_Click(ClickEvent e)` which also supports right-click via `AddLeftRightClickCallbacks`.

### Scene Visibility
ToolbarControl implements `TC_GameScenesVisibility : IVisibility` for the Blizzy toolbar. This maps `ApplicationLauncher.AppScenes` flags to `HighLogic.LoadedScene` checks, including special handling for `MapView.MapIsEnabled` (distinguishing FLIGHT from MAPVIEW).

### Configuration Persistence
User preferences are saved to `GameData/001_ToolbarControl/PluginData/ToolbarControl.cfg` using KSP's `ConfigNode` system. Each registered mod gets a DATA node storing:
- `name` (mod namespace ID)
- `displayName`
- `useBlizzy` (bool)
- `useStock` (bool)
- `noneAllowed` (bool)

### Blizzy Toolbar Detection
The wrapper uses reflection to avoid a compile-time dependency:
```csharp
Type type = ToolbarTypes.getType("Toolbar.ToolbarManager");
// Uses AssemblyLoader.loadedAssemblies.TypeOperation to search loaded assemblies
```
If Blizzy's toolbar is not installed, `ToolbarManager.ToolbarAvailable` returns false and all buttons fall back to stock.

---

## 6. INTEGRATION PATTERNS USEFUL FOR PARSEK

### Pattern 1: Simple Toggle Button (Record/Stop)

For Parsek's primary use case -- toggling recording on/off:

```csharp
// Registration addon (Startup.Instantly, runs once)
[KSPAddon(KSPAddon.Startup.Instantly, true)]
public class ParsekToolbarRegistration : MonoBehaviour
{
    void Start()
    {
        ToolbarControl.RegisterMod("Parsek_NS", "Parsek");
    }
}

// Flight addon
[KSPAddon(KSPAddon.Startup.Flight, false)]
public class ParsekFlight : MonoBehaviour
{
    ToolbarControl toolbarControl;

    void Start()
    {
        toolbarControl = gameObject.AddComponent<ToolbarControl>();
        toolbarControl.AddToAllToolbars(
            StartRecording, StopRecording,
            ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW,
            "Parsek_NS", "parsekMainButton",
            "Parsek/Textures/parsek_record_38",    // Active = recording
            "Parsek/Textures/parsek_idle_38",       // Inactive = idle
            "Parsek/Textures/parsek_record_24",
            "Parsek/Textures/parsek_idle_24",
            "Parsek Mission Recorder"
        );
    }

    void OnDestroy()
    {
        toolbarControl.OnDestroy();
        Destroy(toolbarControl);
    }
}
```

### Pattern 2: Dynamic Icon Changes

Change icon to reflect state (idle/recording/playing):
```csharp
// Switch to playback icon at runtime
toolbarControl.SetTexture("Parsek/Textures/parsek_play_38", "Parsek/Textures/parsek_play_24");

// Reset to default icon management (uses active/inactive based on toggle state)
toolbarControl.SetTexture("", "");
```

### Pattern 3: Programmatic State Control

```csharp
// Force button to active state (e.g., when recording starts via hotkey)
toolbarControl.SetTrue(makeCall: true);   // true = fire onTrue callback

// Force button to inactive state
toolbarControl.SetFalse(makeCall: false);  // false = don't fire onFalse callback
```

### Pattern 4: Cleanup on Destroy (Critical)

Always clean up in `OnDestroy()`:
```csharp
void OnDestroy()
{
    if (toolbarControl != null)
    {
        toolbarControl.OnDestroy();
        Destroy(toolbarControl);
        toolbarControl = null;
    }
}
```

Failure to call `OnDestroy()` will leave orphaned buttons in the stock toolbar and leak entries in the internal `tcList`.

### Key Integration Notes for Parsek

1. **Dependency**: Parsek must list ToolbarControl as a dependency. It installs to `GameData/001_ToolbarControl/`. The `ClickThroughBlocker` mod (`GameData/000_ClickThroughBlocker/`) is also required.

2. **No hard dependency on Blizzy**: ToolbarControl itself handles the Blizzy reflection wrapper. Parsek only needs to reference `ToolbarControl.dll` -- never Blizzy's toolbar directly.

3. **MODID consistency**: The string passed to `RegisterMod()` must exactly match the `nameSpace` parameter passed to `AddToAllToolbars()`. This is the linking key.

4. **Registration timing**: `RegisterMod` should be called from `KSPAddon.Startup.Instantly` to run before the ToolbarControl options UI initializes at `MainMenu`.

5. **Icon paths**: Relative to `GameData/`, without file extension. The loader tries `.png`, `.jpg`, `.gif`, `.dds` suffixes automatically.

6. **Scene lifecycle**: Since Parsek uses `KSPAddon.Startup.Flight`, the button is created per flight scene. `OnDestroy` handles cleanup when leaving the scene.

7. **User choice**: Players can override which toolbar(s) display Parsek's button through ToolbarControl's options GUI. The mod simply needs to register and ToolbarControl handles the rest.

---

## SUMMARY

ToolbarControl provides a clean abstraction layer for KSP1 toolbar buttons with these key components:

1. **Two-Phase Registration** - Static `RegisterMod` at startup, then `AddToAllToolbars` at scene load
2. **Dual Toolbar Support** - Stock `ApplicationLauncher` and Blizzy Toolbar via reflection wrapper
3. **User Preferences** - Per-mod selection of stock/blizzy/both/none, persisted to config
4. **Toggle Button Model** - onTrue/onFalse callbacks with active/inactive icon states
5. **Runtime Icon Control** - `SetTexture()` for dynamic icon changes, `SetTrue()`/`SetFalse()` for programmatic state

For Parsek, the integration is straightforward:
- Add ToolbarControl + ClickThroughBlocker as dependencies
- Create one `Startup.Instantly` addon for `RegisterMod`
- Add `ToolbarControl` component in the flight addon's `Start()`
- Provide 38x38 and 24x24 icons for active/inactive states
- Call `OnDestroy()` + `Destroy()` on cleanup
- Optionally use `SetTexture()` to reflect recording/playback/idle states
