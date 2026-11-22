# CV-20261122-UPDATE-01 独立 QA 合同

2026-11-22，Asia/Hong_Kong。QA 未参与被验收的产品实现；本文件是验收计划，**不是运行 PASS**。实际范围依据本轮 run.md 的 REQ-01/02/03，以及 leader 后续确认的架构接口。未决接口须在冻结前对齐，不能以修改测试掩盖失败。

## 范围、证据与文件归属

- 用户目标：Windows x64 免安装版在应用内手动检查官方稳定版，有新版时点击下载、校验、保存退出、替换和重启；失败仍保留可用程序和书库。
- 产品候选版本1.5.0；公开v1.4.0无此入口，必须先安装含功能版本。不能宣称旧v1.4.0已获得自动升级能力。
- 实际官方资料 E-UPDATE-01：2026-11-22只读查询的公开 `https://api.github.com/repos/rearcherr/ComicVerse/releases/latest`，稳定版v1.4.0，Release454449683、EXE779079778，75,085,372字节，摘要598D5FFB5457F912D3C9BB07EB838405A39040A89540839D41AE1C992258FB0C。复用 `artifacts/github-v1.4.0-qa/public-latest.json` 及已完成的公开完整性核验；不得向GitHub上传测试版本或改Release。
- 版本分支、网络与坏文件使用明确标记的合成HttpMessageHandler/流夹具；这不是已发布的未来版本，不等于真实网络兼容性证明。实际GitHub查询仅只读公开元数据，不为普通smoke依赖联网或下载远端EXE。
- QA 独占本轮qa-plan.md、后续qa-report.md及新 `src/ComicVerse.App/SmokeTest.OnlineUpdate.cs`。leader独占共享SmokeTest.cs入口和进程交接实现；Core/桌面作者修改产品；QA不派生子角色，不改其实现。

## 正常与异常场景矩阵

|场景编号|需求|操作／夹具|可观察通过标准|
|---|---|---|---|
|UP-Q01|REQ-01|真实设置入口→检查；固定官方v1.4.0元数据，当前同版本|清楚显示已最新；不下载、不启动辅助进程、不改EXE或进度|
|UP-Q02|REQ-01|稳定新版；当前版本小于目标|显示当前／目标版本和准确资产大小；只有点击更新并重启才下载，检查本身不退出|
|UP-Q03|REQ-01|当前版本大于远端、1.10.0与1.9.0、可解析的v前缀／附加版本信息|数值版本比较正确；不降级或把字符串排序当版本排序；未支持格式明确安全失败|
|UP-Q04|REQ-01|draft／prerelease／无Release／无效JSON或tag|稳定版策略明确；不把错误响应或无效版本当可更新目标；当前程序保持可用|
|UP-Q05|REQ-01|404、403限流、断网／连接错误、超时；随后重试成功|错误可见且可重试，不卡UI、不自动退出；正常重试不被旧错误回调覆盖|
|UP-Q06|REQ-01|检查期间取消／关更新窗；旧请求随后完成；重复点击检查|取消完成且不发布旧结果；只保留有效最新操作，不产生并发下载或多个更新助手|
|UP-Q07|REQ-02|下载有效同源EXE，分块响应、已知长度|显示合理进度和阶段，UI仍响应；正确SHA、长度和实际程序版本全部满足才进入准备重启|
|UP-Q08|REQ-02|下载期间取消／关更新窗；半文件及迟到回调|当前程序与书库不退出，目标EXE不变；半成品不会被当成完成包，不进入替换；可再次检查／下载|
|UP-Q09|REQ-02|缺失／名称不符／错误平台／多个歧义EXE；非法来源URL；缺失或格式错误SHA|明确拒绝不合格目标；不以下载后自算SHA替代可信预期摘要；不启动下载文件|
|UP-Q10|REQ-02|HTTP长度不符、截断流、超限声明／实际超限、流中途异常|资源有界；验证失败可见且当前程序继续运行；不进入退出或替换，不修改现有EXE|
|UP-Q11|REQ-02|SHA不符、内容非PE、PE版本与Release不符、新旧版本关系不符|校验失败；UI保留错误和重试路径；旧程序与原进度保持；正确哈希不能替代版本检查|
|UP-Q12|REQ-01/02|检查／下载／取消／失败／已最新的真实UpdateWindow按钮和进度|按钮启用状态、反馈、取消、重试及关闭行为符合阶段；深浅主题／最小支持窗口不裁切；不只断言服务返回值|
|UP-Q13|REQ-02|漫画成功页尚未到保存防抖→确认升级退出；小说当前章／滚动／排版草稿|实际关闭前持久化最近成功事实；新进程重开同一隔离库位置／模式／设置正确；不能因pending目标覆盖成功页或回到更旧DB|
|UP-Q14|REQ-02|多阅读窗或更新窗仍开着→确认升级；交接启动失败／退出保存失败|不能在旧程序仍持有目标文件时强制覆盖；交接失败不静默丢进度，保留可读旧程序／明确恢复方式；保存失败语义须架构定稿|
|UP-Q15|REQ-02/03|冻结实际App在隔离程序目录完成下载、旧进程退出、替换、重启|目标路径不变，新EXE哈希与批准测试载荷相符；旧PID真实结束，新PID/实际EXE/Desktop/data匹配；书架与隔离DB初始化完成后才确认启动成功|
|UP-Q16|REQ-02/03|受控锁定／拒绝写入导致replace失败|未把半写文件留作唯一程序；旧EXE或明确命名的恢复文件大小／哈希正确，可实际启动；库和阅读内容未被替换|
|UP-Q17|REQ-02/03|替换完成后relaunch失败或未达启动确认|不得提前清掉唯一旧备份；按合同恢复旧程序并证明可启动，或明确记录保留的恢复路径／错误；不能将Process.Start返回视为书架已就绪|
|UP-Q18|REQ-02/03|旧进程未退出／超时、取消与提交竞争、并发更新尝试|等待有界，失败证据保留；不误关其它程序、不并发覆盖同一目标、不把旧回调当新更新完成|
|UP-Q19|REQ-03|所有fixture和重启链前后路径、库、源包、EXE核验|所有子进程继承隔离Desktop和独立数据；只在本轮隔离程序目录写入；不访问真实书库或替换publish正式EXE；旧／新／备份／暂存均有哈希记录|

