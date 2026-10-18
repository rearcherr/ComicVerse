using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using ComicVerse.Core.Models;
using Microsoft.Data.Sqlite;

namespace ComicVerse.Core.Services;

/// <summary>本地书架数据库（SQLite）：书籍、进度、书签、设置。</summary>
public sealed class LibraryService : IDisposable
{
    private readonly SqliteConnection _conn;
    /// <summary>当前 schema 版本（PRAGMA user_version），用于一次性迁移。</summary>
    private const int SchemaVersion = 2;

    public string DbPath { get; }
    public string CoverDir { get; }

    public LibraryService(string dbPath)
    {
        DbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        CoverDir = Path.Combine(Path.GetDirectoryName(dbPath)!, "covers");
        Directory.CreateDirectory(CoverDir);

        _conn = new SqliteConnection("Data Source=" + dbPath);
        _conn.Open();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA busy_timeout=5000;
                PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS books (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    title TEXT NOT NULL,
                    path TEXT NOT NULL,
                    type INTEGER NOT NULL,
                    format INTEGER NOT NULL,
                    size INTEGER NOT NULL DEFAULT 0,
                    fingerprint TEXT NOT NULL DEFAULT '',
                    page_count INTEGER NOT NULL DEFAULT 0,
                    chapter_count INTEGER NOT NULL DEFAULT 0,
                    cover_path TEXT,
                    progress REAL NOT NULL DEFAULT 0,
                    last_read TEXT,
                    added TEXT NOT NULL,
                    error TEXT
                );
                CREATE TABLE IF NOT EXISTS progress (
                    book_id INTEGER PRIMARY KEY,
                    page_index INTEGER NOT NULL DEFAULT 0,
                    scroll REAL NOT NULL DEFAULT 0,
                    chapter_index INTEGER NOT NULL DEFAULT 0,
                    mode TEXT NOT NULL DEFAULT '',
                    zoom REAL NOT NULL DEFAULT 0,
                    updated TEXT NOT NULL,
                    FOREIGN KEY(book_id) REFERENCES books(id) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS bookmarks (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    book_id INTEGER NOT NULL,
                    page_index INTEGER NOT NULL DEFAULT 0,
                    chapter_index INTEGER NOT NULL DEFAULT 0,
                    note TEXT NOT NULL DEFAULT '',
                    created TEXT NOT NULL,
                    FOREIGN KEY(book_id) REFERENCES books(id) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_books_type ON books(type);
                CREATE INDEX IF NOT EXISTS idx_books_last_read ON books(last_read);
                """;
            cmd.ExecuteNonQuery();
        }

        MigratePathUniqueIndex();

        // 旧库迁移：补充 mode / zoom 列
        foreach (string column in new[] { "mode TEXT NOT NULL DEFAULT ''", "zoom REAL NOT NULL DEFAULT 0" })
        {
            try
            {
                using var alter = _conn.CreateCommand();
                alter.CommandText = "ALTER TABLE progress ADD COLUMN " + column;
                alter.ExecuteNonQuery();
            }
            catch
            {
                // 列已存在则忽略
            }
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public Book? GetBook(long id)
    {
        return QueryBooks("WHERE id = $p", new SqliteParameter("$p", id)).FirstOrDefault();
    }

    /// <summary>
    /// 保证 books.path 唯一：同一路径只应有一条记录。
    ///
    /// 关键点：去重**只在旧库升级时执行一次**（PRAGMA user_version &lt; 2），并且只删
    /// "路径字符串完全相同（NOCASE）"的重复行。绝不能每次启动都删：
    /// 漫画常见的"第01话"与"第1话"是两条**不同的**路径，NOCASE 分组不会把它们算作重复；
    /// 但若用户先导入了 A、后又导入了同一路径的更新版本，则保留 id 最大（最新）的那条。
    /// </summary>
    private void MigratePathUniqueIndex()
    {
        int version = 0;
        try
        {
            using var ver = _conn.CreateCommand();
            ver.CommandText = "PRAGMA user_version";
            version = Convert.ToInt32(ver.ExecuteScalar() ?? 0);
        }
        catch (Exception ex)
        {
            Log.Error("读取 schema 版本失败", ex);
        }

        try
        {
            if (version < 2)
            {
                // 一次性清理：只对"完全相同的路径"去重，保留最新一条；
                // ON DELETE CASCADE 会一并删掉被丢弃行的 progress/bookmark。
                using var dedupe = _conn.CreateCommand();
                dedupe.CommandText = """
                    DELETE FROM books
                    WHERE id NOT IN (SELECT MAX(id) FROM books GROUP BY path COLLATE NOCASE);
                    """;
                dedupe.ExecuteNonQuery();
                Log.Info("已合并历史重复书库记录（同一路径只保留最新一条）");
            }

            using var idx = _conn.CreateCommand();
            idx.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_books_path ON books(path COLLATE NOCASE);";
            idx.ExecuteNonQuery();

            using var bump = _conn.CreateCommand();
            bump.CommandText = $"PRAGMA user_version={SchemaVersion}";
            bump.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // 迁移失败不应阻止启动：退化为「按路径查找」，只是少一层数据库级保护
            Log.Error("建立 books.path 唯一索引失败", ex);
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public Book? GetBookByFingerprint(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return null;
        return QueryBooks("WHERE fingerprint = $p", new SqliteParameter("$p", fingerprint)).FirstOrDefault();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public Book? GetBookByPath(string path)
    {
        return QueryBooks("WHERE path = $p", new SqliteParameter("$p", path)).FirstOrDefault();
    }

    /// <summary>
    /// 按路径查找，与 books.path 的唯一索引（COLLATE NOCASE）语义保持一致：
    /// Windows 路径大小写不敏感，"D:\A.cbz" 与 "d:\a.cbz" 必须被认作同一本书，
    /// 否则命中唯一索引后 UpsertBook 会找不到"已存在的行"从而把冲突抛给上层。
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public Book? GetBookByPathNoCase(string path)
    {
        return QueryBooks("WHERE path = $p COLLATE NOCASE", new SqliteParameter("$p", path)).FirstOrDefault();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<Book> GetBooks(string? search = null, string filter = "all", string sort = "recent", int limit = 0)
    {
        var where = new List<string>();
        var pars = new List<SqliteParameter>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(title LIKE $s OR path LIKE $s)");
            pars.Add(new SqliteParameter("$s", "%" + search.Trim() + "%"));
        }
        if (filter == "comic") where.Add("type = " + (int)BookType.Comic);
        else if (filter == "novel") where.Add("type = " + (int)BookType.Novel);

        string order = sort switch
        {
            "title" => "title COLLATE NOCASE",
            "added" => "added DESC, id DESC",
            _ => "COALESCE(last_read, added) DESC"
        };
        string sql = "WHERE " + (where.Count > 0 ? string.Join(" AND ", where) : "1=1") + " ORDER BY " + order;
        if (limit > 0) sql += " LIMIT " + limit;
        return QueryBooks(sql, pars.ToArray());
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<Book> GetRecent(int limit = 20)
    {
        return QueryBooks("WHERE last_read IS NOT NULL ORDER BY last_read DESC LIMIT $p",
            new SqliteParameter("$p", limit));
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public long UpsertBook(Book book)
    {
        using var cmd = _conn.CreateCommand();
        if (book.Id > 0)
        {
            cmd.CommandText = """
                UPDATE books SET title=$t, path=$path, type=$ty, format=$f, size=$s,
                    fingerprint=$fp, page_count=$pc, chapter_count=$cc, cover_path=$cv,
                    progress=$pr, last_read=$lr, error=$er
                WHERE id=$id
                """;
            cmd.Parameters.AddWithValue("$id", book.Id);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO books(title, path, type, format, size, fingerprint, page_count,
                    chapter_count, cover_path, progress, last_read, added, error)
                VALUES($t, $path, $ty, $f, $s, $fp, $pc, $cc, $cv, $pr, $lr, $ad, $er)
                """;
            cmd.Parameters.AddWithValue("$ad", book.AddedTime == default ? DateTime.Now : book.AddedTime);
        }
        cmd.Parameters.AddWithValue("$t", book.Title);
        cmd.Parameters.AddWithValue("$path", book.FilePath);
        cmd.Parameters.AddWithValue("$ty", (int)book.Type);
        cmd.Parameters.AddWithValue("$f", (int)book.Format);
        cmd.Parameters.AddWithValue("$s", book.FileSize);
        cmd.Parameters.AddWithValue("$fp", book.Fingerprint);
        cmd.Parameters.AddWithValue("$pc", book.PageCount);
        cmd.Parameters.AddWithValue("$cc", book.ChapterCount);
        cmd.Parameters.AddWithValue("$cv", (object?)book.CoverPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pr", book.Progress);
        cmd.Parameters.AddWithValue("$lr", book.LastReadTime == default ? DBNull.Value : book.LastReadTime);
        cmd.Parameters.AddWithValue("$er", (object?)book.Error ?? DBNull.Value);
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT */ && book.Id <= 0)
        {
            // books.path 有唯一索引：同一路径被两个导入流程（或"双击文件关联 + 拖拽"）同时插入时会撞索引。
            // 此时不能崩，改为更新已存在的那一条并返回它的 id。
            // 注意大小写：唯一索引是 NOCASE，"D:\A.CBZ" 与 "d:\a.cbz" 是同一行，必须用 NOCASE 查询才能找到它。
            var existing = GetBookByPathNoCase(book.FilePath);
            if (existing is null) throw;   // 不是路径唯一性冲突，交给上层处理
            Log.Info($"书库中已存在同一路径，改为更新: {book.FilePath}");
            existing.Title = book.Title;
            existing.FilePath = book.FilePath;
            existing.FileSize = book.FileSize;
            existing.PageCount = book.PageCount;
            existing.ChapterCount = book.ChapterCount;
            existing.Fingerprint = book.Fingerprint;
            existing.Format = book.Format;
            existing.Error = null;
            if (!string.IsNullOrEmpty(book.CoverPath)) existing.CoverPath = book.CoverPath;
            return UpsertBook(existing);   // 递归调用会走 Id>0 的 UPDATE 分支
        }

        if (book.Id > 0) return book.Id;
        cmd.CommandText = "SELECT last_insert_rowid()";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>把书的源文件标记为"失联"（文件被删除/移动/改名）。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void MarkMissing(long id, string error = "源文件已不存在，可能被移动、重命名或删除")
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE books SET error=$e WHERE id=$id AND (error IS NULL OR error<>$e)";
        cmd.Parameters.AddWithValue("$e", error);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>清除"失联"标记（文件回来了，例如插回了移动硬盘）。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ClearMissing(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE books SET error=NULL WHERE id=$id AND error=$e";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$e", MissingErrorText);
        cmd.ExecuteNonQuery();
    }

    public const string MissingErrorText = "源文件已不存在，可能被移动、重命名或删除";

    /// <summary>
    /// 轻量校验：把文件/文件夹已不存在的书记为失联、又回来时清掉标记。
    /// 用一次 UPDATE ... WHERE id IN (...) 批量写入，避免书架刷新时逐本写库。
    /// 返回本次新发现失联的书的数量。
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int EnsureFilesPresent(IEnumerable<Book> books)
    {
        var missing = new List<long>();
        var found = new List<long>();
        foreach (var b in books)
        {
            if (string.IsNullOrWhiteSpace(b.FilePath)) continue;
            bool exists;
            try
            {
                // 文件夹书 → Directory.Exists；文件书 → File.Exists（对不存在的路径都返回 false，无需区分）
                exists = File.Exists(b.FilePath) || Directory.Exists(b.FilePath);
            }
            catch
            {
                exists = true;   // 路径过长/权限异常等：不要误判为失联
            }

            if (!exists)
            {
                missing.Add(b.Id);
                b.Error = MissingErrorText;
            }
            else if (b.Error == MissingErrorText)
            {
                found.Add(b.Id);
                b.Error = null;
            }
        }

        if (missing.Count > 0)
        {
            RunIdListUpdate("UPDATE books SET error=$e WHERE id IN (", missing, "$e", MissingErrorText);
            Log.Info($"发现 {missing.Count} 本书的源文件不存在，已在书架标记");
        }
        if (found.Count > 0)
            RunIdListUpdate("UPDATE books SET error=NULL WHERE id IN (", found, null, null);
        return missing.Count;
    }

    private void RunIdListUpdate(string prefix, List<long> ids, string? paramName, string? paramValue)
    {
        foreach (var chunk in Chunk(ids, 500))
        {
            using var cmd = _conn.CreateCommand();
            var names = new string[chunk.Count];
            for (int i = 0; i < chunk.Count; i++)
            {
                names[i] = "$p" + i;
                cmd.Parameters.AddWithValue(names[i], chunk[i]);
            }
            cmd.CommandText = prefix + string.Join(",", names) + ")";
            if (paramName is not null) cmd.Parameters.AddWithValue(paramName, paramValue!);
            try
            {
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Log.Error("更新失联标记失败", ex);
                return;
            }
        }
    }

    private static IEnumerable<List<long>> Chunk(List<long> source, int size)
    {
        for (int i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }

    /// <summary>
    /// 事务性批量操作：一批导入在同一个事务里提交，中途失败整体回滚，
    /// 避免"导了一半"的半成品书架；也显著减少 WAL commit 次数。
    /// 本类型的所有命令共用同一个连接，因此本连接上的语句会自动加入当前事务。
    /// 返回的句柄 Dispose 时若未 Commit 则回滚。
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public TransactionScope BeginTransaction() => new(_conn.BeginTransaction());

    public sealed class TransactionScope : IDisposable
    {
        private readonly SqliteTransaction? _tx;
        private bool _committed;
        public TransactionScope(SqliteTransaction tx) => _tx = tx;

        public void Commit()
        {
            if (_committed) return;
            _tx?.Commit();
            _committed = true;
        }

        public void Dispose()
        {
            try
            {
                if (!_committed) _tx?.Rollback();
                _tx?.Dispose();
            }
            catch
            {
                // 回滚失败不应影响主流程（连接关闭时 SQLite 也会自动回滚）
            }
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RemoveBook(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM books WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public ReadingProgress? GetProgress(long bookId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT page_index, scroll, chapter_index, mode, zoom, updated FROM progress WHERE book_id=$id";
        cmd.Parameters.AddWithValue("$id", bookId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ReadingProgress
        {
            BookId = bookId,
            PageIndex = r.GetInt32(0),
            ScrollOffset = r.GetDouble(1),
            ChapterIndex = r.GetInt32(2),
            Mode = r.IsDBNull(3) ? "" : r.GetString(3),
            Zoom = r.IsDBNull(4) ? 0 : r.GetDouble(4),
            UpdatedAt = DateTime.TryParse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.Now
        };
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SaveProgress(long bookId, double progress, int pageIndex, double scroll, int chapterIndex,
        string mode = "", double zoom = 0)
    {
        var now = DateTime.Now;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO progress(book_id, page_index, scroll, chapter_index, mode, zoom, updated)
            VALUES($id, $pi, $sc, $ci, $mo, $zo, $up)
            ON CONFLICT(book_id) DO UPDATE SET
                page_index=$pi, scroll=$sc, chapter_index=$ci, mode=$mo, zoom=$zo, updated=$up;
            UPDATE books SET progress=$pr, last_read=$up WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", bookId);
        cmd.Parameters.AddWithValue("$pi", pageIndex);
        cmd.Parameters.AddWithValue("$sc", scroll);
        cmd.Parameters.AddWithValue("$ci", chapterIndex);
        cmd.Parameters.AddWithValue("$mo", mode);
        cmd.Parameters.AddWithValue("$zo", zoom);
        cmd.Parameters.AddWithValue("$up", now.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$pr", progress);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 只重置阅读位置，保留用户选择的阅读模式/缩放。
    /// （旧实现复用 SaveProgress 的默认参数，会把 mode/zoom 一并清空。）
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ResetProgress(long bookId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE progress SET page_index=0, scroll=0, chapter_index=0 WHERE book_id=$id;
            UPDATE books SET progress=0 WHERE id=$id;
            """;
        cmd.Parameters.AddWithValue("$id", bookId);
        cmd.ExecuteNonQuery();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void AddBookmark(Bookmark bm)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO bookmarks(book_id, page_index, chapter_index, note, created) VALUES($b,$p,$c,$n,$t)";
        cmd.Parameters.AddWithValue("$b", bm.BookId);
        cmd.Parameters.AddWithValue("$p", bm.PageIndex);
        cmd.Parameters.AddWithValue("$c", bm.ChapterIndex);
        cmd.Parameters.AddWithValue("$n", bm.Note);
        cmd.Parameters.AddWithValue("$t", (bm.CreatedAt == default ? DateTime.Now : bm.CreatedAt).ToString("o", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<Bookmark> GetBookmarks(long bookId)
    {
        var result = new List<Bookmark>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, page_index, chapter_index, note, created FROM bookmarks WHERE book_id=$b ORDER BY created DESC";
        cmd.Parameters.AddWithValue("$b", bookId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new Bookmark
            {
                Id = r.GetInt64(0),
                BookId = bookId,
                PageIndex = r.GetInt32(1),
                ChapterIndex = r.GetInt32(2),
                Note = r.GetString(3),
                CreatedAt = DateTime.TryParse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.Now
            });
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void DeleteBookmark(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM bookmarks WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public string? GetSetting(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>一次性读取全部设置（AppSettingsService 用它建内存缓存，避免每次读设置都查库）。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public Dictionary<string, string> GetSettings()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM settings";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!r.IsDBNull(0) && !r.IsDBNull(1))
                result[r.GetString(0)] = r.GetString(1);
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetSetting(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key, value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Backup(string? targetPath = null)
    {
        targetPath ??= Path.Combine(Path.GetDirectoryName(DbPath)!, $"library-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        // 不能用 File.Copy：库以 WAL 模式运行，数据在 -wal 里，直接拷主库会得到一个空壳。
        // VACUUM INTO 在当前连接上生成一致快照，天然包含 WAL 中未 checkpoint 的数据。
        if (File.Exists(targetPath)) File.Delete(targetPath);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $p";
        cmd.Parameters.AddWithValue("$p", targetPath);
        cmd.ExecuteNonQuery();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    private List<Book> QueryBooks(string where, params SqliteParameter[] pars)
    {
        var result = new List<Book>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, path, type, format, size, fingerprint, page_count, chapter_count, cover_path, progress, last_read, added, error FROM books " + where;
        foreach (var p in pars) cmd.Parameters.Add(p);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new Book
            {
                Id = r.GetInt64(0),
                Title = r.GetString(1),
                FilePath = r.GetString(2),
                Type = (BookType)r.GetInt32(3),
                Format = (BookFormat)r.GetInt32(4),
                FileSize = r.GetInt64(5),
                Fingerprint = r.GetString(6),
                PageCount = r.GetInt32(7),
                ChapterCount = r.GetInt32(8),
                CoverPath = r.IsDBNull(9) ? null : r.GetString(9),
                Progress = r.GetDouble(10),
                LastReadTime = r.IsDBNull(11) ? default : DateTime.TryParse(r.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : default,
                AddedTime = DateTime.TryParse(r.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var a) ? a : DateTime.Now,
                Error = r.IsDBNull(13) ? null : r.GetString(13)
            });
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Dispose()
    {
        _conn.Dispose();
    }
}
