param(
    [Parameter(Mandatory = $true)]
    [int]$ProcessId,
    [ValidateSet('Idle', 'Dashboard', 'Standard', 'Forge')]
    [string]$Scenario = 'Idle',
    [int]$DurationSeconds = 30,
    [int]$SampleIntervalMilliseconds = 1000,
    [string]$OutputPath,
    [string]$ExpectedExecutablePath,
    [int]$ForgeHostProcessId = 0,
    [string]$ExpectedForgeHostExecutablePath,
    [switch]$AggregateForgeHost,
    [double]$WorkingSetPrivateThresholdMiB = 0
)

$ErrorActionPreference = 'Stop'
$thresholdMiB = if ($WorkingSetPrivateThresholdMiB -gt 0) {
    $WorkingSetPrivateThresholdMiB
} else {
    switch ($Scenario) {
        'Idle' { 160 }
        'Dashboard' { 180 }
        'Standard' { 220 }
        'Forge' { 420 }
    }
}

if (-not ('BohemiXMemory.NativeWindow' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace BohemiXMemory
{
    public static class NativeWindow
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsZoomed(IntPtr hWnd);
    }
}
'@
}

function Get-Percentile {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Values,
        [Parameter(Mandatory = $true)]
        [ValidateRange(0, 1)]
        [double]$Percentile
    )

    $ordered = @($Values | Where-Object { $null -ne $_ } | Sort-Object)
    if ($ordered.Count -eq 0) {
        return 0
    }

    $index = [Math]::Ceiling(($ordered.Count - 1) * $Percentile)
    return $ordered[[int]$index]
}

function Get-WindowSnapshot {
    param([System.Diagnostics.Process]$Process)

    $Process.Refresh()
    $handle = $Process.MainWindowHandle
    if ($handle -eq [IntPtr]::Zero) {
        return [pscustomobject]@{
            WindowState = 'NoWindow'
            WindowPhysicalWidth = 0
            WindowPhysicalHeight = 0
            WindowDpi = 0
        }
    }

    $rect = [BohemiXMemory.NativeWindow+RECT]::new()
    $hasRect = [BohemiXMemory.NativeWindow]::GetWindowRect($handle, [ref]$rect)
    $dpi = [BohemiXMemory.NativeWindow]::GetDpiForWindow($handle)
    $state = if ([BohemiXMemory.NativeWindow]::IsIconic($handle)) {
        'Minimized'
    } elseif ([BohemiXMemory.NativeWindow]::IsZoomed($handle)) {
        'Maximized'
    } else {
        'Normal'
    }

    return [pscustomobject]@{
        WindowState = $state
        WindowPhysicalWidth = if ($hasRect) { [Math]::Max(0, $rect.Right - $rect.Left) } else { 0 }
        WindowPhysicalHeight = if ($hasRect) { [Math]::Max(0, $rect.Bottom - $rect.Top) } else { 0 }
        WindowDpi = [int]$dpi
    }
}

$targetProcess = Get-Process -Id $ProcessId -ErrorAction Stop
$executablePath = $targetProcess.Path
if ([string]::IsNullOrWhiteSpace($executablePath)) {
    throw "Unable to resolve the executable path for process $ProcessId."
}

