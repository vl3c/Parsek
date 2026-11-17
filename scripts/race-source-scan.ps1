# The shared race source scan (multiplayer design 15.4, plan R1-06), dot-sourced by the race
# grep-audit gates: grep-audit-race-core-no-store.ps1, grep-audit-race-no-writer.ps1 and
# grep-audit-race-no-recorder.ps1. Not a gate itself.
#
# The race files: every .cs file of each race module folder that exists (RaceCore now;
# RaceExport and RaceWorld are picked up the day their folders land, with no edit here) and
# Source/Parsek/Controllers/ParsekRace*.cs.
#
# Each file is LEXED, not grepped: comments become blanks, the contents of string and char
# literals become blanks (quotes and interpolation holes stay, so `$"{RecordingStore.X}"` is
# code), line breaks are kept so line numbers hold. A preprocessor line (a '#' first on its
# line) is blanked like a comment, and the format clause of a hole (from a ':' outside every
# bracket of the hole to its closing '}') is literal text. Before lexing, every character
# outside tab, LF, CR and 0x20..0x7E is refused on the raw text (one leading byte-order mark
# stripped) and read by the lexer as a blank. A shape the lexer does not model (a
# raw string literal, an unterminated literal or comment, a quote or line break in a format
# clause) throws, and the gate exits 2: it fails closed rather than reading half a file.
#
# Mirrored character for character by RaceSourceScan / RaceLex in
# Source/Parsek.Tests/GrepAuditTests.Race.cs, the managed arm that runs where pwsh is absent.
# GrepAuditTests.RaceGates_ScriptsMirrorTheManagedScan compares the two lists below with the
# managed constants everywhere, and runs both arms on one fixed input where pwsh exists.

$raceModuleFolders = @('RaceCore', 'RaceExport', 'RaceWorld')
$raceControllerPattern = 'ParsekRace*.cs'

$script:rlSpecial = [char[]]@('/', "'", '"', '$', '@', '{', '}', '#')
# In a hole the brackets and ':' matter too: a ':' outside every bracket starts the format
# clause, which is literal text up to the closing '}'.
$script:rlHoleSpecial = [char[]]@('/', "'", '"', '$', '@', '{', '}', '#', '(', ')', '[', ']', ':')
$script:raceTypeRefRe = [regex]::new('(?<![A-Za-z0-9_.])([A-Z][A-Za-z0-9_]*)\s*\.')
$script:raceAliasRe = [regex]::new('^\s*(?:global\s+)?using\s+@?([A-Za-z_][A-Za-z0-9_]*)\s*=')

function New-RaceLexError([string]$What) {
    $line = 1
    $limit = [Math]::Min($script:rlI, $script:rlT.Length)
    for ($j = 0; $j -lt $limit; $j++) {
        if ($script:rlT[$j] -ceq [char]10) { $line++ }
    }
    return "race scan: $What at $($script:rlRel):$line; the lexer does not model it, so the gate fails closed"
}

function Add-RaceBoth([char]$C) {
    [void]$script:rlM.Append($C)
    [void]$script:rlK.Append($C)
}

function Add-RaceBlank([char]$C) {
    if ($C -ceq [char]10) { [void]$script:rlM.Append([char]10) } else { [void]$script:rlM.Append([char]32) }
    [void]$script:rlK.Append($C)
}

function Get-RaceAt([int]$J) {
    if ($J -lt $script:rlT.Length) { return $script:rlT[$J] }
    return [char]0
}

# True when only blanks stand between the start of the line and position P.
function Test-RaceAtLineStart([int]$P) {
    $t = $script:rlT
    $j = $P - 1
    while ($j -ge 0 -and ($t[$j] -ceq [char]32 -or $t[$j] -ceq [char]9 -or $t[$j] -ceq [char]11 -or $t[$j] -ceq [char]12)) { $j-- }
    return ($j -lt 0 -or $t[$j] -ceq [char]10)
}

function Invoke-RaceBlankToEndOfLine {
    $t = $script:rlT
    $end = $t.IndexOf([char]10, $script:rlI)
    if ($end -lt 0) { $end = $t.Length }
    [void]$script:rlM.Append([char]32, $end - $script:rlI)
    [void]$script:rlK.Append([char]32, $end - $script:rlI)
    $script:rlI = $end
}

