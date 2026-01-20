# Swift Application Settings Analysis

This document analyzes the settings exposed in the original Swift Burst Photo application and explains their underlying implementation and impact on the processing pipeline.

## 1. Exposure Control
Controls how the image exposure is corrected during post-processing.

**UI Options:**
- "Off"
- "Linear (full bit range)"
- "Linear (relative +1 EV)"
- "Non-linear (target ±0 EV)"
- "Non-linear (target +1 EV)"

**Internal Mapping (`denoise.swift`):**
- "Off" -> `"Off"`
- "Linear (full bit range)" -> `"LinearFullRange"`
- "Linear (relative +1 EV)" -> `"Linear1EV"`
- "Non-linear (target ±0 EV)" -> `"Curve0EV"`
- "Non-linear (target +1 EV)" -> `"Curve1EV"`

**Implementation Details (`exposure/exposure.swift`, `denoise.swift`):**
- **Non-Bayer Sensors:** If the sensor is non-Bayer (e.g., X-Trans) and Exposure Control is not "Off", it is forcibly set to "Off" with a user alert.
- **"Off"**:
    - If the exposure is uniform, internal white/black levels are set to -1, skipping specific normalization steps.
    - Skips the `correct_exposure` step entirely.
- **Linear Modes**:
    - Handled by `correct_exposure_linear_state` shader.
    - **LinearFullRange**: Sets a target factor of -1.0 (logic specific to shader, likely normalizes to full range).
    - **Linear1EV**: Sets a target factor of 2.0 (effectively +1 EV gain).
- **Non-linear (Curve) Modes**:
    - Handled by `correct_exposure_state` shader.
    - Applies a tone curve to lift shadows and protect highlights.
    - Uses a blurred version of the image as a local luminance map.
    - **Curve0EV**: Curve parameter 0.
    - **Curve1EV**: Curve parameter 100.

**Usage in .NET CLI:**
```bash
--exposure-control <Off|LinearFullRange|Linear1EV|Curve0EV|Curve1EV>
```
*Default: LinearFullRange*

## 2. Noise Reduction
Controls the strength of the denoising and merging process.

**UI Options:**
- Slider value: Integer from `1` to `23`.
- `1` (Left): "Small values increase motion robustness and image sharpness".
- `23` (Right): "Large values increase the strength of noise reduction" / "max. (simple average w/o alignment)".

**Implementation Details:**
- **Value 23 (Max)**:
    - Triggers a "Special Mode": `calculate_temporal_average`.
    - This performs simple temporal averaging of frames without alignment.
    - Useful for static scenes with very high noise where alignment might fail or introduce artifacts.
    - `hotpixel` correction strength is reduced (multiplied by 0.25).
- **Values 1-22**:
    - Used to calculate `robustness` parameters for the merging algorithms.
    - **Frequency Domain**:
        - `robustness_rev`: Derived from `(26.5 or 28.5) - noise_reduction`.
        - Affects `robustness_norm`, `read_noise`, and `max_motion_norm`.
        - Higher noise reduction value -> Lower `robustness_rev` -> Higher `robustness_norm` (more aggressive merging/denoising).
    - **Spatial Domain**:
        - `robustness_rev`: Derived from `36.0 - noise_reduction`.
        - Affects `robustness` weight in `robust_merge`.

**Usage in .NET CLI:**
```bash
--noise-reduction <1.0 - 23.0>
```
*Default: 13.0*

## 3. Tile Size
Controls the base tile size used for the alignment pyramid.

**UI Options:**
- "Small" -> 16
- "Medium" -> 32
- "Large" -> 64

**Implementation Details (`merge/*.swift`):**
- Sets the `tile_size` for the **initial/coarsest** level of the alignment pyramid.
- **Frequency Domain**: Code explicitly notes: "The tile size for merging in frequency domain is set to 8x8 for all tile sizes used for alignment." The user setting impacts the *alignment* phase tile sizes, but the actual *merging* happens at 8x8 blocks.
- **Impact**:
    - **Large (64)**: Larger tiles for alignment. More robust to large motion, but might miss fine local motion.
    - **Small (16)**: Smaller tiles. Better handling of complex local motion, but potentially less stable on flat areas or large displacements.

**Usage in .NET CLI:**
```bash
--tile-size <Small|Medium|Large>
```
*Default: Medium*

## 4. Search Distance
Controls the depth of the alignment pyramid (resolution levels).

**UI Options:**
- "Small" -> 128 (Internal Value)
- "Medium" -> 64 (Internal Value)
- "Large" -> 32 (Internal Value)

**Implementation Details:**
- The internal integer value represents the **Minimum Resolution** (in pixels) to stop the pyramid alignment.
- Pyramid construction loop: `while (res > search_distance) ...`
- **Small Setting (Val 128)**:
    - Loop stops earlier.
    - Alignment is performed only at coarser resolutions (down to ~128px dimension).
    - Faster, but potentially less precise alignment for fine details.
- **Large Setting (Val 32)**:
    - Loop continues longer.
    - Alignment is performed down to very fine resolutions (down to ~32px dimension).
    - Slower, but higher precision alignment.

**Usage in .NET CLI:**
```bash
--search-distance <Small|Medium|Large>
```
*Default: Medium*

*Note: The naming "Small/Large" in UI maps inversely to the internal "Minimum Resolution" value, but correctly corresponds to "Small/Large" search effort/precision.*

## 5. Merging Algorithm
Selects the core algorithm for merging frames.

**UI Options:**
- "Fast"
- "Higher quality"

**Implementation Details:**
- **Fast**:
    - Calls `align_merge_spatial_domain` (`merge/spatial.swift`).
    - Uses a spatial domain approach with binomial blur for noise estimation.
    - Uses `robust_merge` kernel.
- **Higher quality**:
    - Calls `align_merge_frequency_domain` (`merge/frequency.swift`).
    - Uses FFT/DFT (Fast/Discrete Fourier Transform) for merging.
    - Includes deconvolution (`deconvolute_frequency_domain`) to sharpen and reduce artifacts.
    - Generally computationally more expensive but produces cleaner results.
- **Fallback**:
    - If sensor is non-Bayer (e.g., X-Trans), "Higher quality" defaults back to "Fast" due to algorithm constraints (frequency domain algo currently only supports Bayer).

**Usage in .NET CLI:**
```bash
--algorithm <Fast|HigherQuality>
```
*Default: Fast*

## 6. Output Bit Depth
Controls the bit depth scaling of the output DNG.

**UI Options:**
- "Native"
- "Scale to 16 bit"

**Internal Mapping:**
- "Native" -> `"Native"`
- "Scale to 16 bit" -> `"16Bit"`

**Implementation Details (`denoise.swift`, `texture/texture.swift`):**
- **Native**:
    - Preserves the original white level range of the camera.
    - `factor_16bit` = 1.
- **Scale to 16 bit**:
    - Calculates a scaling factor to stretch the data to fill the 16-bit range (0-65535).
    - `factor_16bit` = `pow(2.0, 16.0 - ceil(log2(white_level)))`.
    - Example: If input is 12-bit (max ~4095), factor might be $2^{16-12} = 16$.
    - The output DNG `WhiteLevel` tag is updated to the new scaled value (up to 65535).
- **Constraints**:
    - Forces "Native" if non-Bayer sensor.
    - Forces "Native" if Exposure Control is "Off" (incompatible combination).

**Usage in .NET CLI:**
```bash
--bit-depth <Native|16Bit>
```
*Default: Native*
