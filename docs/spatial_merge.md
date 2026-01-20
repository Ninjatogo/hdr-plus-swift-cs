# Spatial Domain Merging (Fast)

## Overview

The spatial domain merging algorithm (`burstphoto/merge/spatial.swift`) is the "Fast" merging mode in Burst Photo. It performs alignment and merging in the spatial domain, using a robust weighted averaging scheme to reject ghosting artifacts caused by motion or misalignment.

## Algorithm Pipeline

The function `align_merge_spatial_domain` orchestrates the process.

1.  **Preparation**:
    *   Determines pyramid parameters (downscale factors, tile sizes).
    *   Prepares the reference texture (hot pixel correction, padding).
    *   Builds the reference pyramid (reused for aligning all other frames).
    *   Estimates noise characteristics from the reference image (`estimate_color_noise`).

2.  **Iterative Merging**:
    *   The reference image is added to the accumulator (`final_texture`) first.
    *   For each comparison image in the burst:
        1.  **Align**: Calls `align_texture` (see [Alignment](alignment.md)) to warp the comparison image to the reference.
        2.  **Robust Merge**: Calls `robust_merge` to compute a weight map and merge the aligned image.
        3.  **Accumulate**: Adds the weighted result to `final_texture`.

## Robust Merge Logic

The `robust_merge` function determines how much each pixel from the aligned comparison image should contribute to the final result.

$$ Weight = \frac{D^2}{D^2 + \sigma^2} $$
*(Simplified conceptual formula, actual implementation varies)*

1.  **Blur**: The aligned comparison texture is blurred.
2.  **Color Difference**: Computes the difference between the blurred reference and blurred comparison textures (`color_difference`).
    *   Blurring helps compare structural differences rather than high-frequency noise.
3.  **Weight Calculation**: `compute_merge_weight` shader calculates a per-pixel weight.
    *   Uses the color difference.
    *   Uses the estimated noise standard deviation (`noise_sd`).
    *   Uses a `robustness` parameter (derived from user settings) to tune sensitivity.
4.  **Upsample Weights**: The weight map (often lower resolution) is bilinear upsampled to the full image size.
5.  **Weighted Add**: The aligned texture is added to the accumulator, scaled by the weight map.

## Logic Flow Diagram

```mermaid
graph TD
    Ref[Reference Texture] --> RefPyramid[Build Ref Pyramid]
    Ref --> RefBlur[Blur Reference]
    RefBlur --> NoiseEst[Estimate Noise SD]

    Ref --> Accum[Accumulator (Final Texture)]

    subgraph For_Each_Comp_Image
        Comp[Comparison Texture] --> Align[Align Texture]
        RefPyramid --> Align

        Align --> AlignedComp[Aligned Texture]
        AlignedComp --> CompBlur[Blur Comp]

        RefBlur --> Diff[Color Difference]
        CompBlur --> Diff

        Diff --> WeightCalc[Compute Merge Weight]
        NoiseEst --> WeightCalc

        WeightCalc --> Upsample[Upsample Weight]
        AlignedComp --> WeightedAdd[Weighted Add]
        Upsample --> WeightedAdd
        WeightedAdd --> Accum
    end
```

## Key Functions

### `color_difference`
Calculates the sum of absolute differences between color channels for super-pixels.
*   Handles Bayer pattern (R, G, G, B) differences.

### `robust_merge`
Encapsulates the weighting and merging steps.
*   **Inputs**: Reference, Reference (Blurred), Aligned Comparison, Noise Stats.
*   **Outputs**: Merged contribution (to be added to accumulator).

## Metal Shaders involved
*   `color_difference`: Computes pixel-wise difference.
*   `compute_merge_weight`: Calculates the robust weight based on difference and noise model.
*   `add_texture_weighted`: Accumulates the result.
