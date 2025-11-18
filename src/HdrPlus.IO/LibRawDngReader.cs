using LibRawSharp;
using MetadataExtractor;
using System.Text;

namespace HdrPlus.IO;

/// <summary>
/// LibRaw-based DNG/RAW reader supporting 700+ camera formats.
/// Extracts full metadata including color matrices, EXIF, and CFA patterns.
/// </summary>
public class LibRawDngReader : IDngReader
{
    public string[] SupportedExtensions => new[]
    {
        // Canon
        ".cr2", ".cr3", ".crw",
        // Nikon
        ".nef", ".nrw",
        // Sony
        ".arw", ".srf", ".sr2",
        // Olympus
        ".orf",
        // Panasonic
        ".rw2", ".raw",
        // Fujifilm
        ".raf",
        // Pentax
        ".pef", ".ptx",
        // Sigma
        ".x3f",
        // Leica
        ".rwl", ".dng",
        // Phase One
        ".iiq",
        // Hasselblad
        ".3fr", ".fff",
        // Adobe/Generic
        ".dng",
        // Other
        ".kdc", ".dcr", ".mrw", ".mos", ".erf", ".mef", ".nef"
    };

    public bool IsSupported(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return SupportedExtensions.Contains(ext);
    }

    public DngImage ReadDng(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"RAW file not found: {filePath}");
        }

        using var rawProcessor = new LibRaw();

        // Open and decode the RAW file
        var openResult = rawProcessor.OpenFile(filePath);
        if (openResult != LibRawError.Success)
        {
            throw new IOException($"LibRaw failed to open file: {openResult}");
        }

        // Unpack the raw data
        var unpackResult = rawProcessor.Unpack();
        if (unpackResult != LibRawError.Success)
        {
            throw new IOException($"LibRaw failed to unpack data: {unpackResult}");
        }

        // Get image dimensions
        var sizes = rawProcessor.Sizes;
        int width = sizes.Width;
        int height = sizes.Height;
        int rawWidth = sizes.RawWidth;
        int rawHeight = sizes.RawHeight;

        // Extract raw pixel data (16-bit)
        var rawData = ExtractRawData(rawProcessor, width, height);

        // Parse mosaic pattern
        var (mosaicPattern, mosaicWidth, cfaPattern, cfaPlaneColor) = ParseCfaPattern(rawProcessor);

        // Extract color data
        var colorData = rawProcessor.ColorData;
        var idata = rawProcessor.ImageData;

        // Black and white levels
        var blackLevels = ExtractBlackLevels(colorData);
        int whiteLevel = (int)colorData.Maximum;

        // Color matrices
        var colorMatrix1 = ExtractColorMatrix(colorData.CameraRgbToXyz, 0);
        var colorMatrix2 = ExtractColorMatrix(colorData.CameraRgbToXyz, 1);

        // White balance multipliers (as-shot neutral)
        var asShotNeutral = ExtractAsShotNeutral(colorData);

        // Extract EXIF metadata using MetadataExtractor
        var (exifData, xmpMetadata) = ExtractExifAndXmp(filePath);

        // Parse camera info
        string? cameraMake = idata.Make;
        string? cameraModel = idata.Model;
        string? normalizedCameraModel = idata.NormalizedMake;

        // Parse exposure data
        double? exposureTime = idata.Shutter > 0 ? idata.Shutter : null;
        double? fNumber = idata.Aperture > 0 ? idata.Aperture : null;
        int? isoSpeed = (int)idata.IsoSpeed > 0 ? (int)idata.IsoSpeed : null;
        double? focalLength = idata.FocalLength > 0 ? idata.FocalLength : null;

        // Calculate ISO × Exposure Time for noise estimation
        double isoExposureTime = 1.0;
        if (exposureTime.HasValue && isoSpeed.HasValue)
        {
            isoExposureTime = isoSpeed.Value * exposureTime.Value;
        }

        // Build DngImage with all metadata
        return new DngImage
        {
            RawData = rawData,
            Width = width,
            Height = height,
            MosaicPatternWidth = mosaicWidth,
            MosaicPattern = mosaicPattern,
            BlackLevels = blackLevels,
            WhiteLevel = whiteLevel,
            ExposureBias = 0, // TODO: Extract from EXIF if available
            IsoExposureTime = isoExposureTime,
            ColorFactors = new double[] { 1.0, 1.0, 1.0 }, // Placeholder
            CameraMake = cameraMake,
            CameraModel = cameraModel,
            FilePath = filePath,

            // Phase 5 enhancements
            ColorMatrix1 = colorMatrix1,
            ColorMatrix2 = colorMatrix2,
            CameraCalibration1 = null, // LibRaw doesn't provide this directly
            CameraCalibration2 = null,
            AsShotNeutral = asShotNeutral,
            AnalogBalance = null, // LibRaw doesn't provide this directly
            CfaPattern = cfaPattern,
            CfaPlaneColor = cfaPlaneColor,
            CfaLayout = 1, // Rectangular (most common)
            BaselineExposure = 0.0,
            BaselineNoise = 1.0,
            BaselineSharpness = 1.0,
            LinearResponseLimit = 1.0,
            ExifData = exifData,
            XmpMetadata = xmpMetadata,
            CameraSerialNumber = null, // TODO: Extract from EXIF
            LensMake = null, // TODO: Extract from EXIF
            LensModel = null, // TODO: Extract from EXIF
            ExposureTime = exposureTime,
            FNumber = fNumber,
            IsoSpeed = isoSpeed,
            FocalLength = focalLength,
            DateTimeOriginal = null, // TODO: Extract from EXIF
            UniqueCameraModel = normalizedCameraModel
        };
    }

    /// <summary>
    /// Extracts raw pixel data from LibRaw processor.
    /// </summary>
    private ushort[] ExtractRawData(LibRaw processor, int width, int height)
    {
        var rawData = new ushort[width * height];
        var rawImage = processor.RawImage;

        if (rawImage != null)
        {
            // Copy raw data (LibRaw provides 16-bit data)
            for (int i = 0; i < Math.Min(rawData.Length, rawImage.Length); i++)
            {
                rawData[i] = rawImage[i];
            }
        }

        return rawData;
    }

    /// <summary>
    /// Parses CFA (Color Filter Array) pattern from LibRaw.
    /// </summary>
    private (string pattern, int width, byte[]? cfaPattern, byte[]? cfaPlaneColor) ParseCfaPattern(LibRaw processor)
    {
        var idata = processor.ImageData;
        var cdesc = idata.ColorDescription;

        // Map LibRaw color codes to pattern string
        // 0=Red, 1=Green, 2=Blue, 3=Green2
        var patternBuilder = new StringBuilder();
        var cfaPatternBytes = new List<byte>();

        // LibRaw uses a 2x2 or larger CFA pattern
        // The cdesc typically has 4 characters for Bayer (RGBG, GRBG, etc.)
        for (int i = 0; i < Math.Min(cdesc.Length, 4); i++)
        {
            char c = cdesc[i];
            switch (c)
            {
                case 'R':
                    patternBuilder.Append('R');
                    cfaPatternBytes.Add(0); // Red
                    break;
                case 'G':
                    patternBuilder.Append('G');
                    cfaPatternBytes.Add(1); // Green
                    break;
                case 'B':
                    patternBuilder.Append('B');
                    cfaPatternBytes.Add(2); // Blue
                    break;
                default:
                    patternBuilder.Append(c);
                    cfaPatternBytes.Add(1); // Default to green
                    break;
            }
        }

        string pattern = patternBuilder.ToString();
        if (string.IsNullOrEmpty(pattern))
        {
            pattern = "RGGB"; // Default Bayer pattern
            cfaPatternBytes = new List<byte> { 0, 1, 1, 2 };
        }

        // Most common is 2x2 Bayer pattern
        int patternWidth = pattern.Length == 4 ? 2 : (int)Math.Sqrt(pattern.Length);

        // CFA plane color: defines color channels
        byte[] cfaPlaneColor = new byte[] { 0, 1, 2 }; // R, G, B

        return (pattern, patternWidth, cfaPatternBytes.ToArray(), cfaPlaneColor);
    }

    /// <summary>
    /// Extracts black levels from LibRaw color data.
    /// </summary>
    private int[] ExtractBlackLevels(LibRawColorData colorData)
    {
        // LibRaw provides black level per channel
        var blackLevels = new int[4];
        for (int i = 0; i < 4; i++)
        {
            blackLevels[i] = (int)colorData.Black;
        }
        return blackLevels;
    }

    /// <summary>
    /// Extracts color matrix from LibRaw (3x3 matrix).
    /// </summary>
    private double[]? ExtractColorMatrix(float[,] matrix, int matrixIndex)
    {
        if (matrix == null || matrix.GetLength(0) < 3 || matrix.GetLength(1) < 3)
        {
            return null;
        }

        var result = new double[9];
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                result[row * 3 + col] = matrix[row, col];
            }
        }

        // Check if matrix is all zeros (not provided)
        bool allZero = result.All(v => Math.Abs(v) < 0.0001);
        return allZero ? null : result;
    }

    /// <summary>
    /// Extracts as-shot neutral (white balance) from LibRaw.
    /// </summary>
    private double[]? ExtractAsShotNeutral(LibRawColorData colorData)
    {
        // LibRaw provides camera multipliers [R, G, B, G2]
        var multipliers = colorData.CameraMultipliers;
        if (multipliers == null || multipliers.Length < 3)
        {
            return null;
        }

        // Normalize so that green = 1.0
        double greenMult = multipliers[1];
        if (greenMult <= 0)
        {
            return null;
        }

        return new double[]
        {
            multipliers[0] / greenMult, // R
            1.0,                        // G
            multipliers[2] / greenMult  // B
        };
    }

    /// <summary>
    /// Extracts EXIF and XMP metadata using MetadataExtractor library.
    /// </summary>
    private (Dictionary<string, string>? exif, string? xmp) ExtractExifAndXmp(string filePath)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);
            var exifDict = new Dictionary<string, string>();
            string? xmpData = null;

            foreach (var directory in directories)
            {
                // Extract EXIF tags
                foreach (var tag in directory.Tags)
                {
                    string key = $"{directory.Name}/{tag.Name}";
                    string value = tag.Description ?? "";
                    exifDict[key] = value;
                }

                // Extract XMP metadata
                if (directory.Name == "XMP")
                {
                    xmpData = directory.Tags
                        .Select(t => t.Description)
                        .Where(d => !string.IsNullOrEmpty(d))
                        .FirstOrDefault();
                }
            }

            return (exifDict.Count > 0 ? exifDict : null, xmpData);
        }
        catch (Exception)
        {
            // If metadata extraction fails, return null
            return (null, null);
        }
    }
}
