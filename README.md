# ComicVerse — 二次元漫画 & 轻小说阅读器

基于 PRD（`ComicVerse-PRD-v1.0.md`）实现的第一版 Windows 桌面阅读器（C# / .NET 10 / WPF）。

## 功能一览

### 书架（书库）
- 拖拽文件/文件夹到窗口导入，或通过「添加文件 / 添加文件夹」导入
- 支持格式：CBZ/ZIP、CBR/RAR、CBT/TAR、CB7/7Z、PDF、图片文件夹、TXT、EPUB
- 混合文件夹自动识别：同一文件夹内的图片归入漫画、TXT/EPUB 单独归入小说
- 网格 / 列表两种视图，封面缩略图 + 进度条 + 最近阅读时间
- 按「全部 / 漫画 / 小说」筛选，按「最近阅读 / 书名 / 添加时间」排序，书名模糊搜索
- 导入去重：按「文件大小 + 首尾 1MB 哈希」指纹，文件移动后进度仍可匹配（PRD Q7 方案）

### 漫画阅读
- 三种模式：翻页（单页）、条漫（纵向连续）、双页（跨页；宽度大于高度的图单独一页）
- 缩放：适应宽度 / 适应整页 / 原始大小 / 自定义滑块（50%–300%），Ctrl+滚轮缩放
- 翻页方向：支持日漫右→左模式切换
- 大文件策略：按需解压 + 后台预取 ±4 页 + LRU 内存缓存（上限可在设置中调整，默认 400MB）
- 翻页淡入过渡动画（180ms）

### 小说阅读
- TXT 自动检测编码（UTF-8 / GBK / Big5 / Shift-JIS），可在阅读器内手动覆盖
- EPUB 解析正文与内嵌图片，生成章节目录
- 翻页 / 滚动两种阅读方式，章节目录跳转
- 排版自定义：字体、字号（12–32px）、行距（1.2–2.5）、段间距、页边距、文字/背景色
- 自动滚动阅读（可调速，滚动/点击即停）

### 进度与书签
- 阅读进度自动保存（防抖 2 秒），关闭或异常退出后重开自动恢复（精确到页/段落）
- 书架显示每本书进度百分比；最近阅读按时间倒序
- 书签：任意页添加书签，列表跳转/删除

### 视觉
- 二次元风格：粉紫渐变主色（#FF6B9D → #C44CEC）+ 深蓝紫底色（#1A1A2E）
- 启动画面（加载进度动画）、深浅主题一键切换

## 运行

### 直接运行
```powershell
dotnet run --project src/ComicVerse.App/ComicVerse.App.csproj
```

发布版：
```powershell
dotnet publish src/ComicVerse.App/ComicVerse.App.csproj -c Release -r win-x64 --self-contained false -o publish
.\publish\ComicVerse.exe
```

也可以在「设置 → 注册文件关联」后直接双击 `.cbz/.cbr/.cbt/.cb7/.pdf/.txt/.epub` 文件打开。

### 首次体验
项目自带了样例资源（`samples/` 目录），可以直接把整个 `samples` 文件夹拖进窗口，或运行：
```powershell
dotnet run --project tests/ComicVerse.Tests/ComicVerse.Tests.csproj -- --samples
```

## 测试与自检

核心功能自动化测试（格式解析、编码检测、进度持久化、封面、缓存等）：
```powershell
dotnet run --project tests/ComicVerse.Tests/ComicVerse.Tests.csproj
```

UI 冒烟自检（独立数据目录，自动导入样例并打开三种漫画模式与小说阅读器，生成截图）：
```powershell
$env:COMICVERSE_DATA_DIR = "$env:TEMP\cv-smoke"
$env:SMOKE_OUT_DIR = "$env:TEMP\cv-smoke-out"
dotnet run --project src/ComicVerse.App/ComicVerse.App.csproj -- --smoke .\samples
```

## 快捷键

| 按键 | 功能 |
|------|------|
| ← / →（或点击画面左/右） | 上一页 / 下一页（日漫模式下反向） |
| PageUp / PageDown / Space | 上/下一页 |
| Home / End | 首页 / 末页 |
| ↑ / ↓ | 条漫与小说滚动模式上下滚动 |
| +/- | 缩放（漫画） |
| 0 | 适应整页 |
| F | 全屏 |
| B | 书签 |
| M / Esc | 隐藏/显示工具栏 |

## 数据位置

- 书库数据库与封面缓存：`%LOCALAPPDATA%\ComicVerse\`
- 日志：`%LOCALAPPDATA%\ComicVerse\logs\app.log`

## 已知限制

- RAR 依赖 SharpCompress（纯托管实现），超大 RAR 解压速度低于原生 unrar；损坏包会给出友好错误提示
- WebP/AVIF 图片依赖 Windows 自带编解码器，若系统缺少对应组件会提示「无法解码」
- EPUB 按章节分页（章节内页码从 1 开始），连续整本页码是后续迭代项
- 书签仅支持“页/章节”级定位，无批注文本编辑（P1-02 的部分实现）
