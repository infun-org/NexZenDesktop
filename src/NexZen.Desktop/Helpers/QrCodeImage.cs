using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Net.Codecrete.QrCodeGenerator;

namespace NexZen.Desktop.Helpers;

public static class QrCodeImage
{
    public static Bitmap Render(string text, int moduleSize = 8, int borderModules = 4)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text is empty.", nameof(text));

        var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        int dim = (qr.Size + borderModules * 2) * moduleSize;
        var bitmap = new WriteableBitmap(
            new PixelSize(dim, dim),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        var pixels = new byte[dim * dim * 4];
        for (int y = 0; y < dim; y++)
        {
            int moduleY = y / moduleSize - borderModules;
            for (int x = 0; x < dim; x++)
            {
                int moduleX = x / moduleSize - borderModules;
                bool dark = moduleX >= 0 && moduleY >= 0 && moduleX < qr.Size && moduleY < qr.Size
                    && qr.GetModule(moduleX, moduleY);
                byte v = dark ? (byte)0 : (byte)255;
                int i = (y * dim + x) * 4;
                pixels[i] = v;
                pixels[i + 1] = v;
                pixels[i + 2] = v;
                pixels[i + 3] = 255;
            }
        }

        using var fb = bitmap.Lock();
        int srcStride = dim * 4;
        if (fb.RowBytes == srcStride)
        {
            Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        }
        else
        {
            for (int y = 0; y < dim; y++)
                Marshal.Copy(pixels, y * srcStride, fb.Address + y * fb.RowBytes, srcStride);
        }
        return bitmap;
    }
}
