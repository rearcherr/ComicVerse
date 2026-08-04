using System.Text.RegularExpressions;

namespace ComicVerse.Core;

/// <summary>自然排序：数字按数值比较，其余按字符串比较（如 page2 &lt; page10）。</summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static readonly NaturalStringComparer Instance = new();

    private static readonly Regex ChunkRegex = new(@"\d+|\D+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var xc = ChunkRegex.Matches(x);
        var yc = ChunkRegex.Matches(y);
        int count = Math.Min(xc.Count, yc.Count);
        for (int i = 0; i < count; i++)
        {
            string a = xc[i].Value;
            string b = yc[i].Value;
            bool aNum = char.IsDigit(a[0]);
            bool bNum = char.IsDigit(b[0]);
            if (aNum && bNum)
            {
                // 去掉前导零后按长度/数值比较
                string at = a.TrimStart('0');
                string bt = b.TrimStart('0');
                if (at.Length != bt.Length) return at.Length < bt.Length ? -1 : 1;
                int cmp = string.CompareOrdinal(at, bt);
                if (cmp != 0) return cmp;
            }
            else if (aNum != bNum)
            {
                return aNum ? -1 : 1; // 数字块排在文字块前
            }
            else
            {
                int cmp = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
            }
        }
        return x.Length.CompareTo(y.Length);
    }
}
