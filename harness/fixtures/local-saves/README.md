# Operator-local fixture saves

Nothing here is committed except this file and `.gitignore`. A spec whose
`fixture.saveTemplate` starts with `fixtures/local-saves/` names a host the OPERATOR
stages from a save he already has, rather than one the repo carries.

## Why the prefix exists

Every committed fixture under `../saves/` is small, synthetic and shaped: a clean-slate
career, a pad craft, a harvested recorded tree. That is right for a lane that asserts a
mechanism.

The GUI census asserts nothing about a mechanism - it walks every Parsek window and
takes a screenshot so a human (or a supervising agent) can look at the layout. What it
needs from a host is DENSITY: a Missions window with missions in it, a Recordings table
with rows, a Career State window with contracts and strategies, a Kerbals roster with
kerbals, a Logistics window with routes. A window with nothing in it photographs as an
empty box, which tells a reviewer nothing.

The only save with that density is the operator's own long-lived career. It cannot be
committed (tens of megabytes, and it is a personal save), and it cannot be synthesised -
the point is the accumulated content, not a shape a builder script could mint.

## Staging one

```bash
python harness/tools/stage_local_fixture.py \
    --from "../Kerbal Space Program/saves/c1" --as c1-gui
python harness/tools/stage_local_fixture.py --list
python harness/tools/stage_local_fixture.py --as c1-gui --remove
```

The copy is verbatim by default: `persistent.sfs`, `persistent.loadmeta`, every
`Parsek/<dir>` sidecar tree, `Ships/`, `AddOns/`. `--no-quicksaves` drops the save's own
quicksave / backup `.sfs` files (keeping `persistent.sfs` and everything under
`Parsek/`), which on a long career is usually most of the bytes and none of the content
a census reads.

The leaf name is also the RUN SAVE NAME inside the KSP instance, so it follows the same
filename-safe rule `validate_spec` applies to `runSaveName`.

## What holds with the fixture absent

Every committed test suite. `hlib.is_local_fixture_template` classifies such a template
from its PATH (not from a hand-maintained spec-id list), the per-spec cells that read a
template off disk skip a local-fixture lane with a stated reason, and a run that tries
to fly one is refused pre-boot as `INVALID(staging)` with the staging command in the
error (`hlib.local_fixture_hint`).

Two properties are load-bearing and easy to break:

- **The prefix is NOT under `fixtures/saves/`.** `test_saveparse.py`'s
  `test_fixture_set_is_exactly_the_committed_set` lists the directories under
  `fixtures/saves/` and compares them to a pinned set, so a staged local fixture there
  would red that cell on the one machine that can actually fly the lane - green for
  everyone who cannot, red for the operator.
- **A local-fixture lane is `tier = "operator"`.** It is run on request, never on a
  cadence, because on any other machine it cannot run at all.

## What a local fixture must NOT be used for

Anything whose verdict depends on the host's contents. A local fixture is unreproducible
by construction - it is one person's save at one moment - so a lane on it can assert
that a window DREW, never what it drew. Every gating expectation (`recordings.count`,
`saveParse` blocks, the ledger oracle) belongs on a committed fixture.
