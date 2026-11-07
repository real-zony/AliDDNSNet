#requires -Version 7.0
#requires -PSEdition Core

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64', 'linux-arm', 'osx-x64', 'osx-arm64')]
    [string] $RuntimeIdentifier,
    [string] $Version,
    [string] $OutputParent,
    [string] $DotnetPath = 'dotnet',
    [switch] $IncludeBaseline,
    [string] $Launcher,
    [string[]] $LauncherArguments = @()
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

function Invoke-NativeCommand([string] $Executable, [string[]] $Arguments, [string] $Operation) {
    # Native diagnostics stay visible without becoming part of the result object.
    & $Executable @Arguments 2>&1 | ForEach-Object { Write-Host $_ }
    $nativeExitCode = $LASTEXITCODE
    if ($nativeExitCode -ne 0) {
        $failure = [System.InvalidOperationException]::new("$Operation failed with exit code $nativeExitCode. See the diagnostics above. Outputs remain in $outputDirectory.")
        $failure.Data['ExitCode'] = $nativeExitCode
        throw $failure
    }
}

function Assert-PublishPayload([string] $Directory) {
    foreach ($requiredFile in @($executableName, 'settings.json.example')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $requiredFile) -PathType Leaf)) {
            throw "Published output is missing ${requiredFile}: $Directory"
        }
    }

    $allowedFiles = @($executableName, 'settings.json.example')
    if ($targetOS -eq 'win') {
        $allowedFiles += 'AliCloudDynamicDNS.pdb'
    }
    elseif ($targetOS -eq 'linux') {
        $allowedFiles += 'AliCloudDynamicDNS.dbg'
    }
    foreach ($entry in Get-ChildItem -LiteralPath $Directory -Recurse -Force) {
        if ($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            throw "Published output contains a link and cannot be safely packaged: $($entry.FullName)"
        }
        if ($entry.Name -ieq 'settings.json') {
            throw "Published output contains a real settings.json: $($entry.FullName)"
        }

        $relativePath = [System.IO.Path]::GetRelativePath($Directory, $entry.FullName)
        $parts = $relativePath -split '[\\/]'
        # macOS Native AOT symbols are a bundle, not a single file.
        $isSymbolBundle = $targetOS -eq 'osx' -and $parts[0] -ceq 'AliCloudDynamicDNS.dSYM'
        if ($entry.PSIsContainer) {
            if (-not $isSymbolBundle) {
                throw "Unexpected deployment directory: $($entry.FullName)"
            }
        }
        elseif (-not (($parts.Count -eq 1 -and $entry.Name -cin $allowedFiles) -or
            ($isSymbolBundle -and $parts.Count -gt 1))) {
            throw "Unexpected deployment file; refusing to create an incomplete or unsafe archive: $($entry.FullName)"
        }
    }
}

function Invoke-PublishSmoke([string] $Directory) {
    $smokeParameters = @{ PublishDirectory = $Directory }
    if ($hasLauncher) {
        $smokeParameters.Launcher = $launcherExecutable
        $smokeParameters.LauncherArguments = $LauncherArguments
    }
    & $smokeScript @smokeParameters | ForEach-Object { Write-Host $_ }
}

