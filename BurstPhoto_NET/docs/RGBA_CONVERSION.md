# RGBA Conversion Documentation

## Overview

The RGBA conversion functionality is a critical optimization step in the image processing pipeline. Its primary purpose is to pack 2x2 blocks of single-channel Bayer pattern data into individual RGBA pixels.

This transformation allows the application to leverage SIMD (Single Instruction, Multiple Data) operations on the GPU. By treating the four sub-pixels of a Bayer pattern (typically R, G, G, B) as a single vector unit (a pixel with 4 components), subsequent operations—such as frequency domain merging and Fourier Transforms—can process four raw pixels simultaneously in a single thread.

## Context in Pipeline

This conversion occurs during the **Merging** phase, specifically within the Frequency Domain Merging workflow (`align_merge_frequency_domain`).

1.  **Preparation**: Raw images are first "prepared" (hotpixel correction, exposure compensation, black level subtraction). The result is a single-channel floating-point texture representing the Bayer data.
2.  **Conversion (Packing)**: The prepared single-channel texture is converted to an RGBA texture using the logic described here.
3.  **Processing**: The RGBA texture is used for:
    *   Noise estimation (`calculate_rms_rgba`)
    *   Fast Fourier Transforms (`forward_ft`)
    *   Mismatch calculation (`calculate_mismatch_rgba`)
    *   Merging (`merge_frequency_domain`)
4.  **Reversion (Unpacking)**: After merging and applying inverse FFT, the resulting RGBA texture is unpacked back into a single-channel Bayer-like texture (`convert_to_bayer`) before final cropping and accumulation.

## Inputs and Outputs

### Inputs
*   **Conceptually**: A single-channel 2D array (texture) containing raw Bayer sensor data.
*   **Format**: 32-bit Floating Point (R32Float).
*   **Current .NET Implementation Note**: The .NET port currently uses `LibRaw` which may output processed 16-bit linear RGB images. While the logic below describes the operation on *Raw Bayer* data, a developer re-implementing this for the .NET port must ensure the input is effectively treated as a mosaic pattern. If starting from a demosaiced RGB image, one might conceptually treat it as valid input only if it's being "re-mosaiced" or if the pipeline is adapted to handle full RGB planes differently. However, the core algorithm assumes a mosaic structure where `(0,0)` is one color, `(1,0)` is another, etc.

### Outputs
*   **Packed Output**: An RGBA texture with half the width and half the height of the input texture.
*   **Format**: 32-bit Floating Point per channel (RGBA32Float) or 16-bit (RGBA16Float) depending on precision requirements.

## Logic Description

The logic maps spatial locality in the Bayer image to channel depth in the RGBA image.

*   Input Pixel `(x, y)` maps to Output Pixel `(x/2, y/2)` Channel `Red`
*   Input Pixel `(x+1, y)` maps to Output Pixel `(x/2, y/2)` Channel `Green`
*   Input Pixel `(x, y+1)` maps to Output Pixel `(x/2, y/2)` Channel `Blue`
*   Input Pixel `(x+1, y+1)` maps to Output Pixel `(x/2, y/2)` Channel `Alpha`

*Note: The channel mapping names (Red, Green, Blue, Alpha) are just convenient labels for the 0th, 1st, 2nd, and 3rd components of the vector. They do not necessarily correspond to the actual Red, Green, and Blue colors of the Bayer filter, which depends on the specific CFA pattern (RGGB, BGGR, etc.).*

## Pseudo Code

The following pseudo-code describes the compute kernel logic.

### 1. Packing (Convert to RGBA)

This function takes a full-resolution single-channel texture and packs it into a half-resolution RGBA texture.

```csharp
// Inputs:
// input_texture: 2D array of floats (Single Channel)
// width, height: Dimensions of the output texture (half of input)
// pad_left, pad_top: Optional offsets to align the bayer pattern or crop

// For each pixel (x, y) in the OUTPUT texture:
function ConvertToRGBA(x, y) {
    // Calculate the coordinates in the source (input) texture
    // The factor of 2 accounts for the downscaling
    let src_x = (x * 2) + pad_left;
    let src_y = (y * 2) + pad_top;

    // Read the 2x2 block from the input
    let val_0 = input_texture[src_x,     src_y];     // Top-Left
    let val_1 = input_texture[src_x + 1, src_y];     // Top-Right
    let val_2 = input_texture[src_x,     src_y + 1]; // Bottom-Left
    let val_3 = input_texture[src_x + 1, src_y + 1]; // Bottom-Right

    // Write to the output texture at (x, y)
    // The 4 values become the 4 components of the single output pixel
    output_texture[x, y] = Vector4(val_0, val_1, val_2, val_3);
}
```

### 2. Unpacking (Convert to Bayer)

This function takes the processed half-resolution RGBA texture and unpacks it back into a full-resolution single-channel texture.

```csharp
// Inputs:
// input_texture: 2D array of Vector4 (RGBA texture)
// width, height: Dimensions of the input texture

// For each pixel (gid_x, gid_y) in the INPUT texture:
function ConvertToBayer(gid_x, gid_y) {
    // Calculate coordinates in the output texture
    let out_x = gid_x * 2;
    let out_y = gid_y * 2;

    // Read the vector value from the input
    let pixel_vector = input_texture[gid_x, gid_y];

    // Unpack the components back to their spatial positions
    output_texture[out_x,     out_y]     = pixel_vector.x; // Component 0
    output_texture[out_x + 1, out_y]     = pixel_vector.y; // Component 1
    output_texture[out_x,     out_y + 1] = pixel_vector.z; // Component 2
    output_texture[out_x + 1, out_y + 1] = pixel_vector.w; // Component 3
}
```
