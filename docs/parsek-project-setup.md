# Parsek KSP1 Mod - Project Setup Guide

## Overview

This guide covers the modern approach to setting up a KSP1 mod project, combining the **KSPBuildTools** NuGet-based build system with patterns from well-maintained mods like FMRS.

---

## 1. Recommended Project Structure

```
Parsek/
├── .github/
│   └── workflows/
│       └── build.yml                 # GitHub Actions CI/CD
├── GameData/
│   └── Parsek/
│       ├── Plugins/
│       │   └── (Parsek.dll)          # Built output goes here
│       ├── PluginData/
│       │   └── settings.cfg          # User settings (excluded from version control)
│       ├── Textures/
│       │   ├── toolbar_24.png        # Stock toolbar icon
│       │   └── toolbar_38.png        # Blizzy toolbar icon
│       └── Parsek.version            # KSP-AVC version file
├── Source/
│   ├── Parsek/
│   │   ├── Core/
│   │   │   ├── MainTimeline.cs       # ScenarioModule - timeline management
│   │   │   ├── TimeController.cs     # Time warp and event dispatch
│   │   │   └── PersistenceManager.cs # Save/load handling
│   │   ├── Recording/
│   │   │   ├── MissionRecorder.cs    # VesselModule - captures events
│   │   │   ├── MissionRecording.cs   # Data structure for recordings
│   │   │   ├── TrajectoryFrame.cs    # Position/rotation snapshot
│   │   │   └── TimelineEvent.cs      # Base event class
│   │   ├── Playback/
│   │   │   ├── PlaybackEngine.cs     # Manages playback vessels
│   │   │   ├── PlaybackVessel.cs     # Individual ghost vessel
│   │   │   └── KinematicPlayer.cs    # Position interpolation
│   │   ├── UI/
│   │   │   ├── RecordingWindow.cs    # Recording controls
│   │   │   ├── TimelineViewer.cs     # Visual timeline
│   │   │   ├── ToolbarButton.cs      # Toolbar integration
│   │   │   └── Styles.cs             # GUI styling
│   │   ├── Events/
│   │   │   ├── StagingEvent.cs
│   │   │   ├── TrajectoryUpdateEvent.cs
│   │   │   └── SOIChangeEvent.cs
│   │   ├── Utils/
│   │   │   ├── Logger.cs             # Logging wrapper
│   │   │   ├── Extensions.cs         # Extension methods
│   │   │   └── ConfigHelper.cs       # ConfigNode utilities
│   │   ├── Properties/
│   │   │   └── AssemblyInfo.cs
│   │   └── Parsek.csproj             # Project file
│   └── Parsek.sln                    # Solution file
├── .gitignore
├── CHANGELOG.md
├── LICENSE
├── README.md
└── Parsek.version                    # Master version file
```

---

## 2. Modern Build System: KSPBuildTools

### Installation

Add KSPBuildTools via NuGet:

```bash
dotnet add package KSPBuildTools
```

Or add to your `.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="KSPBuildTools" Version="1.1.1" />
</ItemGroup>
```

### Sample .csproj File

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>latest</LangVersion>
    <AssemblyName>Parsek</AssemblyName>
    <RootNamespace>Parsek</RootNamespace>
    
    <!-- KSPBuildTools Configuration -->
    <KSPBT_ModRoot>$(MSBuildThisFileDirectory)/../GameData/Parsek</KSPBT_ModRoot>
    <KSPBT_ModPluginFolder>Plugins</KSPBT_ModPluginFolder>
    
    <!-- Version from .version file or git tags -->
    <Version>0.1.0</Version>
    <FileVersion>$(Version)</FileVersion>
    <AssemblyVersion>$(Version)</AssemblyVersion>
  </PropertyGroup>

  <!-- KSPBuildTools NuGet Package -->
  <ItemGroup>
    <PackageReference Include="KSPBuildTools" Version="1.1.1" />
  </ItemGroup>

  <!-- Mod Dependencies (auto-installed via CKAN) -->
  <ItemGroup>
    <ModReference Include="ModuleManager">
      <DLLPath>GameData/ModuleManager*.dll</DLLPath>
      <CKANIdentifier>ModuleManager</CKANIdentifier>
    </ModReference>
    <ModReference Include="0Harmony">
      <DLLPath>GameData/000_Harmony/0Harmony.dll</DLLPath>
      <CKANIdentifier>Harmony2</CKANIdentifier>
    </ModReference>
    <ModReference Include="ClickThroughBlocker">
      <DLLPath>GameData/000_ClickThroughBlocker/Plugins/ClickThroughBlocker.dll</DLLPath>
      <CKANIdentifier>ClickThroughBlocker</CKANIdentifier>
    </ModReference>
    <ModReference Include="ToolbarController">
      <DLLPath>GameData/001_ToolbarController/Plugins/ToolbarController.dll</DLLPath>
      <CKANIdentifier>ToolbarController</CKANIdentifier>
    </ModReference>
  </ItemGroup>

  <!-- Auto-generate version file -->
  <ItemGroup>
    <KSPVersionFile Include=".">
      <Destination>$(KSPBT_ModRoot)/Parsek.version</Destination>
      <URL>https://github.com/username/Parsek/releases/latest/download/Parsek.version</URL>
      <Download>https://github.com/username/Parsek/releases/latest</Download>
    </KSPVersionFile>
  </ItemGroup>
