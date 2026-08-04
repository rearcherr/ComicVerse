using System.Globalization;
using System.IO;
using ComicVerse.Core.Models;
using Microsoft.Data.Sqlite;

namespace ComicVerse.Core.Services;

/// <summary>本地书架数据库（SQLite）：书籍、进度、书签、设置。</summary>
public sealed class LibraryService : IDisposable
{
    private readonly SqliteConnection _conn;

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
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
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

    public Book? GetBook(long id)
    {
        return QueryBooks("WHERE id = $p", new SqliteParameter("$p", id)).FirstOrDefault();
    }

    public Book? GetBookByFingerprint(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return null;
        return QueryBooks("WHERE fingerprint = $p", new SqliteParameter("$p", fingerprint)).FirstOrDefault();
    }

    public Book? GetBookByPath(string path)
    {
        return QueryBooks("WHERE path = $p", new SqliteParameter("$p", path)).FirstOrDefault();
    }

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

    public List<Book> GetRecent(int limit = 20)
    {
        return QueryBooks("WHERE last_read IS NOT NULL ORDER BY last_read DESC LIMIT $p",
            new SqliteParameter("$p", limit));
    }

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
        cmd.ExecuteNonQuery();

        if (book.Id > 0) return book.Id;
        cmd.CommandText = "SELECT last_insert_rowid()";
        return (long)cmd.ExecuteScalar()!;
    }

    public void RemoveBook(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM books WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

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

    public void DeleteBookmark(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM bookmarks WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public string? GetSetting(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key, value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public void Backup(string? targetPath = null)
    {
        targetPath ??= Path.Combine(Path.GetDirectoryName(DbPath)!, $"library-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        File.Copy(DbPath, targetPath, overwrite: true);
    }

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

    public void Dispose()
    {
        _conn.Dispose();
    }
}
