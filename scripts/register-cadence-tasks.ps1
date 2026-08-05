# OPTIONAL, AND CURRENTLY NOT REGISTERED ANYWHERE.
#
# The operating mode today is ON DEMAND. A human runs
#     cd harness && python tools/cadence_runner.py --tier {daily|nightly|operator}
# walks away, and reads the outcome afterwards. NO scheduled task exists on the
# dev machine, by operator decision (2026-08-05), pending the selection-layer
# refactor tracked as HARNESS-TIER-TAXONOMY in docs/dev/todo-and-known-bugs.md.
#
# This script exists so scheduling can be turned ON reproducibly if that
# decision is ever taken - and so the constraints it would imply are written
# down and reviewable BEFORE anyone turns it on rather than discovered
# afterwards. Running it registers three Windows tasks under \Parsek\, each
# invoking harness/tools/cadence_runner.py with a fixed --tier.
#
# The repo carries only this script: scheduled tasks are MACHINE-LOCAL and are
# not (and cannot be) checked in. Run it once per machine that should fly the
# cadences; re-run it to change hours; -Unregister removes them again. It never
# registers anything by importing a task XML, so what is registered is exactly
# what is readable here.
#
# Constraints that scheduling WOULD imply (each is a decision, not a default):
#   Interactive logon only  KSP needs the interactive desktop. A task that fires
#                           while nobody is logged on simply does not run - which
#                           is the correct failure (a headless launch would fly
#                           blind and produce untrustworthy evidence).
#   -WakeToRun              the machine may wake from sleep to fly. This needs
#                           wake timers ENABLED in the active power plan; the
#                           runner separately holds the system awake for the
#                           duration (ES_SYSTEM_REQUIRED).
#   NO StartWhenAvailable   a missed window stays missed. StartWhenAvailable
#                           would fire a multi-hour KSP run the moment the box
#                           came back - i.e. mid-workday, taking the machine
#                           lock and the GPU out from under whoever is at it.
#   IgnoreNew               a second instance never starts while one is running.
#   ExecutionTimeLimit      an OUTER backstop only (4h daily / 12h nightly /
#                           12h operator). run.py's per-spec budgets do the real
#                           hang-killing; this only catches a wedged process.
#   Daily->nightly GAP      load-bearing. Skip-don't-queue means a nightly
#                           window that opens while the daily still holds the
#                           machine lock SKIPS - every day, forever, if the gap
#                           is shorter than the daily's real duration. The 2h
#                           default gap covers the observed ~20 min daily with
#                           margin; if the daily ever runs toward its 4h
#                           backstop the nightly WILL skip (a SKIPPED history
#                           line, not a hang). Keep the gap > the daily's worst
#                           case whenever overriding these times.
#
# Flags:
#   -DailyTime      HH:mm for the daily tier   (default 21:00)
#   -NightlyTime    HH:mm for the nightly tier (default 23:00)
#   -OperatorDay    weekday for the operator tier (default Saturday)
#   -OperatorTime   HH:mm for the operator tier (default 10:00)
#   -PythonExe      interpreter to run the runner with (default: `python` on PATH)
#   -RepoRoot       worktree whose harness/ is scheduled (default: this script's ..)
#   -Unregister     remove the three tasks and exit; registers nothing
#
# Windows PowerShell 5.1 compatible (no pwsh-7-only syntax).
param(
    [string]$DailyTime = "21:00",
    [string]$NightlyTime = "23:00",
    [string]$OperatorDay = "Saturday",
    [string]$OperatorTime = "10:00",
    [string]$PythonExe,
    [string]$RepoRoot,
    [switch]$Unregister
)

$ErrorActionPreference = "Stop"

$TaskPath = "\Parsek\"
$TaskNames = @("Harness Daily", "Harness Nightly", "Harness Operator Weekly")

function Remove-CadenceTasks {
    foreach ($name in $TaskNames) {
        $existing = Get-ScheduledTask -TaskPath $TaskPath -TaskName $name -ErrorAction SilentlyContinue
        if ($existing) {
            Unregister-ScheduledTask -TaskPath $TaskPath -TaskName $name -Confirm:$false
            Write-Host ("  removed {0}{1}" -f $TaskPath, $name)
        }
        else {
            Write-Host ("  not present {0}{1}" -f $TaskPath, $name)
        }
    }
}

if ($Unregister) {
    Write-Host "Unregistering Parsek harness cadence tasks:"
    Remove-CadenceTasks
    Write-Host "Done. No cadence tasks remain under $TaskPath."
    return
}

