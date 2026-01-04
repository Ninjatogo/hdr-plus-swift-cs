// dng_jxl_stubs.cpp - Stub implementations for JXL functions when XMP is disabled
// These functions throw exceptions since JXL encoding is not supported in this build

#include "dng_exceptions.h"
#include "dng_jxl.h"

// Disable warning about deprecated flags
#pragma warning(disable: 4005)

// ParseJXL - throws exception since JXL parsing is not supported
bool ParseJXL(dng_host &host,
              dng_stream &stream,
              dng_info &info,
              bool supportBasicCodeStream,
              bool supportContainer) {
    ThrowProgramError("JXL parsing is not supported in this build");
    return false;
}

// dng_jxl_decoder stub implementations
dng_jxl_decoder::~dng_jxl_decoder() {
}

void dng_jxl_decoder::Decode(dng_host &host, dng_stream &stream) {
    ThrowProgramError("JXL decoding is not supported in this build");
}

void dng_jxl_decoder::ProcessExifBox(dng_host &host,
                                     const std::vector<uint8> &data) {
    ThrowProgramError("JXL EXIF processing is not supported in this build");
}

void dng_jxl_decoder::ProcessXMPBox(dng_host &host,
                                    const std::vector<uint8> &data) {
    ThrowProgramError("JXL XMP processing is not supported in this build");
}

void dng_jxl_decoder::ProcessBox(dng_host &host,
                                 const dng_string &name,
                                 const std::vector<uint8> &data) {
    ThrowProgramError("JXL box processing is not supported in this build");
}

// EncodeJXL_Tile stubs
void EncodeJXL_Tile(dng_host &host,
                    dng_stream &stream,
                    const dng_pixel_buffer &buffer,
                    const dng_jxl_color_space_info &colorSpaceInfo,
                    const dng_jxl_encode_settings &settings) {
    ThrowProgramError("JXL tile encoding is not supported in this build");
}

void EncodeJXL_Tile(dng_host &host,
                    dng_stream &stream,
                    const dng_image &image,
                    const dng_jxl_color_space_info &colorSpaceInfo,
                    const dng_jxl_encode_settings &settings) {
    ThrowProgramError("JXL tile encoding is not supported in this build");
}

// EncodeJXL_Container stubs
void EncodeJXL_Container(dng_host &host,
                         dng_stream &stream,
                         const dng_image &image,
                         const dng_jxl_encode_settings &settings,
                         const dng_jxl_color_space_info &colorSpaceInfo,
                         const dng_metadata *metadata,
                         const bool includeExif,
                         const bool includeXMP,
                         const bool includeIPTC,
                         const dng_bmff_box_list *additionalBoxes) {
    ThrowProgramError("JXL container encoding is not supported in this build");
}

void EncodeJXL_Container(dng_host &host,
                         dng_stream &stream,
                         const dng_pixel_buffer &buffer,
                         const dng_jxl_encode_settings &settings,
                         const dng_jxl_color_space_info &colorSpaceInfo,
                         const dng_metadata *metadata,
                         const bool includeExif,
                         const bool includeXMP,
                         const bool includeIPTC,
                         const dng_bmff_box_list *additionalBoxes) {
    ThrowProgramError("JXL container encoding is not supported in this build");
}

// Utility stubs
real32 JXLQualityToDistance(uint32 quality) {
    return 1.0f;
}

dng_jxl_encode_settings* JXLQualityToSettings(uint32 quality) {
    return nullptr;
}

void PreviewColorSpaceToJXLEncoding(const PreviewColorSpaceEnum colorSpace,
                                    const uint32 planes,
                                    dng_jxl_color_space_info &info) {
    ThrowProgramError("JXL color space encoding is not supported in this build");
}

bool SupportsJXL(const dng_image &image) {
    return false;  // JXL is not supported in this build
}
