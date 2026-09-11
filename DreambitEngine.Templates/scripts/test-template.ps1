[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$KeepOutput
)

$ErrorActionPreference = "Stop"
$templateRoot = Split-Path -Parent $PSScriptRoot
$engineRoot = Split-Path -Parent $templateRoot
$templateProject = Join-Path $templateRoot "DreambitEngine.Templates.csproj"
$runtimeProject = Join-Path $engineRoot "DreambitEngine/DreambitEngine.csproj"
$editorApiProject = Join-Path $engineRoot "Dreambit.Editor.Abstractions/Dreambit.Editor.Abstractions.csproj"
$buildProject = Join-Path $engineRoot "DreambitEngine.Build/DreambitEngine.Build.csproj"
$testRoot = Join-Path $templateRoot "TemplateTests"
$feed = Join-Path $testRoot "packages"
$templateHive = Join-Path $testRoot ".template-hive"
$testName = "Dreambit.TemplateSmokeTest"
$generated = Join-Path $testRoot $testName
$testFps = 144
$version = ([xml](Get-Content $templateProject)).Project.PropertyGroup.Version | Select-Object -First 1

function Invoke-DotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

Push-Location $templateRoot
try {
    Remove-Item $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $feed -ItemType Directory -Force | Out-Null

    foreach ($project in @($runtimeProject, $editorApiProject, $buildProject, $templateProject)) {
        Invoke-DotNet -Arguments @(
            "pack", $project, "-c", $Configuration,
            "-p:PackageVersion=$version", "-o", $feed, "--nologo")
    }

    $templatePackage = Join-Path $feed "DreambitEngine.Templates.$version.nupkg"
    Invoke-DotNet -Arguments @(
        "new", "--debug:custom-hive", $templateHive,
        "install", $templatePackage, "--force")

    Invoke-DotNet -Arguments @(
        "new", "--debug:custom-hive", $templateHive,
        "dreambit-game", "-n", $testName, "-o", $generated,
        "--game-title", "Template Smoke Test",
        "--sdkVersion", $version,
        "--targetRenderer", "DesktopVK",
        "--target-fps", $testFps,
        "--no-update-check")

    $expectedFiles = @(
        ".dreambit/project.json",
        ".editorconfig",
        ".gitignore",
        "Directory.Packages.props",
        "$testName.sln",
        "scripts/update-dreambit.ps1",
        "src/Directory.Build.props",
        "src/$testName/$testName.csproj",
        "src/$testName.Content/$testName.Content.csproj",
        "src/$testName.VK/$testName.VK.csproj"
    )

    foreach ($relativePath in $expectedFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $generated $relativePath) -PathType Leaf)) {
            throw "Generated template is missing '$relativePath'."
        }
    }

    foreach ($removedPath in @("external", "build")) {
        if (Test-Path -LiteralPath (Join-Path $generated $removedPath)) {
            throw "Generated package-based project unexpectedly contains '$removedPath'."
        }
    }

    $metadata = Get-Content -Raw -LiteralPath (Join-Path $generated ".dreambit/project.json") |
        ConvertFrom-Json
    if ($metadata.name -ne $testName -or
        $metadata.sdk.version -ne $version -or
        $metadata.targetRenderer -ne "DesktopVK" -or
        [Guid]$metadata.projectId -eq [Guid]::Empty) {
        throw "Generated Dreambit project metadata is invalid."
    }

    $program = Get-Content -Raw -LiteralPath (Join-Path $generated "src/$testName.VK/Program.cs")
    if ($program -notmatch 'title: "Template Smoke Test"' -or
        $program -notmatch "Core\.SetTargetFps\($testFps\);") {
        throw "Generated game title or target FPS was not replaced correctly."
    }

    $textFiles = Get-ChildItem $generated -Recurse -File |
        Where-Object { $_.Extension -in ".cs", ".csproj", ".json", ".md", ".props", ".sln", ".targets" }
    foreach ($textFile in $textFiles) {
        if ((Get-Content -Raw -LiteralPath $textFile.FullName) -match '__DREAMBIT_[A-Z_]+__') {
            throw "Generated output contains an unresolved placeholder in '$($textFile.FullName)'."
        }
    }

    $solutionPath = Join-Path $generated "$testName.sln"
    $solutionProjects = @(dotnet sln $solutionPath list) |
        ForEach-Object { $_.Trim() -replace '\\', '/' }
    $expectedSolutionProjects = @(
        "src/$testName/$testName.csproj",
        "src/$testName.Content/$testName.Content.csproj",
        "src/$testName.VK/$testName.VK.csproj"
    )
    foreach ($expectedProject in $expectedSolutionProjects) {
        if ($solutionProjects -notcontains $expectedProject) {
            throw "Generated solution is missing '$expectedProject'."
        }
    }

    Invoke-DotNet -Arguments @(
        "restore", $solutionPath,
        "-p:RestoreAdditionalProjectSources=$feed", "--nologo")
    $launcherProject = Join-Path $generated "src/$testName.VK/$testName.VK.csproj"
    $importedSdkVersion = dotnet msbuild $launcherProject `
        -getProperty:DreambitSdkVersion `
        --nologo
    if ($LASTEXITCODE -ne 0 -or ($importedSdkVersion | Select-Object -Last 1).Trim() -ne $version) {
        throw "DreambitEngine.Build did not expose the expected SDK version."
    }
    Invoke-DotNet -Arguments @(
        "build", $solutionPath, "--no-restore", "--nologo")

    $editorApiAssembly = Join-Path `
        $generated `
        "src/$testName.VK/Build/Debug/Dreambit.Editor.Abstractions.dll"
    if (-not (Test-Path -LiteralPath $editorApiAssembly -PathType Leaf)) {
        throw "The launcher output is missing Dreambit.Editor.Abstractions.dll."
    }

    if ($KeepOutput) {
        Write-Host "Template smoke test passed: $generated" -ForegroundColor Green
    }
    else {
        Write-Host "Template smoke test passed." -ForegroundColor Green
    }
}
finally {
    Pop-Location
    if (-not $KeepOutput) {
        Remove-Item $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
