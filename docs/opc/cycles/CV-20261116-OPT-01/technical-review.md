# CV-20261116-OPT-01 技术验证：漫画精确页号跳转

日期：2026-11-16；角色：tech_architect；需求：REQ-CV-20261116-OPT-01-01（IDEA-003 子集）；交接：leader、desktop_engineer、qa_acceptance。

## 结论与实施边界

**可以立即开发页号输入入口，现有三种漫画模式已有可复用导航能力。** 用户可输入任意页号的 UI 尚不存在。最小实现为新增 `src/ComicVerse.App/Windows/ReaderWindow.ComicPageJump.cs`，加上原 `ReaderWindow.xaml`、`ReaderWindow.xaml.cs` 的必要接线；不改 Core、数据库、图片加载器或既有 canonical 算法。不扩展缩略图、卷间导航、小说定位或新导航引擎。

保留 `PageInfoText` 类型、名称和成功位置语义；漫画以按钮包裹页号文本，点击或 Ctrl+G 打开小浮层。小说仍显示原文本且不提供跳页。浮层显示 `1..PageCount`，提供输入、跳转和取消；Enter 应用，Esc 取消，严格整数验证。推荐窗口内 `Border` 浮层，避免新增窗口／原生 `Popup` 所带来的独立焦点和键盘路由问题。

**当前批准范围无架构 blocker。TA-B01 已由 PRD 与 leader 明确边界，作为静态风险留待办。** 坏图成功事实保护限定单页／双页；条漫按真实视口 offset 保留既有行为，不宣称目标图片解码成功保护。条漫目前只有布局就绪与真实滚动 offset 到达判据，尚无目标图片成功状态参与提交；本轮不扩生产文件范围、不修改此机制。若 QA 实际复现核心故障，再按流程回流处理，不能把静态风险写成已复现故障或本轮阻塞。

本报告只做源码审查，未运行 App、构建或测试，未调查环境、进程、SDK、远端 CI 或制品。环境和基线核查由 leader 完成。已读取根 `AGENTS.md`、`docs/opc/WORKFLOW.md`、`.codex/agents/tech_architect.toml`、`control.json`、`BACKLOG.md` 和本轮 `run.md`。control 指向本轮 planning；BACKLOG 开头仍是历史 standby 描述，当前授权以用户分工及 control/run 为准，本角色不改这些文件。

## 源码证据：已有与缺失

以下行号为审查时工作区位置；后续实现可能使行号移动，以方法名为稳定定位。TA-E 编号为本报告局部证据，供 leader 关联 IDEA-003 和最终需求编号。

| 证据 | 文件与位置 | 观察及含义 |
| --- | --- | --- |
| TA-E01 | `src/ComicVerse.App/Windows/ReaderWindow.xaml:366-408` | 底栏有上一页／下一页、`PageInfoText` TextBlock、0..1000 比例 Slider，无漫画页码输入。ReaderWindow 各 partial 中未发现用户页号提交处理器。 |
| TA-E02 | `src/ComicVerse.App/Windows/ReaderWindow.xaml.cs:120-121, 1916-1919, 2040-2094, 2270-2287` | `TestJumpToPage` 是内部 smoke 钩子；现有用户入口为比例进度条、Home/End、书签。书签强制切分页，不能直接作为保持模式的新入口。 |
| TA-E03 | `src/ComicVerse.App/Windows/ReaderWindow.xaml.cs:502-552, 809-889` | 分页与双页已有取消／版本／当前 intent 校验，图片完整成功才提交快照；失败保留旧快照与可重试游标。 |
| TA-E04 | `src/ComicVerse.App/Windows/ReaderWindow.ComicNavigation.cs:15-55, 58-106` | 已有异步尺寸读取、横图单独显示、竖图连续段配对、双页导航队列及 epoch 取消。无须复制 canonical 算法。 |
| TA-E05 | `src/ComicVerse.App/Windows/ReaderWindow.ComicPresentation.cs:17-45, 93-142` | 当前有效 pending 保存原始零基页索引；切模式继承 pending；无 pending 时从成功快照／初始位置进入。条漫按页跳转与按比例跳转是不同入口。 |
| TA-E06 | `src/ComicVerse.App/Windows/ReaderWindow.xaml.cs:1884-1896, 1948-2001, 2102-2113` | 书签与持久化读成功快照；保存防抖为 2 秒，关闭先 SaveNow 再清理。没有成功快照时不覆盖既有历史。 |
| TA-E07 | `src/ComicVerse.App/Controls/WebtoonViewer.xaml.cs:65-79, 333-347, 400-408, 490-527`；`ReaderWindow.ComicPresentation.cs:58-69, 129-142` | 条漫到达判断只校验布局／offset；解码异步赋 Image.Source，与当前位置通知分离。VisibleFirst/Last 是带预加载余量的区间，不等于真实可见区域。支持 TA-B01。 |
| TA-E08 | `docs/产品调研与改进-2026-11-02.md:40`；`docs/opc/BACKLOG.md` IDEA-003 | 页号跳转是历史候选方向；源码缺口可验证，真实用户痛点频率与价值仍属待验证假设。没有开展新的竞品调查。 |