</Project>
```

### KSP Install Location

Create a `.user` file or set environment variable:

```xml
<!-- Parsek.csproj.user (don't commit this) -->
<Project>
  <PropertyGroup>
    <KSPRoot>C:\Games\Kerbal Space Program</KSPRoot>
  </PropertyGroup>
</Project>
```

Or set `KSP_DIR` environment variable.

---

## 3. Required Dependencies

### Runtime Dependencies (Users Must Install)

| Dependency | Purpose | License | CKAN ID |
|------------|---------|---------|---------|
| **Module Manager** | Config patching, essential for any mod | CC-BY-SA | `ModuleManager` |
| **Harmony (HarmonyKSP)** | Runtime method patching | MIT | `Harmony2` |
| **ClickThroughBlocker** | Prevents clicks through UI windows | GPL-3.0 | `ClickThroughBlocker` |
| **ToolbarController** | Unified toolbar button management | GPL-3.0 | `ToolbarController` |

### Development Dependencies (Build Time Only)

| Dependency | Purpose | Source |
|------------|---------|--------|
| **KSPBuildTools** | MSBuild integration, reference resolution | NuGet |
| **.NET SDK** | Build toolchain | Microsoft |
| **KSP Assemblies** | Reference DLLs (auto-found by KSPBuildTools) | KSP Install |

### KSP Reference Assemblies

These are automatically referenced by KSPBuildTools from your KSP install:

```
KSP_x64_Data/Managed/
├── Assembly-CSharp.dll        # Main KSP API
├── Assembly-CSharp-firstpass.dll
├── UnityEngine.dll            # Unity core
├── UnityEngine.CoreModule.dll
├── UnityEngine.UI.dll
├── UnityEngine.IMGUIModule.dll
└── ... (other Unity modules)
```

---

## 4. Key Base Classes

### Entry Points

```csharp
// Addon that runs once per scene
[KSPAddon(KSPAddon.Startup.Flight, false)]
public class ParsekFlight : MonoBehaviour
{
    void Start() { }
    void OnDestroy() { }
}

// Scenario module - persists in save file
public class MainTimeline : ScenarioModule
{
    public override void OnLoad(ConfigNode node) { }
    public override void OnSave(ConfigNode node) { }
}

// Per-vessel module
public class MissionRecorder : VesselModule
{
    protected override void OnStart() { }
    void FixedUpdate() { }
}

// Part module (if needed)
public class ParsekPartModule : PartModule
{
    public override void OnStart(StartState state) { }
}
```

### Essential KSP APIs

```csharp
// Time
double ut = Planetarium.GetUniversalTime();
TimeWarp.SetRate(index, instant);
TimeWarp.CurrentRateIndex;

// Vessels
FlightGlobals.ActiveVessel;
FlightGlobals.Vessels;
vessel.protoVessel;
vessel.GetOrbit();

// Save/Load
HighLogic.CurrentGame.Updated();
GamePersistence.SaveGame(filename, folder, SaveMode.OVERWRITE);
ConfigNode node = ConfigNode.Load(path);

// Events
GameEvents.onVesselCreate.Add(callback);
GameEvents.onStageSeparation.Add(callback);
GameEvents.onVesselSOIChanged.Add(callback);
```

---

## 5. Recommended Code Patterns

### Singleton Pattern (from FMRS)

```csharp
[KSPAddon(KSPAddon.Startup.Flight, false)]
public class ParsekCore : MonoBehaviour
{
    public static ParsekCore Instance { get; private set; }
    
    void Awake()
    {
        if (Instance != null)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }
    
    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }
}
```

### Logging Wrapper

```csharp
public static class Log
{
    private const string PREFIX = "[Parsek]";
    
    public static void Info(string message) 
        => Debug.Log($"{PREFIX} {message}");
    
