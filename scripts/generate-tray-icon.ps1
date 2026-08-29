param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\assets\brand\exports\tokenburn-tray-icon.ico')
)

$ErrorActionPreference = 'Stop'
$exportRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\assets\brand\exports')).Path

# Each tray size starts from the nearest native export. The 20px shell size is rendered from the
# 24px master, never from the 256px marketing asset.
$sources = [string[]]@(
    (Join-Path $exportRoot 'tokenburn-app-icon-16.png'),
    (Join-Path $exportRoot 'tokenburn-app-icon-24.png'),
    (Join-Path $exportRoot 'tokenburn-app-icon-24.png'),
    (Join-Path $exportRoot 'tokenburn-app-icon-32.png'),
    (Join-Path $exportRoot 'tokenburn-app-icon-48.png'),
    (Join-Path $exportRoot 'tokenburn-app-icon-256.png')
)
$sizes = [int[]](16, 20, 24, 32, 48, 256)
if ($sources | Where-Object { -not (Test-Path -LiteralPath $_) }) {
    throw 'One or more TokenBurn tray-icon source PNGs are missing.'
}

Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class TokenBurnTrayIcoGenerator
{
    public static void Write(string outputPath, string[] sourcePaths, int[] sizes)
    {
        if (sourcePaths.Length != sizes.Length) throw new ArgumentException("Source and size counts differ.");
        var frames = new byte[sizes.Length][];
        for (var index = 0; index < sizes.Length; index++) frames[index] = BuildFrame(sourcePaths[index], sizes[index]);

        using (var stream = File.Create(outputPath))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)frames.Length);
            var offset = 6 + frames.Length * 16;
            for (var index = 0; index < frames.Length; index++)
            {
                writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
                writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(frames[index].Length);
                writer.Write(offset);
                offset += frames[index].Length;
            }
            for (var index = 0; index < frames.Length; index++) writer.Write(frames[index]);
        }
    }

    private static byte[] BuildFrame(string sourcePath, int size)
    {
        using (var source = new Bitmap(sourcePath))
        using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, size, size));
            }

            // Low-alpha pixels beyond the rounded-corner edge are not artwork. Clearing their
            // RGB as well as alpha prevents white/coral matte dots in GDI tray rendering.
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.A < 96) bitmap.SetPixel(x, y, Color.FromArgb(0, 0, 0, 0));
            }
            var transparentBlack = Color.FromArgb(0, 0, 0, 0);
            bitmap.SetPixel(0, 0, transparentBlack);
            bitmap.SetPixel(size - 1, 0, transparentBlack);
            bitmap.SetPixel(0, size - 1, transparentBlack);
            bitmap.SetPixel(size - 1, size - 1, transparentBlack);

            var maskStride = ((size + 31) / 32) * 4;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                // ICO DIB height includes both the XOR image and 1-bit AND mask.
                writer.Write(40);
                writer.Write(size);
                writer.Write(size * 2);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(0);
                writer.Write(size * size * 4);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);

                // DIB XOR rows are bottom-up and stored BGRA.
                for (var y = size - 1; y >= 0; y--)
                for (var x = 0; x < size; x++)
                {
                    var color = bitmap.GetPixel(x, y);
                    writer.Write(color.B);
                    writer.Write(color.G);
                    writer.Write(color.R);
                    writer.Write(color.A);
                }

                // AND mask uses the same bottom-up orientation. A 1 bit is transparent.
                for (var y = size - 1; y >= 0; y--)
                {
                    var row = new byte[maskStride];
                    for (var x = 0; x < size; x++)
                    {
                        if (bitmap.GetPixel(x, y).A != 0) continue;
                        row[x / 8] |= (byte)(1 << (7 - (x % 8)));
                    }
                    writer.Write(row);
                }

                return stream.ToArray();
            }
        }
    }
}
'@ -ReferencedAssemblies System.Drawing

[TokenBurnTrayIcoGenerator]::Write($OutputPath, $sources, $sizes)
Write-Host "Generated $OutputPath"
