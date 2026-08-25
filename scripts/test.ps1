$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$app = Join-Path $projectRoot 'bin\DeepSeekHarnessControlPanel.exe'
$testSources = @(
    (Join-Path $projectRoot 'tests\StopTargetResolverTests.cs'),
    (Join-Path $projectRoot 'tests\UninstallTargetPlannerTests.cs'),
    (Join-Path $projectRoot 'tests\ControlPanelLayoutTests.cs'),
    (Join-Path $projectRoot 'tests\LogLineFormatterTests.cs'),
    (Join-Path $projectRoot 'tests\LogViewRenderingTests.cs'),
    (Join-Path $projectRoot 'tests\HarnessInstallationValidatorTests.cs'),
    (Join-Path $projectRoot 'tests\HarnessStartupPolicyTests.cs')
)
$testOutput = Join-Path $projectRoot 'bin\StopTargetResolverTests.exe'

& (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $compiler /nologo /target:exe /main:StopTargetResolverTests /out:$testOutput /r:$app $testSources
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $testOutput
exit $LASTEXITCODE
