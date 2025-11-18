using TiffLibrary;
using TiffLibrary.ImageEncoder;

namespace HdrPlus.IO;

/// <summary>
/// Writes DNG files using TiffLibrary with full metadata support.
/// </summary>
public class DngWriter
{
    /// <summary>
    /// Compression type for DNG output.
    /// </summary>
    public enum CompressionType
    {
        None = 1,
        LZW = 5,
        Deflate = 8,
        PackBits = 32773
    }

    /// <summary>
    /// Bits per sample (8 or 16 bit output).
    /// </summary>
    public int BitsPerSample { get; set; } = 16;

    /// <summary>
    /// Compression method to use.
    /// </summary>
    public CompressionType Compression { get; set; } = CompressionType.None;

    /// <summary>
    /// Writes a DNG image to disk.
    /// </summary>
    public void WriteDng(DngImage image, string outputPath)
    {
        if (image == null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        // Create TIFF file
        using var fileStream = File.Create(outputPath);
        using var tiffWriter = TiffFileWriter.Open(fileStream, leaveOpen: false);

        // Build TIFF structure for DNG
        var builder = new TiffImageFileDirectoryWriter();

        // Required TIFF tags
        builder.AddImageWidth(image.Width);
        builder.AddImageLength(image.Height);
        builder.AddBitsPerSample(new ushort[] { (ushort)BitsPerSample });
        builder.AddCompression((TiffCompression)Compression);
        builder.AddPhotometricInterpretation(TiffPhotometricInterpretation.ColorFilterArray);
        builder.AddSamplesPerPixel(1);
        builder.AddRowsPerStrip(image.Height);

        // DNG-specific tags
        builder.AddDngVersion(new byte[] { 1, 4, 0, 0 }); // DNG 1.4
        builder.AddDngBackwardVersion(new byte[] { 1, 1, 0, 0 });

        // Camera info
        if (!string.IsNullOrEmpty(image.CameraMake))
        {
            builder.AddMake(image.CameraMake);
        }
        if (!string.IsNullOrEmpty(image.CameraModel))
        {
            builder.AddModel(image.CameraModel);
        }
        if (!string.IsNullOrEmpty(image.UniqueCameraModel))
        {
            builder.AddUniqueCameraModel(image.UniqueCameraModel);
        }
        if (!string.IsNullOrEmpty(image.CameraSerialNumber))
        {
            builder.AddCameraSerialNumber(image.CameraSerialNumber);
        }

        // Lens info
        if (!string.IsNullOrEmpty(image.LensMake))
        {
            builder.AddLensMake(image.LensMake);
        }
        if (!string.IsNullOrEmpty(image.LensModel))
        {
            builder.AddLensModel(image.LensModel);
        }

        // ==================== PHASE 5: CFA PATTERN ====================

        // CFA (Color Filter Array) pattern
        builder.AddCfaRepeatPatternDim(new ushort[] { (ushort)image.MosaicPatternWidth, (ushort)image.MosaicPatternWidth });

        if (image.CfaPattern != null && image.CfaPattern.Length > 0)
        {
            builder.AddCfaPattern(image.CfaPattern);
        }

        if (image.CfaPlaneColor != null && image.CfaPlaneColor.Length > 0)
        {
            builder.AddCfaPlaneColor(image.CfaPlaneColor);
        }

        builder.AddCfaLayout(image.CfaLayout);

        // ==================== PHASE 5: BLACK/WHITE LEVELS ====================

        // Black and white levels
        builder.AddBlackLevel(image.BlackLevels.Select(b => (TiffRational)b).ToArray());
        builder.AddWhiteLevel(new uint[] { (uint)image.WhiteLevel });

        // ==================== PHASE 5: COLOR CALIBRATION ====================

        // Color matrices
        if (image.ColorMatrix1 != null && image.ColorMatrix1.Length == 9)
        {
            builder.AddColorMatrix1(image.ColorMatrix1.Select(v => new TiffRational(v)).ToArray());
        }

        if (image.ColorMatrix2 != null && image.ColorMatrix2.Length == 9)
        {
            builder.AddColorMatrix2(image.ColorMatrix2.Select(v => new TiffRational(v)).ToArray());
        }

        if (image.CameraCalibration1 != null && image.CameraCalibration1.Length == 9)
        {
            builder.AddCameraCalibration1(image.CameraCalibration1.Select(v => new TiffRational(v)).ToArray());
        }

        if (image.CameraCalibration2 != null && image.CameraCalibration2.Length == 9)
        {
            builder.AddCameraCalibration2(image.CameraCalibration2.Select(v => new TiffRational(v)).ToArray());
        }

        // White balance
        if (image.AsShotNeutral != null && image.AsShotNeutral.Length >= 3)
        {
            builder.AddAsShotNeutral(image.AsShotNeutral.Select(v => new TiffRational(v)).ToArray());
        }

        if (image.AnalogBalance != null && image.AnalogBalance.Length >= 3)
        {
            builder.AddAnalogBalance(image.AnalogBalance.Select(v => new TiffRational(v)).ToArray());
        }

        // ==================== PHASE 5: BASELINE VALUES ====================

        builder.AddBaselineExposure(new TiffRational(image.BaselineExposure));
        builder.AddBaselineNoise(new TiffRational(image.BaselineNoise));
        builder.AddBaselineSharpness(new TiffRational(image.BaselineSharpness));
        builder.AddLinearResponseLimit(new TiffRational(image.LinearResponseLimit));

        // ==================== PHASE 5: EXIF METADATA ====================

        // Standard EXIF tags
        if (image.ExposureTime.HasValue)
        {
            builder.AddExposureTime(new TiffRational(image.ExposureTime.Value));
        }

        if (image.FNumber.HasValue)
        {
            builder.AddFNumber(new TiffRational(image.FNumber.Value));
        }

        if (image.IsoSpeed.HasValue)
        {
            builder.AddIsoSpeedRatings(new ushort[] { (ushort)image.IsoSpeed.Value });
        }

        if (image.FocalLength.HasValue)
        {
            builder.AddFocalLength(new TiffRational(image.FocalLength.Value));
        }

        if (image.DateTimeOriginal.HasValue)
        {
            builder.AddDateTimeOriginal(image.DateTimeOriginal.Value.ToString("yyyy:MM:dd HH:mm:ss"));
        }

        // ==================== PHASE 5: XMP METADATA ====================

        if (!string.IsNullOrEmpty(image.XmpMetadata))
        {
            builder.AddXmpMetadata(image.XmpMetadata);
        }

        // Write pixel data
        var pixelData = new byte[image.RawData.Length * 2];
        Buffer.BlockCopy(image.RawData, 0, pixelData, 0, pixelData.Length);

        builder.AddStripOffsets(new ulong[] { 0 }); // Will be updated by TiffWriter
        builder.AddStripByteCounts(new ulong[] { (ulong)pixelData.Length });

        // Write IFD
        using var ifdWriter = tiffWriter.CreateFirstImageFileDirectory();
        builder.Write(ifdWriter);

        // Write pixel data
        ifdWriter.WriteAlignedByteData(pixelData);

        ifdWriter.Flush();
        tiffWriter.Flush();
    }
}

// Extension methods to add DNG-specific TIFF tags
internal static class TiffBuilderExtensions
{
    public static void AddImageWidth(this TiffImageFileDirectoryWriter builder, int width)
    {
        builder.WriteTag(TiffTag.ImageWidth, TiffValueCollection.Single((uint)width));
    }

