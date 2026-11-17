# S6 on the owner's PC: race mark survival, menu entry, create dialog, difficulty panel, content table

This is the step list for the PC half of task S6 in
`docs/dev/plans/multiplayer/r0-spikes.md` ("S6 Race mark survival, main menu entry, create
a save"). It assumes S0's `PC-SETUP.md` ran to the end: the worktree `$W` of
`spike/mp-r0`, and the instances `spike-r0` and `spike-r0-content` exist. Run it top to
bottom in ONE PowerShell 7 window (`pwsh`): later steps reuse the variables of step 0.
Every step ends with a check; when a check fails, stop there and report the step number
and its output. Raw output goes to `<umbrella>\logs\spikes\S6\<date>\` (`$R`), console
captures to `<umbrella>\logs\console\<date>\` (`$C`).

Which instance each part uses:

| Part | Steps | Instance | Why |
|------|-------|----------|-----|
| A. Build and stage | 0-3 | both | DLL into both, template staged as a local fixture |
| B. The lane, driven | 4-5 | `spike-r0-content` (`$IC`) | the template is the `stock-tree` career `cs-career-mun4` (as RW-5), so the lane needs a stock-tree copy |
| C. Main menu, driven | 6-7 | `spike-r0` (`$I`) | any instance serves; `spike-r0` has the owner's graphics |
| D. Content table (f), driven | 8-12 | `spike-r0-content` (`$IC`) | the stock tech tree (no CommunityTechTree, no ProbesBeforeCrew) |
| E. HAND steps | H1-H7 | `spike-r0` (`$I`) | mouse, keyboard, paste, the panel's own controls, UI scale |
| F. Close-out | 13 | both | |

Time: about 3.5 hours. A: 30 min (most of it the re-provision). B: 50 min (two lane
runs of about 20 min each, plus checks). C: 20 min. D: 60 min (four runs of about 8 min,
plus the ReStock installs). E: 40 min. F: 10 min.

DRIVEN means the code or the harness does the step and a log line proves it; HAND means
the owner does it with mouse and keyboard and the log line proves what happened. Only
part E is HAND, plus the ReStock downloads in part D.

## Forbidden commands (read this before step 2)

`GameData\Squad` and `GameData\SquadExpansion` in both spike instances are JUNCTIONS into
the dev install. Everything in `PC-SETUP.md` -> "Forbidden commands" holds here: no
`Remove-Item`, `rmdir /S`, `rd /S`, `del /S`, Explorer delete, `robocopy /MIR` or
`/PURGE` on any folder under `automation\spike-r0*` that is or holds a junction, and no
`Copy-Item -Recurse` / `xcopy` / Explorer copy of an instance. This file deletes NOTHING
inside an instance. It only MOVES real (non-link) folders and files in and out:

- before every `Move-Item` of a folder, run
  `(Get-Item -LiteralPath "<path>" -Force).LinkType` and go on only when it prints an
  EMPTY line (a real folder); if it prints `Junction`, stop;
- `Move-Item` within the umbrella's drive is a rename: it never follows a link and never
  copies;
- the folders moved are `$IC\GameData\KSPCommunityFixes` (a dev-sourced COPY, step 5),
  the ReStock folders the owner copied in (part D), the two patch files (step 11) and
  `parsek-spike\S6` (the spike's own output, every run).

## 0. Variables and preconditions (5 min)

```powershell
Set-Location "<UMBRELLA ROOT>"
$U   = (Get-Location).Path
$D   = Get-Date -Format yyyy-MM-dd
$W   = "$U\Parsek-spike-mp-r0"
$DEV = "$U\Kerbal Space Program"
$I   = "$U\automation\spike-r0"
$IC  = "$U\automation\spike-r0-content"
$R   = "$U\logs\spikes\S6\$D"
$C   = "$U\logs\console\$D"
$S6  = "$W\spikes\r0\S6"
$K   = "$W\spikes\r0\s0_check.py"
$P   = "$U\automation\_parked-S6"
New-Item -ItemType Directory -Force -Path $R, $C, $P | Out-Null
Test-Path $W, $I, $IC, "$DEV\KSP_x64.exe"
Get-Process KSP_x64 -ErrorAction SilentlyContinue
Test-Path "$U\automation\.ksp-machine.lock"
Get-ChildItem Env:PARSEK_* -ErrorAction SilentlyContinue
Test-Path "$I\parsek-spike\S6", "$IC\parsek-spike\S6"
```

Check: the first `Test-Path` prints `True` four times; `Get-Process` prints nothing; the
lock prints `False`; `Env:PARSEK_*` prints nothing; the last line prints `False False`
(no S6 output left from an earlier attempt; if `True`, move that folder to
`$R\leftover-<instance>` with the LinkType check first).

## 1. Update the worktree, build without deploying, check the DLL (10 min)

```powershell
git -C $W pull --ff-only
git -C $W rev-parse HEAD | Tee-Object "$R\spike-commit.txt"
git -C $W status --short
Set-Location $W
dotnet build Source\Parsek\Parsek.csproj -c Debug -p:SkipKspDeploy=true *> "$C\spike-S6-build.log"
$LASTEXITCODE
Select-String -Path "$C\spike-S6-build.log" -SimpleMatch -Pattern "KSP deploy skipped", "Build succeeded", " 0 Error(s)"
python $K dll "$W\Source\Parsek\bin\Debug\Parsek.dll" --utf8 SpikeS6Menu --utf8 SpikeS6Host --utf8 SpikeS6ProbeScenario | Tee-Object "$R\build-dll.txt"
python "$S6\check_mark.py" --selftest
python "$S6\make_template.py" --selftest
python "$S6\content_diff.py" --selftest
```

Check: the sha is the one the supervisor reported for `spike/mp-r0` with S6 merged;
`status` prints nothing; exit `0`; the three build lines found; the `dll` check prints
`RESULT: PASS` (all three S6 class names present in UTF-8); the three self tests print
`check_mark selftest: PASS (hash 33f1d41c85676b3f)`, `make_template selftest: PASS`,
`content_diff selftest: PASS`.

## 2. The DLL into both instances (15 min)

`spike-r0-content` gets a RE-PROVISION, not a copy: the lane in part B reads that
instance's provision manifest, and a hand copy makes it stale (PC-SETUP, "Later spike
builds"). `spike-r0` gets a plain copy (no lane runs there).

```powershell
Set-Location "$W\harness"
python provision\provision.py --profile spike-r0-content --parsek-dll "$W\Source\Parsek\bin\Debug\Parsek.dll" *> "$C\spike-S6-content-provision.log"
$LASTEXITCODE
Select-String -Path "$C\spike-S6-content-provision.log" -SimpleMatch -Pattern "(override)", "installed-sha256=", "instance=spike-r0-content slot=spike-r0-content provisioned exit=0"
Test-Path "$IC\GameData\CommunityTechTree", "$IC\GameData\ProbesBeforeCrew", "$IC\GameData\KSPCommunityFixes"
Copy-Item "$W\Source\Parsek\bin\Debug\Parsek.dll" "$I\GameData\Parsek\Plugins\Parsek.dll"
Set-Location $U
foreach ($p in "$I\GameData\Parsek\Plugins\Parsek.dll", "$IC\GameData\Parsek\Plugins\Parsek.dll") {
  python $K dll $p --utf8 SpikeS6Menu --utf8 SpikeS6Host --utf8 SpikeS6ProbeScenario
  "{0}  {1}  {2}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash.ToLowerInvariant(), (git -C $W rev-parse HEAD), $p |
    Add-Content "$R\deployed-dll-sha256.txt"
}
Get-Content "$R\deployed-dll-sha256.txt"
python $K dll "$DEV\GameData\Parsek\Plugins\Parsek.dll" --expect-absent | Tee-Object "$R\dev-dll-before.txt"
python $K links $IC --expect GameData/Squad GameData/SquadExpansion
```

Check: exit `0` and the three provision patterns found; `False False True` (no
CommunityTechTree, no ProbesBeforeCrew, KSPCF present); both `dll` checks `RESULT: PASS`;
two hash lines with ONE hash, equal to `build-dll.txt`'s `sha256=` and to the provision
log's `installed-sha256=`; the dev DLL check `RESULT: PASS` (the spike marker is absent
there); the links check `RESULT: PASS`.

## 3. Compose the template and stage it as the lane's local fixture (5 min)

`make_template.py` copies the committed career fixture `cs-career-mun4` and its three
shared craft, and inserts `template\mark.cfg` after the last top-level SCENARIO of GAME.

```powershell
python "$S6\make_template.py" --out "$R\template\spike-s6-mark" | Tee-Object "$R\template.txt"
python "$W\harness\tools\stage_local_fixture.py" --from "$R\template\spike-s6-mark" --as spike-s6-mark | Tee-Object "$R\stage.txt"
python "$S6\check_mark.py" tree "$W\harness\fixtures\local-saves\spike-s6-mark" | Tee-Object "$R\template-check.txt"
Set-Location "$W\harness"
python run.py --id SPIKE-S6-mark-survives --dry-run *> "$C\spike-S6-dryrun.log"
$LASTEXITCODE
```

Check: `template written: ... marks=1 hash=33f1d41c85676b3f scenarios=24`; the staging
prints the staged path; `check_mark tree` ends
`tree: files=1 failed=0 expectHash=33f1d41c85676b3f result=PASS` with the line
`OK        marks=1 hash=33f1d41c85676b3f ...persistent.sfs`; the dry run exits `0`.

## Part B. The lane, DRIVEN (`spike-r0-content`)

The lane `SPIKE-S6-mark-survives` loads the template, saves, quicksaves and quickloads,
launches and recovers, records and commits a tree, runs Rewind to Launch, records a
decouple (a rewind point `.sfs`), starts a Re-Fly, runs Discard Re-fly (Parsek's
`RevertInterceptor` write of persistent.sfs), launches and Reverts to Launch, saves and
quits. The armed spike logs every hook and every `.sfs` write it sees, and copies each
written file to `parsek-spike\S6\snapshots\`. The spec gates on those lines: any written
GAME save with `marks=0`, two marks, or another hash reds the lane, as does a missing
patch target.

## 4. Lane run 1: with KSPCommunityFixes (25 min)

```powershell
Get-ChildItem Env:PARSEK_* -ErrorAction SilentlyContinue
$env:PARSEK_SPIKE = "S6"
Set-Location "$W\harness"
python run.py --id SPIKE-S6-mark-survives --instance-dir $IC *> "$C\spike-S6-lane1.log"
"run.py exit=$LASTEXITCODE"
Remove-Item Env:PARSEK_SPIKE
Select-String -Path "$C\spike-S6-lane1.log" -SimpleMatch -Pattern "verdict=", "skipped (R0 spike instance"
$L1 = "$R\lane1-kspcf"; New-Item -ItemType Directory -Force -Path $L1 | Out-Null
Copy-Item "$IC\KSP.log" "$L1\KSP.log"
python $K links "$IC\saves\spike-s6-mark" --expect-none
robocopy "$IC\saves\spike-s6-mark" "$L1\save" /E /XJ /R:1 /W:1 /NP /NFL /NDL | Out-Null; "robocopy exit=$LASTEXITCODE"
(Get-Item -LiteralPath "$IC\parsek-spike" -Force).LinkType
(Get-Item -LiteralPath "$IC\parsek-spike\S6" -Force).LinkType
Move-Item "$IC\parsek-spike\S6" "$L1\parsek-spike-S6"
python "$S6\check_mark.py" log "$L1\KSP.log" | Tee-Object "$L1\check-log.txt"
python "$S6\check_mark.py" tree "$L1\save" "$L1\parsek-spike-S6\snapshots" | Tee-Object "$L1\check-tree.txt"
```

Check before launch: `Env:PARSEK_*` prints nothing (no `PARSEK_SPIKE_S6_*` may be set
for a lane: the AUTO drive would fight the seam).

Check after:
- the console log holds `verdict=PASS scenario=SPIKE-S6-mark-survives` and
  `admit parsek-dll vs stock-minimal: skipped (R0 spike instance ...)`;
- the links check `RESULT: PASS`, robocopy exit `0` to `7`, both LinkType lines EMPTY;
- `check-log.txt` ends `log: savewatchLines=N badGameSaves=0 otherFailures=0
  result=PASS` with N about 15 or more; its "save writes" list holds `persistent.sfs`,
  `quicksave.sfs` and at least one `Parsek/RewindPoints/<rpId>.sfs`, every one
  `marks=1 hash=33f1d41c85676b3f`; "hook order per cycle" shows a cycle whose first entry
  carries the mark; the "other S6 lines" list holds `patch summary applied=6 missing=0`
  and at least one `earliest cycle=N source=LoadGameCfg hook=...`;
- `check-tree.txt` ends `result=PASS` (every saved file and every snapshot copy holds
  exactly one mark with the pinned hash; files without a GAME node are listed as
  `NOT-GAME` and not judged).

If the verdict is not PASS, do not re-run: keep `$L1` and the console log and report.
A `marks=0` line names the path that dropped the node (the `trigger=` and the file).

## 5. Lane run 2: KSPCommunityFixes parked out of the instance (25 min)

```powershell
(Get-Item -LiteralPath "$IC\GameData\KSPCommunityFixes" -Force).LinkType
Move-Item "$IC\GameData\KSPCommunityFixes" "$P\KSPCommunityFixes"
Test-Path "$IC\GameData\KSPCommunityFixes", "$P\KSPCommunityFixes"
$env:PARSEK_SPIKE = "S6"
Set-Location "$W\harness"
python run.py --id SPIKE-S6-mark-survives --instance-dir $IC *> "$C\spike-S6-lane2.log"
"run.py exit=$LASTEXITCODE"
Remove-Item Env:PARSEK_SPIKE
Move-Item "$P\KSPCommunityFixes" "$IC\GameData\KSPCommunityFixes"
Test-Path "$IC\GameData\KSPCommunityFixes", "$P\KSPCommunityFixes"
Select-String -Path "$C\spike-S6-lane2.log" -SimpleMatch -Pattern "verdict="
$L2 = "$R\lane2-nokspcf"; New-Item -ItemType Directory -Force -Path $L2 | Out-Null
Copy-Item "$IC\KSP.log" "$L2\KSP.log"
Select-String -Path "$L2\KSP.log" -SimpleMatch -Pattern "KSPCommunityFixes" | Select-Object -First 3
robocopy "$IC\saves\spike-s6-mark" "$L2\save" /E /XJ /R:1 /W:1 /NP /NFL /NDL | Out-Null; "robocopy exit=$LASTEXITCODE"
(Get-Item -LiteralPath "$IC\parsek-spike\S6" -Force).LinkType
Move-Item "$IC\parsek-spike\S6" "$L2\parsek-spike-S6"
python "$S6\check_mark.py" log "$L2\KSP.log" | Tee-Object "$L2\check-log.txt"
python "$S6\check_mark.py" tree "$L2\save" "$L2\parsek-spike-S6\snapshots" | Tee-Object "$L2\check-tree.txt"
```

Check: the first LinkType line is EMPTY (a real folder; if `Junction`, stop); after the
first move `False True`, after the move back `True False` (KSPCF is back; the step is
not done until it is); `Select-String` on the game log prints NOTHING for
`KSPCommunityFixes` (it really ran without it); then the same checks as step 4.
Leave KSPCF in place from here on.

## Part C. Main menu, DRIVEN (`spike-r0`)

`PARSEK_SPIKE_S6_AUTO=1` drives the main menu with the code's own inputs: it taps the
stock Start button, walks the stage-two list with the stock keyboard-navigation event
(`GameEvents.onMenuNavGetInput`, Down until the clone is hovered, then Accept), types the
200-character folder into the field, opens the stock difficulty panel, sets the four
planned values on the panel's own `GameParameters` (not through its controls: that is
H4), closes it with its private `Dismiss(bool)`, creates the save, reaches the Space
Center, logs the forty values, saves, returns to the main menu, resumes the save through
stock's load-dialog call sequence, logs the forty again, and quits.

## 6. AUTO edit run (10 min)

```powershell
$env:PARSEK_SPIKE = "S6"; $env:PARSEK_SPIKE_S6_AUTO = "1"; $env:PARSEK_SPIKE_S6_DIFF = "edit"; $env:PARSEK_SPIKE_S6_QUIT = "1"
Start-Process -FilePath "$I\KSP_x64.exe" -WorkingDirectory $I -ArgumentList "-logFile `"$I\Player.log`"" -Wait
Get-ChildItem Env:PARSEK_* | ForEach-Object { Remove-Item "Env:$($_.Name)" }
$A1 = "$R\auto-edit"; New-Item -ItemType Directory -Force -Path $A1 | Out-Null
Copy-Item "$I\KSP.log" "$A1\KSP.log"
(Get-Item -LiteralPath "$I\parsek-spike\S6" -Force).LinkType
Move-Item "$I\parsek-spike\S6" "$A1\parsek-spike-S6"
python "$S6\check_mark.py" log "$A1\KSP.log" | Tee-Object "$A1\check-log.txt"
python "$S6\check_mark.py" tree "$I\saves\spike-s6-created" "$A1\parsek-spike-S6\snapshots" | Tee-Object "$A1\check-tree.txt"
```

Do not touch mouse or keyboard while it runs; KSP quits by itself (about 5 minutes).

Check (`check-log.txt`, "other S6 lines", in this order):
- `options auto=True diff=Edit ...`; `patch summary applied=6 missing=0`;
- `bodies scene=MAINMENU count=17 home=Kerbin homeRadius=600000` (the R2-02 check);
- `menu clone ok stageTwo=N->N+1 index=... label=Multiplayer order=[...]` with the clone
  right after the Scenarios entry;
- `auto: nav reached the clone after Down xN; Accept`, then `menu tap via=auto-accept`;
- `dialog open mode=CAREER save=spike-s6-created uiScale=1 ...`;
- `auto: folder input set=True readBackLen=200 equals=True`;
- `difficulty shown panel=GameDifficulty ... dialogAlive=True ... panelOnTop=True ...`;
- four `auto: edit <Key>=<Value> ok=True` lines, `auto: panel Dismiss(True)`;
- `difficulty accepted=True sameObject=True changedFromDefaults=4 [...]`;
- `create save=spike-s6-created folderLen=200 folderEqualsPrimed=True mode=CAREER ... mark=1 probe=1`;
- `earliest cycle=N source=create hook=...`;
- `difficulty check phase=sc-created planned=4 problems=0 result=PASS`;
- `resume: LoadSFSFile folder=spike-s6-created node=True nodeMarks=1`,
  `resume: starting mark=1`, an `earliest cycle=M source=LoadGameCfg hook=...` line;
- `difficulty check phase=sc-after-resume planned=4 problems=0 result=PASS`;
- `proto-load module=ParsekRaceSpikeMark ... instantiated=False` and
  `probe OnLoad scene=SPACECENTER marker=spike-s6-probe` (a proto added before
  `Game.Start` is instantiated when its type exists, and kept as a node when it does not);
- last line `log: ... badGameSaves=0 otherFailures=0 result=PASS`;
- `check-tree.txt` ends `result=PASS`.

A `Warn` line `auto: nav did not reach the clone in 12 steps` means the keyboard event
path failed; the code then taps the clone directly so the rest still runs; record it.

## 7. AUTO cancel run (10 min)

```powershell
$env:PARSEK_SPIKE = "S6"; $env:PARSEK_SPIKE_S6_AUTO = "1"; $env:PARSEK_SPIKE_S6_DIFF = "cancel"; $env:PARSEK_SPIKE_S6_SAVE = "spike-s6-cancel"; $env:PARSEK_SPIKE_S6_QUIT = "1"
Start-Process -FilePath "$I\KSP_x64.exe" -WorkingDirectory $I -ArgumentList "-logFile `"$I\Player.log`"" -Wait
Get-ChildItem Env:PARSEK_* | ForEach-Object { Remove-Item "Env:$($_.Name)" }
$A2 = "$R\auto-cancel"; New-Item -ItemType Directory -Force -Path $A2 | Out-Null
Copy-Item "$I\KSP.log" "$A2\KSP.log"
(Get-Item -LiteralPath "$I\parsek-spike\S6" -Force).LinkType
Move-Item "$I\parsek-spike\S6" "$A2\parsek-spike-S6"
python "$S6\check_mark.py" log "$A2\KSP.log" | Tee-Object "$A2\check-log.txt"
```

Check: four `auto: edit` lines then `auto: panel Dismiss(False)`;
`difficulty accepted=False ... changedFromDefaults=0`; `difficulty check phase=sc-created
choice=Cancel accepted=False movedFromDefaults=0 result=PASS` and the same for
`sc-after-resume`; `result=PASS` on the last line.

## Part D. Content table (f), DRIVEN (`spike-r0-content`)

`PARSEK_SPIKE_S6_CONTENT=1` writes `parsek-spike\S6\content-<label>-mainmenu.cfg` at the
main menu (GameData folders, the database tech tree and the raw stock `TechTree.cfg`,
every loaded part with its `partConfig`, `PARTUPGRADE`, `EXPERIMENT_DEFINITION`, each
body's science values), then creates a Career save, and at the Space Center reads the
tree (`content-<label>-rnd-sc-entry.cfg`), clicks the R&D building through its own
handler and reads the tree it shows (`content-<label>-rnd-rnd-open.cfg`).

Before EACH run of this part:

```powershell
Test-Path "$IC\GameData\CommunityTechTree", "$IC\GameData\ProbesBeforeCrew", "$IC\parsek-spike\S6"
Get-ChildItem "$IC\GameData" -Directory | Select-Object Name
```

Check: `False False False`, and the folder list is what the run's heading says.

The run itself, with `$LBL` set by each step:

```powershell
$env:PARSEK_SPIKE = "S6"; $env:PARSEK_SPIKE_S6_AUTO = "1"; $env:PARSEK_SPIKE_S6_DIFF = "accept"
$env:PARSEK_SPIKE_S6_CONTENT = "1"; $env:PARSEK_SPIKE_S6_LABEL = $LBL; $env:PARSEK_SPIKE_S6_RESUME = "0"; $env:PARSEK_SPIKE_S6_QUIT = "1"
$env:PARSEK_SPIKE_S6_SAVE = "spike-s6-content-$LBL"
Start-Process -FilePath "$IC\KSP_x64.exe" -WorkingDirectory $IC -ArgumentList "-logFile `"$IC\Player.log`"" -Wait
Get-ChildItem Env:PARSEK_* | ForEach-Object { Remove-Item "Env:$($_.Name)" }
$F = "$R\content-$LBL"; New-Item -ItemType Directory -Force -Path $F | Out-Null
Copy-Item "$IC\KSP.log" "$F\KSP.log"
(Get-Item -LiteralPath "$IC\parsek-spike\S6" -Force).LinkType
Move-Item "$IC\parsek-spike\S6" "$F\parsek-spike-S6"
Select-String -Path "$F\KSP.log" -SimpleMatch -Pattern "content mainmenu label=", "rnd-tree source=", "rnd open"
python "$S6\content_diff.py" rnd "$F\parsek-spike-S6\content-$LBL-mainmenu.cfg" "$F\parsek-spike-S6\content-$LBL-rnd-rnd-open.cfg" | Tee-Object "$F\rnd.txt"
python "$S6\content_diff.py" variants "$F\parsek-spike-S6\content-$LBL-mainmenu.cfg" | Tee-Object "$F\variants.txt"
```

Check (every run): one `content mainmenu label=<LBL> ... dbTrees=1 ...` line with
`parts=` above 0; `rnd open: RnDBuilding.OnClicked invoked`; two `rnd-tree source=`
lines (`sc-entry` and `rnd-open`), each `result=PASS`; `rnd.txt` ends
`rnd: mainmenu=N rnd=N onlyMainmenu=0 onlyRnd=0 changed=0 result=PASS`. If the R&D building does not open (`rnd open skipped` / `failed`), HAND:
click the R&D building yourself while the game waits 120 frames, or rerun without
`PARSEK_SPIKE_S6_QUIT` and click it, then quit.

## 8. Content run `clean` (10 min)

```powershell
$LBL = "clean"
```

Then the run block. The GameData list holds Squad, SquadExpansion, Parsek, the stack
(kRPC, MechJeb2, TestingTools) and the stock-tree mods, and nothing else.

## 9. Content run `restock` (15 min, HAND download)

Download ReStock from its official release page and copy the folders its install notes
name into `$IC\GameData` (copy only; never a link). Note each folder name in
`$R\restock-folders.txt`.

```powershell
$LBL = "restock"
```

Then the run block. Afterwards park the ReStock folders (one `Move-Item` per folder,
after its LinkType check prints an EMPTY line) into `$P\restock\`. Do not park them yet
if the next step adds ReStock+ on top: leave them for step 10.

## 10. Content run `restockplus` (15 min, HAND download)

With ReStock still in place, copy ReStock+ and the dependencies its install notes name
into `$IC\GameData`; append the folder names to `$R\restock-folders.txt`.

```powershell
$LBL = "restockplus"
```

Then the run block. Afterwards park every folder listed in `$R\restock-folders.txt`
(LinkType check, then `Move-Item` into `$P\restock\`) and check that the GameData list
equals step 8's again.

## 11. Content run `patches` (10 min)

```powershell
Copy-Item "$S6\patches\S6-tech-cost.cfg", "$S6\patches\S6-engine-thrust.cfg" "$IC\GameData\"
$LBL = "patches"
```

Then the run block. Afterwards:

```powershell
Move-Item "$IC\GameData\S6-tech-cost.cfg", "$IC\GameData\S6-engine-thrust.cfg" $P
Test-Path "$IC\GameData\S6-tech-cost.cfg", "$IC\GameData\S6-engine-thrust.cfg"
```

Check: `False False`.

## 12. The content diffs (5 min)

```powershell
$M = { param($l) "$R\content-$l\parsek-spike-S6\content-$l-mainmenu.cfg" }
python "$S6\content_diff.py" diff (& $M clean) (& $M patches) | Tee-Object "$R\diff-clean-vs-patches.txt"
python "$S6\content_diff.py" diff (& $M clean) (& $M restock) | Tee-Object "$R\diff-clean-vs-restock.txt"
python "$S6\content_diff.py" diff (& $M clean) (& $M restockplus) | Tee-Object "$R\diff-clean-vs-restockplus.txt"
Get-Content "$R\content-clean\variants.txt" -Tail 5
Get-Content "$R\content-restockplus\variants.txt"
```

Check: `diff-clean-vs-patches.txt` shows exactly two gameplay differences, the
`basicRocketry` node `cost` (now 77) and part `liquidEngine`'s `maxThrust` (now 999),
and ends `result=DIFFERENT`. The ReStock diffs list GameData folders and, for parts,
only visual keys (model, texture, MODEL nodes); any change of a stock part's gameplay
value or a tech node is copied into the report for the owner. `variants.txt` names the
ReStock+ parts whose variants change gameplay values and the `PARTUPGRADE` count.

## Part E. HAND steps (`spike-r0`, one session, 40 min)

One KSP session, armed but not AUTO. Screenshots with F1 land in `$I\Screenshots`.

```powershell
Get-ChildItem Env:PARSEK_* -ErrorAction SilentlyContinue
$env:PARSEK_SPIKE = "S6"
Start-Process -FilePath "$I\KSP_x64.exe" -WorkingDirectory $I -ArgumentList "-logFile `"$I\Player.log`""
```

Check before launch: `Env:PARSEK_*` printed nothing. Then in the game, in this order:

- **H1 Mouse click.** Main menu, Start Game. The second list shows "Multiplayer" right
  after "Scenarios". F1. Click "Multiplayer". The dialog opens. F1. Click Cancel.
  Log: `menu tap via=mouse`, `dialog open ...`, `dialog cancelled`.
- **H2 Arrows and Enter.** Still in the second list, press the Down arrow until
  "Multiplayer" is highlighted, then Enter. Log: `menu clone hovered=True` and
  `menu tap via=enter arrowsUsed=True`.
- **H3 Paste.** Click into the folder field, press Ctrl+V (the dialog put a
  200-character path on the clipboard when it opened). Log:
  `dialog folder changed len=0->200 equalsPrimed=True`.
- **H4 The panel through its own controls.** Click Difficulty. The stock panel opens
  over the dialog (F1). Change: "Allow Other Launch Sites" off; the funds reward
  slider to 80 %; "Kerbal G-Limits" on; "Enable Plasma Blackout" on. Accept. Log:
  `difficulty shown ... panelOnTop=True`, `difficulty accepted=True sameObject=True
  changedFromDefaults=4 [...]` naming exactly those four keys. Change the save name
  field to `spike-s6-hand`, click Create. At the
  Space Center, log: `difficulty check phase=sc-created planned=4 problems=0
  result=PASS`. Then open the stock difficulty window of the created save (Escape,
  Settings, Difficulty Options) and look at the four values; F1; close it without
  changes. Escape, Quit to Main Menu (save).
- **H5 Cancel.** Start Game, Multiplayer, Difficulty, change any value, press Cancel.
  Log: `difficulty accepted=False ... changedFromDefaults=0`. Change the save name to
  `spike-s6-hand-cancel`, Create; at the Space Center: `difficulty check
  phase=sc-created choice=Cancel accepted=False movedFromDefaults=0 result=PASS`.
  Escape, Quit to Main Menu.
- **H6 Real Resume Saved.** Start Game, Resume Saved, pick `spike-s6-hand`, Load. Log: `cycle begin cycle=N source=LoadGameCfg
  gameMarks=1`, `earliest cycle=N source=LoadGameCfg hook=...`,
  `sc-arrival n=... mark=1 probeLoaded=1 ...`, `probe OnLoad scene=SPACECENTER`.
  Escape, Quit to Main Menu.
- **H7 UI scale.** Settings, General, UI Scale 150 %, Apply, back. Start Game,
  Multiplayer (F1), Difficulty (F1), Cancel both. Log: `dialog open ... uiScale=1.5
  lossyScale=...` and `difficulty shown ... panelLossyScale=...`, both larger than the
  1.0 lines of H1 and H4. Set UI Scale back to 100 %, Apply. Quit.

```powershell
Remove-Item Env:PARSEK_SPIKE
$H = "$R\hand"; New-Item -ItemType Directory -Force -Path "$H\shots" | Out-Null
Copy-Item "$I\KSP.log" "$H\KSP.log"
Copy-Item "$I\Screenshots\*.png" "$H\shots\"
(Get-Item -LiteralPath "$I\parsek-spike\S6" -Force).LinkType
Move-Item "$I\parsek-spike\S6" "$H\parsek-spike-S6"
python "$S6\check_mark.py" log "$H\KSP.log" | Tee-Object "$H\check-log.txt"
python "$S6\check_mark.py" tree "$I\saves\spike-s6-hand" "$I\saves\spike-s6-hand-cancel" | Tee-Object "$H\check-tree.txt"
```

Check: every log line named in H1-H7 is in `check-log.txt` ("other S6 lines"); both end
`result=PASS`; the screenshots show the clone in the list, the dialog, the panel on top
of the dialog at both scales, and the four values in the created save's stock window.

## 13. Close-out and what to hand back (10 min)

```powershell
python $K dll "$DEV\GameData\Parsek\Plugins\Parsek.dll" --expect-absent | Tee-Object "$R\dev-dll-after.txt"
python $K links $IC --expect GameData/Squad GameData/SquadExpansion
Test-Path "$IC\GameData\KSPCommunityFixes"
Get-ChildItem $P -Recurse -Depth 1 | Select-Object FullName
Get-ChildItem $R | Select-Object Name, Length
```

Check: `RESULT: PASS` twice; `True` (KSPCF is back); `$P` holds only the parked ReStock
folders and the two patch files. Hand back `$R` and the console logs in `$C`: the commit
and DLL hashes, the two lane folders, the two AUTO folders, the four content folders and
three diffs, and the hand folder with its screenshots are the S6 evidence for the
spike report (`r0-spikes.md` 6.4).
