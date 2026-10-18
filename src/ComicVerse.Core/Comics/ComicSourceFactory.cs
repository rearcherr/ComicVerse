using System.IO;

namespace ComicVerse.Core.Comics;

public static class ComicSourceFactory
{
    // 7z 文件头魔数：37 7A BC AF 27 1C
    private static readonly byte[] SevenZipMagic = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

    public static IComicSource Create(string path)
    {
        if (Directory.Exists(path))
        {
            return new FolderComicSource(path);
        }

        if (!File.Exists(path))
        {
            throw new ComicSourceException("文件不存在: " + path);
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();

        // PDF / 图片这两个靠扩展名判断即可（PDF 有魔数但不便于在未读到 xref 时判定）
        if (ext == ".pdf")
            return new PdfComicSource(path);
        if (ImageHelper.IsImageFile(path))
            return new FolderComicSource(Path.GetDirectoryName(path)!, new[] { path });

        // 压缩包：一律按**内容魔数**分派，扩展名只作为兜底。
        // 现实里「.cb7 实为 zip」「.cbr 实为 tar」很常见（改扩展名党、部分打包工具），
        // 早期只对 .cbr/.cbt 做异常回退，导致 .cb7/.7z 伪包装直接打不开。
        return TryCreateByContent(path, ext);
    }

    private static IComicSource TryCreateByContent(string path, string ext)
    {
        var errors = new List<(string Format, Exception Error)>();

        foreach (string format in OrderCandidatesByMagic(path, ext))
        {
            try
            {
                var source = CreateByFormat(format, path);
                if (source.PageCount > 0) return source;
                source.Dispose();
                errors.Add((format, new ComicSourceException("压缩包内没有图片文件")));
            }
            catch (Exception ex)
            {
                errors.Add((format, ex));
            }
        }

        // 所有候选都失败：优先把「魔数吻合」的那个错误作为主因，避免被无关格式
        // 的 "Rar signature not found" 之类掩盖住真实原因（旧实现用 first ?? ex 有此问题）。
        throw BuildFailure(path, ext, errors);
    }

    /// <summary>按魔数给出候选顺序（魔数吻合的排最前），再补上扩展名对应的格式。</summary>
    private static IEnumerable<string> OrderCandidatesByMagic(string path, string ext)
    {
        var magic = ReadMagic(path);
        var ordered = new List<string>();

        void Add(string f)
        {
            if (!ordered.Contains(f)) ordered.Add(f);
        }

        if (magic is not null)
        {
            if (magic.Value.IsZip) Add("zip");
            if (magic.Value.IsRar) Add("rar");
            if (magic.Value.Is7z) Add("7z");
            if (magic.Value.IsTar) Add("tar");
        }

        switch (ext)
        {
            case ".cbz":
            case ".zip":
                Add("zip");
                break;
            case ".cbr":
            case ".rar":
                Add("rar");
                break;
            case ".cbt":
            case ".tar":
                Add("tar");
                break;
            case ".cb7":
            case ".7z":
                Add("7z");
                break;
            default:
                // 未知扩展名：魔数没命中就按常见顺序逐个试
                Add("zip");
                Add("rar");
                Add("7z");
                Add("tar");
                break;
        }

        // 魔数没命中时，其它格式也值得一试（有些 zip 前置了自解压头等）
        Add("zip");
        Add("rar");
        Add("7z");
        Add("tar");
        return ordered;
    }

    private static IComicSource CreateByFormat(string format, string path) => format switch
    {
        "zip" => new ZipComicSource(path),
        "rar" => new RarComicSource(path),
        "7z" => new SevenZipComicSource(path),
        "tar" => new TarComicSource(path),
        _ => throw new ComicSourceException("不支持的文件格式: " + format)
    };

    private static ComicSourceException BuildFailure(string path, string ext, List<(string Format, Exception Error)> errors)
    {
        string name = Path.GetFileName(path);
        if (errors.Count == 0)
            return new ComicSourceException("无法识别压缩包格式: " + name);

        // 「读通了但没有图片」只在**魔数吻合的那个格式**上才有意义：
        // 例如一个真正合法的空 CBZ 报“没有图片”，而一个彻底损坏的文件不该报成“没有图片”。
        var magic = ReadMagic(path);
        string? magicFormat =
            magic is null ? null :
            magic.Value.IsZip ? "zip" :
            magic.Value.IsRar ? "rar" :
            magic.Value.Is7z ? "7z" :
            magic.Value.IsTar ? "tar" : null;

        if (magicFormat is not null)
        {
            var hit = errors.FirstOrDefault(e => e.Format == magicFormat);
            if (hit.Error is ComicSourceException sc && sc.Message.Contains("没有图片"))
                return new ComicSourceException("压缩包内未找到图片: " + name, sc);
        }

        // 否则按候选顺序第一条（通常是魔数吻合的格式）作为主因
        var primary = errors[0];
        string detail = primary.Error.Message;
        var aggregate = new AggregateException(errors.Select(e => e.Error));
        return new ComicSourceException($"无法读取漫画文件（按 {primary.Format.ToUpperInvariant()} 解析失败）: {name} — {detail}", aggregate);
    }

    /// <summary>读取文件头判断格式；读不到返回 null。</summary>
    private static Magic? ReadMagic(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[512];
            int read = fs.Read(buf, 0, buf.Length);
            return new Magic(
                IsZip: read >= 2 && buf[0] == 'P' && buf[1] == 'K',
                IsRar: read >= 4 && buf[0] == 'R' && buf[1] == 'a' && buf[2] == 'r' && buf[3] == '!',
                Is7z: read >= 6 && buf.AsSpan(0, 6).SequenceEqual(SevenZipMagic),
                IsTar: read >= 262 && buf[257] == (byte)'u' && buf[258] == (byte)'s' && buf[259] == (byte)'t'
                       && buf[260] == (byte)'a' && buf[261] == (byte)'r');
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct Magic(bool IsZip, bool IsRar, bool Is7z, bool IsTar);
}
