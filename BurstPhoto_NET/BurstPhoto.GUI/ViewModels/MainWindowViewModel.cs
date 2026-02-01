using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Models;
using BurstPhoto.GUI.Services;
using BurstPhoto.Rendering;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BurstPhoto.GUI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly DenoisePipelineFactory _pipelineFactory;
    private CancellationTokenSource? _cts;
    private readonly List<GpuInfo> _gpuInfoList = [];

    // Input files
    [ObservableProperty]
    private ObservableCollection<string> _inputFiles = [];

    // Output directory
    [ObservableProperty]
    private string _outputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    // Algorithm options
    public string[] AlgorithmOptions { get; } = ["Fast", "Higher Quality"];

    [ObservableProperty]
    private int _selectedAlgorithmIndex;

    // Tile size options
    public string[] TileSizeOptions { get; } = ["Small (16px)", "Medium (32px)", "Large (64px)"];

    [ObservableProperty]
    private int _selectedTileSizeIndex = 1; // Default: Medium

    // Search distance options
    public string[] SearchDistanceOptions { get; } = ["Small (128px)", "Medium (64px)", "Large (32px)"];

    [ObservableProperty]
    private int _selectedSearchDistanceIndex = 1; // Default: Medium

    // Noise reduction
    [ObservableProperty]
    private double _noiseReduction = 13.0;

    // Exposure control options
    public string[] ExposureControlOptions { get; } = ["Off", "Linear Full Range", "Linear +1 EV", "Curve 0 EV", "Curve +1 EV"];

    [ObservableProperty]
    private int _selectedExposureControlIndex = 1; // Default: Linear Full Range

    // Bit depth options
    public string[] BitDepthOptions { get; } = ["Native", "16-bit"];

    [ObservableProperty]
    private int _selectedBitDepthIndex; // Default: Native

    // GPU options
    public ObservableCollection<string> GpuOptions { get; } = [];

    [ObservableProperty]
    private int _selectedGpuIndex;

    // Advanced options
    [ObservableProperty]
    private bool _enableDebugDump;

    [ObservableProperty]
    private bool _enableFftValidation;

    [ObservableProperty]
    private bool _enableProfiling;

    [ObservableProperty]
    private bool _skipReduceArtifacts;

    [ObservableProperty]
    private bool _enableLogging;

    [ObservableProperty]
    private string _logFilePath = string.Empty;

    // Processing state
    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    // Computed properties
    public bool HasFiles => InputFiles.Count > 0;

    public bool CanProcess => InputFiles.Count >= 2 && !IsProcessing;

    public string InputFilesCountText => InputFiles.Count switch
    {
        0 => "No files selected",
        1 => "1 file selected (need at least 2)",
        _ => $"{InputFiles.Count} files selected"
    };

    public MainWindowViewModel(DenoisePipelineFactory pipelineFactory)
    {
        _pipelineFactory = pipelineFactory;

        InputFiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasFiles));
            OnPropertyChanged(nameof(CanProcess));
            OnPropertyChanged(nameof(InputFilesCountText));
        };

        // Enumerate available GPUs
        EnumerateGpus();
    }

    private void EnumerateGpus()
    {
        GpuOptions.Clear();
        _gpuInfoList.Clear();

        // Add auto-detect option first
        GpuOptions.Add("Auto (prefer discrete GPU)");

        try
        {
            var gpus = GpuEnumerator.EnumerateGpus();
            foreach (var gpu in gpus)
            {
                _gpuInfoList.Add(gpu);
                GpuOptions.Add($"[{gpu.Index}] {gpu.Name} ({gpu.Type})");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"GPU enumeration failed: {ex.Message}";
        }

        SelectedGpuIndex = 0; // Default to auto-detect
    }

    /// <summary>
    /// Gets the selected GPU index for the pipeline, or null for auto-detect.
    /// </summary>
    private int? GetSelectedGpuIndex()
    {
        // Index 0 is "Auto", indices 1+ correspond to _gpuInfoList[index-1]
        if (SelectedGpuIndex <= 0 || SelectedGpuIndex > _gpuInfoList.Count)
            return null;

        return _gpuInfoList[SelectedGpuIndex - 1].Index;
    }

    public void AddFilesFromPaths(string[] paths)
    {
        foreach (var path in paths)
        {
            if (!InputFiles.Contains(path))
            {
                InputFiles.Add(path);
            }
        }
    }

    [RelayCommand]
    private void AddFiles()
    {
        // This will be called from the view after showing the file dialog
        StatusMessage = "Use the file dialog to add files";
    }

    [RelayCommand]
    private void ClearFiles()
    {
        InputFiles.Clear();
        StatusMessage = "Files cleared";
    }

    [RelayCommand]
    private void BrowseOutputDirectory()
    {
        // This will be called from the view after showing the folder dialog
        StatusMessage = "Use the folder dialog to select output directory";
    }

    [RelayCommand]
    private void BrowseLogFile()
    {
        StatusMessage = "Use the file dialog to select log file";
    }

    [RelayCommand]
    private async Task Process()
    {
        if (!CanProcess) return;

        _cts = new CancellationTokenSource();
        IsProcessing = true;
        StatusMessage = "Initializing GPU...";
        ProgressValue = 0;

        IDenoisePipeline? pipeline = null;

        try
        {
            // Create pipeline with selected GPU
            var gpuIndex = GetSelectedGpuIndex();
            pipeline = _pipelineFactory.Create(gpuIndex);

            StatusMessage = "Processing...";

            var options = BuildProcessingOptions();
            var progress = new ProcessingProgress();

            // Subscribe to progress updates (dispatch to UI thread since processing runs on background)
            progress.PropertyChanged += (_, args) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (args.PropertyName == nameof(ProcessingProgress.ProgressValue))
                    {
                        // Convert from 0-100,000,000 to 0-100
                        ProgressValue = progress.ProgressValue / 1_000_000.0;
                    }
                    else if (args.PropertyName == nameof(ProcessingProgress.CurrentStage))
                    {
                        // Update status message with current stage
                        if (!string.IsNullOrEmpty(progress.CurrentStage))
                        {
                            StatusMessage = progress.CurrentStage;
                        }
                    }
                });
            };

            // Run processing on background thread to keep UI responsive
            // This allows cancellation to work and progress updates to be visible
            var inputFiles = InputFiles.ToList(); // Copy to avoid cross-thread collection access
            var outputDir = OutputDirectory;
            var token = _cts.Token;

            var outputPath = await Task.Run(async () =>
                await pipeline.ProcessAsync(
                    inputFiles,
                    options,
                    progress,
                    outputDir,
                    token),
                token);

            StatusMessage = $"Complete: {System.IO.Path.GetFileName(outputPath)}";

            // Show alerts if any constraints were auto-applied
            if (progress.ShowNonBayerHighQualityAlert)
            {
                StatusMessage += " (Algorithm downgraded to Fast for non-Bayer sensor)";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled";
        }
        catch (Exception ex)
        {
            // Log full exception details to console for debugging
            Console.WriteLine($"[ERROR] Processing failed: {ex}");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            _cts?.Dispose();
            _cts = null;

            // Dispose the pipeline to release GPU resources and clear cached results
            pipeline?.Dispose();

            // Force garbage collection to reclaim memory from disposed resources
            // Large image arrays end up on the Large Object Heap (LOH), which requires
            // explicit compaction to return memory to the OS
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusMessage = "Cancelling...";
    }

    private ProcessingOptions BuildProcessingOptions()
    {
        return new ProcessingOptions
        {
            Merging = SelectedAlgorithmIndex == 0 ? MergingAlgorithm.Fast : MergingAlgorithm.HigherQuality,
            TileSize = SelectedTileSizeIndex switch
            {
                0 => TileSizeOption.Small,
                1 => TileSizeOption.Medium,
                _ => TileSizeOption.Large
            },
            SearchDistance = SelectedSearchDistanceIndex switch
            {
                0 => SearchDistanceOption.Small,
                1 => SearchDistanceOption.Medium,
                _ => SearchDistanceOption.Large
            },
            NoiseReduction = NoiseReduction,
            ExposureControl = SelectedExposureControlIndex switch
            {
                0 => ExposureControlOption.Off,
                1 => ExposureControlOption.LinearFullRange,
                2 => ExposureControlOption.Linear1Ev,
                3 => ExposureControlOption.Curve0Ev,
                _ => ExposureControlOption.Curve1Ev
            },
            OutputBitDepth = SelectedBitDepthIndex == 0 ? OutputBitDepthOption.Native : OutputBitDepthOption.Bit16,
            EnableDebugDump = EnableDebugDump,
            EnableFftValidation = EnableFftValidation,
            EnableProfiling = EnableProfiling,
            SkipReduceArtifacts = SkipReduceArtifacts
        };
    }
}
