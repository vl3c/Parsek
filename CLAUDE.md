# Claude Development Notes

## Project Configuration

### KSP Installation
- **Local KSP instance** at `Kerbal Space Program/` in project root
- This is a **clean copy** for testing, NOT the Steam installation
- Steam installation stays intact for normal gameplay
- Safe to test and modify without affecting Steam

### KSP Instance Setup
**Minimal mods installed (testing environment):**
- Squad / SquadExpansion (stock game files)
- ModuleManager 4.2.3 (essential for mods)
- Harmony (essential for runtime patching)
- **Parsek** (our mod being developed)

All other mods removed for clean testing environment.

### Build Configuration
- KSP assemblies referenced from: `Kerbal Space Program/KSP_x64_Data/Managed/`
- Build output copied to: `Kerbal Space Program/GameData/Parsek/Plugins/`
- Auto-deployed on every build via MSBuild post-build target

### Project Structure
```
Parsek/
├── Kerbal Space Program/    # Local KSP instance for testing
│   ├── GameData/
│   │   ├── Squad/           # Stock parts
│   │   ├── 000_Harmony/     # Essential dependency
│   │   ├── ModuleManager.*  # Essential dependency
│   │   └── Parsek/          # Our mod (auto-deployed)
│   │       └── Plugins/
│   │           ├── Parsek.dll
│   │           └── Parsek.pdb
│   ├── KSP_x64_Data/
│   │   └── Managed/         # Assembly references
│   └── KSP.log              # Debug output here
├── Source/                   # Mod source code
│   ├── Parsek/
│   │   ├── Parsek.csproj    # SDK-style project
│   │   ├── ParsekSpike.cs   # Main spike — recording, playback, UI, scene handling
│   │   ├── RecordingStore.cs # Static storage surviving scene changes
│   │   └── ParsekScenario.cs # ScenarioModule for save/load persistence
│   └── Parsek.Tests/         # Unit tests (xUnit)
├── docs/                     # Documentation
├── mods/                     # Reference mods (git-ignored)
├── CLAUDE.md                 # This file
├── build.bat                 # Build script (alternative to dotnet)
└── .gitignore               # Git ignore rules
```

## Development Workflow

### Build
```bash
cd Source/Parsek
dotnet build              # Debug build (default)
dotnet build -c Release   # Release build
```

Builds automatically copy to `Kerbal Space Program/GameData/Parsek/Plugins/`

### Unit Tests
```bash
cd Source/Parsek.Tests
dotnet test
```

### In-Game Test
```bash
"Kerbal Space Program/KSP_x64.exe"
```

**Recording + Timeline (core flow):**
1. Launch KSP, start any flight (career mode for resource tracking)
2. Press **F9** to start recording
3. Fly around for 30-60 seconds
4. Press **F9** to stop recording
5. Revert to Launch (Esc → Revert to Launch)
6. A "Merge to Timeline?" dialog appears — click **Merge to Timeline**
7. Wait on the pad until UT reaches original recording timestamps
8. Green-cyan ghost sphere appears and replays previous flight
9. Funds/science/reputation deltas are applied at the correct UT

**Manual preview (no revert needed):**
1. Record as above, press **F10** to preview playback immediately
2. Press **F11** to stop preview

**Controls:**
- **F9** — Start/Stop recording
- **F10** — Preview playback (current recording, relative time)
- **F11** — Stop preview
- **Alt+P** — Toggle UI window

### Debug
- Check `Kerbal Space Program/KSP.log` for errors
- Look for `[Parsek Spike]` and `[Parsek Scenario]` log entries
- Alt+F12 opens Unity debug console in-game

## Git Configuration

**Ignored folders:**
- `Kerbal Space Program/` - Local KSP instance (too large)
- `mods/` - Reference mods (submodules/clones)
- `.claude/` - Local Claude settings
- `bin/`, `obj/` - Build artifacts

**Tracked:**
- `Source/` - Mod source code
- `docs/` - Documentation
- `build.bat` - Build script
- `.gitignore` - Git ignore rules

## Why This Approach

- **Isolated testing** - No conflicts with Steam installation
- **Clean environment** - Minimal mods = easier debugging
- **Fast iteration** - Auto-deployment on build
- **Reproducible** - Can share exact test environment setup
