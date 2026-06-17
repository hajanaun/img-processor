using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImgApp;

public record ProcessingOptions(
    bool TrimContent = true,
    bool AddBorder = false,
    int BorderPx = 20,
    float Brightness = 1.0f,
    int MaxPx = 0,
    string OutputFormat = "PNG",
    int JpegQuality = 90
);

public static class ImageProcessor
{
    /// <summary>
    /// Hlavní metoda. Přijme syrové bajty obrázku a vrátí zpracované bajty.
    /// Pořadí kroků: EXIF orientace → bílé pozadí → ořez → jas → resize → okraj → uložení.
    /// </summary>
    public static byte[] ProcessImage(byte[] rawBytes, ProcessingOptions opts)
    {
        using var img = Image.Load<Rgba32>(rawBytes);

        // 1) Oprava EXIF orientace (foto z mobilu)
        img.Mutate(x => x.AutoOrient());

        // 2) Složit na bílé pozadí (průhlednost → bílá)
        using var rgb = FlattenOnWhite(img);

        // 3) Ořez bílého pozadí na RGB24 – těsně před uložením, žádné JPEG artefakty ještě nevznikly
        if (opts.TrimContent)
        {
            var bounds = FindContentBounds(rgb);
            if (bounds.HasValue)
                rgb.Mutate(x => x.Crop(bounds.Value));
        }

        // 4) Jas (1.0 = beze změny, < 1.0 = ztmavení, > 1.0 = zesvětlení)
        if (opts.Brightness != 1.0f)
            rgb.Mutate(x => x.Brightness(opts.Brightness));

        // 5) Zmenšení – Lanczos3, nikdy nezvětšuje
        if (opts.MaxPx > 0 && Math.Max(rgb.Width, rgb.Height) > opts.MaxPx)
            rgb.Mutate(x => x.Resize(new ResizeOptions
            {
                Sampler = KnownResamplers.Lanczos3,
                Mode = ResizeMode.Max,
                Size = new Size(opts.MaxPx, opts.MaxPx),
            }));

        // 6) Bílý okraj
        if (opts.AddBorder && opts.BorderPx > 0)
            rgb.Mutate(x => x.Pad(
                rgb.Width + opts.BorderPx * 2,
                rgb.Height + opts.BorderPx * 2,
                Color.White));

        // 7) Uložení do výstupního formátu
        using var ms = new MemoryStream();
        switch (opts.OutputFormat.ToUpperInvariant())
        {
            case "JPG" or "JPEG":
                rgb.Save(ms, new JpegEncoder
                {
                    Quality = opts.JpegQuality,
                    ColorType = JpegEncodingColor.YCbCrRatio444,
                });
                break;
            case "PNG":
                rgb.Save(ms, new PngEncoder());
                break;
            case "WEBP":
                rgb.Save(ms, new WebpEncoder
                {
                    FileFormat = WebpFileFormatType.Lossless,
                    Method = WebpEncodingMethod.BestQuality,
                });
                break;
            default:
                rgb.Save(ms, new JpegEncoder { Quality = opts.JpegQuality });
                break;
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Najde bounding box pixelů, které jsou "obsah" na již zploštěném RGB24 obraze:
    ///   R &lt; 255 nebo G &lt; 255 nebo B &lt; 255
    /// Nulová tolerance – průhledné oblasti jsou již složeny na bílou pomocí FlattenOnWhite.
    /// </summary>
    private static Rectangle? FindContentBounds(Image<Rgb24> img)
    {
        int minX = int.MaxValue, maxX = int.MinValue;
        int minY = int.MaxValue, maxY = int.MinValue;

        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    var px = row[x];
                    if (px.R < 255 || px.G < 255 || px.B < 255)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
        });

        return maxX >= minX
            ? new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1)
            : null;
    }

    /// <summary>
    /// Složí RGBA obrázek na bílé pozadí a vrátí nový RGB24 obrázek.
    /// Výpočet: výsledek = barva × alfa + bílá × (1 − alfa)
    /// </summary>
    private static Image<Rgb24> FlattenOnWhite(Image<Rgba32> src)
    {
        var dst = new Image<Rgb24>(src.Width, src.Height);
        src.ProcessPixelRows(dst, (srcAcc, dstAcc) =>
        {
            for (int y = 0; y < srcAcc.Height; y++)
            {
                var srcRow = srcAcc.GetRowSpan(y);
                var dstRow = dstAcc.GetRowSpan(y);
                for (int x = 0; x < srcRow.Length; x++)
                {
                    var s = srcRow[x];
                    float a = s.A / 255f;
                    float inv = 1f - a;
                    dstRow[x] = new Rgb24(
                        (byte)MathF.Round(s.R * a + 255 * inv),
                        (byte)MathF.Round(s.G * a + 255 * inv),
                        (byte)MathF.Round(s.B * a + 255 * inv));
                }
            }
        });
        return dst;
    }
}
