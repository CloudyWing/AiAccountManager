[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0',

    [Parameter()]
    [string]$AppServerZipPath,

    [Parameter()]
    [string]$VpkToolPath,

    [Parameter()]
    [string]$NugetPackagesPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $repositoryRoot 'artifacts'
$releaseDirectory = Join-Path $artifactRoot 'releases'
$outputDirectory = Join-Path $repositoryRoot 'outputs\first-release'
$nugetPackages = $null
$appServerVersion = 'rust-v0.162.1'
$appServerArchiveSha256 = '7E4B9F8D0622BDF946B5D8BFE6DDE873F90F5C8FDF69E450489FD4F52B7FFCEA'
$appServerExecutableSha256 = '9526F00E454EDBA5942822D89674C9857F18D7322F788F3C75D2256F692DEB46'
$appServerFileName = 'codex-app-server-x86_64-pc-windows-msvc.exe'

if ([string]::IsNullOrWhiteSpace($AppServerZipPath)) {
    $AppServerZipPath = Join-Path $repositoryRoot '.local\cache\codex-app-server\rust-v0.162.1\codex-app-server-x86_64-pc-windows-msvc.exe.zip'
}
if ([string]::IsNullOrWhiteSpace($NugetPackagesPath)) {
    if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        $NugetPackagesPath = Join-Path $repositoryRoot '.local\packages'
    } else {
        $NugetPackagesPath = $env:NUGET_PACKAGES
    }
}
$nugetPackages = [System.IO.Path]::GetFullPath($NugetPackagesPath)

# 未指定 vpk 路徑時，使用 .config\dotnet-tools.json 宣告的 local tool。
$useLocalVpk = [string]::IsNullOrWhiteSpace($VpkToolPath)
$toolManifestPath = Join-Path $repositoryRoot '.config\dotnet-tools.json'

$AppServerZipPath = [System.IO.Path]::GetFullPath($AppServerZipPath)
$projectPath = Join-Path $repositoryRoot 'src\AiAccountManager.Desktop\AiAccountManager.Desktop.csproj'
if (-not (Test-Path -LiteralPath $AppServerZipPath -PathType Leaf)) {
    throw "Codex app-server archive was not found: $AppServerZipPath"
}
if ($useLocalVpk) {
    if (-not (Test-Path -LiteralPath $toolManifestPath -PathType Leaf)) {
        throw "Tool manifest was not found: $toolManifestPath"
    }
} else {
    $VpkToolPath = [System.IO.Path]::GetFullPath($VpkToolPath)
    if (-not (Test-Path -LiteralPath $VpkToolPath -PathType Leaf)) {
        throw "Velopack CLI was not found: $VpkToolPath"
    }
}
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Desktop project file was not found: $projectPath"
}

function Assert-PathContained {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Parent
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside the owned output directory: $fullPath"
    }
}

function Remove-OwnedDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Parent
    )

    Assert-PathContained -Path $Path -Parent $Parent
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function ConvertTo-CommandText {
    param([string[]]$Parts)

    return (($Parts | ForEach-Object {
        if ($_ -match '[\s"]') {
            '"' + $_.Replace('"', '\"') + '"'
        } else {
            $_
        }
    }) -join ' ')
}