# Code until the end of the text, or (in a hole) until an unmatched '}', not consumed.
# A preprocessor line (a '#' first on its line) is blanked like a comment: read as code,
# `#region notes /*` would open a block comment over the real code below it.
function Invoke-RaceLexCode([bool]$InHole) {
    $t = $script:rlT
    $depth = 0
    if ($InHole) { $special = $script:rlHoleSpecial } else { $special = $script:rlSpecial }
    while ($script:rlI -lt $t.Length) {
        $next = $t.IndexOfAny($special, $script:rlI)
        if ($next -lt 0) { $next = $t.Length }
        if ($next -gt $script:rlI) {
            [void]$script:rlM.Append($t, $script:rlI, $next - $script:rlI)
            [void]$script:rlK.Append($t, $script:rlI, $next - $script:rlI)
            $script:rlI = $next
            continue
        }
        $c = $t[$script:rlI]
        $n = Get-RaceAt ($script:rlI + 1)
        if ($c -ceq [char]'#' -and (Test-RaceAtLineStart $script:rlI)) {
            Invoke-RaceBlankToEndOfLine
            continue
        }
        if ($c -ceq [char]'/' -and $n -ceq [char]'/') {
            Invoke-RaceBlankToEndOfLine
            continue
        }
        if ($c -ceq [char]'/' -and $n -ceq [char]'*') {
            $end = $t.IndexOf('*/', $script:rlI + 2, [System.StringComparison]::Ordinal)
            if ($end -lt 0) { throw (New-RaceLexError 'an unterminated block comment') }
            for ($j = $script:rlI; $j -lt $end + 2; $j++) {
                if ($t[$j] -ceq [char]10) { $b = [char]10 } else { $b = [char]32 }
                [void]$script:rlM.Append($b)
                [void]$script:rlK.Append($b)
            }
            $script:rlI = $end + 2
            continue
        }
        if ($c -ceq [char]39) {
            Invoke-RaceLexChar
            continue
        }
        if ($c -ceq [char]'"' -or $c -ceq [char]'$' -or $c -ceq [char]'@') {
            if (Invoke-RaceLexString) { continue }
        }
        if ($InHole) {
            if ($c -ceq [char]'{' -or $c -ceq [char]'(' -or $c -ceq [char]'[') {
                $depth++
            }
            elseif ($c -ceq [char]')' -or $c -ceq [char]']') {
                if ($depth -gt 0) { $depth-- }
            }
            elseif ($c -ceq [char]'}') {
                if ($depth -eq 0) { return }
                $depth--
            }
            elseif ($c -ceq [char]':' -and $depth -eq 0) {
                # Even `::`: the compiler starts the format clause at the first top-level ':'
                # (`{global::X}` does not compile), so `{x::/*}` is a hole whose format text is `:/*`.
                Invoke-RaceLexFormatClause
                return
            }
        }
        Add-RaceBoth $c
        $script:rlI++
    }
    if ($InHole) { throw (New-RaceLexError 'an unterminated interpolation hole') }
}

# The format clause of a hole: the ':' and everything up to the hole's closing '}' (not
# consumed) is literal text. A conditional operator in a hole compiles only in brackets, so a
# ':' outside every bracket always starts this clause.
function Invoke-RaceLexFormatClause {
    $t = $script:rlT
    Add-RaceBoth $t[$script:rlI]
    $script:rlI++
    while ($true) {
        if ($script:rlI -ge $t.Length) { throw (New-RaceLexError 'an unterminated interpolation format clause') }
        $c = $t[$script:rlI]
        if ($c -ceq [char]'}') { return }
        if ($c -ceq [char]'"' -or $c -ceq [char]10) {
            throw (New-RaceLexError 'a quote or line break in an interpolation format clause')
        }
        Add-RaceBlank $c
        $script:rlI++
    }
}

function Invoke-RaceLexChar {
    $t = $script:rlT
    $i = $script:rlI
    $j = $i + 1
    if ((Get-RaceAt $j) -ceq [char]92) { $j += 2 } else { $j += 1 }
    while ($j -lt $t.Length -and $t[$j] -cne [char]39 -and $t[$j] -cne [char]10 -and ($j - $i) -le 10) { $j++ }
    if ($j -ge $t.Length -or $t[$j] -cne [char]39) { throw (New-RaceLexError 'a char literal') }
    Add-RaceBoth ([char]39)
    for ($k = $i + 1; $k -lt $j; $k++) { Add-RaceBlank $t[$k] }
    Add-RaceBoth ([char]39)
    $script:rlI = $j + 1
}

