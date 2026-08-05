---
name: run-tier
description: Fly one harness tier (daily|nightly|operator) on request and report the outcome
---

# Run a harness tier

The operator asks for a tier by type. Fly exactly that tier, report what
happened. Mechanics live in `harness/README.md` -> "Running a tier on request";
this file is the procedure.

**Never** provision without asking, never re-run a failure, never queue behind a
busy machine.

## 1. Coordination preflight

The machine runs ONE automation KSP at a time.

- Read `<umbrella>/automation/.ksp-machine.lock` (umbrella = parent of the
  worktree). A live holder means a sibling run or a provision owns the box.
  Run from a SIBLING worktree of the umbrella (`<umbrella>/Parsek-<branch>/`):
  a session worktree under `.claude/worktrees/` derives the wrong umbrella —
  it reads a nonexistent lock (concluding "free" while a sibling really holds
  it) and resolves no instance. Move to a sibling worktree first.
- Check for a live `KSP_x64.exe` (`tasklist /FI "IMAGENAME eq KSP_x64.exe"`) —
  an interactive game session blocks a run too (one GPU).

If either is busy: **report the holder (pid, worktree, selection, since) and
STOP.** Do not wait, do not poll, do not queue. The operator decides when to ask
again. (The runner enforces this itself and exits 3; checking first lets you say
so without leaving a skipped-run artifact.)

## 2. Build / instance agreement

Harness flights use `automation/stock-minimal`, NOT the dev install. Confirm
which build the instance carries before flying:

```bash
# from the REPO ROOT, so the build does not deploy to the dev instance
dotnet build Source/Parsek/Parsek.csproj
```

Hash-compare `Source/Parsek/bin/Debug/Parsek.dll` against
`automation/stock-minimal/GameData/Parsek/Plugins/Parsek.dll`.

On a mismatch: **tell the operator which build the instance carries and ask
before provisioning.** `provision/provision.py --profile stock-minimal` deploys
*the invoking worktree's* DLL — which may be a WIP build — so that is a human
call, always. Never provision silently.

## 3. Fly it

```bash
cd harness && python tools/tier_runner.py --tier <daily|nightly|operator>
```

Run it in the background and monitor. It takes minutes to hours. Do not attach a
timeout of your own: run.py owns per-spec budgets. Tail
`harness/results/tier-runs/<tier>_<stamp>.log` for progress.

## 4. Report

Give the operator: the `history.txt` line, the verdict tally, and
`harness/results/index.html` for the detail. Exit codes: 0 GREEN, 1 RED,
2 NEEDS-PROVISION, 3 SKIPPED, 4 runner error.

- **PARSEK-FAIL** — a finding. File it in `docs/dev/todo-and-known-bugs.md`.
  NEVER re-run it away; run.py already applied its retry policy.
- **NEEDS-PROVISION** — the instance is not fit. Report it; provisioning is the
  operator's call (step 2).
- **KILLED** — a budget kill. Name it explicitly; it must never hide behind the
  passes beside it.
- **XPASS** — amber. An expected-fail guard now passes: confirm the bug is
  closed, then remove its `expectedFail` key.
