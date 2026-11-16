# S0 on the owner's PC: spike DLL, three instances, three proof sessions

This is the step list for the PC half of task S0 in
`docs/dev/plans/multiplayer/r0-spikes.md` ("Instances", "Procedure", "Proof"). Run it top
to bottom, once, in ONE PowerShell 7 window (`pwsh`, not Windows PowerShell 5.1): later
steps reuse the variables set in step 0. Every step ends with a check; when a check
fails, stop there and report the step number and its output. Nothing here flies a
harness lane or touches the dev install except to read it.

Time: about 1 hour, most of it the two provisioning runs and the copy.

## Forbidden commands (read this before step 8)

The spike instances hold DIRECTORY JUNCTIONS: `GameData\Squad` and
`GameData\SquadExpansion` inside `automation\spike-r0` (and `-content`) are links to the
dev install's own `Kerbal Space Program\GameData\Squad` and `...\SquadExpansion`. A
delete that follows a junction deletes the DEV INSTALL's game and DLC files. So, on any
folder under `automation\spike-r0*`, and on anything in the dev install:

- never `Remove-Item` (with or without `-Recurse`, with or without `-Force`) on a
  junction or on a folder that contains one (Windows PowerShell 5.1 can delete through
  a link even without `-Recurse` once you confirm its prompt);
- never `rmdir /S` / `rd /S` (cmd) on a junction or on a folder that contains one;
- never `del /S`, never Explorer Delete or Shift+Delete on a junction or its parent;
- never `robocopy /MIR` or `/PURGE` into a folder that contains a junction;
- never copy an instance with Explorer, `Copy-Item -Recurse` or `xcopy`: they follow
  the junctions and turn them into real copies of the DLC (step 8 uses `robocopy /XJ`).

The ONE safe way to remove a junction is cmd's `rmdir` WITHOUT `/S`, on the link
itself, after confirming it is a link:

```powershell
(Get-Item -LiteralPath "<path>" -Force).LinkType     # must print: Junction
cmd /c rmdir "<path>"
```

`rmdir` without `/S` removes a junction and never its target; on a real folder that has
content it refuses ("The directory is not empty.") and deletes nothing. To delete a
whole spike instance later, use "Starting over" at the end of this file.

## 0. Variables and preconditions

