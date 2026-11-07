#Requires -Version 7.0
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Image,

    [Parameter(Mandatory = $true)]
    [ValidateSet('linux/amd64', 'linux/arm64', 'linux/arm/v7')]
    [string] $Platform,

    [ValidateNotNullOrEmpty()]
    [string] $ExpectedSource = 'https://github.com/real-zony/AliDDNSNet'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$dockerPath = (Get-Command docker -CommandType Application -ErrorAction Stop).Source
$workingDirectory = [System.IO.Directory]::CreateTempSubdirectory('AliDDNSNet-container-smoke-').FullName
$runId = [guid]::NewGuid().ToString('N')
$ownedContainers = [System.Collections.Generic.List[string]]::new()
$accessId = 'SMOKE_FULL_ACCESS_ID_MUST_NOT_APPEAR'
$accessKey = 'SMOKE_ACCESS_KEY_MUST_NOT_APPEAR'

function Invoke-Docker {
    param(
        [string[]] $Arguments,
        [int] $TimeoutSeconds = 30,
        [switch] $AllowFailure
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $dockerPath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'Docker could not start.'
        }
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            if (-not $process.WaitForExit(5000)) {
                throw 'Docker did not terminate after its smoke deadline.'
            }
            throw "Docker exceeded the $TimeoutSeconds-second smoke deadline."
        }
        $result = [pscustomobject]@{
            ExitCode = $process.ExitCode
            Stdout = $stdout.GetAwaiter().GetResult()
            Stderr = $stderr.GetAwaiter().GetResult()
        }
        if (-not $AllowFailure -and $result.ExitCode -ne 0) {
            throw "Docker operation failed with exit $($result.ExitCode): $($result.Stderr)"
        }
        return $result
    }
    finally {
        $process.Dispose()
    }
}

function New-OwnedContainer {
    param(
        [string] $Case,
        [string[]] $ApplicationArguments = @(),
        [switch] $MountConfiguration
    )

    $name = "aliddnsnet-smoke-$runId-$Case"
    # Register before creation so a timed-out Docker client cannot orphan its container.
    $ownedContainers.Add($name)
    $arguments = @(
        'container', 'create', '--name', $name, '--pull', 'never',
        '--platform', $Platform, '--network', 'none',
        '--label', "org.aliddnsnet.smoke=$runId", '--env', 'NO_COLOR=1'
    )
    if ($MountConfiguration) {
        $arguments += @('--mount', "type=bind,source=$workingDirectory,target=/smoke,readonly")
    }
    $arguments += @($imageId) + $ApplicationArguments
    $created = Invoke-Docker -Arguments $arguments
    $containerId = $created.Stdout.Trim()
    if ($containerId -notmatch '^[a-f0-9]{64}$') {
        throw 'Docker did not return a container ID.'
    }
    return $containerId
}

function Assert-SafeOutput([string] $Text) {
    if ($Text.Contains($accessId) -or $Text.Contains($accessKey) -or $Text -match '(?i)Signature=') {
        throw 'Container output leaked credentials or a signed request.'
    }
    if ($Text -match '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F-\x9F]') {
        throw 'Redirected container output contains terminal control characters.'
    }
    if ($Text -match '(?im)^\s*at\s+\S+\(|Unhandled exception|System\.Text\.Json\.JsonException') {
        throw 'Container output contains a raw exception instead of a safe diagnostic.'
    }
}

function Invoke-ContainerApplication {
    param(
        [string] $Case,
        [string[]] $ApplicationArguments,
        [switch] $MountConfiguration
    )

    $containerId = New-OwnedContainer -Case $Case -ApplicationArguments $ApplicationArguments -MountConfiguration:$MountConfiguration
    $started = Invoke-Docker -Arguments @('container', 'start', '--attach', $containerId) -AllowFailure
    $inspected = Invoke-Docker -Arguments @('container', 'inspect', '--format', '{{json .State}}', $containerId)
    $state = $inspected.Stdout | ConvertFrom-Json
    if ($state.Running -or $state.Status -ne 'exited' -or -not [string]::IsNullOrEmpty($state.Error)) {
        throw "Container case '$Case' did not exit normally."
    }
    $text = $started.Stdout + $started.Stderr
    Assert-SafeOutput -Text $text
    return [pscustomobject]@{ ExitCode = $state.ExitCode; Text = $text }
}

