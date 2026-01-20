Based on the LibTiff.Net documentation you've provided, here are answers to your questions:

## 1. WhiteLevel Tag (50717) - OverflowException

**Correct TiffType and TiffFieldInfo configuration:**

```csharp
new TiffFieldInfo(
    (TiffTag)50717,           // WhiteLevel tag
    -1,                        // read_count: variable length
    -1,                        // write_count: variable length  
    TiffType.LONG,            // DNG spec allows LONG or SHORT
    FieldBit.Custom,
    true,                      // passCount = true for variable length
    false,                     // readCount
    "WhiteLevel"
)
```

**Key points:**
- For tags that can have variable counts, use `-1` for both read_count and write_count
- Set `passCount = true` when using variable counts - this tells LibTiff.Net to expect a count parameter in SetField/GetField
- Use `TiffType.LONG` for the primary type (you can also register a second TiffFieldInfo with `TiffType.SHORT` if needed)

**Special considerations for tags above 50000:**
- No special issues - private/custom tags work the same way as standard tags
- The OverflowException likely stems from incorrect read_count/write_count values, not the tag ID itself

## 2. BlackLevel Tag (50714) - Partial Write

**Correct way to write multi-value RATIONAL arrays:**

```csharp
// For BlackLevel with 4 rational values:
float[] blackLevels = new float[4] { 512.0f, 512.0f, 512.0f, 512.0f };
image.SetField((TiffTag)50714, 4, blackLevels);
```

**Correct TiffFieldInfo for BlackLevel:**

```csharp
new TiffFieldInfo(
    (TiffTag)50714,           // BlackLevel
    -1,                        // read_count: variable
    -1,                        // write_count: variable
    TiffType.RATIONAL,         // or TiffType.LONG depending on DNG version
    FieldBit.Custom,
    true,                      // passCount = true
    false,
    "BlackLevel"
)
```

**Usage pattern from documentation:**
Looking at the custom tags example in document #1, the pattern is:

```csharp
float[] rationals = { 0.333333f, 0.444444f };
image.SetField(TIFFTAG_RATIONALTAG, 2, rationals);
```

So for BlackLevel:
```csharp
image.SetField((TiffTag)50714, 4, blackLevels);
```

**BlackLevelRepeatDim consideration:**
Yes, you should set BlackLevelRepeatDim (50713) first. From DNG spec patterns, this tells the reader how to interpret the BlackLevel array dimensions.

## 3. General DNG Tag Registration

**SetTagExtender approach:**

Yes, `SetTagExtender` is the **recommended and proper method** for registering custom tags. From document #1:

```csharp
private static Tiff.TiffExtendProc m_parentExtender;

public static void TagExtender(Tiff tif)
{
    TiffFieldInfo[] dngFieldInfo = 
    {
        new TiffFieldInfo((TiffTag)50717, -1, -1, TiffType.LONG, 
            FieldBit.Custom, true, false, "WhiteLevel"),
        new TiffFieldInfo((TiffTag)50714, -1, -1, TiffType.RATIONAL, 
            FieldBit.Custom, true, false, "BlackLevel"),
        // ... other DNG tags
    };

    tif.MergeFieldInfo(dngFieldInfo, dngFieldInfo.Length);

    if (m_parentExtender != null)
        m_parentExtender(tif);
}

// Register once at startup:
Tiff.TiffExtendProc extender = TagExtender;
m_parentExtender = Tiff.SetTagExtender(extender);
```

**Constraints on read_count/write_count for variable-length tags:**

From the documentation:
- Set both to `-1` for variable-length tags
- When using `-1`, you **must** set `passCount = true` in the TiffFieldInfo constructor
- The actual count is passed as the first parameter in `SetField()` calls
- Use `TiffFieldInfo.Variable2` constant instead of hardcoded `-1` for clarity

**Example DNG implementation pattern:**

```csharp
using BitMiracle.LibTiff.Classic;

public class DngWriter
{
    private static Tiff.TiffExtendProc m_parentExtender;

    public static void RegisterDngTags()
    {
        Tiff.TiffExtendProc extender = DngTagExtender;
        m_parentExtender = Tiff.SetTagExtender(extender);
    }

    private static void DngTagExtender(Tiff tif)
    {
        TiffFieldInfo[] dngTags = 
        {
            // Variable count tags
            new TiffFieldInfo((TiffTag)50717, -1, -1, TiffType.LONG, 
                FieldBit.Custom, true, false, "WhiteLevel"),
            new TiffFieldInfo((TiffTag)50714, -1, -1, TiffType.RATIONAL, 
                FieldBit.Custom, true, false, "BlackLevel"),
            
            // Fixed count tags
            new TiffFieldInfo((TiffTag)50713, 2, 2, TiffType.SHORT, 
                FieldBit.Custom, false, true, "BlackLevelRepeatDim"),
            new TiffFieldInfo((TiffTag)50706, 1, 1, TiffType.BYTE, 
                FieldBit.Custom, false, false, "DNGVersion"),
        };

        tif.MergeFieldInfo(dngTags, dngTags.Length);

        if (m_parentExtender != null)
            m_parentExtender(tif);
    }

    public void WriteDngImage(string filename)
    {
        using (Tiff image = Tiff.Open(filename, "w"))
        {
            // Set dimensions first
            image.SetField(TiffTag.IMAGEWIDTH, width);
            image.SetField(TiffTag.IMAGELENGTH, height);
            
            // Set BlackLevelRepeatDim before BlackLevel
            short[] repeatDim = { 2, 2 };
            image.SetField((TiffTag)50713, repeatDim);
            
            // Now set BlackLevel (4 values for 2x2 CFA)
            float[] blackLevels = { 512f, 512f, 512f, 512f };
            image.SetField((TiffTag)50714, 4, blackLevels);
            
            // Set WhiteLevel
            int[] whiteLevels = { 16383 };
            image.SetField((TiffTag)50717, 1, whiteLevels);
            
            // ... write image data
            image.WriteDirectory();
        }
    }
}

// Call once at startup:
DngWriter.RegisterDngTags();
```

**Critical points:**
1. Call `RegisterDngTags()` **once** before opening any TIFF files
2. Use `passCount = true` for all variable-length tags
3. Always pass the count as the first parameter after the tag: `SetField(tag, count, array)`
4. For fixed-length tags, set exact read_count and write_count values