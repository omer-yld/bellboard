using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace Bellboard.Core;

/// <summary>QR codes for pairing a phone: a bitmap for Settings, block characters for `bell remote pair`.</summary>
public static class QrCode
{
    /// <summary>Dark modules, with a 4-module quiet zone.</summary>
    public static bool[,] Matrix(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var rows = data.ModuleMatrix;
        var n = rows.Count;
        var m = new bool[n, n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                m[y, x] = rows[y][x];
        return m;
    }

    /// <summary>
    /// Two rows per line with half blocks, drawn light-on-dark so it scans from a dark terminal: light modules are
    /// printed, dark ones are left as the background.
    /// </summary>
    public static string ToText(string text)
    {
        var m = Matrix(text);
        int n = m.GetLength(0);
        var sb = new StringBuilder();
        for (int y = 0; y < n; y += 2)
        {
            for (int x = 0; x < n; x++)
            {
                bool top = !m[y, x];
                bool bottom = y + 1 < n ? !m[y + 1, x] : true;
                sb.Append(top && bottom ? '█' : top ? '▀' : bottom ? '▄' : ' ');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public static BitmapSource ToBitmap(string text, int scale = 6)
    {
        var m = Matrix(text);
        int n = m.GetLength(0), size = n * scale, stride = size * 4;
        var pixels = new byte[size * stride];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                byte v = m[y / scale, x / scale] ? (byte)0x0D : (byte)0xFF;
                int i = y * stride + x * 4;
                pixels[i] = v; pixels[i + 1] = v; pixels[i + 2] = v; pixels[i + 3] = 0xFF;
            }
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }
}