## 精确复用调用链

### 分页

新浮层 Apply → 严格校验得到 `target = displayedNumber - 1` → `LoadPageAsync(target)`（xaml.cs:502）→ `BeginComicNavigation(target)`（Presentation:24）→ 取消旧 `_pageLoadCts`、递增 `_pageLoadVersion`、设置请求游标 `_page` → `_loader.GetPageAsync(target, token)` → 检查窗口／模式／游标／version／intent → 非空图片赋 `PageImage.Source` → `CommitComicPresentation("paged", target, 1)` → `FinishComicNavigation(intent)` → `UpdateComicInfo()`／`ScheduleSave()`。

null／异常只显示 `ComicLoadFailureText` 并结束当前 intent，不覆盖旧图和成功快照；失败请求游标仍可用于重试／Next。旧任务晚到不得清掉新 intent。`TestJumpToPage(int)`直接进入此链，不能代表新浮层输入或三模式分发已测。

### 双页与 canonical

新浮层 Apply → `CancelDoubleNavigation()`（Navigation:58，取消旧 Next/Prev 队列和 epoch）→ `_spreadStart = target` → `RenderDoubleSpread()`（xaml.cs:809）→ 方法在第一个 await 前执行 `BeginComicNavigation(_spreadStart)`，记录**原始 target** → `GetCanonicalDoubleSpreadAsync(requestedStart)`（Navigation:39）→ `GetDoubleSpreadLengthAsync(spread)`（Navigation:29）→ 按 `_rtl` 映射左右索引 → 加载两张／单张图片 → 全部成功后原子更新左右图及索引 → `CommitComicPresentation("double", spread, spreadLength)` → 结束 intent／更新状态／安排保存。

不要在提交处理器先 canonical，也不要用规范化后的 `_spreadStart` 重建 pending。例如全竖图输入第 10 页（target=9），归组后 spread=8，显示第 9–10 页；尚未成功时切分页／条漫应继承原 target=9。成功后再切模式按既有成功跨页起点进入，DB 保存 spread=8，不能把输入 10 硬写进进度。

横图单页；两张相邻竖图配对，横图后重新计算连续竖图段起点；末尾孤页单页。RTL 只交换左右显示与横向方向键，不改变页号的自然索引或 canonical 结果。输入页应属于完整呈现的跨页，无须强制位于左侧。双页任一图片失败，旧完整跨页、页号、书签与 DB 成功位置应保留。

### 条漫

新浮层 Apply → `NavigateWebtoonToPage(target)`（Presentation:99）→ `BeginComicNavigation(target)`，Fraction 为 null → 若尚在初始化／恢复则 `ShowWebtoon()` 继承同一 intent → 否则 `WebtoonView.ScrollToPage(target)`（Viewer:333）→ 按 `_tops[target]` 计算 offset 并限制在可滚动范围 → 滚动事件／延迟 resync → `UpdateComicInfo()` → `CaptureCurrentComicPresentation()`、`CompletePresentedWebtoonNavigation()` → `HasReachedWebtoonNavigation()` → `HasReachedPageOffset(target)`（Viewer:75）确认实际 offset。