    public static void Warning(string message) 
        => Debug.LogWarning($"{PREFIX} {message}");
    
    public static void Error(string message) 
        => Debug.LogError($"{PREFIX} {message}");
    
    [System.Diagnostics.Conditional("DEBUG")]
    public static void Debug(string message) 
        => UnityEngine.Debug.Log($"{PREFIX} [DEBUG] {message}");
}
```

### ConfigNode Helpers

```csharp
public static class ConfigHelper
{
    public static T GetValue<T>(this ConfigNode node, string name, T defaultValue = default)
    {
        if (!node.HasValue(name)) return defaultValue;
        
        string value = node.GetValue(name);
        
        if (typeof(T) == typeof(double))
            return (T)(object)double.Parse(value);
        if (typeof(T) == typeof(bool))
            return (T)(object)bool.Parse(value);
        if (typeof(T) == typeof(Guid))
            return (T)(object)new Guid(value);
        // ... etc
        
        return defaultValue;
    }
}
```

### GUI Window Base (with ClickThroughBlocker)

```csharp
public abstract class ParsekWindow
{
    protected int windowId;
    protected Rect windowRect;
    protected bool visible;
    protected string title;
    
    protected ParsekWindow(string title)
    {
        this.title = title;
        this.windowId = GetHashCode();
        this.windowRect = new Rect(100, 100, 300, 200);
    }
    
    public void Draw()
    {
        if (!visible) return;
        
        // ClickThroughBlocker integration
        windowRect = ClickThruBlocker.GUILayoutWindow(
            windowId,
            windowRect,
            DrawWindow,
            title
        );
    }
    
    protected abstract void DrawWindow(int id);
    
    public void Show() => visible = true;
    public void Hide() => visible = false;
    public void Toggle() => visible = !visible;
}
```

---

## 6. Version File Format (KSP-AVC)

```json
{
    "NAME": "Parsek",
    "URL": "https://github.com/username/Parsek/releases/latest/download/Parsek.version",
    "DOWNLOAD": "https://github.com/username/Parsek/releases/latest",
    "VERSION": {
        "MAJOR": 0,
        "MINOR": 1,
        "PATCH": 0
    },
    "KSP_VERSION_MIN": {
        "MAJOR": 1,
        "MINOR": 12,
        "PATCH": 0
    },
    "KSP_VERSION_MAX": {
        "MAJOR": 1,
        "MINOR": 12,
        "PATCH": 5
    }
}
```

---

## 7. .gitignore

```gitignore
# Build outputs
bin/
obj/
*.dll
*.pdb

# IDE
.vs/
.idea/
*.user
*.suo

# KSP specific
GameData/Parsek/Plugins/*.dll
GameData/Parsek/PluginData/

# Dependencies (managed by CKAN/build)
packages/

# OS
.DS_Store
Thumbs.db
```

---

## 8. GitHub Actions CI

```yaml
# .github/workflows/build.yml
name: Build

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

jobs:
  build:
    runs-on: ubuntu-latest
    
    steps:
    - uses: actions/checkout@v4
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '8.0.x'
    
    - name: Restore dependencies
      run: dotnet restore Source/Parsek.sln
    
    - name: Build
      run: dotnet build Source/Parsek.sln --configuration Release --no-restore
    
    - name: Upload artifact
      uses: actions/upload-artifact@v4
      with:
        name: Parsek
        path: GameData/Parsek/
```

---

## 9. Quick Start Commands

```bash
# Create solution
mkdir -p Source/Parsek
cd Source
dotnet new sln -n Parsek
cd Parsek
dotnet new classlib -n Parsek -f net472
cd ..
dotnet sln add Parsek/Parsek.csproj

# Add KSPBuildTools
cd Parsek
dotnet add package KSPBuildTools

# Build
dotnet build --configuration Release
```

---

## 10. Reference Mods to Study

| Mod | GitHub | Key Learnings |
|-----|--------|---------------|
| **FMRS** | linuxgurugamer/FMRS | Save points, time jumping, vessel state |
| **Persistent Trails** | JPLRepo/KSPPersistentTrails | Trajectory recording, kinematic replay |
| **Kerbal Alarm Clock** | TriggerAu/KerbalAlarmClock | Event scheduling, warp control |
| **StageRecovery** | linuxgurugamer/StageRecovery | Background vessel processing |

---

## Next Steps

1. **Initialize project** using the structure above
2. **Configure KSPBuildTools** with your KSP install path
3. **Create basic addon** that logs to verify setup works
4. **Study FMRS source** for save point implementation
5. **Implement MVP** incrementally: recording → playback → UI → persistence
