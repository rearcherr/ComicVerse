# CV-20261103-01 非作者代码复核

复核角色：tech_architect；未参与本轮生产实现。日期：2026-11-03（+08:00）。当前为开发中第一次源码复核，候选尚未冻结；此记录不代签独立QA，也不声称执行过构建或产品回归。

## 本轮复核发现

|编号／状态|位置与触发|影响／处理|
|---|---|---|
|R-01 已由leader发现、作者修复，架构复核确认|ComicPresentation.CaptureCurrentComicPresentation：原else-if允许webtoon未ready/restoring且snapshot.Mode相同时按PageScale.ScaleX更新Zoom|条漫恢复中可能把无关分页缩放写入成功快照。当前代码只允许有匹配实际图的paged/double刷新Zoom，webtoon未ready不进入该分支；保留成功位置。最终回归仍需覆盖。|
|R-02 作者已修复、非作者定稿复核确认|ShowComicPaged/ShowDouble跨mode入口原来无条件清目标view图像，但成功快照可能仍是该目标模式（切出的新模式未成功，随即返回）|新`HasRetainedComicPresentation`检查目标mode、单页呈现索引和图像、双页完整图像／张数／左右索引集合（允许RTL顺序）；跨模式仅不匹配时清图，缩放刷新和“仍显示”文案使用同一检查。该源码反例已消除，独立QA须对新增受控场景重跑，不能借源码复核宣称实际回归已过。|

## 当前源码核对结果

### 漫画呈现与进度

- 保留`_page`／`_spreadStart`作为请求游标，故失败后的Prev/Next沿请求目标继续，而非困在已损坏目标之前。
- 单页有效bitmap且版本／mode仍current时才提交成功快照；失败保存不再取请求页。
- 双页先局部取得所有必需bitmap，检查current，再同时提交图像、显示索引、可见页数和快照。半对不替换上一完整组；失败重试仍指向目标组。跨页分组及队列未作大重写。
- `SaveNow`读取快照，最近成功A即使还在2秒防抖窗口也可在失败B之后关闭flush；首次无快照时不覆盖历史。这避免“失败时仅跳过保存”退回更旧位置。
- 首次`_fromStart`打开调用SaveNow时，如果无成功画面会保留历史；之后成功回调正常安排保存，关窗flush成功事实。与“首次加载失败保护历史”一致。
- 模式入口从成功快照或恢复的初始目标进入；取消版本及entry守卫保留；书签取成功页，无成功快照时禁用／安全不创建。
- 条漫只在IsReady且非restoring时以真实视口采集快照；当前修复后的Zoom分支不会在条漫未ready时采分页值。小说保存链路保持独立。
- 当前尚待R-02及独立QA运行证据，不能据上述静态核对放行最终包。

### TAR／PAX

- 索引用标准TarReader的最终Name/DataOffset/Length，数据起点不再叠加512。普通页payload不复制，仅建立随机读取偏移索引；自然排序、共享读取锁及PageReadGuard保留。
- 每次GetNextEntry前预扫连续元数据，finally恢复流位置；源码中不读取entry.DataStream，.NET10的seekable ProcessDataBlock在返回前跳到数据与padding末尾，下一GetNextEntry的AdvanceDataStreamIfNeeded回同一末尾。该调用方式下预扫位置不变量成立。
- x/g/L/K/X每块≤1MiB、连续块最多32，即单个预扫链声明payload总量最多32MiB；不是宣称整本归档只有1MiB元数据。octal/base256负值、非法数字和Int64溢出有界拒绝，padding/next使用checked并核对文件内范围。
- 标准库会在读取header后统一设置DataOffset，包括目录及global metadata；当前在entry过滤前的非负和文件边界校验不因无普通payload而必然误拒。g作为非图片过滤，不宣称其全局path/size字段已应用。
- 仅RegularFile/V7RegularFile/ContiguousFile进入图片索引，links/sparse不当普通图片；没有ExtractToFile或对归档路径的外部文件读取。
- GetPageStream使用ReadExactly，索引后源文件截断时会失败并释放MemoryStream，不能返回半页流冒充完整读取。
- Core新增自测覆盖PAX Unicode/path和size覆盖、后条目不继承、自然排序、随机与并发读取、USTAR/GNU、元数据超限／截断／非法数值／链上限、超大普通页限制。此处仅审读测试代码，未执行，实际退出码由leader提供。
- QA需区分严格拒绝与标准库安全处理：PRD没有承诺对非法UTF8一律拒绝，标准库安全替换且实际页数和像素正确或安全拒绝均应如实记录；不能宣称完整POSIX PAX兼容。

