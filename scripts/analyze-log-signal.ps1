param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$LogPath,

    [int]$Top = 20
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
    throw "Log file not found: $LogPath"
}

$lines = Get-Content -LiteralPath $LogPath
$parsekLines = $lines | Where-Object { $_.Contains("[Parsek]") }

Write-Output "Log signal summary"
Write-Output "Path: $LogPath"
Write-Output "TotalLines: $($lines.Count)"
Write-Output "ParsekLines: $($parsekLines.Count)"
if ($lines.Count -gt 0) {
    $pct = [Math]::Round(($parsekLines.Count * 100.0) / $lines.Count, 2)
    Write-Output "ParsekPercent: $pct"
}
Write-Output ""

$structured = foreach ($line in $parsekLines) {
    if ($line -match "\[Parsek\]\[(?<level>[^\]]+)\]\[(?<subsystem>[^\]]+)\]\s*(?<message>.*)$") {
        [pscustomobject]@{
            Level = $Matches.level
            Subsystem = $Matches.subsystem
            Message = $Matches.message
            Key = "$($Matches.level)|$($Matches.subsystem)|$($Matches.message)"
            SubsystemKey = "$($Matches.level)|$($Matches.subsystem)"
        }
    }
}

Write-Output "Top subsystem/level counts:"
$structured |
    Group-Object SubsystemKey |
    Sort-Object Count -Descending |
    Select-Object -First $Top Count, Name |
    Format-Table -AutoSize |
    Out-String -Width 240 |
    Write-Output

Write-Output "Top exact repeated messages:"
$structured |
    Group-Object Key |
    Sort-Object Count -Descending |
    Select-Object -First $Top Count, Name |
    Format-Table -AutoSize |
    Out-String -Width 320 |
    Write-Output

Write-Output "WARN/ERROR messages:"
$structured |
    Where-Object { $_.Level -eq "WARN" -or $_.Level -eq "ERROR" } |
    Group-Object Key |
    Sort-Object Count -Descending |
    Select-Object -First $Top Count, Name |
    Format-Table -AutoSize |
    Out-String -Width 320 |
    Write-Output

Write-Output "Rate-limit suppressed counts:"
$structured |
    Where-Object { $_.Message -match "\bsuppressed=(?<count>\d+)" } |
    ForEach-Object {
        [pscustomobject]@{
            Suppressed = [int]([regex]::Match($_.Message, "\bsuppressed=(\d+)").Groups[1].Value)
            Level = $_.Level
            Subsystem = $_.Subsystem
            Message = $_.Message
        }
    } |
    Sort-Object Suppressed -Descending |
    Select-Object -First $Top Suppressed, Level, Subsystem, Message |
    Format-Table -AutoSize |
    Out-String -Width 320 |
    Write-Output