Replace the one placeholder: the umbrella folder that holds `Parsek\`,
`Kerbal Space Program\`, `automation\` and `logs\`.

```powershell
Set-Location "<UMBRELLA ROOT>"
$U   = (Get-Location).Path
$D   = Get-Date -Format yyyy-MM-dd
$W   = "$U\Parsek-spike-mp-r0"
$DEV = "$U\Kerbal Space Program"
$I   = "$U\automation\spike-r0"
$IC  = "$U\automation\spike-r0-content"
$IN  = "$U\automation\spike-r0-nodlc"
$R   = "$U\logs\spikes\S0\$D"
$C   = "$U\logs\console\$D"
New-Item -ItemType Directory -Force -Path $R, $C | Out-Null
$PSVersionTable.PSVersion.Major
python -c "import sys; print(sys.version_info[:2] >= (3, 11))"
Test-Path "$DEV\KSP_x64.exe", "$DEV\GameData\Squad", "$DEV\GameData\SquadExpansion", "$U\Parsek\.git"
(Get-Item -LiteralPath "$DEV\GameData\SquadExpansion" -Force).LinkType
Test-Path $W, $I, $IC, $IN
Get-Process KSP_x64 -ErrorAction SilentlyContinue
Test-Path "$U\automation\.ksp-machine.lock"
Get-ChildItem Env:PARSEK_* -ErrorAction SilentlyContinue
```

Check:
- PowerShell major version `7` or more; Python line prints `True` (the provisioner needs
  3.11+ for `tomllib`).
- The first `Test-Path` line prints `True` four times.
- The `LinkType` line prints an EMPTY line (the dev install's `SquadExpansion` is a real
  folder; if it prints `Junction`, stop: the plan assumes a real DLC folder there).
- The second `Test-Path` line prints `False` four times. A `True` means a previous
  attempt left a folder: stop and use "Starting over" for it first.
- `Get-Process` prints nothing (no KSP running), the lock `Test-Path` prints `False` (no
  harness run or provisioning holds the machine), and `Get-ChildItem Env:PARSEK_*`
  prints nothing.

## 1. Worktree of the spike branch

Only git runs here; the instances do not exist yet.

```powershell
git -C "$U\Parsek" fetch origin spike/mp-r0
git -C "$U\Parsek" worktree add "$W" -b spike/mp-r0 origin/spike/mp-r0
git -C "$W" rev-parse HEAD | Tee-Object "$R\spike-commit.txt"
git -C "$W" status --short
python "$W\spikes\r0\s0_check.py" --selftest
```

Check: the printed sha is the one the supervisor reported for `spike/mp-r0`; `status`
prints nothing; the self test ends `SELFTEST PASS`. (If `worktree add` says the branch
already exists, run `git -C "$U\Parsek" worktree add "$W" spike/mp-r0` instead and check
the sha the same way.)

## 2. Snapshot the dev install's stock folders (the baseline for steps 8 and 12)

`s0_check.py` only reads, and never walks into a link.

```powershell
python "$W\spikes\r0\s0_check.py" tree "$DEV\GameData\SquadExpansion" | Set-Content "$R\dev-squadexpansion-before.txt"
python "$W\spikes\r0\s0_check.py" tree "$DEV\GameData\Squad"          | Set-Content "$R\dev-squad-before.txt"
Get-Content "$R\dev-squadexpansion-before.txt", "$R\dev-squad-before.txt"
```

Check: two lines `path=... files=N bytes=M links=0` with `N` and `M` above 0.

## 3. Build the spike DLL WITHOUT deploying it to the dev install

Built from the worktree ROOT with `SkipKspDeploy`: a build started inside
`Source\Parsek` would copy the spike DLL into the dev install.

```powershell
Set-Location $W
dotnet build Source\Parsek\Parsek.csproj -c Debug -p:SkipKspDeploy=true *> "$C\spike-s0-build.log"
$LASTEXITCODE
Select-String -Path "$C\spike-s0-build.log" -SimpleMatch -Pattern "KSP deploy skipped", "Build succeeded", " 0 Error(s)"
python "$W\spikes\r0\s0_check.py" dll "$W\Source\Parsek\bin\Debug\Parsek.dll" | Tee-Object "$R\build-dll.txt"
python "$W\spikes\r0\s0_check.py" dll "$DEV\GameData\Parsek\Plugins\Parsek.dll" --expect-absent | Tee-Object "$R\dev-dll-before.txt"
```

Check: exit code `0`; all three build lines found; the first `dll` check prints
`PARSEK-SPIKE-R0 utf8=0 utf16=` with a number above 0, `SpikeHost utf8=` above 0 and
`RESULT: PASS`; the dev install's DLL check prints `RESULT: PASS` (the marker is absent
there).

## 4. Provision `spike-r0` (copy of stock-minimal, owner's graphics)

```powershell
Set-Location "$W\harness"
python provision\provision.py --profile spike-r0 --parsek-dll "$W\Source\Parsek\bin\Debug\Parsek.dll" *> "$C\spike-r0-provision.log"
$LASTEXITCODE
Select-String -Path "$C\spike-r0-provision.log" -SimpleMatch -Pattern "slot=spike-r0 copy=1 krpc-ports=50060/50061", "(override)", "installed-sha256=", "junctioned GameData/Squad", "junctioned GameData/SquadExpansion", "instance=spike-r0 slot=spike-r0 provisioned exit=0"
```

Check: exit code `0` and all six patterns found (`installed-sha256=... hashMatch OK`).
On a second run the two `junctioned` lines may be replaced by `SF9 skip mutable-surface
copy + junctions`; that is fine. If it aborts while fetching or building kRPC /
TestingTools, read the `ABORT:` line: the usual cure is
`python provision\provision.py --seed-cache-from "$U\Parsek\harness\provision\.cache"`
(no network; any worktree that provisioned before has that folder), or adding
`--krpc-src <an existing kRPC clone>` to the provision command, then rerun this step.

## 5. Provision `spike-r0-content` (copy of stock-tree, for S6 step (f) only)

```powershell
python provision\provision.py --profile spike-r0-content --parsek-dll "$W\Source\Parsek\bin\Debug\Parsek.dll" *> "$C\spike-r0-content-provision.log"
$LASTEXITCODE
Select-String -Path "$C\spike-r0-content-provision.log" -SimpleMatch -Pattern "slot=spike-r0-content copy=1 krpc-ports=50080/50081", "installed-sha256=", "instance=spike-r0-content slot=spike-r0-content provisioned exit=0"
Test-Path "$IC\GameData\CommunityTechTree", "$IC\GameData\ProbesBeforeCrew"
```

Check: exit code `0`, the three patterns found, and `False False` (no CommunityTechTree,
no ProbesBeforeCrew in the content instance).

## 6. Verify the deployed DLLs in both encodings, and the junctions

```powershell
Set-Location $U
python "$W\spikes\r0\s0_check.py" dll "$I\GameData\Parsek\Plugins\Parsek.dll"  | Tee-Object "$R\spike-r0-dll.txt"
python "$W\spikes\r0\s0_check.py" dll "$IC\GameData\Parsek\Plugins\Parsek.dll" | Tee-Object "$R\spike-r0-content-dll.txt"
Get-Item "$W\Source\Parsek\bin\Debug\Parsek.dll", "$I\GameData\Parsek\Plugins\Parsek.dll", "$IC\GameData\Parsek\Plugins\Parsek.dll" | Select-Object FullName, Length, LastWriteTime
python "$W\spikes\r0\s0_check.py" links $I  --expect GameData/Squad GameData/SquadExpansion | Tee-Object "$R\spike-r0-links.txt"
python "$W\spikes\r0\s0_check.py" links $IC --expect GameData/Squad GameData/SquadExpansion | Tee-Object "$R\spike-r0-content-links.txt"
```

Check: both `dll` checks print `RESULT: PASS` with `PARSEK-SPIKE-R0 utf16=` above 0 and
`SpikeHost utf8=` above 0, and the same `sha256=` as `build-dll.txt` from step 3 and as
the `installed-sha256=` of both provision logs; the three files have the same `Length`.
Both `links` checks print `RESULT: PASS`, and each `link` line points into
`$DEV\GameData\Squad` and `$DEV\GameData\SquadExpansion`.

## 7. Pin the deployed SHA-256

```powershell
foreach ($p in "$I\GameData\Parsek\Plugins\Parsek.dll", "$IC\GameData\Parsek\Plugins\Parsek.dll") {
  "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash.ToLowerInvariant(), $p |
    Add-Content "$R\deployed-dll-sha256.txt"
}
Get-Content "$R\deployed-dll-sha256.txt"
```

Check: two lines with the same hash, equal to step 6's `sha256=`. Every spike report
quotes this hash (and step 1's commit). Step 8 adds the no-DLC copy's line.

## 8. The no-DLC copy `spike-r0-nodlc` (for S3)

Safe by construction: `robocopy /XJ` copies the instance WITHOUT its junctions, so the
copy never holds a `SquadExpansion` entry and nothing has to be deleted; then the one
junction the copy needs (`Squad`) is created fresh with `mklink /J`, the same command the
provisioner uses. Do not run this while KSP runs from `spike-r0`.

```powershell
python "$W\spikes\r0\s0_check.py" tree $I
Get-PSDrive -Name (Split-Path $U -Qualifier).TrimEnd(':') | Select-Object Free
robocopy $I $IN /E /XJ /R:1 /W:1 /NP /NFL /NDL *> "$R\robocopy-nodlc.txt"
"robocopy exit=$LASTEXITCODE"
```

Check: `Free` is well above the `bytes=` the `tree` line printed; robocopy exit code
`0` to `7` (8 or more is a failure: read `$R\robocopy-nodlc.txt`).

```powershell
python "$W\spikes\r0\s0_check.py" links $IN --expect-none
python "$W\spikes\r0\s0_check.py" tree $IN
Test-Path "$IN\GameData\Squad", "$IN\GameData\SquadExpansion"
```

Check: `RESULT: PASS` (no link was copied); the `tree` line's `files=` equals the
`files=` of `spike-r0` above (links=0 here, 2 there); `False False`. If either path
exists, stop: something other than `robocopy /XJ` made the copy; go to "Starting over".

```powershell
cmd /c mklink /J "$IN\GameData\Squad" "$DEV\GameData\Squad"
```

Check: it prints `Junction created for ...\spike-r0-nodlc\GameData\Squad <<===>>
...\Kerbal Space Program\GameData\Squad`.

The three checks (run all three; all must hold):

```powershell
# 1. In the copy, Squad is a junction and SquadExpansion is not listed.
cmd /c dir /AL "$IN\GameData"
python "$W\spikes\r0\s0_check.py" links $IN --expect GameData/Squad | Tee-Object "$R\nodlc-links.txt"
# 2. SquadExpansion is gone from the copy.
Test-Path "$IN\GameData\SquadExpansion"
# 3. The dev install's DLC is still there, unchanged (no output = unchanged).
python "$W\spikes\r0\s0_check.py" tree "$DEV\GameData\SquadExpansion" | Set-Content "$R\dev-squadexpansion-step8.txt"
Compare-Object (Get-Content "$R\dev-squadexpansion-before.txt") (Get-Content "$R\dev-squadexpansion-step8.txt")
Get-Content "$R\dev-squadexpansion-step8.txt"
```

Check:
1. `dir /AL` lists exactly one entry, `<JUNCTION>     Squad [<dev>\GameData\Squad]`, and
   no `SquadExpansion` line; the `links` check prints `RESULT: PASS`.
2. `False`.
3. `Compare-Object` prints nothing and the last line shows the same `files=` and
   `bytes=` as step 2.

Should check 1 or 2 ever show a `SquadExpansion` JUNCTION in the copy, remove only that
link with the safe command at the top (`LinkType` must print `Junction`, then
`cmd /c rmdir "$IN\GameData\SquadExpansion"`, no `/S`) and run the three checks again.

Then pin the copy's DLL:

```powershell
python "$W\spikes\r0\s0_check.py" dll "$IN\GameData\Parsek\Plugins\Parsek.dll" | Tee-Object "$R\spike-r0-nodlc-dll.txt"
"{0}  {1}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath "$IN\GameData\Parsek\Plugins\Parsek.dll").Hash.ToLowerInvariant(), "$IN\GameData\Parsek\Plugins\Parsek.dll" |
  Add-Content "$R\deployed-dll-sha256.txt"
