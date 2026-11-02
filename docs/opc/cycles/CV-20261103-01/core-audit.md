# C-01 Core 格式可靠性审查

日期：2026-11-03；角色：core_engineer；基线：`3a892fd`／v1.3.1。当前只读生产代码，未构建、未运行 ComicVerse，未读取真实书库。本文不是产品复现或 QA PASS。

## 可执行候选：PAX 扩展路径被忽略

证据 `E-CV-20261103-01-C01`（源码审查）：`src/ComicVerse.Core/Comics/TarComicSource.cs` 的 `BuildIndex` 遇到 `x`／`g`／`X` 时直接跳到下一头，不解析 `path`。普通头只读100字节name与155字节prefix。后继条目只用该退化名称判断图片扩展名及自然排序。因此合法PAX图片可能被忽略，或以截断／占位名称参与错误排序。这是源码反例；当前未在产品内运行确认。

来源 `E-CV-20261103-01-C02`（官方资料，访问2026-11-03）：[Python tarfile文档](https://docs.python.org/3.12/library/tarfile.html)列明支持POSIX.1-2001 PAX，且Python 3.8起默认创建该格式。它说明这是常规归档变体，不证明当前用户的使用频率。[Microsoft TAR最佳实践源文档](https://github.com/dotnet/docs/blob/main/docs/standard/io/zip-tar-best-practices.md)说明PAX扩展属性可覆盖路径与大小等头字段。未安装或操作归档工具。

反证／范围限制：现有测试 `TestTarSource` 与 `TestData.MakeTar` 使用Ustar及短路径，已经通过；该候选不是“所有TAR都失败”。原实现支持GNU LongName，不能误称长路径全部不支持。OPC历史没有PAX用户报障，触发频率未知。此次不涉及ZIP／RAR／7Z重构。

## 独立 QA 最小验证设计

1. 在独立数据目录生成有效且不同颜色PNG；用 `System.Formats.Tar.PaxTarEntry` 与 `TarWriter` 创建CBT。条目名为110个ASCII字符目录加 `/p10.png`、同目录 `/p2.png`；写入顺序10后2，预期索引2后10，像素可区分。Microsoft[官方PaxTarEntry API](https://learn.microsoft.com/en-us/dotnet/api/system.formats.tar.paxtarentry?view=net-10.0)定义该格式和包含相对路径的Name。
2. 先检查归档实际头：不同writer可能采用不同占位名。若普通头仍有图片扩展，只能证明可能丢路径，不能算漏页已复现。可另造合法PAX局部 `path` 覆盖普通头 `placeholder`（不含扩展），size为真实PNG长度；或使用Python tarfile PAX_FORMAT作为独立参考生成器。
3. 单张长路径包：真实App导入应成功、页数1、显示真实像素；短名+长路径混合包：应显示全体页数，而非静默漏页；自然排序按有效完整路径。
4. 同一包关闭重开一致；局部 `x` 属性不能泄漏到后继没有扩展头的普通条目。
5. 负向：截断／非法长度PAX记录须明确失败且可再次导入正常包；保留Ustar、GNU LongName、现有PageReadGuard上限与并发随机页读取回归。

QA已获leader授权在 `SmokeTest.OpcReliability.cs` 添加真实App自检夹具；由QA自行运行基线，本文不代签结论。

## 最小修复界面与技术选项（尚未实施）

只影响 `TarComicSource.cs` 的索引构建和私有offset记录；保留 `IComicSource`、自然排序、FileStream共享锁、按需随机页读取、PageReadGuard，不增加依赖，不改存储结构。

- 选项A：保留手写索引，增加有上限的PAX记录解析，按UTF-8字节长度读取记录、处理下一条目属性生命周期、path优先级，并明确size对数据偏移的影响。风险是再次自制标准解析器，局部／全局语义容易不完整；不能只用字符串Split解析字节长度。
- 选项B：用已在.NET 10内置的 `TarReader.GetNextEntry(copyData:false)` 读取完整有效名称，记录 `DataOffset` 与 `Length`；后续仍直接定位到原FileStream中的页数据。官方[GetNextEntry](https://learn.microsoft.com/en-us/dotnet/api/system.formats.tar.tarreader.getnextentry?view=net-10.0)说明false不复制数据；[DataOffset](https://learn.microsoft.com/en-us/dotnet/api/system.formats.tar.tarentry.dataoffset?view=net-10.0)为相对归档流绝对起点的偏移。已阅读[.NET 10 TarReader源文件](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Formats.Tar/src/System/Formats/Tar/TarReader.cs)：seekable流通过Position跳过前页数据，局部PAX在读取实际数据之前覆盖属性；不是顺序解压整本。新边界包括内置reader拒绝某些混合格式／重复元数据，以及全局扩展属性的适用限制，不能默认扩大支持承诺。

建议由架构与leader按独立QA真实fixture结果选择B或最小A，确认READY后才动生产文件。若采用B，需要验证索引没有把全页复制到内存、条目范围在文件长度内，以及Ustar／GNU和大小限制未回退。

## 命令与未完成验证

- 全部生产与测试文件均只读审查；命令读取成功。
- 曾尝试只在Python内存生成TAR并输出原始头以检查fixture，`python`指向WindowsApps别名，立即退出1且无输出；未生成文件，未运行ComicVerse。检查无本任务新建进程，未重试该别名。该尝试不算有效运行证据。
- 尚未验证PAX产品实际行为、性能、所有损坏文件或真实用户价值。下一位：QA复现 → planner/leader判定READY → 架构审查技术选项 → 指定文件后开发。

## READY 后实现交接

- 独立QA已在真实App基线复现：证据目录`artifacts/opc-CV-20261103-01-qa-baseline/`，35.3秒、退出1、无超时／crash；标准reader确认长路径包2页，App导入拒绝；混合包预期3页，App为1页。引用QA交接，不冒充Core亲自执行。
- leader批准`REQ-CV-20261103-01-02` READY后，独占实现`src/ComicVerse.Core/Comics/TarComicSource.cs`，独占自测文件`tests/ComicVerse.Tests/Program.TarPax.cs`。没有更改Program.cs、App、版本号或正式制品；统一构建由leader执行。
- 实现选择标准库索引加分配前预扫。每次GetNextEntry之前扫描连续raw元数据头，1MiB单条载荷限制、32条连续元数据限制、非负八进制／GNU base-256解析与checked偏移／文件边界；finally恢复原Position。32条限制避免反复预扫巨大元数据链造成平方成本。普通图片payload不按1MiB限制，不读entry.DataStream。
- 保存有效Name、DataOffset和Length；自然排序与锁内随机读取保留。所有条目范围需在文件内；页读取采用ReadExactly，源文件在索引后截断时明确失败并释放临时流，不再返回不完整的成功流。图片仍受原256MiB PageReadGuard约束。
- 解析能力范围为标准库支持的常规局部PAX path/size、USTAR、GNU LongName及常规图片文件；不索引链接或稀疏文件，不承诺POSIX全局结构字段、BINARY编码和全部元数据链兼容性。大于1MiB或连续超过32条元数据明确拒绝。
- 新自测入口`private static void TestTarPaxReliability(string work)`：长ASCII／中文路径、自然排序、重复重开和并发随机读取字节／解码／尺寸；USTAR prefix与GNU LongName；手写合法局部path及size覆盖raw size=0并检查下一短名条目不受污染；x/g/L/K/X超大声明、截断元数据／页、非法PAX记录、非法八进制／负base-256、过长元数据链、无图片、超大页拒绝。
- 当前开发静态检查`git diff --check`退出0；尚未构建或运行新增自测，不宣称通过。交架构做非作者复核，再由leader统一构建／运行，最终由QA验收冻结制品。
