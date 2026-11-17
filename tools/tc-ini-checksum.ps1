<#
tc-ini-checksum.ps1 — контрольная сумма секций плагинов Total Commander.

По официальной справке TC (раздел «Настройка INI-файла», секция [PackerPlugins64]):
  $checksum$ = Adler32 от ВСЕГО содержимого парной секции ([PackerPlugins],
  [FileSystemPlugins], [ListerPlugins], [ContentPlugins]), получаемого
  вызовом GetPrivateProfileSection.

Скрипт:
  --show   печатает записанные TC суммы и найденный вариант их вычисления;
  --set    пересчитывает сумму и записывает её в секцию (для установки плагина).

Запуск: powershell -ExecutionPolicy Bypass -File tools\tc-ini-checksum.ps1 --show
#>

[CmdletBinding()]
param(
    [ValidateSet('show', 'set')][string]$Mode = 'show',
    [string]$IniPath = 'C:\TCPU75\Wincmd.ini',
    [string[]]$Pairs = @('PackerPlugins64=PackerPlugins', 'FileSystemPlugins64=FileSystemPlugins',
                         'ListerPlugins64=ListerPlugins', 'ContentPlugins64=ContentPlugins'),
    [switch]$Backup
)

$ErrorActionPreference = 'Stop'

function Get-SectionRange([string[]]$lines, [string]$name) {
    $start = -1; $stop = $lines.Count
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*\[([^\]]*)\]\s*$') {
            if ($start -lt 0) { if ($matches[1] -eq $name) { $start = $i + 1 } }
            else { $stop = $i; break }
        }
    }
    if ($start -lt 0) { return $null }
    return , @($start, $stop)
}

function Get-SectionBody([string[]]$lines, [string]$name, [switch]$KeepBlank) {
    $range = Get-SectionRange $lines $name
    if (-not $range) { return $null }
    $body = New-Object System.Collections.Generic.List[string]
    foreach ($line in $lines[$range[0]..($range[1] - 1)]) {
        if (-not $KeepBlank -and -not $line.Trim()) { continue }
        $body.Add($line.TrimEnd())
    }
    return , $body
}

function Get-Adler32([byte[]]$bytes) {
    [uint64]$a = 1; [uint64]$b = 0
    foreach ($byte in $bytes) {
        $a = ($a + $byte) % 65521
        $b = ($b + $a) % 65521
    }
    return [uint32](($b -shl 16) -bor $a)
}

function Get-Bytes([string]$text, [string]$encoding) {
    switch ($encoding) {
        'utf8'    { return [byte[]][System.Text.UTF8Encoding]::new($false).GetBytes($text) }
        'ansi1251'{ return [byte[]][System.Text.Encoding]::GetEncoding(1251).GetBytes($text) }
        'utf16le' { return [byte[]][System.Text.Encoding]::Unicode.GetBytes($text) }
        'utf16le+bom' { return [byte[]][System.Text.Encoding]::Unicode.GetPreamble() + [byte[]][System.Text.Encoding]::Unicode.GetBytes($text) }
    }
}

function Resolve-ChecksumModel([string[]]$iniLines, [string]$sourceSection, [uint32]$wanted) {
    $encodings = @('utf8', 'ansi1251', 'utf16le', 'utf16le+bom')
    $separators = @{ 'NUL' = [string][char]0; 'CRLF' = "`r`n"; 'LF' = "`n" }
    $suffixes = @{ 'нет' = ''; 'один разделитель' = $null; 'два NUL' = ([string][char]0 + [string][char]0) }

    foreach ($blankMode in @($true, $false)) {
        $body = Get-SectionBody $iniLines $sourceSection -KeepBlank:$blankMode
        if (-not $body -or $body.Count -eq 0) { continue }

        foreach ($sepName in $separators.Keys) {
            $sep = $separators[$sepName]
            foreach ($enc in $encodings) {
                foreach ($sufName in @('нет', 'один разделитель', 'два NUL')) {
                    $suffix = if ($sufName -eq 'один разделитель') { $sep } elseif ($sufName -eq 'два NUL') { [string][char]0 + [string][char]0 } else { '' }
                    $text = ($body -join $sep) + $suffix
                    $sum = Get-Adler32 (Get-Bytes $text $enc)
                    if ($sum -eq $wanted) {
                        return [pscustomobject]@{
                            Encoding = $enc; Separator = $sepName; Suffix = $sufName
                            KeepBlank = $blankMode; Value = $sum
                        }
                    }
                }
            }
        }
    }
    return $null
}

$raw = Get-Content -LiteralPath $IniPath
$rawLines = @($raw)
Write-Host "ini: $IniPath (строк: $($rawLines.Count))" -ForegroundColor Cyan

$models = @()

foreach ($pair in $Pairs) {
    $dst, $src = $pair -split '=', 2
    $range = Get-SectionRange $rawLines $dst
    if (-not $range) { Write-Host "  [$dst] не найдена — пропуск" -ForegroundColor DarkYellow; continue }

    $recorded = $null
    foreach ($line in $rawLines[$range[0]..($range[1] - 1)]) {
        if ($line -match '^\s*\$checksum\$\s*=\s*(\d+)\s*$') { $recorded = [uint32]$matches[1]; break }
    }
    if ($null -eq $recorded) { Write-Host "  [$dst] строки `$checksum`$ нет" -ForegroundColor DarkYellow; continue }

    $model = Resolve-ChecksumModel $rawLines $src $recorded
    if ($model) {
        Write-Host ("  [{0}] записано {1} — совпало: кодировка {2}, разделитель {3}, хвост «{4}», пустые строки {5}" -f `
            $dst, $recorded, $model.Encoding, $model.Separator, $model.Suffix, $(if ($model.KeepBlank) { 'включены' } else { 'пропущены' })) -ForegroundColor Green
        $models += $model
    }
    else {
        Write-Host ("  [{0}] записано {1} — повторить не удалось" -f $dst, $recorded) -ForegroundColor Red
    }
}

if (-not $models) {
    Write-Host "`nАлгоритм не подтверждён: пересчитывать суммы нельзя." -ForegroundColor Red
    exit 2
}

$agree = $models | Group-Object Encoding, Separator, Suffix, KeepBlank
Write-Host ("`nМодель вычисления ({0} вариантов из {1} проверок): {2}" -f $agree.Count, $Pairs.Count, (($agree | ForEach-Object { $_.Name }) -join ' | ')) -ForegroundColor Cyan

if ($Mode -eq 'set') {
    # Пересчёт и запись суммы в целевую секцию — только при подтверждённой модели.
    $model = $agree[0].Name
    Write-Host "`nРежим --set пока не включён в этом скрипте: он вызывается из install-tc-plugin.ps1." -ForegroundColor Yellow
    exit 0
}
