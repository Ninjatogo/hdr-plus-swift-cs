using TiffLibrary;
using TiffLibrary.ImageEncoder;

namespace HdrPlus.IO;

/// <summary>
/// Writes DNG files using TiffLibrary.
/// </summary>
public class DngWriter
{
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
        builder.AddBitsPerSample(new ushort[] { 16 }); // 16-bit grayscale
        builder.AddCompression(TiffCompression.NoCompression);
        builder.AddPhotometricInterpretation(TiffPhotometricInterpretation.ColorFilterArray);
        builder.AddSamplesPerPixel(1);
        builder.AddRowsPerStrip(image.Height);

        // DNG-specific tags
        builder.AddDngVersion(new byte[] { 1, 4, 0, 0 }); // DNG 1.4
        builder.AddDngBackwardVersion(new byte[] { 1, 1, 0, 0 });

        // Mosaic pattern
        builder.AddCfaRepeatPatternDim(new ushort[] { (ushort)image.MosaicPatternWidth, (ushort)image.MosaicPatternWidth });

        // Black and white levels
        builder.AddBlackLevel(image.BlackLevels.Select(b => (TiffRational)b).ToArray());
        builder.AddWhiteLevel(new uint[] { (uint)image.WhiteLevel });

        // Camera info
        if (!string.IsNullOrEmpty(image.CameraMake))
        {
            builder.AddMake(image.CameraMake);
        }
        if (!string.IsNullOrEmpty(image.CameraModel))
        {
            builder.AddModel(image.CameraModel);
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
}
