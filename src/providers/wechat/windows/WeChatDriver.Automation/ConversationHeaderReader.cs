using System.Drawing;
using System.Drawing.Drawing2D;
using System.Security.Cryptography;
using WeChatDriver.Capture;
using WeChatDriver.Core;
using WeChatDriver.Windows;

namespace WeChatDriver.Automation;

/// <summary>Runtime-owned, exact title observations. Cache only unchanged captured pixels.</summary>
public sealed class ConversationHeaderReader(IOcrEngine primary, Func<IOcrEngine?> fallbackFactory)
{
    private IOcrEngine? _fallback;
    private bool _fallbackInitialized;
    private string? _hash;
    private string[] _lines=[];
    public string FallbackStatus => _fallback is null ? "unavailable" : $"{_fallback.Language}; available={_fallback.IsAvailable}";

    public IReadOnlyList<string> Read(Bitmap header)
    {
        var png=WindowCapture.ToPng(header);
        var hash=Convert.ToHexString(SHA256.HashData(png));
        if(hash==_hash) return _lines;
        using var enlarged=new Bitmap(header.Width*2,header.Height*2);
        using(var g=Graphics.FromImage(enlarged))
        {
            g.InterpolationMode=InterpolationMode.HighQualityBicubic;
            g.DrawImage(header,0,0,enlarged.Width,enlarged.Height);
        }
        var scaled=WindowCapture.ToPng(enlarged);
        var lines=primary.RecognizePng(scaled).Select(l=>l.Text).ToList();
        lines.AddRange(primary.RecognizePng(png).Select(l=>l.Text));
        using(var large=new Bitmap(header.Width*3,header.Height*3))
        {
            using(var g=Graphics.FromImage(large))
            {
                g.InterpolationMode=InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode=PixelOffsetMode.Half;
                g.DrawImage(header,0,0,large.Width,large.Height);
            }
            lines.AddRange(primary.RecognizePng(WindowCapture.ToPng(large)).Select(l=>l.Text));
        }
        if(!_fallbackInitialized)
        {
            _fallback=fallbackFactory();
            _fallbackInitialized=true;
        }
        if(_fallback?.IsAvailable==true)
        {
            // A second local recognizer resolves small Latin glyphs such as rn/m.
            // Never substitute lookalike characters or fuzzy-match a recipient.
            try
            {
                lines.AddRange(_fallback.RecognizePng(scaled).Select(l=>l.Text));
                lines.AddRange(_fallback.RecognizePng(png).Select(l=>l.Text));
                // Small anti-aliased Latin strokes can merge (rn/m, ar/a). Remove gray
                // edge pixels before enlarging; recognition must still yield the exact title.
                using var contrast = CreateHighContrastVariant(header);
                lines.AddRange(_fallback.RecognizePng(WindowCapture.ToPng(contrast)).Select(l=>l.Text));
            }
            catch(WindowAutomationException) { /* Primary observation is still valid. */ }
        }
        _lines=lines.Select(MessageFingerprint.Normalize).Where(s=>s.Length>0).Distinct(StringComparer.Ordinal).ToArray();
        _hash=hash;
        return _lines;
    }

    public static Bitmap CreateHighContrastVariant(Bitmap header)
    {
        using var binary = new Bitmap(header.Width, header.Height);
        for (var y = 0; y < header.Height; y++)
        for (var x = 0; x < header.Width; x++)
        {
            var pixel = header.GetPixel(x, y);
            var value = (pixel.R + pixel.G + pixel.B) / 3 < 120 ? 0 : 255;
            binary.SetPixel(x, y, Color.FromArgb(value, value, value));
        }
        var enlarged = new Bitmap(header.Width * 3, header.Height * 3);
        using var graphics = Graphics.FromImage(enlarged);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(binary, 0, 0, enlarged.Width, enlarged.Height);
        return enlarged;
    }
}
