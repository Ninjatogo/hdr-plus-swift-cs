# Frequency Domain Merging (High Quality)

## Overview

The frequency domain merging algorithm (`burstphoto/merge/frequency.swift`) corresponds to the "High Quality" setting. It operates by transforming image tiles into the frequency domain (using Fourier Transforms) to perform merging. This allows for more precise handling of noise and structural alignment, particularly for complex textures and motion.

## Algorithm Pipeline

The function `align_merge_frequency_domain` orchestrates the process.

### Artifact Suppression Loop
The entire merging process runs **4 times**. In each iteration, the image is shifted by a small amount (related to the tile size). This "shift-and-merge" strategy helps suppress blocking artifacts that can occur at the boundaries of the frequency domain tiles.

For each of the 4 iterations:
1.  **Preparation**:
    *   Reference and comparison textures are padded and shifted.
    *   **RGBA Conversion**: Images are converted to RGBA format to utilize SIMD instructions efficiently on the GPU.
2.  **Reference Processing**:
    *   **Forward FFT**: The reference image is transformed into the frequency domain (`forward_ft`).
    *   **RMS Calculation**: Root Mean Square values are calculated for normalization.
3.  **Iterative Merging**:
    For each comparison image:
    1.  **Align**: Spatial alignment is performed first (see [Alignment](alignment.md)).
    2.  **Mismatch Calculation**: Calculates the difference (`mismatch_texture`) between aligned and reference images.
    3.  **Normalization**: Normalizes the mismatch to handle exposure differences.
    4.  **Forward FFT**: Transforms the aligned comparison image to the frequency domain.
    5.  **Frequency Merge**: `merge_frequency_domain` shader blends the reference and comparison coefficients based on robustness and motion norms.
4.  **Reconstruction**:
    *   **Deconvolution**: Corrects potential blurring.
    *   **Backward FFT**: Transforms the merged frequency data back to the spatial domain (`backward_ft`).
    *   **Bayer Conversion**: Converts RGBA back to Bayer pattern.
    *   **Accumulate**: Adds the result of this iteration to the final accumulator.

## Frequency Domain Merging Logic

The core logic happens in the `merge_frequency_domain` shader. It combines the Fourier coefficients of the reference ($R$) and the aligned comparison ($C$).

$$ Out = \frac{W_r \cdot R + W_c \cdot C}{W_r + W_c} $$

The weights ($W$) are derived from:
*   **Robustness Norm**: User setting for noise reduction strength.
*   **Read Noise**: Sensor characteristics.
*   **Motion Norm**: Penalty for differences (mismatch) to avoid ghosting.

## Logic Flow Diagram

```mermaid
graph TD
    subgraph Iteration_1_to_4
        Ref[Reference Texture] --> ConvertRef[Convert to RGBA]
        ConvertRef --> FFT_Ref[Forward FFT]

        Comp[Comparison Texture] --> Align[Align (Spatial)]
        Align --> ConvertComp[Convert to RGBA]
        ConvertComp --> FFT_Comp[Forward FFT]

        FFT_Ref --> Merge[Frequency Merge]
        FFT_Comp --> Merge

        Align --> Mismatch[Calc Mismatch]
        Ref --> Mismatch
        Mismatch --> Merge

        Merge --> MergedFreq[Merged Frequency Data]
        MergedFreq --> IFFT[Backward FFT]
        IFFT --> ConvertBayer[Convert to Bayer]
    end

    ConvertBayer --> Accum[Final Accumulator]
```

## Key Functions

### `forward_ft` / `backward_ft`
Performs the Forward and Backward Fourier Transforms.
*   Uses optimized FFT for small tile sizes (<= 8).
*   Uses DFT for larger tile sizes.
*   Operates on RGBA packed data.

### `merge_frequency_domain`
The core kernel that blends the spectral components.
*   **Inputs**: Ref FFT, Aligned FFT, RMS, Mismatch, Highlight Norm.
*   **Parameters**: Robustness, Read Noise, Motion Norm.

### `convert_to_rgba`
 packs Bayer data into RGBA pixels to allow 4-way SIMD operations during the heavy math of FFT/Merge.

## Metal Shaders involved
*   `forward_fft` / `forward_dft`
*   `backward_fft` / `backward_dft`
*   `merge_frequency_domain`
*   `calculate_mismatch_rgba`
*   `reduce_artifacts_tile_border`
