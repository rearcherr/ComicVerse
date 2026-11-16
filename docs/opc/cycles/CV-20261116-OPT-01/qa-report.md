# CV-20261116-OPT-01 独立 QA

2026-11-16；执行者 `qa_acceptance`，未参与本轮产品实现。基线 `37ade220ca356f1cee40150b9ef7dc6335b91de2`，一轮 120 分钟。已读取本轮 requirements、interaction、technical-review，并按 leader 批准范围完成独立夹具，源文件可提交云 CI。

**结论：夹具已冻结，可提交；产品待云端构建与最终候选独立 QA，不予放行。** 所有运行矩阵项均为 **NOT_RUN**；本地未构建、未运行 App，尚无本轮 EXE／ZIP 验收哈希。历史 PASS 只用于识别回归范围，不转移为本轮结论。

## 夹具冻结交接

- 文件：`src/ComicVerse.App/SmokeTest.ComicPageJump.cs`，635 行，SHA256 **1BD1C2B56DAFA3396CF27FABFA54835C4A0396A8894F1B39D7AEE70DA6F9BA44**。此为本地源码字节哈希；Git/CI 换行转换后的原始候选输入仍须独立核对。
- 方法：`CheckComicPageJumpAsync(string outDir)` → `Task<(bool Ok, string Detail)>`；输出 `comic-page-jump-details.json` 与 `page-jump-*.png`。leader 已完成 SmokeTest.cs 的专项／完整入口接入，QA 未修改该文件。
- 静态验证：Roslyn 仅解析语法，0 诊断；夹具内方法无重名；7 个约定 PageJump 控件在实际 XAML 中齐全；仓库 diff 空白检查无错误。证据 [fixture-static-review.json](../../../../artifacts/opt-01-qa/fixture-static-review.json)。解析器首次 XML namespace 参数构造错误已修正并记录，未涉及产品运行。**语法解析不等于编译或测试通过。**
- 8 组有界场景：输入／键盘、双页／RTL／混排、条漫、单／双页坏目标、单页书、pending、30 页远跳／PDF切片、小说禁用。正常视窗固定 980×720；最小视窗读取实际 MinWidth／MinHeight（当前900×560）；另有全屏与浅色错误态截图。截图须最终实际生成并由 QA 目视确认。

|PRD AC|新入口夹具已写的代表场景（全部未运行）|
|---|---|
|AC-01|按钮提交；真实 Ctrl+G 绑定／命令；30 页从第2页到第17页，图像背景像素、底栏17/30及DB16互证|
|AC-02|页3／4规范组、末尾孤页、RTL左右交换、横页单独显示、横页后重新分组、关闭重开|
|AC-03/04|条漫中间／近邻／首末页真实offset与底部clamp、不等高页、单页不足一屏；当前页重复；空白前导零；单物理页PDF三色三切片跳第3逻辑页，不能混淆物理页|
|AC-05/06|12类非法串与纠正、Enter、Esc、取消按钮、背景取消、重复命令全选、编辑键隔离、WPF焦点正反循环、隐藏工具栏／全屏|
|AC-07|漫画初始化前按钮和命令不可用；小说入口无交互、命令不可用|
|AC-08/10|新入口单页坏目标／双页第二张坏图不提交半组；成功画面／DB保持，失败关闭重开、输入可读C恢复；成功分页／双页／条漫重开|
|AC-09/10|受控慢B被C替代且旧完成不回灌；非法草稿不取消合法pending；原始后页目标跨双页归组再入条漫；pending关闭保存成功页|
|AC-11|980宽深色、最小窗口错误态与浅色截图；其余原滑块／书签／小说等由同一候选完整logic／OPC及既有smoke回归覆盖|

证据边界：Ctrl+G 检查 **KeyBinding 及绑定命令的 CanExecute／Execute**，未注入 OS 修饰键；Enter／Esc 等走 WPF PreviewKeyDown／KeyDown 路由；文本赋值触发 TextChanged，未使用系统剪贴板；Tab 验证 Cycle 配置及 WPF MoveFocus 正反遍历，未声称 OS Tab 按键实测。受控延迟复用既有 OpcControlledSource／安装夹具，3秒硬上限，无新增生产测试状态。条漫按批准 TA-B01 采用真实视口语义，坏图保护只断言单／双页，不扩大 Viewer 范围。

