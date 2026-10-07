using Android.Content;
using Android.Provider;
using ComicVerse.Core;

namespace ComicVerse.Droid.Services;

/// <summary>
/// 安卓沙盒存储：书架里的书必须落在应用私有目录里（Core 以文件路径读写），
/// 因此通过 SAF 选中的文件/文件夹会被复制进来。
/// </summary>
public static class StorageImporter
{
    public static string CopyFile(Context context, Android.Net.Uri uri, string? relativeFolder = null)
    {
        string name = Sanitize(QueryDisplayName(context, uri) ?? "import-" + Guid.NewGuid().ToString("N"));
        string dir = AppServices.ImportDir;
        if (!string.IsNullOrWhiteSpace(relativeFolder))
            dir = Path.Combine(dir, SanitizeRelative(relativeFolder));
        Directory.CreateDirectory(dir);

        string target = UniquePath(dir, name);
        using var input = context.ContentResolver!.OpenInputStream(uri)
                          ?? throw new IOException("无法读取所选文件");
        using var output = File.Create(target);
        input.CopyTo(output);
        return target;
    }

    /// <summary>递归复制 SAF 目录树，返回复制出来的根目录（交给导入服务按文件夹规则识别）。</summary>
    public static string? CopyTree(Context context, Android.Net.Uri treeUri,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        string? rootId = DocumentsContract.GetTreeDocumentId(treeUri);
        if (string.IsNullOrEmpty(rootId)) return null;

        string rootName = Sanitize(QueryDocumentName(context, treeUri, rootId!) ?? "导入文件夹");
        string root = UniqueDirectory(AppServices.ImportDir, rootName);
        Directory.CreateDirectory(root);
        CopyDocument(context, treeUri, rootId!, root, root, progress, ct);
        return root;
    }

    private static void CopyDocument(Context context, Android.Net.Uri treeUri, string documentId,
        string targetDir, string labelRoot, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, documentId);
        var resolver = context.ContentResolver!;

        using var cursor = resolver.Query(childrenUri, new[]
        {
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnMimeType
        }, null, null, null);
        if (cursor is null) return;

        while (cursor.MoveToNext())
        {
            ct.ThrowIfCancellationRequested();
            string? childId = cursor.GetString(0);
            string? name = cursor.GetString(1);
            string? mime = cursor.GetString(2);
            if (string.IsNullOrEmpty(childId) || string.IsNullOrEmpty(name)) continue;

            if (mime == DocumentsContract.Document.MimeTypeDir)
            {
                string childDir = UniqueDirectory(targetDir, name!);
                Directory.CreateDirectory(childDir);
                CopyDocument(context, treeUri, childId!, childDir, labelRoot, progress, ct);
            }
            else
            {
                progress?.Report(name!);
                try
                {
                    var docUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, childId!);
                    using var input = context.ContentResolver!.OpenInputStream(docUri);
                    if (input is null) continue;
                    using var output = File.Create(UniquePath(targetDir, name!));
                    input.CopyTo(output);
                }
                catch (Exception ex)
                {
                    Log.Error("复制失败: " + name, ex);
                }
            }
        }
    }

    private static string? QueryDocumentName(Context context, Android.Net.Uri treeUri, string documentId)
    {
        try
        {
            var docUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, documentId);
            using var cursor = context.ContentResolver!.Query(docUri,
                new[] { DocumentsContract.Document.ColumnDisplayName }, null, null, null);
            if (cursor is not null && cursor.MoveToFirst())
            {
                int index = cursor.GetColumnIndex(DocumentsContract.Document.ColumnDisplayName);
                if (index >= 0) return cursor.GetString(index);
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取文件夹名失败", ex);
        }
        return null;
    }

    private static string? QueryDisplayName(Context context, Android.Net.Uri uri)
    {
        try
        {
            using var cursor = context.ContentResolver!.Query(uri, null, null, null, null);
            if (cursor is not null && cursor.MoveToFirst())
            {
                int index = cursor.GetColumnIndex(OpenableColumns.DisplayName);
                if (index >= 0) return cursor.GetString(index);
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取文件名失败", ex);
        }
        return uri.LastPathSegment;
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "import" : name;
    }

    private static string SanitizeRelative(string relative)
    {
        var parts = relative.Split('/', '\\')
            .Where(p => !string.IsNullOrWhiteSpace(p) && p != "." && p != "..")
            .Select(Sanitize);
        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    private static string UniquePath(string dir, string name)
    {
        string target = Path.Combine(dir, name);
        if (!File.Exists(target) && !Directory.Exists(target)) return target;
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        for (int i = 1; i < 1000; i++)
        {
            target = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(target) && !Directory.Exists(target)) return target;
        }
        return Path.Combine(dir, $"{stem}-{Guid.NewGuid():N}{ext}");
    }

    private static string UniqueDirectory(string parent, string name)
    {
        string target = Path.Combine(parent, name);
        if (!Directory.Exists(target) && !File.Exists(target)) return target;
        for (int i = 1; i < 1000; i++)
        {
            target = Path.Combine(parent, $"{name} ({i})");
            if (!Directory.Exists(target) && !File.Exists(target)) return target;
        }
        return Path.Combine(parent, $"{name}-{Guid.NewGuid():N}");
    }
}