$executablePath = [IO.Path]::GetFullPath($executablePath)
if (-not [string]::IsNullOrWhiteSpace($ExpectedExecutablePath)) {
    $expectedPath = [IO.Path]::GetFullPath($ExpectedExecutablePath)
    if (-not [string]::Equals($executablePath, $expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Process $ProcessId is running '$executablePath', expected '$expectedPath'."
    }
}

$shouldAggregateForgeHost = $AggregateForgeHost -or $ForgeHostProcessId -gt 0 -or $Scenario -eq 'Forge'
$forgeHostProcess = $null
if ($shouldAggregateForgeHost) {
    if ($ForgeHostProcessId -gt 0) {
        $forgeHostProcess = Get-Process -Id $ForgeHostProcessId -ErrorAction Stop
    } else {
        try {
            $forgeHostInfo = Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" |
                Where-Object { $_.Name -eq 'BohemiX.ForgeHost.exe' } |
                Select-Object -First 1
            if ($forgeHostInfo) {
                $forgeHostProcess = Get-Process -Id $forgeHostInfo.ProcessId -ErrorAction Stop
            }
        } catch {
        }
    }
}

$forgeHostExecutablePath = $null
if ($forgeHostProcess) {
    $forgeHostExecutablePath = [IO.Path]::GetFullPath($forgeHostProcess.Path)
    if (-not [string]::IsNullOrWhiteSpace($ExpectedForgeHostExecutablePath)) {
        $expectedForgeHostPath = [IO.Path]::GetFullPath($ExpectedForgeHostExecutablePath)
        if (-not [string]::Equals($forgeHostExecutablePath, $expectedForgeHostPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "ForgeHost process $($forgeHostProcess.Id) is running '$forgeHostExecutablePath', expected '$expectedForgeHostPath'."
        }
    }
}

$measuredProcessIds = @($ProcessId)
if ($forgeHostProcess) {
    $measuredProcessIds += $forgeHostProcess.Id
}

$appModulePath = $null
$appModulePathSource = 'Unavailable'
try {
    $appModule = $targetProcess.Modules |
        Where-Object { $_.ModuleName -eq 'BohemiX.App.dll' } |
        Select-Object -First 1
    if ($appModule) {
        $appModulePath = $appModule.FileName
        $appModulePathSource = 'ProcessModules'
    }
} catch {
}

if ([string]::IsNullOrWhiteSpace($appModulePath)) {
    $candidate = Join-Path ([IO.Path]::GetDirectoryName($executablePath)) 'BohemiX.App.dll'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        $appModulePath = [IO.Path]::GetFullPath($candidate)
        $appModulePathSource = 'ExecutableDirectoryCandidate'
    }
}

function New-PrivateWorkingSetCounter {
    param([System.Diagnostics.Process]$Process)

    $instanceCandidates = if ((Get-Process -Name $Process.ProcessName -ErrorAction SilentlyContinue).Count -gt 1) {
        0..31 | ForEach-Object { if ($_ -eq 0) { $Process.ProcessName } else { "$($Process.ProcessName)#$($_)" } }
    } else {
        @($Process.ProcessName)
    }

    $instanceName = $instanceCandidates | Where-Object {
        try {
            $idCounter = [System.Diagnostics.PerformanceCounter]::new('Process', 'ID Process', $_, $true)
            $matches = [int]$idCounter.NextValue() -eq $Process.Id
            $idCounter.Dispose()
            $matches
        } catch {
            $false
        }
    } | Select-Object -First 1
    if (-not $instanceName) {
        throw "Unable to resolve the performance-counter instance for process $($Process.Id)."
    }

    $counter = [System.Diagnostics.PerformanceCounter]::new(
        'Process',
        'Working Set - Private',
        $instanceName,
        $true)
    $null = $counter.NextValue()
    return $counter
}

$privateWorkingSetCounters = @{}
$privateWorkingSetCounters[$ProcessId] = New-PrivateWorkingSetCounter $targetProcess
if ($forgeHostProcess) {
    $privateWorkingSetCounters[$forgeHostProcess.Id] = New-PrivateWorkingSetCounter $forgeHostProcess
}
$gpuCounters = @{}

function Update-GpuCounters {
    try {
        $category = [System.Diagnostics.PerformanceCounterCategory]::new('GPU Process Memory')
        $instances = @($category.GetInstanceNames() | Where-Object {
            $instance = $_
            $measuredProcessIds | Where-Object { $instance -like "pid_$_*" }
        })
        foreach ($instance in $instances) {
            foreach ($counterName in @('Dedicated Usage', 'Shared Usage')) {
                $key = "$instance|$counterName"
                if (-not $gpuCounters.ContainsKey($key)) {
                    $counter = [System.Diagnostics.PerformanceCounter]::new(
                        'GPU Process Memory',
                        $counterName,
                        $instance,
                        $true)
                    $null = $counter.NextValue()
                    $gpuCounters[$key] = $counter
                }
            }
        }
    } catch {
    }
}

function Get-GpuUsage {
    Update-GpuCounters
    $dedicatedBytes = 0.0
    $sharedBytes = 0.0
    foreach ($entry in $gpuCounters.GetEnumerator()) {
        try {
            $value = [Math]::Max(0, $entry.Value.NextValue())
            if ($entry.Key.EndsWith('|Dedicated Usage')) {
                $dedicatedBytes += $value
            } else {
                $sharedBytes += $value
            }
        } catch {
        }
    }

    return [pscustomobject]@{
        DedicatedMiB = [Math]::Round($dedicatedBytes / 1MB, 2)
        SharedMiB = [Math]::Round($sharedBytes / 1MB, 2)
    }
}

$samples = [System.Collections.Generic.List[object]]::new()
$sampleCount = [Math]::Max(1, [int][Math]::Ceiling(($DurationSeconds * 1000) / $SampleIntervalMilliseconds))
$logicalProcessorCount = [Math]::Max(1, [Environment]::ProcessorCount)
$previousCpu = ($measuredProcessIds | ForEach-Object {
    (Get-Process -Id $_ -ErrorAction Stop).TotalProcessorTime.TotalSeconds
} | Measure-Object -Sum).Sum
$previousTimestamp = [DateTimeOffset]::Now

try {
    for ($index = 0; $index -lt $sampleCount; $index++) {
        Start-Sleep -Milliseconds $SampleIntervalMilliseconds
        $processes = @($measuredProcessIds | ForEach-Object { Get-Process -Id $_ -ErrorAction Stop })
        $process = $processes | Where-Object { $_.Id -eq $ProcessId } | Select-Object -First 1
        $timestamp = [DateTimeOffset]::Now
        $cpu = ($processes | ForEach-Object { $_.TotalProcessorTime.TotalSeconds } | Measure-Object -Sum).Sum
        $elapsedSeconds = [Math]::Max(0.001, ($timestamp - $previousTimestamp).TotalSeconds)
        $cpuPercent = (($cpu - $previousCpu) / $elapsedSeconds / $logicalProcessorCount) * 100
        $privateWorkingSetValues = @{}
        foreach ($entry in $privateWorkingSetCounters.GetEnumerator()) {
            $privateWorkingSetValues[$entry.Key] = $entry.Value.NextValue()
        }
        $privateWorkingSet = ($privateWorkingSetValues.Values | Measure-Object -Sum).Sum
        $gpu = Get-GpuUsage
        $window = Get-WindowSnapshot $process
        $forgeHostSample = $processes | Where-Object { $_.Id -ne $ProcessId } | Select-Object -First 1
        $samples.Add([pscustomobject]@{
            Timestamp = $timestamp
            NormalizedCpuPercent = [Math]::Round([Math]::Max(0, $cpuPercent), 3)
            WorkingSetPrivateMiB = [Math]::Round($privateWorkingSet / 1MB, 2)
            WorkingSetMiB = [Math]::Round((($processes.WorkingSet64 | Measure-Object -Sum).Sum) / 1MB, 2)
            PrivateBytesMiB = [Math]::Round((($processes.PrivateMemorySize64 | Measure-Object -Sum).Sum) / 1MB, 2)
            Handles = [int](($processes.HandleCount | Measure-Object -Sum).Sum)
            Threads = [int](($processes | ForEach-Object { $_.Threads.Count } | Measure-Object -Sum).Sum)
            MainWorkingSetPrivateMiB = [Math]::Round($privateWorkingSetValues[$ProcessId] / 1MB, 2)
            ForgeHostWorkingSetPrivateMiB = if ($forgeHostSample) { [Math]::Round($privateWorkingSetValues[$forgeHostSample.Id] / 1MB, 2) } else { 0 }
            ForgeHostWorkingSetMiB = if ($forgeHostSample) { [Math]::Round($forgeHostSample.WorkingSet64 / 1MB, 2) } else { 0 }
            ForgeHostPrivateBytesMiB = if ($forgeHostSample) { [Math]::Round($forgeHostSample.PrivateMemorySize64 / 1MB, 2) } else { 0 }
            GpuDedicatedMiB = $gpu.DedicatedMiB
            GpuSharedMiB = $gpu.SharedMiB
            WindowState = $window.WindowState
            WindowPhysicalWidth = $window.WindowPhysicalWidth
            WindowPhysicalHeight = $window.WindowPhysicalHeight
            WindowDpi = $window.WindowDpi
        })
        $previousCpu = $cpu
        $previousTimestamp = $timestamp
    }
} finally {
    foreach ($counter in $privateWorkingSetCounters.Values) {
        $counter.Dispose()
    }
    foreach ($counter in $gpuCounters.Values) {
        $counter.Dispose()
    }
}

$tailCount = [Math]::Min(10, $samples.Count)
$tail = @($samples | Select-Object -Last $tailCount)
$tailFirst = $tail | Select-Object -First 1
$tailLast = $tail | Select-Object -Last 1
$windowStates = @($tail.WindowState | Sort-Object -Unique)
$windowSizes = @($tail | ForEach-Object { "$($_.WindowPhysicalWidth)x$($_.WindowPhysicalHeight)@$($_.WindowDpi)" } | Sort-Object -Unique)
$workingSetDrift = [Math]::Round($tailLast.WorkingSetPrivateMiB - $tailFirst.WorkingSetPrivateMiB, 2)

$result = [pscustomobject]@{
    ProcessId = $ProcessId
    ForgeHostProcessId = if ($forgeHostProcess) { $forgeHostProcess.Id } else { $null }
    AggregatedProcessCount = $measuredProcessIds.Count
    ForgeHostAggregated = $null -ne $forgeHostProcess
    ExecutablePath = $executablePath
    ForgeHostExecutablePath = $forgeHostExecutablePath
    AppModulePath = $appModulePath
    AppModulePathSource = $appModulePathSource
    ExpectedExecutablePathValidated = -not [string]::IsNullOrWhiteSpace($ExpectedExecutablePath)
    ExpectedForgeHostExecutablePathValidated = $null -ne $forgeHostProcess -and -not [string]::IsNullOrWhiteSpace($ExpectedForgeHostExecutablePath)
    Scenario = $Scenario
    Samples = $samples.Count
    StableTailSamples = $tailCount
    DurationSeconds = $DurationSeconds
    SampleIntervalMilliseconds = $SampleIntervalMilliseconds
    WindowStates = $windowStates
    WindowPhysicalConfigurations = $windowSizes
    NormalizedCpuMedianPercent = [Math]::Round((Get-Percentile @($tail.NormalizedCpuPercent) 0.5), 3)
    NormalizedCpuP95Percent = [Math]::Round((Get-Percentile @($tail.NormalizedCpuPercent) 0.95), 3)
    WorkingSetPrivateMedianMiB = [Math]::Round((Get-Percentile @($tail.WorkingSetPrivateMiB) 0.5), 2)
    WorkingSetPrivateP95MiB = [Math]::Round((Get-Percentile @($tail.WorkingSetPrivateMiB) 0.95), 2)
    WorkingSetPrivateThresholdMiB = $thresholdMiB
    PrivateBytesMedianMiB = [Math]::Round((Get-Percentile @($tail.PrivateBytesMiB) 0.5), 2)
    PrivateBytesP95MiB = [Math]::Round((Get-Percentile @($tail.PrivateBytesMiB) 0.95), 2)
    WorkingSetMedianMiB = [Math]::Round((Get-Percentile @($tail.WorkingSetMiB) 0.5), 2)
    WorkingSetP95MiB = [Math]::Round((Get-Percentile @($tail.WorkingSetMiB) 0.95), 2)
    GpuDedicatedMedianMiB = [Math]::Round((Get-Percentile @($tail.GpuDedicatedMiB) 0.5), 2)
    GpuDedicatedP95MiB = [Math]::Round((Get-Percentile @($tail.GpuDedicatedMiB) 0.95), 2)
    GpuSharedMedianMiB = [Math]::Round((Get-Percentile @($tail.GpuSharedMiB) 0.5), 2)
    GpuSharedP95MiB = [Math]::Round((Get-Percentile @($tail.GpuSharedMiB) 0.95), 2)
    GpuCountersAvailable = $gpuCounters.Count -gt 0
    HandlesMedian = [int](Get-Percentile @($tail.Handles) 0.5)
    HandlesP95 = [int](Get-Percentile @($tail.Handles) 0.95)
    HandlesPeak = [int](Get-Percentile @($samples.Handles) 1)
    ThreadsMedian = [int](Get-Percentile @($tail.Threads) 0.5)
    ThreadsP95 = [int](Get-Percentile @($tail.Threads) 0.95)
    ThreadsPeak = [int](Get-Percentile @($samples.Threads) 1)
    WorkingSetPrivateDriftMiB = $workingSetDrift
    PrivateBytesDriftMiB = [Math]::Round($tailLast.PrivateBytesMiB - $tailFirst.PrivateBytesMiB, 2)
    HandlesDrift = [int]($tailLast.Handles - $tailFirst.Handles)
    ThreadsDrift = [int]($tailLast.Threads - $tailFirst.Threads)
    StableTailWorkingSetWithin8MiB = [Math]::Abs($workingSetDrift) -le 8
    Passed = (Get-Percentile @($tail.WorkingSetPrivateMiB) 0.5) -le $thresholdMiB
}

$result | Format-List
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutputPath)
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    }

    [pscustomobject]@{
        Result = $result
        Samples = $samples
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resolvedOutputPath -Encoding utf8
}

if (-not $result.Passed) {
    exit 1
}
