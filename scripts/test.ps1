# Build and test into a staging directory so the suite can run while the panel is
# open (the running exe locks bin\).
param(
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot 'bin'
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$app = Join-Path $OutputDirectory 'DeepSeekHarnessControlPanel.exe'
$testSources = @(
    (Join-Path $projectRoot 'tests\StopTargetResolverTests.cs'),
    (Join-Path $projectRoot 'tests\UninstallTargetPlannerTests.cs'),
    (Join-Path $projectRoot 'tests\ControlPanelLayoutTests.cs'),
    (Join-Path $projectRoot 'tests\BrandMarkArtTests.cs'),
    (Join-Path $projectRoot 'tests\UiBackgroundTests.cs'),
    (Join-Path $projectRoot 'tests\LogLineFormatterTests.cs'),
    (Join-Path $projectRoot 'tests\LogViewRenderingTests.cs'),
    (Join-Path $projectRoot 'tests\HarnessInstallationValidatorTests.cs'),
    (Join-Path $projectRoot 'tests\HarnessStartupPolicyTests.cs'),
    (Join-Path $projectRoot 'tests\HarnessLifecyclePolicyTests.cs'),
    (Join-Path $projectRoot 'tests\HarnessProfileDiagnosticsTests.cs'),
    (Join-Path $projectRoot 'tests\HarnessProcessIdentityPolicyTests.cs'),
    (Join-Path $projectRoot 'tests\NodeNetworkPolicyTests.cs'),
    (Join-Path $projectRoot 'tests\UnexpectedErrorReportTests.cs'),
    (Join-Path $projectRoot 'tests\HarnessUpdatePolicyTests.cs'),
    (Join-Path $projectRoot 'tests\SingleInstancePolicyTests.cs'),
    (Join-Path $projectRoot 'tests\HarnessStatusChangePolicyTests.cs'),
    (Join-Path $projectRoot 'tests\OperationCancellationPolicyTests.cs'),
    (Join-Path $projectRoot 'tests\LogExportPolicyTests.cs'),
    (Join-Path $projectRoot 'tests\HarnessPortPolicyTests.cs'),
    (Join-Path $projectRoot 'tests\AutoStartAndTrayPolicyTests.cs'),
    (Join-Path $projectRoot 'tests\PanelVersionPolicyTests.cs')
)
$testOutput = Join-Path $OutputDirectory 'StopTargetResolverTests.exe'

& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $OutputDirectory
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $compiler /nologo /target:exe /main:StopTargetResolverTests /out:$testOutput `
    /r:$app `
    /r:System.Net.Http.dll `
    $testSources
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $testOutput
exit $LASTEXITCODE
