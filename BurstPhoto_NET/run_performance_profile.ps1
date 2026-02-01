# Performance Profiling Runner for BurstPhoto
# Date: 2026-01-31
# Purpose: Run performance profiling with detailed timing metrics

param(
    [int]$Iterations = 3,           # Number of iterations per test for averaging
    [switch]$WarmupRun,             # Run a warmup pass before timing
    [switch]$IncludeSpatial,        # Include spatial (Fast) mode tests
    [switch]$IncludeFrequency,      # Include frequency (HigherQuality) mode tests
    [string]$OutputCsv = "performance_results.csv",
    [switch]$Verbose
)

$ErrorActionPreference = "Continue"

# Paths
$cliPath = ".\BurstPhoto.CLI\bin\Release\net10.0-windows\BurstPhoto.CLI.exe"
$inputPath = ".\Burst Samples\Bracketed Exposure\Input"
$baseOutputPath = "C:\Users\maxwe\Desktop\PerfProfile_$(Get-Date -Format 'yyyyMMdd_HHmmss')"

# Check if CLI exists
if (-not (Test-Path $cliPath)) {
    Write-Host "[ERROR] CLI not found at: $cliPath" -ForegroundColor Red
    Write-Host "Please build in Release mode first: dotnet build -c Release"
    exit 1
}

# Input files
$input1 = "$inputPath\DJI_20250925172104_0018_D.DNG"
$input2 = "$inputPath\DJI_20250925172104_0019_D.DNG"
$input3 = "$inputPath\DJI_20250925172104_0020_D.DNG"

# Verify inputs exist
foreach ($input in @($input1, $input2, $input3)) {
    if (-not (Test-Path $input)) {
        Write-Host "[ERROR] Input file not found: $input" -ForegroundColor Red
        exit 1
    }
}

# Default to both modes if neither specified
if (-not $IncludeSpatial -and -not $IncludeFrequency) {
    $IncludeSpatial = $true
    $IncludeFrequency = $true
}

# Test configurations for profiling
$profileTests = @()

if ($IncludeSpatial) {
    $profileTests += @(
        @{
            Name = "Spatial_Small_NR10"
            Algorithm = "Fast"
            TileSize = "Small"
            SearchDistance = "Medium"
            NoiseReduction = "10"
            ExposureControl = "Off"
        },
        @{
            Name = "Spatial_Medium_NR10"
            Algorithm = "Fast"
            TileSize = "Medium"
            SearchDistance = "Medium"
            NoiseReduction = "10"
            ExposureControl = "Off"
        },
        @{
            Name = "Spatial_Large_NR10"
            Algorithm = "Fast"
            TileSize = "Large"
            SearchDistance = "Medium"
            NoiseReduction = "10"
            ExposureControl = "Off"
        },
        @{
            Name = "Spatial_Medium_NR5"
            Algorithm = "Fast"
            TileSize = "Medium"
            SearchDistance = "Medium"
            NoiseReduction = "5"
            ExposureControl = "Off"
        },
        @{
            Name = "Spatial_Medium_NR20"
            Algorithm = "Fast"
            TileSize = "Medium"
            SearchDistance = "Medium"
            NoiseReduction = "20"
            ExposureControl = "Off"
        }
    )
}

if ($IncludeFrequency) {
    $profileTests += @(
        @{
            Name = "Frequency_Small_NR10"
            Algorithm = "HigherQuality"
            TileSize = "Small"
            SearchDistance = "Medium"
            NoiseReduction = "10"
            ExposureControl = "Off"
        },
        @{
            Name = "Frequency_Medium_NR10"
            Algorithm = "HigherQuality"
            TileSize = "Medium"
            SearchDistance = "Medium"
            NoiseReduction = "10"
            ExposureControl = "Off"
        },
        @{
            Name = "Frequency_Large_NR10"
            Algorithm = "HigherQuality"
            TileSize = "Large"
            SearchDistance = "Medium"
            NoiseReduction = "10"
            ExposureControl = "Off"
        },
        @{
            Name = "Frequency_Medium_NR5"
            Algorithm = "HigherQuality"
            TileSize = "Medium"
            SearchDistance = "Medium"
            NoiseReduction = "5"
            ExposureControl = "Off"
        },
        @{
            Name = "Frequency_Medium_NR20"
            Algorithm = "HigherQuality"
            TileSize = "Medium"
            SearchDistance = "Medium"
            NoiseReduction = "20"
            ExposureControl = "Off"
        }
    )
}