    public static void AddImageLength(this TiffImageFileDirectoryWriter builder, int height)
    {
        builder.WriteTag(TiffTag.ImageLength, TiffValueCollection.Single((uint)height));
    }

    public static void AddBitsPerSample(this TiffImageFileDirectoryWriter builder, ushort[] bits)
    {
        builder.WriteTag(TiffTag.BitsPerSample, TiffValueCollection.UnsafeWrap(bits));
    }

    public static void AddCompression(this TiffImageFileDirectoryWriter builder, TiffCompression compression)
    {
        builder.WriteTag(TiffTag.Compression, TiffValueCollection.Single((ushort)compression));
    }

    public static void AddPhotometricInterpretation(this TiffImageFileDirectoryWriter builder, TiffPhotometricInterpretation interp)
    {
        builder.WriteTag(TiffTag.PhotometricInterpretation, TiffValueCollection.Single((ushort)interp));
    }

    public static void AddSamplesPerPixel(this TiffImageFileDirectoryWriter builder, ushort samples)
    {
        builder.WriteTag(TiffTag.SamplesPerPixel, TiffValueCollection.Single(samples));
    }

    public static void AddRowsPerStrip(this TiffImageFileDirectoryWriter builder, int rows)
    {
        builder.WriteTag(TiffTag.RowsPerStrip, TiffValueCollection.Single((uint)rows));
    }