# ---- resolve paths ---------------------------------------------------------

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}
else {
    $RepoRoot = (Resolve-Path $RepoRoot).Path
}

$harnessDir = Join-Path $RepoRoot "harness"
$runnerPath = Join-Path $harnessDir "tools\cadence_runner.py"
if (-not (Test-Path $runnerPath)) {
    throw "cadence_runner.py not found at $runnerPath (is -RepoRoot pointing at a Parsek worktree?)"
}

if ([string]::IsNullOrWhiteSpace($PythonExe)) {
    $pythonCmd = Get-Command python -ErrorAction SilentlyContinue
    if (-not $pythonCmd) {
        throw "No 'python' on PATH; pass -PythonExe with the interpreter to use."
    }
    $PythonExe = $pythonCmd.Source
}
if (-not (Test-Path $PythonExe)) {
    throw "Python interpreter not found: $PythonExe"
}

# ---- principal (interactive current user) ----------------------------------

$userId = "$env:USERDOMAIN\$env:USERNAME"
$principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Limited

# ---- registration ----------------------------------------------------------

function Register-CadenceTask {
    param(
        [string]$Name,
        [object]$Trigger,
        [string]$Tier,
        [int]$LimitHours,
        [string]$TriggerDescription
    )

    $action = New-ScheduledTaskAction -Execute $PythonExe `
        -Argument ("`"{0}`" --tier {1}" -f $runnerPath, $Tier) `
        -WorkingDirectory $harnessDir

    $settings = New-ScheduledTaskSettingsSet `
        -WakeToRun `
        -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit (New-TimeSpan -Hours $LimitHours) `
        -StartWhenAvailable:$false

    # Idempotent by replacement: -Force overwrites an existing registration, so
    # re-running this script with new hours is the supported way to change them.
    Register-ScheduledTask -TaskPath $TaskPath -TaskName $Name `
        -Action $action -Trigger $Trigger -Principal $principal -Settings $settings `
        -Description ("Parsek automated-test harness: {0} tier, unattended." -f $Tier) `
        -Force | Out-Null

    Write-Host ("  {0,-24} {1,-34} {2} `"{3}`" --tier {4}" -f `
            $Name, $TriggerDescription, $PythonExe, $runnerPath, $Tier)
}

Write-Host "Registering Parsek harness cadence tasks under $TaskPath"
Write-Host ("  repo root  : {0}" -f $RepoRoot)
Write-Host ("  working dir: {0}" -f $harnessDir)
Write-Host ("  principal  : {0} (Interactive, Limited)" -f $userId)
Write-Host ""

Register-CadenceTask -Name "Harness Daily" `
    -Trigger (New-ScheduledTaskTrigger -Daily -At $DailyTime) `
    -Tier "daily" -LimitHours 4 `
    -TriggerDescription ("daily at {0}" -f $DailyTime)

Register-CadenceTask -Name "Harness Nightly" `
    -Trigger (New-ScheduledTaskTrigger -Daily -At $NightlyTime) `
    -Tier "nightly" -LimitHours 12 `
    -TriggerDescription ("daily at {0}" -f $NightlyTime)

Register-CadenceTask -Name "Harness Operator Weekly" `
    -Trigger (New-ScheduledTaskTrigger -Weekly -DaysOfWeek $OperatorDay -At $OperatorTime) `
    -Tier "operator" -LimitHours 12 `
    -TriggerDescription ("weekly {0} at {1}" -f $OperatorDay, $OperatorTime)

Write-Host ""
Write-Host "Registered 3 task(s). Verify with:"
Write-Host ("  schtasks /Query /TN `"{0}Harness Nightly`" /V /FO LIST" -f $TaskPath)
Write-Host ""
Write-Host "REMINDERS - both are prerequisites, not suggestions:"
Write-Host "  1. Wake timers must be ALLOWED in the active power plan or -WakeToRun"
Write-Host "     never fires from sleep. Check with:"
Write-Host "       powercfg /q SCHEME_CURRENT SUB_SLEEP  # look for 'Allow wake timers'"
Write-Host "  2. The user must be LOGGED ON (locked screen is fine, logged out is not):"
Write-Host "     KSP needs the interactive desktop, so a logged-out window is simply"
Write-Host "     skipped - a missing history line in harness/results/cadence/history.txt."
Write-Host ""
Write-Host "Morning review: harness/results/index.html, then results/cadence/history.txt."
Write-Host "Remove everything again with: -Unregister"
