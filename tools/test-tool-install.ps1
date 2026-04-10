[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$SkipPack
)

$ErrorActionPreference = "Stop"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Label,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action
    )

    Write-Host "==> $Label"
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "Step failed: $Label"
    }
}

function Write-StatusFrame {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,
        [Parameter(Mandatory = $true)]
        [ValidateSet("Green", "Red")]
        [string]$Color
    )

    $horizontal = [string]([char]0x2550) * ($Text.Length + 2)
    $topLeft = [char]0x2554
    $topRight = [char]0x2557
    $side = [char]0x2551
    $bottomLeft = [char]0x255A
    $bottomRight = [char]0x255D

    Write-Host ""
    Write-Host "$topLeft$horizontal$topRight" -ForegroundColor $Color
    Write-Host "$side $Text $side" -ForegroundColor $Color
    Write-Host "$bottomLeft$horizontal$bottomRight" -ForegroundColor $Color
}

function Get-ProjectVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    [xml]$projectXml = Get-Content -LiteralPath $ProjectPath
    $propertyGroups = @($projectXml.Project.PropertyGroup)

    $packageVersion = $propertyGroups |
        ForEach-Object { $_.PackageVersion } |
        Where-Object { $_ } |
        Select-Object -First 1
    if ($packageVersion) {
        return $packageVersion
    }

    $version = $propertyGroups |
        ForEach-Object { $_.Version } |
        Where-Object { $_ } |
        Select-Object -First 1
    if ($version) {
        return $version
    }

    throw "Could not determine package version from $ProjectPath"
}

