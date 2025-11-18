using Silk.NET.Direct3D12;

namespace HdrPlus.Compute.DirectX12;

/// <summary>
/// Tracks GPU memory allocations for DirectX 12 resources.
/// Provides statistics and helps identify memory leaks.
/// </summary>
internal class DX12MemoryManager
{
    private readonly object _lock = new object();
    private long _totalAllocatedBytes;
    private int _bufferCount;
    private int _textureCount;
    private long _bufferBytes;
    private long _textureBytes;

    public DX12MemoryManager()
    {
        _totalAllocatedBytes = 0;
        _bufferCount = 0;
        _textureCount = 0;
        _bufferBytes = 0;
        _textureBytes = 0;
    }

    /// <summary>
    /// Records a buffer allocation.
    /// </summary>
    public void RecordBufferAllocation(long sizeInBytes)
    {
        lock (_lock)
        {
            _bufferCount++;
            _bufferBytes += sizeInBytes;
            _totalAllocatedBytes += sizeInBytes;
        }
    }

    /// <summary>
    /// Records a buffer deallocation.
    /// </summary>
    public void RecordBufferDeallocation(long sizeInBytes)
    {
        lock (_lock)
        {
            _bufferCount--;
            _bufferBytes -= sizeInBytes;
            _totalAllocatedBytes -= sizeInBytes;
        }
    }

    /// <summary>
    /// Records a texture allocation.
    /// </summary>
    public void RecordTextureAllocation(long sizeInBytes)
    {
        lock (_lock)
        {
            _textureCount++;
            _textureBytes += sizeInBytes;
            _totalAllocatedBytes += sizeInBytes;
        }
    }

    /// <summary>
    /// Records a texture deallocation.
    /// </summary>
    public void RecordTextureDeallocation(long sizeInBytes)
    {
        lock (_lock)
        {
            _textureCount--;
            _textureBytes -= sizeInBytes;
            _totalAllocatedBytes -= sizeInBytes;
        }
    }

    /// <summary>
    /// Gets total allocated GPU memory in bytes.
    /// </summary>
    public long GetTotalAllocatedBytes()
    {
        lock (_lock)
        {
            return _totalAllocatedBytes;
        }
    }

    /// <summary>
    /// Gets total allocated GPU memory in megabytes.
    /// </summary>
    public double GetTotalAllocatedMB()
    {
        return GetTotalAllocatedBytes() / (1024.0 * 1024.0);
    }

    /// <summary>
    /// Gets statistics about current allocations.
    /// </summary>
    public (int bufferCount, long bufferMB, int textureCount, long textureMB, long totalMB) GetStatistics()
    {
        lock (_lock)
        {
            return (
                _bufferCount,
                _bufferBytes / (1024 * 1024),
                _textureCount,
                _textureBytes / (1024 * 1024),
                _totalAllocatedBytes / (1024 * 1024)
            );
        }
    }

    /// <summary>
    /// Prints current memory statistics.
    /// </summary>
    public string GetStatisticsString()
    {
        var stats = GetStatistics();
        return $"GPU Memory: {stats.totalMB} MB total | Buffers: {stats.bufferCount} ({stats.bufferMB} MB) | Textures: {stats.textureCount} ({stats.textureMB} MB)";
    }
}
