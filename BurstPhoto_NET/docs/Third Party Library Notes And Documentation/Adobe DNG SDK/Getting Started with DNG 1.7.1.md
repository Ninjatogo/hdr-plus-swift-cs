Getting Started with Digital Negative (DNG) 1.7.1  

This package includes the DNG 1.7.1 specification, software development kit (SDK), and sample photos. This document summarizes the changes and new features in version 1.7.1. 

What’s New?  

JPEG XL  

Images can now be compressed with JPEG XL (ISO/IEC 18181-1:2022) to reduce size. Summary: 

• Permitted for monochrome and 3-color images with integer or floating-point pixels.  • Supports output-referred and scene-referred data.  

• Supports lossless and lossy compression. 

• May be used with RowInterleaveFactor and ColumnInterleaveFactor (see next item) to  compress Color Filter Array images. 

ColumnInterleaveFactor  

This tag is similar to the existing RowInterleaveFactor tag, but for pixel data interleaved across columns  instead of rows. It can be combined with RowInterleaveFactor to partition an image into M x N sub images. For example, a Bayer raw image can be arranged as four sub-images (each ½ width and ½ height of a single color) using RowInterleaveFactor and ColumnInterleaveFactor both set to 2\. Each single-color  sub-image can then be compressed, for example, using JPEG XL. 

ColorimetricReference  

The ColorimetricReference has a new option to indicate output-referred high dynamic range images. 

ProfileDynamicRange  

This tag indicates whether a given camera profile is intended for Standard Dynamic Range (SDR) or High  Dynamic Range (HDR) rendering. HDR camera profiles are used for overrange output and affect the  math used to apply tone curves and lookup tables. 

ProfileGainTableMap2  

This is an extended version of ProfileGainTableMap (introduced in DNG 1.6) with extra options intended  to improve quality and reduce storage size. ProfileGainTableMap2 can also be stored separately for each  embedded camera profile; this makes it possible to do profile-specific tone mapping. 

ImageSequenceInfo  

An informative tag for grouping a sequence of related images (e.g., focus bracketing). 

ImageStats  

An informative tag for describing basic statistics of the main image (e.g., weighted average, histogram).  
RGBTables in Camera Profiles  

The RGBTables tag (introduced in DNG 1.6) is now permitted in embedded camera profiles; this makes it  possible to do profile-specific masked color adjustments. 

ProfileGroupName  

A camera profile tag to group related camera profiles. The intent is to relate SDR and HDR versions of a  given camera profile. 

Version Guidance  

The **DNGVersion** tag should be set to 1.7 or later if at least one of the following applies: 

• main image is compressed with JPEG XL  

• any of the new tags (see preceding section) is used 

• RGBTables is used in a camera profile IFD 

• ColorimetricInterpretation value is set to 2 (output-referred HDR) 

The **DNGVersion** tag should be set to 1.7.1 or later if the following applies: 

• ColumnInterleaveFactor is specified with a value greater than 1 

The **DNGBackwardVersion** tag should be set to 1.7 or later if the following applies: • the main image is compressed with JPEG XL 

The **DNGBackwardVersion** tag should be set to 1.7.1 or later if the following applies: • ColumnInterleaveFactor is specified with a value greater than 1 

Changes to the SDK  

The DNG Software Development Kit (SDK) has been updated to support the new tags listed above. The command-line tool dng\_validate has the following additional options: 

\-32 Render output to 32-bit floating-point image 

\-csP3 Render output to Display P3 color space 

\-cs2020 Render output to Rec. 2020 / BT.2100 color space 

\-profile \<name\> Render the image using the named camera profile 

\-losslessJXL Compress image data using lossless JPEG XL 

\-lossyMosaicJXL Compress 2 by 2 mosaic image data using lossy JPEG XL 

   
Sample Photos  

This package includes sample files intended to demonstrate usage of new features.

