using BurstPhoto.Core.Models;

namespace BurstPhoto.Core.Interfaces;

public interface IRawImageWriter
{
    void Write(string path, RawImage image);
    Task WriteAsync(RawImage image, string path);
}
