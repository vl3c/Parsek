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
│   └── Parsek/
│       ├── Parsek.csproj    # SDK-style project
│       └── ParsekSpike.cs   # Spike implementation
├── docs/                     # Documentation
├── mods/                     # Reference mods (git-ignored)
├── claude.md                 # This file
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

### Test
```bash
"Kerbal Space Program/KSP_x64.exe"
```

**Testing the spike:**
1. Launch KSP, start any flight
2. Press **F9** to start recording
3. Fly around for 30-60 seconds
4. Press **F9** to stop recording
5. Press **F10** to start playback
6. Observe green sphere (12m diameter) following your path

### Debug
- Check `Kerbal Space Program/KSP.log` for errors
- Look for `[Parsek Spike]` log entries
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
