#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $BaselineDirectory,
    [Parameter(Mandatory = $true)]
    [string] $AotDirectory,
    [string] $OutputDirectory,
    [ValidateRange(40, 10000)]
    [int] $Iterations = 40,
    [ValidateRange(5, 1000)]
    [int] $Warmup = 5
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'Run this benchmark in Windows x64 PowerShell 7.'
}

# Retain the handle returned by Process.Start: Process.Refresh/TotalProcessorTime can
# lose accounting for a CLI that has already exited. These APIs also work after exit.
if (-not ('AliDDNSNet.WindowsPublishBenchmark.Runner' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace AliDDNSNet.WindowsPublishBenchmark
{
    public sealed class Sample
    {
        public int ExitCode { get; set; }
        public double WallMilliseconds { get; set; }
        public double ProcessLifetimeMilliseconds { get; set; }
        public double CpuMilliseconds { get; set; }
        public long KernelCpu100Nanoseconds { get; set; }
        public long UserCpu100Nanoseconds { get; set; }
        public ulong PeakWorkingSetBytes { get; set; }
        public string StandardOutput { get; set; }
        public string StandardError { get; set; }
    }

    public static class Runner
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint Size;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(SafeProcessHandle process,
            out long creation, out long exit, out long kernel, out long user);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(SafeProcessHandle process,
            ref ProcessMemoryCounters counters, uint size);

        private static async Task<string> DrainAsync(StreamReader reader)
        {
            const int maximumCharacters = 1024 * 1024;
            var text = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            {
                if (text.Length > maximumCharacters - count)
                    throw new InvalidDataException("Application output exceeded the one-MiB-per-stream limit.");
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }

        public static Sample Run(string executable, string workingDirectory, string[] arguments,
            int timeoutMilliseconds, int drainTimeoutMilliseconds)
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            startInfo.Environment["NO_COLOR"] = "1";
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using (var process = new Process { StartInfo = startInfo })
            {
                bool started = false;
                try
                {
                    var wall = Stopwatch.StartNew();
                    started = process.Start();
                    if (!started)
                        throw new InvalidOperationException("Published executable did not start.");
                    SafeProcessHandle handle = process.SafeHandle;
                    // Start both drains before waiting, so neither redirected pipe can deadlock.
                    Task<string> stdout = DrainAsync(process.StandardOutput);
                    Task<string> stderr = DrainAsync(process.StandardError);
                    process.StandardInput.Close();
                    if (!process.WaitForExit(timeoutMilliseconds))
                        throw new TimeoutException("Published executable exceeded the 30-second launch timeout.");
                    wall.Stop();

                    long creation, exit, kernel, user;
                    if (!GetProcessTimes(handle, out creation, out exit, out kernel, out user))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "GetProcessTimes failed; CPU metrics are unavailable.");
                    var memory = new ProcessMemoryCounters();
                    memory.Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>();
                    if (!GetProcessMemoryInfo(handle, ref memory, memory.Size))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "GetProcessMemoryInfo failed; peak working set is unavailable.");
                    if (memory.PeakWorkingSetSize.ToUInt64() == 0 || exit < creation || kernel < 0 || user < 0)
                        throw new InvalidDataException("Windows returned unavailable or invalid process accounting; no zero metrics were substituted.");

                    Task<string[]> drains = Task.WhenAll(stdout, stderr);
                    if (!drains.Wait(drainTimeoutMilliseconds))
                        throw new TimeoutException("Redirected output did not close within five seconds of process exit.");
                    string[] output = drains.GetAwaiter().GetResult();
                    return new Sample
                    {
                        ExitCode = process.ExitCode,
                        WallMilliseconds = wall.Elapsed.TotalMilliseconds,
                        ProcessLifetimeMilliseconds = (exit - creation) / 10000.0,
                        CpuMilliseconds = (kernel + user) / 10000.0,
                        KernelCpu100Nanoseconds = kernel,
                        UserCpu100Nanoseconds = user,
                        PeakWorkingSetBytes = memory.PeakWorkingSetSize.ToUInt64(),
                        StandardOutput = output[0],
                        StandardError = output[1]
                    };
                }
                finally
                {
                    // Never enumerate or kill unrelated processes by executable name.
                    if (started && !process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        if (!process.WaitForExit(5000))
                            throw new TimeoutException("The owned application process tree did not terminate within five seconds.");
                    }
                }
            }
        }
    }
}
'@
}

