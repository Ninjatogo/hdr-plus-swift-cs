# Processing Details: IO and Preparation

## Overview

Beyond alignment and merging, the application performs critical steps to load RAW data, prepare it for processing (linearization, black level subtraction), and finalize the output (exposure correction, saving).

## Image Loading (`io_dng`)

The application supports RAW files via a custom wrapper around the **Adobe DNG SDK** and **LibRaw**.

1.  **DNG Conversion**: If inputs are not DNG, Adobe DNG Converter is invoked externally to create DNGs.
2.  **Reading Metadata**: The `io_dng` module extracts:
    *   Image dimensions.
    *   CFA Pattern (Bayer vs. X-Trans).
    *   Black Level and White Level.
    *   Exposure Time and ISO (for reference selection).
    *   Color Matrix (for basic color interpretation, though processing is mostly in RAW space).
3.  **Loading to Texture**: Raw pixel data is loaded directly into Metal textures.

## Texture Preparation

Before alignment or merging, textures often undergo preparation via `prepare_texture` (in `burstphoto/texture/texture.swift`).

### Steps
1.  **Hot Pixel Correction**: Weights for hot pixels are calculated (`find_hotpixels`) and applied to suppress defective pixels.
2.  **Black Level Subtraction**: The black level is subtracted to linearize the pixel data.
3.  **Exposure Equalization**: If processing a bracketed burst, images are digitally gained (multiplied) to match the exposure level of the reference frame.
4.  **Padding**: Images are often padded (mirror or zero) to accommodate alignment search windows and tile sizes.

## Exposure Correction

After merging, the 32-bit floating point result is linear but needs final adjustments before saving.

### `correct_exposure`
Located in `burstphoto/exposure/exposure.swift`, this function handles:
1.  **White Level Scaling**: Ensures highlights are correctly mapped.
2.  **Tone Mapping**: Can apply linear or curve-based adjustments (`LinearFullRange`, `Curve0EV`, etc.).
3.  **Black Level Addition**: Adds the black level back if required for the output format (though usually DNGs store black level metadata rather than baking it in).

## Data Flow Diagram

```mermaid
graph LR
    Disk[Disk (.dng)] --> DNG_SDK[DNG SDK Wrapper]
    DNG_SDK --> RAM[Raw Pixel Buffer]
    RAM --> Metal[Metal Texture]

    Metal --> Prep[Prepare Texture]
    Prep --> HotPixel[Hot Pixel Correction]
    HotPixel --> Linear[Black Level Subtraction]
    Linear --> Gain[Exposure Equalization]

    Gain --> Processing[Alignment & Merging]

    Processing --> Post[Post-Processing]
    Post --> ToneMap[Tone Mapping]
    ToneMap --> Scale[Scale to 16-bit]
    Scale --> Save[Save to DNG]
```

## Helper Functions

*   `find_hotpixels`: Detects pixels that are statistically likely to be stuck/hot based on neighbors.
*   `convert_float_to_uint16`: Converts the internal float pipeline back to 16-bit unsigned integers for DNG storage.
*   `fill_with_zeros`: Clears textures.
