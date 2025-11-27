using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BurstPhoto.Core.Models;

public class ProcessingProgress : INotifyPropertyChanged
{
    private int _progressInt;
    private bool _includesConversion;
    private bool _showNonBayerHqAlert;
    private bool _showNonBayerExposureAlert;
    private bool _showNonBayerBitDepthAlert;
    private bool _showExposureBitDepthAlert;

    public int ProgressInt
    {
        get => _progressInt;
        set => SetField(ref _progressInt, value);
    }

    public bool IncludesConversion
    {
        get => _includesConversion;
        set => SetField(ref _includesConversion, value);
    }

    public bool ShowNonBayerHqAlert
    {
        get => _showNonBayerHqAlert;
        set => SetField(ref _showNonBayerHqAlert, value);
    }

    public bool ShowNonBayerExposureAlert
    {
        get => _showNonBayerExposureAlert;
        set => SetField(ref _showNonBayerExposureAlert, value);
    }

    public bool ShowNonBayerBitDepthAlert
    {
        get => _showNonBayerBitDepthAlert;
        set => SetField(ref _showNonBayerBitDepthAlert, value);
    }

    public bool ShowExposureBitDepthAlert
    {
        get => _showExposureBitDepthAlert;
        set => SetField(ref _showExposureBitDepthAlert, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
