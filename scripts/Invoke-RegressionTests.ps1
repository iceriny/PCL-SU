#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$DotNetPath = 'dotnet',
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot '../artifacts/test-results'),
    [string]$TestRoot = ''
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$ResultsDirectory = [System.IO.Path]::GetFullPath($ResultsDirectory, $repositoryRoot)
if ([string]::IsNullOrWhiteSpace($TestRoot)) {
    $temporaryRoot = if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
        [System.IO.Path]::GetTempPath()
    } else {
        $env:RUNNER_TEMP
    }
    $TestRoot = Join-Path $temporaryRoot ('PCLSU-Regression-' + [guid]::NewGuid().ToString('N'))
}
$TestRoot = [System.IO.Path]::GetFullPath($TestRoot, $repositoryRoot)
$previousTestRoot = [Environment]::GetEnvironmentVariable('PCLSU_TEST_ROOT')
$previousWriteSecret = [Environment]::GetEnvironmentVariable('PCL_WRITE_SECRET')
$exitCode = 1
$filter = @(
    'FullyQualifiedName~ResourceDownloadMetadataTest'
    'FullyQualifiedName~ModDependencyResolverTest'
    'FullyQualifiedName~ModCompDependencyTest'
    'FullyQualifiedName~FileDownloadRecoveryTest'
    'FullyQualifiedName~MinecraftServiceExceptionTest'
    'FullyQualifiedName~HostConnectionHandlerTest'
    'FullyQualifiedName~ProfileManagementTest'
    'FullyQualifiedName~UpdatePageStateTest'
    'FullyQualifiedName~UpdateManagerTest'
    'FullyQualifiedName~UpdateInstallTest'
    'FullyQualifiedName~UpdateSourceTest'
    'FullyQualifiedName~EncryptHelperKeyTest'
    'FullyQualifiedName~ResourceSearchTextTest'
    'FullyQualifiedName~LaunchLanguageTest'
    'FullyQualifiedName~AnimationDispatchTest'
    'FullyQualifiedName~FileConfigStorageTest'
    'FullyQualifiedName~LoggerTest'
    'FullyQualifiedName~LocalizationTest.LanguageDictionariesShouldNotContainDuplicateKeys'
    'FullyQualifiedName~LocalizationTest.LanguageKeysShouldUseDotNaming'
    'FullyQualifiedName~LocalizationTest.SupportedLanguagesShouldHaveValidCultureAndResourceDictionary'
) -join '|'
$trxPath = Join-Path $ResultsDirectory 'PCLSU-Regression.trx'
$isolatedTests = @(
    @{ Filter = 'ModDependencyDialogTest'; Report = 'PCLSU-DependencyDialog.trx' }
    @{ Filter = 'ControlInputRegressionTest'; Report = 'PCLSU-ControlInput.trx' }
    @{ Filter = 'AnimationClockTest'; Report = 'PCLSU-AnimationClock.trx' }
)

try {
    New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $TestRoot -Force | Out-Null
    [Environment]::SetEnvironmentVariable('PCLSU_TEST_ROOT', $TestRoot)
    # Regression tests use isolated configuration and do not embed service credentials.
    [Environment]::SetEnvironmentVariable('PCL_WRITE_SECRET', $null)
    if (Test-Path -LiteralPath $trxPath) { Remove-Item -LiteralPath $trxPath }
    foreach ($isolated in $isolatedTests) {
        $reportPath = Join-Path $ResultsDirectory $isolated.Report
        if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath }
    }

    $testArguments = @(
        'test', (Join-Path $repositoryRoot 'PCL.Core.Test/PCL.Core.Test.csproj'),
        '--configuration', 'Release',
        '-p:Platform=x64',
        '-p:UseSharedCompilation=false',
        '--filter', $filter,
        '--results-directory', $ResultsDirectory,
        '--logger', 'trx;LogFileName=PCLSU-Regression.trx',
        '--logger', 'console;verbosity=normal',
        '--verbosity', 'quiet',
        '-clp:ErrorsOnly;Summary'
    )
    Push-Location -LiteralPath $repositoryRoot
    try {
        & $DotNetPath @testArguments
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    if ($exitCode -eq 0) {
        if (-not (Test-Path -LiteralPath $trxPath)) {
            throw 'The test runner succeeded without producing the required TRX report.'
        }
        [xml]$report = Get-Content -LiteralPath $trxPath -Raw
        $counters = $report.TestRun.ResultSummary.Counters
        if ([int]$counters.passed -lt 132 -or [int]$counters.failed -gt 0 -or [int]$counters.notExecuted -gt 0) {
            throw "Expected at least 132 passing regressions with no failed or skipped cases; passed=$($counters.passed), failed=$($counters.failed), skipped=$($counters.notExecuted)."
        }

        # WPF Application can only be created once per process; keep the dialog tests isolated.
        foreach ($isolated in $isolatedTests) {
            $dialogTestArguments = @(
                'test', (Join-Path $repositoryRoot 'PCL.Core.Test/PCL.Core.Test.csproj'),
                '--configuration', 'Release',
                '-p:Platform=x64',
                '-p:UseSharedCompilation=false',
                '--no-build',
                '--filter', "FullyQualifiedName~$($isolated.Filter)",
                '--results-directory', $ResultsDirectory,
                '--logger', "trx;LogFileName=$($isolated.Report)",
                '--logger', 'console;verbosity=normal',
                '--verbosity', 'quiet',
                '-clp:ErrorsOnly;Summary'
            )
            Push-Location -LiteralPath $repositoryRoot
            try {
                & $DotNetPath @dialogTestArguments
                $exitCode = $LASTEXITCODE
            } finally {
                Pop-Location
            }
            if ($exitCode -ne 0) { break }
            [xml]$isolatedReport = Get-Content -LiteralPath (Join-Path $ResultsDirectory $isolated.Report) -Raw
            $isolatedCounters = $isolatedReport.TestRun.ResultSummary.Counters
            if ([int]$isolatedCounters.passed -lt 1 -or [int]$isolatedCounters.failed -gt 0 -or [int]$isolatedCounters.notExecuted -gt 0) {
                throw "Isolated regression $($isolated.Filter) did not pass every case."
            }
        }
    }
} catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
    $exitCode = 1
} finally {
    [Environment]::SetEnvironmentVariable('PCLSU_TEST_ROOT', $previousTestRoot)
    [Environment]::SetEnvironmentVariable('PCL_WRITE_SECRET', $previousWriteSecret)
}

exit $exitCode
