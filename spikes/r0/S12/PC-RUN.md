# S12 on the owner's PC: hashing and signing inside KSP's Mono

This is the step list for the PC half of task S12 in
`docs/dev/plans/multiplayer/r0-spikes.md` ("S12 Hashing and signing inside KSP's Mono"). It
assumes S0's `PC-SETUP.md` ran to the end: the worktree `$W` of `spike/mp-r0`, the instance
`spike-r0` and its career save `spike-s0` (PC-SETUP step 9) exist. Run it top to bottom in
ONE PowerShell 7 window (`pwsh`): later steps reuse the variables of step 0. Every step ends
with a check; when a check fails, stop there and report the step number and its output. Raw
output goes to `<umbrella>\logs\spikes\S12\<date>\` (`$R`), console captures to
`<umbrella>\logs\console\<date>\` (`$C`).

## Which PC session this rides in

Session **C4** of the plan (section 5, day 4): "S10b with S12, S11", on `spike-r0`. S12 goes
AFTER S10b's runs and BEFORE S11 (`spikes/r0/S11/PC-RUN.md`), because S11's step 2
re-provisions `spike-r0` and its lane runs need the PC otherwise idle. S12 needs no save
setup, no sync client and no second KSP; close every other KSP first.

| Part | Steps | Kind | Time |
|------|-------|------|------|
| A. Build and copy the DLL into `spike-r0` | 0-1 | DRIVEN (you paste the blocks) | 10 min |
| B. The probe in the game | 2 | HAND (mouse and keyboard in KSP) | 5 min |
| C. Collect and check | 3 | DRIVEN | 5 min |
| D. Close-out | 4 | DRIVEN | 2 min |

Time: about 20 minutes (the plan's "10 minutes on the PC" is the probe itself; the build and
copy add the rest, and step 1 is skipped in practice only if S10b's session already copied a
build of the same commit: its check says so). Your hands are needed for about 10 of them:
pasting the blocks (about 5 minutes) and part B (5 minutes).

DRIVEN means the code does the step and a log line proves it; HAND means you do it with the
mouse and keyboard and the log line proves what happened. Only part B is HAND.

## What the probe does

`SpikeS12.CryptoProbe` runs 32 steps on the main thread, one frame apart, each timed with a
`Stopwatch` and each written as one `[Parsek][INFO][Spike.S12] step ...` line in `KSP.log`
and one row of `<instance>\parsek-spike\S12\probe.csv`:

1. `env`: the runtime (Mono version string), and whether `HashAlgorithmName`,
   `RSASignaturePadding`, `ECDsa`, `ECCurve`, `ECParameters`, `ECDsaCng` exist in KSP's
   class libraries;
2. SHA-256: the FIPS "abc" vector, a 1 KB blob, 1 MB and 10 MB (each once cold, then three
   warm repeats), and `SHA256Managed` on 10 MB, every digest checked against a pinned value;
3. RSA 2048: create, sign the 1 KB blob (new API and the string-named API KSP itself uses),
   verify, refuse a tampered blob, export / import as XML and as parameters, and write the
   PUBLIC key to `parsek-spike\S12\rsa-public-<run>.xml` and read it back;
4. the key container: a named key container `parsek-spike-s12-<run>` is persisted, reopened
   by name, then deleted, with a before / after listing of the runtime's default key stores;
5. ECDsa P-256: create (named curve, else the default), sign, verify, refuse tampered,
   export / import as parameters, and an XML export attempt.

A step that throws is logged `status=fail error=<exception type> message="..."` and the
probe goes on. Then one `[Spike.S12] verdict ... result=PASS|FAIL` line (and
`verdict.csv`): PASS when SHA-256 over 10 MB (warm median) is under 100 ms with the right
digest AND at least one of RSA / ECDsa completes create, sign, verify, tamper refusal and a
key round trip. A FAIL verdict is a valid answer for the spike (it decides R7's fallback):
the run counts as long as all 32 step lines and the verdict line are there.

## What you will see in the game

- KSP at the Space Center of `spike-s0`; the Parsek test runner window after Ctrl+Shift+T.
- During the run the game FREEZES once or twice for a few seconds (up to about 30 s on a
  slow Mono): that is RSA 2048 key generation (`rsa.create`, `container.persist`), on the
  main thread by design. Do not click, do not press keys, do not close KSP until the test row
  shows its result.
- When it finishes, the `CryptoProbe` row is green (passed: verdict PASS) or red (failed:
  verdict FAIL, with the reason under the row). Either is a result; note which.

## What the probe leaves behind (and what it does not)

- In the instance, under `parsek-spike\S12\`: `probe.csv`, `verdict.csv` and
  `rsa-public-<run>.xml` (a throwaway public key). Step 3 moves this folder out.
- The key container is written to the runtime's OWN default key store and deleted again by
  the probe. On KSP's Mono that store is `%APPDATA%\.mono\keypairs\`; on a .NET Framework
  runtime it would be `%APPDATA%\Microsoft\Crypto\RSA\`. The `container.leftover` step line
  says `nothing left` when the store is back to its before-listing. If Mono had to CREATE the
  folder `%APPDATA%\.mono\keypairs` (it is new on this PC unless another Mono program used
  it), the empty folder stays and the line names it under `dirsCreated=`: it is Mono's own
  default location, harmless, and you do NOT need to remove it. If the line says
  `LEFT BEHIND`, it names one small XML file (a throwaway key pair, under 2 KB) in that
  default store: it is harmless too, nothing reads it, and you do not need to remove it;
  report the line.
- Nothing else: no file in the dev install, the umbrella root or the repo, no registry key,
  no certificate store entry. `KSP.log`, `Player.log` and `parsek-test-results.txt` are
  overwritten by the next start; `spike-s0`'s `persistent.sfs` is saved again by the quit to
  the main menu (the spike instance's own throwaway career).

## Forbidden commands (read this before step 1)

`GameData\Squad` and `GameData\SquadExpansion` in `spike-r0` are JUNCTIONS into the dev
install. Everything in `PC-SETUP.md` -> "Forbidden commands" holds here: no `Remove-Item`,
`rmdir /S`, `rd /S`, `del /S`, Explorer delete, `robocopy /MIR` or `/PURGE` on any folder
under `automation\spike-r0*` that is or holds a junction, and no `Copy-Item -Recurse` /
`xcopy` / Explorer copy of an instance. This file deletes NOTHING, inside an instance or
outside it. It copies one FILE in (the DLL, step 1) and MOVES one real folder out,
`parsek-spike\S12` (the spike's own output):

- before the `Move-Item` of a folder, run
  `(Get-Item -LiteralPath "<path>" -Force).LinkType` and go on only when it prints an
  EMPTY line (a real folder); if it prints `Junction`, stop;
- `Move-Item` within the umbrella's drive is a rename: it never follows a link and never
  copies.

## Part A. Build and copy, DRIVEN

## 0. Variables and preconditions (3 min)

KSP closed (S10b done), no other KSP running.

```powershell
Set-Location "<UMBRELLA ROOT>"
$U   = (Get-Location).Path
$D   = Get-Date -Format yyyy-MM-dd
$W   = "$U\Parsek-spike-mp-r0"
$DEV = "$U\Kerbal Space Program"
$I   = "$U\automation\spike-r0"
$R   = "$U\logs\spikes\S12\$D"
$C   = "$U\logs\console\$D"
$K   = "$W\spikes\r0\s0_check.py"
New-Item -ItemType Directory -Force -Path $R, $C | Out-Null
Test-Path $W, $I, "$I\saves\spike-s0\persistent.sfs"
Get-Process KSP_x64 -ErrorAction SilentlyContinue
Get-ChildItem Env:PARSEK_* -ErrorAction SilentlyContinue
Test-Path "$I\parsek-spike\S12"
Test-Path "$env:APPDATA\.mono\keypairs"
Get-ChildItem "$env:APPDATA\.mono\keypairs" -ErrorAction SilentlyContinue | Select-Object Name, Length | Tee-Object "$R\keypairs-before.txt"
```

Check: the first `Test-Path` prints `True` three times; `Get-Process` prints nothing;
`Env:PARSEK_*` prints nothing (if S10b left one, `Remove-Item Env:<name>` it); the next line
prints `False` (no S12 output from an earlier attempt; if `True`, move that folder to
`$R\leftover` with the LinkType check first). The last two lines only record what Mono's key
store held before (`False` and an empty listing are normal).

## 1. Update the worktree, build without deploying, copy into `spike-r0` (7 min)

The "Later spike builds" block of `PC-SETUP.md`, for S12. A hand copy is enough here: no
harness lane runs on this DLL, and S11's step 2 re-provisions `spike-r0` afterwards.

```powershell
git -C $W pull --ff-only
git -C $W rev-parse HEAD | Tee-Object "$R\spike-commit.txt"
git -C $W status --short
Set-Location $W
dotnet build Source\Parsek\Parsek.csproj -c Debug -p:SkipKspDeploy=true *> "$C\spike-S12-build.log"
$LASTEXITCODE
Select-String -Path "$C\spike-S12-build.log" -SimpleMatch -Pattern "KSP deploy skipped", "Build succeeded", " 0 Error(s)"
Copy-Item "$W\Source\Parsek\bin\Debug\Parsek.dll" "$I\GameData\Parsek\Plugins\Parsek.dll"
Set-Location $U
python $K dll "$I\GameData\Parsek\Plugins\Parsek.dll" --utf8 SpikeS12Crypto --utf8 SpikeS12Probes --utf8 SpikeS12Plan | Tee-Object "$R\deployed-dll.txt"
"{0}  {1}  {2}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath "$I\GameData\Parsek\Plugins\Parsek.dll").Hash.ToLowerInvariant(), (git -C $W rev-parse HEAD), "$I\GameData\Parsek\Plugins\Parsek.dll" | Set-Content "$R\deployed-dll-sha256.txt"
Get-Content "$R\deployed-dll-sha256.txt"
python $K dll "$DEV\GameData\Parsek\Plugins\Parsek.dll" --expect-absent | Tee-Object "$R\dev-dll-before.txt"
python $K links $I --expect GameData/Squad GameData/SquadExpansion
```

Check: the sha is the one the supervisor reported for `spike/mp-r0` with S12 merged;
`status` prints nothing; exit `0`; the three build lines found; the `dll` check of the
instance DLL prints `RESULT: PASS` (it fails with `SpikeS12Crypto missing in UTF-8` when the
copy did not happen); the dev DLL check `RESULT: PASS` (no spike marker there); the links
check `RESULT: PASS`.

## Part B. The probe, HAND

## 2. Run `CryptoProbe` from the test runner (5 min, H1)

```powershell
$env:PARSEK_SPIKE = "S12"
Start-Process -FilePath "$I\KSP_x64.exe" -WorkingDirectory $I -ArgumentList "-logFile `"$I\Player.log`"" -Wait
Remove-Item Env:PARSEK_SPIKE
Get-ChildItem Env:PARSEK_* -ErrorAction SilentlyContinue
```

