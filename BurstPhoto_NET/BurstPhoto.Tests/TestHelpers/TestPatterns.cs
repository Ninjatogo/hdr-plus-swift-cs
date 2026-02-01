namespace BurstPhoto.Tests.TestHelpers;

/// <summary>
/// Synthetic test patterns for GPU pipeline testing.
/// Each pattern has known mathematical properties useful for validating pipeline operations.
/// </summary>
public enum TestPattern
{
    /// <summary>All pixels set to a single value. Tests bias/offset bugs.</summary>
    Solid,

    /// <summary>Linear gradient from 0 to max along X axis. Tests interpolation.</summary>
    HorizontalGradient,

    /// <summary>Linear gradient from 0 to max along Y axis. Tests interpolation.</summary>
    VerticalGradient,

    /// <summary>Alternating black/white pixels. Tests high-frequency handling, FFT.</summary>
    Checkerboard,

    /// <summary>Single bright pixel at center. Tests point spread function.</summary>
    Impulse,

    /// <summary>Synthetic RGGB Bayer pattern with known channel values.</summary>
    SyntheticBayer,

    /// <summary>Random values with fixed seed. For statistical tests.</summary>
    WhiteNoise,

    /// <summary>Sine wave pattern along X axis. Tests frequency response.</summary>
    SineWave,

    /// <summary>Concentric circles. Tests radial symmetry preservation.</summary>
    ConcentricCircles
}

/// <summary>
/// Generates test pattern data arrays for various GPU texture tests.
/// </summary>
public static class TestPatternGenerator
{
    /// <summary>
    /// Generates a single-channel float array for the specified test pattern.
    /// </summary>
    /// <param name="width">Texture width in pixels</param>
    /// <param name="height">Texture height in pixels</param>
    /// <param name="pattern">The test pattern to generate</param>
    /// <param name="minValue">Minimum value in the pattern (default 0)</param>
    /// <param name="maxValue">Maximum value in the pattern (default 65535)</param>
    /// <param name="seed">Random seed for WhiteNoise pattern (default 42)</param>
    /// <returns>Float array with width*height elements</returns>
    public static float[] GenerateSingleChannel(
        int width,
        int height,
        TestPattern pattern,
        float minValue = 0f,
        float maxValue = 65535f,
        int seed = 42)
    {
        var data = new float[width * height];
        var range = maxValue - minValue;
        var midValue = (minValue + maxValue) / 2;

        switch (pattern)
        {
            case TestPattern.Solid:
                Array.Fill(data, midValue);
                break;

            case TestPattern.HorizontalGradient:
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    data[y * width + x] = minValue + range * x / Math.Max(1, width - 1);
                break;

            case TestPattern.VerticalGradient:
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    data[y * width + x] = minValue + range * y / Math.Max(1, height - 1);
                break;

            case TestPattern.Checkerboard:
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    data[y * width + x] = ((x + y) % 2 == 0) ? minValue : maxValue;
                break;

            case TestPattern.Impulse:
                Array.Fill(data, minValue);
                data[(height / 2) * width + (width / 2)] = maxValue;
                break;

            case TestPattern.WhiteNoise:
                var rng = new Random(seed);
                for (var i = 0; i < data.Length; i++)
                    data[i] = minValue + (float)rng.NextDouble() * range;
                break;

            case TestPattern.SineWave:
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var phase = 2 * Math.PI * x / 32.0; // 32-pixel period
                    data[y * width + x] = minValue + range * (0.5f + 0.5f * (float)Math.Sin(phase));
                }
                break;

            case TestPattern.ConcentricCircles:
                var cx = width / 2.0;
                var cy = height / 2.0;
                var maxRadius = Math.Min(cx, cy);
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var dx = x - cx;
                    var dy = y - cy;
                    var r = Math.Sqrt(dx * dx + dy * dy);
                    var phase = 2 * Math.PI * r / 16.0; // 16-pixel ring spacing
                    data[y * width + x] = minValue + range * (0.5f + 0.5f * (float)Math.Sin(phase));
                }
                break;