禁止通过设置进度条值实现页号跳转：`NavigateWebtoonToFraction()` 用文档比例，页高不等时不能代替指定页顶部。末页较矮时，页顶无法到视口顶，允许底部 clamp；应检查目标页在真实视口内及 offset 正确，页脚和 DB 仍与实际 `CurrentPage`／ScrollFraction 一致，不能强制显示或保存输入页号。同位置跳转已有 `UpdateComicInfo()` 显式收尾，不依赖必然发生的滚动事件。

TA-B01 的具体推导：Viewer `IsReady` 仅返回 `_layoutReady`；`HasReachedPageOffset` 不检查图片；`LoadAsync` 允许重试后 `img.Source = null`；`NotifyCurrent` 根据几何位置发事件。故“到达坏图位置后 Capture/Save 推进”的路径在静态上存在，未实际复现。旧可靠性用例包含条漫正常图片／offset，但不能证明坏图不提交。PRD 已明确条漫沿用视口语义，本轮不为此增加呈现状态、回调或保存逻辑；该风险交 leader 留待办，只有 QA 实际核心故障按既有流程回流。

### 成功事实、模式切换与持久化

`Mode_Changed()` → `ShowComicPaged`／`ShowDouble`／`ShowWebtoon` → `GetComicModeEntryIntent()` → pending 原目标优先，否则最后成功快照／初始历史位置。`FinishComicNavigation` 用对象身份判断当前 intent，旧任务不得结束新任务。输入 UI 不读 `_page`／`_spreadStart` 当作已成功事实，不直接写 `_comicPresentation`。

`ScheduleSave()` → 2 秒 `_saveTimer` → `SaveNow()` → `CaptureCurrentComicPresentation()` → 读取快照 Page/Scroll/Percent/Mode/Zoom → `App.Library.SaveProgress()`（`src/ComicVerse.Core/Services/LibraryService.cs:565-585`，既有 progress/books 更新）。`AddBookmark_Click()`同样读取快照。成功 A 尚未过防抖，随后 B pending／失败，再关窗，必须保存 A；此前无成功画面则保留历史。新增输入不引入数据库字段、迁移、独立保存或另一套导航 generation。

## 稳定实现与测试契约

### 真实控件名称

以下名称以已批准 `interaction.md` 为最终合同，替代先前口头建议的 ComicPageJump 控件前缀；生产与独立夹具共用同一组名称。

| 名称 | 推荐类型／职责 |
| --- | --- |
| `PageInfoText` | 保留原 TextBlock 和名称；继续表示成功位置／范围，不用作草稿或错误文案。 |
| `PageJumpButton` | 包裹原页号 TextBlock；漫画可点击。小说禁用跳转、焦点及按钮提示，保留原文本显示和颜色，不通过隐藏父按钮误隐藏页号。 |
| `PageJumpPanel` | 窗口内居中浮层，默认 Collapsed；遮罩覆盖阅读区与工具栏，阻止底层点击，不随底栏误隐藏。 |
| `PageJumpInput` | TextBox，打开后获得焦点并全选；草稿与成功快照分离。 |
| `PageJumpRangeText` | TextBlock，显示本书 `1..PageCount`。 |
| `PageJumpErrorText` | TextBlock，验证错误；不覆盖成功页号。 |
| `PageJumpConfirmButton` | Button；与 Enter 共用一次提交处理。 |
| `PageJumpCancelButton` | Button；与 Esc 共用取消处理，不导航。 |

推荐私有入口 `OpenComicPageJump()`、`CloseComicPageJump()`、`CommitComicPageJump()` 和 `NavigateComicToPage(int zeroBasedPage)`，位于新 partial。命名可由桌面实现保持一致；QA 优先依赖上表真实控件，不要求新增直接修改状态的测试钩子。若需要返回值，提交成功仅表示“输入被接受并发出请求”，不是图片已经显示成功。

