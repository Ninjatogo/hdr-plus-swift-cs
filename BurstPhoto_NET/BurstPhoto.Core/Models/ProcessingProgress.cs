using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BurstPhoto.Core.Models;

/// <summary>
/// Tracks processing progress and status alerts for the burst photo denoising pipeline.
/// Implements <see cref="INotifyPropertyChanged"/> to support UI data binding.
/// </summary>
/// <remarks>
/// Progress is reported as a scaled integer value from 0 to <see cref="MaxProgressValue"/> (100,000,000)
/// to provide fine-grained progress updates without floating-point precision issues.
/// </remarks>
public class ProcessingProgress : INotifyPropertyChanged
{
    /// <summary>
    /// The maximum progress value representing 100% completion.
    /// </summary>
    /// <remarks>
    /// Using 100,000,000 allows for very fine-grained progress reporting (to 0.000001%)
    /// while avoiding floating-point precision issues in progress calculations.
    /// </remarks>
    public const int MaxProgressValue = 100_000_000;

    private int _progressValue;
    private string _currentStage = string.Empty;
    private bool _includesConversion;
    private bool _showNonBayerHighQualityAlert;
    private bool _showNonBayerExposureAlert;
    private bool _showNonBayerBitDepthAlert;
    private bool _showExposureBitDepthAlert;

    /// <summary>
    /// Gets or sets the current progress value, ranging from 0 to <see cref="MaxProgressValue"/>.
    /// </summary>
    /// <remarks>
    /// To convert to a percentage: <c>progressPercent = ProgressValue / (MaxProgressValue / 100.0)</c>
    /// </remarks>
    public int ProgressValue
    {
        get => _progressValue;
        set => SetField(ref _progressValue, value);
    }

    /// <summary>
    /// Gets or sets the human-readable description of the current processing stage.
    /// </summary>
    /// <example>
    /// Examples: "Loading images...", "Aligning frames...", "Merging frames...", "Writing output..."
    /// </example>
    public string CurrentStage
    {
        get => _currentStage;
        set => SetField(ref _currentStage, value);
    }

    /// <summary>
    /// Gets or sets whether the output will include format conversion (e.g., DNG to TIFF).
    /// </summary>
    public bool IncludesConversion
    {
        get => _includesConversion;
        set => SetField(ref _includesConversion, value);
    }

    /// <summary>
    /// Gets or sets whether to show an alert that higher-quality merging was disabled
    /// because the sensor uses a non-Bayer color filter array (e.g., X-Trans).
    /// </summary>
    /// <remarks>
    /// Higher-quality frequency-domain merging is only supported for Bayer sensors (2x2 pattern).
    /// Non-Bayer sensors (like Fujifilm X-Trans with 6x6 pattern) fall back to fast spatial merging.
    /// </remarks>
    public bool ShowNonBayerHighQualityAlert
    {
        get => _showNonBayerHighQualityAlert;
        set => SetField(ref _showNonBayerHighQualityAlert, value);
    }

    /// <summary>
    /// Gets or sets whether to show an alert that exposure control was disabled
    /// because the sensor uses a non-Bayer color filter array.
    /// </summary>
    /// <remarks>
    /// Exposure control features (tone curves, linear scaling) require Bayer sensor data.
    /// </remarks>
    public bool ShowNonBayerExposureAlert
    {
        get => _showNonBayerExposureAlert;
        set => SetField(ref _showNonBayerExposureAlert, value);
    }

    /// <summary>
    /// Gets or sets whether to show an alert that 16-bit output was disabled
    /// because the sensor uses a non-Bayer color filter array.
    /// </summary>
    /// <remarks>
    /// 16-bit output upscaling is only supported for Bayer sensors with exposure control enabled.
    /// </remarks>
    public bool ShowNonBayerBitDepthAlert
    {
        get => _showNonBayerBitDepthAlert;
        set => SetField(ref _showNonBayerBitDepthAlert, value);
    }

    /// <summary>
    /// Gets or sets whether to show an alert that 16-bit output was disabled
    /// because exposure control is turned off.
    /// </summary>
    /// <remarks>
    /// 16-bit output requires exposure control to be enabled to properly scale pixel values.
    /// </remarks>
    public bool ShowExposureBitDepthAlert
    {
        get => _showExposureBitDepthAlert;
        set => SetField(ref _showExposureBitDepthAlert, value);
    }

    /// <summary>
    /// Updates the progress value and stage description in a single operation.
    /// </summary>
    /// <param name="progressValue">New progress value (0 to <see cref="MaxProgressValue"/>).</param>
    /// <param name="stage">Human-readable description of the current stage.</param>
    public void Update(int progressValue, string stage)
    {
        _progressValue = progressValue;
        _currentStage = stage;
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(CurrentStage));
    }

    /// <summary>
    /// Increments the progress value and updates the stage description.
    /// </summary>
    /// <param name="increment">Amount to add to the current progress value.</param>
    /// <param name="stage">Human-readable description of the current stage.</param>
    public void Increment(int increment, string stage)
    {
        _progressValue += increment;
        _currentStage = stage;
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(CurrentStage));
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises the <see cref="PropertyChanged"/> event for the specified property.
    /// </summary>
    /// <param name="propertyName">The name of the property that changed. Auto-populated by the compiler.</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Sets a backing field and raises <see cref="PropertyChanged"/> if the value changed.
    /// </summary>
    /// <typeparam name="T">The type of the property.</typeparam>
    /// <param name="field">Reference to the backing field.</param>
    /// <param name="value">The new value to set.</param>
    /// <param name="propertyName">The name of the property. Auto-populated by the compiler.</param>
    /// <returns><c>true</c> if the value changed; otherwise, <c>false</c>.</returns>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
