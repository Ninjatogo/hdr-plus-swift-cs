namespace BurstPhoto.Rendering;

/// <summary>
/// Single Source of Truth for all shader descriptor bindings.
/// These constants MUST match the bindings in the HLSL shader files.
///
/// Convention:
/// - Bindings 0-9: UniformBuffers (cbuffer in HLSL)
/// - Binding 10+: Storage/Sampled Images (Texture2D/RWTexture2D in HLSL)
/// </summary>
public static class ShaderBindings
{
    // ============================================================
    // UNIFORM BUFFERS (Constant Buffers)
    // ============================================================

    /// <summary>Binding 0: Parameter buffer used by most shaders</summary>
    public const int UniformBufferParams = 0;

    // ============================================================
    // SAMPLED IMAGES (Read-Only Textures) - Standard Layout
    // ============================================================

    /// <summary>Binding 1: Primary input texture (read-only)</summary>
    public const int SampledImageInput = 1;

    /// <summary>Binding 2: Secondary input texture (comparison/aligned image)</summary>
    public const int SampledImageComparison = 2;

    /// <summary>Binding 3: Alignment/motion vector texture or third input</summary>
    public const int SampledImageAlignment = 3;

    /// <summary>Binding 4: Auxiliary texture 0 (context-dependent)</summary>
    public const int SampledImageAux0 = 4;

    /// <summary>Binding 5: Auxiliary texture 1 (context-dependent)</summary>
    public const int SampledImageAux1 = 5;

    /// <summary>Binding 6: Auxiliary texture 2 (weight/mask texture)</summary>
    public const int SampledImageAux2 = 6;

    // ============================================================
    // STORAGE IMAGES (Read-Write Textures)
    // ============================================================

    /// <summary>Binding 10: Primary output texture (write-only or read-write)</summary>
    public const int StorageImageOutput = 10;

    /// <summary>Binding 11: Secondary output texture (for multi-output shaders)</summary>
    public const int StorageImageOutput2 = 11;

    /// <summary>Binding 12: Third output texture (RGBA output for conversion)</summary>
    public const int StorageImageOutput3 = 12;

    /// <summary>Binding 13: Weight accumulator output</summary>
    public const int StorageImageWeightAccum = 13;

    // ============================================================
    // STORAGE BUFFERS
    // ============================================================

    /// <summary>Binding 5: Mean texture buffer (for prepare pipeline)</summary>
    public const int StorageBufferMean = 5;

    /// <summary>Binding 6: Black levels buffer (for prepare pipeline)</summary>
    public const int StorageBufferBlackLevels = 6;

    // ============================================================
    // FREQUENCY DOMAIN PIPELINE SPECIFIC
    // Shader: MergeFrequency.hlsl
    // ============================================================

    /// <summary>
    /// Frequency domain shaders use this binding layout:
    /// - Binding 0: FrequencyParams (TileSize, NumTextures)
    /// - Binding 1: RefTexture (reference RGBA input)
    /// - Binding 2: AlignedTexture (comparison RGBA input)
    /// - Binding 3: AuxTexture0 (RMS texture)
    /// - Binding 4: AuxTexture1 (mismatch texture)
    /// - Binding 5: AuxTexture2 (highlights texture)
    /// - Binding 10: OutputTexture (frequency domain or spatial output)
    /// </summary>
    public static class FrequencyDomain
    {
        public const int Params = UniformBufferParams;
        public const int RefTexture = SampledImageInput;           // Binding 1
        public const int AlignedTexture = SampledImageComparison;  // Binding 2
        public const int RmsTexture = SampledImageAlignment;       // Binding 3 (AuxTexture0)
        public const int MismatchTexture = SampledImageAux0;       // Binding 4 (AuxTexture1)
        public const int HighlightsTexture = SampledImageAux1;     // Binding 5 (AuxTexture2)
        public const int OutputTexture = StorageImageOutput;       // Binding 10
    }

    // ============================================================
    // ALIGNMENT/WARP PIPELINE SPECIFIC
    // Shader: Align.hlsl
    // ============================================================

    /// <summary>
    /// Alignment shaders use:
    /// - Binding 0: AlignParams (TileSize, SearchDist, etc)
    /// - Binding 1: RefTexture/InTexture (reference image or input to warp)
    /// - Binding 2: CompTexture (comparison image to align)
    /// - Binding 3: PrevAlignment (previous level alignment vectors)
    /// - Binding 10: OutAlignment/OutTexture (output alignment vectors or warped result)
    ///
    /// Warp shaders use:
    /// - Binding 0: AlignParams
    /// - Binding 1: InTexture (image to warp)
    /// - Binding 2: CompTexture (unused, but bound for layout compatibility)
    /// - Binding 3: Alignment (motion vectors to apply)
    /// - Binding 10: OutTexture (warped result)
    /// </summary>
    public static class Alignment
    {
        public const int Params = UniformBufferParams;
        public const int RefTexture = SampledImageInput;           // Binding 1
        public const int InTexture = SampledImageInput;            // Binding 1 (alias for warp)
        public const int CompTexture = SampledImageComparison;     // Binding 2
        public const int AlignmentVectors = SampledImageAlignment; // Binding 3
        public const int Output = StorageImageOutput;              // Binding 10
    }

