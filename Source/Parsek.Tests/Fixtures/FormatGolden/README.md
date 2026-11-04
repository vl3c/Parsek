# Frozen format goldens

These are saves written by the 1.0 save format (owner ruling 2026-11-04: from 1.0 on,
every build loads every save any 1.0+ build wrote). They are test inputs that never change.

## Rules

- **The files are frozen.** Never edit, re-harvest or regenerate a `vN` folder once it is
  committed. `SaveFormatGoldenGateTests` checks every file against `checksums.txt` and fails
  with "golden saves are frozen; add a new vN folder instead" on any edit, removal or
  addition.
- **A format bump adds a new folder.** When any `SaveFormat.All` row's `Current` moves, write
  a new `vN` folder (next N) with the generator for that version, carrying a `formats.txt`
  with the new value. The gate fails, naming the format and version, until some golden
  folder's `formats.txt` covers every version from that format's baseline to its current.
- **Older folders stay forever.** Every `vN` keeps loading on every later build:
  `FormatGoldenV1Tests` (and its later siblings) load each folder through the production
  readers. A red cell there means a released save no longer loads; fix the reader or add an
  upgrade step, never the golden.

## v1

`v1/` was written once by `FormatGoldenV1GeneratorTests.WriteV1` (skipped unless
`PARSEK_WRITE_FORMAT_GOLDEN=v1`), which runs the production save writers in
`ParsekScenario.OnSave`'s phase order over generator-built state and wraps the PARSEK node in
the stock `fresh-career` harness save. It holds:

- `persistent.sfs`: two committed trees (a lander with a separation branch point,
  parent-anchored debris with a Relative section, a Re-Fly fork in CommittedProvisional; a
  looping landed rover), missions (renamed, cloned with an excluded interval, looping),
  a vessel name decision with a link and a dock-absorb row, kerbal slots, crew replacements,
  rescue-placed markers, group hierarchy and hidden groups, milestone replay state, the
  GAME_STATE node (ledger, events, contract snapshot, baseline, milestones, pending
  science), rewind point, supersede row, ledger tombstone, the Re-Fly marker, merge journal,
  stock-action intent and switch-segment session, committed and dormant routes, every route
  event type, retired route cargo, the stock pool reference, the ledger held window, rewind
  vessel holds and the replay scope (the last five, the take-back era and the settings lock
  filled field by field by the save round-trip harness).
- `Parsek/Recordings/`: PSK0 `.prec` and PSN0 `_vessel.craft` / `_ghost.craft` sidecars, the
  `.pann` cache and the readable `.txt` mirrors the writer produced.
- `Parsek/GameState/`: `ledger.pgld`, `events.pgse`, `milestones.pgsm`, `baseline_9.pgsb`.
- `Parsek/RewindPoints/`: the rewind point's quicksave (a copy of `persistent.sfs`).
- `formats.txt`: the `SaveFormat.All` versions this folder carries.
- `career/`: a verbatim copy of `Source/Parsek.Tests/Fixtures/C2CareerPostFix/`, a career
  harvested from a real driven flight on the 1.0 formats. It predates the
  `parsekSaveFormat` stamp (no harvested save carries it), so its save-wide stamp reads
  Missing; every other stamp is current.

Skipped stores, each with its reason:

- Rewind retirements: no 1.0 writer produces one (rows predate 2026-10-25), and
  `Fixtures_CarryNoRewindRetirement` forbids them in committed fixtures.
- The test batch marker: automation only, and its presence makes a load revert the save.
- Active and pending (in-flight) trees, quickload-resume saves under `Parsek/Saves/` and
  inert trees: the `career/` copy carries a `Parsek/Saves/` file; the in-flight and inert
  shapes are covered by their own round-trip and passthrough suites.
