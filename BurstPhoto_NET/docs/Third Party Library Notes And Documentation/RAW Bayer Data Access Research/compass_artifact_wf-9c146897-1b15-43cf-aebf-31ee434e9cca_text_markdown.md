# Accessing Raw Bayer Data from DNG Files in C#

**HurlbertVisionLab.LibRawWrapper provides direct `RawData.Buffer` access** for single-channel Bayer mosaic data, making it the most straightforward solution. Alternatively, Sdcb.LibRaw's `UnsafeGetHandle()` enables custom P/Invoke access to `imgdata.rawdata.raw_image`, though this requires either a native helper library or careful structure offset calculations. No .NET DNG SDK wrapper exists, but LibRaw handles DNG files natively and can optionally integrate Adobe's SDK during compilation.

---

## Sdcb.LibRaw lacks raw_image exposure but offers a path forward

The Sdcb.LibRaw repository (v0.21.1.7, January 2025) wraps LibRaw's high-level processing API—`OpenFile()`, `Unpack()`, `DcrawProcess()`, and `MakeDcrawMemoryImage()`—but **does not expose `imgdata.rawdata.raw_image`** or any equivalent `RawImage`/`RawData` properties. The library focuses on producing demosaiced RGB output rather than raw sensor data access.

However, `RawContext.UnsafeGetHandle()` returns a valid `libraw_data_t*` pointer, which theoretically enables unsafe pointer arithmetic to reach `raw_image`. The repository contains **no existing issues or PRs** requesting raw Bayer data access, suggesting this would require either a fork or a custom P/Invoke wrapper.

**Forking feasibility** is moderate. The required changes include:
- C# struct definitions mirroring `libraw_rawdata_t` memory layout
- A `GetRawImagePointer()` accessor calculating the correct offset
- Metadata exposure for `raw_width`, `raw_height`, `top_margin`, `left_margin`, and CFA pattern

The main challenge is **structure offset volatility**—LibRaw's `libraw_data_t` contains massive nested structures (notably `libraw_colordata_t` at ~132KB due to `curve[0x10000]`), and layouts shift between LibRaw versions.

---

## LibRaw C API provides straightforward raw data access after unpack()

The canonical workflow for raw Bayer data access skips `dcraw_process()` entirely:

```c
libraw_data_t *lr = libraw_init(0);
libraw_open_file(lr, "image.dng");
libraw_unpack(lr);  // Populates rawdata.raw_image

// Access raw Bayer data directly:
unsigned short *raw = lr->rawdata.raw_image;
int first_visible = lr->sizes.top_margin * (lr->sizes.raw_pitch / 2) + lr->sizes.left_margin;
ushort pixel_value = raw[first_visible + row * (lr->sizes.raw_pitch / 2) + col];
```

**Critical detail**: Use `raw_pitch / 2` (not `raw_width`) for row stride—this handles memory alignment padding, especially when RawSpeed is enabled.

### Key structures for raw access

The `libraw_rawdata_t` structure contains mutually exclusive pointers—only **one** is non-NULL after `unpack()`:

| Pointer | Format | Use Case |
|---------|--------|----------|
| `raw_image` | `ushort*` | Standard Bayer sensors (most cameras) |
| `color3_image` | `ushort(*)[3]` | Linear DNG, Canon sRAW via RawSpeed |
| `color4_image` | `ushort(*)[4]` | 4-shot sensors (Sinar), some sRAW |
| `float_image` | `float*` | Floating-point Bayer data |

**No direct accessor function exists** for `raw_image` in LibRaw's C API—you must access the structure field directly. The `libraw_raw2image()` function creates a separate `imgdata.image[4]` array (4 components per pixel, only one populated for Bayer), which consumes **4× more memory** than accessing `raw_image` directly.

For CFA pattern identification, `libraw_COLOR(lr, row, col)` returns **0=Red, 1=Green1, 2=Blue, 3=Green2**. The pattern description lives in `imgdata.idata.cdesc` (typically "RGBG").

---

## HurlbertVisionLab.LibRawWrapper exposes raw data directly

The most practical alternative is **HurlbertVisionLab.LibRawWrapper** (NuGet: `HurlbertVisionLab.LibRawWrapper` v1.0.2.3), a C++/CLI mixed-mode assembly that explicitly provides raw Bayer access:

```csharp
using LibRawWrapper.Native;

LibRawProcessor processor = new LibRawProcessor();
processor.Open("image.dng");
processor.Unpack();

// Direct access to raw Bayer data
var rawBuffer = processor.RawData.Buffer;  // Single-channel mosaic data
```

This library maps directly to LibRaw's native API and is used by astronomy projects requiring unprocessed sensor data. **Limitations**: Windows-only, x64 architecture required (C++/CLI assembly cannot be AnyCPU), and last confirmed release October 2025.

### Alternative wrapper comparison

