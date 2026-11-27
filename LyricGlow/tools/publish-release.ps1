param(
    [string]$Version,
    [string]$PublishDir,
    [string]$PortableExe,
    [string]$PortableAssetName,
    [string]$AndroidApk,
    [string]$AndroidAssetName,
    [string]$NotesPath,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot
$publishDir = if ($PublishDir) { $PublishDir } else { Join-Path $root 'publish' }
$bridge = Join-Path $env:USERPROFILE '.qoder\mcp\github-mcp'
$owner = 'FAASE200'
$repo = 'Glowline'

if (-not $Version) {
    [xml]$project = Get-Content (Join-Path $root 'LyricGlow.csproj')
    $Version = [string]@($project.Project.PropertyGroup.Version)[0]
}
$tag = "v$Version"
# Releases before 1.0.0 are titled "{Version} Alpha"; later ones keep the product name.
$releaseName = if (([version]$Version).Major -lt 1) { "$tag Alpha" } else { "Glowline $tag" }
if (-not $AndroidApk) {
    $debugApk = Join-Path $publishDir "LyricGlow-$Version-android-universal-debug.apk"
    $plainApk = Join-Path $publishDir "LyricGlow-$Version-android-universal.apk"
    $AndroidApk = if (Test-Path -LiteralPath $debugApk) { $debugApk }
                  elseif (Test-Path -LiteralPath $plainApk) { $plainApk }
                  else { $debugApk }
}
$androidName = if ($AndroidAssetName) { $AndroidAssetName } else { "Glowline-$Version-android-unstable.apk" }

if (-not $PortableExe) {
    $portableCandidates = @(
        (Join-Path $publishDir "Glowline-$Version-final.exe"),
        (Join-Path $publishDir "Glowline-$Version.exe"),
        (Join-Path $publishDir "LyricGlow.exe")
    )
    $PortableExe = $portableCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
$portableName = if ($PortableAssetName) { $PortableAssetName } else { "Glowline-$Version-portable.exe" }

function Get-PublishToken {
    $plain = Join-Path $root '.secrets\publish.token'
    if (Test-Path -LiteralPath $plain) {
        $token = (Get-Content -LiteralPath $plain -Raw).Trim()
        if ($token) { return @{ Token = $token; Source = '.secrets/publish.token' } }
    }
    $blob = Join-Path $bridge 'token.dat'
    if (Test-Path -LiteralPath $blob) {
        Add-Type -AssemblyName System.Security
        try {
            $enc = [IO.File]::ReadAllBytes($blob)
            $dec = [Security.Cryptography.ProtectedData]::Unprotect(
                $enc, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
            $token = [Text.Encoding]::UTF8.GetString($dec).Trim()
        } catch {
            throw 'No se pudo descifrar token.dat con este usuario de Windows. Ejecuta setup.cmd.'
        }
        if ($token) { return @{ Token = $token; Source = 'token cifrado del MCP GitHub (DPAPI)' } }
    }
    throw 'No hay token. Ejecuta el setup.cmd del bridge de GitHub MCP o crea .secrets/publish.token.'
}

function Show-GitHubError($err, [string]$during) {
    $response = $err.Exception.Response
    $detail = $err.Exception.Message
    $status = 0
    if ($response) {
        $status = [int]$response.StatusCode
        try {
            $reader = New-Object IO.StreamReader($response.GetResponseStream())
            $detail = $reader.ReadToEnd()
        } catch { }
    }
    Write-Host "Fallo $during (HTTP $status): $detail"
    switch ($status) {
        401 { Write-Host 'El token no es valido o expiro. Ejecuta setup.cmd otra vez.' }
        403 { Write-Host 'El token necesita Contents: Read and write sobre el repositorio FAASE200/Glowline.' }
        404 { Write-Host 'El token no ve el repositorio. En el PAT fine-grained elige Only select repositories y marca Glowline.' }
    }
}

$credentials = Get-PublishToken
$token = $credentials.Token
$headers = @{
    Authorization = "Bearer $token"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'LyricGlow-Updater'
}

$me = Invoke-RestMethod -Uri 'https://api.github.com/user' -Headers $headers -TimeoutSec 60
Write-Host "Token: $($me.login) [$($credentials.Source)]"

$repoInfo = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo" -Headers $headers -TimeoutSec 60
Write-Host "Repositorio: $($repoInfo.full_name) (privado: $($repoInfo.private))"
if ($repoInfo.permissions) {
    Write-Host "Permisos visibles: push=$($repoInfo.permissions.push) admin=$($repoInfo.permissions.admin)"
}

$assetsToUpload = @(
    [pscustomobject]@{ Name = "LyricGlowSetup-$Version.exe"; Path = (Join-Path $publishDir "LyricGlowSetup-$Version.exe"); Platform = 'windows-x64' }
    [pscustomobject]@{ Name = $portableName; Path = $PortableExe; Platform = 'windows-x64-portable' }
    [pscustomobject]@{ Name = $androidName; Path = $AndroidApk; Platform = 'android-unstable' }
)
foreach ($asset in $assetsToUpload) {
    if (-not $asset.Path -or -not (Test-Path -LiteralPath $asset.Path)) {
        throw "Falta $($asset.Path). Ejecuta dotnet publish y tools\build-installers.ps1."
    }
    $asset | Add-Member -NotePropertyName Size -NotePropertyValue (Get-Item -LiteralPath $asset.Path).Length
    $asset | Add-Member -NotePropertyName Sha256 -NotePropertyValue (Get-FileHash -LiteralPath $asset.Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$release = $null
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo/releases/tags/$tag" -Headers $headers -TimeoutSec 60
} catch {
    $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    if ($status -ne 404) { Show-GitHubError $_ "buscando la release $tag"; throw }
}

if ($release) {
    Write-Host "Release $tag existente (id $($release.id), borrador: $($release.draft))"
} else {
    Write-Host "Release $tag no existe."
}

$existingByName = @{}
if ($release) {
    foreach ($asset in @($release.assets)) { $existingByName[$asset.name] = $asset }
}
$missing = @($assetsToUpload | Where-Object { -not $existingByName.ContainsKey($_.Name) })
foreach ($asset in $assetsToUpload) {
    $remote = $existingByName[$asset.Name]
    if ($remote -and [long]$remote.size -ne [long]$asset.Size) {
        throw "El asset $($asset.Name) ya existe con otro tamano; no se sobrescribira."
    }
}

Write-Host "Assets presentes: $((@($existingByName.Keys) | Sort-Object) -join ', ')"
Write-Host "Assets a subir: $(if ($missing.Count) { ($missing | ForEach-Object Name) -join ', ' } else { 'ninguno' })"
foreach ($asset in $assetsToUpload) {
    Write-Host ("  {0} | {1:N0} bytes | sha256:{2}" -f $asset.Name, $asset.Size, $asset.Sha256)
}

if ($DryRun) {
    Write-Host 'DryRun: no se creo la release ni se subio ningun archivo.'
    return
}

if (-not $release) {
    $bodyText = if ($NotesPath) {
        [string](Get-Content -LiteralPath $NotesPath -Raw -Encoding UTF8)
    } else {
@"
Glowline $Version installer.

- Windows x64 installer only.
- The Android APK will be added in a later release.
"@
    }
    $json = @{
        tag_name = $tag
        name = $releaseName
        body = $bodyText
        draft = $true
        prerelease = $false
    } | ConvertTo-Json -Compress
    try {
        $release = Invoke-RestMethod -Method Post `
            -Uri "https://api.github.com/repos/$owner/$repo/releases" `
            -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($json)) `
            -ContentType 'application/json; charset=utf-8' -TimeoutSec 120
        Write-Host "Borrador creado: $($release.html_url)"
    } catch {
        Show-GitHubError $_ "creando la release $tag"
        throw
    }
}

foreach ($asset in $missing) {
    Write-Host ("Subiendo {0} ({1:N0} bytes)..." -f $asset.Name, $asset.Size)
    try {
        $uploaded = Invoke-RestMethod -Method Post `
            -Uri "https://uploads.github.com/repos/$owner/$repo/releases/$($release.id)/assets?name=$([uri]::EscapeDataString($asset.Name))" `
            -Headers $headers -InFile $asset.Path -ContentType 'application/octet-stream' -TimeoutSec 1800
        Write-Host ("  OK {0} | {1:N0} bytes | {2}" -f $uploaded.name, $uploaded.size, $uploaded.digest)
    } catch {
        Show-GitHubError $_ "subiendo $($asset.Name)"
        throw
    }
}

$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo/releases/$($release.id)" -Headers $headers -TimeoutSec 60
foreach ($asset in $assetsToUpload) {
    $remote = @($release.assets) | Where-Object { $_.name -eq $asset.Name } | Select-Object -First 1
    if (-not $remote) { throw "La release no contiene $($asset.Name) despues de subir." }
    if ([long]$remote.size -ne [long]$asset.Size) {
        throw "Tamano distinto para $($asset.Name): local $($asset.Size), GitHub $($remote.size)."
    }
    Write-Host ("Verificado: {0} | {1:N0} bytes | {2}" -f $remote.name, $remote.size, $remote.digest)
}

if ($release.draft) {
    $patch = @{ draft = $false } | ConvertTo-Json -Compress
    $release = Invoke-RestMethod -Method Patch `
        -Uri "https://api.github.com/repos/$owner/$repo/releases/$($release.id)" `
        -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($patch)) `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 120
    Write-Host "Release publicada: $($release.html_url)"
} else {
    Write-Host "Release ya publicada: $($release.html_url)"
}
