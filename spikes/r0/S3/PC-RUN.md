# S3 on the PC: the four looks, two test suit sets, a save without the DLC, a ghost kerbal (about 2 hours)

Spike S3 of `docs/dev/plans/multiplayer/r0-spikes.md` ("S3 The four looks, replacement
sets, a missing DLC, a suit on a ghost kerbal"). Three KSP sessions: a short one on
`automation\spike-r0` without the test sets (the baseline combo count), a long one on
`spike-r0` with the sets, and one on the no-DLC copy `automation\spike-r0-nodlc`. Then
one Python command prints the report cells. Almost everything is driven through the
Parsek command seam; your hands are needed for two short steps (8 and 10), and your eyes
for the checks.

What the spike changes: it copies one folder of test suit sets, `GameData\ParsekSpikeS3`,
into the two spike instances (never into `GameData\Squad`, which is a junction into the
dev install, and never into the dev install itself); it makes one new sandbox save
`SPIKE-S3-LOOKS` from a copy of the `spike-s0` career (`PC-SETUP.md` step 9) and copies
that save to the no-DLC instance. None of your own saves is opened or copied. Raw output
goes to `<umbrella>\logs\spikes\S3\<date>\`.

Run it in ONE PowerShell 7 window, top to bottom. Every step ends with a check; when a
check fails, stop and send back the step number and its output.

## Junction safety (read before step 3)

`PC-SETUP.md` "Forbidden commands" applies to this whole sheet. In short: never a
recursive delete, `rmdir /S`, `del /S`, Explorer delete or `robocopy /MIR` / `/PURGE` on
a junction or a folder that holds one; every copy here uses `Copy-Item` into a NEW folder
or `robocopy /E /XJ`. The only deletes in this sheet are single named files
(`Remove-Item -LiteralPath <file>`).

## What the harness drives and what needs your hands

DRIVEN = a command you paste that runs on its own (a script, or a line appended to the
Parsek command seam, which the game executes). HAND = clicks or keys in the game.

| step | DRIVEN or HAND | what to do | what proves it worked | minutes |
|---:|---|---|---|---:|
| 0 | DRIVEN | variables, preconditions | the checks of step 0 | 2 |
| 1 | DRIVEN | build the spike DLL, copy it into both instances, verify | four `RESULT: PASS` | 10 |
| 2 | DRIVEN | session A (no sets): launch, read the combo count, quit | `combos summary: reason=mainmenu ... parsekSets=0` | 6 |
| 3 | DRIVEN | install the two test sets into both instances | the checks of step 3 | 2 |
| 4 | DRIVEN | session B: launch with the sets | `parsekSets=4`, four stock `validated.` lines | 4 |
| 5 | DRIVEN | stage a boot copy of `spike-s0`, load it, make the sandbox `SPIKE-S3-LOOKS` | two `verdict=OK` | 4 |
| 6 | DRIVEN | dress phase 1 (`RunTests SpikeS3`) | six `dress:` lines `ok=true`, `saved: name=s3-phase1` | 4 |
| 7 | DRIVEN | load `s3-phase1` | `verdict=OK`, `pcm load:` lines with the suits | 3 |
| 8 | HAND + DRIVEN | Astronaut Complex screenshot; open the suit picker by hand | two screenshots | 5 |
| 9 | DRIVEN | VAB, Kerbal X, launch, flight screenshot | `verdict=OK pid=...`, one screenshot | 5 |
| 10 | HAND + DRIVEN | IVA (C), screenshot, back (C) | one screenshot | 2 |
| 11 | DRIVEN | roster in flight (`RunTests SpikeS3`) | `aboard=true` for three names | 4 |
| 12 | DRIVEN | EVA each of the three, screenshot, board | three `eva live:` lines, three screenshots | 8 |
| 13 | DRIVEN | map view screenshot (the background) | one screenshot | 3 |
| 14 | DRIVEN | ghost kerbals (`RunTests SpikeS3Ghost`) | `ghost summary: parts=8 ...`, two screenshots | 6 |
| 15 | DRIVEN | dress phase 2 in flight, load `s3-phase2`, EVA two kerbals in the sets | `eva live:` with `suitTexture=ParsekSpikeS3/...` | 12 |
| 16 | DRIVEN | quit session B, collect | files copied | 3 |
| 17 | DRIVEN | copy the save to the no-DLC instance | robocopy exit below 8 | 3 |
| 18 | DRIVEN | session C (no DLC): launch | `expansions: MakingHistory=false Serenity=false` | 4 |
| 19 | DRIVEN | load `s3-phase1` without the DLC | `pcm load:` lines, `nodeSuit=Vintage suit=Default` | 3 |
| 20 | DRIVEN | roster, Astronaut Complex screenshot | one screenshot | 5 |
| 21 | DRIVEN | launch, EVA the (saved) Vintage kerbal, ghost kerbals | one `eva live:` line, `ghost summary` | 10 |
| 22 | DRIVEN | quit session C (it saves), collect | files copied | 3 |
| 23 | DRIVEN | summarize | the report cells | 4 |
| 24 | DRIVEN | what to send back | one folder | 3 |
| | | **total** | | **118** |

HAND is 5 minutes (steps 8 and 10). The plan allows 3 hours on the PC for S3; booting KSP
three times is inside steps 2, 4 and 18. Every other step is a pasted command and a look
at its output.

## 0. Variables and preconditions

Same variables as `PC-SETUP.md`; replace the one placeholder.

```powershell
Set-Location "<UMBRELLA ROOT>"
$U  = (Get-Location).Path
$D  = Get-Date -Format yyyy-MM-dd
$W  = "$U\Parsek-spike-mp-r0"
$I  = "$U\automation\spike-r0"
$IN = "$U\automation\spike-r0-nodlc"
$C  = "$U\logs\console\$D"
$RS = "$U\logs\spikes\S3\$D"
New-Item -ItemType Directory -Force -Path $RS, $C, "$RS\shots" | Out-Null
Get-Process KSP_x64 -ErrorAction SilentlyContinue
Test-Path "$U\automation\.ksp-machine.lock"
Get-ChildItem Env:PARSEK_* -ErrorAction SilentlyContinue
Test-Path "$I\saves\spike-s0\persistent.sfs", "$IN\GameData\Parsek\Plugins\Parsek.dll"
Test-Path "$I\saves\SPIKE-S3-BOOT", "$I\saves\SPIKE-S3-LOOKS", "$IN\saves\SPIKE-S3-LOOKS"
Test-Path "$I\GameData\ParsekSpikeS3", "$IN\GameData\ParsekSpikeS3", "$I\parsek-spike\S3", "$IN\parsek-spike\S3"
```

Check: `Get-Process` and `Get-ChildItem` print nothing, the lock prints `False`; then
`True True`, `False False False`, `False False False False`. `spike-s0` is the career of
`PC-SETUP.md` step 9 and `spike-r0-nodlc` the copy of its step 8; if either is missing,
do that step first. A `True` in the last two lines means an earlier attempt: rename, never
delete (`Rename-Item "<path>" "<name>-old-$(Get-Date -Format HHmmss)"`; for
`GameData\ParsekSpikeS3` move it out of `GameData` instead:
`Move-Item "$I\GameData\ParsekSpikeS3" "$RS\old-ParsekSpikeS3"`).

## 1. Build and deploy the S3 DLL (no dev-install deploy)

`PC-SETUP.md` "Later spike builds", with `$Sx = "S3"`, into BOTH instances. The S3 code
must be in the branch: `git -C $W log --oneline -8` shows the S3 commits.

```powershell
$Sx = "S3"
git -C $W pull --ff-only
Set-Location $W
dotnet build Source\Parsek\Parsek.csproj -c Debug -p:SkipKspDeploy=true *> "$C\spike-$Sx-build.log"
$LASTEXITCODE
foreach ($inst in $I, $IN) {
  Copy-Item "$W\Source\Parsek\bin\Debug\Parsek.dll" "$inst\GameData\Parsek\Plugins\Parsek.dll"
  python "$W\spikes\r0\s0_check.py" dll "$inst\GameData\Parsek\Plugins\Parsek.dll" --utf8 SpikeS3Looks --utf8 SpikeS3Ghosts --utf8 SpikeS3Format | Tee-Object -Append "$RS\deployed-dll.txt"
  "{0}  {1}  {2}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath "$inst\GameData\Parsek\Plugins\Parsek.dll").Hash.ToLowerInvariant(), (git -C $W rev-parse HEAD), "$inst\GameData\Parsek\Plugins\Parsek.dll" | Add-Content "$RS\deployed-dll-sha256.txt"
}
Get-Content "$RS\deployed-dll-sha256.txt"
```

Check: exit `0`; `RESULT: PASS` twice per instance with the three class names found in
UTF-8; two hash lines with the same hash.

## 2. Session A: the baseline combo count (no sets installed yet)

The helpers below are used for the rest of the sheet. `$Inst` is the instance the
session runs; `Seam` appends one command to the seam and waits for its terminal answer
(it prints the response line, or `NO TERMINAL RESPONSE` after the wait).

```powershell
$Inst = $I
function ClearSeam { foreach ($f in "parsek-test-commands.txt", "parsek-test-responses.txt", "parsek-test-commands.journal", "parsek-test-commands.lock") { if (Test-Path -LiteralPath "$Inst\$f") { Remove-Item -LiteralPath "$Inst\$f" } } }
function Launch { $env:PARSEK_SPIKE = "S3"; $env:PARSEK_TEST_COMMANDS = "1"; $script:ksp = Start-Process -FilePath "$Inst\KSP_x64.exe" -WorkingDirectory $Inst -ArgumentList "-logFile `"$Inst\Player.log`"" -PassThru }
$script:sq = 0
function Seam([string]$cmd, [int]$wait = 300) {
  $script:sq++; $id = "s3-$(Get-Date -Format HHmmss)-$($script:sq)"
  Add-Content -LiteralPath "$Inst\parsek-test-commands.txt" -Value "id=$id cmd=$cmd"
  $end = (Get-Date).AddSeconds($wait)
  while ((Get-Date) -lt $end) {
    Start-Sleep -Seconds 2
    $hit = Select-String -LiteralPath "$Inst\parsek-test-responses.txt" -SimpleMatch -Pattern "id=$id " -ErrorAction SilentlyContinue |
      Where-Object { $_.Line -match ' verdict=(OK|ERROR|REJECTED|TIMEOUT)( |$)' } | Select-Object -Last 1
    if ($hit) { return $hit.Line }
  }
  "NO TERMINAL RESPONSE for $id ($cmd) after $wait s"
}
function S3Log([string]$p, [int]$n = 3) { Select-String -Path "$Inst\KSP.log" -SimpleMatch -Pattern "[Spike.S3] $p" | Select-Object -Last $n | ForEach-Object { $_.Line } }
function Shot([string]$label) { Seam "CaptureScreenshot label=$label" 60 }
ClearSeam; Launch
```

Wait for the main menu (about 2 minutes), then:

```powershell
S3Log "combos summary"
S3Log "expansions"
Seam "FlushAndQuit" 60
$ksp.WaitForExit(); Copy-Item "$I\KSP.log" "$RS\a-baseline-KSP.log"
```

Check: `combos summary: reason=mainmenu default=<n> stock=<n> extra=<n> parsekSets=0
otherExtra=<n> configs=<n>`; `expansions: MakingHistory=true Serenity=true
suitVintage=true suitFuture=true suitSlim=true`; `verdict=OK` and KSP closes. Write
down `extra=` (the baseline).

## 3. Install the two test sets (both instances)

A plain copy of a folder of 2 cfg files and 5 small PNGs into each instance's own
`GameData`, next to `Squad` (never inside it).

```powershell
foreach ($inst in $I, $IN) {
  Copy-Item -Recurse -LiteralPath "$W\spikes\r0\S3\gamedata\ParsekSpikeS3" -Destination "$inst\GameData\ParsekSpikeS3"
  (Get-ChildItem -Recurse -File "$inst\GameData\ParsekSpikeS3").Count
  (Get-Item -LiteralPath "$inst\GameData\ParsekSpikeS3").LinkType
  Test-Path "$inst\GameData\Squad\ParsekSpikeS3"
}
python "$W\spikes\r0\S3\make_suit_pngs.py" --check
```

Check: per instance `7`, an empty line (a real folder, not a link) and `False`; the PNG
check ends `RESULT: PASS`.

## 4. Session B: launch with the sets

```powershell
$Inst = $I; ClearSeam; Launch
```

At the main menu:

```powershell
S3Log "combos summary"
S3Log "combo: source=extra" 6
Select-String -Path "$I\KSP.log" -Pattern "Suit combo .*ParsekS3_.* validated\." | ForEach-Object { $_.Line }
```

Check: `parsekSets=4` and `extra=` the baseline plus 4; four `combo: source=extra ...
name=ParsekS3_...` lines whose `config=` names `ParsekSpikeS3/...`; four stock lines
`Suit combo Parsek S3 ...-like/ParsekS3_... validated.`

## 5. The sandbox save `SPIKE-S3-LOOKS`

A boot copy of `spike-s0` is loaded (so `spike-s0` itself is never rewritten), and the
seam's `NewGame ... as=` makes a fresh Normal sandbox in a new folder, the way the main
menu's New Game does. A sandbox, so the stock Kerbal X can be launched.

```powershell
robocopy "$I\saves\spike-s0" "$I\saves\SPIKE-S3-BOOT" /E /XJ /R:1 /W:1 /NP /NFL /NDL *> "$C\s3-robocopy-boot.txt"; "robocopy exit=$LASTEXITCODE"
Seam "LoadGame save=SPIKE-S3-BOOT name=persistent scene=spacecenter" 300
Seam "NewGame mode=SANDBOX preset=Normal save=SPIKE-S3-BOOT as=SPIKE-S3-LOOKS" 300
```

Check: robocopy exit `0` to `7`; two lines with `verdict=OK`. The Space Center is now
the new sandbox.

## 6. Dress phase 1

The dress test reads `parsek-spike\S3\dress.txt`. Phase 1 puts Vintage, Future and Slim
on the first three crew (the three seats of the Kerbal X), the base suit on the fourth,
and the two test sets on the first two applicants, then saves `s3-phase1`.

```powershell
New-Item -ItemType Directory -Force -Path "$I\parsek-spike\S3" | Out-Null
Set-Content -LiteralPath "$I\parsek-spike\S3\dress.txt" -Value "phase=1"
Seam "RunTests category=SpikeS3" 600
Select-String -Path "$I\KSP.log" -SimpleMatch -Pattern "BATCH_COMPLETE" | Select-Object -Last 1 | ForEach-Object { $_.Line }
S3Log "dress:" 6
S3Log "saved:" 1
Remove-Item -LiteralPath "$I\parsek-spike\S3\dress.txt"
```

Check: `verdict=OK`; `BATCH_COMPLETE v1 total=3 passed=3 failed=0 skipped=0
category=SpikeS3 scene=SPACECENTER`; six `dress:` lines, each `ok=true`, the crew ones
`suit=Vintage|Future|Slim|Default` and the applicant ones `suit=Default
comboId=ParsekS3_...`; `saved: name=s3-phase1 ...`. Write down the three crew names of
`index=0..2` (normally Jebediah, Bill and Bob Kerman). The control file is removed so no
later batch dresses again.

## 7. Load the dressed save

The batch put `persistent.sfs` back to its pre-batch bytes; `s3-phase1` holds the
dressed roster. Loading it also proves the round trip on the DLC instance.

```powershell
Seam "LoadGame save=SPIKE-S3-LOOKS name=s3-phase1 scene=spacecenter" 300
S3Log "pcm load:" 8
```

Check: `verdict=OK`; `pcm load:` lines with `nodeSuit=Vintage ... suit=Vintage`,
`nodeSuit=Future ... suit=Future`, `nodeSuit=Slim ... suit=Slim`.

## 8. HAND (3 minutes): the Astronaut Complex and the suit picker

```powershell
Seam "StockScreen screen=astronaut act=open" 60
Shot "s3-ac-phase1"
```

Now by hand, in the open Astronaut Complex: on the Applicants tab, click the suit button
of the applicant wearing the gold set (the kerbal named in the `applicant` `dress:` line
with `look=VintageLike`). The suit picker opens. Leave it open and:

```powershell
Shot "s3-picker"
```

Look at the picker: note in one line whether the two Parsek S3 combos (gold
`#C9A227`, green `#2E8B57` swatches) show, and under which suit tab. Close the picker by
hand (click outside it or its close button), then:

```powershell
Seam "StockScreen screen=astronaut act=close" 60
```

Check: three `verdict=OK` lines (the two shots and the close).

## 9. Launch the Kerbal X

```powershell
Seam "GoToEditor facility=VAB craft=Kerbal%20X" 180
$launch = Seam "LaunchFromEditor" 240; $launch
Start-Sleep -Seconds 10
Shot "s3-flight-phase1"
```

Check: `verdict=OK` for both; the launch line carries `pid=<n>`. The screenshot shows
the three crew portraits (bottom right).

## 10. HAND (2 minutes): IVA

Press `C` in the game (IVA view), wait 3 seconds, then:

```powershell
Shot "s3-iva-phase1"
```

Press `C` again to leave IVA. Check: `verdict=OK`.

## 11. The roster in flight

```powershell
Seam "RunTests category=SpikeS3" 600
Select-String -Path "$I\KSP.log" -SimpleMatch -Pattern "BATCH_COMPLETE" | Select-Object -Last 1 | ForEach-Object { $_.Line }
S3Log "aboard=true" 3
S3Log "color sample:" 4
```

Check: `verdict=OK`; `BATCH_COMPLETE v1 total=3 passed=2 failed=0 skipped=1
category=SpikeS3 scene=FLIGHT` (the dress cell skips: no control file); three roster
lines with `aboard=true` and the suits of step 6; `color sample:` lines. A flight batch
ends with a quickload of its starting state, so the scene reloads once; that is expected.

## 12. EVA each of the three (driven)

Use the three names of step 6 (spaces as `%20`). Each kerbal stays on the pod's ladder
(no `release=`), so nobody falls.

```powershell
foreach ($k in "Jebediah%20Kerman", "Bill%20Kerman", "Bob%20Kerman") {
  Seam "EvaExit kerbal=$k settleSeconds=3" 120
  Start-Sleep -Seconds 4
  Shot ("s3-eva-" + ($k -replace '%20.*','').ToLower() + "-phase1")
  Seam "EvaBoard" 120
}
S3Log "eva live:" 3
```

Check: per kerbal `verdict=OK` for EvaExit, the shot and EvaBoard; three `eva live:`
lines with `part=kerbalEVAVintage`, `kerbalEVAFuture`, `kerbalEVASlimSuit` (or their
female forms). If `EvaBoard` answers ERROR, press `B` by hand on the ladder and note it.

## 13. The map background

```powershell
Seam "EnterMapView" 120
Start-Sleep -Seconds 3
Shot "s3-map"
Seam "ExitMapView" 120
```

Check: three `verdict=OK`. The summarizer takes the median color of this image as the
map background.

## 14. Ghost kerbals

Builds each of the eight EVA parts 20 times through Parsek's ghost builder, times the
per-ghost suit swap, checks the prefab's shared materials, shows two rows of ghosts in
front of the camera and writes two screenshots itself (UI hidden for them).

```powershell
Seam "RunTests category=SpikeS3Ghost" 900
Select-String -Path "$I\KSP.log" -SimpleMatch -Pattern "BATCH_COMPLETE" | Select-Object -Last 1 | ForEach-Object { $_.Line }
S3Log "ghost summary:" 1
S3Log "ghost screenshot:" 2
```

Check: `verdict=OK`; `BATCH_COMPLETE v1 total=1 passed=1 failed=0 skipped=0
category=SpikeS3Ghost scene=FLIGHT`; `ghost summary: parts=8 found=8 fallbacks=0
sharedChanged=0 textureBad=0`; two `ghost screenshot: ... written=true`. A FAIL is a
result, not a stop: note it and go on.

## 15. Phase 2: the test sets on EVA

The dress runs in flight on the three seated kerbals (gold set, green set, base suit) and
saves `s3-phase2`; the batch then reloads its starting state, so the sheet loads
`s3-phase2`. The live recording Parsek started at launch is stopped and discarded first
(a load is refused while a recorder runs).

```powershell
Set-Content -LiteralPath "$I\parsek-spike\S3\dress.txt" -Value "phase=2"
Seam "RunTests category=SpikeS3" 600
S3Log "dress:" 3
S3Log "saved:" 1
Remove-Item -LiteralPath "$I\parsek-spike\S3\dress.txt"
Seam "StopRecording" 120
Seam "DiscardTree" 120
Seam "LoadGame save=SPIKE-S3-LOOKS name=s3-phase2" 300
Start-Sleep -Seconds 10
Shot "s3-flight-phase2"
foreach ($k in "Jebediah%20Kerman", "Bill%20Kerman") {
  Seam "EvaExit kerbal=$k settleSeconds=3" 120
  Start-Sleep -Seconds 4
  Shot ("s3-eva-" + ($k -replace '%20.*','').ToLower() + "-phase2")
  Seam "EvaBoard" 120
}
S3Log "eva live:" 2
```

Check: three `dress:` lines `ok=true` (`look=VintageLike`, `FutureLike`, `Default`);
`saved: name=s3-phase2`; `StopRecording` and `DiscardTree` `verdict=OK` (a REJECTED
saying nothing is recording is also fine); LoadGame `verdict=OK`; two `eva live:` lines
with `suitTexture=ParsekSpikeS3/Textures/vintage_like_suit` and `.../future_like_suit` and
`bodyTex=vintage_like_suit#...` / `future_like_suit#...`. The two EVA shots show a gold
and a green suit with a white square on the texture's top-left corner.

## 16. Quit session B and collect

```powershell
Seam "FlushAndQuit" 120
$ksp.WaitForExit()
Copy-Item "$I\KSP.log" "$RS\b-KSP.log"
Copy-Item "$I\parsek-test-responses.txt" "$RS\b-seam-responses.txt"
Copy-Item -Recurse -LiteralPath "$I\parsek-spike\S3" -Destination "$RS\b-parsek-spike-S3"
Copy-Item "$I\Screenshots\s3-*.png" "$RS\shots\"
Copy-Item "$I\parsek-spike\S3\shots\*.png" "$RS\shots\"
(Get-ChildItem "$RS\shots").Count
```

Check: KSP closes; the count is 14 (12 seam shots, 2 ghost shots).

## 17. Copy the save to the no-DLC instance

```powershell
robocopy "$I\saves\SPIKE-S3-LOOKS" "$IN\saves\SPIKE-S3-LOOKS" /E /XJ /R:1 /W:1 /NP /NFL /NDL *> "$C\s3-robocopy-nodlc.txt"; "robocopy exit=$LASTEXITCODE"
Test-Path "$IN\saves\SPIKE-S3-LOOKS\s3-phase1.sfs", "$IN\GameData\SquadExpansion", "$IN\parsek-spike\S3\dress.txt"
```

Check: robocopy exit `0` to `7`; `True False False`.

## 18. Session C: launch without the DLC

```powershell
$Inst = $IN; ClearSeam; Launch
```

At the main menu:

```powershell
S3Log "expansions" 1
S3Log "combos summary" 1
```

Check: `expansions: MakingHistory=false Serenity=false suitVintage=false
suitFuture=false suitSlim=true`; `combos summary: ... parsekSets=4`.

## 19. Load the save made with the DLC

```powershell
Seam "LoadGame save=SPIKE-S3-LOOKS name=s3-phase1 scene=spacecenter" 300
S3Log "pcm load:" 8
```

Check: `verdict=OK` (the save loads); `pcm load:` lines; the Vintage and Future kerbals
show `nodeSuit=Vintage|Future suit=Default comboId=<the stock combo> evaPart=kerbalEVA`
(or `kerbalEVAfemale`). That mapping is the "deterministic fallback suit" of the report.

## 20. Roster and Astronaut Complex without the DLC

```powershell
Seam "RunTests category=SpikeS3" 600
Seam "StockScreen screen=astronaut act=open" 60
Shot "s3-nodlc-ac"
Seam "StockScreen screen=astronaut act=close" 60
```

Check: `verdict=OK` four times; the batch skips the dress cell (no control file in this
instance).

## 21. Launch, EVA the saved-Vintage kerbal, ghost kerbals without the DLC

```powershell
Seam "GoToEditor facility=VAB craft=Kerbal%20X" 180
Seam "LaunchFromEditor" 240
Start-Sleep -Seconds 10
Seam "EvaExit kerbal=Jebediah%20Kerman settleSeconds=3" 120
Start-Sleep -Seconds 4
Shot "s3-nodlc-eva-jebediah"
Seam "EvaBoard" 120
S3Log "eva live:" 1
Seam "RunTests category=SpikeS3Ghost" 900
S3Log "ghost summary:" 1
```

Check: `eva live: ... part=kerbalEVA ... suit=Default`; `ghost summary: parts=8 found=4
fallbacks=4 ...` (the four Vintage / Future parts are missing here and fall back to the
magenta sphere). Other numbers are results; note them.

## 22. Quit session C (it saves) and collect

`FlushAndQuit` saves `persistent.sfs` of the no-DLC game, which is what step 23 reads to
see whether the DLC suits were written back as Default.

```powershell
Seam "FlushAndQuit" 120
$ksp.WaitForExit()
Remove-Item Env:PARSEK_SPIKE, Env:PARSEK_TEST_COMMANDS
Copy-Item "$IN\KSP.log" "$RS\c-nodlc-KSP.log"
Copy-Item "$IN\parsek-test-responses.txt" "$RS\c-seam-responses.txt"
Copy-Item "$IN\saves\SPIKE-S3-LOOKS\persistent.sfs" "$RS\c-nodlc-persistent.sfs"
Copy-Item "$IN\Screenshots\s3-*.png" "$RS\shots\"
Copy-Item "$IN\parsek-spike\S3\shots\s3-ghosts-front.png" "$RS\shots\s3-nodlc-ghosts-front.png"
Copy-Item "$IN\parsek-spike\S3\shots\s3-ghosts-back.png" "$RS\shots\s3-nodlc-ghosts-back.png"
(Get-ChildItem "$RS\shots").Count
```

Check: the count is 18.

## 23. Summarize

```powershell
$S3 = "$W\spikes\r0\S3\summarize_s3.py"
python $S3 summarize --log "$RS\b-KSP.log" --baseline-log "$RS\a-baseline-KSP.log" --nodlc-log "$RS\c-nodlc-KSP.log" --nodlc-save "$RS\c-nodlc-persistent.sfs" --map-png "$RS\shots\s3-map.png" --out "$RS\summary-s3.md" *> "$C\s3-summary.log"
Get-Content "$RS\summary-s3.md" | Select-String -Pattern "^[A-Z-]+:"
```

Check: one line each for `COMBOS`, `DRESS`, `LOOKS`, `NODLC`, `WRITEBACK`,
`GHOST-SHARED`, `GHOST-COST`, `GHOST-PARTS` (twice: the second for `[nodlc]`) and
`COLORS`, each PASS, FAIL, OWED or (WRITEBACK) `suit-kept` / `suit-rewritten`. They are
results; the report takes them as they are.

Optional (not counted): the plan picks the four colors from the screenshots. The
`COLORS` line uses colors sampled from the suit textures in game. To judge your own
picks instead, write `$RS\colors.csv` with the lines `look,hex`, `Default,#RRGGBB`,
`Slim,#...`, `Vintage,#...`, `Future,#...` and rerun the command with
`--colors-csv "$RS\colors.csv"` added.

## 24. What to send back

- `$RS\summary-s3.md` and the one line of `$RS\deployed-dll-sha256.txt`.
- `$RS\shots\` (18 screenshots of the spike saves only; about 20 MB).
- The picker note of step 8, and a note for any step that did not go as written.

The rest of `$RS` (`*-KSP.log`, the seam responses, the CSVs under
`b-parsek-spike-S3`, `c-nodlc-persistent.sfs`) stays on the PC; the report lists it as
raw artifacts. The test sets stay installed in both spike instances (they only add four
suit combos); to remove them later, move the folder out of `GameData`
(`Move-Item "$I\GameData\ParsekSpikeS3" "$RS\removed-ParsekSpikeS3"`).

## Stock members this spike touches (all checked against KSP 1.12.5 `Assembly-CSharp.dll`)

Patched by hand (one target): a postfix on the constructor
`ProtoCrewMember(Game.Modes, ConfigNode, KerbalType)`, bound by the parameter name `node`.

Read: `SuitCombos` (component on `GameDatabase.Instance`; `DefaultCombos`, `StockCombos`,
`ExtraCombos`, `GetCombo(string)`); `SuitCombo` fields `name`, `displayName`,
`suitType`, `gender`, `suitTexture`, `normalTexture`, `sprite`, `primaryColor`,
`secondaryColor`, `defaultSuitTexture`; `GameDatabase.GetConfigs("SUITCOMBOS")`,
`GetTexture(string, bool)`; `UrlDir.UrlConfig.parent.url`;
`Expansions.ExpansionsLoader.IsExpansionInstalled(string)`,
`IsExpansionKerbalSuitInstalled(KerbalSuit)`; `ProtoCrewMember` `suit`, `gender`,
`ComboId`, `SuitTexturePath`, `NormalTexturePath`, `SpritePath`, `veteran`, `type`,
`rosterStatus`, `GetKerbalEVAPartName()`, `GetKerbalIVAName()`; `KerbalRoster.Crew`,
`Applicants`, `GetNewKerbal(KerbalType)`; `PartLoader.getPartInfoByName`;
`AssetBase.GetPrefab(string)`; `KerbalEVA.helmetMesh`, `bodyMesh`, `neckringMesh`;
`AvailablePart.partUrl`, `configFileFullName`, `partPrefab`; `FlightGlobals.VesselsLoaded`,
`ActiveVessel.GetVesselCrew()`; `FlightCamera.fetch.mainCamera`.

Written: `ProtoCrewMember.suit`, `SuitTexturePath`, `NormalTexturePath`, `SpritePath`,
`ComboId` (the stock suit picker's recipe, `HelmetSuitPickerWindow.SetDefaultCombo` /
`SetExtraCombo`); `GamePersistence.SaveGame(string, string, SaveMode)`; on the ghost
copies only, `Material.mainTexture`, `mainTextureScale`, `SetTexture("_BumpMap")`,
`SetTextureScale`. `GameEvents.onHideUI` / `onShowUI` around the two ghost screenshots.

How checked: the spike compiles against the reference assemblies; xUnit
`SpikeS3Tests` reads the string literals out of the IL of
`ProtoCrewMember.GetKerbalEVAPartName`, `GetKerbalIVAName`, `Save(ConfigNode)` and
`SuitCombos.GetSuitCombos` and compares them with the spike's tables, resolves the
patched constructor and its `node` parameter, and checks the field and property shapes
used above.
