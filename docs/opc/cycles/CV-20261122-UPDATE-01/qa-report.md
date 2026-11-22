# CV-20261122-UPDATE-01 最终独立 QA

**指定v1.5.0-rc1候选 PASS，正式同制品交付后独立核验亦 PASS。** QA未实现产品更新服务、UI或安装器；只维护获分配验收partial及证据。QA没有构建、替换正式程序或公开本轮Release；正式复制由leader完成。1.5.1仅为同源测试载荷，不是本次交付版本。

## 最终制品与冻结来源

基线提交528d203ce773c9c2f214fe20ea82c4c5973c5e3a；实际未提交改动由103项冻结清单标识，不以基线冒充候选完整源码。候选信息为 `artifacts/online-update-rc1/candidate.json`；独立证据位于 `artifacts/opc-CV-20261122-UPDATE-01-qa-final/`。

|对象|SHA256|
|---|---|
|1.5.0 EXE／ZIP内EXE／QA独立解包EXE|71451621B9630C969B25007807B5D594E8F7D8FEA415902FCEC0A89B01A09B93|
|ComicVerse-Portable-v1.5.0-win-x64.zip|F52F175B0D878D7A01396C3D87FB868BFC897715A88701EB593ED9EC89AD9AD8|
|最终qa-source-freeze.json，103项|2671F0D0AF576B816D89B33E76965E64A4CDC782481AFD8CEDBB06110757BBAA|
|保留的初始source-freeze.json|635E7F4221CEC0B513094019A4E7463A0FBCE3C6A7E018C60A969D5FE8045347|
|仅测试的1.5.1 EXE|6D33BE397BDE659F4FF19E85EF15724C9F157892E06845925AD9C33019E627FE|

交付EXE 75,121,611字节，FileVersion1.5.0.0／ProductVersion1.5.0；便携ZIP69,771,513字节，13个文件。测试载荷75,121,614字节，实际PE版本1.5.1.0／1.5.1。

初始构建清单期间qa-plan仅追加已确认接口／隔离事务说明，DF99...1006变为87C0...533F，原构建脚本据此终止并保留失败。leader随后保留原freeze，独立重冻结验收清单，只接受该1项非构建说明差异，其余102项逐项相同；EXE／ZIP／测试载荷未重建。QA自行核对两清单、完整输入枚举、版本、13项包内容及各项来源：前置160/160、后置161/161，**123个来源／制品／独立解包／QA核验脚本快照前后0差异**。

## 实际运行结果

所有运行使用统一Invoke-IsolatedProcess、独立Win32 Desktop和数据，不切用户桌面，不操作真实书库，不新建WPF宿主。升级事务的runner根是PowerShell观察脚本，子进程均为实际ComicVerse App；下载仅注入已知本地载荷，GitHub元数据和下载故障由合成HTTP提供。

|运行|实际结果|关键证据|
|---|---|---|
|最终ZIP独立解包EXE完整 --smoke|21:18:40–21:20:42 +08，**122.626秒、exit0、未超时**|PID34220，Desktop a62dce6cdf00423ea97c1c44a12bcf3a；新更新46/46，无SKIP|
|实际1.5.0→1.5.1更新|**17.494秒、根exit0、未超时**|旧40524→新15488，两App正常exit0；Desktop719de3f71b194b4ab730a7fe4017f7c6；原路径新SHA6D33...27FE|
|FileShare.Read阻止替换，恢复旧版|**17.915秒、根exit0、未超时**|旧30452→恢复进程40080，两App正常exit0；Desktopce73cb25600f4e6ca8f710beca85196e；Result=restored、目标旧SHA7145...9B93|

完整回归的明细原logic31/31、OPC72/72（原正式CR口径）、页码跳转60/60；排版30/30、查找16/16、EPUB UI8/8，以及书架／漫画／小说／PDF等适用旧回归通过。短样例不可滚动子场景仍明确skipped，由生成长篇实际滚动恢复补充，未把skipped本身计为执行成功。

本轮冻结来源另由leader实际隔离编译并执行Core控制台测试：43/43、3.490秒、exit0、未超时；QA独立检查start/result桌面与PID、stdout、stderr及文件哈希，记录core-evidence-review.json。未冒称QA亲自重复Core执行。

两次最终事务均验证原工作目录与程序目录不同、最初COMICVERSE_DATA_DIR相对路径在重启链中解析为同一绝对测试库；prepared记录可见页4/6、当时DB仍零基0，新书架ReadingPosition为零基3／paged，旧程序退出前创建的一致数据库备份亦为3／paged。QA独立只读检查实际数据库与备份的progress行，SQLite integrity_check均ok。实际目标EXE哈希、旧新PID、路径、Desktop、重启书架与DB均匹配；助手／锁／暂存目录清理完成。

## QA工具失败、修正与环境观察

