### Technical Documentation: Generating Bayer RAW DNG Files via LibTiff.Net

#### 1\. Introduction and Architectural Suitability

**LibTiff.Net** (by Bit Miracle) is identified as the definitive solution for authoring valid, compliant **Digital Negative (DNG)** files within the .NET ecosystem 1\. While high-level libraries often perform destructive demosaicing on raw data, LibTiff.Net acts as a "File Structure Manager" 2, 3\. It allows for low-level TIFF structure fabrication, precise metadata injection, and the handling of single-plane **Color Filter Array (CFA)** data without unwanted interpretation 3, 4\.  
As a fully managed C\# port of the standard libtiff library, it simplifies deployment by removing dependencies on native binaries like libtiff.dll 3, 5\. Because DNG is an extension of the **TIFF 6.0** and **TIFF/EP** standards, LibTiff.Net provides the granular tag-level access required to define sensor characteristics 1, 6, 7\.

#### 2\. Library Configuration and Custom Tag Extension

A critical requirement for DNG generation is the ability to write private tags (e.g., CFAPattern, ColorMatrix1) that are not part of the standard TIFF 6.0 specification 3, 8, 9\. LibTiff.Net handles this through a **Tag Extender** mechanism 9, 10\.

* **Implementation Strategy:** Developers must define DNG-specific tags as constants and use a TiffExtendProc callback to register them with a TiffFieldInfo array 9, 11\.  
* **Essential Tags to Register:** DNGVersion (50706), UniqueCameraModel (50708), CFAPattern (50711), ColorMatrix1 (50721), and AsShotNeutral (50728) 12, 13\.

#### 3\. Structural Organization: The SubIFD Tree

The Adobe DNG specification recommends using **SubIFD trees** rather than chains 14, 15\. In this architecture:

1. **IFD0 (Primary Directory):** Typically contains a low-resolution thumbnail or preview to ensure compatibility with standard image browsers 16, 17\.  
2. **SubIFD:** Contains the high-resolution raw sensor data, referenced by the SubIFDs tag (0x014A) in IFD0 16, 18\.

**Procedural Step:** In LibTiff.Net, write the primary thumbnail IFD first, create an empty SubIFDs tag to reserve space, then use WriteDirectory to move to the next directory for the raw payload 19, 20\. The library automatically calculates the offsets and populates the SubIFDs tag upon closing the file 17, 19\.

#### 4\. Configuring Mandatory DNG Metadata

For a DNG to be valid in professional software like Adobe Lightroom, specific tags must be precisely configured 21, 22\.

* **Photometric Interpretation:** For Bayer data, Tag 262 **must** be set to **32803 (CFA)** 23, 24\. Because this value is not in the standard LibTiff.Net enum, it must be cast: tif.SetField(TiffTag.PHOTOMETRIC, (Photometric)32803) 11, 25\.  
* **CFA Pattern:** Define the mosaic block dimensions (usually 2x2) and the pattern itself (e.g., 1, 4 for RGGB) using tags 50710 and 50711 12, 26, 27\.  
* **NewSubFileType:** Use 1 for the preview/thumbnail in IFD0 and 0 for the primary raw image in the SubIFD 24, 28, 29\.  
* **Colorimetry:** Supply ColorMatrix1 (a 3x3 matrix mapping native RGB to CIE XYZ) and AnalogBalance to ensure the raw converter can transform sensor data into accurate visible colors 26, 30, 31\.

#### 5\. Handling Raw Pixel Data

LibTiff.Net provides high-performance access to the underlying bitstream, supporting streaming row-by-row via WriteScanline or in chunks via WriteEncodedStrip 32, 33\.

* **Organization:** Strips are traditional; however, for sensors exceeding 40MP, **tiled writing** is preferred for memory efficiency and potential parallelization 32, 34, 35\.  
* **Bit Depth Unpacking:** LibTiff.Net expects byte-aligned samples (8, 16, or 32 bits) 36\. If a sensor outputs **12-bit or 14-bit packed data**, the developer must manually unpack this into 16-bit unsigned short arrays before writing 36, 37\.  
* **CFA Alignment:** Ensure RowsPerStrip (or TileLength) is an even number to avoid splitting the 2x2 Bayer pattern, which prevents de-Bayering artifacts 34, 38\.

#### 6\. Validation and Quality Assurance

LibTiff.Net enforces TIFF structural validity but not DNG semantic validity 39\. A rigorous validation workflow is essential:

* **Command Line Validation:** Use the **Adobe DNG Converter** or dng\_validate.exe from the DNG SDK to verify tag correctness and checksums 39-41.  
* **Visual Checks:** Open the file in a raw viewer like **RawTherapee** 39, 42\.  
* A **pink cast** often indicates incorrect BlackLevel or WhiteLevel settings 39\.  
* A **green/magenta grid** implies an incorrect CFAPattern definition 39\.  
* **Programmatic Validation:** Use Sdcb.LibRaw as a decoder to test if the authored DNG can be successfully unpacked; failure here indicates an invalid DNG structure 43, 44\.

**Analogy for Understanding:** Think of a DNG file as a **shipping container** where LibTiff.Net is the **crane** that loads the cargo. Standard libraries try to "open and decorate" the cargo (demosaicing) before it's even shipped. LibTiff.Net simply places the raw goods into the container and attaches a **highly specific manifest** (metadata tags) that tells the recipient exactly how to unpack and display the items once they arrive 3, 10, 45\.  
