param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    [xml]$project = Get-Content (Join-Path $root 'LyricGlow.csproj')
    $Version = [string]@($project.Project.PropertyGroup.Version)[0]
}

$publishTokenPath = Join-Path $root '.secrets\publish.token'
$tokenPath = if (Test-Path $publishTokenPath) { $publishTokenPath } else { Join-Path $root '.secrets\update.token' }
if (-not (Test-Path $tokenPath)) { throw "Falta $tokenPath" }
$token = (Get-Content $tokenPath -Raw).Trim()
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$headers = @{
    Authorization = "Bearer $token"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
}

$tag = "v$Version"
$rel = Invoke-RestMethod -Uri "https://api.github.com/repos/FAASE200/Glowline/releases/tags/$tag" -Headers $headers
Write-Host "Release $tag (id $($rel.id)); assets: $(($rel.assets | ForEach-Object name) -join ', ')"

foreach ($name in @("LyricGlow-$Version.msi", "LyricGlowSetup-$Version.exe")) {
    if ($rel.assets.name -contains $name) {
        Write-Host "Ya existe: $name"
        continue
    }
    $file = Join-Path $root "publish\$name"
    if (-not (Test-Path $file)) { throw "Falta $file; ejecuta tools\build-installers.ps1" }
    $uri = "https://uploads.github.com/repos/FAASE200/Glowline/releases/$($rel.id)/assets?name=$name"
    $resp = Invoke-RestMethod -Method Post -Uri $uri -Headers $headers -InFile $file -ContentType 'application/octet-stream' -TimeoutSec 900
    Write-Host "Subido: $($resp.name) | $($resp.size) bytes | $($resp.digest)"
}