## 初次复核输入摘要

|文件|初次读取时SHA256|
|---|---|
|TarComicSource.cs|A5B958D6899AF13AC3A2B607A0761D4F81D744E9F0AE2444FC3643DD1A0BFD42|
|ReaderWindow.ComicPresentation.cs|C1DF787BC2BC6EBF1CC6A41F1FA3C209226766755B91BE24968242C7640B956F|
|ReaderWindow.xaml.cs|11DCCAD881332694449062428B9DF713B7F56E0ABEE23FC065630A1D88569D79|
|ReaderWindow.xaml|6BC3CDEF5D3342F2A07F5BDAF1CE26A076CDD47D415B72448A1DB16C9B36B9B7|
|Program.TarPax.cs|2F490248037E91531C2916DB3B6D654BD2858AC2A5D604CE292CAC0F802D9A07|

生产与夹具当时仍在开发，以上不是最终候选manifest。文件变化后须复核定稿；实际构建、专项与最终整包回归由leader／独立QA记录。本角色未修改生产或测试文件，未启动任何构建／产品／测试进程。

## R-02修复后定稿复核

**代码复核结论：通过，可进入统一重建、专项复测及候选冻结；未发现尚待处理的本轮阻塞源码问题。** 这只是非作者代码复核，不是最终包QA放行。leader报告Core开发回归40项退出0；本角色未执行该运行。App专项曾54/56，两条夹具调整待重新运行，不能在这里写成已通过或已确认产品缺陷。

定稿重新读取了Reader主文件的单页／双页入口、LoadPageAsync、RenderDoubleSpread及ComicPresentation partial和XAML，核对R-02保留条件、共用缩放与文案逻辑。HasRetained对单页要求非空图与`_pagedPresentedPage == snapshot.Page`；对单张双页要求左页索引匹配、右页索引-1且无右图；对两张要求两图齐全、张数一致、索引正好是snapshot.Page及下一页，容许RTL交换左右。请求目标、成功快照和实际保留图没有重新混用。

Tar源码和Core自测文件与第一次审读哈希不变，前置guard位置不变量、单块／链限制、DataOffset以及随机读取结论继续适用；没有新增依赖或数据库迁移。QA夹具已冻结供leader统一构建，须以新运行记录确认慢请求、模式返回失败、瞬时重试、实际像素及DB闭环。

|定稿文件|SHA256|
|---|---|
|TarComicSource.cs|A5B958D6899AF13AC3A2B607A0761D4F81D744E9F0AE2444FC3643DD1A0BFD42|
|ReaderWindow.ComicPresentation.cs|053DE0E45ABEA4BBFE302D0B0C18A7A06728E6B3410806F221C772171AD22798|
|ReaderWindow.xaml.cs|4DBC82251C75983C0E9AE541F9FD1030C30C284D7AFF9760B2D04E7F98D820B7|
|ReaderWindow.xaml|6BC3CDEF5D3342F2A07F5BDAF1CE26A076CDD47D415B72448A1DB16C9B36B9B7|
|Program.TarPax.cs|2F490248037E91531C2916DB3B6D654BD2858AC2A5D604CE292CAC0F802D9A07|
|SmokeTest.OpcReliability.cs|B564565EC51E7EE8913505659ACADAF5EF5B743D25435DCA70C807F1034913D1|

交给leader：以这些实际来源构建和整理单一候选，独立QA核验冻结manifest、最终EXE与ZIP并完整回归。任一被审查生产／夹具文件变更后，本复核须核对增量，旧哈希不能冒充新来源。未覆盖范围仍包括真实商业归档、全部PAX方言、所有DPI／输入顺序及极端竞态；不以静态审查宣称穷尽验证。
