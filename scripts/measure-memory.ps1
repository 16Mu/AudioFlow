param([Parameter(Mandatory)][string]$OldExecutable, [Parameter(Mandatory)][string]$NewExecutable)
$ErrorActionPreference = 'Stop'
$audioMeasureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$audioMeasureDirectory = Join-Path $audioMeasureRoot '.cache\memory-comparison'
$audioToneDirectory = Join-Path $audioMeasureDirectory 'tones'
$audioVerificationDirectory = Join-Path $audioMeasureRoot 'tests\AudioFlow.Verification\bin\Release\net10.0-windows10.0.22000.0'
New-Item -ItemType Directory -Path $audioToneDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $audioVerificationDirectory -File | Copy-Item -Destination $audioToneDirectory -Force
$audioToneProcesses = @()
$audioApplicationProcess = $null
$audioMemoryResults = @()
try {
    $audioMeasureNodes = @()
    $audioMeasureConnections = @()
    for ($audioToneIndex = 1; $audioToneIndex -le 6; $audioToneIndex++) {
        $audioTonePath = Join-Path $audioToneDirectory ('MemorySource' + $audioToneIndex + '.exe')
        Copy-Item -LiteralPath (Join-Path $audioToneDirectory 'AudioFlow.Verification.exe') -Destination $audioTonePath -Force
        $audioToneProcesses += Start-Process -FilePath $audioTonePath -ArgumentList @('--tone', (250 + $audioToneIndex * 50), '180') -WindowStyle Hidden -PassThru
        $audioMeasureNodes += [ordered]@{ Id = 'source-' + $audioToneIndex; Kind = 'Application'; Name = '内存测量音源 ' + $audioToneIndex; Key = $audioTonePath; X = 60; Y = 60 + ($audioToneIndex - 1) * 450; Volume = .2 }
        $audioMeasureNodes += [ordered]@{ Id = 'output-' + $audioToneIndex; Kind = 'Playback'; Name = 'Windows 默认输出'; Key = 'windows-default'; PrimaryOutput = $true; OwnerAppKey = $audioTonePath; X = 530; Y = 60 + ($audioToneIndex - 1) * 450; Volume = 1 }
        $audioMeasureConnections += [ordered]@{ Id = 'route-' + $audioToneIndex; SourceId = 'source-' + $audioToneIndex; TargetId = 'output-' + $audioToneIndex; Mode = 'Playback'; Enabled = $true; Volume = 1; Automatic = $true }
    }
    foreach ($audioVariant in @([ordered]@{ Name = 'before'; Executable = $OldExecutable }, [ordered]@{ Name = 'after'; Executable = $NewExecutable })) {
        $audioVariantDirectory = Join-Path $audioMeasureDirectory $audioVariant.Name
        New-Item -ItemType Directory -Path $audioVariantDirectory -Force | Out-Null
        $audioMeasureWorkspace = [ordered]@{ Version = 1; View = 'Global'; RoutingPaused = $false; LastRoutingEnabled = $true; RestoreRoutingStateOnLaunch = $true;
            Configuration = [ordered]@{ Version = 1; CloseToTray = $false; Nodes = $audioMeasureNodes; Connections = $audioMeasureConnections; Channels = @() } }
        $audioMeasureWorkspace | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $audioVariantDirectory 'workspace.json') -Encoding utf8
        $audioApplicationProcess = Start-Process -FilePath $audioVariant.Executable -ArgumentList @('--data-dir', ('"' + $audioVariantDirectory + '"')) -WindowStyle Hidden -PassThru
        Write-Output ('Measuring ' + $audioVariant.Name + ', PID ' + $audioApplicationProcess.Id)
        Start-Sleep -Seconds 12
        $audioMemorySamples = @()
        for ($audioSampleIndex = 0; $audioSampleIndex -lt 15; $audioSampleIndex++) {
            $audioApplicationProcess.Refresh()
            if ($audioApplicationProcess.HasExited) { throw ('Application exited while measuring ' + $audioVariant.Name) }
            $audioMemorySamples += [ordered]@{ PrivateBytes = $audioApplicationProcess.PrivateMemorySize64; WorkingSetBytes = $audioApplicationProcess.WorkingSet64; CpuSeconds = $audioApplicationProcess.TotalProcessorTime.TotalSeconds }
            if ($audioSampleIndex % 5 -eq 0) { Write-Output ($audioVariant.Name + ' privateMiB=' + [Math]::Round($audioApplicationProcess.PrivateMemorySize64 / 1MB, 1)) }
            Start-Sleep -Seconds 2
        }
        $audioSortedPrivate = @($audioMemorySamples.PrivateBytes | Sort-Object)
        $audioSortedWorking = @($audioMemorySamples.WorkingSetBytes | Sort-Object)
        $audioMemoryResults += [ordered]@{ Variant = $audioVariant.Name; Executable = $audioVariant.Executable; SourceCount = 6; Copies = 0; WarmupSeconds = 12; SampleSeconds = 30;
            MedianPrivateBytes = $audioSortedPrivate[7]; MedianWorkingSetBytes = $audioSortedWorking[7]; Samples = $audioMemorySamples }
        if (-not $audioApplicationProcess.CloseMainWindow()) { throw 'Could not request normal application shutdown' }
        if (-not $audioApplicationProcess.WaitForExit(15000)) { throw 'Application did not restore audio settings and exit in time' }
        $audioApplicationProcess = $null
    }
    $audioMemoryResults | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $audioMeasureDirectory 'report.json') -Encoding utf8
    $audioMemoryResults | ForEach-Object { [pscustomobject]$_ } | Select-Object Variant,MedianPrivateBytes,MedianWorkingSetBytes | Format-Table
} finally {
    if ($audioApplicationProcess -and -not $audioApplicationProcess.HasExited) {
        $audioApplicationProcess.CloseMainWindow() | Out-Null
        $audioApplicationProcess.WaitForExit(15000) | Out-Null
    }
    foreach ($audioToneProcess in $audioToneProcesses) {
        if (-not $audioToneProcess.HasExited) { $audioToneProcess.Kill(); $audioToneProcess.WaitForExit() }
        $audioToneProcess.Dispose()
    }
}
