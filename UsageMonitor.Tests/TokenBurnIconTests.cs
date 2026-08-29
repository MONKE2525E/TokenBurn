using System.Drawing;
using System.Windows.Media.Imaging;
using UsageMonitor.Desktop;

namespace UsageMonitor.Tests;

public sealed class TokenBurnIconTests
{
    [Fact]
    public void CanonicalAppIconBackgroundFillsTheSquareCanvas()
    {
        var sourcePath = Path.Combine(FindRepositoryRoot(), "assets", "brand", "logo", "tokenburn-app-icon.svg");
        var document = System.Xml.Linq.XDocument.Load(sourcePath);
        var root = Assert.IsType<System.Xml.Linq.XElement>(document.Root);
        var ns = root.Name.Namespace;
        var background = Assert.Single(root.Elements(ns + "rect"));

        Assert.Equal("0 0 1254 1254", root.Attribute("viewBox")?.Value);
        Assert.Null(background.Attribute("x"));
        Assert.Null(background.Attribute("y"));
        Assert.Equal("1254", background.Attribute("width")?.Value);
        Assert.Equal("1254", background.Attribute("height")?.Value);
    }

    [Fact]
    public void SourceControlledExportSetExistsAndBothIcosLoad()
    {
        var exportDirectory = Path.Combine(FindRepositoryRoot(), "assets", "brand", "exports");
        var expected = new[]
        {
            "tokenburn-app-icon-16.png",
            "tokenburn-app-icon-24.png",
            "tokenburn-app-icon-32.png",
            "tokenburn-app-icon-48.png",
            "tokenburn-app-icon-64.png",
            "tokenburn-app-icon-128.png",
            "tokenburn-app-icon-256.png",
            "tokenburn-mark-gray-16.png",
            "tokenburn-mark-gray-20.png",
            "tokenburn-mark-gray-24.png",
            "tokenburn-mark-gray-32.png",
            "tokenburn-app-icon.ico",
            "tokenburn-tray-icon.ico",
            "tokenburn-mark-gray.ico"
        };

        foreach (var file in expected)
            Assert.True(File.Exists(Path.Combine(exportDirectory, file)), $"Missing icon export: {file}");

        using var appIcon = new Icon(Path.Combine(exportDirectory, "tokenburn-app-icon.ico"));
        using var trayIcon = new Icon(Path.Combine(exportDirectory, "tokenburn-tray-icon.ico"));
        Assert.True(appIcon.Width > 0 && appIcon.Height > 0);
        Assert.True(trayIcon.Width > 0 && trayIcon.Height > 0);
    }

