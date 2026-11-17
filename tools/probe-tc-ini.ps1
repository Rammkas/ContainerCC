<#
Probe-tc-ini.ps1 — безопасная проверка того, как Total Commander относится к ручной
прописке пакера в wincmd.ini. Скрипт ничего не решает сам: он печатает факт.

Что проверяется (по локальным данным этого TC):
  1. [PackerPlugins] хранит путь с «.wcx», но на диске у работающих плагинов есть только
     «.wcx64» => 64-битный TC сам подставляет «64» к имени файла плагина.
  2. У секций плагинных списков есть строка $checksum$; скрипт вычисляет контрольные суммы
     секций простыми алгоритмами и показывает, совпадает ли значение с тем, что записал TC.

Скрипт НЕ меняет wincmd.ini и НЕ трогает запущенный TC.

Запуск:  powershell -ExecutionPolicy Bypass -File tools\probe-tc-ini.ps1
#>

[CmdletBinding()]
param(
    [string]$TcDir = 'C:\TCPU75',
    [string]$IniPath = ''
)

$ErrorActionPreference = 'Stop'

if (-not $IniPath) { $IniPath = Join-Path $TcDir 'Wincmd.ini' }
if (-not (Test-Path -LiteralPath $IniPath)) { throw "Не найден ini: $IniPath" }

Write-Host "ini: $IniPath" -ForegroundColor Cyan

# --- 1. путь из [PackerPlugins] против файлов на диске ---------------------------------
Write-Host "`n1) [PackerPlugins]: чего не хватает на диске" -ForegroundColor Yellow

$lines = Get-Content -LiteralPath $IniPath
$section = ''
$entries = @()

foreach ($line in $lines) {
    if ($line -match '^\s*\[([^\]]+)\]\s*$') { $section = $matches[1]; continue }
    if ($section -ne 'PackerPlugins') { continue }
    if ($line -notmatch '^\s*([^=]+)=\s*(\d+)\s*,\s*(.+)$') { continue }

    $ext = $matches[1].Trim()
    $num = [int]$matches[2]
    $rawPath = $matches[3].Trim()
    $resolved = $rawPath -replace '%COMMANDER_PATH%', $TcDir -replace '%commander_path%', $TcDir
    $entries += [pscustomobject]@{ Ext = $ext; Number = $num; Path = $resolved }
}

$missing64 = 0
$missingBoth = 0

foreach ($e in $entries) {
    $wcx = Test-Path -LiteralPath $e.Path
    $wcx64 = Test-Path -LiteralPath ($e.Path + '64')
    if (-not $wcx -and $wcx64) { $missing64++ }
    if (-not $wcx -and -not $wcx64) { $missingBoth++; Write-Host "  нет ни .wcx, ни .wcx64: $($e.Ext) -> $($e.Path)" -ForegroundColor DarkYellow }
}

Write-Host ("  записей в [PackerPlugins]: {0}" -f $entries.Count)
Write-Host ("  .wcx отсутствует, но .wcx64 есть (TC подставляет «64»): {0}" -f $missing64) -ForegroundColor Green
Write-Host ("  вообще ничего на диске (устаревшие записи): {0}" -f $missingBoth)

$distinct = $entries | Select-Object -ExpandProperty Path | Sort-Object -Unique
Write-Host ("  разных файлов плагинов в записи путей: {0}" -f $distinct.Count)
$numbers = $entries | Group-Object Number | ForEach-Object { "{0}({1})" -f $_.Name, $_.Count }
Write-Host ("  числа: {0}" -f ($numbers -join ' ')) -ForegroundColor DarkGray

# --- 2. $checksum$ и попытки его повторить ---------------------------------------------
Write-Host "`n2) checksum в секциях 64-битных списков" -ForegroundColor Yellow

function Get-SectionLines([string[]]$all, [string]$name) {
    $out = New-Object System.Collections.Generic.List[string]
    $inSection = $false
    foreach ($line in $all) {
        if ($line -match '^\s*\[([^\]]+)\]\s*$') {
            if ($matches[1] -eq $name) { $inSection = $true; continue }
            if ($inSection) { break }
            continue
        }
        if ($inSection -and $line.Trim().Length -gt 0) { $out.Add($line.TrimEnd()) }
    }
    return $out
}

function Sum-Ascii([string[]]$values) {
    $sum = 0
    foreach ($v in $values) { foreach ($c in $v.ToCharArray()) { $sum = ($sum + [int]$c) -band 0xFFFFFFFF }
        $sum = ($sum + 10) -band 0xFFFFFFFF   # перевод строки
    }
    return $sum
}

function Fnv1a32([string[]]$values) {
    $h = [uint64]2166136261
    foreach ($v in $values) {
        foreach ($c in $v.ToCharArray()) { $h = $h -bxor [uint64][int]$c; $h = ($h * 16777619) -band 4294967295 }
        $h = $h -bxor 10; $h = ($h * 16777619) -band 4294967295
    }
    return [uint32]$h
}

foreach ($sec in @('ContentPlugins64', 'ListerPlugins64', 'PackerPlugins64', 'FileSystemPlugins64')) {
    $body = Get-SectionLines $lines $sec
    $recordedLine = $body | Where-Object { $_ -match '^\s*\$checksum\$\s*=\s*(\d+)\s*$' } | Select-Object -First 1
    if (-not $recordedLine) { Write-Host "  ${sec}: \$checksum\$ не найден" -ForegroundColor DarkYellow; continue }
    $recorded = [uint32]([int](($recordedLine -replace '.*=\s*', '')))
    $payload = @($body | Where-Object { $_ -notmatch '^\s*\$checksum\$' })

    $ascii = [uint32](Sum-Ascii $payload)
    $fnv = [uint32](Fnv1a32 $payload)

    Write-Host ("  {0}: записано {1}; payload-строк {2}" -f $sec, $recorded, $payload.Count)
    Write-Host ("     ascii-сумма  = {0} {1}" -f $ascii, $(if ($ascii -eq $recorded) { 'СОВПАЛО' } else { '' })) -ForegroundColor DarkGray
    Write-Host ("     FNV-1a 32    = {0} {1}" -f $fnv, $(if ($fnv -eq $recorded) { 'СОВПАЛО' } else { '' })) -ForegroundColor DarkGray
    Write-Host ("     payload: {0}" -f (($payload | ForEach-Object { $_.Substring(0, [Math]::Min(90, $_.Length)) }) -join ' | ')) -ForegroundColor DarkGray
}

Write-Host "`nВывод:" -ForegroundColor Cyan
Write-Host "  Если .wcx нет, а .wcx64 есть и плагины работают — значит для нового пакетировщика"
Write-Host "  достаточно пути с «.wcx» в [PackerPlugins]; строку checksum пишет сам TC,"
Write-Host "  его пишет сам TC. Проверка на живом TC: после старта TC список секций должен измениться"
Write-Host "  только в сторону добавления новой записи (сделать снимок ini до и после)."
