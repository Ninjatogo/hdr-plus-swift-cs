#include "dng_sdk_wrapper.h"
#include "dng_exceptions.h"
#include "dng_file_stream.h"
#include "dng_host.h"
#include "dng_ifd.h"
#include "dng_image_writer.h"
#include "dng_info.h"
#include "dng_negative.h"
#include "dng_simple_image.h"

// XMP SDK is disabled via qDNGUseXMP=0 preprocessor define
// If XMP support is needed later, add: #include "dng_xmp_sdk.h"

#include <cstring>
#include <string>

// Thread-local storage for error messages
static thread_local std::string g_lastError;

void initialize_xmp_sdk() {
    // No-op when XMP is disabled (qDNGUseXMP=0)
    // If XMP is enabled, call: dng_xmp_sdk::InitializeSDK();
}

void terminate_xmp_sdk() {
    // No-op when XMP is disabled (qDNGUseXMP=0)
    // If XMP is enabled, call: dng_xmp_sdk::TerminateSDK();
}

const char* get_last_error() {
    return g_lastError.c_str();
}

int write_dng_to_disk(
    const char* in_path, 
    const char* out_path, 
    void* pixel_bytes,
    int width,
    int height,
    int white_level) 
{
    try {
        // Validate inputs
        if (!in_path || !out_path || !pixel_bytes) {
            g_lastError = "Invalid null pointer argument";
            return 1;
        }

        // Read source DNG to clone metadata
        dng_host host;
        dng_info info;
        dng_file_stream stream(in_path);
        
        AutoPtr<dng_negative> negative;
        {
            info.Parse(host, stream);
            info.PostParse(host);
            
            if (!info.IsValidDNG()) {
                g_lastError = "Input file is not a valid DNG";
                return dng_error_bad_format;
            }
            
            negative.Reset(host.Make_dng_negative());
            
            // This line ensures that the maker notes are copied
            host.SetSaveDNGVersion(dngVersion_SaveDefault);
            
            negative->Parse(host, stream, info);
            negative->PostParse(host, stream, info);
        }

        // Get the raw IFD info for image structure
        dng_ifd& rawIFD = *info.fIFD[info.fMainIndex];
        
        // Validate dimensions match
        if (rawIFD.fImageWidth != (uint32)width || rawIFD.fImageLength != (uint32)height) {
            g_lastError = "Image dimensions don't match source DNG";
            return 2;
        }

        // Create new simple image to hold our pixel data
        AutoPtr<dng_simple_image> image_pointer(
            new dng_simple_image(
                rawIFD.Bounds(), 
                rawIFD.fSamplesPerPixel, 
                rawIFD.PixelType(), 
                host.Allocator()
            )
        );
        dng_simple_image& image = *image_pointer.Get();

        // Read opcode lists (required for lens calibration data preservation)
        negative->ReadOpcodeLists(host, stream, info);

        // Copy our processed pixel data into the image buffer using public API
        dng_pixel_buffer pixelBuffer;
        image.GetPixelBuffer(pixelBuffer);
        int image_size = width * height * image.PixelSize();
        memcpy(pixelBuffer.DirtyPixel(0, 0), pixel_bytes, image_size);

        // Store modified pixel buffer to the negative using public API
        // Need to cast from dng_simple_image to dng_image base class
        AutoPtr<dng_image> baseImage(image_pointer.Release());
        negative->SetStage1Image(baseImage);

        // Validate the modified image
        // This resets some of the image stats like md5 checksums
        // Running this function will print a warning "NewRawImageDigest does not match raw image"
        // but won't halt the program. Without running this function, the output DNG
        // file would be considered 'damaged'.
        negative->ValidateRawImageDigest(host);

        // Synchronize metadata (may help with some files)
        negative->SynchronizeMetadata();

        // Update white level if specified
        if (white_level > 0) {
            negative->SetWhiteLevel(white_level, 0);
        }

        // Write DNG
        host.SetSaveLinearDNG(false);
        host.SetKeepOriginalFile(false);
        
        dng_file_stream stream2(out_path, true);
        {
            dng_image_writer writer;
            writer.WriteDNG(host, stream2, *negative.Get());
        }

        return 0;
    }
    catch (const dng_exception& e) {
        g_lastError = "DNG SDK exception: error code " + std::to_string(e.ErrorCode());
        return e.ErrorCode();
    }
    catch (const std::exception& e) {
        g_lastError = std::string("Standard exception: ") + e.what();
        return -1;
    }
    catch (...) {
        g_lastError = "Unknown exception occurred";
        return -2;
    }
}