    [Fact]
    public void TrayIcoContainsNativeNotificationAreaFrames()
    {
        var trayPath = Path.Combine(FindRepositoryRoot(), "assets", "brand", "exports", "tokenburn-tray-icon.ico");
        var bytes = File.ReadAllBytes(trayPath);
        Assert.Equal((ushort)0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal((ushort)1, BitConverter.ToUInt16(bytes, 2));
        var frameCount = BitConverter.ToUInt16(bytes, 4);
        var frames = new HashSet<int>();
        for (var index = 0; index < frameCount; index++)
        {
            var entry = 6 + index * 16;
            var width = bytes[entry];
            frames.Add(width == 0 ? 256 : width);
        }

        Assert.Equal(6, frameCount);
        Assert.True(new[] { 16, 20, 24, 32, 48, 256 }.All(frames.Contains));
    }

    [Fact]
    public void TrayIcoDibFramesHaveTransparentCornersAndAndMaskBits()
    {
        var trayPath = Path.Combine(FindRepositoryRoot(), "assets", "brand", "exports", "tokenburn-tray-icon.ico");
        var bytes = File.ReadAllBytes(trayPath);
        var frameCount = BitConverter.ToUInt16(bytes, 4);

        for (var index = 0; index < frameCount; index++)
        {
            var entry = 6 + index * 16;
            var width = bytes[entry] == 0 ? 256 : bytes[entry];
            var bytesInResource = checked((int)BitConverter.ToUInt32(bytes, entry + 8));
            var frameOffset = checked((int)BitConverter.ToUInt32(bytes, entry + 12));
            Assert.Equal(40, BitConverter.ToInt32(bytes, frameOffset));
            Assert.Equal(width, BitConverter.ToInt32(bytes, frameOffset + 4));
            var doubledHeight = BitConverter.ToInt32(bytes, frameOffset + 8);
            Assert.Equal(width * 2, doubledHeight);
            Assert.Equal((ushort)32, BitConverter.ToUInt16(bytes, frameOffset + 14));

            var maskStride = ((width + 31) / 32) * 4;
            var xorStart = frameOffset + 40;
            var maskStart = xorStart + width * width * 4;
            Assert.Equal(bytesInResource, 40 + width * width * 4 + maskStride * width);
            Assert.True(maskStart + maskStride * width <= bytes.Length);

            foreach (var (x, y) in new[] { (0, 0), (width - 1, 0), (0, width - 1), (width - 1, width - 1) })
            {
                // ICO DIB rows are bottom-up. Read the logical image corner, not the stored row.
                var storedRow = width - 1 - y;
                var pixelOffset = xorStart + (storedRow * width + x) * 4;
                Assert.Equal((byte)0, bytes[pixelOffset]);     // blue
                Assert.Equal((byte)0, bytes[pixelOffset + 1]); // green
                Assert.Equal((byte)0, bytes[pixelOffset + 2]); // red
                Assert.Equal((byte)0, bytes[pixelOffset + 3]); // alpha

                var maskByte = bytes[maskStart + storedRow * maskStride + x / 8];
                var maskBit = (maskByte >> (7 - x % 8)) & 1;
                Assert.Equal(1, maskBit); // 1 means transparent in the ICO AND mask.
            }
        }
    }

    [Fact]
    public void EmbeddedIconRolesLoadFromTheDesktopAssembly()
    {
        var app = Assert.IsAssignableFrom<BitmapSource>(TokenBurnIconResources.LoadWpfAppIcon());
        var notification = TokenBurnIconResources.LoadNotificationIcon();
        using var appIcon = TokenBurnIconResources.LoadAppIcon();
        using var tray = TokenBurnIconResources.LoadTrayIcon();

        Assert.Equal(256, app.PixelWidth);
        Assert.Equal(256, app.PixelHeight);
        Assert.Equal(32, notification.PixelWidth);
        Assert.Equal(32, notification.PixelHeight);
        Assert.True(appIcon.Width > 0);
        Assert.True(appIcon.Height > 0);
        Assert.True(tray.Width >= 32);
        Assert.True(tray.Height >= 32);
    }

    [Fact]
    public void AppIdentityAndTrayUseTheCanonicalCoralTokenBurnMark()
    {
        var app = Assert.IsAssignableFrom<BitmapSource>(TokenBurnIconResources.LoadWpfAppIcon());
        using var tray = TokenBurnIconResources.LoadTrayIcon();
        using var trayBitmap = tray.ToBitmap();

        var appPixels = new byte[app.PixelWidth * app.PixelHeight * 4];
        app.CopyPixels(appPixels, app.PixelWidth * 4, 0);
        var appColor = FindOpaquePixel(appPixels, app.PixelWidth * 4);
        Assert.True(appColor.R > 220 && appColor.G < 100 && appColor.B < 120);
        Assert.True(ContainsCoralPixel(trayBitmap));
    }

    private static System.Drawing.Color FindOpaquePixel(byte[] pixels, int stride)
    {
        for (var index = 0; index + 3 < pixels.Length; index += 4)
        {
            if (pixels[index + 3] > 200)
                return System.Drawing.Color.FromArgb(pixels[index + 3], pixels[index + 2], pixels[index + 1], pixels[index]);
        }

        return System.Drawing.Color.Transparent;
    }

    private static System.Drawing.Color FindOpaquePixel(Bitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var color = bitmap.GetPixel(x, y);
            if (color.A > 200) return color;
        }

        return System.Drawing.Color.Transparent;
    }

    private static bool ContainsCoralPixel(Bitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var color = bitmap.GetPixel(x, y);
            if (color.A > 200 && color.R > 220 && color.G < 100 && color.B < 120)
                return true;
        }

        return false;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "UsageMonitor.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the TokenBurn repository root.");
    }
}
