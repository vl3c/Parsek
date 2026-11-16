# S10a on the PC: the census over your own career saves (about 30 minutes)

Spike S10a counts the `Relative` track sections of a save and whether each carries a
complete body-fixed copy. The committed fixtures were already counted in a cloud session
(`REPORT-DRAFT.md`); this sheet runs the same test over your own career saves. No game is
started, nothing is provisioned, and your saves are never opened in place: the test reads a
COPY, and it refuses a folder that sits in a KSP install's `saves` folder.

## Privacy

- Your saves stay on this PC. The copies and the per-save CSVs stay on this PC.
- The only thing that leaves the PC is the summary table `summary-owner.md`: save LABELS
  you choose (`career-A`, `career-B`, ...), section counts and seconds. No save name, no
  vessel name, no kerbal name.
- The CSV itself carries no player-typed text either (no vessel, kerbal, tree or save name;
  the plan needs none): only the label, generated recording ids (GUIDs), UTs, counts and
  flags. It still stays on the PC.

## 0. Before you start

- KSP closed (or at the main menu), so no save is being written while you copy it.
- The spike worktree `<umbrella>\Parsek-spike-mp-r0` is at the `spike/mp-r0` commit that
  carries S10a (`git -C <umbrella>\Parsek-spike-mp-r0 log --oneline -1`; `git pull` first).
- PowerShell 7 (`pwsh`), the .NET SDK you build Parsek with, and Python 3.

In every block below replace `<umbrella>` with your umbrella folder (the one holding
`Kerbal Space Program`, `Parsek` and `logs`) and `<KSP>` with the install whose saves you
count (normally `<umbrella>\Kerbal Space Program`).

## 1. Copy each save (read-only on the original)

Pick a label per save (`career-A`, `career-B`, ...); the label is the copy folder's name
and the only name that reaches the summary. The test needs only `persistent.sfs` and the
`.prec` trajectory files, so only those are copied.

```powershell
$copies = Join-Path $env:TEMP 'parsek-s10-copies'

function Copy-S10Save([string]$SaveName, [string]$Label) {
    $src = Join-Path '<KSP>\saves' $SaveName
    $dst = Join-Path $copies $Label
    if (Test-Path -LiteralPath $dst) { throw "copy $dst already exists; pick another label or delete it" }
    New-Item -ItemType Directory -Force -Path (Join-Path $dst 'Parsek\Recordings') | Out-Null
    Copy-Item -LiteralPath (Join-Path $src 'persistent.sfs') -Destination $dst
    Get-ChildItem -LiteralPath (Join-Path $src 'Parsek\Recordings') -Filter '*.prec' |
        Copy-Item -Destination (Join-Path $dst 'Parsek\Recordings')
    "{0}: {1} .prec files" -f $Label, (Get-ChildItem -LiteralPath (Join-Path $dst 'Parsek\Recordings') -Filter '*.prec').Count
}

Copy-S10Save 'My Career Save'   'career-A'
Copy-S10Save 'Another Career'   'career-B'
```

`Copy-Item` only reads the source. Use the save's folder name as it appears under
`<KSP>\saves\`.

## 2. Run the census

```powershell
$date = Get-Date -Format 'yyyy-MM-dd'
$out  = "<umbrella>\logs\spikes\S10\$date\owner"
New-Item -ItemType Directory -Force -Path $out | Out-Null
New-Item -ItemType Directory -Force -Path "<umbrella>\logs\console\$date" | Out-Null

# One path, or several separated by ';'
$env:PARSEK_SPIKE_S10_SAVE = ((Get-ChildItem -LiteralPath $copies -Directory).FullName -join ';')
$env:PARSEK_SPIKE_OUT      = $out

Set-Location '<umbrella>\Parsek-spike-mp-r0\Source\Parsek.Tests'
dotnet test --filter "FullyQualifiedName~Parsek.Tests.Spikes.RelativeCopyCensusTests.Manual_RelativeCopyCensus_WritesCsv" *> "<umbrella>\logs\console\$date\s10a-owner-census.log"
Get-Content "<umbrella>\logs\console\$date\s10a-owner-census.log" -Tail 5

Remove-Item Env:PARSEK_SPIKE_S10_SAVE, Env:PARSEK_SPIKE_OUT
```

Expect `Passed: 1`. Each save writes `$out\<label>\relative_census.csv` and
`$out\<label>\load_summary.csv`. Open each `load_summary.csv`: `trees` and `recordings`
should match the save, and `sidecars_failed` should be 0 (a non-zero value, or a text in
`faults`, goes into the report as is). A failed run names the reason in the log: a refused
folder (`refused '...'`: you pointed at the live save, not the copy) or two copies with the
same label.

## 3. Summarize

```powershell
Set-Location '<umbrella>\Parsek-spike-mp-r0'
python spikes\r0\S10\summarize_census.py $out --out "$out\summary-owner.md"
```

The script prints two tables (by recorder source over all saves, and by save) and one
decision line. `summary-owner.md` is the file to send back; it holds labels, counts and
seconds only. Paste it into the empty slot of `REPORT-DRAFT.md`.

## 4. Clean up

```powershell
Remove-Item -LiteralPath $copies -Recurse -Force
```

This deletes only the copies under `%TEMP%`. Check the path printed by `$copies` before you
run it.
