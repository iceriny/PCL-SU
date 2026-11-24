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

& $DotNetPath @publishArguments
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

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
