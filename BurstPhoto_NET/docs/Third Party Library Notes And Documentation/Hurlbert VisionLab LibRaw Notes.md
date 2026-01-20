# LibRaw Wrapper Technical Documentation

## Overview

LibRaw Wrapper is a C++/CLI assembly that provides .NET access to the LibRaw library for reading and processing RAW image files from digital cameras. It supports virtually all RAW formats (CRW/CR2, NEF, RAF, DNG, MOS, KDC, DCR, etc.) and exposes both high-level WPF-compatible APIs and low-level processing controls.

**Key Capabilities:**
- Read RAW files with full metadata extraction
- Process images with 32-bit floating point or 16-bit integer precision
- Access to advanced demosaicing algorithms and color processing
- WPF `BitmapSource` integration for UI display
- Fine-grained control over white balance, gamma curves, and color spaces

---

## Installation

### NuGet Package
```powershell
Install-Package HurlbertVisionLab.LibRawWrapper
```

### Platform Requirements
⚠️ **Critical**: This is a mixed-mode (native/managed) assembly compiled for **x64 only**.

**Project Configuration:**
1. Set your application's target platform to **x64**
2. Or if using "Any CPU", uncheck "Prefer 32-bit" in Project Properties → Build

**Supported Frameworks:**
- .NET Framework 4.6+
- .NET 6.0-windows7.0+

---

## API Architecture

LibRaw Wrapper provides two complementary APIs:

### 1. High-Level API (`HurlbertVisionLab.LibRawWrapper`)
- `LibRawBitmapDecoder` - WPF-style decoder similar to `BitmapDecoder`
- Returns `BitmapFrame` objects ready for display or encoding
- Handles common processing automatically

### 2. Low-Level API (`HurlbertVisionLab.LibRawWrapper.Native`)
- `LibRawProcessor` - Direct access to LibRaw functionality
- Full control over processing pipeline
- Access to raw sensor data and metadata

---

## High-Level API Usage

### Basic Image Decoding

```csharp
using System;
using System.IO;
using System.Windows.Media.Imaging;
using HurlbertVisionLab.LibRawWrapper;

// Open a RAW file
var decoder = new LibRawBitmapDecoder(
    new Uri(@"C:\Photos\IMG_0001.CR2"),
    BitmapCreateOptions.None,
    BitmapCacheOption.None
);

// Get the processed image frame
BitmapFrame frame = decoder.Frames[0];

// frame is now ready to display or save
Console.WriteLine($"Format: {frame.PixelFormat}");  // Rgb128Float
Console.WriteLine($"Size: {frame.PixelWidth}x{frame.PixelHeight}");
```

### Loading from Stream

```csharp
using (FileStream stream = File.OpenRead(@"C:\Photos\IMG_0001.CR2"))
{
    var decoder = new LibRawBitmapDecoder(
        stream,
        BitmapCreateOptions.None,
        BitmapCacheOption.None
    );
    
    BitmapFrame frame = decoder.Frames[0];
    // Process frame...
}
```

### Output Format Control

The `BitmapCreateOptions` parameter controls output characteristics:

| Option | Pixel Format | Gamma | Bit Depth | Use Case |
|--------|-------------|-------|-----------|----------|
| `None` | `Rgb128Float` | Linear | 32-bit float | Maximum quality, HDR processing |
| `IgnoreColorProfile` | `Rgb128Float` | Linear | 32-bit float | Custom color management |
| `PreservePixelFormat` | `Rgb48` | sRGB | 16-bit int | Display-ready, smaller memory |
| `IgnoreColorProfile` + `PreservePixelFormat` | `Rgb48` | Linear | 16-bit int | Raw sensor data access |

**Examples:**

```csharp
// Maximum quality for processing pipeline
var decoder = new LibRawBitmapDecoder(uri, 
    BitmapCreateOptions.None, 
    BitmapCacheOption.None);
// → Rgb128Float, linear gamma, equivalent to dcraw -4

// Display-ready output
var decoder = new LibRawBitmapDecoder(uri, 
    BitmapCreateOptions.PreservePixelFormat, 
    BitmapCacheOption.None);
// → Rgb48, sRGB gamma, equivalent to dcraw -6 -W -g 2.4 12.92

// Raw sensor values
var decoder = new LibRawBitmapDecoder(uri, 
    BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
    BitmapCacheOption.None);
// → Rgb48, linear, equivalent to dcraw -D -4
```

