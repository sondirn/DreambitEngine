[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $SdkVersion,

    [string] $PackageSource
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::Combine($PSScriptRoot, '..'))
$packageVersionsPath = Join-Path $projectRoot 'Directory.Packages.props'
$metadataPath = Join-Path $projectRoot '.dreambit/project.json'

foreach ($requiredPath in @($packageVersionsPath, $metadataPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "This is not a package-based Dreambit project: '$requiredPath' is missing."
    }
}

if (-not [string]::IsNullOrWhiteSpace($PackageSource)) {
    $PackageSource = [System.IO.Path]::GetFullPath($PackageSource)
    if (-not (Test-Path -LiteralPath $PackageSource -PathType Container)) {
        throw "Dreambit package source '$PackageSource' does not exist."
    }

    foreach ($packageId in @('DreambitEngine', 'Dreambit.Editor.Abstractions', 'DreambitEngine.Build')) {
        $packagePath = Join-Path $PackageSource "$packageId.$SdkVersion.nupkg"
        if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
            throw "Dreambit package '$packageId' version '$SdkVersion' was not found in '$PackageSource'."
        }
    }
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Content
    )

    [System.IO.File]::WriteAllText(
        $Path,
        $Content,
        [System.Text.UTF8Encoding]::new($false))
}

function Set-DreambitPackageVersion {
    param([Parameter(Mandatory = $true)][string] $Content)

    foreach ($packageId in @('DreambitEngine', 'Dreambit.Editor.Abstractions', 'DreambitEngine.Build')) {
        $pattern = '(<PackageVersion\s+Include="' +
            [System.Text.RegularExpressions.Regex]::Escape($packageId) +
            '"\s+Version=")[^"]+("\s*/?>)'
        if (-not [System.Text.RegularExpressions.Regex]::IsMatch($Content, $pattern)) {
            throw "Directory.Packages.props does not contain a version entry for '$packageId'."
        }
        $Content = [System.Text.RegularExpressions.Regex]::Replace(
            $Content,
            $pattern,
            ('${1}' + $SdkVersion + '${2}'))
    }
    return $Content
}

function Enable-EditorApiRuntimeReference {
    param([Parameter(Mandatory = $true)][string] $Content)

    $referencePattern = '(?is)<PackageReference\b(?=[^>]*\bInclude\s*=\s*["'']Dreambit\.Editor\.Abstractions["''])[^>]*(?:/\s*>|>.*?</PackageReference\s*>)'
    $reference = [System.Text.RegularExpressions.Regex]::Match($Content, $referencePattern)
    if (-not $reference.Success) {
        return $Content
    }

    $updatedReference = [System.Text.RegularExpressions.Regex]::Replace(
        $reference.Value,
        '\s+PrivateAssets\s*=\s*(?:"[^"]*"|''[^'']*'')',
        '',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $updatedReference = [System.Text.RegularExpressions.Regex]::Replace(
        $updatedReference,
        '(?is)\s*<PrivateAssets\b[^>]*>.*?</PrivateAssets\s*>',
        '')

    return $Content.Substring(0, $reference.Index) +
        $updatedReference +
        $Content.Substring($reference.Index + $reference.Length)
}

$originalPackageVersions = [System.IO.File]::ReadAllText($packageVersionsPath)
$originalMetadata = [System.IO.File]::ReadAllText($metadataPath)
$metadata = $originalMetadata | ConvertFrom-Json
if ($null -eq $metadata.sdk) {
    throw "Dreambit project metadata does not contain an SDK version."
}
if ([string]::IsNullOrWhiteSpace($metadata.solution)) {
    throw "Dreambit project metadata does not contain a solution path."
}
if ([string]::IsNullOrWhiteSpace($metadata.gameProject)) {
    throw "Dreambit project metadata does not contain a game project path."
}

$gameProjectPath = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::Combine($projectRoot, $metadata.gameProject))
$projectRootPrefix = $projectRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $gameProjectPath.StartsWith(
        $projectRootPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The game project path must remain inside the Dreambit project."
}
if (-not (Test-Path -LiteralPath $gameProjectPath -PathType Leaf)) {
    throw "The game project '$gameProjectPath' does not exist."
}
$originalGameProject = [System.IO.File]::ReadAllText($gameProjectPath)

try {
    $updatedPackageVersions = Set-DreambitPackageVersion -Content $originalPackageVersions
    $updatedGameProject = Enable-EditorApiRuntimeReference -Content $originalGameProject
    $metadata.sdk.version = $SdkVersion

    Write-Utf8File -Path $packageVersionsPath -Content $updatedPackageVersions
    Write-Utf8File -Path $metadataPath -Content ($metadata | ConvertTo-Json -Depth 10)
    Write-Utf8File -Path $gameProjectPath -Content $updatedGameProject

    $restoreArguments = @(
        'restore',
        (Join-Path $projectRoot $metadata.solution),
        '--nologo',
        '--force-evaluate')
    if (-not [string]::IsNullOrWhiteSpace($PackageSource)) {
        $restoreArguments += "-p:RestoreAdditionalProjectSources=$PackageSource"
    }

    & dotnet @restoreArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore exited with code $LASTEXITCODE."
    }
}
catch {
    Write-Utf8File -Path $packageVersionsPath -Content $originalPackageVersions
    Write-Utf8File -Path $metadataPath -Content $originalMetadata
    Write-Utf8File -Path $gameProjectPath -Content $originalGameProject
    throw
}

Write-Host "Updated this project to Dreambit SDK $SdkVersion." -ForegroundColor Green