function Invoke-LoggedProcess {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$FileName,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$WorkingDirectory
    )

    $processStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processStartInfo.FileName = $FileName
    $processStartInfo.WorkingDirectory = [System.IO.Path]::GetFullPath($WorkingDirectory)
    $processStartInfo.UseShellExecute = $false
    $processStartInfo.RedirectStandardOutput = $true
    $processStartInfo.RedirectStandardError = $true
    $processStartInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        $processStartInfo.ArgumentList.Add($argument)
    }

    $processStartInfo.Environment['NUGET_PACKAGES'] = $nugetPackages
    $processStartInfo.Environment['DOTNET_CLI_HOME'] = $dotnetHome
    $processStartInfo.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $processStartInfo.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $processStartInfo.Environment['DOTNET_NOLOGO'] = '1'

    $startedAtUtc = [DateTimeOffset]::UtcNow
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $processStartInfo
    if (-not $process.Start()) {
        throw "Could not start process: $FileName"
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $endedAtUtc = [DateTimeOffset]::UtcNow

    $stdoutPath = Join-Path $logDirectory ($Name + '.stdout.log')
    $stderrPath = Join-Path $logDirectory ($Name + '.stderr.log')
    [System.IO.File]::WriteAllText($stdoutPath, $stdout, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($stderrPath, $stderr, [System.Text.UTF8Encoding]::new($false))
    $commandParts = @($FileName) + $Arguments
    $result = [ordered]@{
        name = $Name
        command = ConvertTo-CommandText -Parts $commandParts
        workingDirectory = $processStartInfo.WorkingDirectory
        startedAtUtc = $startedAtUtc.ToString('O')
        endedAtUtc = $endedAtUtc.ToString('O')
        exitCode = $process.ExitCode
        stdoutFile = $stdoutPath
        stderrFile = $stderrPath
        stdoutSha256 = (Get-FileHash -LiteralPath $stdoutPath -Algorithm SHA256).Hash.ToLowerInvariant()
        stderrSha256 = (Get-FileHash -LiteralPath $stderrPath -Algorithm SHA256).Hash.ToLowerInvariant()
        environment = [ordered]@{
            NUGET_PACKAGES = $processStartInfo.Environment['NUGET_PACKAGES']
            DOTNET_CLI_HOME = $processStartInfo.Environment['DOTNET_CLI_HOME']
            DOTNET_CLI_TELEMETRY_OPTOUT = $processStartInfo.Environment['DOTNET_CLI_TELEMETRY_OPTOUT']
            DOTNET_SKIP_FIRST_TIME_EXPERIENCE = $processStartInfo.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE']
            DOTNET_NOLOGO = $processStartInfo.Environment['DOTNET_NOLOGO']
        }
    }
    $resultPath = Join-Path $logDirectory ($Name + '.json')
    $temporaryResultPath = $resultPath + '.tmp'
    [System.IO.File]::WriteAllText(
        $temporaryResultPath,
        ($result | ConvertTo-Json -Depth 8),
        [System.Text.UTF8Encoding]::new($false)
    )
    Move-Item -LiteralPath $temporaryResultPath -Destination $resultPath -Force
    $process.Dispose()

    if ($result.exitCode -ne 0) {
        throw "Process '$Name' failed with exit code $($result.exitCode). See $stdoutPath and $stderrPath."
    }

    Write-Output ($result | ConvertTo-Json -Depth 8)
}

$archiveHash = (Get-FileHash -LiteralPath $AppServerZipPath -Algorithm SHA256).Hash.ToUpperInvariant()
if (-not [string]::Equals($archiveHash, $appServerArchiveSha256, [System.StringComparison]::Ordinal)) {
    throw "Codex app-server archive SHA-256 does not match the pinned release."
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($AppServerZipPath)
try {
    if ($archive.Entries.Count -ne 1 -or $archive.Entries[0].FullName -cne $appServerFileName) {
        throw "Codex app-server archive must contain only '$appServerFileName'."
    }
} finally {
    $archive.Dispose()
}

$stageId = [Guid]::NewGuid().ToString('N')
$stageRoot = Join-Path $artifactRoot ('.package-stage-' + $stageId)
$publishDirectory = Join-Path $stageRoot 'publish'
$setupDirectory = Join-Path $stageRoot 'releases'
$outputParent = Join-Path $repositoryRoot 'outputs'
$outputStageDirectory = Join-Path $outputParent ('.first-release-stage-' + $stageId)
$releaseBackupDirectory = Join-Path $artifactRoot ('.releases-backup-' + $stageId)
$outputBackupDirectory = Join-Path $outputParent ('.first-release-backup-' + $stageId)
$logDirectory = Join-Path $artifactRoot 'package-logs'
$dotnetHome = Join-Path $repositoryRoot '.local\dotnet-home'
$releaseBackedUp = $false
$outputBackedUp = $false
$releasePromoted = $false
$outputPromoted = $false
$promotionCommitted = $false

New-Item -ItemType Directory -Path $artifactRoot, $logDirectory, $dotnetHome, $publishDirectory, $setupDirectory, $outputStageDirectory -Force | Out-Null

if ($useLocalVpk) {
    $toolRestoreArguments = @('tool', 'restore', '--tool-manifest', $toolManifestPath)
    Invoke-LoggedProcess -Name 'tool-restore' -FileName 'dotnet' -Arguments $toolRestoreArguments -WorkingDirectory $repositoryRoot | Out-Host
}

$publishArguments = @(
    'publish',
    '.\src\AiAccountManager.Desktop\AiAccountManager.Desktop.csproj',
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--no-build',
    '--no-restore',
    '--output', $publishDirectory
)
Invoke-LoggedProcess -Name 'publish' -FileName 'dotnet' -Arguments $publishArguments -WorkingDirectory $repositoryRoot | Out-Host

$binaryDirectory = Join-Path $publishDirectory 'tools\codex-app-server\0.162.1'
New-Item -ItemType Directory -Path $binaryDirectory -Force | Out-Null
$binaryPath = Join-Path $binaryDirectory $appServerFileName
$archive = [System.IO.Compression.ZipFile]::OpenRead($AppServerZipPath)
try {
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($archive.Entries[0], $binaryPath, $true)
} finally {
    $archive.Dispose()
}
$executableHash = (Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256).Hash.ToUpperInvariant()
if (-not [string]::Equals($executableHash, $appServerExecutableSha256, [System.StringComparison]::Ordinal)) {
    throw "Codex app-server executable SHA-256 does not match the pinned release."
}

$binaryManifest = [ordered]@{
    schema = 'ai-account-manager.codex-app-server-binary.v1'
    version = $appServerVersion
    archiveSha256 = $archiveHash
    executableSha256 = $executableHash
}
$binaryManifestPath = Join-Path $binaryDirectory 'codex-app-server.manifest.json'
[System.IO.File]::WriteAllText(
    $binaryManifestPath,
    ($binaryManifest | ConvertTo-Json -Depth 5),
    [System.Text.UTF8Encoding]::new($false)
)

$noticeSource = Join-Path $repositoryRoot 'packaging\THIRD-PARTY-NOTICES.md'
if (-not (Test-Path -LiteralPath $noticeSource -PathType Leaf)) {
    throw "Third-party notice file is missing: $noticeSource"
}
Copy-Item -LiteralPath $noticeSource -Destination (Join-Path $publishDirectory 'THIRD-PARTY-NOTICES.md') -Force

$packArguments = @(
    'pack',
    '--packId', 'ai-account-manager',
    '--packTitle', 'AI 帳號管理工具',
    '--packAuthors', '雲翼',
    '--framework', 'webview2',
    '--packVersion', $Version,
    '--runtime', 'win-x64',
    '--packDir', $publishDirectory,
    '--mainExe', 'CloudyWing.AiAccountManager.Desktop.exe',
    '--icon', (Join-Path $repositoryRoot 'src\AiAccountManager.Desktop\Assets\ai-account-manager.ico'),
    '--outputDir', $setupDirectory,
    '--instReadme', (Join-Path $repositoryRoot 'packaging\THIRD-PARTY-NOTICES.md')
)
if ($useLocalVpk) {
    Invoke-LoggedProcess -Name 'vpk-pack' -FileName 'dotnet' -Arguments (@('tool', 'run', 'vpk', '--') + $packArguments) -WorkingDirectory $repositoryRoot | Out-Host
} else {
    Invoke-LoggedProcess -Name 'vpk-pack' -FileName $VpkToolPath -Arguments $packArguments -WorkingDirectory $repositoryRoot | Out-Host
}

$setupExecutables = @(Get-ChildItem -LiteralPath $setupDirectory -Filter '*-Setup.exe' -File)
$releaseManifestPath = Join-Path $setupDirectory 'releases.win.json'
$packages = @(Get-ChildItem -LiteralPath $setupDirectory -Filter '*.nupkg' -File)
if ($setupExecutables.Count -ne 1) {
    throw "Velopack must produce exactly one *-Setup.exe installer; found $($setupExecutables.Count)."
}
if ($packages.Count -lt 1) {
    throw 'Velopack did not produce a .nupkg package.'
}
if (-not (Test-Path -LiteralPath $releaseManifestPath -PathType Leaf)) {
    throw 'Velopack did not produce releases.win.json.'
}

$releaseManifestText = [System.IO.File]::ReadAllText($releaseManifestPath)
if (-not $releaseManifestText.Contains($Version, [System.StringComparison]::Ordinal)) {
    throw "The release manifest does not identify version $Version."
}
foreach ($package in $packages) {
    if (-not $releaseManifestText.Contains($package.Name, [System.StringComparison]::Ordinal)) {
        throw "The release manifest does not reference package $($package.Name)."
    }

    $packageArchive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $appServerEntries = @($packageArchive.Entries | Where-Object { $_.FullName.EndsWith($appServerFileName, [System.StringComparison]::Ordinal) })
        $noticeEntries = @($packageArchive.Entries | Where-Object { $_.FullName.EndsWith('THIRD-PARTY-NOTICES.md', [System.StringComparison]::Ordinal) })
        $nuspecEntries = @($packageArchive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [System.StringComparison]::OrdinalIgnoreCase) })
        if ($appServerEntries.Count -ne 1 -or $noticeEntries.Count -lt 1 -or $nuspecEntries.Count -ne 1) {
            throw "Package '$($package.Name)' must contain one app-server executable, third-party notices and one nuspec."
        }

        $binaryStream = $appServerEntries[0].Open()
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $packageBinaryHash = [System.Convert]::ToHexString($sha256.ComputeHash($binaryStream))
        } finally {
            $sha256.Dispose()
            $binaryStream.Dispose()
        }
        if (-not [string]::Equals($packageBinaryHash, $appServerExecutableSha256, [System.StringComparison]::Ordinal)) {
            throw "Package '$($package.Name)' contains an app-server executable with an unexpected SHA-256."
        }

        $readerSettings = [System.Xml.XmlReaderSettings]::new()
        $readerSettings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
        $readerSettings.XmlResolver = $null
        $nuspecStream = $nuspecEntries[0].Open()
        $xmlReader = [System.Xml.XmlReader]::Create($nuspecStream, $readerSettings)
        try {
            $nuspecDocument = [System.Xml.XmlDocument]::new()
            $nuspecDocument.XmlResolver = $null
            $nuspecDocument.Load($xmlReader)
        } finally {
            $xmlReader.Dispose()
            $nuspecStream.Dispose()
        }

        $packageIdNode = $nuspecDocument.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='id']")
        $packageVersionNode = $nuspecDocument.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='version']")
        $packageTitleNode = $nuspecDocument.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='title']")
        $packageAuthorsNode = $nuspecDocument.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='authors']")
        $packageRuntimeDependenciesNode = $nuspecDocument.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='runtimeDependencies']")
        if ($null -eq $packageIdNode -or $packageIdNode.InnerText -cne 'ai-account-manager') {
            throw "Package '$($package.Name)' does not identify product ai-account-manager."
        }
        if ($null -eq $packageVersionNode -or $packageVersionNode.InnerText -cne $Version) {
            throw "Package '$($package.Name)' does not identify version $Version."
        }
        if ($null -eq $packageTitleNode -or $packageTitleNode.InnerText -cne 'AI 帳號管理工具') {
            throw "Package '$($package.Name)' does not contain the expected title."
        }
        if ($null -eq $packageAuthorsNode -or $packageAuthorsNode.InnerText -cne '雲翼') {
            throw "Package '$($package.Name)' does not contain the expected authors."
        }
        if ($null -eq $packageRuntimeDependenciesNode -or $packageRuntimeDependenciesNode.InnerText -cne 'webview2') {
            throw "Package '$($package.Name)' does not declare the expected webview2 runtime dependency."
        }
    } finally {
        $packageArchive.Dispose()
    }
}
$setupExecutable = $setupExecutables[0].FullName
New-Item -ItemType Directory -Path $setupDirectory -Force | Out-Null
$filesToCopy = @($setupExecutable, $releaseManifestPath, (Join-Path $publishDirectory 'THIRD-PARTY-NOTICES.md')) + @($packages | ForEach-Object { $_.FullName })
foreach ($filePath in $filesToCopy) {
    $destinationPath = Join-Path $outputStageDirectory ([System.IO.Path]::GetFileName($filePath))
    Copy-Item -LiteralPath $filePath -Destination $destinationPath -Force
}

