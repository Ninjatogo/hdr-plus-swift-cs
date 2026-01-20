using BurstPhoto.Core.Implementations;
using BurstPhoto.Core.Models;
using Xunit;
using System;
using System.IO;
using System.Text;

namespace BurstPhoto.Tests;

/// <summary>
/// Tests for SimpleRawWriter output format validation.
/// These tests verify the output file is valid and can be read.
/// </summary>
public class OutputValidationTests
{
    [Fact]
    public void Write_CreatesValidPpmHeader()
    {
        // Arrange
        var writer = new SimpleRawWriter();
        var tempPath = Path.Combine(Path.GetTempPath(), $"test_output_{Guid.NewGuid()}.ppm");
        
        var image = new RawImage
        {
            Width = 10,
            Height = 10,
            Data = new ushort[10 * 10 * 3] // RGB image
        };
        
        // Fill with test data
        for (int i = 0; i < image.Data.Length; i++)
        {
            image.Data[i] = (ushort)(i % 65535);
        }

        try
        {
            // Act
            writer.Write(tempPath, image);

            // Assert - Check file exists and has valid PPM header
            Assert.True(File.Exists(tempPath), "Output file should exist");
            
            using var reader = new StreamReader(tempPath);
            string magic = reader.ReadLine()!;
            string dimensions = reader.ReadLine()!;
            string maxVal = reader.ReadLine()!;

            Assert.Equal("P6", magic); // PPM binary format
            Assert.Equal("10 10", dimensions);
            Assert.Equal("65535", maxVal);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void Write_CreatesValidPgmForGrayscale()
    {
        // Arrange
        var writer = new SimpleRawWriter();
        var tempPath = Path.Combine(Path.GetTempPath(), $"test_output_{Guid.NewGuid()}.pgm");
        
        var image = new RawImage
        {
            Width = 10,
            Height = 10,
            Data = new ushort[10 * 10] // Grayscale image
        };
        
        for (int i = 0; i < image.Data.Length; i++)
        {
            image.Data[i] = (ushort)(i % 65535);
        }

        try
        {
            // Act
            writer.Write(tempPath, image);

            // Assert - Check file exists and has valid PGM header
            Assert.True(File.Exists(tempPath));
            
            using var reader = new StreamReader(tempPath);
            string magic = reader.ReadLine()!;
            
            Assert.Equal("P5", magic); // PGM binary format
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void Write_CorrectFileSize()
    {
        // Arrange
        var writer = new SimpleRawWriter();
        var tempPath = Path.Combine(Path.GetTempPath(), $"test_output_{Guid.NewGuid()}.ppm");
        
        int width = 100;
        int height = 100;
        var image = new RawImage
        {
            Width = width,
            Height = height,
            Data = new ushort[width * height * 3] // RGB image
        };

        try
        {
            // Act
            writer.Write(tempPath, image);

            // Assert
            var fileInfo = new FileInfo(tempPath);
            // Header: "P6\n100 100\n65535\n" = 18 bytes
            // Data: 100 * 100 * 3 * 2 = 60000 bytes
            // Total should be header + data
            int expectedHeaderSize = "P6\n100 100\n65535\n".Length;
            int expectedDataSize = width * height * 3 * 2;
            
            Assert.Equal(expectedHeaderSize + expectedDataSize, fileInfo.Length);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void Write_DataIsBigEndian()
    {
        // Arrange
        var writer = new SimpleRawWriter();
        var tempPath = Path.Combine(Path.GetTempPath(), $"test_output_{Guid.NewGuid()}.pgm");
        
        var image = new RawImage
        {
            Width = 1,
            Height = 1,
            Data = new ushort[] { 0x1234 } // Known value to check endianness
        };

        try
        {
            // Act
            writer.Write(tempPath, image);

            // Assert - Read raw bytes after header
            var bytes = File.ReadAllBytes(tempPath);
            // Skip header (P5\n1 1\n65535\n = 14 bytes)
            int headerEnd = Array.IndexOf(bytes, (byte)'\n', 
                Array.IndexOf(bytes, (byte)'\n', 
                    Array.IndexOf(bytes, (byte)'\n') + 1) + 1) + 1;
            
            // In big endian: 0x1234 should be stored as 0x12, 0x34
            Assert.Equal(0x12, bytes[headerEnd]);
            Assert.Equal(0x34, bytes[headerEnd + 1]);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