剩余范围：不重复所有既有可靠性场景；旧失败晚到、书签、无成功历史等需同候选完整回归实际结果，不能引用历史 PASS。枚举中／空源等所有中间状态、自动隐藏定时器、OS键盘／剪贴板、所有DPI与设备时序未穷尽；正常组为稳定截图临时关闭自动隐藏，最后恢复原设置。云 CI 负责首次真实编译和完整smoke，QA后续独立下载并验收同包。按 leader 最新要求，当前不再扩充夹具。

## 独立验收矩阵（拟定，待映射冻结需求）

关联候选 `IDEA-003`。以下页号均为用户看到的 **1 基页号 p**，内部索引为 `p-1`。仅使用隔离目录中的合成漫画、包内样例与新数据库，不读取真实书库。新功能须从实际控件、点击／Enter、WPF 键盘事件路由进入；直接调用内部跳页方法只能作为辅助回归，不能证明新入口可用。

**文件分工**：QA 独占 `src/ComicVerse.App/SmokeTest.ComicPageJump.cs` 独立验收夹具，已按上述合同冻结；leader 唯一负责 `SmokeTest.cs` 集成；生产实现及其自测由开发负责。下表保留最初独立风险矩阵，执行范围以冻结 PRD 和本报告顶部映射为准，不自动新增场景。

|ID|场景与操作|独立判据／证据|
|---|---|---|
|QA-OPT-01|分页：输入首、中、末页，分别点击确认和 Enter；再次跳当前页|实际图像页标／像素、底栏、成功后的 DB 都对应 p；模式不变，无越界或多翻一次；同页操作幂等|
|QA-OPT-02|双页：奇偶 p、首末页、奇数总页数、横竖混排；LTR／RTL|目标 p 必须在完整可见组中，按既有组页规则显示；RTL 只改显示顺序；底栏范围与真实像素一致，DB 保存成功组的合法位置，不要求错误地恒等于输入索引|
|QA-OPT-03|条漫：远距离、缓冲区内邻页、同页、末页；不等高与不足一屏样例|核对真实 VerticalOffset 与目标页起始位置；末页受滚动上限限制时目标页须可见，页码与 DB 反映实际视口。不能仅用缓存已加载或请求索引作为到达证明；像素与截图佐证|
|QA-OPT-04|入口与 Ctrl+G：三模式、工具栏隐藏／全屏、重复调用、尚未就绪、小说|按冻结交互进入并聚焦、选择合理默认页号；有效漫画入口可发现，未就绪不触发无效导航；小说不获得漫画跳页行为。必须记录 FocusedElement／路由结果，不能只调打开方法|
|QA-OPT-05|输入中方向键、Home/End、Space、删除、粘贴；Enter、Esc、失焦；再次打开|编辑键不翻书；Enter 最多提交一次；Esc 首次仅取消页码编辑，不关闭阅读器或产生跳页。取消／失焦语义待冻结；记录焦点和窗口仍存活，避免父级 PreviewKeyDown 抢先处理|
|QA-OPT-06|空串、空白、0、-1、N+1、小数、字母、混合串、超长数字／Int32 溢出|所有明确非法输入均给可恢复提示；画面、模式、滚动、底栏、成功进度／书签不变，不把越界夹紧成合法跳页。空白包围、前导零、+号、全角数字按冻结语法逐项断言|
|QA-OPT-07|成功 A 后提交坏输入／Esc；另做 B 已 pending 时提交坏输入／取消编辑|坏输入／取消不产生新导航，不污染成功 A 或取消既有合法 B；若 B 自然完成，允许因 B 更新，须与“坏输入造成跳页”区分。保持 zoom、阅读方向与成功进度字段|
|QA-OPT-08|A 成功→有效 B 延迟加载→切分页／双页／条漫；期间保存／书签|保留有效 B 的原始目标（含双页临时归组前的奇偶目标）；B 未成功时保存／书签仍基于 A，成功完整呈现后才提交。旧请求晚到不覆盖新意图|
|QA-OPT-09|A 成功→B 解码／读取失败；双页第二张失败；首次打开无成功画面|保留 A 完整成功画面及进度，错误可见且可重试；失败 B 不跨模式继承为有效意图。无成功事实时不得写虚假进度／覆盖历史；双页不提交半组|
|QA-OPT-10|快速 B→C；B 的旧成功／失败晚到；失败后有效 C；pending／失败时关闭重开|最终只接受当前有效请求，旧完成不回灌；保存防抖与关闭 flush 不写失败目标；重开恢复最后成功事实。采用可控延迟／释放，记录时序，避免仅靠固定 sleep|
|QA-OPT-11|跳页后翻页、滑块、Home/End、切模式、缩放、书签、继续阅读；单页书|新入口与原导航一致，不残留意图、卡住或越界；目标图像／底栏／持久化互证。重点复查旧显式页 8（零基）跨模式、条漫末页 clamp、同位置 Home 与手动滚动|
|QA-OPT-12|最终包完整既有 smoke 与同提交 Core 证据|逐项检查 logic、OPC、漫画／PDF、TXT／EPUB、排版／查找、书架／导入、PAX、流所有权等适用回归；保留正式 CR 的既有口径，不能删除旧断言凑通过；skipped 单列|
|QA-OPT-13|同一解包 EXE 两个异常退出反例|分别 `--smoke --verify-failure-exit`、`--smoke --verify-task-failure-exit`，新 data／Desktop；须准确 intentional 异常记录、真实 exit1、未超时。exit0、回退 exit2、缺证或挂起均不能通过|
|QA-OPT-14|来源、版本、隔离、运行前后完整性|逐项执行下述制品门禁；核对 GitHub run／attempt／commit、版本、包内外同字节、输入 manifest 和实际 App Desktop/PID；任何不匹配先停止验收，不运行来源未核实的 App|