### Saving Processed Images

```csharp
using System.Windows.Media.Imaging;

var decoder = new LibRawBitmapDecoder(uri, 
    BitmapCreateOptions.PreservePixelFormat, 
    BitmapCacheOption.None);

BitmapFrame frame = decoder.Frames[0];

// Save as JPEG
var jpegEncoder = new JpegBitmapEncoder { QualityLevel = 95 };
jpegEncoder.Frames.Add(frame);
using (var stream = File.Create(@"C:\Output\output.jpg"))
    jpegEncoder.Save(stream);

// Save as TIFF
var tiffEncoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
tiffEncoder.Frames.Add(frame);
using (var stream = File.Create(@"C:\Output\output.tif"))
    tiffEncoder.Save(stream);
```

### Accessing Metadata

```csharp
var decoder = new LibRawBitmapDecoder(uri, 
    BitmapCreateOptions.None, 
    BitmapCacheOption.None);

// Codec information
Console.WriteLine($"Camera: {decoder.CodecInfo.DeviceManufacturer}");
Console.WriteLine($"Model: {decoder.CodecInfo.DeviceModels}");
Console.WriteLine($"LibRaw Version: {decoder.CodecInfo.Version}");

// Thumbnail
BitmapSource thumbnail = decoder.Thumbnail;
if (thumbnail != null)
{
    Console.WriteLine($"Thumbnail size: {thumbnail.PixelWidth}x{thumbnail.PixelHeight}");
}

// Multiple frames (e.g., Pentax 4-shot)
Console.WriteLine($"Frame count: {decoder.Frames.Count}");
foreach (var frame in decoder.Frames)
{
    // Process each frame...
}
```

---

## Low-Level API Usage

### Basic Processing Pipeline

```csharp
using HurlbertVisionLab.LibRawWrapper.Native;

using (var processor = new LibRawProcessor())
{
    // 1. Open and parse metadata
    processor.Open(@"C:\Photos\IMG_0001.CR2");
    
    // 2. Configure output parameters
    processor.OutputParameters.NoAutoBrightness = true;
    processor.OutputParameters.OutputBitsPerPixel = 16;
    processor.OutputParameters.SetGammaTosRGB();
    
    // 3. Unpack RAW data
    processor.Unpack();
    
    // 4. Process (demosaic, white balance, etc.)
    processor.DcrawProcess();
    
    // 5. Get the processed bitmap
    BitmapSource bitmap = processor.GetProcessedBitmap();
    
    // 6. Clean up for next image
    processor.Recycle();
}
```

### Metadata Access

```csharp
using (var processor = new LibRawProcessor())
{
    processor.Open(@"C:\Photos\IMG_0001.CR2");
    
    // Camera information
    Console.WriteLine($"Make: {processor.ImageParameters.Make}");
    Console.WriteLine($"Model: {processor.ImageParameters.Model}");
    Console.WriteLine($"Software: {processor.ImageParameters.Software}");
    Console.WriteLine($"DNG Version: {processor.ImageParameters.DngVersion}");
    
    // Image dimensions
    Console.WriteLine($"Raw size: {processor.Sizes.RawWidth}x{processor.Sizes.RawHeight}");
    Console.WriteLine($"Visible size: {processor.Sizes.Width}x{processor.Sizes.Height}");
    Console.WriteLine($"Output size: {processor.Sizes.OutputWidth}x{processor.Sizes.OutputHeight}");
    
    // Color information
    Console.WriteLine($"Colors: {processor.ImageParameters.Colors}");
    Console.WriteLine($"Color filter: 0x{processor.ImageParameters.Filters:X8}");
    Console.WriteLine($"Is Foveon: {processor.ImageParameters.IsFoveon}");
    
    // Shooting information
    Console.WriteLine($"ISO: {processor.Other.IsoSpeed}");
    Console.WriteLine($"Shutter: {processor.Other.Shutter}");
    Console.WriteLine($"Aperture: f/{processor.Other.Aperture}");
    Console.WriteLine($"Focal Length: {processor.Other.FocalLength}mm");
    Console.WriteLine($"Date: {processor.Other.Timestamp}");
    
    // Lens information
    Console.WriteLine($"Lens: {processor.Lens.Lens}");
    Console.WriteLine($"Focal Range: {processor.Lens.MinimumFocalLength}-{processor.Lens.MaximumFocalLength}mm");
    
    // GPS data
    var gps = processor.Other.ParsedGps;
    if (gps != null)
    {
        Console.WriteLine($"Location: {gps.LatitudeDegrees}°{gps.LatitudeMinutes}'{gps.LatitudeSeconds}\"{gps.LatitudeRef}");
        Console.WriteLine($"          {gps.LongitudeDegrees}°{gps.LongitudeMinutes}'{gps.LongitudeSeconds}\"{gps.LongitudeRef}");
        Console.WriteLine($"Altitude: {gps.Altitude}m");
    }
}
```

