using System;
using System.IO;
using System.Linq;

namespace BurstPhoto.Tests;

/// <summary>
/// Static paths to test resources for reference comparison tests.
/// </summary>
public static class TestDataPaths
{
    /// <summary>
    /// Gets the root folder for burst samples by searching up from the test assembly location.
    /// </summary>
    public static string BurstSamplesRoot => FindBurstSamplesFolder();
    
    /// <summary>
    /// Gets the path to exiftool executable.
    /// </summary>
    public static string ExiftoolPath => FindExiftool();

    /// <summary>
    /// Test data paths for bracketed exposure test case.
    /// </summary>
    public static class BracketedExposure
    {
        public static string InputFolder => Path.Combine(BurstSamplesRoot, "Bracketed Exposure", "Input");
        public static string OutputFolder => Path.Combine(BurstSamplesRoot, "Bracketed Exposure", "Output");
        public static string SettingsFile => Path.Combine(BurstSamplesRoot, "Bracketed Exposure", "Settings Used.txt");
        public static string ReferenceOutput => Path.Combine(OutputFolder, "DJI_20250925172105_0023_D_merged_f5_l0.dng");
        
        public static string[] InputFiles => Directory.Exists(InputFolder) 
            ? Directory.GetFiles(InputFolder, "*.DNG").OrderBy(f => f).ToArray() 
            : Array.Empty<string>();
    }

    /// <summary>
    /// Test data paths for static exposure test case.
    /// </summary>
    public static class StaticExposure
    {
        public static string Folder => Path.Combine(BurstSamplesRoot, "Static Exposure");
        
        public static string[] InputFiles => Directory.Exists(Folder)
            ? Directory.GetFiles(Folder, "*.DNG").OrderBy(f => f).ToArray()
            : Array.Empty<string>();
    }

    private static string FindBurstSamplesFolder()
    {
        // Start from test assembly location and search upward
        var testDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(testDir);
        
        // Walk up looking for "Burst Samples" folder
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Burst Samples");
            if (Directory.Exists(candidate))
                return candidate;
            
            dir = dir.Parent;
        }
        
        // Fallback: assume we're running from BurstPhoto_NET folder
        var fallback = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", "..", "Burst Samples"));
        if (Directory.Exists(fallback))
            return fallback;
        
        throw new DirectoryNotFoundException("Could not locate 'Burst Samples' folder. Ensure test data is present.");
    }

    private static string FindExiftool()
    {
        // Start from test assembly location and search upward
        var testDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(testDir);
        
        while (dir != null)
        {
            // Look for exiftool folder
            var candidate = Path.Combine(dir.FullName, "exiftool-13-45_x64", "exiftool.exe");
            if (File.Exists(candidate))
                return candidate;
            
            dir = dir.Parent;
        }
        
        // Fallback path
        var fallback = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", "..", "exiftool-13-45_x64", "exiftool.exe"));
        if (File.Exists(fallback))
            return fallback;
        
        throw new FileNotFoundException("Could not locate exiftool.exe. Ensure exiftool is installed.");
    }
}