Get-Content "$R\deployed-dll-sha256.txt"
```

Check: `RESULT: PASS`; three lines, one hash.

Notes on the copy: it keeps `spike-r0`'s kRPC ports 50060 / 50061 (never run kRPC in
both at once) and its `provision-manifest.json` still names the `SquadExpansion`
junction; nothing reads that manifest for this folder, and it must never be passed to
`provision.py` or `run.py`.

## 9. Proof session 1: `PARSEK_SPIKE` unset, a career save loads (`spike-r0`)

```powershell
Remove-Item Env:PARSEK_SPIKE -ErrorAction SilentlyContinue
[Environment]::GetEnvironmentVariable('PARSEK_SPIKE', 'User')
[Environment]::GetEnvironmentVariable('PARSEK_SPIKE', 'Machine')
Get-ChildItem Env:PARSEK_* -ErrorAction SilentlyContinue
Start-Process -FilePath "$I\KSP_x64.exe" -WorkingDirectory $I -ArgumentList "-logFile `"$I\Player.log`"" -Wait
```

Check before launch: the three lines above `Start-Process` print nothing.

In the game: Start Game, Start New, Career, name `spike-s0`, start, and wait for the Space
Center. Escape, Quit to Main Menu (save). Start Game, Resume Saved, `spike-s0`, Load, wait
for the Space Center. Escape, Quit to Main Menu, then Quit. The prompt returns when KSP
exits.