### White Balance Control

```csharp
processor.Open(@"C:\Photos\IMG_0001.CR2");

// Option 1: Use camera white balance
processor.OutputParameters.UseCameraWhiteBalance = true;

// Option 2: Use automatic white balance
processor.OutputParameters.UseAutomaticWhiteBalance = true;

// Option 3: Set custom multipliers
processor.OutputParameters.UserMultipliers = new Pixel4<float>(
    2.0f,  // R
    1.0f,  // G1
    1.0f,  // G2
    1.5f   // B
);

// Option 4: Access camera's stored WB presets
var wbCoefficients = processor.Color.GetWhiteBalanceCoefficients();
foreach (var kvp in wbCoefficients)
{
    ExifLightSource source = kvp.Key;
    Pixel4<int> coeffs = kvp.Value;
    Console.WriteLine($"{source}: R={coeffs.R}, G1={coeffs.G1}, B={coeffs.B}, G2={coeffs.G2}");
}

// Apply daylight preset
var daylightWB = processor.Color.GetWhiteBalanceCoefficient(ExifLightSource.Daylight);
processor.OutputParameters.UserMultipliers = new Pixel4<float>(
    daylightWB.R, daylightWB.G1, daylightWB.B, daylightWB.G2
);
```

### Gamma and Color Space

```csharp
// Linear output (for HDR processing)
processor.OutputParameters.SetGammaToLinear();

// sRGB gamma (display-ready)
processor.OutputParameters.SetGammaTosRGB();

// Rec. 709 gamma
processor.OutputParameters.SetGammaToBT709();

// Custom gamma curve
processor.OutputParameters.SetGamma(
    gamma: 2.2,      // Gamma value
    slope: 4.5       // Linear toe slope (0 for simple curve)
);

// Output color space
processor.OutputParameters.OutputColorspace = OutputColorspace.sRGB;        // Default
processor.OutputParameters.OutputColorspace = OutputColorspace.AdobeRGB;
processor.OutputParameters.OutputColorspace = OutputColorspace.ProPhoto;
processor.OutputParameters.OutputColorspace = OutputColorspace.XYZ;
processor.OutputParameters.OutputColorspace = OutputColorspace.ACES;
processor.OutputParameters.OutputColorspace = OutputColorspace.DciP3;
processor.OutputParameters.OutputColorspace = OutputColorspace.Rec2020;

// Use embedded camera matrix
processor.OutputParameters.UseCameraMatrix = UseCameraMatrix.Always;
```

### Demosaicing Algorithms

