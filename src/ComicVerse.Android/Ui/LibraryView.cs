using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using ComicVerse.Core;
using ComicVerse.Core.Models;
using Path = System.IO.Path;

namespace ComicVerse.Droid.Ui;

/// <summary>书架界面：网格/列表两种视图、筛选、排序、搜索与导入入口。</summary>
public class LibraryView : LinearLayout
{
    private readonly Activity _activity;
    private readonly List<Book> _books = new();
    private readonly Dictionary<string, Bitmap> _coverCache = new();
    private readonly LinkedList<string> _coverOrder = new();
    private readonly Handler _handler = new(Android.OS.Looper.MainLooper!);

    private string _filter = "all";
    private string _sort = "recent";
    private bool _gridMode = true;
    private string _search = "";

    private FrameLayout _contentHost = null!;
    private GridView _grid = null!;
    private ListView _list = null!;
    private TextView _status = null!;
    private TextView _empty = null!;
    private EditText _searchBox = null!;
    private BookAdapter _gridAdapter = null!;
    private BookAdapter _listAdapter = null!;
    private Java.Lang.Runnable? _pendingSearch;

    public Action<Book>? BookOpened { get; set; }
    public Action<Book>? BookOpenedFromStart { get; set; }
    public Action? AddFilesRequested { get; set; }
    public Action? AddFolderRequested { get; set; }
    public Action? SettingsRequested { get; set; }
    public Action? ThemeToggleRequested { get; set; }

    public LibraryView(Activity activity) : base(activity)
    {
        _activity = activity;
        Orientation = Orientation.Vertical;
        SetBackgroundColor(AppTheme.WindowBg);
        Build();
    }

    private void Build()
    {
        RemoveAllViews();

        // 顶部标题栏
        var header = new LinearLayout(Context!) { Orientation = Orientation.Horizontal };
        header.Background = UiKit.HeaderGradient();
        header.SetPadding(UiKit.Dp(16), UiKit.Dp(14), UiKit.Dp(12), UiKit.Dp(12));
        header.SetGravity(GravityFlags.CenterVertical);

        var titleBox = new LinearLayout(Context!) { Orientation = Orientation.Vertical };
        var title = UiKit.Text(Context!, "ComicVerse", 19, AppTheme.TextPrimary, true);
        var subtitle = UiKit.Text(Context!, "漫画与轻小说阅读器", 11, AppTheme.TextSecondary);
        titleBox.AddView(title);
        titleBox.AddView(subtitle);
        header.AddView(titleBox, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent) { Weight = 1f });

        var themeButton = UiKit.IconButton(Context!, AppTheme.Dark ? "☀" : "☾");
        themeButton.Click += (_, _) => ThemeToggleRequested?.Invoke();
        header.AddView(themeButton);

        var settingsButton = UiKit.IconButton(Context!, "⚙");
        settingsButton.Click += (_, _) => SettingsRequested?.Invoke();
        var settingsParams = new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        settingsParams.LeftMargin = UiKit.Dp(8);
        header.AddView(settingsButton, settingsParams);
        AddView(header);

        // 工具行
        var toolbar = new LinearLayout(Context!) { Orientation = Orientation.Horizontal };
        toolbar.SetPadding(UiKit.Dp(12), UiKit.Dp(10), UiKit.Dp(12), UiKit.Dp(6));
        toolbar.SetGravity(GravityFlags.CenterVertical);

        var addFiles = UiKit.Chip(Context!, "＋ 添加文件", true, 13);
        addFiles.Click += (_, _) => AddFilesRequested?.Invoke();
        toolbar.AddView(addFiles);

        var addFolder = UiKit.Chip(Context!, "添加文件夹", false, 13);
        addFolder.Click += (_, _) => AddFolderRequested?.Invoke();
        var folderParams = new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        folderParams.LeftMargin = UiKit.Dp(8);
        toolbar.AddView(addFolder, folderParams);

        toolbar.AddView(new Space(Context!), new LayoutParams(0, 1) { Weight = 1f });

        var viewToggle = UiKit.Chip(Context!, _gridMode ? "▦ 网格" : "☰ 列表", false, 12.5);
        viewToggle.Click += (_, _) =>
        {
            _gridMode = !_gridMode;
            AppServices.Settings.LibraryView = _gridMode ? "grid" : "list";
            Build();
            Refresh();
        };
        toolbar.AddView(viewToggle);
        AddView(toolbar);

        // 筛选 / 排序 / 搜索
        var filters = new LinearLayout(Context!) { Orientation = Orientation.Horizontal };
        filters.SetPadding(UiKit.Dp(12), 0, UiKit.Dp(12), UiKit.Dp(8));
        filters.SetGravity(GravityFlags.CenterVertical);

