using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Models;
using System.IO;
using System.Text;

namespace BurstPhoto.Core.Implementations;

public class SimpleRawWriter : IRawImageWriter
{
    public void Write(string path, RawImage image)
    {
        var pixelCount = image.Width * image.Height;
        var isRgb = image.Data.Length >= pixelCount * 3; // >= to be safe

        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);

        // Header
        var magic = isRgb ? "P6" : "P5";
        var header = $"{magic}\n{image.Width} {image.Height}\n65535\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        writer.Write(headerBytes);

        // Data (Netpbm is Big Endian)
        var buffer = new byte[image.Data.Length * 2];
        for (var i = 0; i < image.Data.Length; i++)
        {
            var val = image.Data[i];
            // Swap to Big Endian
            buffer[2 * i] = (byte)(val >> 8);
            buffer[2 * i + 1] = (byte)(val & 0xFF);
        }

        writer.Write(buffer);
    }

    public Task WriteAsync(RawImage image, string path)
    {
        // For now, delegate to sync version - I/O is typically fast for single images
        return Task.Run(() => Write(path, image));
    }
}