```csharp
// Set interpolation quality
processor.OutputParameters.UserQuality = Interpolation.Linear;       // Fast, low quality
processor.OutputParameters.UserQuality = Interpolation.VNG;          // Variable Number of Gradients
processor.OutputParameters.UserQuality = Interpolation.PPG;          // Patterned Pixel Grouping
processor.OutputParameters.UserQuality = Interpolation.AHD;          // Adaptive Homogeneity-Directed
processor.OutputParameters.UserQuality = Interpolation.DCB;          // DCB (Jacek Gozdz)
processor.OutputParameters.UserQuality = Interpolation.DHT;          // DHT (Anton Petrusevich)
processor.OutputParameters.UserQuality = Interpolation.ModifiedAHD;  // Modified AHD

// DCB-specific settings
processor.OutputParameters.DcbIterations = 3;        // Refinement passes (-1 to disable)
processor.OutputParameters.DcbEnhance = true;        // Color enhancement

// Green channel matching (reduces color artifacts)
processor.OutputParameters.GreenMatching = true;
```

### Highlight Recovery

```csharp
// Clip highlights to white (default)
processor.OutputParameters.HighlightMode = HighlightMode.Clip;

// Leave unclipped (shows pink overexposed areas)
processor.OutputParameters.HighlightMode = HighlightMode.Unclip;

// Blend clipped and unclipped
processor.OutputParameters.HighlightMode = HighlightMode.Blend;

// Reconstruct highlights (level 3-9)
processor.OutputParameters.HighlightRebuildFactor = 5;
```

### Noise Reduction

```csharp
// Wavelet denoising threshold
processor.OutputParameters.DenoisingThreshold = 100.0f;  // Higher = more aggressive

// FBDD noise reduction (before demosaic)
processor.OutputParameters.FbddNoiseReduction = 2;  // 0=off, 1=light, 2=full

// Median filter passes
processor.OutputParameters.MedianPasses = 3;
```

### Exposure Adjustment

```csharp
processor.OutputParameters.CorrectExposure = true;

// Shift in linear scale (0.25 = -2 stops, 8.0 = +3 stops)
processor.OutputParameters.ExposureShift = 1.5f;  // +0.5 EV

// Preserve highlights when brightening
processor.OutputParameters.ExposurePreserveHighlights = 0.5f;  // 0.0-1.0

// Manual brightness
processor.OutputParameters.Brightness = 1.2f;

// Disable auto-brightness
processor.OutputParameters.NoAutoBrightness = true;

// Control clipping threshold for auto-brightness
processor.OutputParameters.AutoBrightnessThreshold = 0.001f;  // 0.1% clipped pixels
```

### Working with Raw Sensor Data

```csharp
processor.Open(@"C:\Photos\IMG_0001.CR2");
processor.Unpack();

// Access raw data buffer
IntPtr rawBuffer = processor.RawData.Buffer;
RawDataFormat format = processor.RawData.BufferFormat;

switch (format)
{
    case RawDataFormat.Short1:
        // Single-channel 16-bit Bayer data
        // Access via processor.RawData.Buffer as ushort[]
        break;
        
    case RawDataFormat.Short3:
        // 3-channel 16-bit data
        break;
        
    case RawDataFormat.Short4:
        // 4-channel 16-bit data (RGBG)
        break;
        
    case RawDataFormat.Float1:
    case RawDataFormat.Float3:
    case RawDataFormat.Float4:
        // Floating-point raw data
        break;
}

// Get image dimensions
int width = processor.Sizes.RawWidth;
int height = processor.Sizes.RawHeight;
int stride = (int)processor.Sizes.RawPitch;

// Black level information
int black = processor.Color.Black;
var perChannelBlack = processor.Color.GetPerChannelBlackCorrection();
```

### Chromatic Aberration Correction

```csharp
// Set red/blue channel multipliers
processor.OutputParameters.ChromaticAberration = new Pixel4<double>(
    1.0,    // Red (no change)
    1.001,  // Green 1
    0.999,  // Blue
    1.001   // Green 2
);
```

### Crop and Rotation

