# CV-20261103-01 技术验证与修复约束

角色：tech_architect。日期：2026-11-03（+08:00）。基线：`3a892fd`／v1.3.1。当前结论为源码及官方实现核对，未运行产品、构建或测试；实际复现与最终制品结论由独立QA补证。本文件不是最终PASS。

## 选题及证据

|证据|类型及来源|结论与限制|
|---|---|---|
|E-CV-20261103-01-31|源码：ReaderWindow.xaml.cs `LoadPageAsync`、`RenderDoubleSpread`、`SaveNow`、`Window_Closing`|单页先将`_page`改成请求目标，再await解码；失败保留旧图且不回滚游标。双页先改`_spreadStart`和`_doublePageCount`，之后才取全部位图。`SaveNow`直接读取这些值，关闭会flush；故未成功目标可以写回阅读位置。与desktop/interaction审查互相确认；是否在实际App复现待QA。|
|E-CV-20261103-01-32|源码：LibraryService.cs `SaveProgress`和`ResetProgress`；SQLite及Microsoft官方事务文档|两条相关写语句没有显式事务，故障在第二条可能留下进度表与书架比例不一致；未用本项目provider实际复现。本轮DEFERRED，不与已选漫画呈现状态混成同一根因。|
|E-CV-20261103-01-33|源码：TarComicSource.cs `BuildIndex`；Core审查交接|`x/g`元数据被直接跳过，未应用PAX长path；name截断后可能无图片扩展名而漏页。QA负责标准PAX最小样例验证。|
|E-CV-20261103-01-34|已读取.NET10.0.0官方TarReader/TarHeader实现和API文档，链接见下|标准库有`DataOffset`和`copyData:false`，可保持随机读取，不需要解压全包；但PAX/GNU元数据读取先按声明size租数组，只有`Array.MaxLength`级限制，不能把页`PageReadGuard`误认为保护了元数据分配。|

## REQ-01 漫画呈现状态与持久化

### 数据不变量

1. 请求目标用于接收快速Next/Prev、重试、进度条或Home/End，不代表已经成功阅读。
2. 成功呈现快照只由仍属于当前模式／版本的成功完成提交。单页需要有效bitmap；双页需要所有必需bitmap完整成功，不能以其中一侧成功提交整对。
3. 保存位置及完成比例来自成功快照，而非请求游标；首次尚无成功呈现时保留既有历史，不把初始化0或失败目标写回。
4. 最近成功A尚未过2秒保存防抖，随即请求失败B并关闭，仍须flush A。仅在pending/failed时`return`跳过保存不满足此不变量，会丢A回到更旧数据库值。
5. 旧请求被新请求、模式切换或关窗替代时，不得提交快照，也不得触发一次保存覆盖新目标。双页队列的边界顺序和奇偶分组不能因修复丢失。

### 最小实现建议

保留既有请求游标及导航队列，新增明确的已呈现快照，至少含页／跨页起点、可见页数和适用模式；缩放取最后实际呈现状态，模式切换尚未成功时不把另一模式的默认值当成新事实。不需要数据库schema变化。

- 单页：完成当前版本的有效bitmap后，一次性提交图像、已呈现页和标签，再ScheduleSave。失败可保留旧图及重试目标；重试仍针对失败目标，不能因回退快照而重试错页。
- 双页：先用局部变量取得规范分组、长度及全部位图，成功后统一提交图像、显示索引和快照。若产品选择显示部分成功侧，也必须明确它不算完整跨页成功，进度不提前完成。
- 模式入口：从上一个稳定位置取目标；快速切模式后只允许最新入口提交。未完成呈现的新模式关闭时，保留或flush上一个稳定快照，避免抹掉刚成功但未防抖保存的A。
- 书签：按已成功呈现位置创建，或在首次尚无成功画面时禁用／不给错误坐标；不能借本轮扩大到像素级书签。
- 保存：以快照计算单页`(page+1)/count`或双页`(start+visibleCount)/count`。Webtoon已就绪的真实视口快照与小说链路继续保留，不将现有防止未初始化覆盖的guard删掉。

### QA可观察场景

- 正常成功与重开、损坏中间页、损坏末页、成功A→失败B→立刻关闭（防抖内）、全部首次页面失败仍保留旧历史。
- 双页完整旧对→目标仅一侧损坏→关闭；以实际图像像素、页号和DB同时判断，不能仅读私有字段。
- 失败重试成功、加载未完成时关闭、未完成请求切模式、旧异步完成不能写回；原有31项逻辑回归仍适用。

## REQ-02 PAX长路径与有界索引

### 推荐方案

