using BitMiracle.LibTiff.Classic;
using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Models;
using System.IO;

namespace BurstPhoto.Core.Implementations;

public class LibTiffDngWriter : IRawImageWriter
{
    // DNG Tag Constants
    private const TiffTag DngVersion = (TiffTag)50706;
    private const TiffTag DngBackwardVersion = (TiffTag)50707;
    private const TiffTag UniqueCameraModel = (TiffTag)50708;
    private const TiffTag LocalizedCameraModel = (TiffTag)50709;
    
    // CFA / DNG Tags
    private const TiffTag CfaPlaneColor = (TiffTag)50710;
    private const TiffTag CfaLayout = (TiffTag)50711; 
    private const TiffTag LinearizationTable = (TiffTag)50712;
    private const TiffTag BlackLevelRepeatDim = (TiffTag)50713;
    private const TiffTag BlackLevel = (TiffTag)50714;
    private const TiffTag BlackLevelDeltaH = (TiffTag)50715;
    private const TiffTag BlackLevelDeltaV = (TiffTag)50716;
    private const TiffTag WhiteLevel = (TiffTag)50717;
    private const TiffTag DefaultScale = (TiffTag)50718;
    private const TiffTag DefaultCropOrigin = (TiffTag)50719;
    private const TiffTag DefaultCropSize = (TiffTag)50720;
    private const TiffTag ColorMatrix1 = (TiffTag)50721;
    private const TiffTag ColorMatrix2 = (TiffTag)50722;
    private const TiffTag CameraCalibration1 = (TiffTag)50723;
    private const TiffTag CameraCalibration2 = (TiffTag)50724;
    private const TiffTag ReductionMatrix1 = (TiffTag)50725;
    private const TiffTag ReductionMatrix2 = (TiffTag)50726;
    private const TiffTag AnalogBalance = (TiffTag)50727;
    private const TiffTag AsShotNeutral = (TiffTag)50728;
    private const TiffTag BaselineExposure = (TiffTag)50730;
    private const TiffTag BaselineNoise = (TiffTag)50731;
    private const TiffTag BaselineSharpness = (TiffTag)50732;
    private const TiffTag BayerGreenSplit = (TiffTag)50733;
    private const TiffTag LinearResponseLimit = (TiffTag)50734;
    private const TiffTag CameraSerialNumber = (TiffTag)50735;
    private const TiffTag LensInfo = (TiffTag)50736;
    private const TiffTag ChromaBlurRadius = (TiffTag)50737;
    private const TiffTag AntiAliasStrength = (TiffTag)50738;
    private const TiffTag ShadowScale = (TiffTag)50739;
    private const TiffTag DngPrivateData = (TiffTag)50740;
    private const TiffTag MakerNoteSafety = (TiffTag)50741;
    private const TiffTag CalibrationIlluminant1 = (TiffTag)50778;
    private const TiffTag CalibrationIlluminant2 = (TiffTag)50779;
    private const TiffTag BestQualityScale = (TiffTag)50780;

    // Standard TIFF/EP Tags
    private const TiffTag CfaRepeatPatternDim = (TiffTag)33421;
    private const TiffTag CfaPattern = (TiffTag)33422;

    private static bool _tagsRegistered;

    public LibTiffDngWriter()
    {
        // Note: LibTiff.Net's SetTagExtender is called once and affects all subsequent file opens
        if (!_tagsRegistered)
        {
            try
            {
                RegisterDngTags();
                _tagsRegistered = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Failed to register DNG tags: {ex.Message}");
            }
        }
    }

    public Task WriteAsync(RawImage image, string path)
    {
        return Task.Run(() => Write(path, image));
    }

    public void Write(string path, RawImage image)
    {
        // Console.WriteLine($"LibTiffDngWriter.Write called for {path}");
        using var output = Tiff.Open(path, "w");
        if (output is null)
        {
            throw new IOException($"Could not open file {path} for writing");
        }
            
        ConfigureTiffTags(output, image);
        WriteImageData(output, image);
            
        output.WriteDirectory();
    }