```csharp
// Auto-rotation from EXIF
processor.OutputParameters.UserFlip = null;  // Auto (default)

// Manual rotation
processor.OutputParameters.UserFlip = Flip.None;
processor.OutputParameters.UserFlip = Flip.Rotate90Clockwise;
processor.OutputParameters.UserFlip = Flip.Rotate90CounterClockwise;
processor.OutputParameters.UserFlip = Flip.Rotate180;

// Crop rectangle (before rotation)
processor.OutputParameters.CropBox = new Int32Rect(
    x: 100,
    y: 100,
    width: 2000,
    height: 1500
);

// Use raw inset crops from DNG
int usedCrop = processor.AdjustToRawInsetCrop(
    mask: 0b11,      // Prefer both crop levels
    maxcrop: 0.95f   // Limit to 95% of original size
);
```

### Progress Monitoring

```csharp
processor.ProgressChanged += OnProgress;

processor.Open(@"C:\Photos\IMG_0001.CR2");
processor.Unpack();
processor.DcrawProcess();

void OnProgress(object sender, LibRawProgressEventArgs e)
{
    Console.WriteLine($"{e.Description}: {e.Percent:P0} ({e.Iteration + 1}/{e.Expected})");
    
    // Cancel processing
    if (userCancelled)
        e.Cancel = true;
}

// Stages reported:
// - Open, Identify, SizeAdjust
// - LoadRaw, Raw2Image
// - RemoveZeroes, BadPixels, DarkFrame
// - FoveonInterpolate, ScaleColors
// - PreInterpolate, Interpolate, MixGreen
// - MedianFilter, Highlights
// - FujiRotate, Flip, ConvertRgb, Stretch
```

### Thumbnail Extraction

```csharp
processor.Open(@"C:\Photos\IMG_0001.CR2");

// Get default thumbnail as bitmap
BitmapSource thumb = processor.GetThumbnailBitmap();

// Or access raw thumbnail data
processor.UnpackThumbnail();
IntPtr thumbBuffer = processor.Thumbnail.ThumbnailBuffer;
int thumbLength = processor.Thumbnail.ThumbnailBufferLength;
ThumbnailFormat format = processor.Thumbnail.Format;

switch (format)
{
    case ThumbnailFormat.Jpeg:
        // Save directly to file
        File.WriteAllBytes("thumb.jpg", processor.Thumbnail.GetThumbnail());
        break;
        
    case ThumbnailFormat.Bitmap:
        // 8-bit RGB bitmap
        break;
        
    case ThumbnailFormat.Bitmap16:
        // 16-bit RGB bitmap
        break;
}

// Access all available thumbnails
var thumbItems = processor.ThumbnailItems;
for (int i = 0; i < thumbItems.Length; i++)
{
    var item = thumbItems[i];
    Console.WriteLine($"Thumbnail {i}: {item.Width}x{item.Height}");
    
    processor.UnpackThumbnail(i);
    BitmapSource bitmap = processor.GetThumbnailBitmap();
}
```

### Manual Memory Management

```csharp
processor.Open(@"C:\Photos\IMG_0001.CR2");
processor.Unpack();
processor.DcrawProcess();

// Get image format
processor.GetMemoryImageFormat(
    out int width, 
    out int height, 
    out int colors, 
    out int bpp
);

// Allocate buffer
int stride = width * colors * (bpp / 8);
byte[] imageData = new byte[height * stride];

unsafe
{
    fixed (byte* pData = imageData)
    {
        processor.CopyMemoryImage(
            pData, 
            stride, 
            bgr: 0  // 0=RGB, 1=BGR
        );
    }
}

// Process imageData as needed...
```

### Floating Point Data

```csharp
if (processor.IsFloatingPoint)
{
    processor.Unpack();
    
    if (!processor.HasFloatingPointData)
    {
        // Data was auto-converted to integer
        // Unconvert with custom parameters
        processor.ConvertFloatingToInteger(
            dmin: 0.0f,      // Expected minimum
            dmax: 65535.0f,  // Expected maximum
            dtarget: 32767.0f // Target maximum after scaling
        );
    }
    
    // Access float data
    IntPtr floatBuffer = processor.RawData.Buffer;
    // Cast to float* and process...
}
```

### Dark Frame Subtraction

```csharp
// Set dark frame file (16-bit PGM format)
processor.OutputParameters.DarkFrameFilename = @"C:\Calibration\dark_frame.pgm";

processor.Unpack();
processor.DcrawProcess();
```

