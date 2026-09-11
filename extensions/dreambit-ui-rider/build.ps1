$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Test-Jdk21([string]$JavaHome) {
    if ([string]::IsNullOrWhiteSpace($JavaHome)) {
        return $false
    }

    $javaExe = Join-Path $JavaHome "bin\java.exe"
    $javacExe = Join-Path $JavaHome "bin\javac.exe"
    $releaseFile = Join-Path $JavaHome "release"

    if (-not (Test-Path $javaExe) -or -not (Test-Path $javacExe) -or -not (Test-Path $releaseFile)) {
        return $false
    }

    # Do not probe with `java -version` here. Java intentionally writes its
    # version banner to stderr, which Windows PowerShell can promote to a
    # NativeCommandError when $ErrorActionPreference is Stop.
    $releaseText = Get-Content -Path $releaseFile -Raw -ErrorAction SilentlyContinue
    return $releaseText -match '(?m)^JAVA_VERSION="21(?:[\._+-]|"$)'
}

function Invoke-VersionCommand([string]$ExecutablePath) {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $ExecutablePath
    $startInfo.Arguments = "-version"
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    [void]$process.Start()

    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    if (-not [string]::IsNullOrWhiteSpace($stdout)) {
        Write-Host $stdout.TrimEnd()
    }
    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        Write-Host $stderr.TrimEnd()
    }

    if ($process.ExitCode -ne 0) {
        throw "Version command failed with exit code $($process.ExitCode): $ExecutablePath"
    }
}

function Get-JavaHomeFromExecutable([string]$ExecutablePath) {
    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
        return $null
    }

    return Split-Path (Split-Path $ExecutablePath -Parent) -Parent
}

function Find-Jdk21 {
    $candidates = New-Object System.Collections.Generic.List[string]

    if ($env:JAVA_HOME) {
        $candidates.Add($env:JAVA_HOME)
    }

    $pathJavac = Get-Command javac -ErrorAction SilentlyContinue
    if ($pathJavac) {
        $candidates.Add((Get-JavaHomeFromExecutable $pathJavac.Source))
    }

    # JDK downloaded by this script on a previous build.
    $localJdks = Join-Path $PSScriptRoot ".tools\jdk-21"
    if (Test-Path $localJdks) {
        Get-ChildItem -Path $localJdks -Filter javac.exe -File -Recurse -ErrorAction SilentlyContinue |
            ForEach-Object { $candidates.Add((Get-JavaHomeFromExecutable $_.FullName)) }
    }

    # Common Windows JDK installation roots.
    $searchRoots = New-Object System.Collections.Generic.List[string]
    if ($env:ProgramFiles) {
        $searchRoots.Add((Join-Path $env:ProgramFiles "Eclipse Adoptium"))
        $searchRoots.Add((Join-Path $env:ProgramFiles "Java"))
        $searchRoots.Add((Join-Path $env:ProgramFiles "Microsoft"))
        $searchRoots.Add((Join-Path $env:ProgramFiles "Amazon Corretto"))
        $searchRoots.Add((Join-Path $env:ProgramFiles "BellSoft"))
        $searchRoots.Add((Join-Path $env:ProgramFiles "Zulu"))
    }

    foreach ($root in $searchRoots) {
        if (-not (Test-Path $root)) {
            continue
        }

        Get-ChildItem -Path $root -Filter javac.exe -File -Recurse -ErrorAction SilentlyContinue |
            ForEach-Object { $candidates.Add((Get-JavaHomeFromExecutable $_.FullName)) }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-Jdk21 $candidate) {
            return $candidate
        }
    }

    return $null
}

function Install-LocalJdk21 {
    $toolsRoot = Join-Path $PSScriptRoot ".tools"
    $jdkRoot = Join-Path $toolsRoot "jdk-21"
    $archive = Join-Path $toolsRoot "temurin-jdk21.zip"

    New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null

    if (Test-Path $jdkRoot) {
        Remove-Item -Path $jdkRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $jdkRoot | Out-Null

    # Adoptium's stable API redirects to the current GA Temurin 21 x64 JDK.
    $url = "https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jdk/hotspot/normal/eclipse"

    Write-Host "Java 21 JDK was not found. Downloading Temurin JDK 21 locally..."
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing

    Write-Host "Extracting JDK 21..."
    Expand-Archive -Path $archive -DestinationPath $jdkRoot -Force
    Remove-Item $archive -Force

    $javac = Get-ChildItem -Path $jdkRoot -Filter javac.exe -File -Recurse -ErrorAction Stop |
        Select-Object -First 1

    if (-not $javac) {
        throw "The JDK archive was extracted, but bin\javac.exe could not be found."
    }

    $javaHome = Get-JavaHomeFromExecutable $javac.FullName
    if (-not (Test-Jdk21 $javaHome)) {
        throw "The downloaded Java installation is not a usable Java 21 JDK: $javaHome"
    }

    return $javaHome
}

$jdk21 = Find-Jdk21
if (-not $jdk21) {
    $jdk21 = Install-LocalJdk21
}

$env:JAVA_HOME = $jdk21
$env:Path = "$env:JAVA_HOME\bin;$env:Path"

Write-Host "Using Java 21 JDK: $env:JAVA_HOME"
Write-Host "Java runtime:"
Invoke-VersionCommand (Join-Path $env:JAVA_HOME "bin\java.exe")

Write-Host "Java compiler:"
Invoke-VersionCommand (Join-Path $env:JAVA_HOME "bin\javac.exe")

python tools/check_catalog.py
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if (Test-Path ".\gradlew.bat") {
    & .\gradlew.bat buildPlugin
    exit $LASTEXITCODE
}

$installedGradle = Get-Command gradle -ErrorAction SilentlyContinue
if ($installedGradle) {
    & gradle buildPlugin
    exit $LASTEXITCODE
}

$gradleVersion = "9.1.0"
$tools = Join-Path $PSScriptRoot ".tools"
$gradleHome = Join-Path $tools "gradle-$gradleVersion"
$gradleExe = Join-Path $gradleHome "bin\gradle.bat"

if (-not (Test-Path $gradleExe)) {
    New-Item -ItemType Directory -Force -Path $tools | Out-Null
    $archive = Join-Path $tools "gradle-$gradleVersion-bin.zip"
    $url = "https://services.gradle.org/distributions/gradle-$gradleVersion-bin.zip"
    Write-Host "Gradle was not found. Downloading Gradle $gradleVersion locally..."
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
    Expand-Archive -Path $archive -DestinationPath $tools -Force
    Remove-Item $archive -Force
}

& $gradleExe buildPlugin
exit $LASTEXITCODE