原冻结事务脚本的两次运行分别17.532秒和17.1秒exit1，均通过更新／恢复、同库进度、字节和清理检查，但在合并的正常关窗判据失败，原证据保留于transaction-success／transaction-replace-blocked，不能声称原脚本原样通过。

leader授权QA在证据目录复制诊断脚本，不改冻结脚本或产品。显式枚举同一隔离Desktop、核对准确PID／EXE后确认.NET MainWindowHandle选中了**UAC_InputIndicatorOverlayWnd**，真实书架是另一个可见HwndWrapper。向真实书架发送WM_CLOSE确实退出；首次诊断未在进程存活时持有句柄，退出码为null，该FAIL亦保留于transaction-success-window-diagnostic。

最终QA脚本关闭前OpenProcess持有查询／同步句柄，严格核对书架标题、WPF类、PID和文件路径，WM_CLOSE后原生WaitForSingleObject／GetExitCodeProcess取得**真实0**，没有Kill代替正常关闭、把null当0或删减退出断言。成功与阻止替换两路径均用此同一诊断脚本通过，完整原始窗口记录和原生退出码保留。

收尾曾另加“旧进程中途ErrorMode永久不变”审计，得到133/134：恢复路径旧App初始32771、中途prepared32769（少0x2），原因**未证明**。该原严格审计及脚本完整保留。依据既定门禁核对实际App／runner初始32771、新版32771、SmokeProcess、异常文件、真实退出／超时和行为，不把额外的全生命周期不变假设冒充既有要求；中途值作为观察保留。没有通过修改系统或EXE补mask，也没有忽略未处理异常。最终运行审计138/138通过。

## 日志、截图和覆盖限制

QA目视8张最终截图：更新最小浅色发现、取消下载、SHA失败、无效版本失败；旧显式页8条漫9/12、完整双页4/5底栏5-6/6、30→17、PDF第三逻辑切片。文字／按钮完整，状态与位图、底栏及DB断言相符。

完整smoke stderr0、无crash文件；8条ERROR逐条核对为PDF私有native缓存回退、明确novel_settings_book_27写失败注入、3种非法PAX安全拒绝、合成403、坏SHA和无效载荷版本。最终success事务2条PDF回退；恢复事务另有1条精确共享锁IOException，随后旧版本实际启动并恢复进度。没有使用任意错误通配忽略未知异常。

**未实际注入：relaunch创建进程失败、健康确认超时／子进程仍活、保存数据库失败时中止升级、多书多窗同时升级、真实跨schema迁移。** 本轮实际跨进程保存对象为一份合成漫画，小说／排版保存由完整旧回归补充，未声称真实升级事务中的小说恢复也已执行。真实设置入口接线为源码复核；真实UpdateWindow事件由注入服务执行；本轮App没有重新连公网检查或下载远端EXE，官方公开v1.4.0元数据可访问性引用前一发布QA证据。

此外未穷尽代理／TLS环境、权限／磁盘组合、所有设备／DPI／第三方格式与真实用户体验。现有v1.4.0自身没有在线升级入口，用户需先安装含此功能的1.5.0；当前没有公开的真实1.5.1版本，不能将测试载荷描述为可用正式更新。

最终机器结论qa-decision.json，完整运行审计runtime-verification.json、前后verification-before/after.json及visual-log-review.json均位于QA证据目录。交回leader，仅可复制本报告指定同一制品；复制后需QA只读核验。本报告不授权将测试1.5.1或未经远端同制品核验的重建包发布。

## 正式交付后独立核验

leader于2026-11-22 21:32:18 +08记录同字节交付后，QA只读正式文件完成 **145/145 PASS**：`publish/ComicVerse.exe`为1.5.0.0／1.5.0、75,121,611字节、SHA仍7145...9B93；`dist/ComicVerse-Portable-v1.5.0-win-x64.zip`仍F52F...9AD8；`publish/使用说明.txt`仍77E3968B324A7A3558348A0810F86190C97A9108425331B4A616BF6B5A980D1C，并与ZIP内说明相同。

按本工作区既有正式布局，publish准确只有EXE和说明两文件，没有新版本子目录；LICENSE及样例仍完整包含于已批准便携ZIP的13项材料中。QA逐项核对正式ZIP13个文件与实际测试bundle的大小／SHA，103项冻结源码／工具及清单也仍全匹配，未重跑runtime、构建或再次交付。

独立交付证据为 `artifacts/opc-CV-20261122-UPDATE-01-qa-final/delivery-verification.json`，leader交付记录为 `artifacts/online-update-delivery.json`。原放行qa-decision.json（E006...402D）保持不变；其当时记录的预交付QA报告已按原字节保存在 `qa-report-before-delivery.md`（SHA5DAF...F6B3），本节是交付后的追加记录。远端1.5.0尚未公开，本地交付结论不冒称远端发布完成；上述未覆盖项继续适用。
