using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Models;
using System.IO;
using System.Runtime.InteropServices;

namespace BurstPhoto.Core.Implementations;

/// <summary>
/// Writes DNG files using the Adobe DNG SDK via native P/Invoke.
/// Uses a "Clone and Patch" approach: reads source DNG metadata, overwrites pixels.
/// </summary>
public class DngSdkWriter : IRawImageWriter, IDisposable
{
    private static bool _initialized;
    private static readonly Lock InitLock = new();
    private bool _disposed;

    #region P/Invoke Declarations

    private const string DllName = "BurstPhoto.Native.dll";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void initialize_xmp_sdk();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void terminate_xmp_sdk();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int write_dng_to_disk(
        string inPath,
        string outPath,
        IntPtr pixelBytes,
        int width,
        int height,
        int whiteLevel);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr get_last_error();

    #endregion

    private static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_initialized) return;
        }

        lock (InitLock)
        {
            if (_initialized) return;

            try
            {
                initialize_xmp_sdk();
                _initialized = true;
                Console.WriteLine("[DngSdkWriter] Adobe DNG SDK initialized");
            }
            catch (DllNotFoundException ex)
            {
                throw new InvalidOperationException(
                    $"Could not find {DllName}. Ensure it is in the application directory or PATH.", ex);
            }
        }
    }

    /// <inheritdoc />
    public Task WriteAsync(RawImage image, string outputPath)
    {
        return Task.Run(() => Write(image, outputPath));
    }

    /// <inheritdoc />
    public void Write(RawImage image, string outputPath)
    {
        EnsureInitialized();
        if (string.IsNullOrEmpty(image.SourcePath))
        {
            throw new ArgumentException(
                "RawImage.SourcePath must be set to the original DNG path for metadata cloning.", 
                nameof(image));
        }

        if (!File.Exists(image.SourcePath))
        {
            throw new FileNotFoundException(
                $"Source DNG file not found: {image.SourcePath}", image.SourcePath);
        }

        Console.WriteLine($"[DngSdkWriter] Writing DNG: {outputPath}");
        Console.WriteLine($"  Source: {image.SourcePath}");
        Console.WriteLine($"  Dimensions: {image.Width}x{image.Height}");
        Console.WriteLine($"  WhiteLevel: {image.WhiteLevel}");

        // Pin the pixel data and pass to native code
        GCHandle pinnedHandle = default;
        try
        {
            // Convert ushort array to byte array for native interop
            var pixelBytes = new byte[image.Data.Length * sizeof(ushort)];
            Buffer.BlockCopy(image.Data, 0, pixelBytes, 0, pixelBytes.Length);

            pinnedHandle = GCHandle.Alloc(pixelBytes, GCHandleType.Pinned);
            var pixelDataPointer = pinnedHandle.AddrOfPinnedObject();

            var result = write_dng_to_disk(
                image.SourcePath,
                outputPath,
                pixelDataPointer,
                image.Width,
                image.Height,
                image.WhiteLevel);

            if (result != 0)
            {
                var lastErrorMessage = GetLastErrorMessage();
                throw new IOException($"DNG SDK write failed (code {result}): {lastErrorMessage}");
            }

            Console.WriteLine($"[DngSdkWriter] Successfully wrote: {outputPath}");
        }
        finally
        {
            if (pinnedHandle.IsAllocated)
            {
                pinnedHandle.Free();
            }
        }
    }

    private static string GetLastErrorMessage()
    {
        try
        {
            var errorPtr = get_last_error();
            if (errorPtr != IntPtr.Zero)
            {
                return Marshal.PtrToStringAnsi(errorPtr) ?? "Unknown error";
            }
        }
        catch
        {
            // Ignore errors getting error message
        }
        return "Unknown error";
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        // Note: We don't call terminate_xmp_sdk() here because:
        // 1. XMP is disabled in this build (qDNGUseXMP=0)
        // 2. The static initialization is shared across instances
        // 3. Terminating during app lifecycle would break subsequent writes

        _disposed = true;
    }

    ~DngSdkWriter()
    {
        Dispose(false);
    }
}