In the game (H1):

1. Main menu: Start Game, Resume Saved, `spike-s0`, Load. Wait for the Space Center.
2. Press **Ctrl+Shift+T**. The Parsek test runner window opens.
3. Find the category line `SpikeS12 (0/1)` (type `SpikeS12` in the window's search box if
   the list is long) and click its name to expand it. One row: `CryptoProbe [single]`, tinted
   light blue (manual-only).
4. Click the small play button (a right-pointing triangle) at the right end of the
   `CryptoProbe [single]` row. Do NOT use the category's `Run` / `Run+` buttons or `Run All`:
   they skip a manual-only test.
5. Wait, hands off: one or two freezes of a few seconds are expected (see "What you will see
   in the game"). The run is over when the row turns green or red; give it up to 2 minutes.
6. Note green or red, and if red the text under the row.
7. Escape, Quit to Main Menu (it saves), then Quit. The prompt returns when KSP exits.

Check: the prompt returned; `Env:PARSEK_*` prints nothing.

## Part C. Collect and check, DRIVEN

## 3. Move the output out and read the lines (5 min)

```powershell
Copy-Item "$I\KSP.log" "$R\KSP.log"
Copy-Item "$I\parsek-test-results.txt" "$R\parsek-test-results.txt"
(Get-Item -LiteralPath "$I\parsek-spike" -Force).LinkType
(Get-Item -LiteralPath "$I\parsek-spike\S12" -Force).LinkType
Move-Item "$I\parsek-spike\S12" "$R\parsek-spike-S12"
Select-String -Path "$R\KSP.log" -Pattern "\[Spike\] armed|\[Spike\.S12\]" | ForEach-Object { $_.Line } | Tee-Object "$R\s12-lines.txt"
(Select-String -Path "$R\s12-lines.txt" -Pattern "\[Spike\.S12\] step ").Count
Select-String -Path "$R\s12-lines.txt" -Pattern "name=(sha256\.10mb\.warm|rsa\.create|ecdsa\.create|container\.leftover) |\] verdict " | ForEach-Object { $_.Line }
Select-String -Path "$R\parsek-test-results.txt" -SimpleMatch -Pattern "CryptoProbe"
Get-ChildItem "$R\parsek-spike-S12" | Select-Object Name, Length
Get-ChildItem "$env:APPDATA\.mono\keypairs" -ErrorAction SilentlyContinue | Select-Object Name, Length | Tee-Object "$R\keypairs-after.txt"
(Get-Content "$R\keypairs-before.txt" -Raw) -eq (Get-Content "$R\keypairs-after.txt" -Raw)
```

Check:
- both LinkType lines are EMPTY;
- `s12-lines.txt` starts with `[Parsek][INFO][Spike] armed: S12 build=PARSEK-SPIKE-R0`, then
  one `probe start run=s12-<stamp> ...` line naming `container=parsek-spike-s12-<stamp>`;
- the step count prints `32`;
- the filtered lines show `sha256.10mb.warm` (its `ms=` is the number the plan's 100 ms
  bound reads), `rsa.create`, `ecdsa.create` (status `ok` or `fail` with the exception type:
  both are answers), `container.leftover` with `nothing left` (see "What the probe leaves
  behind" if it says otherwise), and ONE `verdict ... result=PASS` or `result=FAIL` line;
- `parsek-test-results.txt` names `CryptoProbe` as passed (PASS verdict) or failed (FAIL
  verdict), matching the colour you saw;
- `parsek-spike-S12` holds `probe.csv` (33 lines: header + 32), `verdict.csv` (2 lines) and
  `rsa-public-<stamp>.xml`;
- the last command prints `True`: Mono's key store holds the same files as before (Mono
  names a key file after an MD5 hash of the container name, so the probe's own
  `container.*` lines, which list full paths, are where a leftover is named).

If the step count is not 32, or there is no verdict line, the probe did not finish: do not
re-run; keep `$R` and report the last `[Spike.S12]` line and the tail of `KSP.log`.

## Part D. Close-out

## 4. Close-out and what to hand back (2 min)

```powershell
python $K dll "$DEV\GameData\Parsek\Plugins\Parsek.dll" --expect-absent | Tee-Object "$R\dev-dll-after.txt"
python $K links $I --expect GameData/Squad GameData/SquadExpansion
Get-ChildItem Env:PARSEK_* -ErrorAction SilentlyContinue
Test-Path "$I\parsek-spike\S12"
Get-ChildItem $R | Select-Object Name, Length
```

Check: `RESULT: PASS` twice; no `PARSEK_*` variable; `False`. Hand back `$R` (the commit and
DLL hash, `KSP.log`, `parsek-test-results.txt`, `s12-lines.txt`, `parsek-spike-S12\` with its
CSVs and the public key, the two key-store listings, and your note of green / red) and the
console log `$C\spike-S12-build.log`: they are the S12 evidence for the spike report
(`r0-spikes.md` 6.4). Then go on with S11 (`spikes/r0/S11/PC-RUN.md`).
