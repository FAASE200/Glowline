param(
    [switch]$SkipBuild,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
if ($DryRun) { $SkipBuild = $true }

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'LyricGlow.csproj'
$publishToken = Join-Path $root '.secrets\publish.token'
$updateToken = Join-Path $root '.secrets\update.token'
$tokenFile = if (Test-Path $publishToken) { $publishToken } else { $updateToken }
$exe = Join-Path $root 'publish\LyricGlow.exe'
$publishDir = Join-Path $root 'publish'

if (-not (Test-Path $tokenFile)) { throw "Falta $publishToken o $updateToken" }
$token = (Get-Content $tokenFile -Raw).Trim()
if ($token.Length -eq 0) { throw 'El token está vacío' }

[xml]$xml = Get-Content $project
$version = [string]@($xml.Project.PropertyGroup.Version)[0]
$tag = "v$version"
# Releases before 1.0.0 are titled "{Version} Alpha"; later ones keep the product name.
$releaseName = if (([version]$version).Major -lt 1) { "$tag Alpha" } else { "Glowline $tag" }

if (-not $SkipBuild) {
    Get-Process LyricGlow -ErrorAction SilentlyContinue | Stop-Process
    & dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'La publicación falló' }
}

if (-not (Test-Path $exe)) { throw "No existe $exe" }
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $exe).Length
Write-Host "Versión: $version ($tag)"
Write-Host "SHA-256: $hash"
if ($DryRun) {
    Write-Host 'DryRun: no se creó ninguna release'
    return
}

$configFile = [IO.Path]::GetTempFileName()
$bodyFile = [IO.Path]::GetTempFileName()
$manifestFile = [IO.Path]::GetTempFileName()
try {
    @(
        "header = `"Authorization: Bearer $token`"",
        'header = "Accept: application/vnd.github+json"',
        'header = "X-GitHub-Api-Version: 2022-11-28"',
        'header = "User-Agent: LyricGlow-Updater"'
    ) | Set-Content -Path $configFile -Encoding ASCII

    $body = @{
        tag_name = $tag
        target_commitish = 'main'
        name = $releaseName
        body = "Glowline $version update."
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
        --data-binary "@$exe" `
        "https://uploads.github.com/repos/FAASE200/Glowline/releases/$($release.id)/assets?name=LyricGlow.exe"
    $asset = $assetJson | ConvertFrom-Json
    if (-not $asset.id) { throw "No se pudo subir el exe: $assetJson" }

    $manifest = [ordered]@{
        schemaVersion = 1
        product = 'LyricGlow'
        version = $version
        tag = $tag
        assets = @([pscustomobject]@{
            Name = 'LyricGlow.exe'
            Platform = 'windows-x64'
            Size = $size
            Sha256 = $hash
        })
    }
    [IO.File]::WriteAllText($manifestFile, ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $manifestJson = & curl.exe -sS --config $configFile -X POST `
        -H 'Content-Type: application/octet-stream' `
        --data-binary "@$manifestFile" `
        "https://uploads.github.com/repos/FAASE200/Glowline/releases/$($release.id)/assets?name=manifest.json"
    $manifestAsset = $manifestJson | ConvertFrom-Json
    if (-not $manifestAsset.id) { throw "No se pudo subir manifest.json: $manifestJson" }

    Write-Host "Release $tag publicada: $($release.html_url)"
    Write-Host "Asset: $($asset.name) · $($asset.size) bytes · $($asset.digest)"
    Write-Host "Manifest: $($manifestAsset.name) · $($manifestAsset.size) bytes"
}
finally {
    Remove-Item $configFile, $bodyFile, $manifestFile -ErrorAction SilentlyContinue
}