    // ============================================================
    // TEXTURE CONVERSION PIPELINE SPECIFIC
    // Shader: TextureOps.hlsl (convert_to_rgba, convert_to_bayer)
    // ============================================================

    /// <summary>
    /// Texture conversion shaders (Bayer ↔ RGBA):
    /// - Binding 0: TextureParams (CfaPattern, PadLeft, PadTop, etc)
    /// - Binding 1: InTextureFloat (Bayer input for convert_to_rgba)
    /// - Binding 3: InTextureRGBA (RGBA input for convert_to_bayer)
    /// - Binding 10: OutTextureFloat (Bayer output for convert_to_bayer)
    /// - Binding 12: OutTextureRGBA (RGBA output for convert_to_rgba)
    ///
    /// NOTE: These bindings are unusual - RGBA uses binding 3/12 not 1/10!
    /// This matches the HLSL vk::binding attributes in TextureOps.hlsl.
    /// </summary>
    public static class Conversion
    {
        public const int Params = UniformBufferParams;

        // For convert_to_rgba: Bayer(float) → RGBA
        public const int BayerInput = SampledImageInput;           // Binding 1 (InTextureFloat)
        public const int RgbaOutput = StorageImageOutput3;         // Binding 12 (OutTextureRGBA)

        // For convert_to_bayer: RGBA → Bayer(float)
        public const int RgbaInput = SampledImageAlignment;        // Binding 3 (InTextureRGBA)
        public const int BayerOutput = StorageImageOutput;         // Binding 10 (OutTextureFloat)

        // Dummy bindings (unused but required for layout compatibility)
        public const int UnusedSampled = SampledImageAlignment;    // Binding 3
        public const int UnusedStorage = StorageImageOutput;       // Binding 10
        public const int UnusedStorage2 = StorageImageOutput3;     // Binding 12
    }

    // ============================================================
    // PREPARE TEXTURE PIPELINE SPECIFIC
    // Shader: TextureOps.hlsl (prepare_texture_bayer)
    // ============================================================

    /// <summary>
    /// Prepare texture shaders:
    /// - Binding 0: TextureParams
    /// - Binding 1: Unused (t0 InTextureFloat)
    /// - Binding 2: InTextureUint (raw Bayer input)
    /// - Binding 3: Unused (t2 InTextureRGBA)
    /// - Binding 4: AuxTextureFloat (hot pixel weight)
    /// - Binding 5: MeanTextureBuffer (storage buffer)
    /// - Binding 6: BlackLevels (storage buffer)
    /// - Binding 10: OutTextureFloat (prepared output)
    /// - Binding 11: OutTextureUint (unused)
    /// - Binding 12: OutTextureRGBA (unused)
    /// </summary>
    public static class Prepare
    {
        public const int Params = UniformBufferParams;
        public const int UnusedFloat = SampledImageInput;          // Binding 1
        public const int InputUint = SampledImageComparison;       // Binding 2 (InTextureUint)
        public const int UnusedRgba = SampledImageAlignment;       // Binding 3
        public const int HotPixelWeight = SampledImageAux0;        // Binding 4 (AuxTextureFloat)
        public const int MeanBuffer = StorageBufferMean;           // Binding 5
        public const int BlackLevelsBuffer = StorageBufferBlackLevels; // Binding 6
        public const int OutputFloat = StorageImageOutput;         // Binding 10
        public const int UnusedOutputUint = StorageImageOutput2;   // Binding 11
        public const int UnusedOutputRgba = StorageImageOutput3;   // Binding 12
    }

    // ============================================================
    // EXPOSURE CORRECTION SPECIFIC
    // Shader: Exposure.hlsl
    // ============================================================

    /// <summary>
    /// Exposure correction shader:
    /// - Binding 0: ExposureParams (WhiteLevel, BlackLevel, etc)
    /// - Binding 1: InTexture (merged Bayer data, also used as RW)
    /// - Binding 2: InBlurred (blurred texture for luminance)
    /// - Binding 3: BlackLevelsMean (storage buffer)
    /// - Binding 4: MaxTextureBuffer (storage buffer)
    /// - Binding 10: OutTexture (exposure-corrected result, same as InTexture for RW)
    /// - Binding 11: OutBuffer (for reduction output)
    /// </summary>
    public static class Exposure
    {
        public const int Params = UniformBufferParams;
        public const int InputTexture = SampledImageInput;         // Binding 1 (InTexture)
        public const int BlurredTexture = SampledImageComparison;  // Binding 2 (InBlurred)
        public const int BlackLevelsBuffer = SampledImageAlignment;// Binding 3 (storage buffer)
        public const int MaxBuffer = SampledImageAux0;             // Binding 4 (storage buffer)
        public const int OutputTexture = StorageImageOutput;       // Binding 10
        public const int OutputBuffer = StorageImageOutput2;       // Binding 11
    }
}
