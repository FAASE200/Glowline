param(
    [string]$Version,
    [string]$PublishDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = if ($PublishDir) { $PublishDir } else { Join-Path $root 'publish' }
$installer = Join-Path $root 'installer'
$wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
if (-not (Test-Path $wix)) { $wix = 'wix' }

if (-not $Version) {
    [xml]$project = Get-Content (Join-Path $root 'LyricGlow.csproj')
    $Version = [string]@($project.Project.PropertyGroup.Version)[0]
}

$exe = Join-Path $publish 'LyricGlow.exe'
if (-not (Test-Path $exe)) { throw "Falta $exe; publica la app primero" }

$msi = Join-Path $installer "LyricGlow-$Version.msi"
$allUsersMsi = Join-Path $installer "LyricGlow-$Version-allusers.msi"
$setup = Join-Path $installer "LyricGlowSetup-$Version.exe"

& $wix build (Join-Path $installer 'LyricGlow.wxs') -arch x64 `
    -d "Version=$Version" -d "Scope=perUser" -d "ProjectDir=$root" -d "PublishDir=$publish" -o $msi
if ($LASTEXITCODE -ne 0) { throw 'WiX no pudo crear el MSI por usuario' }

& $wix build (Join-Path $installer 'LyricGlow.wxs') -arch x64 `
    -d "Version=$Version" -d "Scope=perMachine" -d "ProjectDir=$root" -d "PublishDir=$publish" -o $allUsersMsi
if ($LASTEXITCODE -ne 0) { throw 'WiX no pudo crear el MSI para todos los usuarios' }

& $wix build (Join-Path $installer 'LyricGlowBundle.wxs') -arch x64 `
    -ext WixToolset.BootstrapperApplications.wixext `
    -d "Version=$Version" -d "ProjectDir=$root" -d "MsiPath=$msi" -d "AllUsersMsiPath=$allUsersMsi" -o $setup
if ($LASTEXITCODE -ne 0) { throw 'WiX no pudo crear el instalador EXE' }

Copy-Item $msi (Join-Path $publish (Split-Path $msi -Leaf)) -Force
Copy-Item $setup (Join-Path $publish (Split-Path $setup -Leaf)) -Force

Write-Host "MSI: $msi"
Write-Host "MSI todos los usuarios: $allUsersMsi"
Write-Host "EXE: $setup"
