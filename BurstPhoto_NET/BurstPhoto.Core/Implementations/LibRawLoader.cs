using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Models;
using HurlbertVisionLab.LibRawWrapper.Native;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace BurstPhoto.Core.Implementations;

public class LibRawLoader : IRawImageLoader
{
    public unsafe RawImage Load(string path)
    {
        // Use HurlbertVisionLab.LibRawWrapper for raw Bayer data access
        // Note: Manually dispose due to GCHandle bug in the library's Dispose method
        var iProcessor = new LibRawProcessor();
        
        try
        {
            // Open and unpack the raw file
            iProcessor.Open(path);
            iProcessor.Unpack();
            
            // Get raw image dimensions
            var sizes = iProcessor.Sizes;
            var rawWidth = sizes.RawWidth;
            var rawHeight = sizes.RawHeight;
            
            // Get image parameters (includes CFA pattern and camera info)
            var imageParams = iProcessor.ImageParameters;
            var cameraMake = imageParams.Make ?? "";
            var cameraModel = imageParams.Model ?? "";
            
            // Decode CFA pattern from LibRaw's Filters bitmask
            var cfaPattern = DecodeCfaPattern(imageParams.Filters);
            
            // Detect X-Trans sensors using the camera info we already have
            var isXTrans = IsXTransSensor(cameraMake, cameraModel);
            
            // Get color info from LibRaw
            var color = iProcessor.Color;
            var whiteLevel = color.Maximum;
            
            // Get per-channel black level from LibRaw
            // The cblack array in LibRaw contains per-channel black corrections: cblack[0..3] = R, G1, B, G2
            int[] blackLevel;
            try
            {
                var perChannelBlack = color.GetPerChannelBlackCorrection();
                // PerChannelBlackCorrection provides array-style access to cblack[0..3]
                // Try to access as indexable (int this[int index] or similar)
                if (perChannelBlack is System.Collections.IList { Count: >= 4 } list)
                {
                    blackLevel =
                    [
                        Convert.ToInt32(list[0]), 
                        Convert.ToInt32(list[1]), 
                        Convert.ToInt32(list[2]), 
                        Convert.ToInt32(list[3])
                    ];
                }
                else
                {
                    // Use reflection to find properties or indexer
                    var type = perChannelBlack.GetType();
                    var indexer = type.GetProperty("Item");
                    if (indexer != null)
                    {
                        blackLevel = new int[4];
                        for (var i = 0; i < 4; i++)
                        {
                            var val = indexer.GetValue(perChannelBlack, [i]);
                            blackLevel[i] = Convert.ToInt32(val);
                        }
                    }
                    else
                    {
                        // Last resort: try to convert to string and parse, or use base Black
                        blackLevel = [color.Black, color.Black, color.Black, color.Black];
                    }
                }
            }
            catch
            {
                // Fallback to single value if per-channel not available
                blackLevel = [color.Black, color.Black, color.Black, color.Black];
            }
            
            // Get camera multipliers for white balance
            var colorFactors = new float[4];
            var camMult = color.CameraWhiteBalance;
            for (var i = 0; i < Math.Min(4, camMult?.Length ?? 0); i++)
            {
                colorFactors[i] = camMult![i];
            }
            
            // Get ISO and shutter for exposure calculation
            var other = iProcessor.Other;
            var isoExposureTime = other.IsoSpeed * (float)other.Shutter.TotalSeconds;
            
            // Extract DNG-specific metadata that LibRaw doesn't expose
            // (ColorMatrix1/2, CalibrationIlluminant1/2, AsShotNeutral, ExposureBias)
            var dngMeta = ExtractDngMetadata(path);
            
            // Access raw Bayer data directly (single-channel CFA)
            var rawData = iProcessor.RawData;
            var bufferPtr = rawData.Buffer;
            
            // Validate buffer
            if (bufferPtr == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Failed to access raw Bayer data from: {path}");
            }
            
            // Raw Bayer data is single-channel 16-bit
            var pixelCount = rawWidth * rawHeight;
            
            // Create the RawImage with single-channel Bayer data
            var rawImage = new RawImage
            {
                SourcePath = path,
                Width = rawWidth,  // Use raw dimensions for Bayer data
                Height = rawHeight,
                WhiteLevel = whiteLevel,
                // Prefer BlackLevels from DNG metadata (more reliable for some cameras like DJI)
                // Fall back to LibRaw's value if DNG tag not available or empty
                BlackLevels = dngMeta.BlackLevels.Length >= 4 
                    ? dngMeta.BlackLevels 
                    : (dngMeta.BlackLevels.Length > 0 
                        ? Enumerable.Repeat(dngMeta.BlackLevels[0], 4).ToArray()
                        : blackLevel),
                ExposureBias = dngMeta.ExposureBias,
                IsoSpeedExposureTimeProduct = isoExposureTime,
                ColorChannelMultipliers = colorFactors,
                MosaicPatternWidth = isXTrans ? 6 : 2,
                
                // CFA pattern from LibRaw
                CfaPattern = cfaPattern,
                
                // DNG-specific metadata from MetadataExtractor (not exposed by LibRaw)
                ColorMatrix1 = dngMeta.ColorMatrix1,
                ColorMatrix2 = dngMeta.ColorMatrix2,
                CalibrationIlluminant1 = dngMeta.CalibrationIlluminant1,
                CalibrationIlluminant2 = dngMeta.CalibrationIlluminant2,
                AsShotNeutral = dngMeta.AsShotNeutral,
                
                // Camera info from LibRaw
                CameraMake = cameraMake,
                CameraModel = cameraModel,
                
                // Flag to indicate this is raw Bayer, not demosaiced RGB
                IsBayerData = true,
                // Copy raw Bayer data (single-channel 16-bit)
                Data = new ushort[pixelCount]
            };

            var srcPixels = (ushort*)bufferPtr;
            fixed (ushort* dst = rawImage.Data)
            {
                Buffer.MemoryCopy(srcPixels, dst, pixelCount * sizeof(ushort), pixelCount * sizeof(ushort));
            }

            return rawImage;
        }
        finally
        {
            // Workaround for HurlbertVisionLab.LibRawWrapper's GCHandle bug in Dispose
            try
            {
                iProcessor.Recycle(); // Free internal resources first
            }
            catch { /* Ignore disposal errors */ }
            
            try
            {
                iProcessor.Dispose();
            }
            catch (InvalidOperationException)
            {
                // Known issue: "Handle is not initialized" - safe to ignore
            }
        }
    }

