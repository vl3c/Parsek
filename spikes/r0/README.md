# R0 spikes (branch `spike/mp-r0`, never merged)

Throwaway experiments that answer the R0 design questions of the multiplayer plan
(`docs/dev/plans/multiplayer/r0-spikes.md`). This branch is pushed to GitHub as a record
and is never merged and never opened as a pull request. Each spike's product is a report
in `docs/dev/research/mp-spike-<Sx>-<YYYY-MM-DD>/README.md`, landed on `multiplayer`
through a docs-only pull request from a fresh worktree (template: plan section 6.4).

First run on the owner's PC: [`PC-SETUP.md`](PC-SETUP.md) (S0: build, the three
instances, the deployed-DLL check, the three proof sessions).

## The host (S0)

- `Source/Parsek/InGameTests/Spikes/SpikeArming.cs`
  - `SpikeHost`: a `[KSPAddon(Startup.Instantly, true)]` addon that reads `PARSEK_SPIKE`
    once (a comma list, `S4a,S9`; ids match case-insensitively, a prefix never matches).
    Unset or empty: no log line, no Harmony instance, no patch; the DLL behaves as
    `multiplayer`. Armed: logs `[Parsek][INFO][Spike] armed: <list> build=PARSEK-SPIKE-R0`,
    warns once per armed id that no registration names, applies each armed spike's
    patches through the Harmony id `com.parsek.spike`, and ends with one
    `patch summary:` line. `SpikeHost.IsArmed("S4a")` tells a spike's in-game test
    whether its id is armed in this process.
  - `SpikeRegistry.All`: the one list. A spike adds ONE line,
    `new SpikeRegistration("<id>", <Type>.Apply),` where `Apply(Harmony h)` calls
    `h.Patch(...)` by hand. A spike patch class never carries `[HarmonyPatch]`
    (`ParsekHarmony` would apply it in every session); the xUnit cell
    `SpikeArmingTests.NoSpikeType_CarriesHarmonyPatchAttribute` reds when one does.
  - `SpikeArming.SpikeTag("S4a")` is the log tag `Spike.S4a`; every measurement is one
    `ParsekLog` line on that tag plus one CSV row.
- `Source/Parsek/InGameTests/Spikes/SpikeCsv.cs`: rows to
  `<KSP root>/parsek-spike/<Sx>/<name>.csv`, invariant culture (`1.5`, never `1,5`),
  RFC 4180 quoting, header written only into a new or empty file.
  `SpikeCsv.AppendRow(id, name, header, fields...)` for one-shot rows,
  `using (var w = SpikeCsv.Open(id, name, header...)) w.WriteRow(...)` for per-frame rows.
  After a session the owner copies `<instance>/parsek-spike/<Sx>/` to
  `<umbrella>/logs/spikes/<Sx>/<YYYY-MM-DD>/`.
- xUnit: `Source/Parsek.Tests/Spikes/SpikeArmingTests.cs` (classes `SpikeArmingTests`,
  `SpikeCsvTests`). Cloud: `scripts/cloud-test.sh -class Parsek.Tests.Spikes.SpikeArmingTests -class Parsek.Tests.Spikes.SpikeCsvTests`.
- `spikes/r0/s0_check.py`: read-only checks used by `PC-SETUP.md` (deployed DLL markers
  and hash, link listing that never walks a junction, tree sizes, the proof-session log
  checks). `python spikes/r0/s0_check.py --selftest`.

## Instances (provisioned by the owner only)

| Instance | Profile | kRPC ports | Used by |
|----------|---------|-----------|---------|
| `automation/spike-r0` | `harness/provision/profiles/spike-r0.toml` (stock-minimal, no graphics overrides) | 50060 / 50061 | every spike that needs the game |
| `automation/spike-r0-nodlc` | hand copy of `spike-r0` without the `SquadExpansion` junction (`PC-SETUP.md` step 8) | 50060 / 50061 (copied settings) | S3, S5 step 4, C2 sessions |
| `automation/spike-r0-content` | `harness/provision/profiles/spike-r0-content.toml` (stock-tree) | 50080 / 50081 | S6 step (f) only |

The two profiles are registered in `harness/lib/slots.py` (`PROFILE_PORT_BASE`) on this
branch only, so the provision suite's profile-set pin stays green and each instance gets
its own kRPC pair. `spike-r0` and `spike-r0-nodlc` share 50060 / 50061: never run kRPC
in both at once. The S1b scan therefore passes `--rpc-port 50060 --stream-port 50061`.

## Entry points per spike

Ids in `PARSEK_SPIKE` are the ones the plan names; where the plan names none, the
spike's author picks one and adds its `SpikeRegistry` line.

