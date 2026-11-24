#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Architecture = 'x64',
    [ValidateSet('Release', 'Beta', 'CI')]
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet',
    [string]$BuildMetadata = '',
    [string]$ReleaseNotesPath = '',
    [string]$ExpectedVersion = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'PCL.Launcher/PCL.Launcher.csproj'
$artifacts = Join-Path $repositoryRoot 'artifacts'
$publishDirectory = Join-Path $artifacts "publish/$Architecture"
$runtime = 'win-' + $Architecture.ToLowerInvariant()
$publishArguments = @(
    'publish', $project, '--configuration', $Configuration, '--runtime', $runtime,
    "-p:Platform=$Architecture", '-p:UseSharedCompilation=false',
    '--no-self-contained', '--output', $publishDirectory
)
if ($BuildMetadata) { $publishArguments += "-p:PclBuildMetadata=$BuildMetadata" }
if ($ReleaseNotesPath) {
    $resolvedNotes = [IO.Path]::GetFullPath($ReleaseNotesPath, $repositoryRoot)
    $publishArguments += "-p:PclReleaseNotesPath=$resolvedNotes"
}

$publish = [Diagnostics.ProcessStartInfo]::new($DotNetPath)
$publish.UseShellExecute = $false
$publish.CreateNoWindow = $true
$publish.WorkingDirectory = $repositoryRoot
$serviceNames = @(Get-Content -LiteralPath (Join-Path $repositoryRoot '.env.example') | ForEach-Object {
    if ($_ -match '^(PCL_[A-Z0-9_]+)=') { $Matches[1] }
})

$allowLocalSettings = !$env:PCL_WRITE_SECRET -or $env:PCL_WRITE_SECRET -eq '1'
if ($env:GITHUB_ACTIONS -ne 'true' -and $Configuration -ne 'CI' -and $allowLocalSettings) {
    $settings = Join-Path $repositoryRoot '.env.local'
    if (!(Test-Path -LiteralPath $settings) -and (Test-Path -LiteralPath (Join-Path $repositoryRoot '.git') -PathType Leaf)) {
        $worktrees = git -C $repositoryRoot -c core.quotepath=false worktree list --porcelain
        if ($LASTEXITCODE -eq 0 -and $worktrees[0] -match '^worktree (.+)$') {
            $settings = Join-Path $Matches[1] '.env.local'
        }
    }
    if (Test-Path -LiteralPath $settings -PathType Leaf) {
        foreach ($line in [IO.File]::ReadLines($settings)) {
            if ($line -notmatch '^\s*(PCL_[A-Z0-9_]+)\s*=\s*(.*?)\s*$') { continue }
            $name = $Matches[1]
            $value = $Matches[2]
            if ($name -cnotin $serviceNames -or ![string]::IsNullOrWhiteSpace($publish.Environment[$name])) { continue }
            if ($value -match '^([''"])(.*)\1$') { $value = $Matches[2] }
            $publish.Environment[$name] = $value
        }
    }
    $publish.Environment['PCL_WRITE_SECRET'] = '1'
}

if ($publish.Environment['PCL_WRITE_SECRET'] -eq '1') {
    # Only the digest goes through MSBuild. Its change invalidates PCL.Core's compiler inputs.
    $serviceValues = [ordered]@{}
    foreach ($name in $serviceNames) { $serviceValues[$name] = $publish.Environment[$name] }
    $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -Compress $serviceValues))
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    $publishArguments += "-p:PclServiceConfigurationHash=$hash"
}
foreach ($argument in $publishArguments) { $publish.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($publish)
try {
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Publish failed with exit code $($process.ExitCode)." }
} finally {
    $process.Dispose()
}

$appHost = Join-Path $publishDirectory 'PCL.Launcher.exe'
$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($appHost).ProductVersion
if ($ExpectedVersion -and $productVersion -ne $ExpectedVersion) {
    throw "Published version '$productVersion' does not match '$ExpectedVersion'."
}

$rawExecutable = Join-Path $publishDirectory 'Plain Craft Launcher 2.exe'
Move-Item -LiteralPath $appHost -Destination $rawExecutable -Force
$assetName = "PCL_SU_${Configuration}_$Architecture.exe"
$asset = Join-Path $artifacts $assetName
Copy-Item -LiteralPath $rawExecutable -Destination $asset -Force
$hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$asset.sha256", "$hash  $assetName" + [char]10)
Write-Output "Published $productVersion to $asset"