## 冻结前检查缺口与关键建议

下列保留初始**静态风险，未实际复现为产品缺陷**。G-01／G-02 已由 REQ-CV-20261116-OPT-01-01 及 interaction 冻结；G-03／G-04 已接线并编写上述夹具，运行证据仍待交接；G-05／G-06 为最终候选阶段门禁。表内待决状态描述的是初始准备时缺口，不代表当前还在等待控件名。

|缺口|观察与需要冻结的内容|接收角色／状态|
|---|---|---|
|G-01 精确页号合同|双页需以“目标页位于成功完整组内”为判据；条漫末页可能因滚动上限导致底栏当前页不是输入页。明确目标到达、默认输入值、底栏和 DB 的关系，避免把历史合法行为误判或把请求索引冒充显示事实|产品／架构／leader；待决|
|G-02 输入与取消|冻结合法数字语法（建议去首尾空白后仅 ASCII 十进制整数，前导零可接受；不默默夹紧）、空／错值提示、Enter／点击、Esc／失焦与成功后焦点语义；这是建议，未成为已批准要求|产品／交互／leader；待决|
|G-03 键盘路由覆盖|基线 ReaderWindow 窗口级 PreviewKeyDown 处理 Esc，输入焦点保护允许 Esc 进入窗口处理；新增编辑器必须先消费取消。现有 TestJumpToPage 直接调用 LoadPageAsync，不能覆盖 Ctrl+G／输入／提交的实际路由|桌面开发交控件名；QA 编写独立真实事件夹具；leader 集成|
|G-04 成功事实与 pending|基线 ComicPresentation 区分请求与成功显示；旧 CR 已批准“有效 pending 可跨模式继承，失败目标不可”。新入口应经过既有意图通路；必须保留输入错误、pending、failed 三种状态的独立证据|架构／桌面开发；待审查及用例交接|
|G-05 最终候选与来源|尚无本轮冻结 commit、版本、GitHub run／attempt／artifact、下载地址、SHA256、完整输入快照。build-release 已有自包含发布与 candidate/source-freeze；原 workflow manifest 不含本轮 PRD／技术审查，需另附已批准文档快照／哈希|leader；待交接，不据旧构建运行 App|
|G-06 可观察性与独立性|完整 smoke 需实际执行新矩阵对应场景，并导出每项输入、预期／实际页、可见组／offset、mode、焦点、DB、错误文本及关键截图；只有内部 helper 断言或总 exit0 不足。QA 维护新夹具及场景映射，开发交自测，leader 集成后由 QA 独立核验同包|QA／开发／leader；待控件合同与候选交接|

## 最终候选交接与放行门禁

