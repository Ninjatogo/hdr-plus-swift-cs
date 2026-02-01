using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using BurstPhoto.GUI.ViewModels;

namespace BurstPhoto.GUI.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public MainWindow()
    {
        InitializeComponent();

        // Wire up drag-and-drop
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // Using obsolete API for drag-drop compatibility
        var files = e.Data.GetFiles();
#pragma warning restore CS0618
        e.DragEffects = files != null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // Using obsolete API for drag-drop compatibility
        var files = e.Data.GetFiles();
#pragma warning restore CS0618
        if (files == null) return;

        var paths = files
            .Select(f => f.Path.LocalPath)
            .Where(p => IsRawFile(p))
            .ToArray();

        if (paths.Length > 0)
        {
            ViewModel?.AddFilesFromPaths(paths);
        }
    }

    private static bool IsRawFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".dng" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".orf" or ".rw2" or ".raf" or ".pef";
    }

    public async void OnAddFilesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select RAW Files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("RAW Images")
                {
                    Patterns = ["*.dng", "*.cr2", "*.cr3", "*.nef", "*.arw", "*.orf", "*.rw2", "*.raf", "*.pef"]
                },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] }
            ]
        });

        if (files.Count > 0)
        {
            var paths = files.Select(f => f.Path.LocalPath).ToArray();
            ViewModel?.AddFilesFromPaths(paths);
        }
    }

    public async void OnBrowseOutputClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Output Directory",
            AllowMultiple = false
        });

        if (folders.Count > 0 && ViewModel != null)
        {
            ViewModel.OutputDirectory = folders[0].Path.LocalPath;
        }
    }

    public async void OnBrowseLogFileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Select Log File",
            DefaultExtension = "log",
            FileTypeChoices =
            [
                new FilePickerFileType("Log Files") { Patterns = ["*.log"] },
                new FilePickerFileType("Text Files") { Patterns = ["*.txt"] }
            ]
        });

        if (file != null && ViewModel != null)
        {
            ViewModel.LogFilePath = file.Path.LocalPath;
        }
    }
}
