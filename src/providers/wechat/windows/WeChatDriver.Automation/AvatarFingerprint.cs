using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace WeChatDriver.Automation;

/// <summary>
/// 会话行头像的图像指纹 —— 会话身份的第二把钥匙。
///
/// 会话身份原本只有一把钥匙：OCR 读出来的名字。名字是任意文本，短名叠上笔画密的字
/// （实测 小璐璐 —— 两个 17 画的「璐」）会被读成「小 璐 璐」「小璐路璐」「小璐路璐路ONR」，
/// 身份哈希每次都变，于是明明点对了行也判 conversation_unverified。
///
/// 头像是一张图。同一台机器、同一 DPI 下，同一个联系人的头像每次渲染出来是同一块像素，
/// 不依赖识别精度，所以它不抖。
///
/// 取指纹的办法与实测标定（1089×962 窗口 @96 DPI、行高 84px、头像 45×45 位于距列表左边 21px）：
/// 头像框内缩 10% 缩到 24×24 存下来，比较时在一侧搜索 ±4px 的窗口取最大加权相关。
/// 两步都是必要的，实测数据说话 ——
///   · 只做定点取样：行中心偏 2px 就让同一头像的相关掉到 0.90，偏 3px 掉到 0.78；
///     两个都用绿色 logo 的会话（微信团队 / 文件传输助手）最坏能到 0.88，和真匹配一样高。
///   · 加窗口搜索后：同一头像跨两次截图 0.954~1.000，不同头像最高 0.842，中间空出 0.11。
/// 右上角降权到 0.25：微信把未读红点画在头像右上角，同一个会话有未读和已读时那一块像素不同。
/// </summary>
public static class AvatarFingerprint
{
    /// <summary>指纹的边长（像素）。24 给 ±4px 的搜索留出余量。</summary>
    public const int Sample = 24;

    /// <summary>参与相关计算的窗口边长。</summary>
    private const int Window = 16;

    /// <summary>比较时在一侧搜索的像素半径。</summary>
    private const int SearchRadius = 4;

    private const int Unit = Sample * Sample * 3;

    /// <summary>头像框相对会话列表左边界的水平偏移，以 120 DPI 为基准（实测 @96 DPI = 21px）。</summary>
    public const int BoxLeftAt120 = 26;

    /// <summary>头像框边长，以 120 DPI 为基准（实测 @96 DPI = 45px）。</summary>
    public const int BoxSizeAt120 = 56;

    /// <summary>相似度阈值：≥ 0.92 视为同一个头像。实测真匹配最低 0.954、假匹配最高 0.842。</summary>
    public const double MatchThreshold = 0.92;

    /// <summary>
    /// 行内头像框。垂直以行中心为基准居中 —— 实测头像中心与行中心相差 1~2px，由窗口搜索吸收。
    /// </summary>
    public static Rectangle Box(int rowCenterY, int dpi)
    {
        var size = (int)Math.Round(BoxSizeAt120 * dpi / 120.0);
        var left = (int)Math.Round(BoxLeftAt120 * dpi / 120.0);
        return new Rectangle(left, rowCenterY - size / 2, size, size);
    }

    /// <summary>
    /// 截取该行的头像指纹，返回 base64；区域越界或太小则返回 null（调用方退回纯文字路径）。
    /// <paramref name="list"/> 必须是会话列表区自身的位图（坐标原点 = 列表左边界）。
    /// </summary>
    public static string? Capture(Bitmap list, int rowCenterY, int dpi)
    {
        var box = Box(rowCenterY, dpi);
        // 向内收 10%：圆角与背景混合的边缘像素不稳定，不进指纹。
        var inset = Math.Max(1, (int)Math.Round(box.Width * 0.1));
        var inner = Rectangle.Intersect(
            new Rectangle(box.X + inset, box.Y + inset, box.Width - inset * 2, box.Height - inset * 2),
            new Rectangle(0, 0, list.Width, list.Height));
        if (inner.Width < 12 || inner.Height < 12) return null;

        using var small = new Bitmap(Sample, Sample, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(list, new Rectangle(0, 0, Sample, Sample), inner, GraphicsUnit.Pixel);
        }

        var bytes = new byte[Unit];
        for (var y = 0; y < Sample; y++)
        for (var x = 0; x < Sample; x++)
        {
            var pixel = small.GetPixel(x, y);
            var index = (y * Sample + x) * 3;
            bytes[index] = pixel.R;
            bytes[index + 1] = pixel.G;
            bytes[index + 2] = pixel.B;
        }
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// 最大加权相关，-1..1。同一头像（含几像素的行定位偏差）落在 0.95 以上，
    /// 不同头像实测不超过 0.85。任一指纹缺失或形状不符时返回 0。
    /// </summary>
    public static double Similarity(string? left, string? right)
    {
        var a = Decode(left);
        var b = Decode(right);
        if (a is null || b is null || a.Length != Unit || b.Length != Unit) return 0;

        var best = double.NegativeInfinity;
        for (var dy = -SearchRadius; dy <= SearchRadius; dy++)
        for (var dx = -SearchRadius; dx <= SearchRadius; dx++)
        {
            var value = WindowCorrelation(a, b, dx, dy);
            if (value > best) best = value;
        }
        return best;
    }

    public static bool Matches(string? left, string? right) => Similarity(left, right) >= MatchThreshold;

    /// <summary><paramref name="a"/> 取中心窗口，<paramref name="b"/> 平移到 (dx, dy) 后取同样大小的窗口。</summary>
    private static double WindowCorrelation(double[] a, double[] b, int dx, int dy)
    {
        var origin = (Sample - Window) / 2;
        var bx = origin + dx;
        var by = origin + dy;
        if (bx < 0 || by < 0 || bx + Window > Sample || by + Window > Sample) return 0;

        double sumWeight = 0, sumA = 0, sumB = 0, sumAA = 0, sumBB = 0, sumAB = 0;
        for (var y = 0; y < Window; y++)
        for (var x = 0; x < Window; x++)
        {
            var weight = Weight(x, y);
            var ai = ((origin + y) * Sample + (origin + x)) * 3;
            var bi = ((by + y) * Sample + (bx + x)) * 3;
            for (var channel = 0; channel < 3; channel++)
            {
                double av = a[ai + channel];
                double bv = b[bi + channel];
                sumWeight += weight;
                sumA += weight * av;
                sumB += weight * bv;
                sumAA += weight * av * av;
                sumBB += weight * bv * bv;
                sumAB += weight * av * bv;
            }
        }
        if (sumWeight <= 0) return 0;

        var meanA = sumA / sumWeight;
        var meanB = sumB / sumWeight;
        var varianceA = sumAA / sumWeight - meanA * meanA;
        var varianceB = sumBB / sumWeight - meanB * meanB;
        if (varianceA <= 1e-9 || varianceB <= 1e-9) return 0;
        var covariance = sumAB / sumWeight - meanA * meanB;
        return covariance / Math.Sqrt(varianceA * varianceB);
    }

    /// <summary>未读红点压在头像右上角，那一块的权重降下来，头像有未读和没未读时才对得上。</summary>
    private static double Weight(int x, int y) =>
        x >= 0.55 * Window && y <= 0.45 * Window ? 0.25 : 1.0;

    private static double[]? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var bytes = Convert.FromBase64String(value);
            var result = new double[bytes.Length];
            for (var i = 0; i < bytes.Length; i++) result[i] = bytes[i];
            return result;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
