# DNG Writer Debugging Summary

## Objective
Implement DNG writing functionality using `BitMiracle.LibTiff.Net` to replace the previous `SimpleRawWriter`.

## Current Status
**RESOLVED.** DNG files are now being written successfully. The WhiteLevel tag has been disabled as a workaround for a LibTiff.Net bug.

## Issues Encountered and Resolution

### 1. WhiteLevel Tag - OverflowException (RESOLVED)
*   **Problem:** LibTiff.Net's internal definition of WhiteLevel (tag 50717) causes `System.OverflowException` during `WriteDirectory()` in `writeLongArray()`.
*   **Root Cause:** LibTiff.Net uses signed `Int32` internally for LONG arrays. Even valid white level values (e.g., 64448) trigger overflow during the checked arithmetic.
*   **Attempts Made:**
    *   Custom TiffFieldInfo with LONG type + int[] array → Overflow
    *   Custom TiffFieldInfo with SHORT type + short[] or ushort[] array → Internal definition takes precedence
    *   Using internal definition directly → Overflow or NullReferenceException
*   **Resolution:** WhiteLevel tag is commented out. DNG readers typically use default max value (65535 for 16-bit) or infer from bit depth.

### 2. AsShotNeutral Warnings (Minor)
*   **Problem:** LibTiff.Net warnings about negative RATIONAL values when ColorFactors contain very small values.
*   **Resolution:** Values are now clamped to valid positive range (0.001 - 100.0) before writing. Minor warnings may still appear for certain images but don't block functionality.

## Current Implementation
- DNG files are written in ~2.9 seconds for 3 burst images
- Output includes: DNG version, CFA pattern, ColorMatrix1, AsShotNeutral, BlackLevel
- Missing: WhiteLevel (workaround above)

## Future Improvements
1. File an issue with BitMiracle/LibTiff.Net regarding WhiteLevel overflow
2. Consider using a different library (e.g., direct TIFF writing) for DNG tags
3. Investigate raw WhiteLevel bytes writing as direct IFD entry