try {
    $inspected = Invoke-Docker -Arguments @('image', 'inspect', '--format', '{{json .}}', $Image)
    $metadata = $inspected.Stdout | ConvertFrom-Json
    $variant = if ($metadata.PSObject.Properties['Variant']) { [string] $metadata.Variant } else { '' }
    $actualPlatform = "$($metadata.Os)/$($metadata.Architecture)"
    if ($variant) {
        $actualPlatform += "/$variant"
    }
    $validPlatform = switch ($Platform) {
        'linux/amd64' { $metadata.Os -eq 'linux' -and $metadata.Architecture -eq 'amd64' -and $variant -eq '' }
        'linux/arm64' { $metadata.Os -eq 'linux' -and $metadata.Architecture -eq 'arm64' -and $variant -in @('', 'v8') }
        'linux/arm/v7' { $metadata.Os -eq 'linux' -and $metadata.Architecture -eq 'arm' -and $variant -eq 'v7' }
    }
    if (-not $validPlatform) {
        throw "Image platform '$actualPlatform' does not match requested '$Platform'."
    }
    if (@($metadata.Config.Entrypoint).Count -ne 1 -or $metadata.Config.Entrypoint[0] -ne '/app/AliCloudDynamicDNS' -or
        @($metadata.Config.Cmd).Count -ne 2 -or $metadata.Config.Cmd[0] -ne '-i' -or $metadata.Config.Cmd[1] -ne '30' -or
        $metadata.Config.WorkingDir -ne '/app') {
        throw 'Image must use the native /app entry point and a separate overridable -i 30 command.'
    }
    if ($metadata.Config.Labels.'org.opencontainers.image.source' -cne $ExpectedSource -or
        [string]::IsNullOrWhiteSpace($metadata.Config.Labels.'org.opencontainers.image.version')) {
        throw 'Image does not match the expected source or is missing its application version label.'
    }
    # Pin all smoke cases to the inspected local image; never pull or re-resolve a mutable tag.
    $imageId = [string] $metadata.Id
    $payloadContainer = New-OwnedContainer -Case 'payload'
    $payloadDirectory = [System.IO.Directory]::CreateDirectory((Join-Path $workingDirectory 'payload')).FullName
    $null = Invoke-Docker -Arguments @('container', 'cp', "${payloadContainer}:/app/.", $payloadDirectory)
    $payload = @(Get-ChildItem -LiteralPath $payloadDirectory -Force)
    if ($payload.Count -ne 2 -or @($payload | Where-Object { $_.PSIsContainer }).Count -ne 0 -or
        -not (Test-Path -LiteralPath (Join-Path $payloadDirectory 'settings.json.example') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $payloadDirectory 'AliCloudDynamicDNS') -PathType Leaf)) {
        throw 'Image /app must contain only the native executable and settings.json.example, never real configuration or build output.'
    }
    $reader = [System.IO.BinaryReader]::new([System.IO.File]::OpenRead((Join-Path $payloadDirectory 'AliCloudDynamicDNS')))
    try {
        $header = $reader.ReadBytes(64)
    }
    finally {
        $reader.Dispose()
    }
    if ($header.Length -ne 64 -or $header[0] -ne 0x7F -or $header[1] -ne 0x45 -or $header[2] -ne 0x4C -or
        $header[3] -ne 0x46 -or $header[5] -ne 1 -or [System.BitConverter]::ToUInt16($header, 16) -notin @(2, 3)) {
        throw 'Image entry point is not a little-endian native ELF executable.'
    }
    $elfClass, $elfMachine = switch ($Platform) {
        'linux/amd64' { 2; 62 }
        'linux/arm64' { 2; 183 }
        'linux/arm/v7' { 1; 40 }
    }
    if ($header[4] -ne $elfClass -or [System.BitConverter]::ToUInt16($header, 18) -ne $elfMachine -or
        ($Platform -eq 'linux/arm/v7' -and ([System.BitConverter]::ToUInt32($header, 36) -band 0x400) -eq 0)) {
        throw 'Native ELF architecture or Arm hard-float ABI does not match the requested image platform.'
    }

    $help = Invoke-ContainerApplication -Case 'help' -ApplicationArguments @('--help')
    if ($help.ExitCode -ne 0 -or $help.Text -notmatch '--file' -or $help.Text -notmatch '--interval') {
        throw 'Container --help did not return exit 0 with supported CLI options.'
    }

    $missing = Invoke-ContainerApplication -Case 'missing' -ApplicationArguments @('--interval', '0')
    if ($missing.ExitCode -ne 1 -or $missing.Text -notmatch 'settings\.json') {
        throw 'Container missing configuration did not return exit 1 with a settings.json diagnostic.'
    }

    $configurationPath = Join-Path $workingDirectory 'nested-targets.json'
    @"
{
  // Exercise the real JSON parser and nested record-type validator without network access.
  "accessid": "$accessId",
  "AccessKey": "$accessKey",
  "MainDomain": "example.invalid",
  "PublicIpServer": "http://127.0.0.1:9/ip",
  "PublicIpv6Server": "http://127.0.0.1:9/ipv6",
  "UnknownProperty": true,
  "subdomains": [
    { "Type": "A", "SubDomain": "nested.valid", "Interval": 600 },
    { "Type": "CNAME", "SubDomain": "nested.invalid", "Interval": 600 },
  ],
}
"@ | Set-Content -LiteralPath $configurationPath -Encoding utf8
    $nested = Invoke-ContainerApplication -Case 'nested' -MountConfiguration -ApplicationArguments @('--file', '/smoke/nested-targets.json', '--interval', '0')
    if ($nested.ExitCode -ne 1 -or $nested.Text -notmatch '(?i)CNAME|record\s*type|SubDomains\[1\]\.Type|记录类型') {
        throw 'Container nested validation did not return exit 1 with an unsupported-record-type diagnostic.'
    }

    Write-Host "Native container smoke passed: $Image ($Platform; image $imageId)"
}
finally {
    $cleanupFailures = [System.Collections.Generic.List[string]]::new()
    foreach ($containerName in $ownedContainers) {
        try {
            $removed = Invoke-Docker -Arguments @('container', 'rm', '--force', $containerName) -TimeoutSeconds 10 -AllowFailure
            if ($removed.ExitCode -ne 0 -and $removed.Stderr -notmatch '(?i)No such container') {
                $cleanupFailures.Add($containerName)
            }
        }
        catch {
            $cleanupFailures.Add($containerName)
        }
    }
    Remove-Item -LiteralPath $workingDirectory -Recurse -Force
    if ($cleanupFailures.Count -gt 0) {
        throw "Could not remove owned smoke containers: $($cleanupFailures -join ', ')"
    }
}