```powershell
Copy-Item "$I\KSP.log" "$R\session1-unarmed-KSP.log"
Select-String -Path "$R\session1-unarmed-KSP.log" -SimpleMatch -Pattern "[Spike"
python "$W\spikes\r0\s0_check.py" log "$R\session1-unarmed-KSP.log" --session unarmed
python "$W\spikes\r0\s0_check.py" log "$R\session1-unarmed-KSP.log" --session dlc
```

Check: `Select-String` prints NOTHING (no `[Spike` line at all); both checks print
`RESULT: PASS`. The `unarmed` check also shows one `[Parsek][INFO][Init] SessionStart`
line and a `Scene Change : From MAINMENU to SPACECENTER` line; the `dlc` check shows the
stock lines `Expansion <name> detected in path <dir>` (one per DLC: this instance has
both).

## 10. Proof session 2: `PARSEK_SPIKE=S0` logs the armed line (`spike-r0`)

```powershell
$env:PARSEK_SPIKE = "S0"
Start-Process -FilePath "$I\KSP_x64.exe" -WorkingDirectory $I -ArgumentList "-logFile `"$I\Player.log`"" -Wait
Remove-Item Env:PARSEK_SPIKE
```

In the game: Start Game, Resume Saved, `spike-s0`, Load, wait for the Space Center,
Escape, Quit to Main Menu, Quit.

```powershell
Copy-Item "$I\KSP.log" "$R\session2-armed-KSP.log"
Select-String -Path "$R\session2-armed-KSP.log" -SimpleMatch -Pattern "[Spike"
python "$W\spikes\r0\s0_check.py" log "$R\session2-armed-KSP.log" --session armed
```

Check: `Select-String` prints exactly these three Parsek lines (each after KSP's own
`[LOG hh:mm:ss.fff]: ` prefix):

```
[Parsek][INFO][Spike] armed: S0 build=PARSEK-SPIKE-R0
[Parsek][INFO][Spike] spike S0: no patches to apply
[Parsek][INFO][Spike] patch summary: applied=0 failed=0 noPatches=1 root=<...>\automation\spike-r0\parsek-spike
```

and the check prints `RESULT: PASS`. Any `[Parsek][WARN][Spike]` line is a failure.

## 11. Proof session 3: the no-DLC copy sees no expansions (`spike-r0-nodlc`)

```powershell
Get-ChildItem Env:PARSEK_* -ErrorAction SilentlyContinue
Start-Process -FilePath "$IN\KSP_x64.exe" -WorkingDirectory $IN -ArgumentList "-logFile `"$IN\Player.log`"" -Wait
```

