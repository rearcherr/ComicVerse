using System.Text;
using ComicVerse.Core;

namespace ComicVerse.Droid.Services;

/// <summary>TXT 文本编码：默认自动检测，也可在排版面板里手动覆盖。</summary>
public enum EncodingOverride
{
    Auto,
    Utf8,
    Gbk,
    Big5,
    ShiftJis
}

public static class TextEncoding
{
    public static Encoding Resolve(byte[] data, EncodingOverride selection) => selection switch
    {
        EncodingOverride.Utf8 => new UTF8Encoding(false),
        EncodingOverride.Gbk => EncodingDetector.Gbk,
        EncodingOverride.Big5 => EncodingDetector.Big5,
        EncodingOverride.ShiftJis => EncodingDetector.ShiftJis,
        _ => EncodingDetector.Detect(data)
    };
}