1. **交接材料**：leader 冻结 scope／requirements／technical-review，提供候选版本／rc、完整 commit、相对基线变更、GitHub 仓库与 build-release run ID／attempt／artifact ID、candidate.json、source-freeze.json、EXE／便携 ZIP SHA256、构建参数、自测与非作者复核、新旧场景映射。下载需绑定同一 run/attempt，不能以“latest”或旧 1.3.2 PASS 代替身份。
2. **先核来源再运行**：QA 在 `artifacts/opt-01-qa/<candidate>-<run>-<attempt>/` 新目录独立下载同一 selfcontained 候选与云端 evidence。核对 artifact 摘要（如可得）、commit／版本与自包含参数；比对 EXE FileVersion／ProductVersion／实际启动版本，合法版本后缀须对应提交。逐项核对 manifest 的路径、长度、SHA256、重复／遗漏及源／测试／工具／workflow／样例库存；使用候选 commit 的原始源码快照，不能把 Git LF 与本地 CRLF 差异当作已匹配。记录本地工作区状态，变更必须解释。
3. **独立解包**：检查 ZIP 无重复／越界条目，EXE、说明、LICENSE、样例完整；外部 EXE、ZIP 内 EXE 与 QA 解包 EXE 哈希一致，其余材料匹配声明输入。冻结文档快照、runner 与 QA 核验工具也记录哈希。来源／工具不一致则等待正确交接，不能临时重建包。
4. **隔离执行**：仅从上述解包目录运行实际 EXE，统一通过经核验的 `tools/Invoke-IsolatedProcess.ps1`；每次新证据／数据目录、新 Win32 Desktop，不切换用户桌面，不使用临时 WPF 宿主。完整 `--smoke <包内 samples>` 后串行运行两个异常反例；完整 smoke 初始上限 300 秒、反例 30 秒，等待每次不超过 60 秒并核查存活。若需调整限时，先记录理由，不掩盖超时。
5. **运行证据**：保留 start/result、真实退出码、TimedOut、耗时、stdout/stderr、App 日志、summary、具名明细、数据库事实、截图及 smoke-environment。runner 与 App 的 PID／Desktop 必须一致且为独立桌面；核对 SmokeProcess 与既有 ErrorMode 证据。正常运行必须 exit0、未超时、无未处理异常／crash 标记；不能用禁弹窗或修改系统 WER 掩盖失败。已设计故障注入日志逐条对应场景，不泛化忽略 ERROR。
6. **截图和回归**：QA 目视新输入入口／焦点、非法输入保位、分页目标、完整双页／RTL、条漫邻页与末页、失败保留／重试等关键离屏截图；与像素断言、范围、实际 offset 及 DB 对照。完整 smoke 逐项检查本轮矩阵和旧回归；历史 logic31／OPC72 等仅为参考库存，新数量以冻结场景与实际具名结果为准，不要求凑固定数字。
7. **前后哈希与判定**：运行前后再次计算 EXE／ZIP／candidate.json／manifest 本身、manifest 每个输入、包内／解包材料和已冻结工具／文档的哈希，要求零差异。QA 输出绑定本候选的 PASS／FAIL、缺陷与未覆盖范围；核心用例缺证仍受阻，不计 PASS。失败由开发修复、leader 重新冻结并产生新候选，再独立完整适用回归，保留所有旧 FAIL。
8. **同制品交付**：只有最终独立 PASS 后 leader 可复制批准的同一 EXE／ZIP；QA 随后只读核对 `publish/ComicVerse.exe`、`dist/` 对应 ZIP 和说明一致。QA 不替换程序、不构建、不推送、不发布。交付字节或被验收产品／工具变化必须重新验收。

## 本次准备证据与交接

- 只读方法：读取约定与当前状态、`git status`／HEAD、源码与 workflow 静态检查；相关文件 SHA256 与采集时存在性见 [planning-inputs.json](../../../../artifacts/opt-01-qa/planning-inputs.json)。该快照不是最终输入 manifest，也不表明后续工作区不可变化。
- 修改仅此报告、`artifacts/opt-01-qa/` 和新增获授权 `SmokeTest.ComicPageJump.cs`。未构建、未运行 App、未写产品／控制文件或 SmokeTest.cs、未派生 agent；当前矩阵全部 NOT_RUN。
- 未覆盖：本轮全部运行时行为、最终制品完整性、截图和异常反例；所有真实用户体验／性能收益仍无数据。复杂第三方格式、全部 DPI／设备时序等不能因本轮 smoke 而声称穷尽验证。
- 下一接收者：**leader**，可立即提交冻结夹具并推进云 CI；随后交接 G-05～G-06 的最终 GitHub 自包含包。QA 不提前运行旧 App，最终独立验收仍由本角色执行。
