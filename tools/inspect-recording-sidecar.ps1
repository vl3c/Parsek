param(
    [Parameter(Mandatory = $true)]
    [string]$RecordingsDir,
    [string[]]$RecordingIds,
    [string]$ParsekDll,
    [string]$ManagedDir
)
$ErrorActionPreference = 'Stop'
if (-not $ParsekDll) {
    $ParsekDll = Join-Path $PSScriptRoot '..\Source\Parsek\bin\Debug\Parsek.dll'
}
if (-not $ManagedDir) {
    $ManagedDir = Join-Path $PSScriptRoot '..\..\Kerbal Space Program\KSP_x64_Data\Managed'
}
[Reflection.Assembly]::LoadFrom((Join-Path $ManagedDir 'UnityEngine.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $ManagedDir 'UnityEngine.CoreModule.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $ManagedDir 'Assembly-CSharp.dll')) | Out-Null
 $asm = [Reflection.Assembly]::LoadFrom($ParsekDll)
$probeType = $asm.GetType('Parsek.TrajectorySidecarProbe', $true)
$recordingType = $asm.GetType('Parsek.Recording', $true)
$binaryType = $asm.GetType('Parsek.TrajectorySidecarBinary', $true)
$flags = [Reflection.BindingFlags]'Static, NonPublic, Public'
$tryProbe = $binaryType.GetMethod('TryProbe', $flags)
$read = $binaryType.GetMethod('Read', $flags)
if (-not $RecordingIds -or $RecordingIds.Count -eq 0) {
    $RecordingIds = Get-ChildItem $RecordingsDir -Filter '*.prec' | ForEach-Object BaseName
}
foreach ($id in $RecordingIds) {
    $path = Join-Path $RecordingsDir ($id + '.prec')
    if (-not (Test-Path $path)) { Write-Output "MISSING $id $path"; continue }
    $probe = [Activator]::CreateInstance($probeType)
    $invokeArgs = [object[]]@([string]$path, $probe)
    if (-not $tryProbe.Invoke($null, $invokeArgs)) { Write-Output "PROBE_FAIL $id"; continue }
    $probe = $invokeArgs[1]
    $rec = [Activator]::CreateInstance($recordingType)
    $recordingType.GetField('RecordingId').SetValue($rec, $id)
    $read.Invoke($null, [object[]]@([string]$path, $rec, $probe)) | Out-Null
    $points = $recordingType.GetField('Points').GetValue($rec)
    $sections = $recordingType.GetField('TrackSections').GetValue($rec)
    Write-Output "=== $id ==="
    Write-Output (("points={0} sections={1}") -f $points.Count, $sections.Count)
    if ($points.Count -gt 0) {
        $first = $points[0]
        $last = $points[$points.Count - 1]
        Write-Output (("firstUT={0} lastUT={1} firstAlt={2} lastAlt={3} body={4}") -f $first.ut, $last.ut, $first.altitude, $last.altitude, $first.bodyName)
    }
    for ($i = 0; $i -lt $sections.Count; $i++) {
        $s = $sections[$i]
        $frames = if ($null -ne $s.frames) { $s.frames.Count } else { -1 }
        $checkpoints = if ($null -ne $s.checkpoints) { $s.checkpoints.Count } else { -1 }
        Write-Output (("sec[{0}] ut=[{1},{2}] env={3} ref={4} src={5} anchor={6} frames={7} checkpoints={8}") -f $i, $s.startUT, $s.endUT, $s.environment, $s.referenceFrame, $s.source, $s.anchorVesselId, $frames, $checkpoints)
    }
}