function Get-PublishInput([string] $Directory, [string] $Mode) {
    $resolved = Resolve-Path -LiteralPath $Directory
    if ($resolved.Provider.Name -ne 'FileSystem' -or -not (Test-Path -LiteralPath $resolved.ProviderPath -PathType Container)) {
        throw "$Mode input must be a filesystem directory."
    }
    $root = [System.IO.Path]::TrimEndingDirectorySeparator($resolved.ProviderPath)
    if ((Get-Item -LiteralPath $root -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw "$Mode input must not be a directory link."
    }
    $entries = @(Get-ChildItem -LiteralPath $root -Recurse -Force)
    if (@($entries | Where-Object { $_.Attributes -band [System.IO.FileAttributes]::ReparsePoint }).Count -ne 0) {
        throw "$Mode input contains a link; use a self-contained final publish directory."
    }
    $files = @($entries | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)
    if (@($files | Where-Object Name -EQ 'settings.json').Count -ne 0) {
        throw "$Mode input contains real settings.json; refusing an unsafe publish directory."
    }
    $executable = Join-Path $root 'AliCloudDynamicDNS.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "$Mode input is missing AliCloudDynamicDNS.exe."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $root 'settings.json.example') -PathType Leaf)) {
        throw "$Mode input is missing settings.json.example."
    }

    $reader = [System.IO.BinaryReader]::new([System.IO.File]::OpenRead($executable))
    try {
        if ($reader.BaseStream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw "$Mode executable is not a Windows PE file."
        }
        $reader.BaseStream.Position = 0x3C
        $peOffset = $reader.ReadUInt32()
        if ($peOffset -gt $reader.BaseStream.Length - 6) {
            throw "$Mode executable has an invalid Windows PE header."
        }
        $reader.BaseStream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw "$Mode executable must be a Windows x64 executable."
        }
    }
    finally {
        $reader.Dispose()
    }

    $manifest = @(foreach ($file in $files) {
        [pscustomobject]@{
            RelativePath = [System.IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
            Bytes = $file.Length
            IsPdb = $file.Extension -ieq '.pdb'
            Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    })
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
    return [pscustomobject]@{
        Mode = $Mode
        Directory = $root
        Executable = $executable
        FileVersion = $version.FileVersion
        ProductVersion = $version.ProductVersion
        Files = $manifest
    }
}

function Measure-ZipBytes($InputInfo, [object[]] $Files, [string] $ScratchDirectory, [string] $Name) {
    $archivePath = Join-Path $ScratchDirectory $Name
    try {
        $stream = [System.IO.File]::Open($archivePath, [System.IO.FileMode]::CreateNew)
        try {
            $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
            try {
                foreach ($file in $Files) {
                    $entry = $archive.CreateEntry($file.RelativePath, [System.IO.Compression.CompressionLevel]::Optimal)
                    $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                    $source = [System.IO.File]::OpenRead((Join-Path $InputInfo.Directory $file.RelativePath))
                    try {
                        $destination = $entry.Open()
                        try { $source.CopyTo($destination) }
                        finally { $destination.Dispose() }
                    }
                    finally { $source.Dispose() }
                }
            }
            finally { $archive.Dispose() }
            return $stream.Length
        }
        finally { $stream.Dispose() }
    }
    finally {
        if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
    }
}

function Get-SizeMetrics($InputInfo, [string] $ScratchDirectory) {
    [long] $payloadBytes = 0
    [long] $pdbBytes = 0
    [long] $exeBytes = 0
    foreach ($file in $InputInfo.Files) {
        if ($file.IsPdb) { $pdbBytes += $file.Bytes }
        else { $payloadBytes += $file.Bytes }
        if ($file.RelativePath -ieq 'AliCloudDynamicDNS.exe') { $exeBytes = $file.Bytes }
    }
    $payloadFiles = @($InputInfo.Files | Where-Object { -not $_.IsPdb })
    $payloadZip = Measure-ZipBytes $InputInfo $payloadFiles $ScratchDirectory "$($InputInfo.Mode)-payload.zip"
    $totalZip = if ($pdbBytes -eq 0) { $payloadZip }
        else { Measure-ZipBytes $InputInfo $InputInfo.Files $ScratchDirectory "$($InputInfo.Mode)-total.zip" }
    return [pscustomobject]@{
        Mode = $InputInfo.Mode
        Directory = $InputInfo.Directory
        ExecutableBytes = $exeBytes
        DeploymentPayloadBytes = $payloadBytes
        PdbSymbolBytes = $pdbBytes
        TotalDirectoryBytes = $payloadBytes + $pdbBytes
        EquivalentPayloadZipBytes = $payloadZip
        EquivalentTotalZipBytes = $totalZip
        FileVersion = $InputInfo.FileVersion
        ProductVersion = $InputInfo.ProductVersion
        Files = $InputInfo.Files
    }
}

function Assert-SampleBehavior($Sample, [string] $Scenario) {
    $text = $Sample.StandardOutput + "`n" + $Sample.StandardError
    if ($text.Contains($accessId, [StringComparison]::OrdinalIgnoreCase) -or
        $text.Contains($accessKey, [StringComparison]::OrdinalIgnoreCase) -or
        $text -match '(?i)(Signature|AccessKeyId)=') {
        throw 'Application output exposed synthetic credentials or a signed request; output was not retained.'
    }
    if ($text -match '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F-\x9F]' -or
        $text -match '(?im)^\s*at\s+\S+\(|Unhandled exception|System\.Text\.Json\.JsonException') {
        throw 'Application emitted terminal controls or a raw exception instead of the expected safe diagnostic.'
    }
    if ($Scenario -eq 'help') {
        if ($Sample.ExitCode -ne 0 -or $text -notmatch '--file' -or $text -notmatch '--interval') {
            throw 'Help scenario must exit 0 and identify both --file and --interval.'
        }
    }
    elseif ($Sample.ExitCode -ne 1 -or $text -notmatch 'SubDomains\[1\]\.Type must be A or AAAA\.') {
        throw 'Nested config scenario must exit 1 with the expected SubDomains[1].Type validation diagnostic, before network initialization.'
    }
}

function Get-Distribution([double[]] $Values) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { throw 'Cannot summarize an empty measurement set.' }
    $middle = [int][Math]::Floor($sorted.Count / 2)
    $median = if ($sorted.Count % 2 -eq 0) { ($sorted[$middle - 1] + $sorted[$middle]) / 2.0 } else { $sorted[$middle] }
    return [pscustomobject]@{
        Median = $median
        P95 = $sorted[[int][Math]::Ceiling(0.95 * $sorted.Count) - 1]
    }
}

