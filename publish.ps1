# Публикация Container_V3 в переносимом виде.
#
#   .\publish.ps1                          # один exe (self-contained, win-x64)
#   .\publish.ps1 -Profile framework-dependent
#   .\publish.ps1 -Profile both
#   .\publish.ps1 -Profile single-file -Runtime win-arm64
#
# Результат: .\publish\<профиль>\ ; размеры и содержимое выводятся в конце.

param(
    [ValidateSet('single-file', 'framework-dependent', 'both')] [string] $Profile = 'single-file',
    [ValidateSet('win-x64', 'win-arm64', 'win-x86')] [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'Container_V3\Container_V3.csproj'
$profiles = if ($Profile -eq 'both') { 'single-file', 'framework-dependent' } else { $Profile }

foreach ($p in $profiles) {
    Write-Host "`n=== $p ($Runtime) ===" -ForegroundColor Cyan
    $outDir = Join-Path $root "publish\$p"
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

    if ($p -eq 'single-file') {
        dotnet publish $proj -c Release -p:PublishProfile=$p -p:RuntimeIdentifier=$Runtime
    } else {
        dotnet publish $proj -c Release -p:PublishProfile=$p
    }
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($p) завершился с кодом $LASTEXITCODE" }

    $files = Get-ChildItem $outDir -File -Recurse
    $mb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 2)
    $files | Sort-Object Length -Descending |
        Select-Object Name, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB, 1) } }
    Write-Host ("файлов: {0}, всего: {1} МБ -> {2}" -f $files.Count, $mb, $outDir) -ForegroundColor Green
}