| Library | Raw Bayer Access | Platform | Maintenance |
|---------|-----------------|----------|-------------|
| **HurlbertVisionLab.LibRawWrapper** | ✅ `RawData.Buffer` | Win x64 | Active |
| **Sdcb.LibRaw** | ⚠️ Via UnsafeGetHandle() | Cross-platform | Active |
| **FileOnQ.Imaging.Raw** | ❌ High-level only | Windows | Stalled (2022) |
| **FreeImage (RAW_UNPROCESSED)** | ✅ FIT_UINT16 matrix | Cross-platform | Stalled |

LibRawSharp (`pmcxs/LibRawSharp`) no longer exists—the repository has been removed.

---

## P/Invoke implementation requires a native helper or exact offsets

For custom access via Sdcb.LibRaw's `UnsafeGetHandle()`, the safest approach is creating a **small native helper library** that uses `offsetof()`:

```c
// libraw_helper.c - compile against your LibRaw version
#include <libraw/libraw.h>
#include <stddef.h>

__declspec(dllexport) unsigned short* libraw_get_raw_image(libraw_data_t* data) {
    return data->rawdata.raw_image;
}

__declspec(dllexport) void libraw_get_dimensions(libraw_data_t* data,
    unsigned short* raw_width, unsigned short* raw_height,
    unsigned short* width, unsigned short* height,
    unsigned short* top_margin, unsigned short* left_margin,
    unsigned int* raw_pitch) {
    *raw_width = data->sizes.raw_width;
    *raw_height = data->sizes.raw_height;
    *width = data->sizes.width;
    *height = data->sizes.height;
    *top_margin = data->sizes.top_margin;
    *left_margin = data->sizes.left_margin;
    *raw_pitch = data->sizes.raw_pitch;
}
```

**C# P/Invoke bindings:**

```csharp
[DllImport("libraw_helper.dll")]
public static extern IntPtr libraw_get_raw_image(IntPtr librawData);

[DllImport("libraw_helper.dll")]
public static extern void libraw_get_dimensions(IntPtr librawData,
    out ushort rawWidth, out ushort rawHeight,
    out ushort width, out ushort height,
    out ushort topMargin, out ushort leftMargin,
    out uint rawPitch);

// Usage with Sdcb.LibRaw:
using RawContext r = RawContext.OpenFile("image.dng");
r.Unpack();

IntPtr handle = r.UnsafeGetHandle();
IntPtr rawImagePtr = libraw_get_raw_image(handle);

libraw_get_dimensions(handle, out var rawWidth, out var rawHeight,
    out var width, out var height, out var topMargin, out var leftMargin, out var rawPitch);

// Create managed span over raw data
unsafe {
    int stridePixels = (int)(rawPitch / 2);  // Convert byte pitch to ushort count
    var rawData = new Span<ushort>((void*)rawImagePtr, rawHeight * stridePixels);
    
    // Access visible pixel at (row, col):
    ushort pixel = rawData[(topMargin + row) * stridePixels + leftMargin + col];
}
```

**Direct offset calculation** (fragile, version-specific): On x64, `raw_image` sits at offset **8 bytes** within `libraw_rawdata_t` (immediately after `raw_alloc`). However, the offset of `rawdata` within `libraw_data_t` varies dramatically by LibRaw version due to the massive intermediate structures.

---

## No .NET DNG SDK wrapper exists—use LibRaw instead

The Adobe DNG SDK has **no official or maintained C# wrapper**. Creating one would require:
- Wrapping the C++ SDK via C++/CLI or C exports
- Handling the XMP SDK dependency
- Significant development effort with no existing foundation

**LibRaw handles DNG files natively** and can be compiled with `USE_DNGSDK` for enhanced DNG support. For .NET developers, this means LibRaw-based wrappers (Sdcb.LibRaw, HurlbertVisionLab.LibRawWrapper) are the practical path to DNG raw data.

Specific packages searched that don't exist:
- LibDNG for .NET: No NuGet package found
- DngOptics: Does not exist as a .NET library  
- Adobe DNG SDK C# wrapper: No maintained implementation

---

## Recommended implementation path

**For immediate results**: Install `HurlbertVisionLab.LibRawWrapper` from NuGet and use `RawData.Buffer` directly. This provides the cleanest API for single-channel Bayer mosaic data with minimal code.

**For cross-platform needs**: Stay with Sdcb.LibRaw and create a minimal native helper library (10-20 lines of C) that exposes `libraw_get_raw_image()` and dimension accessors. Compile this helper against the same LibRaw version as `raw_r.dll` to ensure structure compatibility.

**For maximum control**: Fork Sdcb.LibRaw and add:
- `RawImageData` property returning `Span<ushort>` for `raw_image`
- `RawSizes` struct exposing `raw_width`, `raw_height`, `raw_pitch`, margins
- `GetCfaColor(int row, int col)` method wrapping `libraw_COLOR()`

The raw Bayer data in `imgdata.rawdata.raw_image` matches what the Swift reference code obtains from `dng_sdk_wrapper.cpp`—both represent the undemosaiced sensor output as a single-channel array where each pixel's position in the CFA pattern determines its color channel.