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
                // 数值相等的数字块（"1" 与 "01"）不能就此结束，也不能依赖 x.Length 兜底：
                // 那会让 Compare(a,b) 与 Compare(b,a) 不再互为相反数，破坏排序契约。
                int digitCmp = CompareNumeric(a, b);
                if (digitCmp != 0) return digitCmp;
                continue;
            }
            if (aNum != bNum)
            {
                return aNum ? -1 : 1; // 数字块排在文字块前
            }

            // 剩下的块至少有一方不是数字（数字块若继续比下去，会比到 '/' '-' '.' 这些分隔符上，
            // 再由分隔符的字符序决定顺序，结果就不再是「数值自然序」了）。
            // 此时先把逐字相等的那部分比较完；一旦出现差异，就交给「块数少者优先」裁决：
            //   ch1      vs ch1.2 / ch1.5 / ch1.10  -> ch1（正篇）在前
            //   1        vs 1-1   / 1-2            -> 1（第 1 页）在前
            //   10       vs 10a                    -> 10 在前
            int cmp = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            if (cmp == 0) continue;
            return xc.Count < yc.Count ? -1 : 1;
        }
        // 公共前缀完全相同、但一方还有剩余块：短的那个是更基础的名字，排在前面。
        if (xc.Count != yc.Count) return xc.Count < yc.Count ? -1 : 1;
        if (x.Length != y.Length) return x.Length < y.Length ? -1 : 1;
        // 完全等价时用原始串做最终裁决，保证 Compare(a,b) == -Compare(b,a)
        return string.CompareOrdinal(x, y);
    }

    /// <summary>按数值比较两个纯数字块：去前导零后先比长度再比字面，长度相同即数值相同，返回 0。</summary>
    private static int CompareNumeric(string a, string b)
    {
        string at = a.TrimStart('0');
        string bt = b.TrimStart('0');
        if (at.Length != bt.Length) return at.Length < bt.Length ? -1 : 1;
        int cmp = string.CompareOrdinal(at, bt);
        if (cmp != 0) return cmp;
        // 数值完全相同（"1" vs "01"）：位数少的（=没补零的）排前面，保证反对称
        return a.Length == b.Length ? 0 : (a.Length < b.Length ? -1 : 1);
    }
}