# Returns $true when a string literal started here (and consumes it), $false when the '$' or
# '@' is not a string prefix.
function Invoke-RaceLexString {
    $t = $script:rlT
    $i = $script:rlI
    $j = $i
    $dollars = 0
    $verbatim = $false
    while (($j - $i) -lt 3 -and ((Get-RaceAt $j) -ceq [char]'$' -or (Get-RaceAt $j) -ceq [char]'@')) {
        if ($t[$j] -ceq [char]'$') { $dollars++ } else { $verbatim = $true }
        $j++
    }
    if ((Get-RaceAt $j) -cne [char]'"') { return $false }
    if ($dollars -gt 1 -or ($j - $i) -gt 2) { throw (New-RaceLexError 'a raw interpolated string literal') }
    if ((Get-RaceAt ($j + 1)) -ceq [char]'"' -and (Get-RaceAt ($j + 2)) -ceq [char]'"') {
        throw (New-RaceLexError 'a raw string literal')
    }
    $interpolated = ($dollars -eq 1)
    for ($k = $i; $k -le $j; $k++) { Add-RaceBoth $t[$k] }
    $script:rlI = $j + 1
    while ($true) {
        $p = $script:rlI
        if ($p -ge $t.Length) { throw (New-RaceLexError 'an unterminated string literal') }
        $c = $t[$p]
        $n = Get-RaceAt ($p + 1)
        if ((-not $verbatim) -and $c -ceq [char]92) {
            if (($p + 1) -ge $t.Length -or $n -ceq [char]10) { throw (New-RaceLexError 'an escape at the end of a line') }
            Add-RaceBlank $c
            Add-RaceBlank $n
            $script:rlI = $p + 2
            continue
        }
        if ($verbatim -and $c -ceq [char]'"' -and $n -ceq [char]'"') {
            Add-RaceBlank $c
            Add-RaceBlank $n
            $script:rlI = $p + 2
            continue
        }
        if ($c -ceq [char]'"') {
            Add-RaceBoth $c
            $script:rlI = $p + 1
            return $true
        }
        if ($interpolated -and ($c -ceq [char]'{' -or $c -ceq [char]'}') -and $n -ceq $c) {
            Add-RaceBlank $c
            Add-RaceBlank $n
            $script:rlI = $p + 2
            continue
        }
        if ($interpolated -and $c -ceq [char]'{') {
            Add-RaceBoth $c
            $script:rlI = $p + 1
            Invoke-RaceLexCode $true
            Add-RaceBoth ([char]'}')
            $script:rlI = $script:rlI + 1
            continue
        }
        if ((-not $verbatim) -and $c -ceq [char]10) { throw (New-RaceLexError 'an unterminated string literal') }
        Add-RaceBlank $c
        $script:rlI = $p + 1
    }
}

# A race file's text: its bytes decoded as UTF-8 (an invalid sequence becomes U+FFFD, which
# the raw check refuses), one leading U+FEFF byte-order mark stripped. Mirrors RaceReadText.
function Get-RaceText([string]$Path) {
    $text = [System.Text.UTF8Encoding]::new($false, $false).GetString([System.IO.File]::ReadAllBytes($Path))
    if ($text.Length -gt 0 -and $text[0] -ceq [char]0xFEFF) { $text = $text.Substring(1) }
    return $text
}

# A character a race file may NOT hold: anything but tab, LF, CR and 0x20..0x7E. The compiler
# reads more characters than the lexer models as line ends (U+0085, U+2028, U+2029) and as
# blanks (U+001A, form feed, vertical tab, the Unicode spaces), and drops format characters
# (U+00AD) from identifiers; refusing every other character on the RAW text, before any
# masking, covers all of them at once. Matched per UTF-16 code unit, as RaceAllowedChar is.
$script:raceRefusedCharRe = [regex]::new('[^\t\n\r\x20-\x7E]')

# The refusal of the first character of a raw line the race files may not hold
# ('[control] U+XXXX' below 0x80, '[non-ascii] U+XXXX' above), or $null.
function Get-RaceRawRefusal([string]$RawLine) {
    $m = $script:raceRefusedCharRe.Match($RawLine)
    if (-not $m.Success) { return $null }
    $v = [int]$m.Value[0]
    if ($v -lt 0x80) { $kind = '[control] U+' } else { $kind = '[non-ascii] U+' }
    return $kind + $v.ToString('X4', [System.Globalization.CultureInfo]::InvariantCulture)
}

# Lexes one file: Masked (comments and literal contents blank), Kept (comments blank), Lines
# (Masked split on line breaks) and Raw (the text split the same way). A refused character is a
# blank to the lexer (it is reported on its raw line, so the gate is red whatever the lexer then
# reads); line breaks and length are unchanged.
function ConvertTo-RaceLexed([string]$Text, [string]$Rel) {
    $raw = $Text.Replace("`r`n", "`n").Replace("`r", "`n")
    $script:rlT = $script:raceRefusedCharRe.Replace($raw, ' ')
    $script:rlI = 0
    $script:rlRel = $Rel
    $script:rlM = [System.Text.StringBuilder]::new($script:rlT.Length)
    $script:rlK = [System.Text.StringBuilder]::new($script:rlT.Length)
    Invoke-RaceLexCode $false
    $masked = $script:rlM.ToString()
    return [pscustomobject]@{
        Masked = $masked
        Kept   = $script:rlK.ToString()
        Lines  = $masked.Split([char]10)
        Raw    = $raw.Split([char]10)
    }
}

