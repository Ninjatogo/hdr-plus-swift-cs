using BurstPhoto.Core.Models;

namespace BurstPhoto.Core.Interfaces;

/// <summary>
/// Defines the GPU-accelerated compute pipeline for burst photo alignment and merging.
/// </summary>
/// <remarks>
/// Implementations of this interface handle the computationally intensive operations:
/// <list type="bullet">
///   <item><description>Building image pyramids for multi-scale alignment</description></item>
///   <item><description>Tile-based motion estimation between frames</description></item>
///   <item><description>Warping comparison frames to align with the reference</description></item>
///   <item><description>Merging aligned frames using spatial or frequency domain algorithms</description></item>
/// </list>
/// </remarks>
public interface IComputePipeline : IDisposable
{
    /// <summary>
    /// Processes a burst of images to produce a single aligned and merged output.
    /// </summary>
    /// <param name="input">
    /// The rendering input containing all loaded images and the reference frame index.
    /// </param>
    /// <param name="options">
    /// Processing options controlling tile size, search distance, merging algorithm, etc.
    /// </param>
    /// <param name="progress">
    /// Progress reporter for UI updates. The pipeline should update this throughout processing.
    /// </param>
    /// <param name="cancellationToken">
    /// Token to support cancellation of the processing operation.
    /// </param>
    /// <returns>
    /// The merged <see cref="RawImage"/> with reduced noise and improved detail.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is cancelled via the <paramref name="cancellationToken"/>.
    /// </exception>
    Task<RawImage> ProcessAsync(
        RenderingInput input,
        ProcessingOptions options,
        ProcessingProgress progress,
        CancellationToken cancellationToken = default);
}