    public static void AddStripOffsets(this TiffImageFileDirectoryWriter builder, ulong[] offsets)
    {
        builder.WriteTag(TiffTag.StripOffsets, TiffValueCollection.UnsafeWrap(offsets));
    }

    public static void AddStripByteCounts(this TiffImageFileDirectoryWriter builder, ulong[] counts)
    {
        builder.WriteTag(TiffTag.StripByteCounts, TiffValueCollection.UnsafeWrap(counts));
    }

    public static void AddMake(this TiffImageFileDirectoryWriter builder, string make)
    {
        builder.WriteTag(TiffTag.Make, TiffValueCollection.Single(make));
    }

    public static void AddModel(this TiffImageFileDirectoryWriter builder, string model)
    {
        builder.WriteTag(TiffTag.Model, TiffValueCollection.Single(model));
    }

    public static void AddDngVersion(this TiffImageFileDirectoryWriter builder, byte[] version)
    {
        builder.WriteTag((TiffTag)50706, TiffValueCollection.UnsafeWrap(version)); // DNGVersion
    }

    public static void AddDngBackwardVersion(this TiffImageFileDirectoryWriter builder, byte[] version)
    {
        builder.WriteTag((TiffTag)50707, TiffValueCollection.UnsafeWrap(version)); // DNGBackwardVersion
    }

    public static void AddCfaRepeatPatternDim(this TiffImageFileDirectoryWriter builder, ushort[] dims)
    {
        builder.WriteTag((TiffTag)33421, TiffValueCollection.UnsafeWrap(dims)); // CFARepeatPatternDim
    }

    public static void AddBlackLevel(this TiffImageFileDirectoryWriter builder, TiffRational[] levels)
    {
        builder.WriteTag((TiffTag)50714, TiffValueCollection.UnsafeWrap(levels)); // BlackLevel
    }

    public static void AddWhiteLevel(this TiffImageFileDirectoryWriter builder, uint[] levels)
    {
        builder.WriteTag((TiffTag)50717, TiffValueCollection.UnsafeWrap(levels)); // WhiteLevel
    }

    // ==================== PHASE 5: NEW EXTENSION METHODS ====================

    public static void AddCfaPattern(this TiffImageFileDirectoryWriter builder, byte[] pattern)
    {
        builder.WriteTag((TiffTag)33422, TiffValueCollection.UnsafeWrap(pattern)); // CFAPattern
    }

    public static void AddCfaPlaneColor(this TiffImageFileDirectoryWriter builder, byte[] planeColor)
    {
        builder.WriteTag((TiffTag)50710, TiffValueCollection.UnsafeWrap(planeColor)); // CFAPlaneColor
    }

    public static void AddCfaLayout(this TiffImageFileDirectoryWriter builder, ushort layout)
    {
        builder.WriteTag((TiffTag)50711, TiffValueCollection.Single(layout)); // CFALayout
    }

    public static void AddColorMatrix1(this TiffImageFileDirectoryWriter builder, TiffRational[] matrix)
    {
        builder.WriteTag((TiffTag)50721, TiffValueCollection.UnsafeWrap(matrix)); // ColorMatrix1
    }

    public static void AddColorMatrix2(this TiffImageFileDirectoryWriter builder, TiffRational[] matrix)
    {
        builder.WriteTag((TiffTag)50722, TiffValueCollection.UnsafeWrap(matrix)); // ColorMatrix2
    }

    public static void AddCameraCalibration1(this TiffImageFileDirectoryWriter builder, TiffRational[] matrix)
    {
        builder.WriteTag((TiffTag)50723, TiffValueCollection.UnsafeWrap(matrix)); // CameraCalibration1
    }

