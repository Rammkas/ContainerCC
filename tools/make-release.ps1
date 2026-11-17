<#
make-release.ps1 — собирает из уже собранного плагина готовый к пересылке комплект.

Что получается:
  dist\ContainerCC\ContainerCC.wcx64        плагин (ровно тот файл, что publish\wcx-plugin\ContainerCC.dll)
  dist\ContainerCC\install-tc-plugin.ps1    установщик (он самодостаточен: берёт плагин рядом с собой)
  dist\ContainerCC\install.bat              двойной щелчок: установить
  dist\ContainerCC\uninstall.bat            двойной щелчок: удалить
  dist\ContainerCC\README.md                инструкция для того, кому архив отправляют
  dist\ContainerCC\SHA256.txt               контрольная сумма плагина
  dist\ContainerCC-<версия>-win64.zip       архив для пересылки

Плагин берётся из publish\wcx-plugin\ContainerCC.dll; если файла нет, он собирается.
Полезно сначала прогнать проверку: tools\verify-plugin.ps1. NativeAOT при каждой сборке
выдаёт слегка отличающийся набор байтов, поэтому пересобирать плагин после проверки не
стоит: тогда в архив попадёт ровно тот файл, который проверял харнесс.

Параметры:
  -WithCli   положить рядом утилиту Container_V3.exe (одним файлом): она нужна для compact
  -NoZip     не упаковывать папку в zip
  -Version   версия в имени архива; по умолчанию берётся из файла плагина
#>

[CmdletBinding()]
param(
    [switch]$WithCli,
    [switch]$NoZip,
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

$PluginSource = Join-Path $RepoRoot 'publish\wcx-plugin\ContainerCC.dll'
$Installer = Join-Path $RepoRoot 'tools\install-tc-plugin.ps1'
$ReadmeSource = Join-Path $RepoRoot 'tools\release-README.md'
$OutDir = Join-Path $RepoRoot 'dist\ContainerCC'

function Invoke-Step([string]$title, [scriptblock]$body) {
    Write-Host "`n=== $title ===" -ForegroundColor Yellow
    $LASTEXITCODE = 0
    & $body
    if ($LASTEXITCODE -ne 0) { Write-Host "  провал (exit $LASTEXITCODE)" -ForegroundColor Red; exit $LASTEXITCODE }
}

if (-not (Test-Path -LiteralPath $PluginSource)) {
    Invoke-Step 'Сборка плагина (NativeAOT)' {
        dotnet publish ContainerWcx\ContainerWcx.csproj -c Release -r win-x64 --nologo -v q
    }
}
foreach ($required in @($PluginSource, $Installer, $ReadmeSource)) {
    if (-not (Test-Path -LiteralPath $required)) { Write-Host "Нет файла: $required" -ForegroundColor Red; exit 1 }
}

$pluginItem = Get-Item -LiteralPath $PluginSource
if (-not $Version) {
    $Version = $pluginItem.VersionInfo.ProductVersion
    if (-not $Version) { $Version = '0.0.0' }
}

# Папка выпуска создаётся заново: путь проверяется перед удалением.
$expectedOut = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'dist\ContainerCC'))
$targetOut = [System.IO.Path]::GetFullPath($OutDir)
if ($targetOut -ne $expectedOut) { Write-Host "Неожиданный путь выпуска: $targetOut" -ForegroundColor Red; exit 1 }
if (Test-Path -LiteralPath $targetOut) {
    if (-not (Test-Path -LiteralPath $targetOut -PathType Container)) {
        Write-Host "По пути выпуска лежит не папка: $targetOut" -ForegroundColor Red; exit 1
    }
    Remove-Item -LiteralPath $targetOut -Recurse -Force
}
New-Item -ItemType Directory -Path $targetOut -Force | Out-Null

Invoke-Step 'Комплект' {
    Copy-Item -LiteralPath $PluginSource -Destination (Join-Path $targetOut 'ContainerCC.wcx64')
    # Установщик переписывается с BOM: Windows PowerShell 5.1 без BOM читает файл
    # в кодовой странице системы и ломает кириллицу и длинные тире в тексте скрипта.
    $installerBytes = [System.IO.File]::ReadAllBytes($Installer)
    if (-not ($installerBytes[0] -eq 0xEF -and $installerBytes[1] -eq 0xBB -and $installerBytes[2] -eq 0xBF)) {
        $installerBytes = [byte[]]@(0xEF, 0xBB, 0xBF) + $installerBytes
    }
    [System.IO.File]::WriteAllBytes((Join-Path $targetOut 'install-tc-plugin.ps1'), $installerBytes)
    Copy-Item -LiteralPath $ReadmeSource -Destination (Join-Path $targetOut 'README.md')

    # Содержимое .bat только латиницей, чтобы не зависеть от кодовой страницы консоли.
    $runner = @'
@echo off
chcp 65001 >nul
setlocal
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" set "PS=powershell"
"%PS%" -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath '%~dp0' -File | Unblock-File"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-tc-plugin.ps1"%ARGS%
echo.
pause
'@
    Set-Content -LiteralPath (Join-Path $targetOut 'install.bat') -Value ($runner -replace '%ARGS%', ' %*') -Encoding ascii
    Set-Content -LiteralPath (Join-Path $targetOut 'uninstall.bat') -Value ($runner -replace '%ARGS%', ' -Mode uninstall %*') -Encoding ascii

    $hash = (Get-FileHash -LiteralPath (Join-Path $targetOut 'ContainerCC.wcx64') -Algorithm SHA256).Hash
    $lines = @(
        "ContainerCC.wcx64",
        "SHA-256: $hash",
        ("Size: {0} bytes" -f (Get-Item -LiteralPath (Join-Path $targetOut 'ContainerCC.wcx64')).Length),
        "Version: $Version",
        "Source: publish\wcx-plugin\ContainerCC.dll"
    )
    Set-Content -LiteralPath (Join-Path $targetOut 'SHA256.txt') -Value $lines -Encoding utf8
    Write-Host "  $($hash)"
}

if ($WithCli) {
    Invoke-Step 'Утилита Container_V3.exe для compact' {
        dotnet publish Container_V3\Container_V3.csproj -c Release -p:PublishProfile=single-file -p:RuntimeIdentifier=win-x64 -o dist\tmp-cli --nologo -v q
    }
    $cli = Join-Path $RepoRoot 'dist\tmp-cli\Container_V3.exe'
    if (-not (Test-Path -LiteralPath $cli)) { Write-Host "Нет Container_V3.exe после публикации: $cli" -ForegroundColor Red; exit 1 }
    Copy-Item -LiteralPath $cli -Destination (Join-Path $targetOut 'Container_V3.exe')
    $cliTmp = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'dist\tmp-cli'))
    Remove-Item -LiteralPath $cliTmp -Recurse -Force
}

$zip = ''
if (-not $NoZip) {
    $zip = Join-Path $RepoRoot "dist\ContainerCC-$Version-win64.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -LiteralPath $targetOut -DestinationPath $zip
}

Write-Host "`nГотово." -ForegroundColor Green
Get-ChildItem -LiteralPath $targetOut -File -Recurse |
    Sort-Object Name |
    Select-Object Name, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB, 1) } }, LastWriteTime |
    Format-Table -AutoSize
if ($zip) {
    Write-Host ("архив: {0} ({1:N0} байт)" -f $zip, (Get-Item -LiteralPath $zip).Length) -ForegroundColor Green
}
Write-Host "Тому, кому отправляете, нужно распаковать архив, закрыть Total Commander и запустить install.bat."
