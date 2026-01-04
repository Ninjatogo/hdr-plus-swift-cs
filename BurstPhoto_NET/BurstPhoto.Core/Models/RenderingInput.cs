using System.Collections.Generic;

namespace BurstPhoto.Core.Models;

/// <summary>
/// Encapsulates the input data for the compute pipeline.
/// </summary>
public class RenderingInput
{
    public required IReadOnlyList<RawImage> Images { get; init; }
    public required int ReferenceFrameIndex { get; init; }
}
