<#
install-tc-plugin.ps1 — установка плагина ContainerCC.wcx64 в Total Commander.

Скрипт самодостаточен: его можно положить рядом с файлом плагина и отдать вместе с ним.
В репозитории он берёт publish\wcx-plugin\ContainerCC.dll, в готовом архиве он берёт
ContainerCC.wcx64 рядом с собой. Total Commander и её ini скрипт находит сам: по запущенному
процессу, по записи об установке в реестре, по COMMANDER_PATH и по типовым каталогам; каталог
можно указать и явно параметром -TcDir.

Что делает установка:
  1. Находит Total Commander и проверяет, что она 64-битная: плагин собран для x64.
  2. Проверяет, что Total Commander закрыта (TC перезаписывает свой ini при выходе,
     поэтому правки при запущенном TC будут потеряны).
  3. Делает резервную копию Wincmd.ini в папке ini-backups рядом со скриптом.
  4. Копирует AOT-библиотеку плагина в <TC>\Plugins\wcx\ContainerCC\ContainerCC.wcx64.
  5. Добавляет в [PackerPlugins] строку:
         cc=31,%COMMANDER_PATH%\Plugins\wcx\ContainerCC\ContainerCC.wcx
     Число 31 — возможности плагина, то есть ответ GetPackerCaps(): NEW|MODIFY|MULTIPLE|DELETE|OPTIONS.
     TC сверяет его с плагином; при расхождении копирование в контейнер и удаление из него
     будут недоступны, поэтому при смене возможностей плагин нужно переустановить этим скриптом.
     (TC 64-бит сам подставляет «64» к расширению: путь в ini остаётся .wcx,
      а грузится .wcx64 — так устроены все 64-битные плагины в этом ini).
  6. Добавляет cc в список [PackerPlugins64] exts=... (список расширений,
     для которых у плагина есть 64-битная версия).

Контрольная сумма $checksum$ в [PackerPlugins64] не меняется: по справке TC это
Adler32 от содержимого [PackerPlugins], полученный GetPrivateProfileSection;
воспроизвести её вне TC не удалось, поэтому она оставляется как есть — при
следующем сохранении ini Total Commander пересчитает её сам.

Режимы:
  -Mode install      установить (по умолчанию)
  -Mode uninstall    убрать записи плагина из ini и удалить файл плагина
  -Mode check        показать, что сейчас в ini и на диске
  -DryRun            показать, что будет сделано, ничего не меняя

Примеры:
  powershell -ExecutionPolicy Bypass -File install-tc-plugin.ps1 -DryRun
  powershell -ExecutionPolicy Bypass -File install-tc-plugin.ps1
  powershell -ExecutionPolicy Bypass -File install-tc-plugin.ps1 -Mode check
  powershell -ExecutionPolicy Bypass -File tools\install-tc-plugin.ps1 -TcDir 'D:\TotalCmd' -Mode check
#>

