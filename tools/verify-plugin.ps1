<#
verify-plugin.ps1 — быстрая проверка плагина ContainerCC.wcx64 без Total Commander:
пересобрать плагин, при необходимости упаковать тестовый каталог и прогнать
харнесс (он вызывает экспорты WCX так же, как это делает TC).

Запуск:
  powershell -ExecutionPolicy Bypass -File tools\verify-plugin.ps1
  powershell -ExecutionPolicy Bypass -File tools\verify-plugin.ps1 -SkipBuild
  powershell -ExecutionPolicy Bypass -File tools\verify-plugin.ps1 -Compare   (сравнение с Total7zip на compare.zip)
#>

[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$Compare,
    [string]$ComparePlugin = '',
    [string]$TcDir = 'C:\TCPU75'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

$Plugin = Join-Path $RepoRoot 'publish\wcx-plugin\ContainerCC.dll'
$Cli = Join-Path $RepoRoot 'publish\single-file\Container_V3.exe'
$HarnessProject = Join-Path $RepoRoot 'tools\wcx-harness'
$Src = Join-Path $RepoRoot '.harness\src'
$Archive = Join-Path $RepoRoot '.harness\test.cc'
$Work = Join-Path $RepoRoot '.harness\work'

function Invoke-Step([string]$title, [scriptblock]$body) {
    Write-Host "`n=== $title ===" -ForegroundColor Yellow
    & $body
    if ($LASTEXITCODE -ne 0) { Write-Host "  провал (exit $LASTEXITCODE)" -ForegroundColor Red; exit $LASTEXITCODE }
}

if (-not $SkipBuild) {
    Invoke-Step 'Сборка плагина (NativeAOT)' {
        dotnet publish ContainerWcx\ContainerWcx.csproj -c Release -r win-x64 --nologo -v q
    }
}

if (-not (Test-Path -LiteralPath $Plugin)) { Write-Host "Нет плагина: $Plugin" -ForegroundColor Red; exit 1 }
Write-Host ("plugin: {0} ({1:N0} байт)" -f $Plugin, (Get-Item -LiteralPath $Plugin).Length) -ForegroundColor Green

if (-not (Test-Path -LiteralPath $Src)) {
    Write-Host "Нет тестовых данных: $Src — создайте каталог с файлами и повторите запуск." -ForegroundColor Red
    exit 2
}

if (-not (Test-Path -LiteralPath $Cli)) {
    Invoke-Step 'Сборка командной строки утилиты' {
        dotnet publish Container_V3\Container_V3.csproj -c Release -r win-x64 --self-contained -o publish\single-file --nologo -v q
    }
}

$newestSrc = Get-ChildItem -LiteralPath $Src -Recurse -File -Force -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending | Select-Object -First 1
$repack = -not (Test-Path -LiteralPath $Archive) -or ($newestSrc -and $newestSrc.LastWriteTime -gt (Get-Item -LiteralPath $Archive).LastWriteTime)
if ($repack) {
    Invoke-Step 'Упаковка тестового каталога' {
        if (Test-Path -LiteralPath $Archive) { Remove-Item -LiteralPath $Archive -Force }
        & $Cli pack $Src -o $Archive -chunk 1M -level 3 | Out-Null
        Write-Host ("  {0} ({1:N0} байт)" -f $Archive, (Get-Item -LiteralPath $Archive).Length)
    }
}

if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }

$harnessArgs = @('--plugin', $Plugin, '--container', $Archive, '--source', $Src, '--workdir', $Work)
if ($Compare) {
    $foreign = $ComparePlugin
    if (-not $foreign) {
        $candidates = Get-ChildItem -Path $TcDir -Recurse -File -Filter '*.wcx64' -ErrorAction SilentlyContinue |
                      Where-Object { $_.Name -match 'Total7zip|Multiarc|iso|rpm' }
        $foreign = ($candidates | Where-Object { $_.Name -match 'Total7zip' } | Select-Object -First 1)
        if (-not $foreign) { $foreign = $candidates | Select-Object -First 1 }
        if ($foreign) { $foreign = $foreign.FullName }
    }
    $zip = Join-Path $RepoRoot '.harness\compare.zip'
    if ($foreign -and (Test-Path -LiteralPath $foreign) -and (Test-Path -LiteralPath $zip)) {
        Write-Host "  сравнение с: $foreign" -ForegroundColor Cyan
        $harnessArgs += @('--compare-plugin', $foreign, '--compare-archive', $zip)
    }
    else { Write-Host "  сравнение пропущено: не найден чужой плагин или $zip" -ForegroundColor DarkYellow }
}

Invoke-Step 'Харнесс WCX (листинг, распаковка, тест, ошибки)' {
    dotnet run --project $HarnessProject -c Release -- @harnessArgs
}

Write-Host "`nГотово: плагин проверен. Установка в Total Commander: tools\install-tc-plugin.ps1" -ForegroundColor Green