### Bad Pixel Mapping

```csharp
// dcraw format: "column row unix_timestamp" (one pixel per line)
processor.OutputParameters.BadPixelsFilename = @"C:\Calibration\bad_pixels.txt";
```

### ICC Profile Handling

```csharp
// Use embedded profile
processor.OutputParameters.CameraProfileFilename = "embed";

// Use external input profile
processor.OutputParameters.CameraProfileFilename = @"C:\Profiles\camera.icc";

// Use output profile
processor.OutputParameters.OutputProfileFilename = @"C:\Profiles\output.icc";

// Extract embedded profile
if (processor.Color.HasProfile)
{
    byte[] profileData = processor.Color.GetProfile();
    File.WriteAllBytes("embedded.icc", profileData);
    
    // Or as ColorContext
    var colorContext = processor.Color.GetColorContext();
}
```

### Advanced RAW Processing Options

```csharp
// Processing flags
processor.RawParameters.Options = 
    ProcessingOptions.DngStage2 |           // DNG SDK Stage 2
    ProcessingOptions.DngStage3 |           // DNG SDK Stage 3
    ProcessingOptions.ConvertFloatToInt |   // Force float→int conversion
    ProcessingOptions.DngAddEnhanced;       // Include Enhanced DNG frames

// Special processing modes
processor.RawParameters.Specials = 
    ProcessingSpecials.SrawNoRgb |          // Disable YCC→RGB for Canon sRAW
    ProcessingSpecials.NoDP2QInterpolateRG; // Disable Sigma DP2Q interpolation

// Shot selection (multi-frame files)
processor.RawParameters.ShotSelect = 0;  // First frame

// Memory limit (MB)
processor.RawParameters.MaxRawMemoryMB = 4096;

// Camera-specific
processor.RawParameters.CoolscanNefGamma = 2.2f;  // Coolscan NEF gamma
processor.RawParameters.Pentax4shotOrder = "3102";  // Pentax 4-shot order
processor.RawParameters.SonyArw2PosterizationThreshold = 100;
```

### DNG Processing

```csharp
if (processor.ImageParameters.DngVersion != null)
{
    Console.WriteLine($"DNG Version: {processor.ImageParameters.DngVersion}");
    
    // Enable DNG SDK processing
    processor.RawParameters.DngProcessing = 
        DngProcessing.Float |    // Process floating-point data
        DngProcessing.Linear |   // Process linear data
        DngProcessing.Deflate;   // Process deflate-compressed data
    
    processor.RawParameters.Options |= 
        ProcessingOptions.DngStage2 |       // Apply OpcodeList2
        ProcessingOptions.DngStage3 |       // Apply OpcodeList3
        ProcessingOptions.DngAddMasks;      // Extract transparency masks
}
```

### Multi-Frame RAW Files

```csharp
// Check frame count
int frameCount = processor.ImageParameters.RawCount;

for (int i = 0; i < frameCount; i++)
{
    processor.RawParameters.ShotSelect = i;
    processor.Unpack();
    processor.DcrawProcess();
    
    BitmapSource frame = processor.GetProcessedBitmap();
    // Save or process frame...
    
    processor.Recycle();
}
```

---

## Advanced Scenarios

### Batch Processing with Parallelization

```csharp
using System.Collections.Concurrent;
using System.Threading.Tasks;

string[] rawFiles = Directory.GetFiles(@"C:\Photos", "*.CR2");
var outputQueue = new ConcurrentQueue<string>();

Parallel.ForEach(rawFiles, new ParallelOptions { MaxDegreeOfParallelism = 4 }, file =>
{
    using (var processor = new LibRawProcessor())
    {
        processor.Open(file);
        processor.OutputParameters.OutputBitsPerPixel = 16;
        processor.OutputParameters.SetGammaTosRGB();
        processor.Unpack();
        processor.DcrawProcess();
        
        BitmapSource bitmap = processor.GetProcessedBitmap();
        
        string outputFile = Path.ChangeExtension(file, ".tif");
        var encoder = new TiffBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        
        using (var stream = File.Create(outputFile))
            encoder.Save(stream);
            
        outputQueue.Enqueue(outputFile);
    }
});
```