| 行为 | 必须保持的最小约定 |
| --- | --- |
| 解析 | 对 Trim 后文本使用严格无符号十进制整数解析，例如 `int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number)`；要求 count>0 且 `1 <= number <= count`，通过后才减一。允许前导零。 |
| 非法输入 | 空白、0、负数、正号、小数、指数、混入字符、分隔符、全角数字、溢出和超过总页数均提示范围／整数要求；不静默 clamp，不取消原 pending，不修改游标、快照、DB、书签。仅限制键入字符不足以校验粘贴。 |
| 打开 | 用当前成功页作默认值；双页用成功起始页+1，条漫用实际成功位置；无成功快照可用初始化页+1作为建议输入，但不宣称已阅读。打开本身不导航／保存。 |
| 应用 | 有效输入只分发一次，关闭浮层、恢复内容焦点；异步加载与失败提示沿用既有阅读器。连续再次打开并提交允许新有效目标取代旧目标。 |
| 取消 | Esc、取消按钮或点击浮层背景丢弃草稿／错误并恢复焦点，不取消已经存在的阅读导航。失焦不自动提交。 |
| 键盘 | 在窗口 `Window_PreviewKeyDown` 的普通快捷键／Esc逻辑之前识别已打开浮层的 Enter/Esc 和漫画 Ctrl+G，处理后设 Handled。输入中的方向键、Home/End、空格、数字和编辑快捷键交给 TextBox，不触发翻页／缩放／关闭。 |
| 就绪 | 漫画已打开、窗口未关闭、loader PageCount>0 且交互 ready 才可打开／提交；条漫按现有 positionReady 门禁处理初始化／恢复。分页／双页 loading 或失败仍应允许新有效目标，不能因等待图片而锁死跳出坏页的入口。 |
| 状态变更 | 正常输入不切阅读模式。若浮层仍开着但模式／就绪状态发生改变，取消浮层及草稿；外部已提交 intent 继续按原规则跨模式，不由浮层取消。关闭阅读器沿用原保存顺序。 |
| 布局 | 不新增固定宽度底栏输入，原页号成为按钮，居中浮层独立承载表单；检查最小900×560、长页数、深浅主题和全屏。编辑期间停止自动隐藏栏，Tab在浮层内循环；重复Ctrl+G只聚焦并全选当前输入。 |

现有窗口在 xaml.cs:2207 仅为小说间距框特判 Esc，否则 Esc 可能隐藏栏或关窗；新浮层必须提前截获。`IsKeyboardInputFocused()`（691）已覆盖 TextBox；保留该保护，不重写全窗导航键语义。

## 可测入口、回归风险与判据

qa_acceptance 独占的 `SmokeTest.ComicPageJump.cs` 应通过 `PageJumpButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))`、设置真实 `PageJumpInput.Text`、`PageJumpConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))`走产品处理链；leader 单独维护 `SmokeTest.cs` 集成入口。另用实际路由键事件覆盖 Enter/Esc；Ctrl+G 需验证真实 modifier 的路由条件，不能仅调 Open 方法声称快捷键通过。测试必须位于既有隔离 Win32 Desktop 的实际 App `--smoke` 进程中，不建立独立 WPF 宿主或接管用户桌面。

观察点可直接复用：`IsComicPageLoaded`、`IsDoubleReady`、`IsWebtoonReady`、`PageInfoText.Text`、PageImage／LeftImage／RightImage 像素、`DoubleLeftPageIndex`／`DoubleRightPageIndex`、`WebtoonView.HasReachedPageOffset`、真实 ScrollViewer offset、`TestFlushSave()`、`App.Library.GetProgress/GetBookmarks`。`CurrentPageNumber` 和 `CurrentSpreadStart` 是请求游标，不能单独证明成功。`WebtoonVisibleRange` 含缓冲页，也不能单独证明目标出现在屏幕上。