Check before launch: `Get-ChildItem` prints nothing. In the game: wait for the main menu,
then Quit.

```powershell
Copy-Item "$IN\KSP.log" "$R\session3-nodlc-KSP.log"
Select-String -Path "$R\session3-nodlc-KSP.log" -SimpleMatch -Pattern "No Expansions detected", " detected in path ", "[Spike"
python "$W\spikes\r0\s0_check.py" log "$R\session3-nodlc-KSP.log" --session nodlc
```

Check: `Select-String` prints the one stock line `No Expansions detected` and nothing
else (no `Expansion ... detected in path`, no `[Spike`); the check prints
`RESULT: PASS`. This is how KSP 1.12.5 reports Making History and Breaking Ground
(Serenity) as not installed: `ExpansionsLoader` logs `No Expansions detected` when
`GameData\SquadExpansion` is absent (decompiled `Expansions.ExpansionsLoader`).

## 12. Close-out checks and what to hand back

```powershell
python "$W\spikes\r0\s0_check.py" dll "$DEV\GameData\Parsek\Plugins\Parsek.dll" --expect-absent | Tee-Object "$R\dev-dll-after.txt"
python "$W\spikes\r0\s0_check.py" tree "$DEV\GameData\SquadExpansion" | Set-Content "$R\dev-squadexpansion-after.txt"
python "$W\spikes\r0\s0_check.py" tree "$DEV\GameData\Squad"          | Set-Content "$R\dev-squad-after.txt"
Compare-Object (Get-Content "$R\dev-squadexpansion-before.txt") (Get-Content "$R\dev-squadexpansion-after.txt")
Compare-Object (Get-Content "$R\dev-squad-before.txt") (Get-Content "$R\dev-squad-after.txt")
Get-ChildItem $R | Select-Object Name, Length
```