### HDR Merging from Exposure Brackets

```csharp
string[] bracketedFiles = new[] 
{ 
    "IMG_001.CR2",  // -2 EV
    "IMG_002.CR2",  //  0 EV
    "IMG_003.CR2"   // +2 EV
};

var images = new List<float[]>();
int width = 0, height = 0;

foreach (string file in bracketedFiles)
{
    using (var processor = new LibRawProcessor())
    {
        processor.Open(file);
        processor.OutputParameters.OutputBitsPerPixel = 32;
        processor.OutputParameters.SetGammaToLinear();
        processor.OutputParameters.NoAutoBrightness = true;
        processor.Unpack();
        processor.DcrawProcess();
        
        processor.GetMemoryImageFormat(out width, out height, out int colors, out _);
        
        float[] imageData = new float[width * height * colors];
        unsafe
        {
            fixed (float* pData = imageData)
                processor.CopyMemoryImage(pData, width * colors * sizeof(float), 0);
        }
        
        images.Add(imageData);
    }
}

// Merge using exposure fusion or HDR algorithm...
float[] merged = MergeHDR(images, width, height);
```

### Custom Demosaicing Pipeline

```csharp
processor.Open(@"C:\Photos\IMG_0001.CR2");
processor.Unpack();

// Disable built-in interpolation
processor.OutputParameters.NoInterpolation = true;

// Access raw Bayer data
IntPtr rawBuffer = processor.RawData.Buffer;
int rawWidth = processor.Sizes.RawWidth;
int rawHeight = processor.Sizes.RawHeight;
uint filters = processor.ImageParameters.Filters;

// Implement custom demosaicing...
// unsafe { /* Process raw data */ }

// Then manually call remaining processing steps
processor.OutputParameters.NoAutoScale = false;
processor.DcrawProcess();
```

### XMP Metadata Extraction

```csharp
processor.Open(@"C:\Photos\IMG_0001.CR2");

if (processor.ImageParameters.HasXmpData)
{
    byte[] xmpData = processor.ImageParameters.GetXmpData();
    string xmpXml = System.Text.Encoding.UTF8.GetString(xmpData);
    
    // Parse XMP with XML or XMP library
    var xmpDoc = System.Xml.Linq.XDocument.Parse(xmpXml);
    // Process XMP metadata...
}
```

---

## Error Handling

### Exception Types

```csharp
try
{
    processor.Open(@"C:\Photos\invalid.raw");
}
catch (NotSupportedException ex)
{
    // File format not supported
    Console.WriteLine($"Unsupported format: {ex.Message}");
}
catch (FileNotFoundException ex)
{
    // File not found
}
catch (InvalidDataException ex)
{
    // Corrupted RAW data
}
catch (IOException ex)
{
    // I/O error
}
catch (OutOfMemoryException ex)
{
    // Insufficient memory
}
catch (OperationCanceledException ex)
{
    // Processing cancelled via ProgressChanged event
}
```

### Warnings and Processing Flags

```csharp
processor.Open(@"C:\Photos\IMG_0001.CR2");
processor.Unpack();
processor.DcrawProcess();

// Check for warnings
Warnings warnings = processor.Warnings;
if (warnings.HasFlag(Warnings.BadCameraWhiteBalance))
    Console.WriteLine("Camera white balance unsuitable");
    
if (warnings.HasFlag(Warnings.FallbackToAhd))
    Console.WriteLine("Unsupported interpolation, used AHD instead");

// Check processing stages completed
Progress progress = processor.Progress;
if (progress.HasFlag(Progress.LoadRaw))
    Console.WriteLine("RAW data loaded");
    
if (progress.HasFlag(Progress.ConvertRgb))
    Console.WriteLine("RGB conversion complete");

// Check data error count
int errors = processor.ErrorCount;
if (errors > 0)
    Console.WriteLine($"Encountered {errors} data errors during unpack");
```

---

## Performance Optimization

### Memory Management

