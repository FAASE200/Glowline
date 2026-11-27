param(
    [switch]$SkipBuild,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
if ($DryRun) { $SkipBuild = $true }

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'Glowline.csproj'
$publishDir = Join-Path $root 'publish'
$installerScript = Join-Path $PSScriptRoot 'build-installers.ps1'
$publishToken = Join-Path $root '.secrets\publish.token'
$updateToken = Join-Path $root '.secrets\update.token'

function Get-PublishToken {
    if (Test-Path -LiteralPath $publishToken) {
        $value = (Get-Content -LiteralPath $publishToken -Raw).Trim()
        if ($value) { return @{ Token = $value; Source = '.secrets/publish.token' } }
    }

    $blob = Join-Path $env:USERPROFILE '.qoder\mcp\github-mcp\token.dat'
    if (Test-Path -LiteralPath $blob) {
        Add-Type -AssemblyName System.Security
        try {
            $enc = [IO.File]::ReadAllBytes($blob)
            $dec = [Security.Cryptography.ProtectedData]::Unprotect(
                $enc, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
            $value = [Text.Encoding]::UTF8.GetString($dec).Trim()
            if ($value) { return @{ Token = $value; Source = 'MCP GitHub (DPAPI)' } }
        } catch {
            throw 'No se pudo descifrar token.dat con este usuario de Windows.'
        }
    }

    if (Test-Path -LiteralPath $updateToken) {
        $value = (Get-Content -LiteralPath $updateToken -Raw).Trim()
        if ($value) { return @{ Token = $value; Source = '.secrets/update.token' } }
    }

    throw 'No hay token. Crea .secrets/publish.token o configura el bridge de GitHub MCP.'
}

$credentials = Get-PublishToken
$token = $credentials.Token
Write-Host "Token: $($credentials.Source)"

[xml]$xml = Get-Content $project
$version = [string]@($xml.Project.PropertyGroup.Version)[0]
$tag = "v$version"
# Releases before 1.0.0 are titled "{Version} Alpha"; later ones keep the product name.
$releaseName = if (([version]$version).Major -lt 1) { "$tag Alpha" } else { "Glowline $tag" }

if (-not $SkipBuild) {
    & dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'La publicación portable falló' }

    & $installerScript -Version $version -PublishDir $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo construir el instalador' }
}

$setup = Join-Path $publishDir "GlowlineSetup-$version.exe"
if (-not (Test-Path -LiteralPath $setup)) {
    throw "Falta $setup; compila el instalador primero"
}
$assetName = Split-Path $setup -Leaf
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $setup).Length
Write-Host "Versión: $version ($tag)"
Write-Host "Instalador: $assetName ($size bytes)"
Write-Host "SHA-256: $hash"
if ($DryRun) {
    Write-Host 'DryRun: no se creó ninguna release'
    return
}

$configFile = [IO.Path]::GetTempFileName()
$bodyFile = [IO.Path]::GetTempFileName()
try {
    @(
        "header = `"Authorization: Bearer $token`"",
        'header = "Accept: application/vnd.github+json"',
        'header = "X-GitHub-Api-Version: 2022-11-28"',
        'header = "User-Agent: Glowline-Updater"'
    ) | Set-Content -Path $configFile -Encoding ASCII

    $notes = @"
Glowline $version Alpha

Windows x64 installer. This release starts the Glowline rebrand and moves updates to the public repository.

- Renamed the visible app to Glowline.
- Removed the Visualizer and the design editor.
- Default interface palette is now black and white; album-art palette remains optional.
- Public auto-updates now download and open this installer.
- No portable EXE or Android APK is attached.
"@
    $body = @{
        tag_name = $tag
        target_commitish = 'main'
        name = $releaseName
        body = $notes
        draft = $false
        prerelease = $false
    } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($bodyFile, $body, [Text.UTF8Encoding]::new($false))

    $releaseJson = & curl.exe -sS --config $configFile -X POST `
        -H 'Content-Type: application/json' `
        --data-binary "@$bodyFile" `
        'https://api.github.com/repos/FAASE200/Glowline/releases'
    $release = $releaseJson | ConvertFrom-Json
    if (-not $release.id) { throw "No se pudo crear la release: $releaseJson" }

    $assetJson = & curl.exe -sS --config $configFile -X POST `
        -H 'Content-Type: application/octet-stream' `
        --data-binary "@$setup" `
        "https://uploads.github.com/repos/FAASE200/Glowline/releases/$($release.id)/assets?name=$([uri]::EscapeDataString($assetName))"
    $asset = $assetJson | ConvertFrom-Json
    if (-not $asset.id) { throw "No se pudo subir el instalador: $assetJson" }

    Write-Host "Release $tag publicada: $($release.html_url)"
    Write-Host "Asset: $($asset.name) · $($asset.size) bytes · $($asset.digest)"
}
finally {
    Remove-Item $configFile, $bodyFile -ErrorAction SilentlyContinue
}