$RepoRoot = Split-Path -Path $PSScriptRoot -Parent
$ArtifactsRoot = Join-Path $RepoRoot "artifacts"
$PackageOutput = Join-Path $RepoRoot "src\MemShack.Cli\nuget"
$SmokeRoot = Join-Path $ArtifactsRoot ("tool-smoke\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$ToolPath = Join-Path $SmokeRoot "tool-path"
$HomeRoot = Join-Path $SmokeRoot "home"
$ProjectRoot = Join-Path $SmokeRoot "project"
$PalacePath = Join-Path $SmokeRoot "palace"
$LocalRoot = Join-Path $SmokeRoot "local-manifest"
$ConfigRoot = Join-Path $HomeRoot ".mempalace"
$DotnetHome = Join-Path $RepoRoot "src\.dotnet"
$NuGetPackages = Join-Path $RepoRoot "src\.nuget\packages"
$ProjectPath = Join-Path $RepoRoot "src\MemShack.Cli\MemShack.Cli.csproj"
$Version = Get-ProjectVersion -ProjectPath $ProjectPath
$PackageId = "LoxSmoke.Mems"
$ToolCommand = "mems"
$ToolExe = Join-Path $ToolPath "mems.exe"
$BundledChromaReadme = Join-Path $RepoRoot "src\MemShack.Cli\chroma\win-x64\README.md"
$PackagePath = Join-Path $PackageOutput "$PackageId.$Version.nupkg"

try {
    New-Item -ItemType Directory -Force -Path $ArtifactsRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $PackageOutput | Out-Null
    New-Item -ItemType Directory -Force -Path $SmokeRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $ToolPath | Out-Null
    New-Item -ItemType Directory -Force -Path $HomeRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $ConfigRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $ProjectRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $LocalRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $DotnetHome | Out-Null
    New-Item -ItemType Directory -Force -Path $NuGetPackages | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $ProjectRoot "backend") | Out-Null

    $env:DOTNET_CLI_HOME = $DotnetHome
    $env:NUGET_PACKAGES = $NuGetPackages
    $env:HOME = $HomeRoot
    $env:USERPROFILE = $HomeRoot
    $homeDriveRoot = [System.IO.Path]::GetPathRoot($HomeRoot)
    $env:HOMEDRIVE = $homeDriveRoot.Substring(0, 2)
    $env:HOMEPATH = $HomeRoot.Substring(2)

    if (-not $SkipPack) {
        Invoke-Checked -Label "Pack tool package" -Action {
            dotnet pack $ProjectPath -c $Configuration
        }
    }

    if (-not (Test-Path -LiteralPath $PackagePath)) {
        throw "Expected package was not found: $PackagePath"
    }

    Set-Content -Path (Join-Path $ProjectRoot "backend\memory-sample.txt") -Value @"
JWT authentication protects the backend API.
JWT authentication protects the backend API.
JWT authentication protects the backend API.
JWT authentication protects the backend API.
JWT authentication protects the backend API.
JWT authentication protects the backend API.
JWT authentication protects the backend API.
JWT authentication protects the backend API.
"@

    Invoke-Checked -Label "Install tool with --tool-path" -Action {
        dotnet tool install $PackageId --tool-path $ToolPath --add-source $PackageOutput --version $Version --ignore-failed-sources
    }

    if (-not (Test-Path -LiteralPath $ToolExe)) {
        throw "Installed tool executable was not found: $ToolExe"
    }

    $helpText = [string]::Join("`n", (& $ToolExe --help 2>&1))
    if ($LASTEXITCODE -ne 0) {
        throw "mems --help failed"
    }

    if ($helpText -notmatch "mems init <dir>") {
        throw "Installed tool help output did not mention the mems command."
    }

    $bundledChromaPath = [string]::Join("`n", (& $ToolExe __where-chroma 2>&1)).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "mems __where-chroma failed"
    }

    if ($bundledChromaPath -notlike "*\chroma\win-x64\chroma.exe") {
        throw "Installed tool did not report the expected bundled Chroma path: $bundledChromaPath"
    }

    $bundledChromaDirectory = Split-Path -Parent $bundledChromaPath
    if (-not (Test-Path -LiteralPath $bundledChromaDirectory)) {
        throw "Installed tool Chroma directory was not found: $bundledChromaDirectory"
    }

    $installedChromaReadme = Join-Path $bundledChromaDirectory "README.md"
    if (-not (Test-Path -LiteralPath $installedChromaReadme)) {
        throw "Installed tool did not include the bundled Chroma README: $installedChromaReadme"
    }

    if (-not (Test-Path -LiteralPath $BundledChromaReadme)) {
        throw "Expected bundled Chroma README was not found in the source tree: $BundledChromaReadme"
    }

    Invoke-Checked -Label "Run mems init" -Action {
        & $ToolExe init $ProjectRoot --yes
    }

    Set-Content -Path (Join-Path $ConfigRoot "config.json") -Value @"
{
  "vector_store_backend": "compatibility"
}
"@

    Invoke-Checked -Label "Run mems mine" -Action {
        & $ToolExe --palace $PalacePath mine $ProjectRoot
    }

    $statusText = [string]::Join("`n", (& $ToolExe --palace $PalacePath status 2>&1))
    if ($LASTEXITCODE -ne 0) {
        throw "mems status failed"
    }

    if ($statusText -notmatch "WING: project") {
        throw "Installed tool status output did not include the expected wing."
    }

    $searchText = [string]::Join("`n", (& $ToolExe --palace $PalacePath search "JWT authentication" 2>&1))
    if ($LASTEXITCODE -ne 0) {
        throw "mems search failed"
    }

    if ($searchText -notlike '*Results for: "JWT authentication"*') {
        throw "Installed tool search output did not include the expected result header."
    }

    Push-Location $LocalRoot
    try {
        Invoke-Checked -Label "Create local tool manifest" -Action {
            dotnet new tool-manifest
        }

        Invoke-Checked -Label "Install local tool" -Action {
            dotnet tool install --local $PackageId --add-source $PackageOutput --version $Version --ignore-failed-sources
        }

        $localList = [string]::Join("`n", (dotnet tool list --local))
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet tool list --local failed"
        }

        if ($localList -notmatch $PackageId -or $localList -notmatch $ToolCommand) {
            throw "Local tool list did not contain the packaged tool."
        }

        Invoke-Checked -Label "Run local tool" -Action {
            dotnet tool run $ToolCommand -- --help
        }

        Invoke-Checked -Label "Update local tool" -Action {
            dotnet tool update --local $PackageId --add-source $PackageOutput --version $Version --ignore-failed-sources
        }

        Invoke-Checked -Label "Uninstall local tool" -Action {
            dotnet tool uninstall --local $PackageId
        }
    }
    finally {
        Pop-Location
    }

    Write-StatusFrame -Text "Tool packaging smoke tests passed" -Color Green
    Write-Host "Artifacts:"
    Write-Host "  Package: $PackagePath"
    Write-Host "  Smoke root: $SmokeRoot"
}
catch {
    $failureMessage = if ($_.Exception -and $_.Exception.Message) {
        $_.Exception.Message
    }
    else {
        $_.ToString()
    }

    Write-StatusFrame -Text "Tool packaging smoke tests failed: $failureMessage" -Color Red
    exit 1
}
