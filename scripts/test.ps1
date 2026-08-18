$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$app = Join-Path $projectRoot 'bin\DeepSeekHarnessControlPanel.exe'
$testSources = @(
    (Join-Path $projectRoot 'tests\StopTargetResolverTests.cs'),
    (Join-Path $projectRoot 'tests\UninstallTargetPlannerTests.cs'),
    (Join-Path $projectRoot 'tests\ControlPanelLayoutTests.cs')
)
$testOutput = Join-Path $projectRoot 'bin\StopTargetResolverTests.exe'

& (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $compiler /nologo /target:exe /out:$testOutput /r:$app $testSources
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $testOutput
exit $LASTEXITCODE
