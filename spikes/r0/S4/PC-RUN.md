# S4a on the PC: where the KSC is, the event order, the turn-back (about 75 minutes)

Spike S4a of `docs/dev/plans/multiplayer/r0-spikes.md` ("S4a Probe"). Two KSP sessions on
the spike instance `automation\spike-r0`, then two Python commands that print the report
cells. The spike only reads the game; the one thing it changes is a turn-back of one
spike save at the main menu (session 2). It uses only saves made from the `spike-s0`
career that `PC-SETUP.md` step 9 created, so none of your own saves is opened or copied.

Run it in ONE PowerShell 7 window, top to bottom. Every step ends with a check; when a check
fails, stop and send back the step number and its output.

## What the harness drives and what needs your hands

DRIVEN = a command you paste that runs on its own (a script, or a line appended to the
Parsek command seam, which the game executes). HAND = clicks or keys in the game.

| step | DRIVEN or HAND | what to do | what proves it worked | minutes |
|---:|---|---|---|---:|
| 0 | DRIVEN | variables, preconditions | the checks of step 0 | 2 |
| 1 | DRIVEN | build the spike DLL, copy it into `spike-r0`, verify it | two `RESULT: PASS` | 8 |
| 2 | DRIVEN | stage three spike saves from `spike-s0` | three `staged ...` lines | 2 |
| 3 | DRIVEN | clear the seam files, arm, launch KSP | main menu shown | 3 |
| 4 | HAND | Resume Saved `SPIKE-S4A-ON` | log line `dump done id=d1-SPACECENTER ... errors=0` | 3 |
| 5 | DRIVEN | seam `LoadGame` into `SPIKE-S4A-OFF` | response `verdict=OK`; `dump done id=d2-SPACECENTER` | 3 |
| 6 | HAND | VAB, a pod, Launch | `dump done id=d3-FLIGHT` | 5 |
| 7 | HAND | F5, then hold F9 | `dump done id=d4-FLIGHT` | 3 |
| 8 | HAND | Revert to Launch | `dump done id=d5-FLIGHT` | 3 |
| 9 | HAND | back to the Space Center | `dump done id=d6-SPACECENTER` | 2 |
| 10 | DRIVEN | seam `LoadGame` into `SPIKE-S4A-ON` | response `verdict=OK`; `dump done id=d7-SPACECENTER` | 3 |
| 11 | HAND | VAB, a pod, Launch (with other sites allowed) | `dump done id=d8-FLIGHT` | 4 |
| 12 | HAND | Quit to Main Menu, Start New career `SPIKE-S4A-NEW` | `dump done id=d9-SPACECENTER` | 5 |
| 13 | HAND | Quit KSP | prompt returns | 1 |
| 14 | DRIVEN | collect, check, summarize | `Ways covered: 5 of 5`, `Field completeness: PASS`, `hooks applied=15 missing=0` | 8 |
| 15 | DRIVEN | hash `SPIKE-REFUSE-1` before | hash printed | 2 |
| 16 | HAND | session 2: Resume Saved `SPIKE-REFUSE-1` (turned back), F1, then Resume Saved `SPIKE-S4A-ON`, quit | message at the main menu; second save loads | 10 |
| 17 | DRIVEN | hash after, turn-back check | `TURN-BACK: PASS` or `PARTIAL` | 4 |
| 18 | DRIVEN | what to send back | two small files | 3 |
| | | **total** | | **74** |

Booting KSP twice is inside steps 3 and 16. The plan allows 1.5 hours for S4a.

## 0. Variables and preconditions

Same variables as `PC-SETUP.md`; replace the one placeholder.

```powershell
Set-Location "<UMBRELLA ROOT>"
$U  = (Get-Location).Path
$D  = Get-Date -Format yyyy-MM-dd
$W  = "$U\Parsek-spike-mp-r0"
$I  = "$U\automation\spike-r0"
$C  = "$U\logs\console\$D"
$RS = "$U\logs\spikes\S4a\$D"
New-Item -ItemType Directory -Force -Path $RS, $C | Out-Null
Get-Process KSP_x64 -ErrorAction SilentlyContinue
Test-Path "$U\automation\.ksp-machine.lock"
Get-ChildItem Env:PARSEK_* -ErrorAction SilentlyContinue
Test-Path "$I\saves\spike-s0\persistent.sfs", "$I\saves\SPIKE-S4A-ON", "$I\saves\SPIKE-S4A-OFF", "$I\saves\SPIKE-REFUSE-1", "$I\saves\SPIKE-S4A-NEW"
Test-Path "$I\parsek-spike\S4a"
```

