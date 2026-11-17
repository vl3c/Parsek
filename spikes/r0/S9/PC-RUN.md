# S9 on the PC: which debug actions a mod can observe (about 105 minutes)

Spike S9 of `docs/dev/plans/multiplayer/r0-spikes.md` ("S9 Which debug actions a mod can
observe"). One KSP session on the spike instance `automation\spike-r0`, then one Python
command that prints the report cells. The spike only reads the game: every hook logs one
line and returns. What changes is the spike saves you press the debug buttons in: four
copies of the `spike-s0` career that `PC-SETUP.md` step 9 created, plus one new career
made in this run. None of your own saves is opened or copied.

Run it in ONE PowerShell 7 window, top to bottom. Every step ends with a check; when a check
fails, stop and send back the step number and its output.

## How the run is cut into segments

Each group of presses starts with one PowerShell line, `Seg <name>`. It appends a
`MissionMark` command to the Parsek command seam, and the game writes
`MISSIONMARK label=s9-seg-<name>` into `KSP.log` as soon as it has focus again. The
summarizer counts the spike's lines from one mark to the next. So, for every segment:

1. Get the debug screen the group needs ready in the game (Alt+F12, the tab named in
   the step).
2. Switch to PowerShell, type `Seg <name>`, switch back to KSP.
3. Wait 2 seconds, then do exactly the presses of the step, **2 seconds apart** (the
   switches are also read by a 1 Hz poll, which must see each change).

A press too many or too few shows as `FAIL` on that tool's row; it is not a disaster,
but note it in the step's line of the report. Never press a button the step does not
name while a segment runs; if you must (a dialog), note it.

## What the harness drives and what needs your hands

DRIVEN = a command you paste that runs on its own (a script, or a line appended to the
Parsek command seam). HAND = clicks or keys in the game.

| step | DRIVEN or HAND | what to do | what proves it worked | minutes |
|---:|---|---|---|---:|
| 0 | DRIVEN | variables, preconditions | the checks of step 0 | 2 |
| 1 | DRIVEN | build the spike DLL, copy it into `spike-r0`, verify it | two `RESULT: PASS` | 8 |
| 2 | DRIVEN | stage four spike saves from `spike-s0` | four `staged ...` lines | 2 |
| 3 | DRIVEN | clear the seam files, arm, launch KSP, define `Seg` | `hooks applied=85 missing=0 failed=0` | 3 |
| 4 | HAND | Resume Saved `SPIKE-S9-PRESS`, open Alt+F12 | `inventory:` line for `SPACECENTER` | 3 |
| 5 | HAND | `Seg career`: 12 currency buttons x 3 | (summary) | 3 |
| 6 | HAND | `Seg max`: Max Tech, Max Facilities, Max XP x 3 | (summary) | 2 |
| 7 | HAND | `Seg contracts`: generate, accept / complete, decline, four tools, each x 3 | (summary) | 6 |
| 8 | HAND | `Seg kerbal`: create a kerbal x 3 | (summary) | 3 |
| 9 | HAND | `Seg physics`: Physics Load x 3 (never Save) | (summary) | 1 |
| 10 | HAND | `Seg difficulty`: 7 switches x 6 clicks | (summary) | 4 |
| 11 | HAND | `Seg console-ksc`: `/funds`, `/science`, `/reputation` x 3 | `[DebugConsole]: Funds set to` x 3 | 3 |
| 12 | HAND | `Seg idle-flight`: VAB, a pod, Launch | `inventory:` line for `FLIGHT` | 4 |
| 13 | HAND | `Seg toggles`: 13 cheat switches x 6 clicks | (summary) | 6 |
| 14 | HAND | `Seg heat`: heat generation switch x 6 | (summary) | 1 |
| 15 | HAND | `Seg gravity`: drag the gravity slider, Reset x 3 | (summary) | 2 |
| 16 | HAND | `Seg comets`: 10 spawn buttons x 3, colliders x 6, kill x 3 | (summary) | 5 |
| 17 | HAND | `Seg teleport`: Set Orbit, Rendezvous, console `/rendezvous`, Set Position, each x 3 | (summary) | 8 |
| 18 | HAND | `Seg repair`: Repair x 3 | (summary) | 1 |
| 19 | HAND | `Seg thrower`: arm switch x 6 (two throws while armed) | (summary) | 2 |
| 20 | HAND | legacy window, only if the inventory shows one | (summary) | 2 |
| 21 | HAND | `Seg idle`, quit to the main menu; new Easy career `SPIKE-S9-CONTROL`; `Seg control`: normal play | (summary) | 15 |
| 22 | HAND | `SPIKE-S9-MS-EARLY`: `Seg ms-early`, right-click Max Progression x 3 | (summary) | 3 |
| 23 | HAND | `SPIKE-S9-MS-FULL`: `Seg ms-full`, left-click Max Progression x 3 | (summary) | 3 |
| 24 | HAND | `SPIKE-S9-MS-B4D455`: `Seg ms-b4d455`, console `/b4d455` x 3 | (summary) | 3 |
| 25 | HAND | quit KSP | prompt returns | 1 |
| 26 | DRIVEN | collect and summarize | `HOOKS: PASS`, `PRESSES:`, `CONTROL:`, `MILESTONES:` lines | 5 |
| 27 | DRIVEN | what to send back | one small file | 2 |
| | | **total** | | **103** |

The plan allows 2 hours on the PC for S9. Booting KSP is inside step 3.

## 0. Variables and preconditions

Same variables as `PC-SETUP.md`; replace the one placeholder. (`spike-r0-nodlc` works as
well: set `$I` to it.)

```powershell
Set-Location "<UMBRELLA ROOT>"
$U  = (Get-Location).Path
$D  = Get-Date -Format yyyy-MM-dd
$W  = "$U\Parsek-spike-mp-r0"
$I  = "$U\automation\spike-r0"
$C  = "$U\logs\console\$D"
$RS = "$U\logs\spikes\S9\$D"
New-Item -ItemType Directory -Force -Path $RS, $C | Out-Null
Get-Process KSP_x64 -ErrorAction SilentlyContinue
Test-Path "$U\automation\.ksp-machine.lock"
Get-ChildItem Env:PARSEK_* -ErrorAction SilentlyContinue
Test-Path "$I\saves\spike-s0\persistent.sfs", "$I\saves\SPIKE-S9-PRESS", "$I\saves\SPIKE-S9-MS-EARLY", "$I\saves\SPIKE-S9-MS-FULL", "$I\saves\SPIKE-S9-MS-B4D455", "$I\saves\SPIKE-S9-CONTROL"
Test-Path "$I\parsek-spike\S9"
```

Check: `Get-Process` and `Get-ChildItem` print nothing, the lock prints `False`; the save
line prints `True False False False False False` (`spike-s0` is the career of
`PC-SETUP.md` step 9; if it is missing, do that step first). If the last line prints
`True`, an earlier attempt left a CSV: rename the folder, never delete it:
`Rename-Item "$I\parsek-spike\S9" "S9-old-$(Get-Date -Format HHmmss)"`. If a `SPIKE-S9-*`
save exists from an earlier attempt, rename it the same way
(`Rename-Item "$I\saves\<name>" "<name>-old"`).

## 1. Build and deploy the S9 DLL (no dev-install deploy)

`PC-SETUP.md` "Later spike builds", with `$Sx = "S9"`. The S9 code must be in the branch:
`git -C $W log --oneline -8` shows the S9 commits (the supervisor merges
`spike/mp-r0-s9` into `spike/mp-r0`).

```powershell
$Sx = "S9"
git -C $W pull --ff-only
Set-Location $W
dotnet build Source\Parsek\Parsek.csproj -c Debug -p:SkipKspDeploy=true *> "$C\spike-$Sx-build.log"
$LASTEXITCODE
Copy-Item "$W\Source\Parsek\bin\Debug\Parsek.dll" "$I\GameData\Parsek\Plugins\Parsek.dll"
python "$W\spikes\r0\s0_check.py" dll "$I\GameData\Parsek\Plugins\Parsek.dll" --utf8 SpikeS9Cheats --utf8 SpikeS9Hooks --utf8 SpikeS9Format | Tee-Object "$RS\deployed-dll.txt"
"{0}  {1}  {2}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath "$I\GameData\Parsek\Plugins\Parsek.dll").Hash.ToLowerInvariant(), (git -C $W rev-parse HEAD), "$I\GameData\Parsek\Plugins\Parsek.dll" | Set-Content "$RS\deployed-dll-sha256.txt"
Get-Content "$RS\deployed-dll-sha256.txt"
```

Check: exit `0`; `RESULT: PASS` with the three class names found in UTF-8. The hash line
is the "deployed DLL sha256" of the report.

## 2. Stage the four spike saves

Plain copies of `spike-s0`. Each milestone way gets its own copy, so its progress nodes
are not done yet (plan: "each way that completes milestones runs from a fresh career
copy"). The script writes only into the new folder, refuses one that exists, and refuses
any target inside a `Kerbal Space Program` folder.

```powershell
$S9 = "$W\spikes\r0\S9\summarize_s9.py"
foreach ($n in "SPIKE-S9-PRESS", "SPIKE-S9-MS-EARLY", "SPIKE-S9-MS-FULL", "SPIKE-S9-MS-B4D455") {
  python $S9 stage --from "$I\saves\spike-s0" --to "$I\saves\$n"
}
```

Check: four lines `staged ... sha256=...`, all with the same hash.

## 3. Arm and launch, define the two helpers

The seam (`PARSEK_TEST_COMMANDS=1`) is armed only for the segment marks. Clear any old
channel files first (single named files, never a folder).

```powershell
foreach ($f in "parsek-test-commands.txt", "parsek-test-responses.txt", "parsek-test-commands.journal", "parsek-test-commands.lock") {
  if (Test-Path -LiteralPath "$I\$f") { Remove-Item -LiteralPath "$I\$f" }
}
$env:PARSEK_SPIKE = "S9"
$env:PARSEK_TEST_COMMANDS = "1"
$ksp = Start-Process -FilePath "$I\KSP_x64.exe" -WorkingDirectory $I -ArgumentList "-logFile `"$I\Player.log`"" -PassThru
$script:mk = 0
function Seg([string]$name) {
  $script:mk++
  Add-Content -LiteralPath "$I\parsek-test-commands.txt" -Value "id=s9-mk-$($script:mk) cmd=MissionMark label=s9-seg-$name"
  "mark $($script:mk): s9-seg-$name queued; switch to KSP, wait 2 s, then press"
}
function S9Log([string]$p) { Select-String -Path "$I\KSP.log" -SimpleMatch -Pattern $p | Select-Object -Last 3 | ForEach-Object { $_.Line } }
```

Wait for the main menu. Then:

```powershell
S9Log "[Spike.S9] hooks applied="
S9Log "[Spike] patch summary:"
S9Log "[Spike.S9] event host up"
```

Check: `hooks applied=85 missing=0 failed=0`, `patch summary: applied=1 failed=0 ...`,
and `event host up: 12 GameEvents subscribed`. A `missing=` or `failed=` above 0: run
`S9Log "[Parsek][WARN][Spike.S9]"` and send the lines back; the run can go on (that
hook's rows read 0).

## 4. HAND: load `SPIKE-S9-PRESS`, open the debug window

Start Game, Resume Saved, `SPIKE-S9-PRESS`, Load. At the Space Center press Alt+F12 and
click through the tabs once (Cheats, Contracts, Gameplay / Kerbals, Difficulty, Physics,
Console) so you know where each is. Then:

```powershell
S9Log "[Spike.S9] inventory:"
```

Check: a line `inventory: ... scene=SPACECENTER DebugToolbar=<n> ConsoleCommands=<n>
CheatsCareer=<n> ...`. Write down `DebugToolbar=<n>`: step 20 runs only when some
inventory line shows `DebugToolbar=` above 0 AND Alt+F12 brings up a second, old-style
window.

## 5. `Seg career`: the currency buttons (Cheats tab)

Cheats tab ready. `Seg career`. Then press each of the twelve funds / science / reputation
buttons (plus and minus, small and big) **3 times**, 2 seconds apart, one button after
the other. 36 presses.

## 6. `Seg max`: Max Tech, Max Facilities, Max XP (Cheats tab)

`Seg max`. Press Max Tech 3 times, Max Facilities 3 times, Max XP 3 times (2 seconds
apart; the first press of each does the work, the next two find nothing left to do).
Do NOT touch Max Progression here (steps 22 and 23 do it on fresh copies).

## 7. `Seg contracts`: the contract screens (Contracts tab)

Contracts tab ready. `Seg contracts`. Then, 2 seconds apart:

1. In the add / new contracts list, press Generate (any contract type, any prestige) 3
   times.
2. In the existing contracts list, press the LEFT button of an Offered contract (it
   accepts), press the left button of the same, now Active, contract (it completes),
   and press the left button of another Offered contract (it accepts): 3 left presses.
3. Press the RIGHT button of three different Offered contracts (declines): 3 right
   presses.
4. In Tools: Regenerate Current x 3, Reset Weights x 3, Clear Finished x 3, then Clear
   Current x 3 (last, it empties the list).

Note in one line which labels the left and right buttons showed.

## 8. `Seg kerbal`: create kerbals (Gameplay / Kerbals tab)

`Seg kerbal`. Create a kerbal 3 times (type a name, Submit; any names).

## 9. `Seg physics`: reload the physics database (Physics tab)

`Seg physics`. Press Load 3 times. **Never press Save**: it writes the instance's
physics file.

## 10. `Seg difficulty`: the Difficulty screen

Difficulty tab ready. `Seg difficulty`. It has no apply button; each switch writes at
once. Click each of its 7 switches 6 times (on, off, on, off, on, off, so each ends as it
started), **2 seconds apart**. 42 clicks, about a minute and a half.

## 11. `Seg console-ksc`: the console (Console tab)

Console tab ready. `Seg console-ksc`. Type each line and press Enter, 2 seconds apart:

```
/funds 1000000
/funds 1000000
/funds 1000000
/science 500
/science 500
/science 500
/reputation 100
/reputation 100
/reputation 100
```

Check: `S9Log "[DebugConsole]: Reputation set to"` prints a line.

## 12. `Seg idle-flight`: launch a pod

`Seg idle-flight` (anything that fires while the scene changes counts as "outside a
press", and the report lists it). VAB, New, a `Mk1 Command Pod` (or any root part),
Launch, accept the crew. On the pad, open Alt+F12, Cheats tab, and wait 5 seconds.

Check: `S9Log "[Spike.S9] inventory:"` shows a `scene=FLIGHT` line.

## 13. `Seg toggles`: the cheat switches (Cheats tab)

Cheats tab ready. `Seg toggles`. Click each of these 13 switches 6 times, **2 seconds
apart** (each ends as it started): Allow Part Clipping, Biomes Visible in Map, Ignore EVA
Construction Mass Limit, Ignore Kerbal Inventory Limits, Ignore Max Temperature,
Infinite Electricity, Infinite Propellant, Middle Mouse Click Set Position, No Crash
Damage, Non Strict Part Attachment, Pause On Vessel Unpack, Unbreakable Joints, and the
Hack Gravity switch. 78 clicks, about three minutes. Leave the strings / localisation
switches alone.

## 14. `Seg heat`: the heat generation switch (Resources tab)

`Seg heat`. Click the heat generation switch 6 times, 2 seconds apart.

## 15. `Seg gravity`: the gravity slider (Cheats tab)

`Seg gravity`. Turn the Hack Gravity switch on, drag its slider to about half and wait
3 seconds, press Reset 3 times (2 seconds apart), turn the switch off. (The two switch
clicks show as "other" lines of this segment; that is expected.)

## 16. `Seg comets`: the comet buttons (Cheats tab, Comets)

`Seg comets`. 2 seconds apart: each of the 10 spawn buttons (random, short, mid, long,
interstellar, and the five sentinel ones) 3 times; the colliders switch 6 times; Kill 3
times last. The comets are what step 17 rendezvouses with.

## 17. `Seg teleport`: Set Orbit, Rendezvous, console rendezvous, Set Position

`Seg teleport`. 5 seconds apart:

1. Set Orbit screen: leave Kerbin and a valid orbit (for example 700000 m), press Set
   Orbit 3 times.
2. Its rendezvous part: pick one of the comets, press Rendezvous 3 times.
3. Console tab: type `/vessels`, Enter; note a comet's index `<n>`; then
   `/rendezvous <n>` three times.
4. Set Position screen: leave the default position near the Space Center, press Set
   Position 3 times.

If the vessel is destroyed on the way, note the sub-step and go on from step 18 with a
new pod only if Repair needs one (it does not).

## 18. `Seg repair`: Repair (Cheats tab)

`Seg repair`. Press Repair (fix repairable parts) 3 times. A pod has no repairable
module, so its effect row reads 0; that is a result.

## 19. `Seg thrower`: the object thrower (Cheats tab)

`Seg thrower`. Click the arm switch 6 times, 2 seconds apart; while it is armed the
second time, click twice on open ground away from the vessel (two throws).

## 20. Legacy cheat window (only if step 4 or 12 showed `DebugToolbar=` above 0)

If an old-style debug window shows besides the new one: `Seg legacy`, press each of its
science, funds, reputation and experience cheat buttons 3 times, and note what the
buttons read. Otherwise skip this step and note "no legacy window"; the classification
lists the legacy entry points as "not seen in this run".

## 21. The control session: normal play in a new career

1. `Seg idle`. Escape, Quit to Main Menu (Save and quit).
2. Start Game, Start New, Career, name `SPIKE-S9-CONTROL`, difficulty preset **Easy**
   (250000 funds, so a facility upgrade can be bought), Start. Wait at the Space Center.
3. `Seg control`. From here on **no Alt+F12** until step 22.
4. Mission Control: accept "Launch our first vessel!" (or, if it is not offered, the
   cheapest contract; note which).
5. Space Center: upgrade one facility (Launch Pad is fine), confirm the cost.
6. VAB: a `Mk1 Command Pod`, Launch. On the pad: right-click the pod, Crew Report,
   Transmit. EVA the kerbal, right-click the kerbal, EVA Report, then board the pod
   again (the report goes into the pod).
7. F5, wait 2 seconds, hold F9 until the quicksave loads.
8. Recover the vessel (the button at the top of the screen); answer any Parsek merge
   dialog with its default button.
9. R&D: buy one tech node you can afford (5 science; note which). If science is short,
   note it and go on.
10. Wait 5 seconds at the Space Center.

Note in one line each sub-step that did not go as written.

## 22. Milestone way 1: Max Progression, right click (early progression)

1. `Seg idle`. Escape, Quit to Main Menu. Resume Saved `SPIKE-S9-MS-EARLY`, Load.
2. Alt+F12, Cheats tab. `Seg ms-early`.
3. RIGHT-click Max Progression 3 times, 5 seconds apart. Wait 10 seconds.

If Max Progression is not on the Space Center's Cheats tab, launch a pod and do it on
the pad; note it.

## 23. Milestone way 2: Max Progression, left click (full progression)

1. `Seg idle`. Escape, Quit to Main Menu. Resume Saved `SPIKE-S9-MS-FULL`, Load.
2. Alt+F12, Cheats tab. `Seg ms-full`.
3. LEFT-click Max Progression 3 times, 10 seconds apart (it walks every body). Wait 10
   seconds.

## 24. Milestone way 3: the console's `b4d455`

1. `Seg idle`. Escape, Quit to Main Menu. Resume Saved `SPIKE-S9-MS-B4D455`, Load.
2. Alt+F12, Console tab. `Seg ms-b4d455`.
3. Type `/b4d455`, Enter, 3 times, 10 seconds apart. Wait 10 seconds.

## 25. Quit

`Seg idle`. Escape, Quit to Main Menu, Quit. Then:

```powershell
$ksp.WaitForExit()
Remove-Item Env:PARSEK_SPIKE, Env:PARSEK_TEST_COMMANDS
```

## 26. DRIVEN: collect and summarize

```powershell
Copy-Item "$I\KSP.log" "$RS\KSP.log"
Copy-Item "$I\parsek-test-responses.txt" "$RS\seam-responses.txt"
Copy-Item "$I\parsek-spike\S9\observed.csv" "$RS\observed.csv"
Select-String -Path "$RS\KSP.log" -SimpleMatch -Pattern "[Parsek][WARN][Spike" | Select-Object -First 20 | ForEach-Object { $_.Line }
python $S9 summarize "$RS\KSP.log" --out "$RS\summary-s9.md" *> "$C\s9-summary.log"
Get-Content "$RS\summary-s9.md" | Select-String -Pattern "^Segments:", "^HOOKS:", "^PRESSES:", "^CONTROL:", "^MILESTONES:"
```

Check:

- `Segments:` names every segment of steps 5 to 24 (`career`, `max`, `contracts`,
  `kerbal`, `physics`, `difficulty`, `console-ksc`, `idle-flight`, `toggles`, `heat`,
  `gravity`, `comets`, `teleport`, `repair`, `thrower`, `idle`, `control`, `idle#2`,
  `ms-early`, `idle#3`, `ms-full`, `idle#4`, `ms-b4d455`, `idle#5`). A missing name
  means its mark never reached the log: compare with `$RS\seam-responses.txt`.
- `HOOKS: PASS applied=85 missing=0 failed=0`.
- `PRESSES:`, `CONTROL:` and `MILESTONES:` are results, PASS or not; the report takes
  them as they are. `CONTROL: PARTIAL` means zero false positives but a control step
  missing (its row says which).

## 27. What to send back

- `$RS\summary-s9.md` (spike saves only: tool names, counts, progress node ids, UT
  values, switch names; no save content of yours).
- The line from `$RS\deployed-dll-sha256.txt`, the button labels of step 7, the legacy
  note of step 20, and any notes from steps 21 to 24.

The rest of `$RS` (`KSP.log`, `observed.csv`, the seam responses) stays on the PC; the
report lists it as raw artifacts.

## Stock members this spike touches (all checked against KSP 1.12.5 `Assembly-CSharp.dll`)

Patched by hand, a prefix that logs on entry and a finalizer that pops the debug scope
(85 targets, `SpikeS9Hooks.All()`):

- `KSP.UI.Screens.DebugToolbar.Screens.Cheats.CheatsCareer`: `OnFundsPosClick`,
  `OnFundsPosBigClick`, `OnFundsNegClick`, `OnFundsNegBigClick`, `OnSciencePosClick`,
  `OnSciencePosBigClick`, `OnScienceNegClick`, `OnScienceNegBigClick`, `OnRepPosClick`,
  `OnRepPosBigClick`, `OnRepNegClick`, `OnRepNegBigClick`, `OnMaxTechClick`,
  `OnMaxFacilityClick`, `OnMaxXPClick`, `FixRepairableParts` (all `()`).
- `...Cheats.MaxProgression.OnPointerClick(PointerEventData)`.
- `OnToggleChanged(bool)` of `AllowPartClipping`, `BiomesVisibleInMap`,
  `IgnoreEVAConstructionMassLimit`, `IgnoreKerbalInventoryLimits`, `IgnoreMaxTemperature`,
  `InfiniteElectricity`, `InfinitePropellant`, `MiddleMouseClickSetPosition`,
  `NoCrashDamage`, `NonStrictPartAttachment`, `PauseOnVesselUnpack`, `UnbreakableJoints`.
- `...Cheats.HackGravity`: `OnToggleChanged(bool)`, `OnSliderChanged(float)`,
  `OnResetClick()`, `SetGravityFactor(double)`.
- `...Cheats.SetOrbit`: `OnSetOrbitClick()`, `OnRendezvousClick()`;
  `...Cheats.SetPosition.OnSetPositionClick()`; `FlightGlobals.PostOrbitSet(CelestialBody)`.
- `...Cheats.CheatsComets`: `OnKillClick`, `OnRandomClick`, `OnShortClick`, `OnMidClick`,
  `OnLongClick`, `OnInterstellarClick`, `OnSentinelClick`, `OnSentinelShortClick`,
  `OnSentinelMidClick`, `OnSentinelLongClick`, `OnSentinelInterstellarClick` (all `()`),
  `OnCollidersClick(bool)`; `...Cheats.CheatsObjectThrower.OnArmedToggle(bool)`.
- `...Screens.Contract.ScreenContractExistingItem`: `OnLeftButtonClicked()`,
  `OnRightButtonClicked()`; `ScreenContractNewItem.GenerateContract(Type,
  Contract.ContractPrestige)` (static, private); `ScreenContractTools`:
  `OnResetWeightsClicked`, `OnRegenerateCurrentClicked`, `OnClearCurrentClicked`,
  `OnClearFinishedClicked`.
- `...Screens.GamePlay.ScreenKerbalCreate.OnSubmitClicked()`;
  `...Screens.Physics.ScreenPhysics`: `OnLoadClicked()`, `OnSaveClicked()`;
  `...Screens.Resources.ScreenResources.OnHeatGenerationToggle(bool)`.
- `ConsoleCommands` (each `(string)`): `OnFundsCommand`, `OnScienceCommand`,
  `OnReputationCommand`, `OnRendezvousCommand`, `OnTargetvCommand`, `OnBadassCommand`.
- The legacy `DebugToolbar`: `CheatInScience(float)`, `CheatInFunds(double)`,
  `CheatInReputation(float)`, `CheatInExperience()`.
- Effects: `ResearchAndDevelopment.CheatTechnology()`, `.CheatAddScience(float)`;
  `ScenarioUpgradeableFacilities.CheatFacilities()`; `KerbalRoster.CheatExperience()`
  (static); `ProgressTracking.CheatProgression()`, `.CheatEarlyProgression()`;
  `ProgressNode.CheatComplete()`; `ModuleDeployablePart.CheatRepair()`.
- Currency, logged only when the reason is `TransactionReasons.Cheating`:
  `Funding.AddFunds(double, TransactionReasons)`, `.SetFunds(double, ...)`;
  `ResearchAndDevelopment.AddScience(float, ...)`, `.SetScience(float, ...)`;
  `Reputation.AddReputation(float, ...)`, `.SetReputation(float, ...)`.

Read by the 1 Hz poll: every public static `bool` of `CheatOptions` (13 fields);
`HackGravity.gravityFactor` (public `double`) and `hacked` (protected `bool`, by
reflection); `CheatsObjectThrower.isArmed` (private `bool`, by reflection);
`DebugToolbar.toolbarShown` (public static); `HighLogic.CurrentGame.Parameters`
`.Difficulty.AllowStockVessels`, `.Difficulty.MissingCrewsRespawn`, `.Flight.CanRestart`,
`.Flight.CanLeaveToEditor`, `.Flight.CanQuickSave`, `.Flight.CanQuickLoad`;
`HighLogic.LoadedScene`; `Resources.FindObjectsOfTypeAll<T>()` for the inventory.
Per line: `Time.frameCount`, `Time.realtimeSinceStartup`, `Planetarium.fetch`,
`Planetarium.GetUniversalTime()`; in the event handlers `ProgressNode.Id`,
`.IsComplete`, `RDTech.techID`, `UpgradeableFacility.id`, `ProtoCrewMember.name`,
`Contract.ContractID`.

GameEvents subscribed: `OnProgressReached`, `OnProgressComplete`, `OnProgressAchieved`,
`OnFundsChanged`, `OnScienceChanged`, `OnReputationChanged`, `OnTechnologyResearched`,
`OnKSCFacilityUpgraded`, `onKerbalAdded`, `onKerbalLevelUp`, `Contract.onAccepted`,
`Contract.onCompleted`.

How checked: the spike compiles against the reference assemblies (every type, public
member and GameEvent above); the xUnit test
`SpikeS9HooksTests.EveryHook_ResolvesToADeclaredMethodOfTheReferenceAssembly` resolves all
85 targets with `AccessTools.DeclaredMethod` against the reference `Assembly-CSharp.dll`
(declared on that exact type, exact argument types; a renamed target reds it, checked by
mutation); the two reflected fields and every caller chain named in the report draft were
read from `ikdasm Assembly-CSharp.dll`. At runtime each target is resolved alone and a
miss is logged as `hook missing: ...`, so a wrong name costs one row, not the session.
