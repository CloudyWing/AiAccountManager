[CmdletBinding()]
param(
    [string]$ReleaseDirectory = (Join-Path $PSScriptRoot '..\artifacts\releases'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\winget'),
    [string]$TemplateDirectory = (Join-Path $PSScriptRoot '..\packaging\winget'),
    [string]$BaseUrl = 'https://github.com/CloudyWing/AiAccountManager/releases/download',
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$manifestPath = Join-Path $ReleaseDirectory 'release-artifacts.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Release artifacts manifest not found: $manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schema -cne 'ai-account-manager.release-artifacts.v1' -or $manifest.packId -cne 'ai-account-manager') {
    throw 'Release artifacts schema or packId is invalid.'
}
$releaseVersion = [string]$manifest.version
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Invalid release version: $releaseVersion"
}
if ($Version -and $Version -cne $releaseVersion) {
    throw "Requested version '$Version' does not match '$releaseVersion'."
}
$installers = @($manifest.files | Where-Object { $_.name -like '*-Setup.exe' })
if ($installers.Count -ne 1) {
    throw "Expected exactly one Setup.exe entry; found $($installers.Count)."
}
$installer = $installers[0]
$name = [string]$installer.name
if ([System.IO.Path]::GetFileName($name) -cne $name) {
    throw 'Installer name must be a file name.'
}
$hash = [string]$installer.sha256
$installerPath = Join-Path $ReleaseDirectory $name
if ($hash -notmatch '^[0-9a-fA-F]{64}$' -or -not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw 'Installer file or SHA-256 is invalid.'
}
if ((Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash -ine $hash) {
    throw "Installer SHA-256 does not match: $name"
}

$values = @{
    '{{PackageVersion}}' = $releaseVersion
    '{{InstallerUrl}}' = "$($BaseUrl.TrimEnd('/'))/v$releaseVersion/$name"
    '{{InstallerSha256}}' = $hash.ToUpperInvariant()
    '{{ReleaseDate}}' = [DateTime]::UtcNow.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
}
$templateNames = @(
    'CloudyWing.AiAccountManager.yaml',
    'CloudyWing.AiAccountManager.installer.yaml',
    'CloudyWing.AiAccountManager.locale.zh-TW.yaml',
    'CloudyWing.AiAccountManager.locale.en-US.yaml'
)
$stage = Join-Path $OutputDirectory ('.stage-' + [guid]::NewGuid().ToString('N'))
$backup = Join-Path $OutputDirectory ('.backup-' + [guid]::NewGuid().ToString('N'))
$destination = Join-Path $OutputDirectory $releaseVersion
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$backedUp = $false
try {
    foreach ($templateName in $templateNames) {
        $content = [System.IO.File]::ReadAllText((Join-Path $TemplateDirectory $templateName))
        foreach ($key in $values.Keys) {
            $content = $content.Replace($key, $values[$key])
        }
        if ($content.Contains('{{')) {
            throw "Unresolved placeholder in $templateName."
        }
        [System.IO.File]::WriteAllText((Join-Path $stage $templateName), $content, [System.Text.UTF8Encoding]::new($false))
    }
    if (Test-Path -LiteralPath $destination) {
        [System.IO.Directory]::Move($destination, $backup)
        $backedUp = $true
    }
    try {
        [System.IO.Directory]::Move($stage, $destination)
    } catch {
        if ($backedUp) {
            [System.IO.Directory]::Move($backup, $destination)
            $backedUp = $false
        }
        throw
    }
    if ($backedUp) {
        Remove-Item -LiteralPath $backup -Recurse -Force
    }
    Write-Output "WinGet manifest created: $destination"
} finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
exit 0