Check: `Get-Process` and `Get-ChildItem` print nothing, the lock prints `False`; the save
line prints `True False False False False` (`spike-s0` is the career of `PC-SETUP.md`
step 9; if it is missing, do that step first). If the last line prints `True`, an earlier
attempt left CSVs: rename the folder, never delete it:
`Rename-Item "$I\parsek-spike\S4a" "S4a-old-$(Get-Date -Format HHmmss)"`. If a `SPIKE-*`
save exists from an earlier attempt, rename it the same way (`Rename-Item "$I\saves\<name>" "<name>-old"`).

## 1. Build and deploy the S4a DLL (no dev-install deploy)

`PC-SETUP.md` "Later spike builds", with `$Sx = "S4a"`. The S4a code must be in the
branch: `git -C $W log --oneline -5` shows the S4a commits (the supervisor merges
`spike/mp-r0-s4a` into `spike/mp-r0`; run `git -C $W pull --ff-only` first).

```powershell
$Sx = "S4a"
git -C $W pull --ff-only
Set-Location $W
dotnet build Source\Parsek\Parsek.csproj -c Debug -p:SkipKspDeploy=true *> "$C\spike-$Sx-build.log"
$LASTEXITCODE
Copy-Item "$W\Source\Parsek\bin\Debug\Parsek.dll" "$I\GameData\Parsek\Plugins\Parsek.dll"
python "$W\spikes\r0\s0_check.py" dll "$I\GameData\Parsek\Plugins\Parsek.dll" --utf8 SpikeS4EventOrder --utf8 SpikeS4Probe --utf8 SpikeS4ProbeTests | Tee-Object "$RS\deployed-dll.txt"
"{0}  {1}  {2}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath "$I\GameData\Parsek\Plugins\Parsek.dll").Hash.ToLowerInvariant(), (git -C $W rev-parse HEAD), "$I\GameData\Parsek\Plugins\Parsek.dll" | Set-Content "$RS\deployed-dll-sha256.txt"
Get-Content "$RS\deployed-dll-sha256.txt"
```

Check: exit `0`; `RESULT: PASS` with the three class names found in UTF-8. The hash line
is the "deployed DLL sha256" of the report.

## 2. Stage the three spike saves

Each is a copy of `spike-s0`. The script writes only into the new folder, refuses one that
exists, and refuses any target inside a `Kerbal Space Program` folder.

```powershell
$S4 = "$W\spikes\r0\S4\summarize_s4a.py"
python $S4 stage --from "$I\saves\spike-s0" --to "$I\saves\SPIKE-S4A-ON"  --allow-other-sites true
python $S4 stage --from "$I\saves\spike-s0" --to "$I\saves\SPIKE-S4A-OFF" --allow-other-sites false
python $S4 stage --from "$I\saves\spike-s0" --to "$I\saves\SPIKE-REFUSE-1"
```

Check: three lines `staged ... AllowOtherLaunchSites=True|False|unchanged sha256=...`. An
error naming `AllowOtherLaunchSites lines (expected 1)` means the career has no such key:
stop and send the line back.

## 3. Session 1: arm and launch

The seam (`PARSEK_TEST_COMMANDS=1`) is armed only so steps 5 and 10 can ask the game to
load a save from the Space Center without the main menu (the "race to single player"
route of the plan). Its channel files sit in the instance root; clear any old ones first
(single named files, never a folder).