    private void ConfigureTiffTags(Tiff tif, RawImage image)
    {
        var width = image.Width;
        var height = image.Height;

        tif.SetField(TiffTag.IMAGEWIDTH, width);
        tif.SetField(TiffTag.IMAGELENGTH, height);
        tif.SetField(TiffTag.DNGVERSION, "\x01\x04\x00\x00"); // 1.4.0.0
        tif.SetField(TiffTag.DNGBACKWARDVERSION, "\x01\x01\x00\x00"); // 1.1.0.0
        
        // Use camera model from source if available, otherwise use generic name
        var cameraModel = !string.IsNullOrEmpty(image.CameraModel) 
            ? $"{image.CameraMake} {image.CameraModel}".Trim() 
            : "BurstPhoto_NET DNG Writer";
        tif.SetField(UniqueCameraModel, cameraModel);

        // Determine if RGB or Bayer
        var isRgb = image.Data.Length >= width * height * 3;

        if (isRgb)
        {
            // Linear DNG (demosaiced RGB)
            tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
            tif.SetField(TiffTag.SAMPLESPERPIXEL, 3);
            tif.SetField(TiffTag.BITSPERSAMPLE, 16);
            tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG); // Interleaved RGB
        }
        else
        {
            // Bayer Raw
            tif.SetField(TiffTag.PHOTOMETRIC, (Photometric)32803); // CFA
            tif.SetField(TiffTag.SAMPLESPERPIXEL, 1);
            tif.SetField(TiffTag.BITSPERSAMPLE, 16);
            tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);

