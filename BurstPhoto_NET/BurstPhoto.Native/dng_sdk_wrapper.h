#ifndef __dng_sdk_wrapper__
#define __dng_sdk_wrapper__

// Windows DLL export/import macros
#ifdef _WIN32
    #ifdef BURSTPHOTO_EXPORTS
        #define BURSTPHOTO_API __declspec(dllexport)
    #else
        #define BURSTPHOTO_API __declspec(dllimport)
    #endif
#else
    #define BURSTPHOTO_API
#endif

// C-compatible wrapper around C++ DNG SDK code
#ifdef __cplusplus
extern "C" {
#endif

    // Initialize / terminate Adobe XMP SDK (no-op when XMP disabled)
    BURSTPHOTO_API void initialize_xmp_sdk();
    BURSTPHOTO_API void terminate_xmp_sdk();

    // Function to read a DNG image, overwrite its pixel values, and save the result.
    // Parameters:
    //   in_path: Path to source DNG file (used to clone metadata)
    //   out_path: Path to write output DNG file
    //   pixel_bytes: Pointer to raw 16-bit pixel data (row-major, single channel)
    //   width: Image width in pixels
    //   height: Image height in pixels
    //   white_level: White level value to set (0 to keep original)
    // Returns: 0 on success, non-zero error code on failure
    BURSTPHOTO_API int write_dng_to_disk(
        const char* in_path, 
        const char* out_path, 
        void* pixel_bytes,
        int width,
        int height,
        int white_level);

    // Get the last error message (for debugging)
    BURSTPHOTO_API const char* get_last_error();

#ifdef __cplusplus
}
#endif

#endif // __dng_sdk_wrapper__
