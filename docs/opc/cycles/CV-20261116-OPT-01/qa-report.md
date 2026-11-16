# CV-20261116-OPT-01 最终独立 QA

**最终候选 PASS，正式交付后独立核验 PASS，无阻塞缺陷；仅放行下表指定哈希的同一制品。** qa_acceptance 未参与产品实现，独立编写获分配的验收夹具，并于 2026-11-16 在本机独立解包后执行最终包。QA没有本地构建、重新打包、修改冻结生产／测试／工具输入、替换正式程序或公开发布；正式同制品复制由leader完成。

候选 comicverse-candidate-1.4.0-43837235527-1；build-release **43837235527 attempt1**；版本 **1.4.0**（FileVersion 1.4.0.0／ProductVersion 1.4.0）；源码提交 **bfc7da90a9c2d8df79050068ed2030793d88eee0**，基线 37ade220ca356f1cee40150b9ef7dc6335b91de2。

|对象|SHA256|
|---|---|
|外部EXE／ZIP内EXE／QA独立解包EXE|598D5FFB5457F912D3C9BB07EB838405A39040A89540839D41AE1C992258FB0C|
|ComicVerse-Portable-v1.4.0-win-x64.zip|BD57370801722522C7B669AC4D3A0D2FF1D13A8E2567E7B04AC6897D18219EB8|
|source-freeze.json|1FDAAA2511C8AC8DDF4401A19A5F1A8B0300202A7D1BEF59E11BEBB2612D6E3F|
|原始Git源码ZIP|D9584C886CED9696D99B08822D0DCB5D97D5ECEDA5A504F9045C54588830FD70|

EXE 75,085,372 字节。批准的实际运行目录为 [final-43837235527-1/bundle](../../../../artifacts/opt-01-qa/final-43837235527-1/bundle/)，批准便携ZIP位于 [GitHub候选目录](../../../../artifacts/opt-01-candidate/cloud/comicverse-candidate-1.4.0-43837235527-1/)。

## 来源、隔离与前后完整性

独立校验 leader 下载的两个 GitHub artifact 的 digest、run／attempt／commit、全部压缩条目和解出文件，不重复下载大包。原始 Git ZIP 的 **155 个文件**逐项匹配，候选 manifest 的 **90 项输入**无遗漏，覆盖源码、测试、工具、workflow、样例和使用说明；冻结 PRD／交互／技术审查另由原始源码ZIP完整性覆盖。未将本地CRLF与云端LF差异冒称同字节。

运行前 **719 项核验通过**；运行后 **1275 项核验通过**。两次快照共有 **555 项来源／制品／原始云端证据／独立解包／QA工具与配置文件，0 差异**。版本、包内外EXE、全部包材料、manifest本身和每项输入均重新核验；冻结构建路径的 Git diff 为空。run/control 的过程更新不作为构建输入，未污染冻结来源。

三次真实运行均使用冻结的 tools/Invoke-IsolatedProcess.ps1、同一独立解包EXE、各自新的 data 与 Win32 Desktop，App 实际 PID／Desktop 与 runner 相同，SmokeProcess=true，ErrorMode32771。不切换用户桌面，不注入用户鼠标键盘，不读真实书库，不启动临时WPF宿主。

|本机实际执行|起止时间（+08:00）|结果|
|---|---|---|
|完整 --smoke，使用包内 samples|17:18:42.120–17:20:43.608|**121.487秒，exit0，未超时**|
|--smoke --verify-failure-exit|17:21:11.614–17:21:12.189|**0.574秒，exit1，未超时**，准确 intentional dispatcher failure|
|--smoke --verify-task-failure-exit|17:21:21.348–17:21:21.908|**0.560秒，exit1，未超时**，准确 intentional unobserved task failure|

完整 smoke PID38848，Desktop ComicVerse-QA-689e48c693b84d1bb908989f3eb5b739；两个反例分别 PID35416／19564，Desktop 后缀 b9edc7992c7040e3a91ed907f4c1e1e6／6d868feae255482abce452530d36026e。三次串行且桌面不同，正常运行 stderr0、无 crash 文件；两个反例的非零退出是预期结果，不作为正常运行成功。

同提交云端 Core **43/43**；云端最终自包含包完整 smoke **118.899秒 exit0**，参数确认 win-x64／self-contained true／single-file。云端结果仅作来源与辅助证据，本次本机同包结果为新执行，未沿用任何历史 PASS。

## 需求验收与回归

REQ-CV-20261116-OPT-01-01 新夹具 **60/60**；旧 logic **31/31**、OPC **72/72**，以及排版 **30/30**、查找 **16/16**、EPUB UI **8/8**、漫画／小说／书架／PDF 等完整适用回归通过。旧OPC保留正式CR口径；未删改旧判据或降低期望。