```csharp
// Reuse processor for multiple files
using (var processor = new LibRawProcessor())
{
    foreach (string file in rawFiles)
    {
        processor.Open(file);
        processor.Unpack();
        processor.DcrawProcess();
        
        // Process bitmap...
        
        // Free memory for next file
        processor.Recycle();  // Keeps processor object alive
    }
}

// Or recycle just the datastream
processor.RecycleStream();  // Frees file handle but keeps metadata
```

### Fast Preview Generation

```csharp
// Half-size processing (much faster)
processor.OutputParameters.HalfSize = true;
processor.Unpack();
processor.DcrawProcess();

// Or use embedded thumbnail
BitmapSource quickPreview = processor.GetThumbnailBitmap();
```

### Minimal Processing

```csharp
// Skip auto-brightness calculation
processor.OutputParameters.NoAutoBrightness = true;

// Skip auto-scaling
processor.OutputParameters.NoAutoScale = true;

// Use fastest interpolation
processor.OutputParameters.UserQuality = Interpolation.Linear;

// Disable noise reduction
processor.OutputParameters.DenoisingThreshold = 0;
processor.OutputParameters.FbddNoiseReduction = 0;
processor.OutputParameters.MedianPasses = 0;
```

---

## Platform-Specific Considerations

### .NET Framework 4.6+
- Requires `PresentationCore.dll` and `WindowsBase.dll`
- WPF types available by default

### .NET 6+ (Windows)
- Requires `<UseWPF>true</UseWPF>` in .csproj
- NuGet package includes necessary runtime files (`Ijwhost.dll`)

### Memory Limits
- Default max allocation: 2048 MB
- Adjust via `RawParameters.MaxRawMemoryMB`
- Monitor with `processor.ErrorCount` for allocation failures

---

## Supported Camera Formats

LibRaw supports 800+ camera models. Check support:

```csharp
// Get list of all supported cameras
string[] cameras = LibRawProcessor.GetSupportedCameras();
foreach (string camera in cameras)
    Console.WriteLine(camera);

// Get LibRaw version
Version version = LibRawProcessor.Version;
Console.WriteLine($"LibRaw {version}");
```

Common formats include:
- **Canon**: CRW, CR2, CR3
- **Nikon**: NEF, NRW
- **Sony**: ARW, SR2, SRF
- **Fujifilm**: RAF
- **Olympus**: ORF
- **Panasonic**: RW2
- **Pentax**: PEF, DNG
- **Adobe**: DNG
- **Phase One**: IIQ
- **Hasselblad**: 3FR, FFF
- **Leica**: DNG, RWL

---

## Troubleshooting

### "Platform target mismatch" Error
**Cause**: Application compiled for wrong architecture  
**Solution**: Set platform to x64 or uncheck "Prefer 32-bit"

### Out of Memory Exceptions
**Cause**: Large RAW files exceeding memory limits  
**Solutions**:
- Increase `processor.RawParameters.MaxRawMemoryMB`
- Use `processor.OutputParameters.HalfSize = true`
- Process in batches with `Recycle()` between files

### Color Cast or Incorrect White Balance
**Cause**: Auto white balance may not work for all scenes  
**Solutions**:
- Try `processor.OutputParameters.UseCameraWhiteBalance = true`
- Use custom multipliers from `processor.Color.GetWhiteBalanceCoefficients()`
- Set manual `UserMultipliers`

### Posterization in Shadows
**Cause**: Aggressive noise reduction or limited bit depth  
**Solutions**:
- Use 32-bit float output: `OutputBitsPerPixel = 32`
- Reduce `DenoisingThreshold`
- Disable `FbddNoiseReduction`

---

## References

- **LibRaw Documentation**: https://libraw.org/docs
- **dcraw Manual**: https://www.dechifro.org/dcraw/dcraw.1.html
- **Source Code**: https://github.com/hurlbertvisionlab/LibRawWrapper
- **NuGet Package**: https://www.nuget.org/packages/HurlbertVisionLab.LibRawWrapper

---

*This documentation covers LibRaw Wrapper version 1.0.2.3 with LibRaw 202502 snapshot.*