| 场景 | 最低可观察判据 |
| --- | --- |
| 分页输入1、中间页、末页、前导零／前后空格 | 点击与 Enter 均到 target；图像像素、页脚、成功后的 DB一致，mode 不变。 |
| 无效文本集合 | 错误可见、面板仍开、输入可修改；图像／页脚／DB不变。另在旧有效目标 pending 时提交非法值，证明未撤销旧目标。 |
| 双页奇偶页、混合横竖、末尾孤页 | 输入页属于 canonical 完整组；RTL/LTR组相同、左右对应翻转；数据库保存组起点。禁止只检查 `_spreadStart`。 |
| 双页目标为配对后页，解码阻塞时切分页／条漫 | 保留原始目标，例如输入10仍继承target9；不能变成canonical8。成功后切模式按成功组起点，区分两种时序。 |
| 旧双页 Next/Prev 队列未完时输入目标 | 新目标取消旧队列；旧异步回调不能抢回位置／模式。 |
| 条漫不等高页、近邻已缓冲页、末页低缩放、同位置重复跳 | 实际 offset 等于目标页顶／底部clamp；真实目标区域可见；同位置pending可结束，之后手动滚动和切模式不会回旧目标；DB跟实际视口。 |
| A成功，B pending／失败，保存／书签／关闭重开 | 分页保留A，双页保留完整A组；A未过2秒防抖也能保存；无成功画面保留历史。失败后新输入C可恢复，旧B晚到不覆盖C。 |
| A→B→C快速输入，B晚成功／晚失败；快速跨模式 | 最后有效请求C胜出；旧B不得清新intent；原有pending跨模式标准保持。 |
| Esc／取消／失焦、键盘编辑、小说、初始化未就绪、自动隐藏栏 | 取消不翻页、不隐藏整个阅读器或关闭；编辑键不泄漏给窗口快捷键；小说页号保留且无入口；未就绪不发请求。 |
| 条漫坏图边界 | 按PRD保留既有视口语义，QA记录适用回归结果及限制；不宣称解码成功保护。TA-B01留待办，若实际复现核心故障再回流，不扩大本轮生产范围。 |

已有可复用夹具与回归入口：

- `src/ComicVerse.App/SmokeTest.OpcReliability.cs:516-563` 的 `OpcControlledSource`／`OpcInstallControlled` 提供受控延迟、失败及独立 cache。相同 SmokeTest partial 可复用其私有成员；注意同时更新 Reader 与已初始化 Webtoon 的 loader，避免缓存命中或已释放引用污染结论。gate有3秒上限，测试应在等待超时前主动释放。
- 同文件 `OpcControlledRaceAsync`（566）、`OpcPendingIntentCasesAsync`（733）和条漫 offset 边界（853起）覆盖成功事实、原始pending、旧回调晚到及真实clamp；保留原判据，新增输入场景不替换旧回归。
- `src/ComicVerse.App/SmokeTest.LogicAudit.cs:175起` 的快速模式回归，以及混合双页／奇数边界用例；`SmokeTest.cs:63-79` 已有 `--opc-reliability-only`、`--logic-audit-only` 路由。leader另将新跳页检查接入完整 smoke，专项通过不等于最终整包通过。
- `tests/ComicVerse.Tests/Program.cs:384` 的 `TestFarJumpLoad`是加载器远跳验证，不覆盖 UI 输入、焦点、三模式路由或成功快照，不能代替本轮验收。本角色未运行任何上述检查。

## 文件归属、回退与交接

| 文件／职责 | 唯一写入者与界限 |
| --- | --- |
| 本 `technical-review.md` | tech_architect独占；本次仅新增此文档。 |
| `src/ComicVerse.App/Windows/ReaderWindow.ComicPageJump.cs`（新增） | desktop_engineer：浮层状态、验证、共用提交／取消、三模式分发。 |
| `src/ComicVerse.App/Windows/ReaderWindow.xaml` | desktop_engineer：页号按钮及窗口内浮层；保留PageInfoText。 |
| `src/ComicVerse.App/Windows/ReaderWindow.xaml.cs` | desktop_engineer：键盘优先路由、初始化／就绪／关闭必要接线；不重写旧加载、保存和书签链。 |
| `src/ComicVerse.App/SmokeTest.ComicPageJump.cs`（新增） | qa_acceptance独占：独立验收夹具、新入口断言与最终候选验收；leader和桌面不并行编辑，不由开发自测代签。 |
| `src/ComicVerse.App/SmokeTest.cs` | leader独占：集成新smoke入口；不与QA夹具或桌面生产文件交叉写入。 |
| `ComicNavigation.cs`、`ComicPresentation.cs`、`Controls/WebtoonViewer.xaml.cs` | 本轮只读复用；TA-B01按已批准PRD保留旧行为并留待办，不扩大生产范围。 |
| `src/ComicVerse.Core/`、`tests/ComicVerse.Tests/Program.cs`、control/run、正式制品 | 本角色不写；本输入方案无Core／数据库改动需要。 |