| File Name  | Notes |
| :---- | ----- |
| 01\_jxl\_linear\_raw\_integer.dng  | Purpose is to demonstrate high bit-depth integer images  compressed with JPEG XL.  • 16-bit integer scene-referred linear RGB raw image  compressed with JPEG XL.   • Main image is tiled (126 tiles, each 688 x 704). • DNGVersion and DNGBackwardVersion both set to 1.7. • Includes uncompressed thumbnail. |
| 02\_jxl\_linear\_raw\_float.dng  | Purpose is to demonstrate floating-point (e.g., HDR) images  compressed with JPEG XL.  • 16-bit floating-point scene-referred linear RGB raw  image compressed with JPEG XL.  • Main image is tiled (63 tiles, each 672 x 784).  • DNGVersion and DNGBackwardVersion both set to 1.7.  • Includes uncompressed thumbnail. |
| 03\_jxl\_bayer\_raw\_integer.dng  | Purpose is to demonstrate use of RowInterleaveFactor and  ColumnInterleaveFactor for compressing Color Filter Array  images with JPEG XL  • 16-bit **integer** scene-referred Bayer CFA raw image  compressed with JPEG XL.  • RowInterleaveFactor and **ColumnInterleaveFactor** both set to 2\.  • Main image is tiled (40 tiles, each 1280 x 1440). • DNGVersion and DNGBackwardVersion both set to  1.7.1.   • Includes JPEG compressed thumbnail. |
| 04\_PGTM2\_per\_profile.dng  | Purpose is to demonstrate **ProfileGainTableMap2** and its  usage per-camera profile.  • Main image is a simple uniform gray square.  • Contains two embedded camera profiles, one in IFD 0  and the other in an ExtraCameraProfiles IFD.  • Contains two ProfileGainTableMap2 tags, one in each  profile.  • The first PGTM2 is in IFD 0, whose profile is named  “PGTM Example (Top-Left Bright)”. It makes the top left corner of the image brighter.  • The second PGTM2 is in the camera profile named  “PGTM Example (Top-Right Bright)”. It makes the top right corner of the image brighter.  Use the following dng\_validate commands:  • dng\_validate \-tif out1 \-profile “PGTM Example (Top Left Bright)” 04\_PGTM2\_per\_profile.dng |

|  | • dng\_validate \-tif out2 \-profile “PGTM Example (Top Right Bright)” 04\_PGTM2\_per\_profile.dng |
| :---- | ----- |
| 05\_PGTM2\_unsigned8.dng  06\_PGTM2\_unsigned16.dng  07\_PGTM2\_float16.dng  08\_PGTM2\_float32.dng | Purpose is to demonstrate ProfileGainTableMap2 with  different data types for storage.  • Main image is a simple uniform gray square.  • The PGTM makes the center of the image brighter. |
| 09\_ImageSequenceInfo\_1\_of\_3.dng 10\_ImageSequenceInfo\_2\_of\_3.dng 11\_ImageSequenceInfo\_3\_of\_3.dng | Purpose is to demonstrate ImageSequenceInfo tag with a 3- image sequence representing a focus bracket. Use  dng\_validate \-v on these files and search for   “ImageSequenceInfo” in the output. |
| 12\_ImageStats\_WeightedAverage.dng 13\_ImageStats\_Several.dng | Purpose is to demonstrate ImageStats tag.  • Image 12 contains only a single field: the weighted  average. It does not provide any information about the  weighted used.  • Image 13 is the same image but contains extra  ImageStats fields: the weights used, and color values  for the 1%, 5%, 10%, 50% (median), 90%, 95%, and  99% percentiles.  Use dng\_validate \-v on these files and search for “ImageStats”  in the output. |
| 14\_hdr\_sdr\_profiles.dng  | Purpose is to demonstrate the use of ProfileDynamicRange and  HDR camera profiles.  • Main image is a linear scene-referred grayscale “step  wedge” where each gray square is ½ the value of the  square to its left (i.e., one f-stop decrements).  • The brightest pixel in the stored image (leftmost gray  square) has a pixel value of 1.0.  • BaselineExposure is set to \+2.  • Contains 4 camera profiles: named SDR, HDR Linear,  HDR Tone Map, and HDR Tint.  • The main camera profile is SDR and is placed in IFD 0\. • The SDR profile does not contain a   ProfileDynamicRange tag. It is therefore assumed to be  Standard Dynamic Range. It has a   ProfileGainTableMap2 tag, but all entries are set to a  gain of 1 (i.e., no change). It has a ProfileToneCurve tag  which tone maps the step wedge to SDR.  • The HDR Linear profile contains a ProfileDynamicRange  tag with DynamicRange set to 1, indicating HDR. It has  a ProfileToneCurve tag with an identity curve. This  profile does no tone mapping, so the rendered output  has linear values from \+4 (brightest square) to   0.0078125 (darkest square).  • The HDR Tone Map profile contains a   ProfileDynamicRange tag with DynamicRange set to 1,  indicating HDR. It has a ProfileToneCurve tag that maps  |

|  | a linear value of 4.0 to 2.0, maps 2.0 to 1.0, and maps  1.0 to 0.8. The rendered output of the three leftmost  squares has values 2.0, 1.0, and 0.8, respectively.  • The HDR Tint profile contains a ProfileDynamicRange  tag with DynamicRange set to 1, indicating HDR. It has  a RGBTables tag that tints the overrange (\> 1\) values  blue and tint the SDR (\< 1\) values red. It makes no  change to the SDR white level (1.0).  Use the following dng\_validate commands to render: • dng\_validate \-tif out\_sdr \-profile “SDR” INPUT • dng\_validate \-32 \-tif out\_hdr1 \-profile “HDR Linear”  INPUT  • dng\_validate \-32 \-tif out\_hdr2 \-profile “HDR Tone  Map” INPUT  • dng\_validate \-32 \-tif out\_hdr3 \-profile “HDR Tint”  INPUT |
| :---- | :---- |

