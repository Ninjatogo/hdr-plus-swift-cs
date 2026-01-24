# Test Matrix Runner for Bracketed Exposure Samples
# Date: 2026-01-21
# Purpose: Run complete test matrix with output logging

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
Write-Host ""

$testNumber = 1
$successCount = 0
$failCount = 0

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

        # Check if successful
        if ($exitCode -eq 0) {
            Write-Host "[SUCCESS] Test completed in $([math]::Round($duration, 2))s"
            $successCount++

            # Count output files
            $outputFiles = Get-ChildItem -Path $outputFolder -Filter "*.dng" -ErrorAction SilentlyContinue
            Write-Host "  Output files: $($outputFiles.Count)"
        } else {
            Write-Host "[FAILED] Test failed with exit code: $exitCode"
            $failCount++
            Write-Host "  Check log: $logFile"
        }
    } catch {
        Write-Host "[ERROR] Test failed with exception: $($_.Exception.Message)"
        $failCount++
        "Error: $($_.Exception.Message)" | Out-File -FilePath $logFile -Encoding UTF8
    }

    Write-Host ""
    $testNumber++
}

Write-Host "========================================"
Write-Host "  Test Matrix Complete"
Write-Host "========================================"
Write-Host "Total Tests: $($tests.Count)"
Write-Host "Successful: $successCount"
Write-Host "Failed: $failCount"
Write-Host ""
Write-Host "Output Location: $baseOutputPath"
Write-Host ""

# Return exit code based on results
if ($failCount -gt 0) {
    exit 1
} else {
    exit 0
}
