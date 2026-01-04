using System;
using System.IO;
using System.Linq;
using BurstPhoto.Core.Implementations;

class Program {
    static void Main() {
        // Find test DNG
        string basePath = AppContext.BaseDirectory;
        DirectoryInfo dir = new DirectoryInfo(basePath);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Burst Samples")))
            dir = dir.Parent;
            
        if (dir == null) {
            Console.WriteLine("Could not find Burst Samples folder");
            return;
        }
        
        string testDng = Path.Combine(dir.FullName, "Burst Samples", "Bracketed Exposure", "Input", "DJI_20250925172104_0018_D.DNG");
        
        Console.WriteLine($"=== Testing LibRawLoader CFA extraction ===\n");
        Console.WriteLine($"DNG Path: {testDng}");
        
        if (!File.Exists(testDng)) {
            Console.WriteLine($"File not found!");
            return;
        }
        
        try {
            var loader = new LibRawLoader();
            var image = loader.Load(testDng);
            
            Console.WriteLine($"\n=== Loaded RawImage ===");
            Console.WriteLine($"Dimensions: {image.Width} x {image.Height}");
            Console.WriteLine($"IsBayerData: {image.IsBayerData}");
            Console.WriteLine($"WhiteLevel: {image.WhiteLevel}");
            Console.WriteLine($"BlackLevel: [{string.Join(", ", image.BlackLevel)}]");
            Console.WriteLine($"CfaPattern: [{string.Join(", ", image.CfaPattern)}]");
            Console.WriteLine($"ColorMatrix1 length: {image.ColorMatrix1.Length}");
            Console.WriteLine($"AsShotNeutral length: {image.AsShotNeutral.Length}");
            Console.WriteLine($"CameraMake: \"{image.CameraMake}\"");
            Console.WriteLine($"CameraModel: \"{image.CameraModel}\"");
            
            if (image.CfaPattern.Length >= 4) {
                string patternName = image.CfaPattern.SequenceEqual(new[] {0,1,1,2}) ? "RGGB" :
                                     image.CfaPattern.SequenceEqual(new[] {2,1,1,0}) ? "BGGR" :
                                     image.CfaPattern.SequenceEqual(new[] {1,0,2,1}) ? "GRBG" :
                                     image.CfaPattern.SequenceEqual(new[] {1,2,0,1}) ? "GBRG" : "Unknown";
                Console.WriteLine($"CfaPattern Name: {patternName}");
            }
            
            Console.WriteLine($"\nExpected: [2, 1, 1, 0] (BGGR)");
            Console.WriteLine($"Actual:   [{string.Join(", ", image.CfaPattern)}]");
            Console.WriteLine($"Match: {(image.CfaPattern.SequenceEqual(new[] {2,1,1,0}) ? "YES" : "NO")}");
        }
        catch (Exception ex) {
            Console.WriteLine($"Error: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
