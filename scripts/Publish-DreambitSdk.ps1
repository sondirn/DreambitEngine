[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [string] $NuGetSource = 'https://api.nuget.org/v3/index.json',

    [string] $ApiKey = $env:NUGET_API_KEY,

    [string] $OutputDirectory,

    [switch] $SkipTests,

    [switch] $Push,

    [switch] $SkipPush,

    [switch] $SkipTemplateInstall,

    [string] $LocalSourceName = 'Dreambit-local',

    [switch] $SkipLocalSourceUpdate
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::Combine($PSScriptRoot, '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = [System.IO.Path]::Combine(
        $repositoryRoot,
        'artifacts',
        'packages',
        $Version)
}
else {
    $OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
}

if ($Push -and -not $SkipPush -and [string]::IsNullOrWhiteSpace($ApiKey)) {
    throw 'Publishing requires -ApiKey or the NUGET_API_KEY environment variable.'
}

if (Get-Process -Name 'Dreambit.Editor' -ErrorAction SilentlyContinue) {
    throw 'Dreambit.Editor is running. Close it before publishing so the validation build can replace its assemblies.'
}

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    $displayArguments = for ($index = 0; $index -lt $Arguments.Count; $index++) {
        if ($index -gt 0 -and $Arguments[$index - 1] -eq '--api-key') {
            '<redacted>'
        }
        else {
            $Arguments[$index]
        }
    }
    Write-Host ('dotnet ' + ($displayArguments -join ' ')) -ForegroundColor DarkGray
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
}

function Set-VersionInFile {
    param(
        [Parameter(Mandatory = $true)][string] $RelativePath,
        [Parameter(Mandatory = $true)][string] $PreviousVersion
    )

    $path = [System.IO.Path]::Combine($repositoryRoot, $RelativePath)
    $text = [System.IO.File]::ReadAllText($path)
    if ($PreviousVersion -ne $Version) {
        if (-not $text.Contains($PreviousVersion)) {
            throw "Version surface '$RelativePath' does not contain expected version '$PreviousVersion'."
        }
        $text = $text.Replace($PreviousVersion, $Version)
        [System.IO.File]::WriteAllText(
            $path,
            $text,
            [System.Text.UTF8Encoding]::new($false))
    }
}

$runtimeProject = [System.IO.Path]::Combine(
    $repositoryRoot,
    'DreambitEngine',
    'DreambitEngine.csproj')
$runtimeProjectText = [System.IO.File]::ReadAllText($runtimeProject)
$versionMatch = [System.Text.RegularExpressions.Regex]::Match(
    $runtimeProjectText,
    '<PackageVersion[^>]*>(?<version>[^<]+)</PackageVersion>')
if (-not $versionMatch.Success) {
    throw 'Could not read the current Dreambit SDK version from DreambitEngine.csproj.'
}
$previousVersion = $versionMatch.Groups['version'].Value.Trim()

$versionSurfaces = @(
    'DreambitEngine\DreambitEngine.csproj',
    'Dreambit.Editor.Abstractions\Dreambit.Editor.Abstractions.csproj',
    'DreambitEngine.Build\DreambitEngine.Build.csproj',
    'DreambitEngine.Build\buildTransitive\DreambitEngine.Build.props',
    'DreambitEngine.Templates\DreambitEngine.Templates.csproj',
    'Dreambit.Editor\Dreambit.Editor.csproj',
    'Dreambit.Editor\Projects\DreambitSdkConstants.cs',
    'Dreambit.Editor\docs\project-format.md',
    'DreambitEngine.Templates\README.md',
    'DreambitEngine.Templates\content\dreambit-game\.template.config\template.json',
    'DreambitEngine.Templates\content\dreambit-game-source\.template.config\template.json'
)
foreach ($surface in $versionSurfaces) {
    Set-VersionInFile -RelativePath $surface -PreviousVersion $previousVersion
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Invoke-DotNet @(
    'restore',
    [System.IO.Path]::Combine($repositoryRoot, 'DreambitEngine.sln'),
    '--ignore-failed-sources')

if (-not $SkipTests) {
    Invoke-DotNet @(
        'test',
        [System.IO.Path]::Combine($repositoryRoot, 'Dreambit.Editor.Tests', 'Dreambit.Editor.Tests.csproj'),
        '--configuration', 'Release',
        '--no-restore')
}

$packageProjects = @(
    'DreambitEngine\DreambitEngine.csproj',
    'Dreambit.Editor.Abstractions\Dreambit.Editor.Abstractions.csproj',
    'DreambitEngine.Build\DreambitEngine.Build.csproj',
    'DreambitEngine.Templates\DreambitEngine.Templates.csproj'
)
foreach ($project in $packageProjects) {
    Invoke-DotNet @(
        'pack',
        [System.IO.Path]::Combine($repositoryRoot, $project),
        '--configuration', 'Release',
        '--output', $OutputDirectory,
        '--no-restore',
        "-p:PackageVersion=$Version")
}

$packageIds = @(
    'DreambitEngine',
    'Dreambit.Editor.Abstractions',
    'DreambitEngine.Build',
    'DreambitEngine.Templates'
)
$packages = foreach ($packageId in $packageIds) {
    $path = [System.IO.Path]::Combine(
        $OutputDirectory,
        "$packageId.$Version.nupkg")
    if (-not [System.IO.File]::Exists($path)) {
        throw "Expected package was not produced: $path"
    }
    $path
}

if (-not $SkipLocalSourceUpdate) {
    Write-Host "Updating local NuGet source '$LocalSourceName'." -ForegroundColor DarkGray
    & dotnet nuget update source $LocalSourceName --source $OutputDirectory
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Local NuGet source '$LocalSourceName' is not registered; adding it." -ForegroundColor DarkGray
        Invoke-DotNet @(
            'nuget', 'add', 'source', $OutputDirectory,
            '--name', $LocalSourceName)
    }
    else {
        Invoke-DotNet @('nuget', 'enable', 'source', $LocalSourceName)
    }
}

if ($Push -and -not $SkipPush) {
    foreach ($package in $packages) {
        Invoke-DotNet @(
            'nuget', 'push', $package,
            '--source', $NuGetSource,
            '--api-key', $ApiKey,
            '--skip-duplicate',
            '--timeout', '600')
    }
}

if (-not $SkipTemplateInstall) {
    $templatePackage = $packages | Where-Object {
        [System.IO.Path]::GetFileName($_).StartsWith(
            'DreambitEngine.Templates.',
            [System.StringComparison]::OrdinalIgnoreCase)
    }
    # dotnet allows multiple versions with the same template identity to remain
    # registered. Remove Dreambit's old registrations before installing the one
    # coordinated with this SDK release.
    Write-Host 'Removing older Dreambit project-template registrations.' -ForegroundColor DarkGray
    & dotnet new uninstall 'DreambitEngine.Templates'
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'No previous Dreambit project template was installed.' -ForegroundColor DarkGray
    }
    Invoke-DotNet @('new', 'install', $templatePackage, '--force')
}

Write-Host "Dreambit SDK $Version is ready." -ForegroundColor Green
Write-Host "Packages: $OutputDirectory"
if (-not $Push -or $SkipPush) {
    Write-Host 'NuGet push was skipped.' -ForegroundColor Yellow
}