|PRD AC|最终同包结果与证据|
|---|---|
|AC-01|按钮／Enter正常提交；Ctrl+G绑定命令从30页漫画第2页到第17页，画面P17、底栏17/30、DB16及预定像素一致|
|AC-02|页3／4归规范组、奇数末页、RTL仅交换左右、横竖混排、双页完整组重开通过；底栏与DB组首一致|
|AC-03/04|条漫远距／近邻／首末／不等高／不足一屏及同页完成；末页输入9的真实offset3693.43，目标页可见、底栏8/9与视口／DB7一致；不等高目标3实际offset1542.36。单物理页PDF按3个逻辑切片跳到红色第3片，底栏3/3、DB2|
|AC-05/06|12类错误输入、空白前导零纠正、Esc／取消按钮／背景取消、重复命令全选、编辑快捷键隔离、WPF焦点正反循环、隐藏栏与全屏场景通过|
|AC-07|初始化前入口和命令不可用；小说漫画跳页入口及命令禁用，小说仍正常显示|
|AC-08/10|单页坏目标仍显示成功页2；双页第二张坏图仍保留完整1-2组；DB不写失败目标，关闭重开恢复成功事实，再输入可读目标恢复|
|AC-09/10|新入口受控慢B被C取代且旧完成不回灌；非法草稿不取消合法pending，后页原始目标跨双页／条漫保持；pending关闭仍保存成功页4|
|AC-11|980宽深色、900×560最小深／浅色错误态与全屏均目视合格；原导航、滑块、书签、缩放、小说等由本候选完整回归补充|

条漫遵循 PRD／TA-B01 的真实视口／offset 语义，本次不把单／双页解码成功快照保护套用到条漫坏图，也未扩大 Viewer 的实现范围。

## 截图与日志人工复核

QA目视复核 **17 张**最终运行截图：全部15张 page-jump 场景图，以及原显式页8跨模式和CR后完整双页4/5两张关键旧回归。入口／错误提示／按钮完整，深浅最小窗口未裁切；真实像素、底栏、滚动及数据库断言相符。逐图观察及哈希见 [visual-log-review.json](../../../../artifacts/opt-01-qa/final-43837235527-1/visual-log-review.json)。

代表截图：[30→17](../../../../artifacts/opt-01-qa/final-43837235527-1/full-smoke/smoke/page-jump-30-to-17.png)、[最小浅色错误态](../../../../artifacts/opt-01-qa/final-43837235527-1/full-smoke/smoke/page-jump-minimum-light-error.png)、[RTL双页](../../../../artifacts/opt-01-qa/final-43837235527-1/full-smoke/smoke/page-jump-double-rtl-980.png)、[条漫末尾clamp](../../../../artifacts/opt-01-qa/final-43837235527-1/full-smoke/smoke/page-jump-webtoon-last-clamp-980.png)、[失败保留完整组](../../../../artifacts/opt-01-qa/final-43837235527-1/full-smoke/smoke/page-jump-double-failure-980.png)、[PDF第3逻辑切片](../../../../artifacts/opt-01-qa/final-43837235527-1/full-smoke/smoke/page-jump-pdf-logical-slice.png)。

本次完整日志 **5 条 ERROR** 全部逐项复核：PDF提取位置回退私有native缓存（后续PDF通过）、精确 novel_settings_book_27 SQLite写失败注入（恢复通过）、非法记录长度／截断元数据／超1MiB的3个PAX样例安全拒绝。没有未处理异常或额外未解释错误，未使用历史通配白名单掩盖新问题。

summary中的 enabled=False 表示无成功画面时禁用书签；panel=False 表示浮层按预期关闭；clickCaptured=False 配合 released=True 表示单击不残留捕获。短小说本章不可滚动的子模式恢复场景明确 skipped，另有生成长篇的实际滚动恢复通过，不将 skipped 本身计为已执行。

## 覆盖限制与交接

Ctrl+G 验证真实 KeyBinding、CanExecute／Execute 命令路由，未做OS修饰键注入；Enter／Esc等走WPF事件路由。Tab检查Cycle配置与MoveFocus正反遍历；文本赋值触发TextChanged，未操作系统剪贴板。这些证据不等于真实用户键盘／剪贴板可用性已全部验证。

测试使用合成私有数据和包内样例。没有穷尽枚举中／空源等所有中间状态、全部DPI／设备竞态、复杂第三方格式、海量／网络书库、纯净Windows首次native提取、自动隐藏定时器及真实用户满意度；相关限制不因完整smoke通过而消失。

机器结论：[qa-decision.json](../../../../artifacts/opt-01-qa/final-43837235527-1/qa-decision.json)。证据目录含前后快照、三次execution／环境／日志／反例、完整summary／JSON断言、截图及人工复核；准备记录保留为 qa-report-before-final.md。核验脚本准备期间的一次XML参数错误已修正并记录；最终Before之后未修改核验工具，无本轮产品FAIL被改写为PASS。

## 正式交付后独立只读核验

**PASS。** leader于2026-11-16 17:27:30完成同制品复制后，QA独立读取正式文件并完成 **48项核验**：publish/全部 **13个文件**（EXE、说明、LICENSE、样例）与批准快照及当前已测试bundle逐项长度／SHA256一致，无额外／缺失文件；dist/便携ZIP与批准ZIP同哈希，ZIP内13项也逐项匹配。

正式 publish/ComicVerse.exe 为1.4.0（FileVersion1.4.0.0），75,085,372字节，EXE和ZIP哈希仍为本报告首表批准值；publish/没有临时版本子目录。本次只读正式制品、仅写QA证据和报告，**没有重跑runtime、构建或再次交付**。

证据：[delivery-verification.json](../../../../artifacts/opt-01-qa/final-43837235527-1/delivery-verification.json)。本轮“最终候选独立运行→批准同一制品→leader复制→独立交付后核验”闭环完成，QA收尾，交回leader归档；上述覆盖限制继续适用。
