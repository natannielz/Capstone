using System.Runtime.InteropServices;
using ImageMagick;

namespace UnitToko.ImageDecoder;

internal static class StrictImageDecoder
{
    static StrictImageDecoder() {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new PlatformNotSupportedException();
        System.Environment.SetEnvironmentVariable("MAGICK_CONFIGURE_PATH", AppContext.BaseDirectory);
        MagickNET.SetNativeLibraryDirectory(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native"));
    }
    internal static (int Width, int Height) Decode(byte[] bytes, string mime, int maximumWidth, int maximumHeight, long maximumPixels) {
        ResourceLimits.Width = (ulong)maximumWidth; ResourceLimits.Height = (ulong)maximumHeight;
        ResourceLimits.Area = (ulong)maximumPixels; ResourceLimits.Memory = 128 * 1024 * 1024; ResourceLimits.Disk = 0;
        // Native collection decoding uses an internal list slot as well as the one accepted frame.
        ResourceLimits.Thread = 1; ResourceLimits.ListLength = 2; ResourceLimits.MaxProfileSize = 1024 * 1024;
        var warning = false; var format = mime == "image/png" ? MagickFormat.Png : MagickFormat.Jpeg;
        using var images = new MagickImageCollection(); images.Warning += (_, _) => warning = true;
        try {
            images.Read(bytes, new MagickReadSettings { Format = format });
            if (warning || images.Count != 1) throw new InvalidDataException();
            var image = images[0]; image.Warning += (_, _) => warning = true;
            if (image.Format != format || image.Width == 0 || image.Height == 0 || image.Width > maximumWidth || image.Height > maximumHeight || (long)image.Width * image.Height > maximumPixels) throw new InvalidDataException();
            using var pixels = image.GetPixels();
            for (var y = 0; y < image.Height; y++) if (pixels.GetArea(0, y, image.Width, 1) is not { Length: > 0 }) throw new InvalidDataException();
            if (warning) throw new InvalidDataException();
            return (checked((int)image.Width), checked((int)image.Height));
        } catch (MagickException) { throw new InvalidDataException(); }
    }
}