[CmdletBinding()]
param(
    [ValidateSet('install', 'uninstall', 'check')][string]$Mode = 'install',
    # Каталог Total Commander. Если не задан, скрипт ищет сам: запущенный процесс,
    # запись об установке в реестре, переменная COMMANDER_PATH, типовые каталоги.
    [string]$TcDir = '',
    # Путь к Wincmd.ini. Если не задан: сначала рядом с программой (портативная TC),
    # затем %APPDATA%\GHISLER\Wincmd.ini (обычная установка).
    [string]$IniPath = '',
    # Файл плагина. Если не задан: ContainerCC.wcx64 рядом со скриптом (готовый архив),
    # иначе собранная в репозитории publish\wcx-plugin\ContainerCC.dll.
    [string]$PluginSource = '',
    [string]$Extension = 'cc',
    # Число в [PackerPlugins] Total Commander сопоставляет с ответом GetPackerCaps() плагина.
    # Плагин отвечает 31 = NEW|MODIFY|MULTIPLE|DELETE|OPTIONS (создание, изменение, несколько
    # файлов, удаление, диалог параметров «Настроить»). Если здесь другое число, TC считает, что
    # у плагина нет нужных возможностей, и не предложит копирование/удаление — смена возможностей
    # требует переустановки плагина.
    [int]$FormatCode = 31,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$PluginName = 'ContainerCC'
$PluginDirName = $PluginName

function Write-Step([string]$text) { Write-Host "  $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "  $text" -ForegroundColor Green }
function Write-Bad([string]$text) { Write-Host "  $text" -ForegroundColor Red }

function Get-Sha256([string]$path) {
    # Get-FileHash появился в PowerShell 5.0; в старой оболочке считаем хеш через .NET.
    if (Get-Command -Name Get-FileHash -ErrorAction SilentlyContinue) {
        return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($path)
    try { $bytes = $sha.ComputeHash($stream) } finally { $stream.Dispose(); $sha.Dispose() }
    return (($bytes | ForEach-Object { '{0:X2}' -f $_ }) -join '')
}

# --------------------------------------------------------------------------
# Где стоит Total Commander, где её ini и где файл плагина
# --------------------------------------------------------------------------

function Get-TcDirInfo([string]$dir) {
    if (-not $dir -or -not (Test-Path -LiteralPath $dir)) { return $null }
    $exe64 = Join-Path $dir 'Totalcmd64.exe'
    if (Test-Path -LiteralPath $exe64) {
        return [pscustomobject]@{ Dir = $dir; Exe = $exe64; Is64 = $true }
    }
    $exe32 = Join-Path $dir 'Totalcmd.exe'
    if (Test-Path -LiteralPath $exe32) {
        return [pscustomobject]@{ Dir = $dir; Exe = $exe32; Is64 = $false }
    }
    return $null
}

function Find-TcDirCandidates() {
    $dirs = New-Object System.Collections.Generic.List[string]

    # переменная COMMANDER_PATH есть в окружении, если скрипт запустили из-под TC
    if ($env:COMMANDER_PATH) { $dirs.Add($env:COMMANDER_PATH) }

    # запущенная Total Commander: путь процесса говорит, где она стоит
    foreach ($proc in @(Get-Process -Name 'TOTALCMD64', 'TOTALCMD' -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $proc.Path } catch { $path = $null }
        if ($path) { $dirs.Add((Split-Path -Parent $path)) }
    }

    # запись об установке в списке программ
    $uninstallRoots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    foreach ($root in $uninstallRoots) {
        Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue | ForEach-Object {
            $item = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
            if (-not $item) { return }
            if (-not $item.DisplayName) { return }
            if ($item.DisplayName -notmatch 'Total Commander') { return }
            if ($item.InstallLocation) { $dirs.Add([string]$item.InstallLocation) }
            if ($item.DisplayIcon) {
                $icon = ([string]$item.DisplayIcon) -replace '"', ''
                if ($icon) { $dirs.Add((Split-Path -Parent $icon)) }
            }
        }
    }

    # типовые места: Program Files и портативные сборки в корнях дисков
    $scanRoots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, "$env:LOCALAPPDATA\Programs", 'C:\', 'D:\')
    foreach ($root in $scanRoots) {
        if (-not $root -or -not (Test-Path -LiteralPath $root)) { continue }
        Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'totalcmd|total|tc' } |
            ForEach-Object { $dirs.Add($_.FullName) }
    }

    $result = New-Object System.Collections.Generic.List[string]
    foreach ($d in $dirs) {
        $t = ([string]$d).Trim().TrimEnd('\')
        if (-not $t) { continue }
        if (-not $result.Contains($t)) { $result.Add($t) }
    }
    return $result
}

function Resolve-TcInstall() {
    if ($TcDir) {
        $info = Get-TcDirInfo $TcDir
        if (-not $info) { Write-Bad "В каталоге $TcDir нет ни Totalcmd64.exe, ни Totalcmd.exe. Проверьте -TcDir." }
        return $info
    }
    $found = New-Object System.Collections.Generic.List[object]
    foreach ($c in Find-TcDirCandidates) {
        $info = Get-TcDirInfo $c
        if ($info) { $found.Add($info) }
    }
    if ($found.Count -eq 0) {
        Write-Bad "Total Commander не найдена. Запустите скрипт с её каталогом: -TcDir 'C:\TotalCmd'"
        Write-Bad "Каталог виден в свойствах ярлыка Total Commander (поле «Рабочая папка»)."
        return $null
    }
    $x64 = @($found | Where-Object { $_.Is64 })
    $pool = $found.ToArray()
    if ($x64.Count -gt 0) { $pool = $x64 }
    if ($pool.Count -gt 1) {
        Write-Step "Найдено несколько Total Commander: $((($pool | ForEach-Object { $_.Dir }) -join '  |  ')) беру первую, точную задайте через -TcDir."
    }
    return $pool[0]
}

function Resolve-TcIni([string]$tcDir) {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($tcDir) { $candidates.Add((Join-Path $tcDir 'Wincmd.ini')) }
    if ($env:APPDATA) { $candidates.Add((Join-Path $env:APPDATA 'GHISLER\Wincmd.ini')) }
    $existing = @($candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
    if ($existing.Count -eq 0) { return $null }
    if ($existing.Count -gt 1) {
        Write-Step "Wincmd.ini найден в двух местах: $($existing -join '  |  ') беру более свежий по дате записи."
        $newest = Get-ChildItem -LiteralPath $existing[0], $existing[1] | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        return $newest.FullName
    }
    return $existing[0]
}

function Resolve-PluginSource() {
    $candidates = @(
        (Join-Path $PSScriptRoot "$PluginName.wcx64"),
        (Join-Path $PSScriptRoot "$PluginName.dll"),
        (Join-Path $RepoRoot 'publish\wcx-plugin\ContainerCC.dll')
    )
    foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { return $c } }
    return $candidates[0]
}

$t = Resolve-TcInstall
if (-not $t) { exit 2 }
$TcDir = $t.Dir
$TcExe = $t.Exe
if (-not $t.Is64) {
    Write-Bad "В $TcDir стоит только 32-битная Total Commander ($TcExe). Этот плагин собран для 64-битной версии: ему нужен Totalcmd64.exe."
    exit 2
}
if (-not $IniPath) {
    $ini = Resolve-TcIni $TcDir
    if (-not $ini) {
        Write-Bad "Не найден Wincmd.ini: ни $TcDir\Wincmd.ini, ни %APPDATA%\GHISLER\Wincmd.ini. Укажите путь явно: -IniPath 'C:\...\Wincmd.ini'"
        exit 2
    }
    $IniPath = $ini
}
if (-not $PluginSource) { $PluginSource = Resolve-PluginSource }

$PluginFile64 = Join-Path $TcDir "Plugins\wcx\$PluginDirName\$PluginName.wcx64"
$IniKeyPath = "%COMMANDER_PATH%\Plugins\wcx\$PluginDirName\$PluginName.wcx"

function Get-TcProcesses([string]$tcDir) {
    $prefix = $tcDir.ToLower()
    if (-not $prefix.EndsWith('\')) { $prefix += '\' }
    $result = New-Object System.Collections.Generic.List[object]
    foreach ($proc in (Get-Process -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $proc.Path } catch { $path = $null }
        if ($path -and $path.ToLower().StartsWith($prefix)) { $result.Add($proc) }
    }
    return $result
}

# --------------------------------------------------------------------------
# Чтение/запись ini с сохранением кодировки и перевода строк
# --------------------------------------------------------------------------

function Read-IniFile([string]$path) {
    $bytes = [byte[]][System.IO.File]::ReadAllBytes($path)
    $encoding = [System.Text.UTF8Encoding]::new($false)
    $preamble = [byte[]]@()

    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        $encoding = [System.Text.Encoding]::Unicode
        $preamble = [byte[]]@(0xFF, 0xFE)
        $bytes = $bytes[2..($bytes.Length - 1)]
    }
    elseif ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $encoding = [System.Text.UTF8Encoding]::new($true)
        $preamble = [byte[]]@(0xEF, 0xBB, 0xBF)
        $bytes = $bytes[3..($bytes.Length - 1)]
    }

    $text = $encoding.GetString($bytes)
    $newline = if ($text -match "`r`n") { "`r`n" } else { "`n" }
    $lines = @(($text -split "`r`n|`n"))

    return [pscustomobject]@{
        Lines = $lines; Newline = $newline; Encoding = $encoding; Preamble = $preamble
    }
}

function Write-IniFile([string]$path, $state) {
    $text = ($state.Lines -join $state.Newline)
    $body = $state.Encoding.GetBytes($text)
    $out = [byte[]]($state.Preamble + $body)
    if ($DryRun) {
        Write-Step "DryRun: файл $path не записывается (в файле $($state.Lines.Count) строк)"
        return
    }
    [System.IO.File]::WriteAllBytes($path, $out)
}

function Find-SectionRange([string[]]$lines, [string]$name) {
    $start = -1; $stop = $lines.Count
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*\[([^\]]*)\]\s*$') {
            if ($start -lt 0) { if ($matches[1] -eq $name) { $start = $i + 1 } }
            else { $stop = $i; break }
        }
    }
    if ($start -lt 0) { return $null }
    return [pscustomobject]@{ Start = $start; Stop = $stop }
}

