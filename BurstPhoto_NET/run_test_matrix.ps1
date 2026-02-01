# Test Matrix Runner for Bracketed Exposure Samples
# Date: 2026-01-21 (Updated: 2026-01-31)
# Purpose: Run complete test matrix with output logging and performance profiling

param(
    [switch]$Profile,           # Enable performance profiling (--profile flag)
    [switch]$ExportCsv,         # Export results to CSV
    [string]$CsvFile = ""       # Custom CSV filename (default: auto-generated)
)

$ErrorActionPreference = "Continue"

# Paths
$cliPath = ".\BurstPhoto.CLI\bin\Release\net10.0-windows\BurstPhoto.CLI.exe"
$inputPath = ".\Burst Samples\Bracketed Exposure\Input"
$baseOutputPath = "C:\Users\maxwe\Desktop\TestOutput"

# Input files
$input1 = "$inputPath\DJI_20250925172104_0018_D.DNG"
$input2 = "$inputPath\DJI_20250925172104_0019_D.DNG"
$input3 = "$inputPath\DJI_20250925172104_0020_D.DNG"

# Test configurations
$tests = @(
    @{
        Name = "Test1_Fast_Small_NR8_Off"
        Algorithm = "Fast"
        TileSize = "Small"
        SearchDistance = "Medium"
        NoiseReduction = "8"
        ExposureControl = "Off"
        Folder = "Debug_Fast_Small_NR8_Off"
    },
    @{
        Name = "Test2_Fast_Large_NR18_L1"
        Algorithm = "Fast"
        TileSize = "Large"
        SearchDistance = "Small"
        NoiseReduction = "18"
        ExposureControl = "Linear1EV"
        Folder = "Debug_Fast_Large_NR18_L1"
    },
    @{
        Name = "Test3_HQ_Med_NR13_LFR"
        Algorithm = "HigherQuality"
        TileSize = "Medium"
        SearchDistance = "Medium"
        NoiseReduction = "13"
        ExposureControl = "LinearFullRange"
        Folder = "Debug_HQ_Med_NR13_LFR"
    },
    @{
        Name = "Test4_Fast_Med_NR5_C0"
        Algorithm = "Fast"
        TileSize = "Medium"
        SearchDistance = "Large"
        NoiseReduction = "5"
        ExposureControl = "Curve0EV"
        Folder = "Debug_Fast_Med_NR5_C0"
    },
    @{
        Name = "Test5_HQ_Small_NR20_C1"
        Algorithm = "HigherQuality"
        TileSize = "Small"
        SearchDistance = "Large"
        NoiseReduction = "20"
        ExposureControl = "Curve1EV"
        Folder = "Debug_HQ_Small_NR20_C1"
    },
    @{
        Name = "Test6_Fast_Large_NR23_Off"
        Algorithm = "Fast"
        TileSize = "Large"
        SearchDistance = "Medium"
        NoiseReduction = "23"
        ExposureControl = "Off"
        Folder = "Debug_Fast_Large_NR23_Off"
    }
)

Write-Host "========================================"
Write-Host "  BurstPhoto Test Matrix Runner"
Write-Host "  Bracketed Exposure Sample Set"
Write-Host "========================================"
Write-Host ""
Write-Host "Input Files:"
Write-Host "  - DJI_20250925172104_0018_D.DNG"
Write-Host "  - DJI_20250925172104_0019_D.DNG"
Write-Host "  - DJI_20250925172104_0020_D.DNG"
Write-Host ""
Write-Host "Output Base: $baseOutputPath"
Write-Host "Total Tests: $($tests.Count)"
if ($Profile) {
    Write-Host "Profiling: ENABLED" -ForegroundColor Green
}
Write-Host ""

$testNumber = 1
$successCount = 0
$failCount = 0

# Results collection for CSV export
$allResults = @()

# Function to parse profiling output
function Parse-ProfilingOutput {
    param([string[]]$Output)

    $perfData = @{
        Total = 0
        Stages = @{}
    }

    foreach ($line in $Output) {
        # Match [PERF] Total: XXXms
        if ($line -match '\[PERF\]\s+Total:\s+(\d+)ms') {
            $perfData.Total = [int]$matches[1]
        }
        # Match [PERF] StageName: XXXms or [PERF] StageName: XXXms (xN, avg=YYYms)
        elseif ($line -match '\[PERF\]\s+(\w+):\s+(\d+)ms') {
            $stageName = $matches[1]
            $stageTime = [int]$matches[2]
            $perfData.Stages[$stageName] = $stageTime
        }
    }

    return $perfData
}