function Save-Report {
    # The report path was reserved with CreateNew; overwrite only our own report.
    $json = $report | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($reportStagingPath, $json, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::Move($reportStagingPath, $reportPath, $true)
}

$baseline = Get-PublishInput $BaselineDirectory 'Baseline'
$aot = Get-PublishInput $AotDirectory 'Aot'
if ($baseline.Directory -ieq $aot.Directory) {
    throw 'BaselineDirectory and AotDirectory must identify distinct publish directories.'
}
if ($baseline.ProductVersion -and $aot.ProductVersion -and $baseline.ProductVersion -cne $aot.ProductVersion) {
    throw 'Executable product versions differ; publish both modes from the same source and version.'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'TempFiles') (
        'windows-publish-comparison-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
}
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$outputPath = [System.IO.Path]::TrimEndingDirectorySeparator($outputPath)
foreach ($inputInfo in @($baseline, $aot)) {
    if ($outputPath -ieq $inputInfo.Directory -or
        $outputPath.StartsWith($inputInfo.Directory + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OutputDirectory must not be inside either published input directory.'
    }
}
[void][System.IO.Directory]::CreateDirectory($outputPath)
$reportPath = Join-Path $outputPath 'comparison.json'
$reservation = [System.IO.File]::Open($reportPath, [System.IO.FileMode]::CreateNew)
$reservation.Dispose()
$reportStagingPath = Join-Path $outputPath ('.comparison-' + [Guid]::NewGuid().ToString('N') + '.tmp')
$scratchPath = $null
$accessId = 'BENCHMARK_SYNTHETIC_ACCESS_ID_MUST_NOT_APPEAR'
$accessKey = 'BENCHMARK_SYNTHETIC_ACCESS_KEY_MUST_NOT_APPEAR'
$samples = [System.Collections.Generic.List[object]]::new()
$runtimeOverrides = [ordered]@{}
foreach ($name in @('DOTNET_TieredCompilation', 'DOTNET_TieredPGO', 'DOTNET_ReadyToRun', 'DOTNET_gcServer',
        'COMPlus_TieredCompilation', 'COMPlus_TieredPGO', 'COMPlus_ReadyToRun', 'COMPlus_gcServer')) {
    $runtimeOverrides[$name] = [Environment]::GetEnvironmentVariable($name)
}
$report = [ordered]@{
    SchemaVersion = 1
    Status = 'Running'
    StartedUtc = [DateTime]::UtcNow.ToString('o')
    CompletedUtc = $null
    Failure = $null
    Environment = [ordered]@{
        MachineName = [Environment]::MachineName
        OperatingSystem = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        OSVersion = [Environment]::OSVersion.VersionString
        OSArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        ProcessArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        LogicalProcessorCount = [Environment]::ProcessorCount
        ProcessorIdentifier = [Environment]::GetEnvironmentVariable('PROCESSOR_IDENTIFIER')
        PowerShellVersion = $PSVersionTable.PSVersion.ToString()
        HarnessRuntime = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        StopwatchFrequency = [System.Diagnostics.Stopwatch]::Frequency
        RuntimeOverrides = $runtimeOverrides
    }
    Settings = [ordered]@{
        IterationsPerModePerScenario = $Iterations
        WarmupsPerModePerScenario = $Warmup
        Scenarios = @('help', 'nested-config-validation')
        Order = 'Baseline/Aot on odd iterations; Aot/Baseline on even iterations, for warmup and measurement separately.'
        LaunchTimeoutMilliseconds = 30000
        OutputDrainTimeoutMilliseconds = 5000
        StandardOutputAndError = 'Redirected UTF-8, concurrently drained, maximum 1048576 characters each; NO_COLOR=1; stdin closed.'
        WallTime = 'Stopwatch immediately before Process.Start until bounded WaitForExit returns; excludes subsequent output drain and accounting.'
        CpuAndMemory = 'GetProcessTimes and GetProcessMemoryInfo on retained launch handle after exit, before disposal. CPU zero may be real Windows accounting granularity; unavailable metrics fail the run.'
        Percentiles = 'Median (mean of middle pair for even count), p95 nearest-rank ceil(0.95*N); warmups excluded.'
        Compression = 'New ZIP per mode, Optimal/Deflate, identical method and fixed 1980-01-01 entry timestamps. Payload excludes only .pdb; total includes .pdb. ZIPs are measured then deleted.'
        Scope = 'Warm-file-cache repeated process startup/CLI and source-generated nested config validation only; not disk cold-start, DNS throughput, or steady-state memory. Same source/build settings remain the publisher/caller responsibility.'
        SyntheticConfiguration = 'Comment, lower-case properties, unknown property, trailing commas, valid A target followed by unsupported CNAME. Validation fails before networking; no live credentials.'
    }
    Sizes = @()
    Samples = $samples
    Statistics = @()
}

try {
    Save-Report
    $scratchPath = Join-Path $outputPath ('.work-' + [Guid]::NewGuid().ToString('N'))
    [void][System.IO.Directory]::CreateDirectory($scratchPath)
    $configurationPath = Join-Path $scratchPath 'nested-targets.json'
    $configuration = @"
{
  // Match the release smoke test's source-generated nested deserialization path.
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
"@
    [System.IO.File]::WriteAllText($configurationPath, $configuration, [System.Text.UTF8Encoding]::new($false))
    $report.Sizes = @((Get-SizeMetrics $baseline $scratchPath), (Get-SizeMetrics $aot $scratchPath))
    Save-Report

    foreach ($scenario in @('help', 'nested-config-validation')) {
        $arguments = if ($scenario -eq 'help') { [string[]]@('--help') }
            else { [string[]]@('--file', $configurationPath, '--interval', '0') }
        foreach ($phase in @('Warmup', 'Measured')) {
            $count = if ($phase -eq 'Warmup') { $Warmup } else { $Iterations }
            for ($iteration = 1; $iteration -le $count; $iteration++) {
                $orderedInputs = if ($iteration % 2 -eq 1) { @($baseline, $aot) } else { @($aot, $baseline) }
                for ($position = 0; $position -lt $orderedInputs.Count; $position++) {
                    $inputInfo = $orderedInputs[$position]
                    try {
                        $sample = [AliDDNSNet.WindowsPublishBenchmark.Runner]::Run(
                            $inputInfo.Executable, $scratchPath, $arguments, 30000, 5000)
                        Assert-SampleBehavior $sample $scenario
                    }
                    catch {
                        throw "Launch rejected ($($inputInfo.Mode), $scenario, $phase iteration $iteration): $($_.Exception.Message)"
                    }
                    $samples.Add([pscustomobject]@{
                        Sequence = $samples.Count + 1
                        Mode = $inputInfo.Mode
                        Scenario = $scenario
                        Phase = $phase
                        Iteration = $iteration
                        PositionInPair = $position + 1
                        ExitCode = $sample.ExitCode
                        WallMilliseconds = $sample.WallMilliseconds
                        ProcessLifetimeMilliseconds = $sample.ProcessLifetimeMilliseconds
                        CpuMilliseconds = $sample.CpuMilliseconds
                        KernelCpu100Nanoseconds = $sample.KernelCpu100Nanoseconds
                        UserCpu100Nanoseconds = $sample.UserCpu100Nanoseconds
                        PeakWorkingSetBytes = $sample.PeakWorkingSetBytes
                        StandardOutput = $sample.StandardOutput
                        StandardError = $sample.StandardError
                    })
                }
            }
            # Persist outside launch timings; never write between members of a pair.
            Save-Report
        }
    }

    $report.Statistics = @(foreach ($scenario in @('help', 'nested-config-validation')) {
        foreach ($mode in @('Baseline', 'Aot')) {
            $measured = @($samples | Where-Object { $_.Phase -eq 'Measured' -and $_.Mode -eq $mode -and $_.Scenario -eq $scenario })
            if ($measured.Count -ne $Iterations) { throw 'Incomplete measured sample set.' }
            [pscustomobject]@{
                Scenario = $scenario
                Mode = $mode
                Count = $measured.Count
                WallMilliseconds = Get-Distribution ([double[]]$measured.WallMilliseconds)
                CpuMilliseconds = Get-Distribution ([double[]]$measured.CpuMilliseconds)
                PeakWorkingSetBytes = Get-Distribution ([double[]]$measured.PeakWorkingSetBytes)
            }
        }
    })
    $report.Status = 'Succeeded'
    $report.CompletedUtc = [DateTime]::UtcNow.ToString('o')
    Save-Report

    Write-Host 'Windows publish comparison (warm file cache; median / p95; no DNS/network workload)'
    $report.Statistics | Select-Object Scenario, Mode, Count,
        @{ Name = 'Wall ms'; Expression = { '{0:F3} / {1:F3}' -f $_.WallMilliseconds.Median, $_.WallMilliseconds.P95 } },
        @{ Name = 'CPU ms'; Expression = { '{0:F3} / {1:F3}' -f $_.CpuMilliseconds.Median, $_.CpuMilliseconds.P95 } },
        @{ Name = 'Peak WS MiB'; Expression = { '{0:F3} / {1:F3}' -f ($_.PeakWorkingSetBytes.Median / 1MB), ($_.PeakWorkingSetBytes.P95 / 1MB) } } |
        Format-Table -AutoSize | Out-Host
    $report.Sizes | Select-Object Mode, ExecutableBytes, DeploymentPayloadBytes, PdbSymbolBytes,
        TotalDirectoryBytes, EquivalentPayloadZipBytes, EquivalentTotalZipBytes | Format-Table -AutoSize | Out-Host
    Write-Host "Raw samples, manifests and environment: $reportPath"
    [pscustomobject]@{ OutputDirectory = $outputPath; ResultPath = $reportPath }
}
catch {
    $report.Status = 'Failed'
    $report.CompletedUtc = [DateTime]::UtcNow.ToString('o')
    $report.Failure = $_.Exception.Message.Replace($accessId, '[redacted]').Replace($accessKey, '[redacted]')
    Save-Report
    throw
}
finally {
    if ($scratchPath -and (Test-Path -LiteralPath $scratchPath)) {
        Remove-Item -LiteralPath $scratchPath -Recurse -Force
    }
    if (Test-Path -LiteralPath $reportStagingPath) {
        Remove-Item -LiteralPath $reportStagingPath -Force
    }
}