| Spike | Code | In-game category / entry | `PARSEK_SPIKE` and other env | Instance |
|-------|------|--------------------------|------------------------------|----------|
| S0 | `InGameTests/Spikes/SpikeArming.cs`, `SpikeCsv.cs` | `SpikeHost` (addon) | `S0` (no patches; arming proof) | all three |
| S1b | `harness/tools/kerbin_equator_scan.py` (tooling PR on `multiplayer`) | the scan tool over kRPC | none | `spike-r0` |
| S2 | `InGameTests/Spikes/S2/SpikeS2Science.cs` | `SpikeS2`: `DumpTaggedColliders`, `SampleBiomeGrid`, `ReadLandedAtOnPad`; override `SpikeS2BiomeOverride` | plan names no id for the override | `spike-r0` |
| S3 | `InGameTests/Spikes/S3/SpikeS3Looks.cs`, `spikes/r0/S3/gamedata/` | `SpikeS3`: `ApplySuitToRoster`, `ListSuitCombos`, `BuildGhostKerbal` | none named | `spike-r0`, `spike-r0-nodlc` |
| S4a | `InGameTests/Spikes/S4/SpikeS4Probe.cs` | `SpikeS4`: `DumpKscState`; `SpikeS4EventOrder`; `SpikeS4TurnBack` | `S4a` (event order), `S4a-turnback` (turn-back prefix) | `spike-r0` |
| S4b | `InGameTests/Spikes/S4/SpikeS4Move.cs` | `SpikeS4Move` | `S4b`, plus `PARSEK_SPIKE_SITE=<lat>,<lon>` | `spike-r0` |
| S5 | `InGameTests/Spikes/S5/SpikeS5Scenery.cs` | `SpikeS5`: `MeasureClone`, `FrameCost`, `HeightProbe` | none named | `spike-r0` (+ `spike-r0-nodlc` for step 4) |
| S6 | `InGameTests/Spikes/S6/` (`SpikeS6.cs` patches, `SpikeS6Host.cs` hooks + save watch, `SpikeS6Menu.cs` menu clone + dialog + Difficulty + Create, `SpikeS6ContentDump.cs`, `SpikeS6ProbeScenario.cs`, pure `SpikeS6Cfg` / `SpikeS6Difficulty` / `SpikeS6Options` / `SpikeS6Content`); `harness/scenarios/SPIKE-S6-mark-survives.toml`; `spikes/r0/S6/` (`make_template.py`, `template/mark.cfg`, `check_mark.py`, `content_diff.py`, `s6cfg.py`, `patches/`); runbook [`S6/PC-RUN.md`](S6/PC-RUN.md) | lane `SPIKE-S6-mark-survives`; the menu steps run from the armed host at the main menu (no in-game test category) | `S6`; `PARSEK_SPIKE_S6_AUTO=1` drives the menu steps, plus `_DIFF=edit/accept/cancel`, `_CONTENT=1`, `_LABEL`, `_SAVE`, `_FOLDER`, `_MODE`, `_RESUME`, `_QUIT`, `_SNAPSHOTS` | lane and step (f): `spike-r0-content` (stock-tree career); menu steps: `spike-r0` |
| S7 | `InGameTests/Spikes/S7/SpikeS7FlagReplica.cs` | `SpikeS7` | none named | `spike-r0` |
| S8a | `spikes/r0/S8/fileaccess.py` (no game) | script | none | none |
| S8b | `InGameTests/Spikes/S8/SpikeS8Io.cs` | `SpikeS8`: `MainThreadRead` | none named | `spike-r0` |
| S9 | `InGameTests/Spikes/S9/SpikeS9Cheats.cs` | hand-applied postfixes, 1 Hz poll | plan names no id (`S9` is the natural one) | `spike-r0` |
| S10a | `Source/Parsek.Tests/Spikes/RelativeCopyCensusTests.cs` (manual xUnit; pure part in `RelativeCopyCensus.cs`), `spikes/r0/S10/summarize_census.py`, PC run sheet `spikes/r0/S10/PC-RUN.md` | xUnit `Manual_RelativeCopyCensus_WritesCsv` | `PARSEK_SPIKE_S10_SAVE` (one copy or several, `;`), `PARSEK_SPIKE_OUT` | none (cloud or PC) |
| S10b | `InGameTests/Spikes/S10/SpikeS10Flatten.cs` | `SpikeS10` | `S10b` | `spike-r0` |
| S11 | `InGameTests/Spikes/S11/` (`SpikeS11Cost.cs` the in-game sweep, pure `SpikeS11Plan.cs` / `SpikeS11FirstOpen.cs`); injection preset `spike-s11-ghosts` (`Source/Parsek.Tests/Spikes/SpikeS11Fixture.cs`, fact `SyntheticRecordingTests.InjectSpikeS11Ghosts`); `harness/scenarios/SPIKE-S11-ghost-cost.toml`; `spikes/r0/S11/summarize_cost.py`; runbook [`S11/PC-RUN.md`](S11/PC-RUN.md) | `SpikeS11`: `GhostCostSweep` (isolated), flown by lane `SPIKE-S11-ghost-cost` | `S11` (plan names none); `PARSEK_SPIKE_S11_PASS=asis\|uncapped`, `_FRAMES` (default 600), `_SHOTS=0` turns screenshots off | `spike-r0` (re-provisioned: the lane reads its manifest) |
| S12 | `InGameTests/Spikes/S12/SpikeS12Crypto.cs` | `SpikeS12`: `CryptoProbe` | none named | `spike-r0` |

Every in-game spike test is `AllowBatchExecution = false` and runs alone through
Ctrl+Shift+T or the `RunTests` seam verb (plan 6.2); the verb runs it only with
`isolated = "true"` and the test's `RestoreBatchFlightBaselineAfterExecution = true`. A frame-cost session (S5's frame
cost, S8b, S10b, S11) runs with nothing else open.

## Rules that still hold on this branch

- Build the spike DLL with `-p:SkipKspDeploy=true` from the worktree root
  (`dotnet build Source\Parsek\Parsek.csproj -c Debug -p:SkipKspDeploy=true`); a build
  started in `Source\Parsek` deploys to the dev install.
- Never write into the repo tree or the umbrella root: raw output goes to
  `<umbrella>/logs/spikes/<Sx>/<YYYY-MM-DD>/`.
- Never delete recursively through a junction (`PC-SETUP.md`, "Forbidden commands").