```powershell
foreach ($f in "parsek-test-commands.txt", "parsek-test-responses.txt", "parsek-test-commands.journal", "parsek-test-commands.lock") {
  if (Test-Path -LiteralPath "$I\$f") { Remove-Item -LiteralPath "$I\$f" }
}
$env:PARSEK_SPIKE = "S4a"
$env:PARSEK_TEST_COMMANDS = "1"
Remove-Item Env:PARSEK_SPIKE_S4A_TURNBACK -ErrorAction SilentlyContinue
$ksp = Start-Process -FilePath "$I\KSP_x64.exe" -WorkingDirectory $I -ArgumentList "-logFile `"$I\Player.log`"" -PassThru
function S4Log([string]$p) { Select-String -Path "$I\KSP.log" -SimpleMatch -Pattern $p | Select-Object -Last 3 | ForEach-Object { $_.Line } }
```

The prompt comes back at once (KSP keeps running). Wait for the main menu. Then:

```powershell
S4Log "[Spike.S4a] hooks applied="
S4Log "[Spike] patch summary:"
```

Check: `hooks applied=15 missing=0` and `patch summary: applied=1 failed=0 ...`. A
`missing=` above 0: run `S4Log "[Spike.S4a] hook missing"` and send the lines back; the
session can still go on (that hook's events are simply absent).

## 4. HAND: Resume Saved, `SPIKE-S4A-ON` (way 1: resume)

In the game: Start Game, Resume Saved, `SPIKE-S4A-ON`, Load. Wait at the Space Center
for 10 seconds, touching nothing (the dump runs 3 seconds after arrival).

```powershell
S4Log "[Spike.S4a] dump done"
```

Check: `dump done id=d1-SPACECENTER stateRows=<hundreds> footprints=<n> corners=<n> circles=<n> errors=0`.

If no `dump done` line appears within 30 seconds, run the dump by hand: Ctrl+Shift+T,
category `SpikeS4`, run `DumpKscState`, and note it in the report.

## 5. DRIVEN: seam load into `SPIKE-S4A-OFF` (way 2: seam)

Still at the Space Center:

```powershell
Add-Content -LiteralPath "$I\parsek-test-commands.txt" -Value "id=s4a-seam-1 cmd=LoadGame save=SPIKE-S4A-OFF name=persistent"
```

The Space Center reloads by itself. Wait 10 seconds after it is back.

```powershell
Select-String -Path "$I\parsek-test-responses.txt" -SimpleMatch -Pattern "id=s4a-seam-1 "
S4Log "[Spike.S4a] dump done"
```

Check: a response line with `verdict=OK` (an earlier `ACCEPTED` line may precede it) and
`dump done id=d2-SPACECENTER ... errors=0`. A `REJECTED` or `ERROR` line: send it back.

## 6. HAND: launch a pod (the launch step)

VAB, New, put a `Mk1 Command Pod` (or any root part you have) down, Launch, accept the
crew. On the pad, wait 10 seconds without staging.

Check: `S4Log "[Spike.S4a] dump done"` shows `id=d3-FLIGHT ... errors=0`.

## 7. HAND: quicksave, quickload (way 3: quickload)

Press F5, wait 2 seconds, then hold F9 until the game loads the quicksave. Wait 10 seconds.

Check: `dump done id=d4-FLIGHT ... errors=0`.

## 8. HAND: Revert to Launch (way 4: revert)

Escape, Revert to Launch (if it asks, confirm). Wait 10 seconds.

Check: `dump done id=d5-FLIGHT ... errors=0`.

## 9. HAND: back to the Space Center

Escape, Space Center (if asked, recover or leave the vessel, either is fine). Wait 10 seconds.

Check: `dump done id=d6-SPACECENTER ... errors=0`.

## 10. DRIVEN: seam load back into `SPIKE-S4A-ON` (way 2 again, other sites allowed)

```powershell
Add-Content -LiteralPath "$I\parsek-test-commands.txt" -Value "id=s4a-seam-2 cmd=LoadGame save=SPIKE-S4A-ON name=persistent"
```

Wait 10 seconds after the Space Center is back.

```powershell
Select-String -Path "$I\parsek-test-responses.txt" -SimpleMatch -Pattern "id=s4a-seam-2 "
S4Log "[Spike.S4a] dump done"
```

Check: `verdict=OK`; `dump done id=d7-SPACECENTER ... errors=0`.

## 11. HAND: a launch with other launch sites allowed

As step 6 (VAB, pod, Launch, wait 10 seconds on the pad). Then Escape, Space Center.

Check: `dump done id=d8-FLIGHT ... errors=0` (and a SPACECENTER dump after it).

## 12. HAND: New Game (way 5: new)

Escape, Quit to Main Menu (Save and Quit if asked). Start Game, Start New, Career, name
`SPIKE-S4A-NEW`, leave every other setting as it is, Start. Wait 10 seconds at the Space
Center.

Check: the newest `dump done` line names a `SPACECENTER` dump with `errors=0`.

## 13. HAND: quit

Escape, Quit to Main Menu, Quit. Then:

```powershell
$ksp.WaitForExit()
Remove-Item Env:PARSEK_SPIKE, Env:PARSEK_TEST_COMMANDS
```

## 14. DRIVEN: collect, check, summarize session 1

```powershell
Copy-Item "$I\KSP.log" "$RS\session1-KSP.log"
Copy-Item "$I\parsek-test-responses.txt" "$RS\session1-seam-responses.txt"
Copy-Item -Recurse "$I\parsek-spike\S4a" "$RS\csv"
Select-String -Path "$RS\session1-KSP.log" -SimpleMatch -Pattern "[Parsek][WARN][Spike" | Select-Object -First 20 | ForEach-Object { $_.Line }
Select-String -Path "$RS\session1-KSP.log" -SimpleMatch -Pattern "[Spike.S4a] dump done" | ForEach-Object { $_.Line }
python $S4 summarize "$RS\csv" --out "$RS\summary-s4a.md" *> "$C\s4a-summary.log"
Get-Content "$RS\summary-s4a.md" | Select-String -SimpleMatch -Pattern "Ways covered", "Field completeness", "| # | way"
```

`$RS\csv` holds no subfolder link, so `Copy-Item -Recurse` is safe here (the forbidden
list of `PC-SETUP.md` is about the instance folders).

Check: no `[WARN][Spike` line (a `hook missing` or `dump section ... failed` line goes into
the report as is); nine or more `dump done` lines, each `errors=0`;
`Ways covered: 5 of 5 (resume, new, seam, quickload, revert)` and
`Field completeness: PASS`. The ways table also lists a `launch` row (step 6 and 11).

## 15. DRIVEN: hash the turn-back save before session 2

```powershell
$h1 = (Get-FileHash -Algorithm SHA256 -LiteralPath "$I\saves\SPIKE-REFUSE-1\persistent.sfs").Hash.ToLowerInvariant()
$h1 | Set-Content "$RS\refuse-before.sha256"
Copy-Item "$I\saves\SPIKE-REFUSE-1\persistent.sfs" "$RS\refuse-before.sfs"
$h1
```

## 16. HAND: session 2, the turn-back

```powershell
$env:PARSEK_SPIKE = "S4a"
$env:PARSEK_SPIKE_S4A_TURNBACK = "1"
$ksp = Start-Process -FilePath "$I\KSP_x64.exe" -WorkingDirectory $I -ArgumentList "-logFile `"$I\Player.log`"" -PassThru
```

At the main menu:

1. Start Game, Resume Saved, `SPIKE-REFUSE-1`, Load. Expected: no scene loads; you stay
   at the main menu and a message reads `[Parsek] Spike S4a: this save was turned back
   (SPIKE-REFUSE-1)` for 10 seconds. Press F1 while it shows (stock screenshot into
   `$I\Screenshots`). Note in one line what the menu shows (which buttons, whether the
   load dialog is still open).
2. From that same menu: Start Game, Resume Saved, `SPIKE-S4A-ON`, Load. Expected: the
   Space Center loads normally.
3. Escape, Quit to Main Menu, Quit.

```powershell
$ksp.WaitForExit()
Remove-Item Env:PARSEK_SPIKE, Env:PARSEK_SPIKE_S4A_TURNBACK
```

If the menu is stuck (no button reacts), close KSP from the task bar, note it, and go on
with step 17: the check reports FAIL, which is a result.

## 17. DRIVEN: hash after, turn-back check

```powershell
$h2 = (Get-FileHash -Algorithm SHA256 -LiteralPath "$I\saves\SPIKE-REFUSE-1\persistent.sfs").Hash.ToLowerInvariant()
$h2 | Set-Content "$RS\refuse-after.sha256"
Copy-Item "$I\saves\SPIKE-REFUSE-1\persistent.sfs" "$RS\refuse-after.sfs"
Copy-Item "$I\KSP.log" "$RS\session2-KSP.log"
Get-ChildItem "$I\Screenshots" | Sort-Object LastWriteTime | Select-Object -Last 1 | Copy-Item -Destination "$RS\turnback-message.png"
python $S4 turnback --log "$RS\session2-KSP.log" --before-hash $h1 --after-hash $h2 --before-sfs "$RS\refuse-before.sfs" --after-sfs "$RS\refuse-after.sfs" | Tee-Object "$RS\turnback.txt"
```

Check: the last line is `TURN-BACK: PASS` (hash unchanged) or `TURN-BACK: PARTIAL` (stock
re-wrote the file with the same content); `FAIL` lines name the reason. Any of the three is
a result; the report needs the line.

## 18. What to send back

- `$RS\summary-s4a.md` and `$RS\turnback.txt` (spike saves only: stock coordinates,
  event names, counts, key paths; no save content of yours).
- The line from `$RS\deployed-dll-sha256.txt` and the menu note of step 16.
- `$RS\turnback-message.png` if it is under 2 MB.

The rest of `$RS` (two KSP logs, the CSVs, the two `persistent.sfs` copies) stays on the
PC; the report lists it as raw artifacts.

## Stock members this spike touches (all checked against KSP 1.12.5 `Assembly-CSharp.dll`)

Read: `SpaceCenter.Instance`, `.Latitude`, `.Longitude`, `.SrfNVector`, `.AreaRadius`,
`.cb`, `.spaceCenterAreaTrigger`; `PQSCity` (all fields, `lod[i].visibleRange`),
`PQSCity2`, `PQSMod_MapDecalTangent`, `PQSMod_MapDecal` (all fields, `position`, `radius`);
`SpaceCenterCamera2` (all fields, `GetPivot()`); `PSystemSetup.Instance`,
`.SpaceCenterFacilities`, `.LaunchSites`, `.StockLaunchSites`, `.NonStockLaunchSites`,
`.IsStockLaunchSite(string)`; `PSystemSetup.SpaceCenterFacility` (fields, `.spawnPoints`,
`.IsLaunchFacility()`) and its `SpawnPoint` (`.GetSpawnPointTransform()`); `LaunchSite`
(fields, `.spawnPoints`, `.isPQSCity`, `.isPQSCity2`, `.IsSetup`, `.Body`,
`.launchSiteTransform`, `.pqsCity2`) and `LaunchSite.SpawnPoint`;
`CelestialBody.pqsController`, `.GetLatLonAlt(Vector3d, out, out, out)`;
`FlightGlobals.GetHomeBody()`; `HighLogic.CurrentGame` (get / set), `.LoadedScene`,
`.SaveFolder`; `Game.Title`, `.Mode`, `.startScene`, `.Parameters.Difficulty.AllowOtherLaunchSites`.

Patched by hand (prefix unless noted): `Game.Start()`; `SpaceCenter.Start()` (postfix);
`PQSCity.OnSetup()` (postfix); `MainMenu.OnLoadDialogPipelineFinished(ConfigNode, string)`
(protected); `MainMenu.CreateAndStartGame(string, Game.Modes, GameScenes, EditorFacility)`
(private); `QuickSaveLoad.quickLoad(string, string)` (private);
`FlightDriver.RevertToLaunch()`; `FlightDriver.StartWithNewLaunch(string, string, string,
VesselCrewManifest)`; `FlightDriver.StartAndFocusVessel(Game, int)` and `(string, int)`;
`GamePersistence.LoadGame(string, string, bool, bool)`;
`GamePersistence.SaveGame(Game, string, string, SaveMode)` and the internal
`SaveGame(string, string, SaveMode, GameScenes)`; `HighLogic.LoadScene(GameScenes)`;
Parsek's own `ParsekScenario.OnLoad(ConfigNode)`.

GameEvents: `onGameSceneLoadRequested`, `onGameSceneSwitchRequested`,
`onGameStateCreated`, `onGameStateLoad`, `onGameStatePostLoad`, `onGameNewStart`,
`onLevelWasLoaded`, `onLevelWasLoadedGUIReady`, `onFlightReady`.

How checked: the spike compiles against the reference assemblies (every name above that
the code uses directly), and each Harmony target was read from
`ikdasm Assembly-CSharp.dll` (name, visibility, exact argument types). Each target is
resolved alone at runtime and a miss is logged as `hook missing: ...`, so a wrong name
costs one event column, not the session.
