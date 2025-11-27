using BurstPhoto.Core.Models;

namespace BurstPhoto.Core.Interfaces;

public interface IRawImageLoader
{
    RawImage Load(string path);
}