        filters.AddView(FilterChip("全部", "all"));
        var comic = FilterChip("漫画", "comic");
        var novel = FilterChip("小说", "novel");
        var comicParams = new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        comicParams.LeftMargin = UiKit.Dp(6);
        filters.AddView(comic, comicParams);
        var novelParams = new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        novelParams.LeftMargin = UiKit.Dp(6);
        filters.AddView(novel, novelParams);

        var sortChip = UiKit.Chip(Context!, SortLabel(), false, 12.5);
        sortChip.Click += (_, _) => ShowSortDialog(sortChip);
        var sortParams = new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        sortParams.LeftMargin = UiKit.Dp(6);
        filters.AddView(sortChip, sortParams);

        _searchBox = new EditText(Context!)
        {
            Hint = "搜索书名或路径",
            TextSize = 13
        };
        _searchBox.SetTextColor(AppTheme.TextPrimary);
        _searchBox.SetHintTextColor(AppTheme.TextFaint);
        _searchBox.Background = UiKit.RoundStroke(AppTheme.SurfaceAlt, 8, AppTheme.Border, 1);
        _searchBox.SetPadding(UiKit.Dp(10), UiKit.Dp(6), UiKit.Dp(10), UiKit.Dp(6));
        _searchBox.SetSingleLine(true);
        _searchBox.TextChanged += (_, _) =>
        {
            _search = _searchBox.Text ?? "";
            if (_pendingSearch is not null) _handler.RemoveCallbacks(_pendingSearch);
            _pendingSearch = new Java.Lang.Runnable(() =>
            {
                _pendingSearch = null;
                Refresh();
            });
            _handler.PostDelayed(_pendingSearch, 280);
        };
        var searchParams = new LayoutParams(0, ViewGroup.LayoutParams.WrapContent) { Weight = 1f };
        searchParams.LeftMargin = UiKit.Dp(10);
        filters.AddView(_searchBox, searchParams);
        AddView(filters);

        // 内容区
        _contentHost = new FrameLayout(Context!);
        _gridAdapter = new BookAdapter(_activity, _books, true, GetCover);
        _listAdapter = new BookAdapter(_activity, _books, false, GetCover);

        _grid = new GridView(Context!);
        _grid.SetNumColumns(-1);                          // AUTO_FIT：按列宽自动决定列数
        _grid.SetColumnWidth(UiKit.Dp(154));
        _grid.SetHorizontalSpacing(UiKit.Dp(4));
        _grid.SetVerticalSpacing(UiKit.Dp(8));
        _grid.SetClipToPadding(false);
        _grid.SetAdapter(_gridAdapter);
        _grid.VerticalScrollBarEnabled = false;
        _grid.SetPadding(UiKit.Dp(10), UiKit.Dp(4), UiKit.Dp(10), UiKit.Dp(12));
        _grid.ItemClick += (_, e) => OpenBook(_books[(int)e.Position]);
        _grid.ItemLongClick += (_, e) => { ShowBookMenu(_books[(int)e.Position]); e.Handled = true; };
        _contentHost.AddView(_grid, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _list = new ListView(Context!);
        _list.SetAdapter(_listAdapter);
        _list.SetClipToPadding(false);
        _list.DividerHeight = 0;
        _list.VerticalScrollBarEnabled = false;
        _list.SetPadding(UiKit.Dp(12), UiKit.Dp(6), UiKit.Dp(12), UiKit.Dp(12));
        _list.ItemClick += (_, e) => OpenBook(_books[(int)e.Position]);
        _list.ItemLongClick += (_, e) => { ShowBookMenu(_books[(int)e.Position]); e.Handled = true; };
        _contentHost.AddView(_list, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _empty = UiKit.Text(Context!, "书架还是空的\n点「添加文件」导入漫画或小说", 14, AppTheme.TextSecondary);
        _empty.Gravity = GravityFlags.Center;
        var emptyParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Center
        };
        _contentHost.AddView(_empty, emptyParams);

        AddView(_contentHost, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0) { Weight = 1f });