    public static void AddCameraCalibration2(this TiffImageFileDirectoryWriter builder, TiffRational[] matrix)
    {
        builder.WriteTag((TiffTag)50724, TiffValueCollection.UnsafeWrap(matrix)); // CameraCalibration2
    }

    public static void AddAsShotNeutral(this TiffImageFileDirectoryWriter builder, TiffRational[] neutral)
    {
        builder.WriteTag((TiffTag)50728, TiffValueCollection.UnsafeWrap(neutral)); // AsShotNeutral
    }

    public static void AddAnalogBalance(this TiffImageFileDirectoryWriter builder, TiffRational[] balance)
    {
        builder.WriteTag((TiffTag)50727, TiffValueCollection.UnsafeWrap(balance)); // AnalogBalance
    }

    public static void AddBaselineExposure(this TiffImageFileDirectoryWriter builder, TiffRational exposure)
    {
        builder.WriteTag((TiffTag)50730, TiffValueCollection.Single(exposure)); // BaselineExposure
    }

    public static void AddBaselineNoise(this TiffImageFileDirectoryWriter builder, TiffRational noise)
    {
        builder.WriteTag((TiffTag)50731, TiffValueCollection.Single(noise)); // BaselineNoise
    }

    public static void AddBaselineSharpness(this TiffImageFileDirectoryWriter builder, TiffRational sharpness)
    {
        builder.WriteTag((TiffTag)50732, TiffValueCollection.Single(sharpness)); // BaselineSharpness
    }

    public static void AddLinearResponseLimit(this TiffImageFileDirectoryWriter builder, TiffRational limit)
    {
        builder.WriteTag((TiffTag)50734, TiffValueCollection.Single(limit)); // LinearResponseLimit
    }

    public static void AddUniqueCameraModel(this TiffImageFileDirectoryWriter builder, string model)
    {
        builder.WriteTag((TiffTag)50708, TiffValueCollection.Single(model)); // UniqueCameraModel
    }

    public static void AddCameraSerialNumber(this TiffImageFileDirectoryWriter builder, string serial)
    {
        builder.WriteTag((TiffTag)50735, TiffValueCollection.Single(serial)); // CameraSerialNumber
    }

    public static void AddLensMake(this TiffImageFileDirectoryWriter builder, string make)
    {
        builder.WriteTag((TiffTag)50827, TiffValueCollection.Single(make)); // LensMake
    }

    public static void AddLensModel(this TiffImageFileDirectoryWriter builder, string model)
    {
        builder.WriteTag((TiffTag)50736, TiffValueCollection.Single(model)); // LensModel
    }

    public static void AddExposureTime(this TiffImageFileDirectoryWriter builder, TiffRational time)
    {
        builder.WriteTag(TiffTag.ExposureTime, TiffValueCollection.Single(time)); // ExposureTime
    }

    public static void AddFNumber(this TiffImageFileDirectoryWriter builder, TiffRational fNumber)
    {
        builder.WriteTag(TiffTag.FNumber, TiffValueCollection.Single(fNumber)); // FNumber
    }

    public static void AddIsoSpeedRatings(this TiffImageFileDirectoryWriter builder, ushort[] iso)
    {
        builder.WriteTag(TiffTag.ISOSpeedRatings, TiffValueCollection.UnsafeWrap(iso)); // ISOSpeedRatings
    }

    public static void AddFocalLength(this TiffImageFileDirectoryWriter builder, TiffRational length)
    {
        builder.WriteTag(TiffTag.FocalLength, TiffValueCollection.Single(length)); // FocalLength
    }

    public static void AddDateTimeOriginal(this TiffImageFileDirectoryWriter builder, string dateTime)
    {
        builder.WriteTag(TiffTag.DateTimeOriginal, TiffValueCollection.Single(dateTime)); // DateTimeOriginal
    }

    public static void AddXmpMetadata(this TiffImageFileDirectoryWriter builder, string xmp)
    {
        var xmpBytes = System.Text.Encoding.UTF8.GetBytes(xmp);
        builder.WriteTag((TiffTag)700, TiffValueCollection.UnsafeWrap(xmpBytes)); // XMP
    }
}