foreach ($test in $tests) {
    Write-Host "========================================"
    Write-Host "Running: $($test.Name) ($testNumber of $($tests.Count))"
    Write-Host "========================================"
    Write-Host "  Algorithm: $($test.Algorithm)"
    Write-Host "  Tile Size: $($test.TileSize)"
    Write-Host "  Search Distance: $($test.SearchDistance)"
    Write-Host "  Noise Reduction: $($test.NoiseReduction)"
    Write-Host "  Exposure Control: $($test.ExposureControl)"
    Write-Host ""

    # Create output folder
    $outputFolder = Join-Path $baseOutputPath $test.Folder
    New-Item -ItemType Directory -Force -Path $outputFolder | Out-Null

    # Build command arguments
    $argsArray = @(
        "process",
        $input1,
        $input2,
        $input3,
        "--algorithm", $test.Algorithm,
        "--tile-size", $test.TileSize,
        "--search-distance", $test.SearchDistance,
        "--noise-reduction", $test.NoiseReduction,
        "--exposure-control", $test.ExposureControl,
        "--gpu", "1",
        "-o", $outputFolder
    )

    # Add profiling flag if requested
    if ($Profile) {
        $argsArray += "--profile"
    }

    # Log file path
    $logFile = Join-Path $outputFolder "test_log.txt"

    Write-Host "Starting test... (logging to test_log.txt)"
    $startTime = Get-Date

    # Run the command and capture output
    try {
        $output = & $cliPath @argsArray 2>&1
        $exitCode = $LASTEXITCODE

        # Write output to log file
        $output | Out-File -FilePath $logFile -Encoding UTF8

        $endTime = Get-Date
        $duration = ($endTime - $startTime).TotalSeconds

        # Parse profiling data if enabled
        $perfData = $null
        if ($Profile) {
            $perfData = Parse-ProfilingOutput -Output $output
        }

        # Check if successful
        if ($exitCode -eq 0) {
            Write-Host "[SUCCESS] Test completed in $([math]::Round($duration, 2))s" -ForegroundColor Green
            $successCount++

            # Count output files
            $outputFiles = Get-ChildItem -Path $outputFolder -Filter "*.dng" -ErrorAction SilentlyContinue
            Write-Host "  Output files: $($outputFiles.Count)"

            # Display profiling summary if enabled
            if ($Profile -and $perfData.Total -gt 0) {
                Write-Host ""
                Write-Host "  Performance Breakdown:" -ForegroundColor Cyan
                Write-Host "    Total (internal): $($perfData.Total)ms"

                # Sort stages by time descending and display top ones
                $sortedStages = $perfData.Stages.GetEnumerator() | Sort-Object Value -Descending
                $displayCount = 0
                foreach ($stage in $sortedStages) {
                    if ($displayCount -lt 5 -and $stage.Key -ne "Unaccounted") {
                        $pct = [math]::Round(($stage.Value / $perfData.Total) * 100, 1)
                        Write-Host "    $($stage.Key): $($stage.Value)ms ($pct%)" -ForegroundColor Gray
                        $displayCount++
                    }
                }
            }

            # Collect result for CSV
            $result = [PSCustomObject]@{
                TestName = $test.Name
                Algorithm = $test.Algorithm
                TileSize = $test.TileSize
                SearchDistance = $test.SearchDistance
                NoiseReduction = $test.NoiseReduction
                ExposureControl = $test.ExposureControl
                WallClockSec = [math]::Round($duration, 2)
                Success = $true
                ExitCode = $exitCode
            }

            # Add profiling columns if available
            if ($Profile -and $perfData) {
                $result | Add-Member -NotePropertyName "TotalMs" -NotePropertyValue $perfData.Total
                foreach ($stage in $perfData.Stages.GetEnumerator()) {
                    $result | Add-Member -NotePropertyName "Stage_$($stage.Key)" -NotePropertyValue $stage.Value
                }
            }

            $allResults += $result

        } else {
            Write-Host "[FAILED] Test failed with exit code: $exitCode" -ForegroundColor Red
            $failCount++
            Write-Host "  Check log: $logFile"

            # Still collect failed result
            $result = [PSCustomObject]@{
                TestName = $test.Name
                Algorithm = $test.Algorithm
                TileSize = $test.TileSize
                SearchDistance = $test.SearchDistance
                NoiseReduction = $test.NoiseReduction
                ExposureControl = $test.ExposureControl
                WallClockSec = [math]::Round($duration, 2)
                Success = $false
                ExitCode = $exitCode
            }
            $allResults += $result
        }
    } catch {
        Write-Host "[ERROR] Test failed with exception: $($_.Exception.Message)" -ForegroundColor Red
        $failCount++
        "Error: $($_.Exception.Message)" | Out-File -FilePath $logFile -Encoding UTF8

        $result = [PSCustomObject]@{
            TestName = $test.Name
            Algorithm = $test.Algorithm
            TileSize = $test.TileSize
            SearchDistance = $test.SearchDistance
            NoiseReduction = $test.NoiseReduction
            ExposureControl = $test.ExposureControl
            WallClockSec = 0
            Success = $false
            ExitCode = -1
        }
        $allResults += $result
    }

    Write-Host ""
    $testNumber++
}

