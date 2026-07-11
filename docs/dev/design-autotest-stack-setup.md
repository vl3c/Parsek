# Design: Automation Stack Setup Script (Module M-A6)

Status: DRAFT (2026-07-11). Module M-A6 of the Automated Testing Plan
(`docs/dev/automated-testing-plan.md`, sections 2, 10, 11b, 12). Ops/tooling
module: the Data Model section describes manifest and profile FILE FORMATS
rather than in-game structs. Follows the house design-doc template
(`docs/dev/design-doc-template.md`) adapted for a provisioning tool.

This doc records verified ground truth (the kRPC tag/flag audit, the
KRPC.MechJeb pairing, the dev-install inventory) inline as the authority for
the setup script. Where a fact could only be confirmed by downloading a
release artifact at install time, it is marked OPEN with the exact command.

## Implementation Status (v1)

v1 ships the `--dry-run` PLANNER and the pure decision library
(`harness/provision/provlib.py`, fully unit-tested). The heavy live provisioning
phases (BUILD-TT, CLONE, INSTALL, and the SETTINGS/DEPLOY/MM-CACHE/MANIFEST
writes that follow them) and `--repair` are NOT yet implemented: a non-dry-run
invocation aborts loudly at the first unimplemented phase (`EC-LIVE`) rather than
half-provisioning an instance or writing a manifest that claims a completeness
the run cannot back. The pure decisions each live phase will make (pin
resolution, junction classification, settings-delta application, disk/path
guards, dev-install aliasing, DLL-identity grep, lock arbitration) are already
implemented and tested so live execution is assembly of vetted pieces. Live
execution lands with the coordinated smoke-run task (see "Test Plan" and
"Deferred to live execution").

---

## Ground Truth (verified 2026-07-11)

All findings below were gathered by read-only inspection of the local clones
under `C:\Users\vlad3\Documents\Code\Parsek\mods\` and the dev install at
`C:\Users\vlad3\Documents\Code\Parsek\Kerbal Space Program`. They supersede
the capability description in automated-testing-plan.md section 2 where they
conflict, because that section describes the local master snapshot, not the
release tag.

### GT-1: kRPC tags and the v0.5.4 pin

- `git fetch --tags` in `mods/krpc` lists releases through `v0.5.4`, which is
  the newest release tag. `v0.5.4` = commit `11f1f1366fa4301049f6eac6640604127a9d763b`,
  tagged 2024-06-10. This is the correct pin for KSP 1.12.5 (kRPC 0.4.x/0.5.x
  is the KSP 1.8-1.12 line; 0.5.4 is the last 0.5.x release).
- The local clone HEAD is `9a155a1c448f093dec4747769aaddb62a684608a`, a
  `master` snapshot dated 2026-07-11. **The clone proves master, not v0.5.4.**
  Every capability claim MUST be checked at the tag with `git show v0.5.4:<path>`,
  never against the working tree.

### GT-2: TestingTools capability set AT v0.5.4 (the decision-forcing finding)

TestingTools source at `tools/TestingTools/src/` at v0.5.4 contains exactly
five files: `AutoLoadGame.cs`, `AutoSwitchVessel.cs`, `OrbitTools.cs`,
`TestingTools.cs`, `TestingTools.csproj`. There is **no `TestingToolsOptions.cs`**
at v0.5.4.

RPC/property surface in `TestingTools.cs` at v0.5.4 (verified by
`git show v0.5.4:tools/TestingTools/src/TestingTools.cs`):

| Member | Signature | Notes |
|---|---|---|
| `CurrentSave` | property (string, get) | name of current save |
| `LoadSave` | `(string directory, string name)` | loads + focuses active vessel |
| `RemoveOtherVessels` | `()` | `.Die()` on all but active |
| `SetCircularOrbit` | `(string body, double altitude)` | teleport to circular orbit |
| `SetOrbit` | `(string body, double sma, ecc, inc, lan, argPe, meanAnomaly, epoch)` | full-element teleport |
| `ClearRotation` | `(Vessel vessel = null)` | zero rotational velocity |
| `ApplyRotation` | `(float angle, Tuple<float,float,float> axis, Vessel vessel = null)` | apply a rotation |

`AutoLoadGame.cs` at v0.5.4 **hardcodes** `Game => "default"` and
`Save => "persistent"` as read-only properties, auto-loads on `MAINMENU`
after a 15-frame delayed callback, and `AutoSwitchVessel` switches to the
first non-`SpaceObject` proto vessel after a 15-frame delay in `SPACECENTER`.

**NOT present at v0.5.4:**
- No `--krpc-auto-load-*` command-line flags. AutoLoadGame reads nothing from
  the command line; save selection is a compiled-in constant.
- No `Quit()` RPC.
- No `SetLanded()` RPC (and no `SetLanded` extension in `OrbitTools.cs`).

### GT-3: The master-only delta

At the clone HEAD (`9a155a1c`, 2026-07-11) master adds, in
`TestingToolsOptions.cs` (introduced at that commit; `git log --all` shows the
file touched only by `9a155a1c "RPC Deprecation (#926)"`):

- `const string AutoLoadPrefix = "--krpc-auto-load-";` and the seven
  arguments: `game=`, `save=`, `vessel=`, `craft=`, `craft-directory=`,
  `craft-fixture-dir=`, `launch-site=`, parsed from
  `Environment.GetCommandLineArgs()`; `AutoLoadRequested` is true iff any
  auto-load arg is supplied (default stays `default`/`persistent`).
- `Quit()` (bare `Application.Quit()`) in `TestingTools.cs`.
- `SetLanded(string body, double lat, double lon, double alt = 0)` RPC plus
  the `OrbitTools.SetLanded(...)` extension.

**Conclusion:** the automated-testing-plan section 2 capability list (auto-load
boot flags, `Quit()`, `SetLanded()`) describes the master snapshot. It is NOT
available from the v0.5.4 release. This resolves the section 12 risk
("kRPC 0.5.4-at-tag flag set unverified"): the flags are confirmed ABSENT at
v0.5.4. The setup script must choose a pin with eyes open (see Behavior,
step PIN).

### GT-4: TestingTools build coupling at v0.5.4

`git show v0.5.4:tools/TestingTools/src/TestingTools.csproj` shows the project
is bazel-tree-coupled and cannot be built standalone as-is:

- KSP DLL HintPaths point at `..\..\..\lib\ksp\KSP_Data\Managed\*.dll` (a
  bazel-staged `KSP_Data` layout, not the install's `KSP_x64_Data`).
- References `Google.Protobuf.dll` and `KRPC.IO.Ports.dll` from
  `..\..\..\bazel-bin\tools\build\ksp\`.
- `ProjectReference`s `core/src/KRPC.Core.csproj` and
  `service/SpaceCenter/src/KRPC.SpaceCenter.csproj`.
- Compiles a generated `bazel-bin/tools/TestingTools/AssemblyInfo.cs`.

It compiles four sources upstream (`AutoLoadGame.cs`, `AutoSwitchVessel.cs`,
`OrbitTools.cs`, `TestingTools.cs`) and uses `using KRPC.Service;` (attributes
in KRPC.Core) and `KRPC.SpaceCenter.Services.Vessel`. The setup script does
NOT run bazel; it authors a standalone shim project (Behavior step BUILD-TT)
that compiles only TWO of those four (`OrbitTools.cs`, `TestingTools.cs`) and
DROPS `AutoLoadGame.cs` / `AutoSwitchVessel.cs` so the seam's `LoadGame` boot
(M-A2) owns save selection without a racing auto-loader.

### GT-5: kRPC release zip does not ship TestingTools

TestingTools lives under `tools/` with its own project and is a developer
testing utility, not part of the distributable. The mod release
(`GameData/kRPC/` from krpc.github.io) ships the runtime binaries
(`KRPC.dll`, `KRPC.Core.dll`, `KRPC.SpaceCenter.dll`, `Google.Protobuf.dll`,
`KRPC.IO.Ports.dll`) but not `TestingTools.dll`. This matches the plan's
"TestingTools is NOT in release binaries."
OPEN (install-time confirm): after download,
`unzip -l krpc-<ver>.zip | grep -i testingtools` must return empty; and
`unzip -l | grep -iE 'KRPC.Core.dll|KRPC.SpaceCenter.dll|Google.Protobuf.dll'`
must return the three compile references. If TestingTools ever appears in the
zip, skip BUILD-TT and install the shipped DLL instead.

### GT-6: KRPC.MechJeb pairing (resolves the plan's open question)

`mods/KRPC.MechJeb` is the genhis repo (`origin https://github.com/genhis/KRPC.MechJeb`),
HEAD `398bc337` (2024-12-23 "Prepare for next release"), sole tag `v0.7.1`.
`CHANGELOG.md`:

- `[0.7.1] - 2024-12-23` Changed: **"Updated for kRPC 0.5.4"**.
- `[0.7.0] - 2023-03-30` Changed: "Updated for KSP 1.12 and kRPC 0.5.2 ...
  Updated AscentAutopilot and AirplaneAutopilot for MechJeb 2.14.3.0".

`KRPC.MechJeb.csproj` links `KRPC.dll`, `KRPC.Core.dll`, `KRPC.SpaceCenter.dll`,
`Assembly-CSharp.dll`, `UnityEngine*.dll` from `lib/`, targeting `v4.5.2`.

**Decision:** for a kRPC v0.5.4 pin, **genhis KRPC.MechJeb 0.7.1** is the
CHANGELOG-proven pair. The darchambault 0.8.1 fork is NOT present locally and
is only relevant if the script pins a kRPC newer than 0.5.4 (a master commit
for the auto-load flags), where 0.7.1's ABI against release KRPC.dll may not
hold. See Behavior step PAIR for the decision procedure.

### GT-7: MechJeb2 has no release tags

`mods/MechJeb2` HEAD `748ca68a` (2026-07-08), `git tag` empty. MechJeb2 ships
dev builds numbered by CI (e.g. 2.15.x.x), not git tags; `AssemblyInfo.cs`
carries the placeholder `1.0.0.0` (real version injected at build). The pin is
therefore a **downloaded release build number** (the plan's target: MJ 2.15
line), not a git ref. The local clone is source-only and untagged; do not try
to `git checkout` a MechJeb version.

### GT-8: Dev install inventory

Path: `C:\Users\vlad3\Documents\Code\Parsek\Kerbal Space Program`. Managed DLL
dir: `KSP_x64_Data\Managed`. `settings.cfg` present with these keys (verified):

```
AUTOSTRUT_SYMMETRY   = True
SIMULATE_IN_BACKGROUND = True
PHYSICS_FRAME_DT_LIMIT = 0.04
CONIC_PATCH_DRAW_MODE = 3
CONIC_PATCH_LIMIT    = 4
UI_SCALE             = 1.2
FULLSCREEN           = True
QUALITY_PRESET       = 5
TEXTURE_QUALITY      = 0
SHADOWS_QUALITY      = 4
FRAMERATE_LIMIT      = 120
AERO_FX_QUALITY      = 3
TERRAIN_SHADER_QUALITY = 3
```

`GameData/` mods present: `000_ClickThroughBlocker`, `000_Harmony`,
`001_ToolbarControl`, `B9PartSwitch`, `BetterTimeWarp`, `CommunityDeltaVMaps`,
`CommunityTechTree`, `DistantObject`, `HideEmptyTechTreeNodes`,
`KSPCommunityFixes`, `KSPTextureLoader`, `Kopernicus`, `ModularFlightIntegrator`,
`ModuleManager.4.2.3.dll` (+ `ModuleManager.ConfigCache`, `ConfigSHA`,
`Physics`, `TechTree`), `Parsek`, `ProbesBeforeCrew`, `ReStock`, `ReStockPlus`,
`RestockWaterfallExpansion`, `Squad`, `SquadExpansion`, `StockWaterfallEffects`
(SWE), `Waterfall`, `WaterfallRestock`.

Notes that drive the profile design:
- `PersistentRotation` is listed in the plan's modded-compat profile but is
  **NOT present** in the dev GameData. OPEN: either drop it from the profile or
  source it separately (mark in manifest as `absent-source`). Do not silently
  omit.
- SWE = `StockWaterfallEffects` is present; ReStock/ReStockPlus and Waterfall
  are present, so modded-compat can be assembled from the dev install for
  those. `BetterTimeWarp` present.
- ModuleManager cache files (`ConfigCache`/`ConfigSHA`/`Physics`/`TechTree`)
  exist in the dev GameData; the stale-cache edge case (EC-2) is real.

### GT-9: Parsek.Tests.csproj HintPath pattern (reference-resolution template)