Check: `RESULT: PASS` (the dev install never got the spike DLL); both `Compare-Object`
lines print nothing (the dev install's `Squad` and `SquadExpansion` are unchanged). Hand back `$R` (`<umbrella>\logs\spikes\S0\<date>\`) and the console
logs in `$C`: the commit in `spike-commit.txt`, the hash in `deployed-dll-sha256.txt`, the
three session logs and the check outputs are the S0 evidence.

## Later spike builds (the "Procedure" bullet, every spike)

After each new commit on `spike/mp-r0`, with no KSP running and `$Sx` set to the spike
(`$Sx = "S4a"`):

```powershell
git -C $W pull --ff-only
Set-Location $W
dotnet build Source\Parsek\Parsek.csproj -c Debug -p:SkipKspDeploy=true *> "$C\spike-$Sx-build.log"
$LASTEXITCODE
$RS = "$U\logs\spikes\$Sx\$D"; New-Item -ItemType Directory -Force -Path $RS | Out-Null
Copy-Item "$W\Source\Parsek\bin\Debug\Parsek.dll" "$I\GameData\Parsek\Plugins\Parsek.dll"
python "$W\spikes\r0\s0_check.py" dll "$I\GameData\Parsek\Plugins\Parsek.dll" | Tee-Object "$RS\deployed-dll.txt"
"{0}  {1}  {2}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath "$I\GameData\Parsek\Plugins\Parsek.dll").Hash.ToLowerInvariant(), (git -C $W rev-parse HEAD), "$I\GameData\Parsek\Plugins\Parsek.dll" | Set-Content "$RS\deployed-dll-sha256.txt"
```

Then the spike's own class name in UTF-8 (one `--utf8` per class the spike adds):

```powershell
python "$W\spikes\r0\s0_check.py" dll "$I\GameData\Parsek\Plugins\Parsek.dll" --utf8 SpikeS4EventOrder
```

Check: exit `0` from the build and `RESULT: PASS` from both checks (the second fails with
`SpikeS4EventOrder missing in UTF-8` when the copied DLL predates the spike's commit).
Copy into `$IN` or `$IC` the same way when the spike runs there. A hand copy makes
the instance's provision manifest stale: before a harness lane runs on the instance (the
S6 lane), re-provision it with step 4's command instead of copying.

## Starting over (removing a spike instance)

Never with a recursive delete first. For `<dir>` one of `$IN`, `$I`, `$IC`, with no KSP
running from it:

```powershell
python "$W\spikes\r0\s0_check.py" links "<dir>"
```

For EACH `link <rel> -> <target>` line it prints:

```powershell
(Get-Item -LiteralPath "<dir>\<rel>" -Force).LinkType    # must print: Junction
cmd /c rmdir "<dir>\<rel>"                               # no /S
```

Then:

```powershell
python "$W\spikes\r0\s0_check.py" links "<dir>" --expect-none
```

Only when that prints `RESULT: PASS` (no link left inside), delete the folder:

```powershell
cmd /c rmdir /S /Q "<dir>"
```

and re-run step 12's two `tree` lines and `Compare-Object` lines (both must print
nothing).
