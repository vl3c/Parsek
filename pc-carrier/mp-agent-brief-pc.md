# Parsek multiplayer: common brief for a task agent on the owner's PC

Carrier file on the never-merged branch `mp/pc-carrier`. Written by the multiplayer
implementation thread (the supervisor, in the cloud). The Remote Control session on the PC
is the local dispatcher: it starts one agent per task with this brief plus the task's row,
checks the agent's evidence, pushes the finished sub-branch and reports to the supervisor.

You implement ONE task of the merged multiplayer plan for Parsek (a Kerbal Space Program mod,
C# net472 + a Python test harness). You are a peer engineer: think, re-read the code, and say
so when the plan is wrong.

## Read before you write

1. `.claude/CLAUDE.md` of the checkout (repo rules; binding).
2. `docs/dev/plans/multiplayer/README.md` section 3 (the eleven binding rules; where a plan
   file says "on main", read "on `multiplayer`").
3. Your task's section in its plan file (named in your task row), plus that file's section 3
   ("what the code looks like today") and its "what other plans must know" part where they
   touch your task.
4. `docs/dev/plans/multiplayer/task-index.md` section C.1 (hot files) for the files you edit.
5. The design sections your task cites: `docs/dev/design-competitive-multiplayer.md`.

The plan's file paths and line numbers were measured on 2026-11-15; the code has moved a
little. Re-read the code and re-anchor. If the plan text for your task is wrong or stale, fix
that text in the same commit and say so in your report. Do not re-plan and do not widen the
task.

## Where you work

- The umbrella folder is `C:\Users\vlad3\Documents\Code\Parsek`; the main git checkout is its
  child `Parsek`. NEVER edit, build or commit inside the main checkout. Create your own
  sibling worktree and sub-branch (names are in your task row):
  `cd Parsek; git fetch origin <base>; git worktree add ../Parsek-<wt> -b <branch> origin/<base>`
  where `<base>` is `multiplayer` unless your task row names another base (a task stacked on
  an unmerged sibling branch). Every pull request targets `multiplayer`, never `main`.
- Do not touch another agent's or another session's `Parsek-*` worktree. Other agents work in
  parallel; the hot files they hold are named in your task row.
- Never run bare `git stash`. Set work aside with a WIP commit.
- Commit locally on your sub-branch. Do NOT push, do NOT open a pull request, do NOT call any
  GitHub tool: the dispatcher session pushes your sub-branch (that branch only, never
  `multiplayer` or `main`), and the supervisor opens the pull request.
- Do not spawn subagents.

## The game on this PC: hands off

- A harness flight session is running on this PC at the same time (it holds the machine
  lock and flies KSP copies under `automation/`). You must NOT provision, fly a lane, launch
  KSP, run `harness/run.py`, `provision.py`, `parallel_runner.py` or `tier_runner.py`, or
  touch anything under `automation/` or `Kerbal Space Program/`.
- NEVER deploy a DLL to a game: no `dotnet build` started from a `Source/Parsek` directory
  without `-p:SkipKspDeploy=true`, and never `-p:ForceKspDeploy=true`. `dotnet test` builds
  Parsek through the project reference and never deploys; that is the normal way to build.
- In-game or flight proof your task owes ("PC: yes", "in-game", "lanes", "flight") is NOT
  yours to fly: name the exact lane ids or in-game categories and the command in your
  report, and the supervisor schedules them on a combined proof branch.

## Build and test (Windows, PowerShell 7)

- xUnit: `cd Source/Parsek.Tests; dotnet test` (filter while iterating:
  `dotnet test --filter "FullyQualifiedName~Parsek.Tests.Foo"`). Run the FULL suite once
  before you finish. `SyntheticRecordingTests.InjectAllRecordings` writes into the shared dev
  save and can flake when sibling runs overlap: retry it once before investigating.
- Release-config JIT differs (see CLAUDE.md "Cloud (Linux) sessions": a Unity call inlined
  back into a method a test calls throws `SecurityException` on the Windows host). If you
  add or move a helper that keeps a Unity call out of a tested method, also run
  `dotnet test -c Release` on the classes that call it.
- Harness Python suites (when you touch `harness/`, or a C# file a harness cell reads; see
  CLAUDE.md "Thirty-three hlib test cells read OUTSIDE harness/"): from `harness/`,
  `python -m unittest discover -s lib -q`, then `-s provision -q`, `-s missions/lib -q`.
- Architecture suite (always when you add, move or rename a `.cs` file or a production
  method): `python -m unittest discover -s scripts/arch -q`, and after a Debug build the
  semantic layer with `PARSEK_ARCH_IL` set to `Source/Parsek/bin/Debug/Parsek.dll` (it needs
  `ikdasm`; if this PC has none, say "semantic layer not run" in your report and the
  supervisor runs it in the cloud). Re-pin with the documented tools (CLAUDE.md "CI"
  paragraph), never by hand, and only when your change is the reason.
- Plan README rule 2: nothing lands without a caller (the unreached-production-method count
  may not rise; a method only an xUnit test calls counts as unreached).
- Captured console output goes under `<umbrella>/logs/console/<YYYY-MM-DD>/<label>.log`,
  never into the repo tree or the umbrella root itself.

## Commits and docs

- Docs move with the code in the same commit: `docs/dev/todo-and-known-bugs.md`,
  `docs/dev/autotest-status.md` (harness changes), the plan text you re-anchored, and the
  registers your task names. `CHANGELOG.md` ONLY for something a player can see, and then
  under a `1.1.0` heading above main's sections (create it if absent). No version bump.
- `.claude/CLAUDE.md` edits only as NEW lines or bullets, then copy it over `AGENTS.md`
  (byte-identical).
- Commit messages: plain ASCII, no em dashes, no emoji, NO `Co-Authored-By`, no session
  link, no signature or trailer of any kind. Comments explain constraints, not history.
- One task, one branch; small focused commits are fine; they merge as one pull request.

## Your final report (under 400 words)

1. Branch, worktree path, commit sha(s).
2. What changed, file by file in one line each.
3. Proof: the exact commands you ran and the tail of their output (test totals, failures).
   Say plainly if anything is red or was not run.
4. PC proof still owed before merge: the exact lane ids or in-game categories and the
   command to fly them. You do not fly them.
5. Deviations from the plan text and why; plan text you corrected.
6. Hot files you touched (task-index C.1) and anything the next task of your track must know.
7. Any question only the owner can answer, in plain player terms.