`Source/Parsek.Tests/Parsek.Tests.csproj` resolves KSP DLLs via `$(KSPDir)`
(overridable by the `KSPDIR` env var, else an ancestor-walk probe of up to five
parents for `Kerbal Space Program/`) and `HintPath`s into
`$(KSPDir)\KSP_x64_Data\Managed\` (`UnityEngine.CoreModule`,
`UnityEngine.IMGUIModule`, `Assembly-CSharp`) plus `0Harmony` from
`$(KSPDir)\GameData\000_Harmony\0Harmony.dll`, each with `<Private>true</Private>`.
The TestingTools shim reuses this exact HintPath approach.

---

## Problem

The mission harness (M-A5) needs a byte-for-byte reproducible KSP automation
environment: a pinned kRPC + TestingTools + MechJeb2 + KRPC.MechJeb stack, a
dedicated cloned KSP instance per profile, the Parsek DLL under test deployed
safely, and a manifest that lets the harness REFUSE to run against a drifted or
mismatched instance. Today none of that is provisioned; the plan (section 2)
even mis-describes the kRPC capability set because it was written from the
master clone. Without a deterministic, idempotent, self-verifying setup step,
every automated run is a bet that the environment matches what the scenarios
assume, and the sibling-worktree DLL race (`.claude/CLAUDE.md`) can silently
deploy the wrong Parsek build into an automation instance. M-A6 is the script
that provisions the stack, records exactly what it installed, and fails loud
on drift or partial failure.

## Terminology

- **Dev install**: the developer's working KSP at
  `Code\Parsek\Kerbal Space Program`. Never modified by this script (read-only
  source for cloning and for mod payloads).
- **Automation instance**: a cloned KSP directory the harness launches.
  Two profiles: `stock-minimal` and `modded-compat`.
- **Profile**: a declarative spec (a file) naming the mod set, settings deltas,
  and component pins that define one automation instance.
- **Component**: a pinned installable (kRPC, TestingTools, MechJeb2,
  KRPC.MechJeb, Parsek.dll, or a dev-sourced mod folder).
- **Manifest**: the JSON record the script emits per instance, enumerating every
  installed component with exact version/tag/commit/hash. The harness's
  admission gate.
- **Staging**: an intermediate directory the Parsek build is copied into and
  hashed BEFORE it is installed, so a concurrent sibling build cannot race the
  bytes mid-copy (`.claude/CLAUDE.md` DLL race).
- **Pin**: an exact, reproducible identifier for a component: a git tag+commit,
  a release build number + download URL + sha256, or a source folder + content
  hash.
- **Drift**: any difference between an instance's current on-disk state and its
  manifest / profile expectation.

## Mental Model

```
  DEV INSTALL (read-only)                 mods/ clones (read-only, pin refs)
  Kerbal Space Program/                   krpc@v0.5.4  KRPC.MechJeb@v0.7.1
    KSP_x64_Data/Managed/*.dll  --+         MechJeb2 (source; pin = download)
    GameData/{Squad,ReStock,...}  |
    settings.cfg  ----------------+
                                  v
                        +----------------------+
                        |   provision.py        |   <-- M-A6, this doc
                        |  (idempotent, logged) |
                        +----------+-----------+
       PIN -> DOWNLOAD -> BUILD-TT -> PAIR -> CLONE -> SETTINGS -> DEPLOY -> MANIFEST -> VERIFY
                                  |
             +--------------------+---------------------+
             v                                            v
   automation/stock-minimal/                  automation/modded-compat/
     GameData/{Squad, Parsek, kRPC,             GameData/{...stock-minimal...,
       TestingTools.dll, MechJeb2,                Waterfall, SWE, ReStock/+,
       KRPC.MechJeb}                              BetterTimeWarp, PersistentRotation?}
     settings.cfg (delta-applied)               settings.cfg (delta-applied)
     Parsek/provision-manifest.json  <-- harness admission gate
```

The script is a converging function: given the pins and profiles, repeated runs
drive each instance to the same state, report drift, and (optionally) repair it.
It never touches the dev install's `GameData`. Parsek.dll flows dev-build ->
staging (hash) -> instance (verify), never dev-`GameData` -> instance.

Order rationale: PIN before anything (a wrong pin invalidates all downstream
work); DOWNLOAD + BUILD-TT before CLONE (fail fast on a broken build before
copying 5-8 GB); PAIR (resolve KRPC.MechJeb) alongside DOWNLOAD; CLONE creates
the instance; SETTINGS + DEPLOY + payload-install populate it; MANIFEST records
truth last; VERIFY re-reads the instance and cross-checks the manifest.

## Data Model (file formats)

Three persisted formats. All are "new data that persists" per the plan's
section 11b, so they get round-trip tests.

### Component pin file: `harness/provision/pins.toml`

Single source of truth for what versions the stack targets. Hand-edited,
reviewed, committed. TOML for human diff-ability.

```toml
schema = 1
kspVersion = "1.12.5"

[krpc]
# GT-1/GT-2: v0.5.4 is the release pin; it LACKS the auto-load flags/Quit/SetLanded.
mode = "tag"                       # "tag" | "commit"
tag = "v0.5.4"
commit = "11f1f1366fa4301049f6eac6640604127a9d763b"
releaseZipUrl = "https://github.com/krpc/krpc/releases/download/v0.5.4/krpc-0.5.4.zip"
releaseZipSha256 = "OPEN-fill-at-first-download"
localClone = "mods/krpc"

[testingtools]
# Built from source at the same krpc ref via the standalone shim (BUILD-TT).
buildFromSource = true
# 2-FILE shim: only OrbitTools.cs + TestingTools.cs. AutoLoadGame.cs and
# AutoSwitchVessel.cs are DROPPED: at v0.5.4 AutoLoadGame unconditionally fires
# 15 frames after MAINMENU trying to load saves/default/persistent.sfs, which
# would RACE the seam's boot (M-A2 LoadGame). BUILD-TT asserts the AutoLoadGame
# type is ABSENT from the built assembly.
sourceFiles = ["OrbitTools.cs", "TestingTools.cs"]
# Recorded so the harness knows which control capabilities exist:
capabilities = ["LoadSave", "RemoveOtherVessels", "SetCircularOrbit", "SetOrbit", "ClearRotation", "ApplyRotation"]
# Boot is driven by the Parsek seam, NOT by TestingTools auto-load (which is dropped):
bootChannel = "parsek-seam-LoadGame"
missingVsMaster = ["autoLoadFlags", "Quit", "SetLanded"]   # GT-2/GT-3

[mechjeb2]
# GT-7: no git tags; pin is a downloaded build.
mode = "release"
buildNumber = "2.15.x.x-OPEN"      # fill with the exact MJ 2.15 dev build chosen
downloadUrl = "OPEN"
sha256 = "OPEN"

[krpc_mechjeb]
# GT-6: genhis 0.7.1 pairs with kRPC 0.5.4 (CHANGELOG-proven).
fork = "genhis"                    # "genhis" | "darchambault"
tag = "v0.7.1"
commit = "398bc337492c5f725c83ab1aac85c32a1c0349ea"
localClone = "mods/KRPC.MechJeb"
pairedKrpcTag = "v0.5.4"           # asserted against [krpc].tag at PAIR
pairedMechjebLine = "2.14.3+"      # from CHANGELOG 0.7.0/0.7.1
```

### Profile file: `harness/provision/profiles/<name>.toml`

```toml
schema = 1
name = "stock-minimal"
baseInstall = "Kerbal Space Program"       # dev install, relative to repo umbrella root
instanceDir = "automation/stock-minimal"   # created by CLONE

# Mods copied verbatim from the dev install's GameData (content-hashed into manifest).
# stock-minimal keeps only what the stack needs plus stock.
# NOTE: Squad + SquadExpansion are NOT listed here - they are stock asset payloads,
# JUNCTIONED (not copied) by the CLONE step and recorded as junction targets in the
# manifest (stock, negligible drift risk). Only non-stock mod folders are dev-sourced
# copies so a dev-GameData change cannot silently drift an instance.
devSourcedMods = [
  "000_Harmony", "ModuleManager.4.2.3.dll",
  "000_ClickThroughBlocker", "001_ToolbarControl",
  "CommunityDeltaVMaps", "CommunityTechTree", "ProbesBeforeCrew",
]
# Stack components installed by the script (not from dev GameData): kRPC,
# TestingTools.dll, MechJeb2, KRPC.MechJeb, Parsek.
stackComponents = ["krpc", "testingtools", "mechjeb2", "krpc_mechjeb", "parsek"]

# GT-8 verified keys. Delta = key -> pinned value; everything else copied from
# the dev settings.cfg then overwritten here.
[settings]
PHYSICS_FRAME_DT_LIMIT = "0.03"    # tighter physics cap for determinism
FRAMERATE_LIMIT = "60"             # cap CPU; automation window is small/low-detail
QUALITY_PRESET = "0"
TEXTURE_QUALITY = "3"              # lowest
SHADOWS_QUALITY = "0"
AERO_FX_QUALITY = "0"
TERRAIN_SHADER_QUALITY = "0"
UI_SCALE = "1.0"
FULLSCREEN = "False"
SCREEN_RESOLUTION_WIDTH = "1280"
SCREEN_RESOLUTION_HEIGHT = "720"
SIMULATE_IN_BACKGROUND = "True"    # MUST stay True: unattended runs lose focus
AUTOSTRUT_SYMMETRY = "False"       # pin autostrut policy (plan section 2/10)
CONIC_PATCH_DRAW_MODE = "3"
```

`modded-compat.toml` differs only by adding to `devSourcedMods`:
`Waterfall`, `WaterfallRestock`, `RestockWaterfallExpansion`,
`StockWaterfallEffects`, `ReStock`, `ReStockPlus`, `BetterTimeWarp`,
`Kopernicus`, `ModularFlightIntegrator`, `B9PartSwitch`, `KSPCommunityFixes`,
`DistantObject`, `KSPTextureLoader`, `HideEmptyTechTreeNodes`; and
`PersistentRotation` guarded by GT-8 OPEN (if absent from dev GameData, the
script records `absent-source` and does not fail unless the profile marks it
`required = true`).

### Version manifest: `<instanceDir>/GameData/Parsek/provision-manifest.json`

Emitted per instance. The harness reads it at startup and refuses to run on
mismatch (plan section 9/10).

**Path decision (unified).** All three provisioning artifacts -- the manifest
(`provision-manifest.json`), the log (`provision-log.txt`), and the lock /
incomplete markers (`.provision.lock`, `.provision-incomplete`) -- live together
under `<instanceDir>/GameData/Parsek/` so they travel with the mod folder and are
trivially found by the harness. This is safe because KSP's `GameDatabase` only
loads `.cfg` (and asset) files and IGNORES `.json` / `.txt` / dotfiles, so the
manifest, log, and lock never become phantom config nodes or get parsed by KSP.

```json
{
  "schema": 1,
  "profile": "stock-minimal",
  "generatedUtc": "2026-07-11T14:32:07Z",
  "provisionScriptCommit": "<git HEAD of the worktree that ran provision.py>",
  "kspVersion": "1.12.5",
  "instanceDir": "automation/stock-minimal",
  "components": {
    "krpc":        { "kind": "release-zip", "tag": "v0.5.4", "commit": "11f1f13...", "sha256": "...", "installedDlls": {"KRPC.dll": "sha256...", "KRPC.Core.dll": "sha256...", "KRPC.SpaceCenter.dll": "sha256..."} },
    "testingtools":{ "kind": "built-shim", "krpcRef": "v0.5.4", "dllSha256": "...", "sourceFiles": ["OrbitTools.cs","TestingTools.cs"], "capabilities": ["LoadSave","RemoveOtherVessels","SetCircularOrbit","SetOrbit","ClearRotation","ApplyRotation"], "bootChannel": "parsek-seam-LoadGame", "autoLoaderAbsent": true, "missing": ["autoLoadFlags","Quit","SetLanded"] },
    "mechjeb2":    { "kind": "release", "buildNumber": "2.15.x.x", "sha256": "..." },
    "krpc_mechjeb":{ "kind": "git", "fork": "genhis", "tag": "v0.7.1", "commit": "398bc33...", "dllSha256": "...", "pairedKrpc": "v0.5.4" },
    "parsek":      { "kind": "staged-build", "assemblyVersion": "0.10.x", "dllSha256": "...", "stagedFrom": "Source/Parsek/bin/Debug/Parsek.dll", "sourceCommit": "<git HEAD>", "signatureStrings": {"<distinctive-utf16>": 1} }
  },
  "devSourcedMods": { "000_Harmony": "treehash...", "ModuleManager.4.2.3.dll": "sha256...", "PersistentRotation": "absent-source" },
  "junctionTargets": { "GameData/Squad": "<devInstall>/GameData/Squad", "GameData/SquadExpansion": "<devInstall>/GameData/SquadExpansion", "KSP_x64_Data/StreamingAssets": "<devInstall>/KSP_x64_Data/StreamingAssets" },
  "settingsDeltasApplied": { "FRAMERATE_LIMIT": "60", "QUALITY_PRESET": "0", "...": "..." },
  "settingsBaseSha256": "<hash of dev settings.cfg the deltas were applied over>",
  "settingsFinalSha256": "<sha256 of the instance settings.cfg as written; VERIFY re-hashes it>",
  "buildId64Sha256": "<sha256 of the instance KSP_x64_Data/../buildID64.txt; asserts actual KSP version vs pins.kspVersion>",
  "verify": { "utf16GrepPassed": true, "dllHashMatchesStaging": true, "settingsFinalHashMatches": true, "buildId64Matches": true, "kspRunningAtProvision": false }
}
```

The harness admission check compares (profile name, kspVersion, every component
pin+hash, Parsek `dllSha256` and `signatureStrings`, `settingsDeltasApplied`)
against its expected pins. Any mismatch => refuse to run with the diff printed.

### Provisioning log: `<instanceDir>/GameData/Parsek/provision-log.txt`

Append-only, one decision per line, harness-archivable (plan section 10 triage).
Format mirrors ParsekLog: `[Provision][LEVEL][Step] message`. Every component
decision (found / installed / skipped / drift / repaired), every hash, every
abort reason. See Diagnostic Logging.

## Behavior

Location decision: the script and its data live under a new `harness/provision/`
package (`harness/provision/provision.py`, `pins.toml`, `profiles/`), keeping
automation tooling under the same `harness/` root the plan establishes for
`harness/scenarios/` and `harness/run.py`, and separate from `scripts/` (which
holds dev-facing build/release/log tools). Entry point:
`python harness/provision/provision.py --profile stock-minimal [--repair] [--dry-run]`.

Steps run in this order; each is idempotent and logged.

- **PREFLIGHT.** Resolve the umbrella root (`Code\Parsek`). Assert the dev
  install, the `mods/` clones, and `pins.toml` exist. Assert KSP is not running
  against any target instance (EC-1). Load the profile. On `--dry-run`, compute
  and print the plan and drift, write nothing.

- **PIN.** Read `pins.toml`. For git-pinned components (`krpc`, `krpc_mechjeb`),
  in the LOCAL clone run read-only `git rev-parse <tag>` and assert it equals the
  recorded `commit` (guards a moved/retagged ref). Do NOT check out the clone;
  read via `git show <ref>:<path>` / `git archive <ref>`. Record the resolved
  commits. Emit the GT-2 capability warning to the log: kRPC v0.5.4 TestingTools
  has no auto-load flags / `Quit` / `SetLanded`; the harness must drive the
  instance BOOT via the M-A2 `LoadGame` verb (the boot channel; kRPC RPCs are
  not available at the main menu, so the boot cannot go through a kRPC RPC),
  quit via the M-A2 `FlushAndQuit` verb, and land fixtures via `SetOrbit`, not
  `SetLanded`. The TestingTools `LoadSave` RPC remains an OPTIONAL in-game-only
  convenience (a mid-flight re-load once a scene is up), never the boot path.
  This step depends on M-A2 shipping the `LoadGame` verb; without it there is no
  main-menu boot channel and the automation instance cannot leave the menu.
  - Tag choice: default pin is v0.5.4 (stable, GT-1). If a profile explicitly
    sets `krpc.mode = "commit"` to a master commit for the auto-load flags, PIN
    logs a loud AMBER: the flags come with an unreleased kRPC ABI and force the
    PAIR re-decision (darchambault 0.8.1). v0.5.4 remains the recommended pin
    because the M-A2 seam already covers boot (the `LoadGame` verb) and
    commit-safe quit (`FlushAndQuit`), so the master auto-load flags buy little
    for real ABI risk.

- **DOWNLOAD.** For `krpc` and `mechjeb2`, download the pinned release to a
  cache (`harness/provision/.cache/`), verify sha256 against `pins.toml`
  (EC-6 disk, EC-3 drift). If `releaseZipSha256`/`sha256` is `OPEN`, download,
  compute, print the hash, and ABORT asking the maintainer to record it (never
  silently trust an unverified artifact). Confirm GT-5: assert the kRPC zip
  contains `KRPC.Core.dll` + `KRPC.SpaceCenter.dll` + `Google.Protobuf.dll` and
  does NOT contain `TestingTools.dll`.

- **BUILD-TT.** Build TestingTools from source at the pinned kRPC ref via a
  standalone shim (GT-4): the script extracts the TWO source files with
  `git archive v0.5.4 tools/TestingTools/src/{OrbitTools,TestingTools}.cs`
  into a temp build dir -- `AutoLoadGame.cs` and `AutoSwitchVessel.cs` are
  DELIBERATELY DROPPED (at v0.5.4 `AutoLoadGame` unconditionally auto-loads
  `saves/default/persistent.sfs` 15 frames after `MAINMENU`, which would race the
  seam's `LoadGame` boot; save selection is the seam's job, M-A2). The script
  writes a generated `TestingTools.shim.csproj`
  (`TargetFramework net472`, `NoStdLib` off) that references (GT-9 pattern):
  - KSP DLLs from `<devInstall>\KSP_x64_Data\Managed\` via `HintPath`
    (`Assembly-CSharp`, `UnityEngine.CoreModule`, plus the modules TestingTools
    uses);
  - `KRPC.Core.dll` and `KRPC.SpaceCenter.dll` (KRPC.Service attributes +
    `Services.Vessel`) and `Google.Protobuf.dll` from the extracted kRPC release
    zip's `GameData/kRPC/`;
  - a script-authored minimal `AssemblyInfo.cs` (replacing the bazel-generated
    one).

  Invocation: `dotnet build TestingTools.shim.csproj -c Release`
  (fallback `msbuild /t:Rebuild /p:Configuration=Release` if `dotnet` cannot
  resolve the net472 reference assemblies). Output `TestingTools.dll` is hashed
  and cached. On build failure, ABORT with the compiler output and the exact
  missing-reference (EC-4). Never install a stale/previous TestingTools.dll.

- **PAIR.** Resolve KRPC.MechJeb (GT-6). If `krpc.tag == "v0.5.4"`, assert
  `krpc_mechjeb.fork == "genhis"` and `tag == "v0.7.1"` (CHANGELOG-proven pair)
  and build/stage its DLL from `mods/KRPC.MechJeb` at v0.7.1 against the release
  KRPC.dll/KRPC.Core.dll/KRPC.SpaceCenter.dll. If a non-v0.5.4 kRPC is pinned,
  the pairing is UNVERIFIED locally: log AMBER with the exact decision procedure
  (web-verify at install time) -- `check the darchambault 0.8.1 release notes /
  the genhis release matrix at https://github.com/Genhis/KRPC.MechJeb/releases
  and https://github.com/darchambault/KRPC.MechJeb/releases for the fork+tag
  whose "Updated for kRPC <ver>" line matches the pinned kRPC and whose MechJeb
  line matches the pinned MJ build` -- and ABORT rather than guess.

- **CLONE.** Create `<instanceDir>` by copying the dev install. Disk strategy
  (GT-8, ~5-8 GB): copy the small mutable surface fully (`KSP_x64.exe`,
  `KSP_x64_Data` EXCEPT the large read-only asset bundles, `settings.cfg`,
  `Internals`, top-level files) and **junction the bulk read-only trees** on
  Windows via `mklink /J`: `KSP_x64_Data\StreamingAssets` and the stock
  `GameData\Squad` / `GameData\SquadExpansion` asset payloads point back at the
  dev install (read-only). `Squad` / `SquadExpansion` are stock payloads (they
  ship with KSP and never change between builds of the same KSP version), so the
  drift risk of junctioning them is negligible; they are therefore NOT listed in
  `devSourcedMods` and are junctioned like the other stock asset trees. Junction
  chosen over hardlinking because KSP asset dirs are large trees where a single
  directory junction is O(1) and trivially re-pointable, and over full copy
  because two profiles would otherwise cost 10-16 GB. Non-stock GameData mod
  folders that the profile lists as `devSourcedMods` and are content-hashed are
  COPIED (not junctioned) so a dev GameData change cannot silently drift an
  instance mid-campaign. Record each junction target in the manifest
  (`junctionTargets`; EC-8: junction-target-moved is a verify failure).

- **SETTINGS.** Read the dev `settings.cfg`, record its sha256
  (`settingsBaseSha256`), apply the profile's `[settings]` deltas as pure
  key-replacements (line-oriented, preserve unknown keys/order), write to the
  instance `settings.cfg`. Suppress popups where the game exposes a setting
  (e.g. disable the update/launcher nag, `CHECK_FOR_UPDATES = False` if present).
  Record `settingsDeltasApplied` and the sha256 of the final written instance
  `settings.cfg` (`settingsFinalSha256`) so VERIFY (and the harness) can detect a
  manual edit made OUTSIDE the delta keys, which the per-key delta comparison
  alone would miss.

- **DEPLOY (Parsek).** Never touch the dev instance's GameData. Copy the Parsek
  build from the STAGING location (guard the sibling-worktree bin/Debug race,
  `.claude/CLAUDE.md`):
  1. Determine the source build. Default `Source/Parsek/bin/Debug/Parsek.dll` in
     the current worktree; require `--parsek-dll <path>` if ambiguous.
  2. Copy source -> `harness/provision/.stage/Parsek.dll`; compute sha256.
  3. Copy stage -> `<instanceDir>/GameData/Parsek/Plugins/Parsek.dll`; re-hash
     the installed file; assert it equals the stage hash (EC-9 lock/partial).
  4. UTF-16 grep verification (`.claude/CLAUDE.md` recipe): confirm one or more
     distinctive UTF-16-LE signature strings appear in the installed DLL with
     the expected counts. The signature set is read from a small pinned list so
     the check survives builds; counts recorded in the manifest.
  Also copy the rest of the Parsek GameData folder (version file, toolbar
  textures) from the dev-sourced `Parsek/` EXCEPT `Plugins/Parsek.dll` (which
  comes from staging) and EXCEPT any existing `provision-manifest.json`.

- **INSTALL (stack payloads).** Extract the kRPC release into
  `<instanceDir>/GameData/kRPC/`; drop the built `TestingTools.dll` alongside;
  extract MechJeb2 into `GameData/MechJeb2/`; drop `KRPC.MechJeb.dll` into
  `GameData/kRPC/` (per its README). Hash every installed DLL into the manifest.

- **MM CACHE.** Delete the copied `ModuleManager.ConfigCache`, `ConfigSHA`,
  `ModuleManager.Physics`, `ModuleManager.TechTree` from the instance GameData so
  ModuleManager regenerates them against the instance's actual mod set on first
  boot (EC-2). Rationale: the dev cache reflects the dev mod set; a stock-minimal
  instance with a stale modded cache would load phantom patches. Regenerate, do
  not copy.

- **MANIFEST.** Write `provision-manifest.json` atomically (tmp + rename) with
  every component pin, hash, settings delta, junction target, timestamp, the
  provisioning script's git HEAD, the final instance `settings.cfg` sha256
  (`settingsFinalSha256`, N-4), and the instance `buildID64.txt` sha256
  (`buildId64Sha256`, N-5, cross-checked against `pins.kspVersion`).

- **VERIFY.** Re-read the instance from scratch and cross-check against the
  just-written manifest: every recorded DLL hash matches the on-disk file; the
  UTF-16 grep still passes; junction targets resolve; `settings.cfg` contains the
  deltas AND its full-file sha256 matches `settingsFinalSha256` (catches a manual
  edit outside the delta keys, N-4); and the instance `buildID64.txt` sha256
  matches `buildId64Sha256`, which pins the ACTUAL instance KSP version against
  `pins.kspVersion` (catches an instance built from a different KSP build than the
  pin claims, N-5). This catches a mid-run clobber (EC-10). On `--repair`, a
  detected drift triggers a targeted re-install of only the drifted component,
  then re-VERIFY. Without `--repair`, drift is reported and the run exits
  non-zero.

Idempotency: re-running with the same pins recomputes hashes, finds every
component already at its pinned hash, logs `skip (up-to-date)` per component, and
converges without rewriting bytes. Only drift or a pin change triggers work.

## Edge Cases

Each: trigger -> expected behavior -> v1 or deferred.

- **EC-1 KSP running during provisioning.** Trigger: a KSP process holds an
  instance's files (or the dev install) open. Expected: PREFLIGHT detects the
  running process (by window/exe path against target instance dirs) and ABORTS
  before any write, with a clear "close KSP for instance X" message. Never
  half-provision. v1.
- **EC-2 Stale ModuleManager cache.** Trigger: dev GameData's MM cache reflects
  a different mod set than the target profile. Expected: MM CACHE step deletes
  the copied cache so it regenerates on first boot; never copy the cache. v1.
- **EC-3 GameData mod version drift between profiles / vs manifest.** Trigger: a
  dev-sourced mod folder changed since the instance was built. Expected: VERIFY
  compares the content tree-hash to the manifest; mismatch => drift report (or
  `--repair` re-copies that folder). v1.
- **EC-4 TestingTools build failure vs KSP DLL version.** Trigger: BUILD-TT
  cannot resolve a reference or the source does not compile against the pinned
  KSP DLLs. Expected: ABORT with compiler output and the missing reference;
  never fall back to a stale TestingTools.dll. v1.
- **EC-5 Manifest mismatch at harness start.** Trigger: the harness finds a
  manifest whose pins/hashes differ from its expectations. Expected (harness
  side, specced here): refuse to run, print the field-level diff, exit
  non-zero. The setup script guarantees the manifest is written last and
  atomically so a present manifest always describes a completed provision. v1.
- **EC-6 Disk exhaustion.** Trigger: CLONE or DOWNLOAD runs out of space
  (two 5-8 GB instances + caches). Expected: pre-check free space against an
  estimate before CLONE; on write failure mid-copy, ABORT and leave a
  `.provision-incomplete` marker under `<instanceDir>/GameData/Parsek/` so
  VERIFY/next-run treats it as not-provisioned (never a silent partial). v1.
- **EC-7 Windows path length.** Trigger: deep KSP asset paths under a long
  umbrella root exceed 260 chars during copy. Expected: use extended-length
  paths (`\\?\` prefix) for all file ops; if still exceeded, ABORT naming the
  offending path. v1.
- **EC-8 Junction target moved/deleted.** Trigger: a junctioned read-only tree's
  target (dev install) was moved or removed. Expected: VERIFY resolves each
  recorded junction target; a dangling junction is a verify FAILURE with a
  repair hint (re-run CLONE). v1.
- **EC-9 Parsek.dll locked / partial copy.** Trigger: the instance's Parsek.dll
  is held open, or the copy is truncated. Expected: DEPLOY hashes the installed
  file and asserts it equals the stage hash; a lock (EC-1 should have caught it)
  or partial copy fails the hash and ABORTS. Never trust an mtime-only copy. v1.
- **EC-10 Two provisioning runs racing.** Trigger: two `provision.py` invocations
  target the same instance concurrently. Expected: a lockfile
  (`<instanceDir>/GameData/Parsek/.provision.lock` with pid + timestamp) is acquired at
  PREFLIGHT; a second run sees the lock and ABORTS. Stale locks (dead pid) are
  reclaimed. v1.
- **EC-11 Sibling-worktree bin/Debug race clobbers the staged DLL.** Trigger: a
  concurrent `cd Source/Parsek && dotnet build` in another worktree overwrites
  `bin/Debug/Parsek.dll` between source-select and stage-copy. Expected: DEPLOY
  copies to staging FIRST, hashes the STAGE (not the source), and installs from
  the stage; the stage is immutable for the rest of the run. If the source hash
  differs from the stage hash on a later re-read, log AMBER (informational: the
  dev build changed) but the installed instance is defined by the stage. v1.
- **EC-12 PersistentRotation absent from dev GameData (GT-8).** Trigger:
  modded-compat lists PersistentRotation but the dev install lacks it. Expected:
  record `absent-source` in the manifest; fail only if the profile marks it
  `required = true`; otherwise proceed and log a WARN. v1.
- **EC-13 kRPC release sha256 unrecorded (OPEN pin).** Trigger: `pins.toml` has
  `sha256 = "OPEN"`. Expected: DOWNLOAD computes and prints the hash, then
  ABORTS asking the maintainer to commit it. Never install an unverified
  artifact. v1.
- **EC-14 Wrong kRPC/KRPC.MechJeb pairing under a non-v0.5.4 pin.** Trigger: a
  master kRPC commit is pinned; genhis 0.7.1 ABI may not match. Expected: PAIR
  detects the non-v0.5.4 pin, refuses to guess, prints the web-verification
  decision procedure, and ABORTS. Deferred: automated fork/tag selection.
- **EC-15 Dev settings.cfg missing a key a delta targets.** Trigger: a profile
  delta names a key absent from the dev settings.cfg. Expected: settings-delta
  application APPENDS the key (KSP tolerates it) and logs it; a delta key that is
  a known typo (not in a validated key set) is a WARN. v1.
- **EC-16 Instance dir aliases the dev install.** Trigger: a profile's
  `instanceDir` resolves to the read-only dev install, or a parent/child of it,
  or a path not under `automation/` (a hand-edited or mis-merged profile).
  Expected: PREFLIGHT rejects it with the pure `check_instance_dir_alias`
  predicate and ABORTS before any write. The live primitives overwrite
  settings.cfg, copy the DLL, and DELETE the MM cache; aimed at the dev tree they
  would corrupt the clone source. v1 (guard is live; the destructive primitives
  it protects are the deferred live phases).

## What Doesn't Change

- The dev install (`Kerbal Space Program/`) is never written: not its GameData,
  not its settings.cfg, not its saves. It is a read-only clone/payload source.
- The Parsek build system, the `-p:ForceKspDeploy` gate, and the intentional-only
  deploy behavior (`.claude/CLAUDE.md`) are untouched. This script does its own
  DEPLOY into automation instances and never invokes the csproj post-build copy.
- The `mods/` clones are read-only pin references. The script never checks them
  out to a different ref (it uses `git show`/`git archive`).
- The harness's scenario/coverage machinery (M-A5) is out of scope; this module
  only produces the instances and manifests it consumes.
- No changes to Parsek source, tests, or in-game hooks.

## Backward Compatibility

Greenfield module; no existing instances or manifests to migrate. Forward policy:
the manifest and pin/profile files carry `schema = 1`; a schema bump makes the
harness refuse an old manifest (EC-5 path) rather than silently mis-admit,
consistent with the project's no-migration stance for versioned data. A pin
change is a normal reviewed edit to `pins.toml`; the next provision run detects
the resulting drift and (with `--repair`) converges the instances.

## Diagnostic Logging

Log to `<instanceDir>/GameData/Parsek/provision-log.txt`, append-only, one decision per
line, format `[Provision][LEVEL][Step] message` (Info/Warn/Amber/Error mirroring
ParsekLog), harness-archivable. Every branch logs.

- **PREFLIGHT**: dev-install/mods/pins resolution (paths); KSP-running check
  result; profile loaded (name, mod count); dry-run vs live.
- **PIN**: per component, resolved `tag -> commit`, and `match`/`MISMATCH`
  against `pins.toml`; the GT-2 capability warning (auto-load/Quit/SetLanded
  absent at v0.5.4); the master-commit AMBER when a non-tag pin is used.
- **DOWNLOAD**: url, bytes, `sha256 expected=... actual=... OK|FAIL`; the GT-5
  zip-contents assertion result; OPEN-hash abort.
- **BUILD-TT**: source ref, the two extracted files, csproj references resolved,
  build command, `build OK dll-sha256=...` or `build FAIL <compiler tail>`, and
  the `AutoLoadGame-absent OK|FAIL` reflection assertion (S-4).
- **PAIR**: fork+tag chosen, the `pairedKrpcTag` assertion, and the
  web-verify AMBER + abort on a non-v0.5.4 pin.
- **CLONE**: bytes copied, dirs junctioned with targets (incl. Squad /
  SquadExpansion, B-3), free-space pre-check, instance `buildID64.txt` sha256
  vs `pins.kspVersion` `OK|MISMATCH` (N-5).
- **SETTINGS**: `settingsBaseSha256`, each delta `key old=... new=...`, popup
  suppressions, appended-key warnings (EC-15), `settingsFinalSha256` (N-4).
- **DEPLOY**: source path, `stage-sha256`, `installed-sha256`,
  `hashMatch OK|FAIL`, UTF-16 grep `string=<s> count=<n> expected=<n>`, the
  EC-11 stage-vs-source AMBER.
- **INSTALL**: each stack DLL installed with its hash.
- **MM CACHE**: which cache files deleted and why (regenerate-not-copy).
- **MANIFEST**: atomic write path + final component/hash summary line.
- **VERIFY**: per component `on-disk sha256 == manifest OK|DRIFT`, junction
  resolution, settings presence, `settingsFinalSha256` re-hash `OK|DRIFT` (N-4),
  `buildId64Sha256` re-hash `OK|DRIFT` (N-5); on `--repair`,
  `repairing <component>` + re-verify result. Final
  `[Provision][Info][Verify] instance=<name> result=OK` or a `DRIFT`/`ABORT`
  summary with the non-zero exit reason.
- **ABORT/EC**: every edge case logs `[Provision][Error|Warn][<Step>] <ec-id>
  <detail>` before exit; the `.provision-incomplete` / lock markers are logged
  on write.

Goal (per the plan): the harness, reading only `provision-log.txt` and
`provision-manifest.json`, can reconstruct exactly what was installed, from
which pins, with which hashes, and why any run aborted.

## Test Plan

Every test states the regression it guards. Split into pure-unit (the logic that
does not touch KSP or the network) and a provisioning smoke run.

### Unit-testable (pure functions, no KSP, no downloads)

- **Manifest diff logic** (`compare_manifest(expected_pins, manifest) -> diff`).
  Input: an expected pin set + a manifest dict; output: field-level diff.
  Fails if: a changed component hash, a changed tag/commit, a missing component,
  or a changed settings delta is NOT reported (guards EC-3/EC-5 silent admission
  of a drifted instance). Cover: identical (empty diff), one hash drift, a pin
  bump, an added/removed component, a settings-delta change.
- **Settings-delta application** (`apply_settings(base_lines, deltas) -> lines`,
  pure). Fails if: an existing key is not replaced, key/line order is not
  preserved, an absent key is not appended, or an unrelated key is mutated
  (guards EC-15 and non-determinism). Cover: replace existing, append missing,
  no-op when equal, comment/blank-line preservation.
- **Pin resolution / mismatch** (`resolve_pin(clone_ref_output, pin) -> ok|err`).
  Fails if: a moved tag (tag resolves to a commit != recorded) is accepted
  (guards GT-1 retag/move). Cover: match, mismatch, missing tag.
- (DROPPED) A pure `testingtools_capabilities(krpc_ref) -> caps` pytest was
  considered and dropped as near-vacuous: it would only restate the hand-authored
  capability table back to itself. The real guard is the BUILD-TT reflection
  smoke over the ACTUAL built assembly (see the provisioning smoke run), which
  proves the six RPCs are exported and `AutoLoadGame`/`Quit`/`SetLanded` are
  absent from the bytes that ship. The capability table in `pins.toml` / the
  manifest stays as the harness's admission INPUT, not something a unit test
  re-derives.
- **Junction-target recording/resolution** (`verify_junctions(manifest, fs) ->
  ok|dangling`). Fails if: a dangling junction is reported OK (guards EC-8).
- **UTF-16 signature grep** (`count_utf16(dll_bytes, s) -> n`). Fails if: a
  present signature counts 0 or an absent one counts >0 (guards the
  `.claude/CLAUDE.md` DLL-identity check and EC-9/EC-11). Cover: synthetic byte
  buffers with known UTF-16-LE substrings.
- **Free-space / disk-budget estimator and path-length guard**
  (`estimate_instance_bytes(profile)`, `is_path_too_long(path)`). Fail if an
  over-budget profile or an over-260-char path is not flagged (guards
  EC-6/EC-7).
- **Lock acquire/reclaim** (`acquire_lock(dir, pid, now)`, pure over an injected
  clock/pid). Fails if: a live lock is stolen or a stale (dead-pid) lock is not
  reclaimed (guards EC-10).

These follow the project convention: pure logic is `internal`/module-level and
directly testable; put them in `harness/provision/` with a `test_provision.py`
(pytest) so they run in the per-PR cadence without KSP.

### Verified by a provisioning smoke run (needs the dev install + network)

- **End-to-end stock-minimal provision** on a scratch umbrella copy: run
  `provision.py --profile stock-minimal`, then assert the manifest exists, every
  recorded DLL hash matches on disk, the UTF-16 grep passes, `settings.cfg`
  carries the deltas, MM caches are absent, and junctions resolve. Fails if any
  step half-provisions or the manifest disagrees with disk (the whole point of
  M-A6). This is the harness's first real dependency and doubles as the Phase 2
  smoke gate (plan section 11).
- **Idempotency re-run**: run again immediately; assert every component logs
  `skip (up-to-date)` and no bytes are rewritten (guards convergence).
- **Drift + repair**: mutate one installed DLL, run `--repair`; assert only that
  component is re-installed and VERIFY passes (guards EC-3 repair path).
- **KSP-running abort**: hold a file in the instance open, run; assert clean
  ABORT with no writes (guards EC-1/EC-9).
- **BUILD-TT smoke**: assert TestingTools.dll builds from the v0.5.4 2-file shim
  against the dev KSP DLLs + release kRPC binaries and exports the six RPCs
  (reflect over the built assembly), that the flags/Quit/SetLanded are absent
  (guards GT-2/GT-4), and that the `AutoLoadGame` type is ABSENT from the built
  assembly (guards S-4: the dropped auto-loader must not race the seam's
  `LoadGame` boot). This reflection smoke over the REAL built assembly is the
  authoritative capability guard.

The smoke runs are operator-driven (they need the GPU-less-but-interactive dev
PC and network); the setup script emits its own pass/fail summary line so an
unattended scheduled run can gate on it.

---

## Open Items (require web verification at install time)

- **O-1 (EC-13)**: exact sha256 of `krpc-0.5.4.zip` and the chosen MJ 2.15 build
  zip. Command: download from the pinned URLs, `sha256sum <file>`, record in
  `pins.toml`. Until recorded, DOWNLOAD aborts by design.
- **O-2 (GT-5)**: confirm the kRPC 0.5.4 zip layout.
  `unzip -l krpc-0.5.4.zip | grep -iE 'testingtools|KRPC.Core.dll|KRPC.SpaceCenter.dll|Google.Protobuf.dll'`
  -- expect no TestingTools, expect the three compile references.
- **O-3 (GT-7)**: the exact MechJeb2 2.15 dev build number + download URL (no git
  tag exists). Source: MechJeb2 CI / SpaceDock / CurseForge release matching KSP
  1.12.
- **O-4 (GT-8/EC-12)**: decide PersistentRotation for modded-compat -- drop it or
  source a KSP-1.12 build separately. If sourced, add its pin+hash to
  `pins.toml`.
- **O-5 (GT-6/EC-14)**: only if a non-v0.5.4 kRPC is ever pinned -- confirm the
  genhis-vs-darchambault fork+tag against the pinned kRPC and MJ build via the
  two releases pages named in the PAIR step.
