using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Models;
using Sdcb.LibRaw;
using System;
using System.Runtime.InteropServices;

namespace BurstPhoto.Core.Implementations;

public class LibRawLoader : IRawImageLoader
{
    public unsafe RawImage Load(string path)
    {
        using var context = RawContext.OpenFile(path);

        // For Vertical Slice, we use DcrawProcess to get a linear 16-bit image.
        // TODO: Switch to raw bayer access.
        context.Unpack();
        context.OutputBitsPerSample = 16;
        context.Gamma[0] = 1;
        context.Gamma[1] = 1; // Linear
        context.DcrawProcess();

        using var processed = context.MakeDcrawMemoryImage();

        int width = processed.Width;
        int height = processed.Height;

        // Access properties verified via CLI debug
        var colorFactors = context.CameraMultipler; // IReadOnlyList<float>

        var rawImage = new RawImage
        {
            Width = width,
            Height = height,
            WhiteLevel = context.ColorMaximum,
            BlackLevel = new int[] { 0, 0, 0, 0 }, // Not directly exposed at top level
            ExposureBias = 0,
            IsoExposureTime = 0, // context.ImageOtherParams is available but need to cast/access fields
            ColorFactors = new float[] { colorFactors[0], colorFactors[1], colorFactors[2], colorFactors[3] },
            MosaicPatternWidth = 2
        };

        // Copy Data
        // processed is likely HxWx3 (RGB)
        // processed.AsSpan<byte>() gives bytes.
        var srcSpan = processed.AsSpan<byte>();

        // We want ushorts.
        // Data length in bytes
        int byteLength = srcSpan.Length;
        int ushortLength = byteLength / 2;

        rawImage.Data = new ushort[ushortLength];

        fixed (ushort* dst = rawImage.Data)
        fixed (byte* src = srcSpan)
        {
            Buffer.MemoryCopy(src, dst, byteLength, byteLength);
        }

        return rawImage;
    }
}