回退：撤销新partial、原XAML/代码接线和新smoke入口即可回到旧页号显示与比例滑块，无数据迁移或用户数据回滚。已产生的有效阅读进度沿用原结构，不为回退功能而重置。若修改候选，仍须重新冻结并由独立QA验收同一制品，历史PASS不可继承。

交接状态：静态能力验证已完成，leader已批准并派发三文件生产实现；正式控件合同以interaction.md为准，TA-B01已按PRD划定边界，当前无架构阻塞。桌面独占三份生产文件，qa_acceptance独占新smoke夹具，leader独占SmokeTest.cs入口；本报告不签发运行PASS。停止扩展研究，待实现交回tech_architect后只读开展非作者复核，重点检查原始pending是否被规范化值替换、非法输入是否污染导航、Esc／输入键是否被窗口截走、单／双页保存是否仍只取成功事实、条漫是否保持既有视口语义。本角色未改生产／测试、未构建、未运行App、未派生agent。

## 实现后非作者复核（2026-11-16，冻结三文件）

**生产静态复核结论：通过，未发现需要阻塞候选构建的具体生产缺陷。** 核对了桌面交回的最终 XAML／code-behind diff 和新增 partial 全文；最终重读与本次初读一致。未参与产品或夹具实现，仅向本文追加结论。此结论允许进入构建／最终候选验收，不是编译通过、运行通过或交付放行。

### 已核对的生产路径

| 位置 | 静态复核结果 |
| --- | --- |
| `ReaderWindow.ComicPageJump.cs:15-37`；`ReaderWindow.xaml.cs:2353` | 就绪条件包含漫画类型、正页数、已就绪交互、未关闭窗口及三模式；条漫额外要求布局就绪且不在恢复。分页／双页慢加载未被错误锁死。小说保留PageInfoText，入口不可点击／聚焦，未新增小说导航。 |
| `ReaderWindow.xaml:402-465` | 保留原PageInfoText实例和类型；7个PageJump控件名与最终合同一致。遮罩跨3行、位于工具栏之上；对话内容居中，Tab循环；引用的按钮样式与深浅主题画刷存在。未见XAML事件名与新处理器不一致。 |
| `ReaderWindow.ComicPageJump.cs:42-81` | 默认输入从成功快照／合法初始页取值；打开停止自动隐藏、显示工具栏、聚焦全选。取消只清会话草稿，恢复可聚焦的ContentGrid；背景点击设Handled，未引入导航取消／保存调用。 |
| `ReaderWindow.ComicPageJump.cs:84-118`；`ReaderWindow.xaml.cs:2204-2210` | 窗口先处理浮层；Esc提前消费，不落入原窗口关闭逻辑。Enter仅提交一次，取消按钮获得焦点时Enter取消。其余编辑键交给控件；严格ASCII整数与范围校验先于任何导航状态变化，非法输入保留草稿并聚焦，不触碰旧pending。 |
| `ReaderWindow.ComicPageJump.cs:120-135` | 有效输入只关闭草稿后分发一次：分页LoadPageAsync、条漫NavigateWebtoonToPage、双页CancelDoubleNavigation→原始target赋_spreadStart→RenderDoubleSpread。没有比例转换、提前canonical、直接创建成功快照或写DB。 |
| `ReaderWindow.xaml.cs:466, 2106, 2179-2200` | 切模式丢弃草稿，关窗关闭浮层时不抢焦点；随后沿用旧保存／取消顺序。打开浮层时禁止切换栏显示和自动隐藏计时；没有取消已经提交的阅读intent。 |
| 既有Navigation／Presentation及保存链 | 新实现只调用原链；双页在RenderDoubleSpread第一个await前保存原始intent，完整图片成功后提交；pending原目标跨模式、旧任务身份／版本检查、单／双页失败保留成功快照未被更改。条漫按批准PRD保留真实offset语义，TA-B01未扩大处理。 |

