# S11 on the owner's PC: ghost cost per frame at race scale

This is the step list for the PC half of task S11 in
`docs/dev/plans/multiplayer/r0-spikes.md` ("S11 Ghost cost per frame at race scale"). It
assumes S0's `PC-SETUP.md` ran to the end: the worktree `$W` of `spike/mp-r0` and the
instance `spike-r0` exist. Run it top to bottom in ONE PowerShell 7 window (`pwsh`): later
steps reuse the variables of step 0. Every step ends with a check; when a check fails, stop
there and report the step number and its output. Raw output goes to
`<umbrella>\logs\spikes\S11\<date>\` (`$R`), console captures to
`<umbrella>\logs\console\<date>\` (`$C`).

## Which PC session this rides in

Session **C4** of the plan (section 5, day 4): "S10b with S12, S11", on `spike-r0`, a
frame-cost session that runs with nothing else open. S11 goes LAST in C4, after S10b's runs
and S12's ten-minute check (`spikes/r0/S12/PC-RUN.md`), because its step 2 re-provisions
`spike-r0` and its lane runs need the PC otherwise idle. Never run it beside another KSP
copy, a harness tier, a build, a sync client or a browser playing video: the numbers are the
frame time of this PC with nothing else running.

| Part | Steps | Kind | Time |
|------|-------|------|------|
| A. Build, re-provision, dry run | 0-3 | DRIVEN (you paste the blocks) | 25 min |
| B. Lane run 1, `asis` (your vsync and frame cap) | 4 | DRIVEN, plus H1 (eyes only) | 15 min |
| C. Lane run 2, `uncapped` (vsync off, no frame cap) | 5 | DRIVEN, plus H1 (eyes only) | 15 min |
| D. Summary and the screenshots | 6 | DRIVEN summary, H2 (look at 20 pictures) | 10 min |
| E. Close-out | 7 | DRIVEN | 5 min |

Time: about 70 minutes. Your hands are needed for about 25 of them: pasting the blocks
(about 15 minutes over the whole sheet) and H2 (10 minutes). During the two lane runs you
only watch (H1); you never touch the mouse or the keyboard while KSP runs.

DRIVEN means the code or the harness does the step and a log line proves it; HAND means you
do it and the result is what you saw. Nothing in this sheet needs a mouse inside KSP.

## What a run does

The lane `SPIKE-S11-ghost-cost` stages `pad-runway-pair` (a sandbox save with the Logi Cargo
Rig on the launch pad) with 64 injected ghost recordings: small rockets (an OKTO probe core
on a white FL-T400 tank and an LV-T30 engine, engine lit) circling the pad 40 to 110 m out
and 30 to 100 m above it. It loads the save in flight and runs the in-game test
`SpikeS11.GhostCostSweep` alone (isolated). The test:

1. times the first open of one real trajectory file (a copy of the largest committed one,
   a Jool tour flight) and five warm opens;
2. turns the flight camera to 300 m from the pad, pitched down a little;
3. for N = 0, 8, 16, 32, 64: turns on the first N recordings, waits until Parsek has built
   exactly those N ghosts, waits 2 s, measures 600 frames in flight view, takes a
   screenshot, opens the map, waits 2 s, measures 600 frames there, takes a screenshot,
   closes the map;
4. fits the frame time against N (ms per ghost) for each view, puts the camera, vsync and
   frame cap back and turns all 64 recordings back on.

Each window writes one `[Parsek][INFO][Spike.S11] step ...` line in `KSP.log` and one row of
`<instance>\parsek-spike\S11\steps.csv` (plus 600 rows of `frames.csv`). The lane is PASS
only when all ten windows count.

## What you must see on screen for a step to count (H1)

- **Flight steps (N = 0, 8, 16, 32, 64).** The launch pad with the Logi Cargo Rig near the
  middle of the screen, seen from about 300 m away and a little above. Around and above it,
  N small rockets with an engine flame, flying slow circles (one lap in 90 s). N = 0: no
  rocket. N = 8: eight, easy to count. N = 16 and 32: clearly more each time. N = 64: a swarm
  around the pad. Each flight step lasts about 10 to 20 s.
- **Map steps.** The map view (planet Kerbin, orbit lines) for about 10 to 20 s. The ghosts
  are a cluster of markers at the Space Center; at this zoom they may sit on top of each
  other. A map step counts on its log line alone.
- A flight step counts when (a) its `step` line says `valid=true` (the code checked that
  Parsek built exactly N ghosts and that all N are inside the camera's view: `onScreen=N`)
  AND (b) its screenshot in `shots\` shows the pad and N rockets. If a picture shows
  something else (the camera looking at the ground, the screen black, a loading screen, a
  dialog over the view), that step does NOT count even with `valid=true`: note the step and
  report it.
- Do not touch the mouse or the keyboard from the launch until the console prints the
  verdict. A mouse drag turns the camera; a key can warp or stage the vessel. Keep the KSP
  window in front; do not switch to another window.

## Forbidden commands (read this before step 2)

`GameData\Squad` and `GameData\SquadExpansion` in `spike-r0` are JUNCTIONS into the dev
install. Everything in `PC-SETUP.md` -> "Forbidden commands" holds here: no `Remove-Item`,
`rmdir /S`, `rd /S`, `del /S`, Explorer delete, `robocopy /MIR` or `/PURGE` on any folder
under `automation\spike-r0*` that is or holds a junction, and no `Copy-Item -Recurse` /
`xcopy` / Explorer copy of an instance. This file deletes NOTHING inside an instance. It only
MOVES one real folder out, `parsek-spike\S11` (the spike's own output, every run):

- before every `Move-Item` of a folder, run
  `(Get-Item -LiteralPath "<path>" -Force).LinkType` and go on only when it prints an
  EMPTY line (a real folder); if it prints `Junction`, stop;
- `Move-Item` within the umbrella's drive is a rename: it never follows a link and never
  copies.

What a run leaves in the instance: the harness's staged save `saves\<run save>` (the next
harness run on `spike-r0` stages over it), `KSP.log` / `Player.log` (overwritten by the next
start) and `parsek-test-results.txt`. The test itself writes only under
`<instance>\parsek-spike\S11\` (CSV files and `shots\*.png`), which step 4 moves out.

## 0. Variables and preconditions (5 min)

KSP closed (S12 done), every other program that draws or syncs closed.

```powershell
Set-Location "<UMBRELLA ROOT>"
$U   = (Get-Location).Path
$D   = Get-Date -Format yyyy-MM-dd
$W   = "$U\Parsek-spike-mp-r0"
$DEV = "$U\Kerbal Space Program"
$I   = "$U\automation\spike-r0"
$R   = "$U\logs\spikes\S11\$D"
$C   = "$U\logs\console\$D"
$S11 = "$W\spikes\r0\S11"
$K   = "$W\spikes\r0\s0_check.py"
New-Item -ItemType Directory -Force -Path $R, $C | Out-Null
Test-Path $W, $I, "$DEV\KSP_x64.exe"
Get-Process KSP_x64 -ErrorAction SilentlyContinue
Test-Path "$U\automation\.ksp-machine.lock"
Get-ChildItem Env:PARSEK_* -ErrorAction SilentlyContinue
Test-Path "$I\parsek-spike\S11"
```

Check: the first `Test-Path` prints `True` three times; `Get-Process` prints nothing; the
lock prints `False`; `Env:PARSEK_*` prints nothing (if S10b or S12 left one, `Remove-Item
Env:<name>` it); the last line prints `False` (no S11 output from an earlier attempt; if
`True`, move that folder to `$R\leftover` with the LinkType check first).

## 1. Update the worktree, build without deploying, check the DLL (10 min)

```powershell
git -C $W pull --ff-only
git -C $W rev-parse HEAD | Tee-Object "$R\spike-commit.txt"
git -C $W status --short
Set-Location $W
dotnet build Source\Parsek\Parsek.csproj -c Debug -p:SkipKspDeploy=true *> "$C\spike-S11-build.log"
$LASTEXITCODE
Select-String -Path "$C\spike-S11-build.log" -SimpleMatch -Pattern "KSP deploy skipped", "Build succeeded", " 0 Error(s)"
python $K dll "$W\Source\Parsek\bin\Debug\Parsek.dll" --utf8 SpikeS11Cost --utf8 SpikeS11FirstOpen --utf8 SpikeS11Plan | Tee-Object "$R\build-dll.txt"
python "$S11\summarize_cost.py" --selftest
```

Check: the sha is the one the supervisor reported for `spike/mp-r0` with S11 merged;
`status` prints nothing; exit `0`; the three build lines found; the `dll` check prints
`RESULT: PASS`; the self test prints `summarize_cost selftest: PASS`.

## 2. Re-provision `spike-r0` with this DLL (10 min)

A RE-PROVISION, not a copy: the lane reads the instance's provision manifest, and a hand copy
makes it stale (PC-SETUP, "Later spike builds"). It replaces the DLL S10b and S12 ran with by
the same branch's newer build.

```powershell
Set-Location "$W\harness"
python provision\provision.py --profile spike-r0 --parsek-dll "$W\Source\Parsek\bin\Debug\Parsek.dll" *> "$C\spike-S11-provision.log"
$LASTEXITCODE
Select-String -Path "$C\spike-S11-provision.log" -SimpleMatch -Pattern "(override)", "installed-sha256=", "instance=spike-r0 slot=spike-r0 provisioned exit=0"
Set-Location $U
python $K dll "$I\GameData\Parsek\Plugins\Parsek.dll" --utf8 SpikeS11Cost --utf8 SpikeS11FirstOpen
"{0}  {1}  {2}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath "$I\GameData\Parsek\Plugins\Parsek.dll").Hash.ToLowerInvariant(), (git -C $W rev-parse HEAD), "$I\GameData\Parsek\Plugins\Parsek.dll" | Set-Content "$R\deployed-dll-sha256.txt"
Get-Content "$R\deployed-dll-sha256.txt"
python $K dll "$DEV\GameData\Parsek\Plugins\Parsek.dll" --expect-absent | Tee-Object "$R\dev-dll-before.txt"
python $K links $I --expect GameData/Squad GameData/SquadExpansion
```

Check: exit `0` and the three provision patterns found; the `dll` check `RESULT: PASS`; the
hash equals `build-dll.txt`'s `sha256=` and the provision log's `installed-sha256=`; the dev
DLL check `RESULT: PASS` (no spike marker there); the links check `RESULT: PASS`.

## 3. Dry run (2 min)

```powershell
Set-Location "$W\harness"
python run.py --id SPIKE-S11-ghost-cost --instance-dir $I --dry-run *> "$C\spike-S11-dryrun.log"
$LASTEXITCODE
Select-String -Path "$C\spike-S11-dryrun.log" -SimpleMatch -Pattern "inject=spike-s11-ghosts", "cmd=RunTests expect=OK budget=540"
```

Check: exit `0` and both patterns found.

## Part B. Lane run 1, `asis`, DRIVEN (`spike-r0`)

## 4. Lane run `asis` (15 min; H1 while it runs)

Your normal vsync and frame cap stay as they are. KSP starts in a window and then applies
your `settings.cfg` (full screen if you play full screen).

```powershell
Get-ChildItem Env:PARSEK_* -ErrorAction SilentlyContinue
$env:PARSEK_SPIKE = "S11"
Set-Location "$W\harness"
python run.py --id SPIKE-S11-ghost-cost --instance-dir $I *> "$C\spike-S11-lane-asis.log"
"run.py exit=$LASTEXITCODE"
Remove-Item Env:PARSEK_SPIKE
Select-String -Path "$C\spike-S11-lane-asis.log" -SimpleMatch -Pattern "verdict=", "skipped (R0 spike instance"
$L1 = "$R\asis"; New-Item -ItemType Directory -Force -Path $L1 | Out-Null
Copy-Item "$I\KSP.log" "$L1\KSP.log"
Copy-Item "$I\parsek-test-results.txt" "$L1\parsek-test-results.txt"
(Get-Item -LiteralPath "$I\parsek-spike" -Force).LinkType
(Get-Item -LiteralPath "$I\parsek-spike\S11" -Force).LinkType
Move-Item "$I\parsek-spike\S11" "$L1\parsek-spike-S11"
Select-String -Path "$L1\KSP.log" -Pattern "\[Spike\.S11\] (sweep|settings|firstopen attempt=1|step|fit|sweep done)" | ForEach-Object { $_.Line } | Tee-Object "$L1\s11-lines.txt"
python "$S11\summarize_cost.py" "$L1\parsek-spike-S11" | Tee-Object "$L1\summary.md"
```

Check before launch: `Env:PARSEK_*` prints nothing.

H1 while it runs (eyes only): after about 3 minutes the flight view shows the pad; then watch
the steps described in "What you must see on screen". Note anything that differs.

Check after:
- the console log holds `verdict=PASS scenario=SPIKE-S11-ghost-cost` and
  `admit parsek-dll vs stock-minimal: skipped (R0 spike instance ...)`;
- both LinkType lines are EMPTY;
- `s11-lines.txt` holds one `sweep start ... pass=asis frames=600 ...`, one `settings` line
  (your screen size, `vSyncCount`, `targetFrameRate`, GPU), `firstopen attempt=1
  kind=trajectory ... ok=true` and `kind=snapshot ... ok=true`, ten `step` lines all
  `valid=true` (five `view=flight` with `onScreen=` equal to `n=`, five `view=map`), six
  `fit` lines, and `sweep done ... steps=10 invalid=0`;
- `summary.md` ends `result=PASS`;
- `$L1\parsek-spike-S11\shots\` holds ten `.png` files.

If the verdict is not PASS: do not re-run, keep `$L1` and the console log and report. ONE
exception: if the console log names the `RunTests` step as TIMEOUT and the `step` lines that
did print show `fps=` under 15, run step 4 once more with `$env:PARSEK_SPIKE_S11_FRAMES =
"300"` set before `run.py` (and `Remove-Item Env:PARSEK_SPIKE_S11_FRAMES` after), into
`$R\asis-300`; say so in the report.

## Part C. Lane run 2, `uncapped`, DRIVEN (`spike-r0`)

## 5. Lane run `uncapped` (15 min; H1 while it runs)

The test sets vsync off and no frame cap for the sweep and puts both back after (your
`settings.cfg` is not changed). A frame-capped run shows the cap, not the cost, until the
cost passes it; this run shows the cost under the cap.

```powershell
$env:PARSEK_SPIKE = "S11"
$env:PARSEK_SPIKE_S11_PASS = "uncapped"
Set-Location "$W\harness"
python run.py --id SPIKE-S11-ghost-cost --instance-dir $I *> "$C\spike-S11-lane-uncapped.log"
"run.py exit=$LASTEXITCODE"
Remove-Item Env:PARSEK_SPIKE, Env:PARSEK_SPIKE_S11_PASS
Get-ChildItem Env:PARSEK_* -ErrorAction SilentlyContinue
Select-String -Path "$C\spike-S11-lane-uncapped.log" -SimpleMatch -Pattern "verdict="
$L2 = "$R\uncapped"; New-Item -ItemType Directory -Force -Path $L2 | Out-Null
Copy-Item "$I\KSP.log" "$L2\KSP.log"
Copy-Item "$I\parsek-test-results.txt" "$L2\parsek-test-results.txt"
(Get-Item -LiteralPath "$I\parsek-spike\S11" -Force).LinkType
Move-Item "$I\parsek-spike\S11" "$L2\parsek-spike-S11"
Select-String -Path "$L2\KSP.log" -Pattern "\[Spike\.S11\] (sweep|settings|uncapped|firstopen attempt=1|step|fit|sweep done|restored)" | ForEach-Object { $_.Line } | Tee-Object "$L2\s11-lines.txt"
python "$S11\summarize_cost.py" "$L2\parsek-spike-S11" | Tee-Object "$L2\summary.md"
```

Check: the same as step 4, with `pass=uncapped`, plus one line `uncapped: vSyncCount <yours>
-> 0, targetFrameRate <yours> -> -1` and one `restored ... vSyncCount=<yours>
targetFrameRate=<yours>`; `Env:PARSEK_*` prints nothing after the run. The flight steps look
the same as in step 4; the frame rate may be higher.

## Part D. Summary and screenshots

## 6. Both runs in one table, and the pictures (10 min; H2)

```powershell
python "$S11\summarize_cost.py" "$R\asis\parsek-spike-S11" "$R\uncapped\parsek-spike-S11" | Tee-Object "$R\summary-both.md"
Get-ChildItem "$R\asis\parsek-spike-S11\shots", "$R\uncapped\parsek-spike-S11\shots" | Select-Object Name, Length
Invoke-Item "$R\asis\parsek-spike-S11\shots"
```

Check: `summary-both.md` ends `summary: runs=2 steps=20 invalid=0 firstopenOk=2/2
result=PASS`; twenty `.png` files, none of length 0.

H2: open each `...-flight-nNN.png` (the folder opens in Explorer; repeat with the
`uncapped` folder). For each, write one line into `$R\shots-check.txt`:
`<file> pad=<yes|no> rockets=<about how many> other=<anything odd>`. The flight pictures
must show the pad and about N rockets (`n00` none, `n08` eight, `n64` a swarm). The map
pictures only need to show the map view.

```powershell
notepad "$R\shots-check.txt"
```

## Part E. Close-out

## 7. Close-out and what to hand back (5 min)

```powershell
python $K dll "$DEV\GameData\Parsek\Plugins\Parsek.dll" --expect-absent | Tee-Object "$R\dev-dll-after.txt"
python $K links $I --expect GameData/Squad GameData/SquadExpansion
Get-ChildItem Env:PARSEK_* -ErrorAction SilentlyContinue
Test-Path "$I\parsek-spike\S11"
Get-ChildItem $R | Select-Object Name, Length
```

Check: `RESULT: PASS` twice; no `PARSEK_*` variable; `False`. Hand back `$R` (the commit
and DLL hash, `asis\` and `uncapped\` with their `KSP.log`, `parsek-test-results.txt`, CSVs,
screenshots and summaries, `summary-both.md`, `shots-check.txt`) and the console logs in `$C`
(`spike-S11-*.log`): they are the S11 evidence for the spike report (`r0-spikes.md` 6.4,
draft in `spikes/r0/S11/REPORT-DRAFT.md`).
