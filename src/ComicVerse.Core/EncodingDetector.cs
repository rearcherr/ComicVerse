using System.Text;

namespace ComicVerse.Core;

/// <summary>
/// 文本编码检测：BOM 优先，其次严格 UTF-8，再按双字节序列统计
/// GBK / Big5 / Shift-JIS 的匹配度（US-25 的自动检测；阅读器内仍可手动覆盖）。
/// </summary>
public static class EncodingDetector
{
    static EncodingDetector()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static readonly Encoding Gbk = Encoding.GetEncoding(936);
    public static readonly Encoding Big5 = Encoding.GetEncoding(950);
    public static readonly Encoding ShiftJis = Encoding.GetEncoding(932);

    public static Encoding Detect(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return new UTF8Encoding(false);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return Encoding.Unicode;
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return Encoding.BigEndianUnicode;
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xFE && data[2] == 0x00 && data[3] == 0x00)
            return Encoding.UTF32;

        // 严格 UTF-8：合法则优先（现代文件绝大多数是 UTF-8）
        try
        {
            var strictUtf8 = new UTF8Encoding(false, true);
            strictUtf8.GetString(data);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            // 非 UTF-8，继续
        }

        int sample = Math.Min(data.Length, 512 * 1024);
        var gbkScore = Score(data.AsSpan(0, sample), Gbk);
        var big5Score = Score(data.AsSpan(0, sample), Big5);
        var sjisScore = Score(data.AsSpan(0, sample), ShiftJis);

        if (sjisScore >= gbkScore && sjisScore >= big5Score && sjisScore > 0 && gbkScore == 0 && big5Score == 0)
            return ShiftJis;
        if (big5Score > gbkScore && big5Score > sjisScore)
            return Big5;
        if (gbkScore >= big5Score && gbkScore >= sjisScore)
            return Gbk;
        if (big5Score > 0)
            return Big5;
        if (sjisScore > 0)
            return ShiftJis;
        return Gbk;
    }

    /// <summary>统计按该编码解码后“像中文/日文正文”的字符数量。</summary>
    private static int Score(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        string s = encoding.GetString(bytes);
        int score = 0;
        foreach (char c in s)
        {
            if (c == '\uFFFD') continue;
            if (c >= 0x4E00 && c <= 0x9FFF) score += 3;          // CJK 统一汉字
            else if (c >= 0x3040 && c <= 0x30FF) score += 4;     // 日文假名
            else if (c >= 0x3400 && c <= 0x4DBF) score -= 2;     // CJK 扩展 A（通常是错误解码）
            else if (c >= 0xE000 && c <= 0xF8FF) score -= 2;    // 私用区
            else if (c >= 0x80) score += 1;
        }
        return score;
    }
}