# Results storage
$results = @()

function Run-SingleTest {
    param(
        [hashtable]$Test,
        [string]$OutputFolder,
        [int]$Iteration
    )

    $argsArray = @(
        "process",
        $input1,
        $input2,
        $input3,
        "--algorithm", $Test.Algorithm,
        "--tile-size", $Test.TileSize,
        "--search-distance", $Test.SearchDistance,
        "--noise-reduction", $Test.NoiseReduction,
        "--exposure-control", $Test.ExposureControl,
        "--gpu", "1",
        "--profile",  # Enable internal profiling output
        "-o", $OutputFolder
    )

    # Capture detailed timing
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        $output = & $cliPath @argsArray 2>&1
        $exitCode = $LASTEXITCODE
        $stopwatch.Stop()

        $result = @{
            TestName = $Test.Name
            Algorithm = $Test.Algorithm
            TileSize = $Test.TileSize
            SearchDistance = $Test.SearchDistance
            NoiseReduction = $Test.NoiseReduction
            Iteration = $Iteration
            TotalTimeMs = $stopwatch.ElapsedMilliseconds
            Success = ($exitCode -eq 0)
            ExitCode = $exitCode
        }

        # Parse internal timing from output if available
        $outputStr = $output -join "`n"

        # Look for timing markers in output (format: [PERF] StageName: XXXms)
        $perfLines = $output | Where-Object { $_ -match '\[PERF\]' }
        foreach ($line in $perfLines) {
            if ($line -match '\[PERF\]\s+(\w+):\s+(\d+)ms') {
                $stageName = $matches[1]
                $stageTime = [int]$matches[2]
                $result["Stage_$stageName"] = $stageTime
            }
        }

        # Look for GPU memory usage if reported
        if ($outputStr -match 'GPU Memory.*?(\d+)\s*MB') {
            $result["GpuMemoryMB"] = [int]$matches[1]
        }

        return $result
    }
    catch {
        $stopwatch.Stop()
        return @{
            TestName = $Test.Name
            Algorithm = $Test.Algorithm
            TileSize = $Test.TileSize
            SearchDistance = $Test.SearchDistance
            NoiseReduction = $Test.NoiseReduction
            Iteration = $Iteration
            TotalTimeMs = $stopwatch.ElapsedMilliseconds
            Success = $false
            ExitCode = -1
            Error = $_.Exception.Message
        }
    }
}

# Header
Write-Host ""
Write-Host "======================================================" -ForegroundColor Cyan
Write-Host "       BurstPhoto Performance Profiling Suite" -ForegroundColor Cyan
Write-Host "======================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Configuration:" -ForegroundColor Yellow
Write-Host "  Iterations per test: $Iterations"
Write-Host "  Warmup run: $WarmupRun"
Write-Host "  Include Spatial (Fast): $IncludeSpatial"
Write-Host "  Include Frequency (HQ): $IncludeFrequency"
Write-Host "  Total test configs: $($profileTests.Count)"
Write-Host "  Total runs: $($profileTests.Count * $Iterations)"
Write-Host "  Output CSV: $OutputCsv"
Write-Host ""
Write-Host "Input files:"
Write-Host "  $input1"
Write-Host "  $input2"
Write-Host "  $input3"
Write-Host ""