$manifestFiles = @(Get-ChildItem -LiteralPath $setupDirectory -File | Sort-Object Name | ForEach-Object {
    [ordered]@{
        name = $_.Name
        length = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$releaseArtifactsManifest = [ordered]@{
    schema = 'ai-account-manager.release-artifacts.v1'
    packId = 'ai-account-manager'
    version = $Version
    runtime = 'win-x64'
    selfContained = $true
    codexAppServer = [ordered]@{
        version = $appServerVersion
        archiveSha256 = $archiveHash.ToLowerInvariant()
        executableSha256 = $executableHash.ToLowerInvariant()
    }
    files = $manifestFiles
}
$stagedReleaseArtifactsManifestPath = Join-Path $setupDirectory 'release-artifacts.json'
[System.IO.File]::WriteAllText(
    $stagedReleaseArtifactsManifestPath,
    ($releaseArtifactsManifest | ConvertTo-Json -Depth 8),
    [System.Text.UTF8Encoding]::new($false)
)
Copy-Item -LiteralPath $stagedReleaseArtifactsManifestPath -Destination $outputStageDirectory -Force

$releaseFiles = @(Get-ChildItem -LiteralPath $setupDirectory -File | Sort-Object Name)
if ($releaseFiles.Count -ne $manifestFiles.Count + 1) {
    throw 'The staged release directory contains an unexpected file set.'
}
foreach ($manifestFile in $manifestFiles) {
    $stagedFile = Join-Path $setupDirectory $manifestFile.name
    $stagedHash = (Get-FileHash -LiteralPath $stagedFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::Equals($stagedHash, $manifestFile.sha256, [System.StringComparison]::Ordinal)) {
        throw "Staged release file '$($manifestFile.name)' does not match its recorded SHA-256."
    }
}

$expectedOutputNames = @($filesToCopy | ForEach-Object { [System.IO.Path]::GetFileName($_) }) + @('release-artifacts.json')
$expectedOutputNames = @($expectedOutputNames | Sort-Object -Unique)
$actualOutputNames = @(Get-ChildItem -LiteralPath $outputStageDirectory -File | ForEach-Object { $_.Name } | Sort-Object)
if (($expectedOutputNames -join "`n") -cne ($actualOutputNames -join "`n")) {
    throw 'The staged first-release directory does not contain the expected file set.'
}
foreach ($sourcePath in @($filesToCopy) + @($stagedReleaseArtifactsManifestPath)) {
    $destinationPath = Join-Path $outputStageDirectory ([System.IO.Path]::GetFileName($sourcePath))
    $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
    $destinationHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash
    if (-not [string]::Equals($sourceHash, $destinationHash, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Staged output file '$([System.IO.Path]::GetFileName($sourcePath))' does not match its source SHA-256."
    }
}

if ((Test-Path -LiteralPath $releaseDirectory) -and -not (Test-Path -LiteralPath $releaseDirectory -PathType Container)) {
    throw "The release destination is not a directory: $releaseDirectory"
}
if ((Test-Path -LiteralPath $outputDirectory) -and -not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    throw "The first-release destination is not a directory: $outputDirectory"
}

try {
    if (Test-Path -LiteralPath $releaseDirectory -PathType Container) {
        [System.IO.Directory]::Move($releaseDirectory, $releaseBackupDirectory)
        $releaseBackedUp = $true
    }
    if (Test-Path -LiteralPath $outputDirectory -PathType Container) {
        [System.IO.Directory]::Move($outputDirectory, $outputBackupDirectory)
        $outputBackedUp = $true
    }

    [System.IO.Directory]::Move($setupDirectory, $releaseDirectory)
    $releasePromoted = $true
    [System.IO.Directory]::Move($outputStageDirectory, $outputDirectory)
    $outputPromoted = $true
    $promotionCommitted = $true
} catch {
    $promotionFailure = $_.Exception
    $rollbackFailures = [System.Collections.Generic.List[System.Exception]]::new()

    if ($outputPromoted -and (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
        try {
            [System.IO.Directory]::Move($outputDirectory, $outputStageDirectory)
        } catch {
            $rollbackFailures.Add($_.Exception)
        }
    }
    if ($releasePromoted -and (Test-Path -LiteralPath $releaseDirectory -PathType Container)) {
        try {
            [System.IO.Directory]::Move($releaseDirectory, $setupDirectory)
        } catch {
            $rollbackFailures.Add($_.Exception)
        }
    }
    if ($outputBackedUp -and (Test-Path -LiteralPath $outputBackupDirectory -PathType Container)) {
        try {
            [System.IO.Directory]::Move($outputBackupDirectory, $outputDirectory)
        } catch {
            $rollbackFailures.Add($_.Exception)
        }
    }
    if ($releaseBackedUp -and (Test-Path -LiteralPath $releaseBackupDirectory -PathType Container)) {
        try {
            [System.IO.Directory]::Move($releaseBackupDirectory, $releaseDirectory)
        } catch {
            $rollbackFailures.Add($_.Exception)
        }
    }

    if ($rollbackFailures.Count -gt 0) {
        $rollbackFailures.Insert(0, $promotionFailure)
        throw [System.AggregateException]::new('Package promotion failed and rollback was incomplete; backup directories were retained.', $rollbackFailures)
    }

    throw
}

if (-not $promotionCommitted) {
    throw 'Package promotion did not complete.'
}

$releaseArtifactsManifestPath = Join-Path $releaseDirectory 'release-artifacts.json'
foreach ($backupDirectory in @(
    [pscustomobject]@{ Path = $releaseBackupDirectory; Parent = $artifactRoot; Exists = $releaseBackedUp },
    [pscustomobject]@{ Path = $outputBackupDirectory; Parent = $outputParent; Exists = $outputBackedUp }
)) {
    if ($backupDirectory.Exists) {
        try {
            Remove-OwnedDirectory -Path $backupDirectory.Path -Parent $backupDirectory.Parent
        } catch {
            Write-Warning "The verified package was promoted, but backup cleanup failed: $($backupDirectory.Path)"
        }
    }
}

Write-Output ([ordered]@{
    status = 'success'
    version = $Version
    publishDirectory = $publishDirectory
    setupDirectory = $releaseDirectory
    releaseDirectory = $releaseDirectory
    outputDirectory = $outputDirectory
    appServerArchiveSha256 = $archiveHash
    appServerExecutableSha256 = $executableHash
    packageFiles = @($filesToCopy | ForEach-Object { [System.IO.Path]::GetFileName($_) })
    releaseArtifactsManifest = $releaseArtifactsManifestPath
} | ConvertTo-Json -Depth 8)