Write-Host "========================================"
Write-Host "  Test Matrix Complete"
Write-Host "========================================"
Write-Host "Total Tests: $($tests.Count)"
Write-Host "Successful: $successCount" -ForegroundColor Green
if ($failCount -gt 0) {
    Write-Host "Failed: $failCount" -ForegroundColor Red
} else {
    Write-Host "Failed: $failCount"
}
Write-Host ""

# Display performance summary if profiling was enabled
if ($Profile -and $allResults.Count -gt 0) {
    Write-Host "========================================"
    Write-Host "  Performance Summary"
    Write-Host "========================================"
    Write-Host ""

    # Group by algorithm
    $spatialTests = $allResults | Where-Object { $_.Algorithm -eq "Fast" -and $_.Success }
    $frequencyTests = $allResults | Where-Object { $_.Algorithm -eq "HigherQuality" -and $_.Success }

    if ($spatialTests.Count -gt 0) {
        $avgSpatial = ($spatialTests | Measure-Object -Property WallClockSec -Average).Average
        Write-Host "Spatial (Fast) Mode:" -ForegroundColor Cyan
        Write-Host "  Tests: $($spatialTests.Count)"
        Write-Host "  Avg Wall Clock: $([math]::Round($avgSpatial, 2))s"
        if ($spatialTests[0].TotalMs) {
            $avgInternal = ($spatialTests | Measure-Object -Property TotalMs -Average).Average
            Write-Host "  Avg Internal: $([math]::Round($avgInternal, 0))ms"
        }
        Write-Host ""
    }

    if ($frequencyTests.Count -gt 0) {
        $avgFrequency = ($frequencyTests | Measure-Object -Property WallClockSec -Average).Average
        Write-Host "Frequency (HigherQuality) Mode:" -ForegroundColor Magenta
        Write-Host "  Tests: $($frequencyTests.Count)"
        Write-Host "  Avg Wall Clock: $([math]::Round($avgFrequency, 2))s"
        if ($frequencyTests[0].TotalMs) {
            $avgInternal = ($frequencyTests | Measure-Object -Property TotalMs -Average).Average
            Write-Host "  Avg Internal: $([math]::Round($avgInternal, 0))ms"
        }
        Write-Host ""
    }

    if ($spatialTests.Count -gt 0 -and $frequencyTests.Count -gt 0) {
        $ratio = [math]::Round($avgFrequency / $avgSpatial, 2)
        Write-Host "Frequency/Spatial Ratio: ${ratio}x"
        Write-Host ""
    }
}

# Export to CSV if requested
if ($ExportCsv -or $CsvFile) {
    if (-not $CsvFile) {
        $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
        $CsvFile = Join-Path $baseOutputPath "test_matrix_results_$timestamp.csv"
    }

    $allResults | Export-Csv -Path $CsvFile -NoTypeInformation
    Write-Host "Results exported to: $CsvFile" -ForegroundColor Green
    Write-Host ""
}

Write-Host "Output Location: $baseOutputPath"
Write-Host ""

# Return exit code based on results
if ($failCount -gt 0) {
    exit 1
} else {
    exit 0
}