# Enumerates and lexes the race files of $RepoRoot. Throws on a source root that is missing or
# a file the lexer cannot model.
function Get-RaceScan([string]$RepoRoot) {
    $rootFull = ((Resolve-Path -LiteralPath $RepoRoot).Path -replace '\\', '/').TrimEnd('/')
    $src = [System.IO.Path]::Combine($rootFull, 'Source', 'Parsek')
    if (-not [System.IO.Directory]::Exists($src)) { throw "race scan: source root not found: $src" }
    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($folder in $raceModuleFolders) {
        $dir = [System.IO.Path]::Combine($src, $folder)
        if (-not [System.IO.Directory]::Exists($dir)) { continue }
        foreach ($p in [System.IO.Directory]::EnumerateFiles($dir, '*.cs', [System.IO.SearchOption]::AllDirectories)) {
            $entries.Add([pscustomobject]@{ Path = $p; Module = $folder })
        }
    }
    $controllers = [System.IO.Path]::Combine($src, 'Controllers')
    if ([System.IO.Directory]::Exists($controllers)) {
        foreach ($p in [System.IO.Directory]::EnumerateFiles($controllers, $raceControllerPattern, [System.IO.SearchOption]::TopDirectoryOnly)) {
            $entries.Add([pscustomobject]@{ Path = $p; Module = 'Controllers' })
        }
    }

    $files = [System.Collections.Generic.List[object]]::new()
    $moduleFiles = 0
    $controllerFiles = 0
    $typeRefs = 0
    foreach ($e in $entries) {
        $rel = $e.Path -replace '\\', '/'
        if ($rel.StartsWith($rootFull + '/', [System.StringComparison]::Ordinal)) {
            $rel = $rel.Substring($rootFull.Length + 1)
        }
        $lexed = ConvertTo-RaceLexed (Get-RaceText $e.Path) $rel
        $files.Add([pscustomobject]@{
            Rel    = $rel
            Module = $e.Module
            Masked = $lexed.Masked
            Kept   = $lexed.Kept
            Lines  = $lexed.Lines
            Raw    = $lexed.Raw
        })
        if ($e.Module -ceq 'Controllers') { $controllerFiles++ } else { $moduleFiles++ }
        foreach ($line in $lexed.Lines) { $typeRefs += $script:raceTypeRefRe.Matches($line).Count }
    }
    return [pscustomobject]@{
        Files           = $files
        ModuleFiles     = $moduleFiles
        ControllerFiles = $controllerFiles
        TypeReferences  = $typeRefs
    }
}

# Returns why the scan is vacuous for this gate, or $null.
function Get-RaceScanVacuity($Scan, [string]$Gate, [int]$MinTypeReferences, [bool]$NeedRaceCore) {
    $raceCore = 0
    foreach ($f in $Scan.Files) { if ($f.Module -ceq 'RaceCore') { $raceCore++ } }
    if ($NeedRaceCore -and $raceCore -eq 0) { return "${Gate}: no RaceCore file in the scan; this gate is vacuous" }
    if ($Scan.ModuleFiles -eq 0) { return "${Gate}: no race module file in the scan; this gate is vacuous" }
    if ($Scan.ControllerFiles -eq 0) {
        return "${Gate}: no Controllers/$raceControllerPattern file in the scan; the glob found none and this gate is vacuous"
    }
    if ($Scan.TypeReferences -lt $MinTypeReferences) {
        return "${Gate}: only $($Scan.TypeReferences) type reference(s) parsed; the parse broke and this gate is vacuous"
    }
    return $null
}

# The refusals every race gate shares, for one line: a character the race files may not hold
# (read on the raw line), a `using X =` alias or a backslash in code (a unicode-escaped
# identifier; both read on the masked line). Adds the violation and returns $true when refused.
function Test-RaceStructural($File, [int]$Index, $Violations) {
    $refusal = Get-RaceRawRefusal $File.Raw[$Index]
    if ($null -ne $refusal) {
        $Violations.Add($File.Rel + ':' + ($Index + 1) + ': ' + $refusal)
        return $true
    }
    $line = $File.Lines[$Index]
    $m = $script:raceAliasRe.Match($line)
    if ($m.Success) {
        $Violations.Add($File.Rel + ':' + ($Index + 1) + ': [alias] ' + $m.Groups[1].Value)
        return $true
    }
    if ($line.IndexOf([char]92) -ge 0) {
        $Violations.Add($File.Rel + ':' + ($Index + 1) + ': [escape] \')
        return $true
    }
    return $false
}
