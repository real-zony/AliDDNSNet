param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
$executableName = if ($IsWindows) { 'AliCloudDynamicDNS.exe' } else { 'AliCloudDynamicDNS' }
$executablePath = Join-Path $publishPath $executableName
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Published executable is missing: $executableName"
}
if (Get-ChildItem -LiteralPath $publishPath -Recurse -File -Filter 'settings.json') {
    throw 'Published output contains a real settings.json.'
}
if (-not (Test-Path -LiteralPath (Join-Path $publishPath 'settings.json.example') -PathType Leaf)) {
    throw 'Published output is missing settings.json.example.'
}

$workingDirectory = [System.IO.Directory]::CreateTempSubdirectory('AliDDNSNet-release-smoke-').FullName
$accessId = 'SMOKE_FULL_ACCESS_ID_MUST_NOT_APPEAR'
$accessKey = 'SMOKE_ACCESS_KEY_MUST_NOT_APPEAR'

function Invoke-PublishedApplication([string[]] $Arguments) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $executablePath
    $startInfo.WorkingDirectory = $workingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['NO_COLOR'] = '1'
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'Published application could not start.'
        }
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Published application exceeded the 30-second smoke timeout.'
        }
        $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        if ($text.Contains($accessId) -or $text.Contains($accessKey) -or $text -match '(?i)Signature=') {
            throw 'Published application leaked credentials or a signed request.'
        }
        if ($text -match '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F-\x9F]') {
            throw 'Redirected output contains terminal control characters.'
        }
        if ($text -match '(?im)^\s*at\s+\S+\(|Unhandled exception|System\.Text\.Json\.JsonException') {
            throw 'Published application emitted a raw exception instead of a safe diagnostic.'
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Text = $text }
    }
    finally {
        $process.Dispose()
    }
}

try {
    $help = Invoke-PublishedApplication -Arguments @('--help')
    if ($help.ExitCode -ne 0 -or $help.Text -notmatch '--file' -or $help.Text -notmatch '--interval') {
        throw 'Published --help did not expose the CLI entry point and supported options.'
    }

    $missing = Invoke-PublishedApplication -Arguments @('--interval', '0')
    if ($missing.ExitCode -ne 1 -or $missing.Text -notmatch 'settings\.json') {
        throw 'Missing default configuration did not return exit 1 with an identifiable settings.json diagnostic.'
    }

    $invalidInterval = Invoke-PublishedApplication -Arguments @('--interval', '1')
    if ($invalidInterval.ExitCode -eq 0 -or [string]::IsNullOrWhiteSpace($invalidInterval.Text)) {
        throw 'An invalid interval was not rejected with a diagnostic.'
    }

    $configurationPath = Join-Path $workingDirectory 'nested-targets.json'
    @"
{
  // Verify comment, case-insensitive property, unknown-property and trailing-comma compatibility.
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

    $nested = Invoke-PublishedApplication -Arguments @('--file', $configurationPath, '--interval', '0')
    if ($nested.ExitCode -ne 1 -or $nested.Text -notmatch '(?i)CNAME|record\s*type|SubDomains\[1\]\.Type|记录类型') {
        throw 'Nested target validation did not return exit 1 with an unsupported-record-type diagnostic.'
    }

    $bomConfigurationPath = Join-Path $workingDirectory 'nested-targets-utf32le.json'
    $bomConfiguration = [System.IO.File]::ReadAllText($configurationPath, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($bomConfigurationPath, $bomConfiguration, [System.Text.UTF32Encoding]::new($false, $true, $true))

    $bomNested = Invoke-PublishedApplication -Arguments @('--file', $bomConfigurationPath, '--interval', '0')
    if ($bomNested.ExitCode -ne 1 -or $bomNested.Text -notmatch '(?i)CNAME|record\s*type|SubDomains\[1\]\.Type|记录类型') {
        throw 'UTF32LE BOM target validation did not return exit 1 with an unsupported-record-type diagnostic.'
    }

    Write-Host "Native published entry-point smoke passed: $executableName"
}
finally {
    Remove-Item -LiteralPath $workingDirectory -Recurse -Force
}
