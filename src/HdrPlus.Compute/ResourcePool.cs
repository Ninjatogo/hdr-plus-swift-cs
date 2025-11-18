using System.Collections.Concurrent;

namespace HdrPlus.Compute;

/// <summary>
/// Resource pool for efficient reuse of GPU buffers and textures.
/// Reduces allocation overhead and GPU memory fragmentation.
/// </summary>
public class ResourcePool : IDisposable
{
    private readonly IComputeDevice _device;
    private readonly ConcurrentBag<PooledBuffer> _bufferPool = new();
    private readonly ConcurrentBag<PooledTexture> _texturePool = new();
    private readonly object _lock = new();
    private bool _disposed;

    public ResourcePool(IComputeDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    /// <summary>
    /// Gets or creates a buffer from the pool.
    /// </summary>
    public IComputeBuffer GetBuffer(int sizeInBytes, BufferUsage usage = BufferUsage.Default)
    {
        // Try to find a matching buffer in the pool
        while (_bufferPool.TryTake(out var pooled))
        {
            if (pooled.SizeInBytes == sizeInBytes && pooled.Usage == usage && !pooled.IsInUse)
            {
                pooled.IsInUse = true;
                return new PooledBufferWrapper(pooled, this);
            }

            // Return to pool if not matching
            _bufferPool.Add(pooled);
        }

        // No matching buffer found, create new one
        var buffer = _device.CreateBuffer(sizeInBytes, usage);
        var pooledBuffer = new PooledBuffer
        {
            Buffer = buffer,
            SizeInBytes = sizeInBytes,
            Usage = usage,
            IsInUse = true
        };

        return new PooledBufferWrapper(pooledBuffer, this);
    }

    /// <summary>
    /// Gets or creates a texture from the pool.
    /// </summary>
    public IComputeTexture GetTexture2D(int width, int height, TextureFormat format, TextureUsage usage = TextureUsage.Default)
    {
        // Try to find a matching texture in the pool
        while (_texturePool.TryTake(out var pooled))
        {
            if (pooled.Width == width && pooled.Height == height &&
                pooled.Format == format && pooled.Usage == usage && !pooled.IsInUse)
            {
                pooled.IsInUse = true;
                return new PooledTextureWrapper(pooled, this);
            }

            // Return to pool if not matching
            _texturePool.Add(pooled);
        }

        // No matching texture found, create new one
        var texture = _device.CreateTexture2D(width, height, format, usage);
        var pooledTexture = new PooledTexture
        {
            Texture = texture,
            Width = width,
            Height = height,
            Format = format,
            Usage = usage,
            IsInUse = true
        };

        return new PooledTextureWrapper(pooledTexture, this);
    }

    /// <summary>
    /// Returns a buffer to the pool for reuse.
    /// </summary>
    internal void ReturnBuffer(PooledBuffer buffer)
    {
        if (_disposed) return;

        lock (_lock)
        {
            buffer.IsInUse = false;
            _bufferPool.Add(buffer);
        }
    }

    /// <summary>
    /// Returns a texture to the pool for reuse.
    /// </summary>
    internal void ReturnTexture(PooledTexture texture)
    {
        if (_disposed) return;

        lock (_lock)
        {
            texture.IsInUse = false;
            _texturePool.Add(texture);
        }
    }

    /// <summary>
    /// Clears unused resources from the pool.
    /// </summary>
    public void Trim()
    {
        lock (_lock)
        {
            // Remove and dispose unused buffers
            var activeBuffers = new ConcurrentBag<PooledBuffer>();
            while (_bufferPool.TryTake(out var buffer))
            {
                if (buffer.IsInUse)
                {
                    activeBuffers.Add(buffer);
                }
                else
                {
                    buffer.Buffer?.Dispose();
                }
            }

            // Return active buffers to pool
            foreach (var buffer in activeBuffers)
                _bufferPool.Add(buffer);

            // Remove and dispose unused textures
            var activeTextures = new ConcurrentBag<PooledTexture>();
            while (_texturePool.TryTake(out var texture))
            {
                if (texture.IsInUse)
                {
                    activeTextures.Add(texture);
                }
                else
                {
                    texture.Texture?.Dispose();
                }
            }

            // Return active textures to pool
            foreach (var texture in activeTextures)
                _texturePool.Add(texture);
        }
    }

    /// <summary>
    /// Gets pool statistics for monitoring.
    /// </summary>
    public PoolStatistics GetStatistics()
    {
        return new PoolStatistics
        {
            TotalBuffers = _bufferPool.Count,
            TotalTextures = _texturePool.Count,
            BuffersInUse = _bufferPool.Count(b => b.IsInUse),
            TexturesInUse = _texturePool.Count(t => t.IsInUse)
        };
    }

    public void Dispose()
    {
        if (_disposed) return;

        lock (_lock)
        {
            _disposed = true;

            // Dispose all pooled buffers
            while (_bufferPool.TryTake(out var buffer))
                buffer.Buffer?.Dispose();

            // Dispose all pooled textures
            while (_texturePool.TryTake(out var texture))
                texture.Texture?.Dispose();
        }
    }

    internal class PooledBuffer
    {
        public IComputeBuffer? Buffer { get; set; }
        public int SizeInBytes { get; set; }
        public BufferUsage Usage { get; set; }
        public bool IsInUse { get; set; }
    }

    internal class PooledTexture
    {
        public IComputeTexture? Texture { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public TextureFormat Format { get; set; }
        public TextureUsage Usage { get; set; }
        public bool IsInUse { get; set; }
    }

    private class PooledBufferWrapper : IComputeBuffer
    {
        private readonly PooledBuffer _pooled;
        private readonly ResourcePool _pool;

        public PooledBufferWrapper(PooledBuffer pooled, ResourcePool pool)
        {
            _pooled = pooled;
            _pool = pool;
        }

        public int SizeInBytes => _pooled.Buffer!.SizeInBytes;

        public void WriteData<T>(ReadOnlySpan<T> data) where T : unmanaged
            => _pooled.Buffer!.WriteData(data);

        public void ReadData<T>(Span<T> data) where T : unmanaged
            => _pooled.Buffer!.ReadData(data);

        public void Dispose() => _pool.ReturnBuffer(_pooled);
    }

    private class PooledTextureWrapper : IComputeTexture
    {
        private readonly PooledTexture _pooled;
        private readonly ResourcePool _pool;

        public PooledTextureWrapper(PooledTexture pooled, ResourcePool pool)
        {
            _pooled = pooled;
            _pool = pool;
        }

        public int Width => _pooled.Texture!.Width;
        public int Height => _pooled.Texture!.Height;
        public int Depth => _pooled.Texture!.Depth;
        public TextureFormat Format => _pooled.Texture!.Format;

        public void WriteData<T>(ReadOnlySpan<T> data) where T : unmanaged
            => _pooled.Texture!.WriteData(data);

        public void ReadData<T>(Span<T> data) where T : unmanaged
            => _pooled.Texture!.ReadData(data);

        public void Dispose() => _pool.ReturnTexture(_pooled);
    }
}

/// <summary>
/// Statistics about resource pool usage.
/// </summary>
public record PoolStatistics
{
    public int TotalBuffers { get; init; }
    public int TotalTextures { get; init; }
    public int BuffersInUse { get; init; }
    public int TexturesInUse { get; init; }
    public int AvailableBuffers => TotalBuffers - BuffersInUse;
    public int AvailableTextures => TotalTextures - TexturesInUse;
}