    /// <summary>
    /// Decodes LibRaw's Filters bitmask into a 4-element CFA pattern array.
    /// The bitmask encodes a 2x2 Bayer pattern where each pixel's color is stored in 2 bits.
    /// </summary>
    /// <param name="filters">The LibRaw Filters bitmask from ImageParameters</param>
    /// <returns>4-element array representing [top-left, top-right, bottom-left, bottom-right] colors (0=R, 1=G, 2=B)</returns>
    private static int[] DecodeCfaPattern(uint filters)
    {
        // Common Bayer pattern bitmasks:
        // RGGB: 0x94949494 -> [0,1,1,2]
        // GRBG: 0x61616161 -> [1,0,2,1]
        // GBRG: 0x49494949 -> [1,2,0,1]
        // BGGR: 0x16161616 -> [2,1,1,0]
        
        // Quick lookup for common patterns
        return filters switch
        {
            0x94949494 => [0, 1, 1, 2], // RGGB
            0x61616161 => [1, 0, 2, 1], // GRBG
            0x49494949 => [1, 2, 0, 1], // GBRG
            0x16161616 => [2, 1, 1, 0], // BGGR
            _ => DecodeGenericCfaPattern(filters)
        };
    }

    /// <summary>
    /// Generic decoder for non-standard CFA patterns.
    /// Extracts the 2x2 pattern from the 32-bit bitmask.
    /// </summary>
    private static int[] DecodeGenericCfaPattern(uint filters)
    {
        // Each 2-bit field represents a color: 0=R, 1=G first row, 2=B, 3=G second row
        // The pattern repeats every 16 bits for a 2x8 arrangement
        // For Bayer sensors, we extract the 2x2 pattern from the first 4 pixels
        
        // Extract colors for positions (0,0), (0,1), (1,0), (1,1)
        // Using the COLOR macro approach: (filters >> ((((row) << 1 & 14) + ((col) & 1)) << 1)) & 3
        
        var pattern = new int[4];
        
        // Position (0,0): row=0, col=0
        pattern[0] = (int)((filters >> ((((0) << 1 & 14) + ((0) & 1)) << 1)) & 3);
        // Position (0,1): row=0, col=1
        pattern[1] = (int)((filters >> ((((0) << 1 & 14) + ((1) & 1)) << 1)) & 3);
        // Position (1,0): row=1, col=0
        pattern[2] = (int)((filters >> ((((1) << 1 & 14) + ((0) & 1)) << 1)) & 3);
        // Position (1,1): row=1, col=1
        pattern[3] = (int)((filters >> ((((1) << 1 & 14) + ((1) & 1)) << 1)) & 3);
        
        // LibRaw uses: 0=R, 1=G, 2=B, 3=G (for the second green)
        // Normalize G values (3 -> 1)
        for (var i = 0; i < 4; i++)
        {
            if (pattern[i] == 3) pattern[i] = 1;
        }
        
        return pattern;
    }

