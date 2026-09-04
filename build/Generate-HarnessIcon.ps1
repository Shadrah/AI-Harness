param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\Harness.App\Assets\Harness.ico')
)

$source = @'
using System;
using System.Collections.Generic;
using System.IO;

public static class HarnessIconGenerator
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    public static void Generate(string outputPath)
    {
        var images = new List<byte[]>();
        foreach (var size in Sizes) images.Add(BuildImage(size));
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)Sizes.Length);
        var offset = 6 + (16 * Sizes.Length);
        for (var index = 0; index < Sizes.Length; index++)
        {
            var size = Sizes[index];
            writer.Write((byte)(size == 256 ? 0 : size)); writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(images[index].Length); writer.Write(offset); offset += images[index].Length;
        }
        foreach (var image in images) writer.Write(image);
    }

    private static byte[] BuildImage(int size)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var xorBytes = size * size * 4;
        var maskBytes = ((size + 31) / 32) * 4 * size;
        writer.Write(40); writer.Write(size); writer.Write(size * 2); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write(xorBytes); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        const int samples = 4;
        var margin = size * 0.055; var radius = size * 0.18; var border = Math.Max(1.0, size * 0.065);
        for (var row = size - 1; row >= 0; row--)
        for (var column = 0; column < size; column++)
        {
            var alphaSamples = 0; var red = 0.0; var green = 0.0; var blue = 0.0;
            for (var sy = 0; sy < samples; sy++)
            for (var sx = 0; sx < samples; sx++)
            {
                var x = column + ((sx + 0.5) / samples); var y = row + ((sy + 0.5) / samples);
                if (!InsideRoundedRectangle(x, y, size, margin, radius)) continue;
                alphaSamples++;
                var inner = InsideRoundedRectangle(x, y, size, margin + border, Math.Max(0, radius - border));
                var h = (((x >= size * 0.29 && x <= size * 0.39) || (x >= size * 0.61 && x <= size * 0.71)) &&
                         y >= size * 0.25 && y <= size * 0.75) ||
                        (x >= size * 0.36 && x <= size * 0.64 && y >= size * 0.45 && y <= size * 0.55);
                if (!inner || h) { red += 101; green += 199; blue += 208; }
                else { red += 17; green += 21; blue += 27; }
            }
            if (alphaSamples == 0) writer.Write(0u);
            else
            {
                writer.Write((byte)Math.Round(blue / alphaSamples)); writer.Write((byte)Math.Round(green / alphaSamples));
                writer.Write((byte)Math.Round(red / alphaSamples)); writer.Write((byte)Math.Round(255.0 * alphaSamples / (samples * samples)));
            }
        }
        writer.Write(new byte[maskBytes]); writer.Flush(); return stream.ToArray();
    }

    private static bool InsideRoundedRectangle(double x, double y, double size, double margin, double radius)
    {
        var nearestX = Math.Max(margin + radius, Math.Min(size - margin - radius, x));
        var nearestY = Math.Max(margin + radius, Math.Min(size - margin - radius, y));
        var dx = x - nearestX; var dy = y - nearestY;
        return (dx * dx) + (dy * dy) <= radius * radius;
    }
}
'@

Add-Type -TypeDefinition $source -Language CSharp
$destination = [System.IO.Path]::GetFullPath($OutputPath)
[HarnessIconGenerator]::Generate($destination)
Write-Host "Generated $destination"