function Close-Lines([string[]]$lines, $drop) {
    $kept = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if (-not $drop.Contains($i)) { $kept.Add($lines[$i]) }
    }
    return , [string[]]$kept.ToArray()
}

# --------------------------------------------------------------------------
# Установка
# --------------------------------------------------------------------------

function Install-Plugin() {
    Write-Host "`nУстановка плагина $PluginName в Total Commander" -ForegroundColor Yellow
    Write-Host "TC: $TcDir | ini: $IniPath" -ForegroundColor Yellow

    $running = @(Get-TcProcesses $TcDir)
    if ($running.Count) {
        Write-Bad "Total Commander запущен (PID $($running.Id -join ', ')). Закройте TC и повторите установку:"
        Write-Bad "  Close-Process -Id $($running.Id -join ',')   или просто закройте окно Total Commander."
        return 3
    }

    if (-not (Test-Path -LiteralPath $IniPath)) { Write-Bad "Не найден ini: $IniPath"; return 4 }
    if (-not (Test-Path -LiteralPath $PluginSource)) {
        Write-Bad "Не найден файл плагина: $PluginSource"
        Write-Bad "Если это готовый архив: файл ContainerCC.wcx64 должен лежать рядом с этим скриптом."
        Write-Bad "Если это репозиторий: сначала соберите плагин dotnet publish ContainerWcx\ContainerWcx.csproj -c Release -r win-x64"
        return 5
    }

    # 1) файл плагина
    $dir = Split-Path -Parent $PluginFile64
    if (-not (Test-Path -LiteralPath $dir)) {
        if ($DryRun) { Write-Step "DryRun: создал бы каталог $dir" } else { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    }
    Write-Step "Плагин: $PluginFile64  (источник $PluginSource)"
    if (-not $DryRun) { Copy-Item -LiteralPath $PluginSource -Destination $PluginFile64 -Force }
    $sourceHash = Get-Sha256 $PluginSource
    if ($DryRun) { Write-Ok "DryRun: поставил бы $sourceHash (файл плагина не меняется)" } else { Write-Ok "Скопировано, SHA-256 $sourceHash" }

    # 2) резервная копия ini
    if (-not $DryRun) {
        $backupDir = Join-Path $PSScriptRoot 'ini-backups'
        if (-not (Test-Path -LiteralPath $backupDir)) { New-Item -ItemType Directory -Path $backupDir -Force | Out-Null }
        $backup = Join-Path $backupDir ("Wincmd.{0:yyyyMMdd-HHmmss}.ini" -f (Get-Date))
        Copy-Item -LiteralPath $IniPath -Destination $backup
        Write-Ok "Резервная копия ini: $backup"
    }

    $state = Read-IniFile $IniPath
    $lines = @($state.Lines)
    $drop = New-Object 'System.Collections.Generic.HashSet[int]'

    # 3) [PackerPlugins]
    $range = Find-SectionRange $lines 'PackerPlugins'
    if (-not $range) {
        Write-Bad "В ini нет секции [PackerPlugins] — её создаст сам TC после установки плагина через его интерфейс."
        return 6
    }
    $newLine = "$Extension=$FormatCode,$IniKeyPath"
    $existing = @()
    for ($i = $range.Start; $i -lt $range.Stop; $i++) {
        if ($lines[$i] -match "^\s*$([regex]::Escape($Extension))\s*=") { $existing += [pscustomobject]@{ Index = $i; Text = $lines[$i] } }
    }

    if ($existing.Count -eq 1 -and $existing[0].Text.Trim() -eq $newLine) {
        Write-Ok "[PackerPlugins] уже содержит нужную запись: $($existing[0].Text.Trim())"
    }
    else {
        if ($existing.Count) {
            Write-Step "Заменяю существующую запись $Extension : $($existing | ForEach-Object { $_.Text.Trim() })"
            $at = $existing[0].Index
            $lines[$at] = $newLine
            for ($k = 1; $k -lt $existing.Count; $k++) { [void]$drop.Add($existing[$k].Index) }
        }
        else {
            # вставляем в конец непустой части секции — TC перечисляет плагины в порядке строк секции
            $tail = 0
            for ($i = $range.Stop - 1; $i -ge $range.Start; $i--) { if (-not $lines[$i].Trim()) { $tail++ } else { break } }
            $insertAt = $range.Stop - $tail
            $lines = @($lines[0..($insertAt - 1)]) + @($newLine) + @($lines[$insertAt..($lines.Count - 1)])
            Write-Step "Добавляю в [PackerPlugins] строку: $newLine (позиция $insertAt)"
        }
    }

    # 4) [PackerPlugins64] exts=
    $range64 = Find-SectionRange $lines 'PackerPlugins64'
    if (-not $range64) {
        Write-Bad "В ini нет секции [PackerPlugins64]: добавляю её вместе с exts."
        $lines = @($lines) + @('', '[PackerPlugins64]', "exts=*$Extension*", '$checksum$=0')
    }
    else {
        $extIndex = -1
        for ($i = $range64.Start; $i -lt $range64.Stop; $i++) {
            if ($lines[$i] -match '^\s*exts\s*=') { $extIndex = $i; break }
        }
        if ($extIndex -lt 0) {
            $lines = @($lines[0..($range64.Start - 1)]) + @("exts=*$Extension*") + @($lines[$range64.Start..($lines.Count - 1)])
            Write-Step "Добавляю в [PackerPlugins64] ключ exts=*$Extension*"
        }
        else {
            $current = $lines[$extIndex]
            $value = ($current -replace '^\s*exts\s*=\s*', '').Trim()
            $hadTrailingStar = $value.EndsWith('*')
            $items = @($value -split '\*' | Where-Object { $_.Trim() })
            if ($items -contains $Extension) {
                Write-Ok "[PackerPlugins64] exts уже содержит $Extension"
            }
            else {
                $items += $Extension
                $joined = (($items | ForEach-Object { $_.Trim() }) -join '*')
                if ($hadTrailingStar) { $joined += '*' }
                $lines[$extIndex] = "exts=$joined"
                Write-Step "Обновляю [PackerPlugins64] exts (добавлено $Extension; всего расширений $($items.Count))"
            }
        }
    }

    $state.Lines = Close-Lines $lines $drop
    Write-IniFile $IniPath $state
    if (-not $DryRun) { Write-Ok "ini обновлён: $IniPath" }

    Write-Host "`nЧто дальше:" -ForegroundColor Yellow
    Write-Host "  1. Запустите Total Commander."
    Write-Host "  2. В панели наведите курсор на файл .$Extension и нажмите Tab (или Ctrl+PgDn — войти в контейнер)."
    Write-Host "  3. Внутри контейнера работайте как с обычной папкой: F5 — копировать, F6 — переместить,"
    Write-Host "     Ctrl+F4 — вытащить выделенное, Del — удалить. Полоса прогресса при копировании в контейнер"
    Write-Host "     заполняется, «Stop» прерывает операцию так, что контейнер остаётся прежним."
    Write-Host "     После удаления файл не уменьшается: мёртвые чанки убирает «Container_V3.exe compact file.cc»."
    return 0
}

# --------------------------------------------------------------------------
# Удаление
# --------------------------------------------------------------------------

function Uninstall-Plugin() {
    Write-Host "`nУдаление записей плагина $PluginName из $IniPath" -ForegroundColor Yellow
    $running = @(Get-TcProcesses $TcDir)
    if ($running.Count) { Write-Bad "Закройте Total Commander (PID $($running.Id -join ', ')) перед удалением."; return 3 }

    $state = Read-IniFile $IniPath
    $lines = @($state.Lines)
    $drop = New-Object 'System.Collections.Generic.HashSet[int]'
    $removed = 0

    foreach ($section in @('PackerPlugins', 'PackerPlugins64')) {
        $range = Find-SectionRange $lines $section
        if (-not $range) { continue }
        for ($i = $range.Start; $i -lt $range.Stop; $i++) {
            if ($section -eq 'PackerPlugins' -and $lines[$i] -match "^\s*$([regex]::Escape($Extension))\s*=" -and $lines[$i] -match [regex]::Escape($PluginName)) {
                Write-Step "Убираю: $($lines[$i].Trim())"
                [void]$drop.Add($i); $removed++
            }
            if ($section -eq 'PackerPlugins64' -and $lines[$i] -match '^\s*exts\s*=') {
                $value = ($lines[$i] -replace '^\s*exts\s*=\s*', '').Trim()
                $hadTrailingStar = $value.EndsWith('*')
                $items = @($value -split '\*' | Where-Object { $_.Trim() -and $_.Trim() -ne $Extension })
                $joined = ($items -join '*')
                if ($hadTrailingStar -and $joined) { $joined += '*' }
                $newLine = "exts=$joined"
                if ($newLine -ne $lines[$i]) { Write-Step "Обновляю: $newLine"; $lines[$i] = $newLine; $removed++ }
            }
        }
    }

    if ($removed -eq 0) { Write-Ok "Записей плагина в ini не найдено — ничего не меняем." }
    else {
        $backupDir = Join-Path $PSScriptRoot 'ini-backups'
        if (-not (Test-Path -LiteralPath $backupDir)) { New-Item -ItemType Directory -Path $backupDir -Force | Out-Null }
        $backup = Join-Path $backupDir ("Wincmd.before-uninstall.{0:yyyyMMdd-HHmmss}.ini" -f (Get-Date))
        if (-not $DryRun) { Copy-Item -LiteralPath $IniPath -Destination $backup; Write-Ok "Резервная копия: $backup" }
        $state.Lines = Close-Lines $lines $drop
        Write-IniFile $IniPath $state
    }

    if (Test-Path -LiteralPath $PluginFile64) {
        if ($DryRun) { Write-Step "DryRun: удалил бы $PluginFile64" }
        else { Remove-Item -LiteralPath $PluginFile64 -Force; Write-Ok "Удалён файл плагина $PluginFile64" }
    }
    return 0
}

# --------------------------------------------------------------------------
# Проверка
# --------------------------------------------------------------------------

function Check-Plugin() {
    Write-Host "`nПроверка установки плагина $PluginName" -ForegroundColor Yellow
    Write-Host "ini: $IniPath" -ForegroundColor Yellow

    $state = Read-IniFile $IniPath
    $range = Find-SectionRange $state.Lines 'PackerPlugins'
    $found = @()
    if ($range) {
        for ($i = $range.Start; $i -lt $range.Stop; $i++) {
            if ($state.Lines[$i] -match "^\s*$([regex]::Escape($Extension))\s*=") { $found += $state.Lines[$i].Trim() }
        }
    }
    if ($found.Count) { Write-Ok "[PackerPlugins]: $($found -join ' | ')" } else { Write-Bad "[PackerPlugins]: записи для .$Extension нет" }

    $range64 = Find-SectionRange $state.Lines 'PackerPlugins64'
    if ($range64) {
        for ($i = $range64.Start; $i -lt $range64.Stop; $i++) {
            $line = $state.Lines[$i]
            if ($line -match '^\s*exts\s*=') {
                $items = @((($line -replace '^\s*exts\s*=\s*','').Trim()) -split '\*' | Where-Object { $_.Trim() })
                if ($items -contains $Extension) { Write-Ok "[PackerPlugins64] exts содержит $Extension ($($items.Count) расширений)" }
                else { Write-Bad "[PackerPlugins64] exts не содержит $Extension" }
            }
            if ($line -match '^\s*\$checksum\$\s*=\s*(\d+)') { Write-Step "записано `$checksum`$ = $($matches[1])" }
        }
    }

    if (Test-Path -LiteralPath $PluginFile64) {
        $item = Get-Item -LiteralPath $PluginFile64
        Write-Ok ("файл плагина: {0} ({1:N0} байт, изменён {2:yyyy-MM-dd HH:mm})" -f $item.FullName, $item.Length, $item.LastWriteTime)
        $expected = if (Test-Path -LiteralPath $PluginSource) { Get-Sha256 $PluginSource } else { '' }
        $actual = Get-Sha256 $PluginFile64
        if ($expected -and $expected -eq $actual) { Write-Ok "файл плагина из комплекта совпадает с установленным" }
        if ($expected -and $expected -ne $actual) { Write-Bad "файл плагина отличается от файла в комплекте — нужно переустановить" }
    }
    else { Write-Bad "файл плагина не найден: $PluginFile64" }
    return 0
}

switch ($Mode) {
    'install'   { exit (Install-Plugin) }
    'uninstall' { exit (Uninstall-Plugin) }
    'check'     { exit (Check-Plugin) }
}