    /// <summary>
    /// Extracts DNG-specific metadata that LibRaw doesn't expose directly.
    /// This includes ColorMatrix1/2, CalibrationIlluminant1/2, AsShotNeutral, and ExposureBias.
    /// </summary>
    private static DngMetadataResult ExtractDngMetadata(string path)
    {
        var result = new DngMetadataResult();

        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);

            // Extract ExposureBias from EXIF SubIFD
            try
            {
                var exifSubIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                if (exifSubIfd != null && exifSubIfd.ContainsTag(ExifDirectoryBase.TagExposureBias))
                {
                    var exposureBiasRational = exifSubIfd.GetRational(ExifDirectoryBase.TagExposureBias);
                    if (exposureBiasRational.Denominator != 0)
                    {
                        var ev = (double)exposureBiasRational.Numerator / exposureBiasRational.Denominator;
                        result.ExposureBias = (int)Math.Round(ev * 100);
                    }
                }
            }
            catch { /* Ignore extraction errors */ }

            // Extract DNG-specific tags from all directories
            foreach (var directory in directories)
            {
                try
                {
                    // DNG Tag 50721 = ColorMatrix1 (SRATIONAL array)
                    if (result.ColorMatrix1.Length == 0)
                    {
                        var cm1Obj = directory.GetObject(50721);
                        if (cm1Obj != null)
                        {
                            result.ColorMatrix1 = ExtractRationalArray(cm1Obj);
                        }
                    }

                    // DNG Tag 50722 = ColorMatrix2 (SRATIONAL array)
                    if (result.ColorMatrix2.Length == 0)
                    {
                        var cm2Obj = directory.GetObject(50722);
                        if (cm2Obj != null)
                        {
                            result.ColorMatrix2 = ExtractRationalArray(cm2Obj);
                        }
                    }

                    // DNG Tag 50778 = CalibrationIlluminant1
                    if (result.CalibrationIlluminant1 == 0 && directory.TryGetInt32(50778, out var ci1))
                    {
                        result.CalibrationIlluminant1 = ci1;
                    }

                    // DNG Tag 50779 = CalibrationIlluminant2
                    if (result.CalibrationIlluminant2 == 0 && directory.TryGetInt32(50779, out var ci2))
                    {
                        result.CalibrationIlluminant2 = ci2;
                    }

                    // DNG Tag 50728 = AsShotNeutral (RATIONAL array)
                    if (result.AsShotNeutral.Length == 0)
                    {
                        var asnObj = directory.GetObject(50728);
                        if (asnObj != null)
                        {
                            result.AsShotNeutral = ExtractRationalArray(asnObj);
                        }
                    }

                    // DNG Tag 50714 = BlackLevels (RATIONAL or LONG array)
                    if (result.BlackLevels.Length == 0)
                    {
                        var blObj = directory.GetObject(50714);
                        if (blObj != null)
                        {
                            result.BlackLevels = ExtractBlackLevelsArray(blObj);
                        }
                    }
                }
                catch
                {
                    // If extraction from this directory fails, continue to the next one
                }
            }
        }
        catch
        {
            // If metadata extraction fails, return defaults
        }

        return result;
    }

    /// <summary>
    /// Extracts a double array from a RATIONAL or SRATIONAL tag object.
    /// </summary>
    private static double[] ExtractRationalArray(object? obj)
    {
        switch (obj)
        {
            case null:
                return [];
            case Rational[] rationals:
                return rationals.Select(r => r.Denominator != 0 ? (double)r.Numerator / r.Denominator : 0).ToArray();
            case object[] objArray:
            {
                var resultList = new List<double>();
                foreach (var item in objArray)
                {
                    if (item is Rational r && r.Denominator != 0)
                    {
                        resultList.Add((double)r.Numerator / r.Denominator);
                    }
                }
                return resultList.ToArray();
            }
            default:
                return [];
        }
    }

    /// <summary>
    /// Extracts BlackLevels array from DNG tag 50714 (can be RATIONAL, LONG, or SHORT).
    /// </summary>
    private static int[] ExtractBlackLevelsArray(object? obj)
    {
        if (obj == null) return [];

        // Handle RATIONAL array (most common for DNG BlackLevels)
        if (obj is Rational[] rationals)
        {
            return rationals.Select(r => r.Denominator != 0 ? (int)(r.Numerator / r.Denominator) : 0).ToArray();
        }
        
        // Handle integer arrays (LONG or SHORT)
        if (obj is int[] intArray)
        {
            return intArray;
        }
        
        if (obj is ushort[] ushortArray)
        {
            return ushortArray.Select(u => (int)u).ToArray();
        }
        
        if (obj is uint[] uintArray)
        {
            return uintArray.Select(u => (int)u).ToArray();
        }

        // Handle object array of mixed types
        if (obj is object[] objArray)
        {
            var resultList = new List<int>();
            foreach (var item in objArray)
            {
                if (item is Rational r && r.Denominator != 0)
                {
                    resultList.Add((int)(r.Numerator / r.Denominator));
                }
                else if (item is int i)
                {
                    resultList.Add(i);
                }
                else if (item is short s)
                {
                    resultList.Add(s);
                }
                else if (item is ushort us)
                {
                    resultList.Add(us);
                }
            }
            return resultList.ToArray();
        }

        return [];
    }

    /// <summary>
    /// Result container for DNG-specific metadata not exposed by LibRaw.
    /// </summary>
    private class DngMetadataResult
    {
        public int ExposureBias { get; set; }
        public double[] ColorMatrix1 { get; set; } = [];
        public double[] ColorMatrix2 { get; set; } = [];
        public int CalibrationIlluminant1 { get; set; }
        public int CalibrationIlluminant2 { get; set; }
        public double[] AsShotNeutral { get; set; } = [];
        public int[] BlackLevels { get; set; } = [];
    }

    /// <summary>
    /// Detects if the camera uses an X-Trans sensor (Fujifilm 6x6 mosaic pattern).
    /// </summary>
    /// <param name="make">Camera make from LibRaw</param>
    /// <param name="model">Camera model from LibRaw</param>
    private static bool IsXTransSensor(string make, string model)
    {
        var makeUpper = make?.ToUpperInvariant() ?? "";
        var modelUpper = model?.ToUpperInvariant() ?? "";
        
        // Fujifilm uses X-Trans in most of their X-series cameras
        if (makeUpper.Contains("FUJI"))
        {
            // X-Trans models typically have "X-" prefix or are known models
            if (modelUpper.Contains("X-T") || modelUpper.Contains("X-E") || modelUpper.Contains("X-PRO") ||
                modelUpper.Contains("X-H") || modelUpper.Contains("X100") || modelUpper.Contains("X-S"))
            {
                return true;
            }
        }
        
        return false;
    }
}