try {
    $RuntimeIdentifier = $RuntimeIdentifier.ToLowerInvariant()
    $targetOS, $targetArchitecture = $RuntimeIdentifier.Split('-')
    $hostOS = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else { 'unsupported' }
    $hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    $hasLauncher = -not [string]::IsNullOrWhiteSpace($Launcher)

    if ($targetOS -ne $hostOS) {
        throw "Native AOT publishing for $RuntimeIdentifier requires a $targetOS host; cross-OS publishing is not supported."
    }
    if ($hostArchitecture -notin @('x64', 'arm64') -or $processArchitecture -ne $hostArchitecture) {
        throw 'Native AOT publishing requires native x64 or Arm64 PowerShell 7, not an emulated or different-architecture process.'
    }
    if ($IncludeBaseline -and $RuntimeIdentifier -ne 'win-x64') {
        throw '-IncludeBaseline is supported only for win-x64.'
    }
    if (-not $hasLauncher -and $LauncherArguments.Count -gt 0) {
        throw '-LauncherArguments requires -Launcher.'
    }
    if ($hasLauncher -and $RuntimeIdentifier -ne 'linux-arm') {
        throw '-Launcher is supported only for linux-arm cross execution on a Linux host.'
    }
    if ($targetArchitecture -ne $hostArchitecture -and -not $hasLauncher) {
        throw "Publishing $RuntimeIdentifier from $hostOS-$hostArchitecture requires a supported smoke launcher. Only linux-arm cross execution with -Launcher is supported."
    }

    $launcherExecutable = $null
    if ($hasLauncher) {
        $launcherCommand = Get-Command -Name $Launcher -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $launcherCommand) {
            throw "Cannot find smoke launcher '$Launcher'. Install the target execution prerequisite before publishing."
        }
        $launcherExecutable = $launcherCommand.Source
    }
    $tarExecutable = $null
    if (-not $IsWindows) {
        $tarCommand = Get-Command -Name tar -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $tarCommand) {
            throw 'Unix release packaging requires tar with gzip support.'
        }
        $tarExecutable = $tarCommand.Source
    }

    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $applicationProject = Join-Path $repositoryRoot 'AliCloudDynamicDNS/AliCloudDynamicDNS.csproj'
    $smokeScript = Join-Path $PSScriptRoot 'smoke-publish.ps1'
    $executableName = if ($IsWindows) { 'AliCloudDynamicDNS.exe' } else { 'AliCloudDynamicDNS' }
    $dotnetCommand = Get-Command -Name $DotnetPath -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $dotnetCommand) {
        throw "Cannot find dotnet executable '$DotnetPath'. Install the .NET SDK selected by global.json or pass -DotnetPath with the full executable path."
    }
    $dotnetExecutable = $dotnetCommand.Source

    if ([string]::IsNullOrWhiteSpace($Version)) {
        $projectXml = [xml] (Get-Content -LiteralPath $applicationProject -Raw)
        $Version = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    }
    if ($Version -cnotmatch '^v?[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$') {
        throw 'Version must be a release version such as 0.1.0, v0.1.0, or 0.1.0-preview.1.'
    }
    $applicationVersion = if ($Version.StartsWith('v', [System.StringComparison]::Ordinal)) { $Version.Substring(1) } else { $Version }
    $releaseVersion = "v$applicationVersion"

    if ([string]::IsNullOrWhiteSpace($OutputParent)) {
        $OutputParent = Join-Path $repositoryRoot 'TempFiles'
    }
    $outputParentPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputParent)
    Push-Location -LiteralPath $repositoryRoot
    try {
        # Resolve the pinned SDK from the repository, not from the caller's directory.
        $sdkVersion = (& $dotnetExecutable --version 2>&1 | Out-String).Trim()
        $nativeExitCode = $LASTEXITCODE
        if ($nativeExitCode -ne 0) {
            Write-Host $sdkVersion
            $failure = [System.InvalidOperationException]::new('The SDK selected by global.json is unavailable. Install that SDK, then retry with native PowerShell 7.')
            $failure.Data['ExitCode'] = $nativeExitCode
            throw $failure
        }
        if ($sdkVersion -notmatch '^10\.0\.\d+$') {
            throw "Expected the .NET 10 SDK selected by global.json; dotnet reported '$sdkVersion'."
        }
        $executionDescription = if ($hasLauncher) { "cross-compiled, smoke-executed via $launcherExecutable" } else { 'native build and execution' }
        Write-Host "Publishing $RuntimeIdentifier with .NET SDK $sdkVersion ($executionDescription)."

        [System.IO.Directory]::CreateDirectory($outputParentPath) | Out-Null
        $outputDirectory = Join-Path $outputParentPath "$RuntimeIdentifier-$releaseVersion-$([System.Guid]::NewGuid().ToString('N'))"
        # No -Force: an existing directory is never reused, overwritten or removed.
        New-Item -ItemType Directory -Path $outputDirectory | Out-Null

        $modes = [System.Collections.Generic.List[object]]::new()
        $modes.Add([pscustomobject]@{ Name = 'aot'; IsAot = $true })
        if ($IncludeBaseline) {
            $modes.Add([pscustomobject]@{ Name = 'baseline'; IsAot = $false })
        }
        foreach ($mode in $modes) {
            $publishDirectory = Join-Path $outputDirectory "publish/$($mode.Name)"
            $artifactDirectory = Join-Path $outputDirectory "build/$($mode.Name)"
            $archiveExtension = if ($IsWindows) { 'zip' } else { 'tar.gz' }
            $baselineSuffix = if ($mode.IsAot) { '' } else { '_baseline' }
            $archive = Join-Path $outputDirectory "AliCloudDynamicDNS_$RuntimeIdentifier${baselineSuffix}_$releaseVersion.$archiveExtension"
            $archiveSmokeDirectory = Join-Path $outputDirectory $(if ($mode.IsAot) { 'archive-smoke' } else { 'archive-smoke-baseline' })
            $mode | Add-Member -NotePropertyName PublishDirectory -NotePropertyValue $publishDirectory
            $mode | Add-Member -NotePropertyName Archive -NotePropertyValue $archive
            $mode | Add-Member -NotePropertyName ArchiveSmokeDirectory -NotePropertyValue $archiveSmokeDirectory

            $publishArguments = @(
                'publish', $applicationProject,
                '--configuration', 'Release', '--framework', 'net10.0', '--runtime', $RuntimeIdentifier,
                '--self-contained', 'true', '--output', $publishDirectory,
                '--artifacts-path', $artifactDirectory,
                "-p:Version=$applicationVersion", '-p:UseAppHost=true', '-p:PublishTrimmed=true'
            )
            if ($mode.IsAot) {
                $publishArguments += '-p:PublishProfile=NativeAot'
            }
            else {
                $publishArguments += @(
                    '-p:PublishAot=false', '-p:PublishSingleFile=true',
                    '-p:IncludeNativeLibrariesForSelfExtract=true'
                )
            }

            Write-Host "Publishing $($mode.Name): $publishDirectory"
            # The SDK discovers native compilers/linkers and reports missing prerequisites.
            Invoke-NativeCommand -Executable $dotnetExecutable -Arguments $publishArguments -Operation 'dotnet publish'
            Assert-PublishPayload -Directory $publishDirectory
            Invoke-PublishSmoke -Directory $publishDirectory
        }

        foreach ($mode in $modes) {
            # Packaging starts only after final native output and publish-directory smoke.
            Assert-PublishPayload -Directory $mode.PublishDirectory
            New-Item -ItemType Directory -Path $mode.ArchiveSmokeDirectory | Out-Null
            if ($IsWindows) {
                [System.IO.Compression.ZipFile]::CreateFromDirectory(
                    $mode.PublishDirectory, $mode.Archive,
                    [System.IO.Compression.CompressionLevel]::Optimal, $false)
                [System.IO.Compression.ZipFile]::ExtractToDirectory($mode.Archive, $mode.ArchiveSmokeDirectory)
            }
            else {
                # tar preserves the native executable's permissions and macOS symbol bundles.
                Invoke-NativeCommand -Executable $tarExecutable -Arguments @(
                    '-czf', $mode.Archive, '-C', $mode.PublishDirectory, '.'
                ) -Operation 'Release archive creation'
                Invoke-NativeCommand -Executable $tarExecutable -Arguments @(
                    '-xzf', $mode.Archive, '-C', $mode.ArchiveSmokeDirectory
                ) -Operation 'Release archive extraction'
            }
            Write-Host "Created archive: $($mode.Archive)"
            Assert-PublishPayload -Directory $mode.ArchiveSmokeDirectory
            Invoke-PublishSmoke -Directory $mode.ArchiveSmokeDirectory
        }

        $baseline = if ($IncludeBaseline) { $modes[1] } else { $null }
        [pscustomobject]@{
            RuntimeIdentifier = $RuntimeIdentifier
            OutputDirectory = $outputDirectory
            AotPublishDirectory = $modes[0].PublishDirectory
            BaselinePublishDirectory = if ($null -ne $baseline) { $baseline.PublishDirectory } else { $null }
            AotArchive = $modes[0].Archive
            BaselineArchive = if ($null -ne $baseline) { $baseline.Archive } else { $null }
            ArchiveSmokeDirectory = $modes[0].ArchiveSmokeDirectory
        }
    }
    finally {
        Pop-Location
    }
}
catch {
    $failureExitCode = if ($_.Exception.Data.Contains('ExitCode')) { [int] $_.Exception.Data['ExitCode'] } else { 1 }
    Write-Error -ErrorRecord $_ -ErrorAction Continue
    exit $failureExitCode
}
