# harness/ - Parsek automated-testing module

This directory is a self-contained module. It is designed so it could later be
split into its own repository and consumed by Parsek as a git submodule at this
same path. Keep it that way.

## Ownership boundary

Everything the harness fetches or generates lives UNDER `harness/`:

- Code: `run.py`, `lib/` (`hlib.py` pure decision library + `oracle.py`, the
  M-B2 pure ledger oracle), `provision/` (`provlib.py` pure, `provision.py`
  shell), `missions/` (M-B1: mission shells + `lib/mlib.py` pure mission
  decisions + `bootstrap_venv.py`), and their `test_*.py`. run.py drives
  seam-only scenarios AND autopilot scenarios (the mission handoff spawns the
  mission subprocess with the venv python; venv admission runs at pre-launch
  ADMIT), plus the M-B2 ledger-oracle verifier (pre-launch seed baseline,
  per-run action manifest, expected-vs-save diff -> PARSEK-FAIL(ledger) on
  hard drift).
- Declarative inputs: `scenarios/*.toml` (incl. `[expectations.ledger]`),
  `coverage/registry.toml`, `provision/pins.toml`, `provision/profiles/*.toml`,
  `missions/<name>.schema.toml` + `missions/requirements.txt`.
- Generated per-run (gitignored): `results/<runId>.json` + `<runId>.manifest.json`
  + `<runId>_mission.json`.
- Caches + scratch (gitignored): `provision/.cache/` (release zips, the
  module-owned git source clones `krpc-src` / `krpc_mechjeb-src`, kRPC compile
  refs, the built `TestingTools.dll`) and `provision/.stage/`.
- Generated outputs (gitignored): `results/`, `coverage/coverage.*`, `flake.json`.

Provisioned KSP instances live at the umbrella root under `automation/`
(gitignored), NOT here. The dev KSP install (`../Kerbal Space Program/`) is a
documented READ-ONLY source (never written).

## The only reach-out into Parsek

The harness consumes a fixed Parsek-repo contract surface (which would remain in
the Parsek repo under a submodule split): `scripts/analyze-recordings.ps1`,
`scripts/validate-ksp-log.ps1`, `scripts/inject-recordings.ps1`,
`scripts/collect-logs.py`, the dotnet-test-hosted `Parsek.Analyzer`, the deployed
`Source/Parsek/bin/Debug/Parsek.dll` under test, and the launch-time seam/hooks
env contracts (`PARSEK_TEST_COMMANDS`, `PARSEK_AUTORUN_TESTS`, etc.).

Do NOT add new reads or writes outside `harness/`, `automation/`, the target
instance, the read-only dev KSP install, or that contract surface. The pinned
kRPC / KRPC.MechJeb git sources are cloned into `provision/.cache/<comp>-src`
(NOT the umbrella `mods/` clones); `--krpc-src <path>` optionally overrides the
kRPC source with an existing clone.

The instance's kRPC `PluginData/settings.cfg` is OWNED by the provisioner: every
provision/repair pass overwrites it with the complete golden template
(`provlib.KRPC_GOLDEN_SETTINGS_LINES`), discarding any hand or in-game edits by
design - a PARTIAL file zero-defaults every omitted key and silently disables
all RPC execution (maxTimePerUpdate=0). Tune kRPC by editing the template, not
the instance file.

Full enumeration + submodule split recipe:
`docs/dev/design-autotest-stack-setup.md` ("Module boundary and submodule
readiness"), cross-referenced from `docs/dev/design-autotest-harness-core.md`.

## Running the tests

```
cd harness
python -m unittest discover -s lib -q
python -m unittest discover -s provision -q
python -m unittest discover -s missions/lib -q
```

Stdlib only (no pytest, no third-party deps) on the BASE interpreter: the
mission shells lazy-import krpc inside their connect function, so all three
discovery roots run with no venv. The ONLY third-party python lives in the
mission venv (`missions/requirements.txt`, bootstrapped by
`missions/bootstrap_venv.py`, gitignored `.venv/`), used exclusively by the
mission subprocess at flight time. A `--dry-run` provision needs no
network, downloads, or writes outside `harness/`:

```
python provision/provision.py --profile stock-minimal --dry-run
```
