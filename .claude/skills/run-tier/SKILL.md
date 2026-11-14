---
name: run-tier
description: Fly one harness tier (daily|nightly|operator|longhaul), or every lane (full), on request and report the outcome
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
- Check for a live `KSP_x64.exe` (`MSYS_NO_PATHCONV=1 tasklist /FI "IMAGENAME eq KSP_x64.exe"`;
  without the prefix Git Bash rewrites `/FI` into a path and tasklist errors out) —
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
cd harness && python tools/tier_runner.py --tier <daily|nightly|operator|longhaul>
```

Run it in the background and monitor. It takes minutes to hours (`longhaul`,
the >= 30 min interplanetary lanes, is about 5 h of p50 wall on its own). Do not attach a
timeout of your own: run.py owns per-spec budgets. Tail
`harness/results/tier-runs/<tier>_<stamp>.log` for progress.

Any console you capture yourself (a provision run, a single `run.py --id`) goes to
`<umbrella>/logs/console/<YYYY-MM-DD>/<label>.log` (`../../logs/console/...` from
`harness/`), never the umbrella root.

## 3b. The full run (`--tier full`)

When the operator asks for "all the tests" / a full run: every lane of every
tier on every install (`harness/README.md` -> "The full run"). It runs about a
day and a half, so:

1. Build the commit to test in a sibling worktree (step 2), then look first:
   `python tools/tier_runner.py --tier full --plan-only` (plan + admission of
   every install, flies nothing, takes no lock).
2. If an install is unfit, the operator's "run everything" request covers
   provisioning ONLY when they said which build to test; otherwise ask. Then
   `python tools/tier_runner.py --tier full --provision` provisions every
   install from this worktree (reference first) and flies.
3. To free the PC mid-run: `python tools/tier_runner.py --stop` (the lane in
   flight finishes; nothing else starts; exit 3 STOPPED). Later:
   `python tools/tier_runner.py --tier full --resume` flies only what is left.
4. Report the history line's `whole-run=[...]` tally (latest verdict per lane
   across every invocation, plus `pending=N`).

## 3c. Parallel runs on 2-3 KSP copies (`tools/parallel_runner.py`)

Use this for any batch of lanes (a tier, or a list of ids) when the operator
wants it fast. Mechanics: `harness/README.md` -> "Parallel runs" and "Install
slots". On the owner's PC a daily tier takes about 21 min on 2 copies and
about 13 min on 3; serial it takes about 25 min.

1. **Slot count.** Use `--slots 2` while the owner is using the PC. Use
   `--slots 3` only when the owner is away or says so. Change it mid-run with
   `set-slots --name <run> 2|3`.
2. **Copies.** Copy 1 is `automation/stock-minimal`. Copies 2 and 3 are
   `automation/stock-minimal-2` and `automation/stock-minimal-3`; they are
   already provisioned on the owner's PC.
   - Every copy must carry the SAME `Parsek.dll`. Otherwise the runner exits 2
     and names the odd slot.
   - To align them, provision all of them from your worktree's build:
     `python provision/provision.py --profile stock-minimal`, then the same
     command with `--copy 2` and with `--copy 3`.
   - Provisioning replaces the DLL other threads use, so it needs the
     operator's go and a PC turn (step 2).
3. **Run** from `harness/` in a sibling worktree. Put the lane list in a text
   file, one id per line, with `#` comments allowed. Or pass a tier:

   ```bash
   python tools/parallel_runner.py run --name <run> --ids-file lanes.txt --slots 2
   python tools/parallel_runner.py run --name <run> --tier daily --slots 2
   python tools/parallel_runner.py status --name <run>
   python tools/parallel_runner.py stop --name <run>      # running lanes finish
   python tools/parallel_runner.py resume --name <run>    # flies what is left
   ```

   - Run it in the background, logging the console to
     `<umbrella>/logs/console/<date>/<run>.log`.
   - `--dry-run` prints the plan and flies nothing.
   - Every window goes to the second virtual desktop. If the PC has only one
     desktop, the runner minimizes the windows and prints a hint.
   - Lanes that move the cursor or need focus (LT-1, GUI-7, pointer or hover
     steps) are skipped unless you pass `--include-exclusive`; then they fly
     last, alone.
4. **Read the results** in `results/parallel/<run>/`.
   - `summary.txt` has one line per lane (`<ended> <VERDICT> <id> attempts=N
     wall=Ns slot=S runIds=...`) and a `# totals` line.
   - `runner.log` and `events.jsonl` show every wait with its reason
     (dispatch gap, memory guard, slots full).
   - The exit codes and the verdict rules are run.py's (step 4).
   - Afterwards, tell the coordinator which DLL all three copies now carry.

## 4. Report

Give the operator: the `history.txt` line, the verdict tally, and
`harness/results/index.html` for the detail. Exit codes: 0 GREEN, 1 RED,
2 NEEDS-PROVISION, 3 SKIPPED or STOPPED, 4 runner error.

- **PARSEK-FAIL** — a finding. File it in `docs/dev/todo-and-known-bugs.md`.
  NEVER re-run it away; run.py already applied its retry policy.
- **NEEDS-PROVISION** — the instance is not fit. Report it; provisioning is the
  operator's call (step 2).
- **KILLED** — a budget kill. Name it explicitly; it must never hide behind the
  passes beside it.
- **XPASS** — amber. An expected-fail guard now passes: confirm the bug is
  closed, then remove its `expectedFail` key.