以上焦点、命令和布局结论是接线审查；真实WPF运行时、视觉截图与最终包行为仍由独立QA验证。桌面已报告XML、62事件引用、控件合同和git diff --check通过，本角色未重复执行、也未将交接报告冒称自己的运行证据。

### 夹具及门禁风险（只读，不要求新增产品能力）

1. **TA-R01：Ctrl+G证据边界。** `SmokeTest.ComicPageJump.cs:77-95` 检查真实Ctrl+G KeyBinding，然后对绑定的RoutedCommand执行CanExecute／Execute；这验证绑定及产品命令，不是带Control修饰键的真实按键匹配和焦点路由。其通过结果不得写成“Ctrl+G实际按键路径已运行验证”；QA应在既有AC-01／AC-06结论中明确覆盖方式与剩余限制。Enter／Esc另有真实WPF路由事件进入窗口处理，二者证据不要混为一谈。
2. **TA-R02：新入口受控竞态证据尚缺。** 当前新夹具有正常输入、非法输入、规范双页／RTL、条漫offset、坏图保留及重开，尚无经新控件提交的受控pending B→C、非法输入不取消pending、双页后页原目标跨模式或pending关闭场景。旧OPC可靠性会进入完整smoke，但通过TestJumpToPage等旧入口；不能单靠旧用例断言新入口已覆盖PRD AC-09及AC-10的pending分支。这是现有验收合同的证据缺口，交qa_acceptance映射／补证，不是已复现生产缺陷，不阻塞先构建候选。
3. **编译／整包门禁仍待实际证据。** 静态核对发现新partial与SmokeTest同命名空间／同partial类、默认SDK包含源文件，方法签名与leader调用吻合；使用的旧私有夹具成员可在同一partial类访问，未发现具体缺失符号。未执行编译，不能给编译PASS。`SmokeTest.cs`已接入专项参数、完整smoke执行、summary及最终布尔返回，`comicPageJump.Ok=false`会使正常完整验收失败，未见漏接放行条件。

**门禁裁定：非作者生产代码静态门禁通过；候选运行／独立QA／同制品交付门禁未判定。** 保留TA-R01／TA-R02的证据边界，按冻结PRD完成独立验收后再放行；未提出扩大三文件生产范围的修改要求。

### 本次复核文件身份

下表是本地源码字节的SHA256，用于后续最终diff核对；不是候选EXE／ZIP哈希，也不替代leader冻结manifest。生产3文件最终diff重读无变化；QA夹具后续若补证，须以其新冻结版本验收。

| 文件（仓库相对路径） | SHA256 |
| --- | --- |
| `src/ComicVerse.App/Windows/ReaderWindow.xaml` | `A23CCD2385AC05210C55808DDF235CEE36E876152CC5903EF9FD694B7931F5F4` |
| `src/ComicVerse.App/Windows/ReaderWindow.xaml.cs` | `E44184975BC923116C345F4FE7BD0B1CBB814A8A601DB5C8A0E730F0D0E740B4` |
| `src/ComicVerse.App/Windows/ReaderWindow.ComicPageJump.cs` | `5C584C5174040BB9CBEC950415479A61DFD98FD2BA24C3C489CDB9C7107DFA05` |
| `src/ComicVerse.App/SmokeTest.ComicPageJump.cs` | `94641EEF4EEDBD2B8ED16236485144F357761BB85D9DC8BD67FFCB742A9BEC31` |
| `src/ComicVerse.App/SmokeTest.cs` | `ADD8A77EAA90EF1BB53F3AA8AEE19BCA23FFCB67D2083E5F2CE6BAC7666321B0` |

接收者：leader用于候选门禁，qa_acceptance用于现有AC证据映射。未改生产／测试文件，未构建、未运行App，未重做环境调查，未派生agent。