# Create output directory
New-Item -ItemType Directory -Force -Path $baseOutputPath | Out-Null

# Warmup run (if requested)
if ($WarmupRun) {
    Write-Host "======================================================" -ForegroundColor Yellow
    Write-Host "  Running warmup pass (results discarded)..." -ForegroundColor Yellow
    Write-Host "======================================================" -ForegroundColor Yellow

    $warmupTest = $profileTests[0]
    $warmupFolder = Join-Path $baseOutputPath "warmup"
    New-Item -ItemType Directory -Force -Path $warmupFolder | Out-Null

    Write-Host "  Warmup: $($warmupTest.Name)"
    $warmupResult = Run-SingleTest -Test $warmupTest -OutputFolder $warmupFolder -Iteration 0
    Write-Host "  Warmup complete: $($warmupResult.TotalTimeMs)ms"
    Write-Host ""

    # Clean up warmup output
    Remove-Item -Path $warmupFolder -Recurse -Force -ErrorAction SilentlyContinue
}

# Main profiling loop
$testNumber = 1
$totalTests = $profileTests.Count * $Iterations

Write-Host "======================================================" -ForegroundColor Green
Write-Host "  Starting Performance Profiling..." -ForegroundColor Green
Write-Host "======================================================" -ForegroundColor Green
Write-Host ""

foreach ($test in $profileTests) {
    Write-Host "----------------------------------------" -ForegroundColor Cyan
    Write-Host "Test: $($test.Name)" -ForegroundColor Cyan
    Write-Host "  Algorithm: $($test.Algorithm), TileSize: $($test.TileSize)"
    Write-Host "----------------------------------------"

    $testTimes = @()

    for ($i = 1; $i -le $Iterations; $i++) {
        $progress = [math]::Round(($testNumber / $totalTests) * 100, 1)
        Write-Host "  Iteration $i/$Iterations... " -NoNewline

        $outputFolder = Join-Path $baseOutputPath "$($test.Name)_iter$i"
        New-Item -ItemType Directory -Force -Path $outputFolder | Out-Null

        $result = Run-SingleTest -Test $test -OutputFolder $outputFolder -Iteration $i
        $results += $result
        $testTimes += $result.TotalTimeMs

        if ($result.Success) {
            Write-Host "$($result.TotalTimeMs)ms" -ForegroundColor Green
        } else {
            Write-Host "FAILED (exit: $($result.ExitCode))" -ForegroundColor Red
        }

        $testNumber++

        # Clean up output files to save disk space (keep logs)
        Get-ChildItem -Path $outputFolder -Filter "*.dng" | Remove-Item -Force -ErrorAction SilentlyContinue
    }

    # Summary for this test
    if ($testTimes.Count -gt 0) {
        $avgTime = [math]::Round(($testTimes | Measure-Object -Average).Average, 2)
        $minTime = ($testTimes | Measure-Object -Minimum).Minimum
        $maxTime = ($testTimes | Measure-Object -Maximum).Maximum
        $stdDev = 0
        if ($testTimes.Count -gt 1) {
            $mean = ($testTimes | Measure-Object -Average).Average
            $sumSquares = ($testTimes | ForEach-Object { [math]::Pow($_ - $mean, 2) } | Measure-Object -Sum).Sum
            $stdDev = [math]::Round([math]::Sqrt($sumSquares / ($testTimes.Count - 1)), 2)
        }
        Write-Host "  Summary: Avg=${avgTime}ms, Min=${minTime}ms, Max=${maxTime}ms, StdDev=${stdDev}ms" -ForegroundColor Yellow
    }
    Write-Host ""
}

# Export results to CSV
Write-Host "======================================================" -ForegroundColor Cyan
Write-Host "  Exporting Results..." -ForegroundColor Cyan
Write-Host "======================================================" -ForegroundColor Cyan

$csvPath = Join-Path $baseOutputPath $OutputCsv