        // 状态栏
        var statusBar = new LinearLayout(Context!) { Orientation = Orientation.Horizontal };
        statusBar.SetBackgroundColor(AppTheme.Surface);
        statusBar.SetPadding(UiKit.Dp(16), UiKit.Dp(8), UiKit.Dp(16), UiKit.Dp(8));
        _status = UiKit.Text(Context!, "共 0 本书", 11.5, AppTheme.TextSecondary);
        statusBar.AddView(_status);
        AddView(statusBar);
    }

    private TextView FilterChip(string label, string value)
    {
        var chip = UiKit.Chip(Context!, label, _filter == value, 12.5);
        chip.Click += (_, _) =>
        {
            _filter = value;
            Build();
            Refresh();
        };
        return chip;
    }

    private string SortLabel() => _sort switch
    {
        "title" => "排序：书名",
        "added" => "排序：添加时间",
        _ => "排序：最近阅读"
    };

    private void ShowSortDialog(TextView chip)
    {
        var options = new[] { "最近阅读", "书名", "添加时间" };
        new AlertDialog.Builder(_activity)!
            .SetTitle("排序方式")!
            .SetItems(options, (_, e) =>
            {
                _sort = e.Which switch { 1 => "title", 2 => "added", _ => "recent" };
                chip.Text = SortLabel();
                Refresh();
            })!
            .Show();
    }

    public void Refresh()
    {
        _books.Clear();
        _books.AddRange(AppServices.Library.GetBooks(_search.Trim(), _filter, _sort));
        _gridAdapter.NotifyDataSetChanged();
        _listAdapter.NotifyDataSetChanged();

        bool empty = _books.Count == 0;
        _empty.Visibility = empty ? ViewStates.Visible : ViewStates.Gone;
        _grid.Visibility = !empty && _gridMode ? ViewStates.Visible : ViewStates.Gone;
        _list.Visibility = !empty && !_gridMode ? ViewStates.Visible : ViewStates.Gone;
        _status.Text = $"共 {_books.Count} 本书 · 图片缓存 {AppServices.Cache.EstimatedBytes / (1024 * 1024)} MB";
    }

    public void SetStatus(string text) => _status.Text = text;

    private void OpenBook(Book book) => BookOpened?.Invoke(book);

    private void ShowBookMenu(Book book)
    {
        var items = new[]
        {
            "继续阅读", "从头开始阅读", "重置阅读进度",
            "移出书架（保留文件）", "移出书架并删除文件"
        };
        new AlertDialog.Builder(_activity)!
            .SetTitle(book.Title)!
            .SetItems(items, (_, e) =>
            {
                switch (e.Which)
                {
                    case 0:
                        OpenBook(book);
                        break;
                    case 1:
                        AppServices.Library.SaveProgress(book.Id, 0, 0, 0, 0);
                        Refresh();
                        BookOpenedFromStart?.Invoke(book);
                        break;
                    case 2:
                        AppServices.Library.SaveProgress(book.Id, 0, 0, 0, 0);
                        Refresh();
                        break;
                    case 3:
                        AppServices.Library.RemoveBook(book.Id);
                        Refresh();
                        break;
                    case 4:
                        AppServices.Library.RemoveBook(book.Id);
                        try
                        {
                            if (Directory.Exists(book.FilePath)) Directory.Delete(book.FilePath, true);
                            else if (File.Exists(book.FilePath)) File.Delete(book.FilePath);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("删除文件失败", ex);
                        }
                        Refresh();
                        break;
                }
            })!
            .Show();
    }

    private Bitmap? GetCover(Book book)
    {
        string? path = book.CoverPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        if (_coverCache.TryGetValue(path, out var cached)) return cached;

        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeFile(path, bounds);
        int sample = 1;
        while (bounds.OutWidth / (sample * 2) >= 320 && bounds.OutHeight / (sample * 2) >= 440)
            sample *= 2;
        var options = new BitmapFactory.Options { InSampleSize = sample };
        var bitmap = BitmapFactory.DecodeFile(path, options);
        if (bitmap is null) return null;

        _coverCache[path] = bitmap;
        _coverOrder.AddLast(path);
        while (_coverOrder.Count > 64)
        {
            string oldest = _coverOrder.First!.Value;
            _coverOrder.RemoveFirst();
            _coverCache.Remove(oldest);
        }
        return bitmap;
    }

    public void ClearCoverCache()
    {
        _coverCache.Clear();
        _coverOrder.Clear();
    }

    private sealed class BookAdapter : BaseAdapter<Book>
    {
        private readonly Activity _activity;
        private readonly List<Book> _books;
        private readonly bool _grid;
        private readonly Func<Book, Bitmap?> _cover;

        public BookAdapter(Activity activity, List<Book> books, bool grid, Func<Book, Bitmap?> cover)
        {
            _activity = activity;
            _books = books;
            _grid = grid;
            _cover = cover;
        }

        public override int Count => _books.Count;
        public override Book this[int position] => _books[position];
        public override long GetItemId(int position) => _books[position].Id;

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var book = _books[position];
            return _grid ? BuildCard(book) : BuildRow(book);
        }

        private View BuildCard(Book book)
        {
            var card = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
            card.Background = UiKit.RoundStroke(AppTheme.Surface, 12, AppTheme.Border, 1);
            card.ClipToOutline = true;
            card.LayoutParameters = new AbsListView.LayoutParams(ViewGroup.LayoutParams.MatchParent, UiKit.Dp(232));

            var coverHost = new FrameLayout(_activity);
            coverHost.SetBackgroundColor(AppTheme.SurfaceAlt);
            var image = new ImageView(_activity) { };
            image.SetScaleType(ImageView.ScaleType.CenterCrop!);
            var bitmap = _cover(book);
            if (bitmap is not null) image.SetImageBitmap(bitmap);
            coverHost.AddView(image, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

            var badge = UiKit.Text(_activity, book.FormatText, 10, Color.White);
            badge.Background = UiKit.Round(Color.Argb(180, 20, 20, 36), 8);
            badge.SetPadding(UiKit.Dp(7), UiKit.Dp(2), UiKit.Dp(7), UiKit.Dp(2));
            var badgeParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.Top | GravityFlags.Right
            };
            badgeParams.TopMargin = UiKit.Dp(8);
            badgeParams.RightMargin = UiKit.Dp(8);
            coverHost.AddView(badge, badgeParams);
            card.AddView(coverHost, new LayoutParams(ViewGroup.LayoutParams.MatchParent, UiKit.Dp(146)));

            var info = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
            info.SetPadding(UiKit.Dp(10), UiKit.Dp(8), UiKit.Dp(10), UiKit.Dp(8));

            var name = UiKit.Text(_activity, book.Title, 12.5, AppTheme.TextPrimary, true);
            name.SetMaxLines(2);
            name.Ellipsize = TextUtils.TruncateAt.End;
            info.AddView(name);

            var progress = new ProgressBar(_activity, null, Android.Resource.Attribute.ProgressBarStyleHorizontal)
            {
                Max = 1000,
                Progress = (int)Math.Round(book.Progress * 1000)
            };
            progress.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(AppTheme.Accent);
            progress.ProgressBackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(AppTheme.ProgressTrack);
            var progressParams = new LayoutParams(ViewGroup.LayoutParams.MatchParent, UiKit.Dp(5));
            progressParams.TopMargin = UiKit.Dp(8);
            info.AddView(progress, progressParams);

            var caption = UiKit.Text(_activity, $"{book.ProgressText} · {LastRead(book)}", 10, AppTheme.TextFaint);
            var captionParams = new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            captionParams.TopMargin = UiKit.Dp(5);
            info.AddView(caption, captionParams);

            card.AddView(info);
            return card;
        }

        private View BuildRow(Book book)
        {
            var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
            row.Background = UiKit.RoundStroke(AppTheme.Surface, 10, AppTheme.Border, 1);
            row.SetPadding(UiKit.Dp(10), UiKit.Dp(8), UiKit.Dp(10), UiKit.Dp(8));
            row.SetGravity(GravityFlags.CenterVertical);
            row.LayoutParameters = new AbsListView.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

            var thumb = new ImageView(_activity);
            thumb.SetScaleType(ImageView.ScaleType.CenterCrop!);
            var bitmap = _cover(book);
            if (bitmap is not null) thumb.SetImageBitmap(bitmap);
            thumb.SetBackgroundColor(AppTheme.SurfaceAlt);
            row.AddView(thumb, new LayoutParams(UiKit.Dp(44), UiKit.Dp(60)));

            var info = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
            var infoParams = new LayoutParams(0, ViewGroup.LayoutParams.WrapContent) { Weight = 1f };
            infoParams.LeftMargin = UiKit.Dp(12);
            var name = UiKit.Text(_activity, book.Title, 14, AppTheme.TextPrimary, true);
            name.Ellipsize = TextUtils.TruncateAt.End;
            name.SetSingleLine(true);
            info.AddView(name);
            var sub = UiKit.Text(_activity, $"{(book.Type == BookType.Comic ? "漫画" : "小说")} · {book.FormatText} · {book.ProgressText}", 11.5, AppTheme.TextSecondary);
            var subParams = new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            subParams.TopMargin = UiKit.Dp(4);
            info.AddView(sub, subParams);
            var path = UiKit.Text(_activity, Path.GetFileName(book.FilePath), 10.5, AppTheme.TextFaint);
            path.Ellipsize = TextUtils.TruncateAt.Middle;
            path.SetSingleLine(true);
            info.AddView(path);
            row.AddView(info, infoParams);

            var last = UiKit.Text(_activity, LastRead(book), 10.5, AppTheme.TextFaint);
            last.Gravity = GravityFlags.Right;
            row.AddView(last);
            return row;
        }

        private static string LastRead(Book book)
        {
            if (book.LastReadTime == default) return "未读";
            var span = DateTime.Now - book.LastReadTime;
            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
            return book.LastReadTime.ToString("yyyy-MM-dd");
        }
    }
}
