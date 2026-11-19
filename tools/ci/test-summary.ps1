<#
.SYNOPSIS
  Prints failed xUnit tests from VSTest TRX logs as GitHub Actions annotations.
.DESCRIPTION
  `dotnet test` does not create annotations for failed tests, and the step log is only
  readable by authorized users. TRX files are written even when the test step fails, so CI
  runs this script afterwards: every failure becomes an `::error::` line, which GitHub shows
  as an annotation on the job page. A solution-level `dotnet test` produces one TRX per test
  project (the default file names differ), so every *.trx in the directory is scanned.
  All literals here are ASCII on purpose: Windows PowerShell 5.1 misreads BOM-less UTF-8, and
  Cyrillic literals in the script would break its parser. Test names and messages are read
  from the files and keep their original text.
#>
param(
    [string] $ResultsDirectory = 'TestResults',
    [int] $MaxFailures = 30
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ResultsDirectory -PathType Container)) {
    Write-Output "::error::no $ResultsDirectory - the test run did not finish (see the dotnet test step)"
    exit 0
}

$trxFiles = @(Get-ChildItem -Path $ResultsDirectory -Filter '*.trx' -File -Recurse)
if ($trxFiles.Count -eq 0) {
    Write-Output "::error::no .trx in $ResultsDirectory - the test run did not finish (see the dotnet test step)"
    exit 0
}

# TRX puts every element in its own default namespace, so XPath needs a prefix.
$total = 0
$passed = 0
$failedNodes = @()

foreach ($trx in $trxFiles) {
    $xml = [xml] (Get-Content -LiteralPath $trx.FullName -Raw)
    $ns = [System.Xml.XmlNamespaceManager]::new($xml.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')

    $results = $xml.SelectNodes('//t:UnitTestResult', $ns)
    $ok = $xml.SelectNodes('//t:UnitTestResult[@outcome="Passed"]', $ns)
    $bad = $xml.SelectNodes('//t:UnitTestResult[@outcome="Failed"]', $ns)

    $total += $results.Count
    $passed += $ok.Count
    foreach ($node in $bad) { $failedNodes += $node }
    Write-Output "::notice::$($trx.Name): total $($results.Count), passed $($ok.Count), failed $($bad.Count)"
}

Write-Output "::notice::all TRX files: total $total, passed $passed, failed $($failedNodes.Count)"

if ($failedNodes.Count -eq 0) {
    Write-Output '::error::no UnitTestResult with outcome="Failed" - the step failed during build or in the test adapter'
    exit 0
}

$shown = 0
foreach ($n in $failedNodes) {
    if ($shown -ge $MaxFailures) {
        Write-Output "::error::$($failedNodes.Count - $shown) more failed tests are in the TRX files"
        break
    }
    $shown++

    $name = $n.testName
    $msg = ''
    $stack = ''
    # Namespace manager is tied to the document the node belongs to, so it is rebuilt per node.
    $ns = [System.Xml.XmlNamespaceManager]::new($n.OwnerDocument.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $info = $n.SelectSingleNode('t:Output/t:ErrorInfo', $ns)
    if ($info) {
        $msgNode = $info.SelectSingleNode('t:Message', $ns)
        if ($msgNode) { $msg = $msgNode.'#text' }
        $stackNode = $info.SelectSingleNode('t:StackTrace', $ns)
        if ($stackNode) { $stack = $stackNode.'#text' }
    }
    $msg = ($msg -replace '\s+', ' ').Trim()
    if (-not $msg) { $msg = '(no error message)' }
    Write-Output "::error::$name : $msg"
    if ($stack) {
        Write-Output "::warning::stack ${name}: $(($stack -replace '\s+', ' ').Trim())"
    }
}