            // CFA Pattern - use source pattern if available
            tif.SetField(CfaRepeatPatternDim, 2, new short[] { 2, 2 });
            if (image.CfaPattern is { Length: >= 4 })
            {
                var cfaBytes = image.CfaPattern.Take(4).Select(i => (byte)i).ToArray();
                tif.SetField(CfaPattern, 4, cfaBytes);
            }
            else
            {
                tif.SetField(CfaPattern, 4, new byte[] { 0, 1, 1, 2 }); // RGGB default
            }
            tif.SetField(CfaPlaneColor, 3, new byte[] { 0, 1, 2 });
            tif.SetField(CfaLayout, 1);
        }

        tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tif.SetField(TiffTag.COMPRESSION, Compression.NONE); 
        tif.SetField(TiffTag.ROWSPERSTRIP, height);

        // BlackLevel from source
        if (image.BlackLevel is { Length: >= 4 })
        {
             // Must set RepeatDim if we have pattern black levels
             tif.SetField(BlackLevelRepeatDim, 2, new short[] { 2, 2 });

             // Convert int[] to double[] for RATIONAL tag
             var bl = new double[image.BlackLevel.Length];
             for(var i=0; i<bl.Length; i++) bl[i] = image.BlackLevel[i];
             tif.SetField(BlackLevel, (short)bl.Length, bl);
        }
        else
        {
             tif.SetField(BlackLevel, 1, new[] { 0.0 });
        }

        // WhiteLevel from source
        // NOTE: LibTiff.Net has a bug where WhiteLevel (tag 50717) causes OverflowException
        // The library has internal handling that forces LONG type conversion with signed int32
        // This fails even for valid values like 16383 or 65535
        //
        // Attempted solutions that failed:
        // 1. Custom TiffFieldInfo with SHORT type - LibTiff.Net internal definition takes precedence
        // 2. Custom TiffFieldInfo with RATIONAL type - Still triggers overflow in writeLongArray
        // 3. Changing passCount parameter - No effect due to internal override
        //
        // Potential workarounds (not yet implemented):
        // - Write WhiteLevel as raw IFD entry using lower-level TIFF API
        // - Patch BitMiracle.LibTiff.NET source code to use uint instead of int
        // - Use alternative DNG writing library (e.g., direct libtiff P/Invoke)
        //
        // For now, WhiteLevel is omitted. Most DNG readers will infer it from:
        // - BITSPERSAMPLE tag (16-bit = 65535 max)
        // - Actual pixel value range in the image data
        //
        // Uncomment below to attempt writing (will cause OverflowException):
        /*
        if (image.WhiteLevel > 0)
        {
            ushort[] whiteLevel = new ushort[] { (ushort)Math.Min(image.WhiteLevel, 65535) };
            tif.SetField(WhiteLevel, 1, whiteLevel);
        }
        else
        {
            tif.SetField(WhiteLevel, 1, new ushort[] { 65535 });
        }
        */

        // ColorMatrix1 - use source if available, otherwise use identity
        if (image.ColorMatrix1 is { Length: >= 9 })
        {
            tif.SetField(ColorMatrix1, image.ColorMatrix1.Length, image.ColorMatrix1);
        }
        else
        {
            // Identity matrix as fallback
            var identityMatrix = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            tif.SetField(ColorMatrix1, 9, identityMatrix);
        }

        // ColorMatrix2 - use source if available
        if (image.ColorMatrix2 is { Length: >= 9 })
        {
            tif.SetField(ColorMatrix2, image.ColorMatrix2.Length, image.ColorMatrix2);
        }

        // AsShotNeutral - use source if available, otherwise compute from ColorFactors
        if (image.AsShotNeutral is { Length: >= 3 })
        {
            tif.SetField(AsShotNeutral, image.AsShotNeutral.Length, image.AsShotNeutral);
        }
        else if (image.ColorFactors is { Length: >= 3 })
        {
            // Compute from ColorFactors (camera multipliers) - these ARE the neutral values
            var asShotNeutral = new double[3];
            if (image.ColorFactors.Length >= 4)
            {
                 double r = image.ColorFactors[0];
                 var g = (image.ColorFactors[1] + image.ColorFactors[2]) / 2.0;
                 double b = image.ColorFactors[3];
                 
                 // Normalize to make max = 1
                 var maxVal = Math.Max(r, Math.Max(g, b));
                 if (maxVal > 0)
                 {
                     asShotNeutral[0] = r / maxVal;
                     asShotNeutral[1] = g / maxVal;
                     asShotNeutral[2] = b / maxVal;
                 }
                 else
                 {
                     asShotNeutral = [1, 1, 1];
                 }
            }
            else
            {
                 asShotNeutral = [1, 1, 1];
            }
            
            tif.SetField(AsShotNeutral, 3, asShotNeutral);
        }
        else
        {
             tif.SetField(AsShotNeutral, 3, new double[] { 1, 1, 1 });
        }
        
        // CalibrationIlluminant1 - use source if available
        if (image.CalibrationIlluminant1 > 0)
        {
            tif.SetField(CalibrationIlluminant1, image.CalibrationIlluminant1);
        }
        else
        {
            tif.SetField(CalibrationIlluminant1, 21); // D65 default
        }

        // CalibrationIlluminant2 - use source if available
        if (image.CalibrationIlluminant2 > 0)
        {
            tif.SetField(CalibrationIlluminant2, image.CalibrationIlluminant2);
        }
    }

    private void WriteImageData(Tiff tif, RawImage image)
    {
        var width = image.Width;
        var height = image.Height;
        var data = image.Data;
        
        var isRgb = data.Length >= width * height * 3;
        var samplesPerPixel = isRgb ? 3 : 1;
        
        var scanlineSize = width * samplesPerPixel * 2;
        var buffer = new byte[scanlineSize];

        for (var row = 0; row < height; row++)
        {
            var rowOffset = row * width * samplesPerPixel;
            Buffer.BlockCopy(data, rowOffset * 2, buffer, 0, scanlineSize);
            tif.WriteScanline(buffer, row);
        }
    }

    private static void RegisterDngTags()
    {
        Tiff.SetTagExtender(DngTagExtender);
    }

    private static void DngTagExtender(Tiff tif)
    {
        var dngFields = new[]
        {
            new TiffFieldInfo(DngVersion, 4, 4, TiffType.BYTE, FieldBit.Custom, false, false, "DNGVersion"),
            new TiffFieldInfo(DngBackwardVersion, 4, 4, TiffType.BYTE, FieldBit.Custom, false, false, "DNGBackwardVersion"),
            new TiffFieldInfo(UniqueCameraModel, -1, -1, TiffType.ASCII, FieldBit.Custom, false, false, "UniqueCameraModel"),
            new TiffFieldInfo(LocalizedCameraModel, -1, -1, TiffType.ASCII, FieldBit.Custom, false, false, "LocalizedCameraModel"),
            
            // CFA Tags - Set pass_count=true for arrays
            new TiffFieldInfo(CfaRepeatPatternDim, 2, 2, TiffType.SHORT, FieldBit.Custom, false, true, "CFARepeatPatternDim"),
            new TiffFieldInfo(CfaPattern, -1, -1, TiffType.BYTE, FieldBit.Custom, false, true, "CFAPattern"),
            new TiffFieldInfo(CfaPlaneColor, -1, -1, TiffType.BYTE, FieldBit.Custom, false, true, "CFAPlaneColor"),
            new TiffFieldInfo(CfaLayout, 1, 1, TiffType.SHORT, FieldBit.Custom, false, false, "CFALayout"), // Fixed 1, no array
            
            new TiffFieldInfo(LinearizationTable, -1, -1, TiffType.SHORT, FieldBit.Custom, false, true, "LinearizationTable"),
            new TiffFieldInfo(BlackLevelRepeatDim, 2, 2, TiffType.SHORT, FieldBit.Custom, false, true, "BlackLevelRepeatDim"),
            new TiffFieldInfo(BlackLevel, -1, -1, TiffType.RATIONAL, FieldBit.Custom, true, false, "BlackLevel"),
            new TiffFieldInfo(BlackLevelDeltaH, -1, -1, TiffType.SRATIONAL, FieldBit.Custom, false, true, "BlackLevelDeltaH"),
            new TiffFieldInfo(BlackLevelDeltaV, -1, -1, TiffType.SRATIONAL, FieldBit.Custom, false, true, "BlackLevelDeltaV"),

            // WhiteLevel: NOT registered here - LibTiff.Net has internal definition
            // Will attempt to write using raw tag write method instead

            new TiffFieldInfo(DefaultScale, 2, 2, TiffType.RATIONAL, FieldBit.Custom, false, true, "DefaultScale"),
            new TiffFieldInfo(DefaultCropOrigin, 2, 2, TiffType.RATIONAL, FieldBit.Custom, false, true, "DefaultCropOrigin"),
            new TiffFieldInfo(DefaultCropSize, 2, 2, TiffType.RATIONAL, FieldBit.Custom, false, true, "DefaultCropSize"),
            new TiffFieldInfo(ColorMatrix1, -1, -1, TiffType.SRATIONAL, FieldBit.Custom, false, true, "ColorMatrix1"),
            new TiffFieldInfo(ColorMatrix2, -1, -1, TiffType.SRATIONAL, FieldBit.Custom, false, true, "ColorMatrix2"),
            new TiffFieldInfo(CameraCalibration1, -1, -1, TiffType.SRATIONAL, FieldBit.Custom, false, true, "CameraCalibration1"),
            new TiffFieldInfo(CameraCalibration2, -1, -1, TiffType.SRATIONAL, FieldBit.Custom, false, true, "CameraCalibration2"),
            new TiffFieldInfo(ReductionMatrix1, -1, -1, TiffType.SRATIONAL, FieldBit.Custom, false, true, "ReductionMatrix1"),
            new TiffFieldInfo(ReductionMatrix2, -1, -1, TiffType.SRATIONAL, FieldBit.Custom, false, true, "ReductionMatrix2"),
            new TiffFieldInfo(AnalogBalance, -1, -1, TiffType.RATIONAL, FieldBit.Custom, false, true, "AnalogBalance"),
            new TiffFieldInfo(AsShotNeutral, -1, -1, TiffType.RATIONAL, FieldBit.Custom, false, true, "AsShotNeutral"),

            new TiffFieldInfo(BaselineExposure, 1, 1, TiffType.SRATIONAL, FieldBit.Custom, false, false, "BaselineExposure"),
            new TiffFieldInfo(BaselineNoise, 1, 1, TiffType.RATIONAL, FieldBit.Custom, false, false, "BaselineNoise"),
            new TiffFieldInfo(BaselineSharpness, 1, 1, TiffType.RATIONAL, FieldBit.Custom, false, false, "BaselineSharpness"),
            new TiffFieldInfo(BayerGreenSplit, 1, 1, TiffType.LONG, FieldBit.Custom, false, false, "BayerGreenSplit"),
            new TiffFieldInfo(LinearResponseLimit, 1, 1, TiffType.RATIONAL, FieldBit.Custom, false, false, "LinearResponseLimit"),
            new TiffFieldInfo(CameraSerialNumber, -1, -1, TiffType.ASCII, FieldBit.Custom, false, false, "CameraSerialNumber"),
            new TiffFieldInfo(LensInfo, 4, 4, TiffType.RATIONAL, FieldBit.Custom, false, false, "LensInfo"),
            new TiffFieldInfo(ChromaBlurRadius, 1, 1, TiffType.RATIONAL, FieldBit.Custom, false, false, "ChromaBlurRadius"),
            new TiffFieldInfo(AntiAliasStrength, 1, 1, TiffType.RATIONAL, FieldBit.Custom, false, false, "AntiAliasStrength"),
            new TiffFieldInfo(ShadowScale, 1, 1, TiffType.RATIONAL, FieldBit.Custom, false, false, "ShadowScale"),
            new TiffFieldInfo(CalibrationIlluminant1, 1, 1, TiffType.SHORT, FieldBit.Custom, false, false, "CalibrationIlluminant1"),
            new TiffFieldInfo(CalibrationIlluminant2, 1, 1, TiffType.SHORT, FieldBit.Custom, false, false, "CalibrationIlluminant2"),
            new TiffFieldInfo(BestQualityScale, 1, 1, TiffType.RATIONAL, FieldBit.Custom, false, false, "BestQualityScale")
        };

        tif.MergeFieldInfo(dngFields, dngFields.Length);
    }
}