上述是场景组，不预报自动化判据数量。最终报告按实际执行明细给出通过、失败和未覆盖；不能用服务单测或截图替代真实文件替换／进程重启结果。

## 当前只读审查与待对齐项

1. 现有App.OnExit释放Library；ReaderWindow.Window_Closing会SaveNow并停止任务，但保存异常仅记录日志后继续关闭。升级需要明确“保存失败是否中止交接”的可观察合同，不能将Close返回当作DB一定持久化的证据。该发现是源码观察，不是本轮实测缺陷。
2. 原SmokeTest运行在实际App、OnExplicitShutdown；普通内存服务fixture不能证明旧进程退出和新进程初始化。成功替换与回退应采用实际App的专项协议，由leader/架构定义一次性交接、启动确认与超时，QA独立检查真实文件／PID／Desktop／DB。不能新建WPF probe宿主。
3. 稳定版本判断、允许的下载来源与重定向、最大资产大小、检查／下载／等待退出／启动确认时限由架构定稿，并以边界及超限用例验证。不要求用无限等待或全盘权限操作制造失败。
4. leader已提出产品1.5.0及单次同源1.5.1测试载荷。该载荷必须有来源冻结、独立标识和SHA，不公开为Release，不用任意下载的EXE充当fixture；实际进程验收须如实区分真实PE版本变化与仅版本响应模拟。

## 实施与运行安排

第一步在获分配partial实现 `CheckOnlineUpdateAsync(string outDir, string[] args)`，返回 `Task<(bool Ok,string Detail)>`。leader接入 `--online-update-only` 和full smoke退出条件。先覆盖服务网络fixture，以及注入真实GitHubUpdateService的UpdateWindow检查、发现、取消、失败；普通完整smoke不真的退出整条测试进程去安装未知载荷。

Core拟定接口为 `GitHubUpdateService(HttpClient? httpClient=null)`、`CheckAsync(Version,CancellationToken)`、`DownloadAsync(UpdateRelease,string,IProgress<UpdateDownloadProgress>?,CancellationToken)`；App的AppUpdateCoordinator及UpdateWindow提供internal注入。测试仅通过实际UI事件和公开协议观察结果，不读取私有状态作唯一断言，不要求修改生产网络安全策略以配合夹具。待具体模型与进程协议定稿后接线。

已对齐实施边界：Core另支持UpdateServiceOptions注入有限check/download超时；固定稳定v三段、唯一uploaded ComicVerse.exe、asset≤256MiB、metadata≤2MiB，手动校验可信重定向。AppUpdateCoordinator及UpdateWindow按internal构造注入；真实控件ActionButton、CancelButton、DownloadProgressBar和状态文本作为可观察结果。

leader批准框架依赖build-bin的下载UI三场景明确SKIPPED（安装器要求免安装自包含版）；该运行仍执行全部服务fixture及检查UI，不能代替最终包。最终self-contained候选必须实际执行进度取消、坏SHA及无效PE失败留窗，不能保留同一SKIP后签PASS。

实际事务入口已确认：`--smoke --online-update-only --online-update-install-fixture --update-fixture-exe=<path> --update-fixture-sha=<sha> --update-fixture-root=<root>`。fixture在真实App内强制旧EXE、载荷、数据和输出都属于指定隔离根；确认载荷真实PE版本更高及SHA正确，准备可见成功页后点击真实更新按钮。`online-update-install-prepared.json`写BookId、零基Page、Mode、DataDir、ParentPid及原版本/EXE哈希供外层独立核验。

事务的isolator根为有界PowerShell观察脚本，子进程始终实际ComicVerse App；避免旧App正常退出后runner关闭整个Job导致假失败。helper与重启App保留--smoke错误fatal；新版正常到书架后写 `online-update-restarted.json`（PID、实际Desktop/EXE、Version、DataDir、Result和Books/ReadingPosition）。replace失败可用准确测试EXE FileShare.Read锁注入，允许读而阻止File.Replace；relaunch失败按同一恢复协议另验证。QA不因同源测试载荷而省略真实新旧PE版本、路径、文件和DB恢复证据。

专项及最终运行均由leader先隔离build／冻结；QA不自行构建。测试通过 `tools/Invoke-IsolatedProcess.ps1`，真实App --smoke、独立Win32 Desktop和数据，不切用户桌面。所有HTTP夹具、流等待、文件锁和子进程均有上限并finally释放；保留真实退出／超时／stderr／crash，未处理异常直接FAIL。

最终候选必须重新独立验收：核对源码／工具／fixture清单、版本、包内外EXE和ZIP SHA；从最终ZIP独立解出运行完整适用回归及专门升级／回退协议。旧logic31、OPC72（正式CR口径）、页码跳转60、排版30、查找16、EPUB8及其它适用回归保留；新场景不能代替旧回归。运行前后来源、制品、测试工具零非预期变化。

QA明确最终PASS后leader才可复制同一制品至正式位置；交付后再只读核验。同一轮候选发生任何产品／工具／包字节变化须重新冻结和独立验收；远端公开另遵守用户授权与同制品下载核验。未覆盖的设备／DPI／第三方代理／权限组合和真实用户体验均在最终报告保留，不以自动化通过外推。
