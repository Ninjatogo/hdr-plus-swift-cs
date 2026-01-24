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
    private static bool _initialized = false;
    private static readonly object _initLock = new();
    private bool _disposed = false;

    #region P/Invoke Declarations

    private const string DllName = "BurstPhoto.Native.dll";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void initialize_xmp_sdk();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void terminate_xmp_sdk();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int write_dng_to_disk(
        string in_path,
        string out_path,
        IntPtr pixel_bytes,
        int width,
        int height,
        int white_level);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr get_last_error();

    #endregion

    public DngSdkWriter()
    {
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;

        lock (_initLock)
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

    public Task WriteAsync(RawImage image, string path)
    {
        return Task.Run(() => Write(path, image));
    }

    public void Write(string path, RawImage image)
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

        Console.WriteLine($"[DngSdkWriter] Writing DNG: {path}");
        Console.WriteLine($"  Source: {image.SourcePath}");
        Console.WriteLine($"  Dimensions: {image.Width}x{image.Height}");
        Console.WriteLine($"  WhiteLevel: {image.WhiteLevel}");

        // Pin the pixel data and pass to native code
        GCHandle handle = default;
        try
        {
            // Get raw bytes from ushort array
            byte[] pixelBytes = new byte[image.Data.Length * sizeof(ushort)];
            Buffer.BlockCopy(image.Data, 0, pixelBytes, 0, pixelBytes.Length);

            handle = GCHandle.Alloc(pixelBytes, GCHandleType.Pinned);
            IntPtr pixelPtr = handle.AddrOfPinnedObject();

            int result = write_dng_to_disk(
                image.SourcePath,
                path,
                pixelPtr,
                image.Width,
                image.Height,
                image.WhiteLevel);

            if (result != 0)
            {
                string errorMsg = GetLastErrorMessage();
                throw new IOException($"DNG SDK write failed (code {result}): {errorMsg}");
            }

            Console.WriteLine($"[DngSdkWriter] Successfully wrote: {path}");
        }
        finally
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }
    }

    private static string GetLastErrorMessage()
    {
        try
        {
            IntPtr errorPtr = get_last_error();
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