# Convert results to objects for CSV export
$csvData = $results | ForEach-Object {
    $obj = [PSCustomObject]@{
        TestName = $_.TestName
        Algorithm = $_.Algorithm
        TileSize = $_.TileSize
        SearchDistance = $_.SearchDistance
        NoiseReduction = $_.NoiseReduction
        Iteration = $_.Iteration
        TotalTimeMs = $_.TotalTimeMs
        Success = $_.Success
        ExitCode = $_.ExitCode
    }

    # Add any stage timings dynamically
    $_.Keys | Where-Object { $_ -like "Stage_*" } | ForEach-Object {
        $obj | Add-Member -NotePropertyName $_ -NotePropertyValue $_[$_] -Force
    }

    if ($_.GpuMemoryMB) {
        $obj | Add-Member -NotePropertyName "GpuMemoryMB" -NotePropertyValue $_.GpuMemoryMB -Force
    }

    $obj
}

$csvData | Export-Csv -Path $csvPath -NoTypeInformation
Write-Host "Results exported to: $csvPath"

# Generate summary report
Write-Host ""
Write-Host "======================================================" -ForegroundColor Green
Write-Host "  Performance Summary Report" -ForegroundColor Green
Write-Host "======================================================" -ForegroundColor Green
Write-Host ""

# Group by test name and calculate statistics
$summary = $results | Group-Object TestName | ForEach-Object {
    $times = $_.Group | Where-Object { $_.Success } | ForEach-Object { $_.TotalTimeMs }
    if ($times.Count -gt 0) {
        $avg = [math]::Round(($times | Measure-Object -Average).Average, 0)
        $min = ($times | Measure-Object -Minimum).Minimum
        $max = ($times | Measure-Object -Maximum).Maximum

        [PSCustomObject]@{
            Test = $_.Name
            AvgMs = $avg
            MinMs = $min
            MaxMs = $max
            Runs = $times.Count
            Algorithm = $_.Group[0].Algorithm
        }
    }
}

# Display summary table
Write-Host "Test Results (sorted by average time):" -ForegroundColor Yellow
Write-Host ""
Write-Host ("{0,-30} {1,10} {2,10} {3,10} {4,6}" -f "Test", "Avg(ms)", "Min(ms)", "Max(ms)", "Runs")
Write-Host ("-" * 70)

$summary | Sort-Object AvgMs | ForEach-Object {
    $color = if ($_.Algorithm -eq "Fast") { "Cyan" } else { "Magenta" }
    Write-Host ("{0,-30} {1,10} {2,10} {3,10} {4,6}" -f $_.Test, $_.AvgMs, $_.MinMs, $_.MaxMs, $_.Runs) -ForegroundColor $color
}

Write-Host ""

# Compare Spatial vs Frequency if both included
if ($IncludeSpatial -and $IncludeFrequency) {
    Write-Host "Algorithm Comparison:" -ForegroundColor Yellow

    $spatialAvg = ($summary | Where-Object { $_.Algorithm -eq "Fast" } | Measure-Object -Property AvgMs -Average).Average
    $freqAvg = ($summary | Where-Object { $_.Algorithm -eq "HigherQuality" } | Measure-Object -Property AvgMs -Average).Average

    if ($spatialAvg -and $freqAvg) {
        $ratio = [math]::Round($freqAvg / $spatialAvg, 2)
        Write-Host "  Spatial (Fast) average: $([math]::Round($spatialAvg, 0))ms" -ForegroundColor Cyan
        Write-Host "  Frequency (HQ) average: $([math]::Round($freqAvg, 0))ms" -ForegroundColor Magenta
        Write-Host "  Frequency/Spatial ratio: ${ratio}x"
    }
}

Write-Host ""
Write-Host "Output folder: $baseOutputPath"
Write-Host ""

# Return summary object for programmatic use
return @{
    Results = $results
    Summary = $summary
    CsvPath = $csvPath
    OutputPath = $baseOutputPath
}