优先使用内置`System.Formats.Tar.TarReader(fs, leaveOpen:true)`和`GetNextEntry(copyData:false)`；私有索引保存最终`Name`、`DataOffset`、`Length`，维持自然排序、共享随机读取锁、单页体积限制。公共`IComicSource`无需修改。

已核对.NET10源码：seekable数据块以流定位跳过payload，`TarReader.AdvanceDataStreamIfNeeded`将指针设到前一entry的头／数据／对齐末尾。只读取元数据建立索引而不访问返回entry.DataStream时，不会复制普通图片payload到内存。返回的DataOffset是数据起点，不能继续叠加旧版512字节header。

### 必要的分配前元数据约束

直接替换成TarReader不足以满足有界元数据验收。v10`TarHeader.ReadExtendedAttributesBlock`和`ReadGnuLongPathDataBlock`会先`ArrayPool.Rent((int)size)`，调用者的页读取guard来不及拦截。

建议在每次`GetNextEntry`前，基于已知下一header位置做极小前置检查：

1. 保存并最终恢复FileStream.Position；初始为0，后续不消费entry.DataStream，正常reader返回后位于下一header。
2. 只扫描连续的PAX `x/g`、GNU `L/K`元数据头（如支持旧`X`需明确）；头部size须先合法、非负、checked计算512对齐、声明payload处于文件范围内，并小于固定元数据上限（建议1MiB）。
3. 元数据自己的raw size控制预扫，不能套入PAX global size；遇普通entry即停止，不扫描其payload，不自行解析path或重写PAX语义。
4. 每条metadata在进入TarReader分配前已受限；具体Position不变量须由Core实现复核和实际测试确认，不能只凭此设计当作已验证。
5. reader取得普通entry后再校验DataOffset、Length及文件范围。保留PageReadGuard；截断页不能作为成功的完整图片返回。

本轮图片条目范围为RegularFile／V7RegularFile／既有ContiguousFile。符号链接、硬链接和稀疏文件不当作常规图片索引；不调用ExtractToFile，不创建归档路径。常规PAX局部path（包括UTF8长名）和size交由标准库解释，不宣称完整POSIX全局属性应用、hdrcharset=BINARY、所有多global链或混合格式均支持。标准库对损坏及部分metadata序列的拒绝范围如实列明。

### QA可观察场景

常规短名、PAX超100字节长名、中文UTF8长名、同包短名与长名自然排序；GNU LongName及USTAR回归；next-entry不被旧path污染；伪造大元数据声明、截断元数据和无图片包应有界失败。正常图片读取须和预期像素／尺寸一致，不能只验证索引数量。

## 文件责任建议与交接

|责任人|建议独占文件|说明|
|---|---|---|
|Core开发|TarComicSource.cs，必要Core自测独占位置由leader分配|不改其他格式及公共接口，不自行构建共享目录|
|桌面开发|ReaderWindow.xaml.cs、ReaderWindow.ComicNavigation.cs及必要partial（由leader确认）|生产请求／呈现／保存链路同一owner；不要同时由另一人改主Reader文件|
|QA|SmokeTest.OpcReliability.cs、qa-plan/qa-report、隔离证据目录|独立生产实现，可加入真实控件及DB观察夹具|
|leader|SmokeTest.cs、版本/说明、控制记录、构建/打包工具|统一接入QA入口与构建；最终源码及工具冻结后才验收候选|
|架构|本architecture.md、后续review.md|只读复核实现与证据，不代签QA|

除本文件外没有修改生产或测试文件，没有启动任何产品／构建／测试。下游：leader决定READY；Core/桌面按明确分配实现；QA补实际基线及最终包证据；架构在非作者身份下复核。

## 官方资料（核对日期2026-11-03）

- [TarEntry.DataOffset API](https://learn.microsoft.com/en-us/dotnet/api/system.formats.tar.tarentry.dataoffset?view=net-10.0)：数据流相对归档起始位置；不可寻址返回-1。
- [TarReader.GetNextEntry API](https://learn.microsoft.com/en-us/dotnet/api/system.formats.tar.tarreader.getnextentry?view=net-10.0)：copyData=false不复制payload；部分损坏与metadata序列会拒绝。
- [.NET10 TarReader源码](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Formats.Tar/src/System/Formats/Tar/TarReader.cs)：seekable跳转和metadata处理交接。
- [.NET10 TarHeader读取源码](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Formats.Tar/src/System/Formats/Tar/TarHeader.Read.cs)：PAX局部字段处理与metadata按声明大小分配。
- [Microsoft.Data.Sqlite事务](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions)、[SQLite事务语义](https://www.sqlite.org/lang_transaction.html)：关联语句的原子提交需要事务边界；本轮不据此宣称已实测provider故障。