            case TestPattern.SyntheticBayer:
                // Generate RGGB pattern: R at (0,0), G at (1,0) and (0,1), B at (1,1)
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var isEvenX = (x % 2) == 0;
                    var isEvenY = (y % 2) == 0;
                    if (isEvenX && isEvenY)
                        data[y * width + x] = minValue + range * 0.25f; // R
                    else if (!isEvenX && isEvenY)
                        data[y * width + x] = minValue + range * 0.50f; // G1
                    else if (isEvenX && !isEvenY)
                        data[y * width + x] = minValue + range * 0.55f; // G2
                    else
                        data[y * width + x] = minValue + range * 0.75f; // B
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(pattern), pattern, "Unknown test pattern");
        }

        return data;
    }

    /// <summary>
    /// Generates a 4-channel RGBA float array for the specified test pattern.
    /// Each pixel has 4 floats (R, G, B, A) stored contiguously.
    /// </summary>
    /// <param name="width">Texture width in pixels</param>
    /// <param name="height">Texture height in pixels</param>
    /// <param name="pattern">The test pattern to generate</param>
    /// <param name="minValue">Minimum value in the pattern (default 0)</param>
    /// <param name="maxValue">Maximum value in the pattern (default 65535)</param>
    /// <param name="seed">Random seed for WhiteNoise pattern (default 42)</param>
    /// <returns>Float array with width*height*4 elements</returns>
    public static float[] GenerateRgba(
        int width,
        int height,
        TestPattern pattern,
        float minValue = 0f,
        float maxValue = 65535f,
        int seed = 42)
    {
        var singleChannel = GenerateSingleChannel(width, height, pattern, minValue, maxValue, seed);
        var rgbaData = new float[width * height * 4];

        for (var i = 0; i < width * height; i++)
        {
            // For most patterns, use the same value in all 4 channels
            rgbaData[i * 4 + 0] = singleChannel[i]; // R
            rgbaData[i * 4 + 1] = singleChannel[i]; // G
            rgbaData[i * 4 + 2] = singleChannel[i]; // B
            rgbaData[i * 4 + 3] = singleChannel[i]; // A
        }

        // For WhiteNoise, generate independent random values per channel
        if (pattern == TestPattern.WhiteNoise)
        {
            var rng = new Random(seed);
            var range = maxValue - minValue;
            for (var i = 0; i < rgbaData.Length; i++)
                rgbaData[i] = minValue + (float)rng.NextDouble() * range;
        }

        return rgbaData;
    }

    /// <summary>
    /// Generates a synthetic Bayer RGGB texture with specific channel values.
    /// Useful for testing demosaicing and conversion accuracy.
    /// </summary>
    /// <param name="width">Texture width (should be even)</param>
    /// <param name="height">Texture height (should be even)</param>
    /// <param name="redValue">Value for red pixels</param>
    /// <param name="green1Value">Value for green pixels at (odd-x, even-y)</param>
    /// <param name="green2Value">Value for green pixels at (even-x, odd-y)</param>
    /// <param name="blueValue">Value for blue pixels</param>
    /// <returns>Float array with width*height elements in RGGB Bayer pattern</returns>
    public static float[] GenerateSyntheticBayer(
        int width,
        int height,
        float redValue,
        float green1Value,
        float green2Value,
        float blueValue)
    {
        var data = new float[width * height];

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var isEvenX = (x % 2) == 0;
            var isEvenY = (y % 2) == 0;

            // RGGB pattern: R at (0,0), G1 at (1,0), G2 at (0,1), B at (1,1)
            if (isEvenX && isEvenY)
                data[y * width + x] = redValue;
            else if (!isEvenX && isEvenY)
                data[y * width + x] = green1Value;
            else if (isEvenX && !isEvenY)
                data[y * width + x] = green2Value;
            else
                data[y * width + x] = blueValue;
        }

        return data;
    }

    /// <summary>
    /// Creates a shifted copy of the input data for alignment testing.
    /// Pixels are shifted by (dx, dy) with zero-fill for out-of-bounds areas.
    /// </summary>
    /// <param name="source">Source data array</param>
    /// <param name="width">Texture width</param>
    /// <param name="height">Texture height</param>
    /// <param name="dx">Horizontal shift (positive = right)</param>
    /// <param name="dy">Vertical shift (positive = down)</param>
    /// <returns>Shifted copy of the source data</returns>
    public static float[] CreateShiftedCopy(float[] source, int width, int height, int dx, int dy)
    {
        var result = new float[source.Length];

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var srcX = x - dx;
            var srcY = y - dy;

            if (srcX >= 0 && srcX < width && srcY >= 0 && srcY < height)
                result[y * width + x] = source[srcY * width + srcX];
            else
                result[y * width + x] = 0; // Zero-fill out-of-bounds
        }

        return result;
    }

    /// <summary>
    /// Creates a shifted copy of RGBA data for alignment testing.
    /// </summary>
    public static float[] CreateShiftedCopyRgba(float[] source, int width, int height, int dx, int dy)
    {
        var result = new float[source.Length];

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var srcX = x - dx;
            var srcY = y - dy;
            var dstIdx = (y * width + x) * 4;

            if (srcX >= 0 && srcX < width && srcY >= 0 && srcY < height)
            {
                var srcIdx = (srcY * width + srcX) * 4;
                result[dstIdx + 0] = source[srcIdx + 0];
                result[dstIdx + 1] = source[srcIdx + 1];
                result[dstIdx + 2] = source[srcIdx + 2];
                result[dstIdx + 3] = source[srcIdx + 3];
            }
            else
            {
                result[dstIdx + 0] = 0;
                result[dstIdx + 1] = 0;
                result[dstIdx + 2] = 0;
                result[dstIdx + 3] = 0;
            }
        }

        return result;
    }
}
