# v1.3.2 GitHub 发布独立QA

当前结论：**发布前本地控制候选PASS，远端资产下载核验待执行，尚不签远端公开完整性PASS**。用户已明确授权上传GitHub，leader负责commit/push/release；qa_acceptance只审查最终发布输入及远端资产，不实现CI／产品、不自行发布。

## 已批准产品基线

- EXE SHA256 `35AE398E9470B9AF639129C2AE7AF11B604422241F72768104A8DD74CD6AC20E`，版本1.3.2，75,049,529字节。
- ZIP SHA256 `3AF65A824948D64632CA003DCD3691795B6CFA7CC9C77FE251C50418CFDE5355`。
- 已有产品完整独立QA与本地同制品交付通过：`docs/opc/cycles/CV-20261103-02/qa-report.md`；旧105项manifest与原始证据不改写。

## 发布检查矩阵

|门禁|独立核对方式|预期|
|---|---|---|
|授权与范围|本次用户“上传github吧”及leader发布记录|只上传批准产品，不推送未知文件或重新构建成正式资产|
|tag重建发布路径|审查最终build-release触发器、权限和全部step|手动CI只产候选，tag不再重建并自动公开；无softprops/gh release自动发布步骤，权限仅所需|
|统一测试隔离|审查ci/build-release的dotnet构建、Core测试、WPF冒烟命令|统一`Invoke-IsolatedProcess.ps1`、实际App --smoke、新数据目录；非零／超时保留失败，日志及桌面信息可检查|
|新发布冻结|旧105项与新发布manifest逐项比对，检查差异白名单及新增资料|仅授权CI与发布资料改变；产品／运行工具／样例／包字节仍与批准输入一致，原PASS不转给重建产物|
|正式上传制品|独立EXE/ZIP/ZIP内EXE/说明hash与版本、条目核对|全部同字节；新增发布QA／校验资料单独标识，不重新打包改变产品ZIP|
|必要运行验证|对同一EXE按新冻结发布输入进行适用独立隔离完整smoke|不重build；以真实退出／Desktop／数据／日志／截图为证，不以静态CI审查冒充云端CI实跑|
|Git/Release一致性|核对提交、tag目标和草稿／发布资产列表|发布绑定审过来源；公开资产为批准EXE/ZIP，不能被CI未经验收的重建覆盖|
|远端完整性|发布后从实际GitHub asset下载到本次QA目录重新SHA256|远端下载EXE/ZIP与批准本地hash一致，记录asset URL／ID及大小；不能只相信上传命令或声明校验值|

Windows运行仅独立Win32 Desktop和隔离测试数据，不操作用户桌面或真实书库；不创建临时WPF宿主。下载的是软件校验制品，不是媒体；长命令有进度、超时和保留证据。仅QA指定目录可写。

## 待验证与限制

静态YAML／PowerShell语法及调用检查不能证明GitHub runner真实可创建独立Desktop；云端执行仍待leader推送后观察，不能以本机运行冒称云端实跑。原产品未覆盖（复杂商业文件、大库、所有DPI/编解码器等）继续适用；远端字节一致也不等于真实用户价值验证。

## 发布前独立最终验收（PASS）

冻结记录：`artifacts/github-v1.3.2-release-freeze/release-freeze.json`，SHA256 **`07659A2BBD8BA511117A1269753574230982B6AEF869B08F28ECA0DB227E22EF`**。独立证据：`artifacts/github-v1.3.2-qa-final/`，含核验脚本、YAML／PowerShell解析结果、运行前后记录、实际smoke证据与`qa-decision.json`。

- 105项来源与原批准manifest逐项映射：恰5项发布控制白名单变化（2workflow及AGENTS／README／WORKFLOW发布说明），其余100项含App、tests、runner、samples与原批准字节一致。旧manifest保留原SHA256，不把5个变化伪写为旧清单0差异。
- 5份不可变发布snapshot和4个待上传资产全部核对大小／SHA256；SHA256SUMS的3行准确覆盖EXE／PortableZIP／approved-sourceZIP。发布源归档SHA256 **`8CFD4E37C66EE179137ED7A593BFB75D8EE6B6FB49B30DB6F9C2326C3B15D124`**，109项中105项逐项对应原批准源输入，另4项为manifest／candidate／最终PASS／交付核验证据。
- PortableZIP 13项与原批准payload逐项相同；候选、ZIP项流与本次独立解包EXE仍为批准`35AE...C20E`，没有重build或重打产品包。
- 最终2YAML经PyYAML解析，16个CI run块经PowerShell AST解析0错误。build-release只有手动触发、contents:read、无公开Release步骤；CI触发branch push／PR／manual。11次构建／候选发布／测试启动全部经隔离runner，独立证据和有界超时、真实退出码保留。3文档差异仅同步发布控制及链接，没有削弱独立QA／同制品门禁。

### 同一EXE在新发布控制下的完整实际自检

2026-11-03 **01:44:45–01:46:37 +08:00，112.19秒，退出0，未达到300秒限制**。实际运行本次从批准Portable ZIP独立解包的EXE与包内samples，PID15364，App与runner Desktop均为`ComicVerse-QA-382a82f15f2b4444ad4a874222000089`，ErrorMode32771／SmokeProcess=true，独立数据目录，无用户桌面／真实书库。

**原logic31/31、可靠性72/72（原60＋正式替代1＋新增11）、排版30/30、查找16/16、基础EPUB UI8/8及漫画／小说／搜索／书架／PDF等完整适用回归均通过。** QA目视原阻塞页8底栏9/12、CR完整4-5底栏5-6/6及失败保留旧双页组截图，与位图／页号／DB一致。不可滚动短例的skipped仍由生成长篇补充验证，不将skipped计为通过。

stderr0，无smoke-crash／其它smoke故障标记。5条ERROR逐项核对：01:44:46 pdfium私有缓存回退（PDF过）；01:46:20 fixture键`novel_settings_book_27`注入SQLite失败并恢复；01:46:23三种明确非法PAX输入的安全拒绝。不是任意忽略错误。完整summary SHA256 `D58E68C36C5EEAEED6F4E2C022CC5E0D0CAC2830AE2BA52F5EBD48EB2D09DDBB`。

运行后新发布freeze的105来源／5snapshot／4资产与13个包内容均0差异，5项与旧产品清单的预期差异仍原样可追溯。**此PASS允许leader提交／推送并按计划草稿上传同一4资产；公开前仍须独立实际下载远端资产SHA核验。** 不将本机静态CI检查／本机App运行写成GitHub云端CI成功；远端最终结论另记后续章节。
