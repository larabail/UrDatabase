[CmdletBinding()]
param(
    [string] $OutputDirectory = "dist/store",
    [string] $SdkVersion = "10.0.26100.0"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw "MSIX packaging requires Windows, PowerShell 7 and the Windows SDK." }

function Invoke-Checked([string] $File, [string[]] $Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE." }
}

$makeAppx = Join-Path ${env:ProgramFiles(x86)} "Windows Kits/10/bin/$SdkVersion/x64/makeappx.exe"
$makePri = Join-Path ${env:ProgramFiles(x86)} "Windows Kits/10/bin/$SdkVersion/x64/makepri.exe"
if (-not (Test-Path -LiteralPath $makeAppx) -or -not (Test-Path -LiteralPath $makePri)) {
    throw "Install Windows SDK $SdkVersion (including MakeAppx and MakePri), or pass -SdkVersion with an installed SDK version."
}
$null = Get-Command dotnet -ErrorAction Stop
$null = Get-Command python -ErrorAction Stop

Push-Location (Join-Path $PSScriptRoot "..")
$scratch = $null
try {
    $version = & python tool/make_store_package.py version
    if ($LASTEXITCODE -ne 0) { throw "Cannot read the release version." }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    $package = Join-Path $output "UrDatabase-$version-store-x64.msix"
    if (Test-Path -LiteralPath $package) { throw "Refusing to replace an existing package: $package" }
    $scratch = Join-Path ([IO.Path]::GetTempPath()) ("urdb-msix-" + [Guid]::NewGuid().ToString("N"))
    $null = New-Item -ItemType Directory -Path $scratch
    $publish = Join-Path $scratch "publish"
    $layout = Join-Path $scratch "layout"

    # A fresh publish, never an existing developer output tree or the normal ZIP build.
    Invoke-Checked dotnet @(
        "publish", "src/UrDatabase.App/UrDatabase.App.csproj",
        "--configuration", "Release", "--runtime", "win-x64", "--self-contained", "true",
        "-p:DistributionChannel=MicrosoftStore", "-p:ContinuousIntegrationBuild=true",
        "-p:TmdbApiKey=$env:TMDB_API_KEY", "-p:OmdbApiKey=$env:OMDB_API_KEY",
        "-p:UrActorApiKey=$env:URACTOR_API_KEY", "--output", $publish
    )
    Invoke-Checked python @("tool/make_store_package.py", "stage", "--publish-dir", $publish, "--output", $layout)
    $priConfig = Join-Path $scratch "priconfig.xml"
    Invoke-Checked $makePri @("createconfig", "/cf", $priConfig, "/dq", "en-US")
    # Ship one MSIX, not a bundle of separate scale/language resource packages.
    [xml] $config = Get-Content -Raw -LiteralPath $priConfig
    $packaging = $config.SelectSingleNode("/resources/packaging")
    if ($null -ne $packaging) { $null = $packaging.ParentNode.RemoveChild($packaging) }
    $config.Save($priConfig)
    Invoke-Checked $makePri @("new", "/pr", $layout, "/cf", $priConfig, "/of", (Join-Path $layout "resources.pri"))
    $priDump = Join-Path $scratch "resources.xml"
    Invoke-Checked $makePri @("dump", "/if", (Join-Path $layout "resources.pri"), "/of", $priDump, "/dt", "detailed")
    Invoke-Checked python @("tool/make_store_package.py", "verify-resources", "--staged", $layout, "--dump", $priDump)
    $null = New-Item -ItemType Directory -Force -Path $output
    $temporaryPackage = Join-Path $scratch "upload.msix"
    # Keep semantic validation enabled. No /nv, SignTool, certificate or Partner Center upload.
    Invoke-Checked $makeAppx @("pack", "/d", $layout, "/p", $temporaryPackage, "/h", "SHA256")
    Invoke-Checked python @("tool/make_store_package.py", "verify", "--package", $temporaryPackage, "--staged", $layout)
    Move-Item -LiteralPath $temporaryPackage -Destination $package
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $package).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($package))" | Set-Content -LiteralPath "$package.sha256" -Encoding ascii
    Write-Host "Unsigned Store-upload package: $package"
    Write-Host "Not a signed sideload installer. Microsoft signs only after Store certification."
}
finally {
    if ($scratch -and (Test-Path -LiteralPath $scratch)) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
    Pop-Location
}
