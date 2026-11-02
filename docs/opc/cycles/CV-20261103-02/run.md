# CV-20261103-02：继续修复 v1.3.2

最终状态：DONE／本地交付完成，执行回standby。正式v1.3.2与独立批准rc2为同一制品，完整验收及交付后核验均PASS。

- 用户新授权：“v1.3.2继续修复吧”。本轮接续未交付的v1.3.2-rc1，默认一轮、120分钟、最多两次QA返修，新预算从0登记；不增加产品功能，不做远端发布。
- 初始核验：上轮105项冻结输入全部未变；失败rc1 EXE仍为03BCF249BC3916F2C3B500ECD6907CEEA5376074EDE53838169B932CD931B373，正式v1.3.1仍为C9F0D6AAC0D269B38FE4C5D56E26275517AACCF007C07900C1212EECAABBBC26。工作区已有上轮未提交源码及OPC配置，全部保留。
- 目标：修复BUG-CV-20261103-01-01，显式页8在同回合切模式后应继续指向8，不能回0；同时坏页／未完成请求不得污染成功进度，已有PAX兼容修复和完整旧回归必须保留。
- 研究路径：使用上轮已验证研究、PRD及最终FAIL证据，按“已明确复现缺陷”的精简流程推进，不重复市场调研。尚不宣称新的真实用户价值已经验证。
- 核心合同：有效但未完成的用户导航意图、已失败的请求、已成功呈现且可保存的位置须分开。导航跨模式可继承有效意图，持久化仍只保存成功位置；晚到旧回调不能回灌。

## 文件归属

|角色|任务|唯一可写|
|---|---|---|
|desktop_engineer|最小导航意图修复|ReaderWindow.xaml.cs、ReaderWindow.ComicPresentation.cs、必要ReaderWindow.ComicNavigation.cs；本轮implementation.md|
|tech_architect|方案与非作者复核|本轮architecture.md、review.md；生产只读|
|qa_acceptance|独立专项及最终整包验收|SmokeTest.OpcReliability.cs、本轮qa-plan.md／qa-report.md、artifacts/opc-CV-20261103-02-qa*/；不改旧logic31判据或产品|
|interaction_designer|意图／失败／成功语义走查|本轮interaction.md；生产只读|
|product_planner|故障验收和最终状态|本轮requirements.md；不更改冻结旧规格|
|leader|登记、整合、统一隔离构建、打包和同制品交付|control、BACKLOG、本run、共享SmokeTest.cs（若必要）、版本／使用说明、打包记录|

子角色沿用各自.codex/agents角色指令，禁止派生。Core本轮不改；市场／用户材料复用，阶段9再更新效果。构建和UI测试统一独立Win32 Desktop与独立数据，实际App --smoke入口；长命令和等待沿用command-hang-guard、wait-subagent-guard。

## 初始证据与阶段

E-CV-20261103-02-01：上轮最终完整smoke120.21秒退出1，logic30/31、新61/61；失败为12页合成漫画：分页0成功→同回合条漫→分页→显式页8→双页→条漫，实际视口0／底栏1/12／DBwebtoon0。旧判据和原始证据保留不修改。

E-CV-20261103-02-02：本次只读源码确认GetComicModeEntryPage()仅取成功快照，需核对其覆盖pending显式请求的路径；方案由架构与交互验证后交开发，最终仍须独立整包PASS。

## READY 与实施

架构／交互／QA已对齐三态合同，leader批准进入实施：独立generation绑定有效pending原始目标（及仅同意图适用fraction），模式继承不新建优先意图；current成功后消费，current失败仅撤自己的意图，取消不当作失败；持久化、书签、底栏只取成功快照。双页未完整成功前不能把奇数目标9量化为8；显式jump不继承旧fraction。未增加新功能／Core改动／存储迁移。

早期验证必须包含原封不动logic31以及原可靠性61＋最小相关pending／旧回调测试，两个专项实际通过后才冻结候选；最终仍从rc2 ZIP独立解包EXE完整回归。上轮FAIL及105输入归档保留，不修改以冒称通过。

构建前复核追加最小文件授权：Desktop可在Controls/WebtoonViewer.xaml.cs添加只读HasReachedPageOffset，按ScrollToPage相同目标clamp与实际offset判定命中；预取VisibleFirst/Last不能当真实视口完成。入口回调同样须按真实位置消费意图。普通键盘在恢复期已有输入guard，程序化新意图重派仅属防御，不报告成已复现用户键盘缺陷。QA静态修正已初始化View与Reader共用测试loader的引用寿命；尚无新轮QA运行失败，返修计数仍0。

R2-01至04非作者源码复核通过；同位置Home不产生位置事件时主动按真实offset检查，避免pending悬挂影响后续手动滚动。QA只增加直接关联的近邻buffer／末页clamp／同位置三个任务，旧31及原61保持。生产三文件静止、QA partial定稿9C2E05782D116D8A7E57D27ECFA2C5236899E5A14AE87094CD277F0129CE3DB7，开始统一隔离App构建。上轮105项中仅预期的三生产文件、QA partial、说明共5项变化；Core和原Core测试源码与40项通过输入完全相同，未假称本轮重新运行Core40。

## 早期验证与合同冲突处理

隔离App构建8.0秒退出0、零警告／错误。独立QA两个独立PowerShell进程分别跑logic-only与OPC-only，各自独立Desktop/DB：logic31/31通过（18.13秒），旧阻塞实际页8、range(7,8)、DBwebtoon8/.683；OPC72中71过1失败（32.03秒），两组输入哈希无变化、stderr空、无crash。

唯一失败不是被忽略：旧61中的“慢请求切双页从成功2进入”把有效pending4（未failed）切模式也要求回成功2，违背本轮已批准AC-02R-02，与8/9规则无法同时成立。leader读取具体fixture后，规划与架构分别确认，登记CR-CV-20261103-02-01正式替代这一条旧解释：未完整成功时flush／书签仍2，完整双页成功后真实4-5、底栏5-6/6、DB4／100%，释放旧回调后不回灌。原60条、failed B回A、旧logic31原文及14条旧snapshot不修改。

保留71/72原FAIL，不声称旧61原判据原样全过；新PRD、review与QA报告明确替代链及复测要求。相邻用例显式Jump2隔离起点。生产代码不因本次合同更正改变，仅独立fixture修改后重新构建／复测。本轮返修记录1/2，未最终放行或交付。

CR后fixture E35953BFF473B794CC6E1D663B542A1CA2BDDCF172ABE09D3E2469FE91E18327，隔离build2 25.9秒退出0。OPC专项实际72/72通过（20.20秒、退出0、未超时），组成原60＋1正式替代＋11关联新增；成功2／pendingSave2／书签2和完整4/5／底栏5-6/6／DBdouble4/1.0均实际验证，9项输入哈希0差异、实际Desktop一致、stderr空、无crash。生产与旧logic31未改，前述31/31证据适用；最终包将再完整重跑。现整理v1.3.2-rc2，尚未最终QA或替换正式程序。

## 最终候选

01:20:53冻结并完成隔离publish（56.5秒退出0），v1.3.2-rc2为105项输入及13项ZIP payload；四份规格snapshot含本轮正式CR，冻结后源码／工具／包不再改。EXE SHA256=35AE398E9470B9AF639129C2AE7AF11B604422241F72768104A8DD74CD6AC20E；ZIP=3AF65A824948D64632CA003DCD3691795B6CFA7CC9C77FE251C50418CFDE5355；manifest=4BBCD6DAB65A14FD2071A81522353307452E962EA743512F61C3CBB7221A8300。详见[candidate.json](candidate.json)。独立QA已接手ZIP解包EXE完整验收，尚未放行／交付。原正式1.3.1保持，另有同hash只读备份用于可逆交付。

## 最终验收与同制品复制

独立QA已正式PASS该指定rc2：完整smoke113.57秒退出0、未超时，旧logic31原文31/31、可靠性72/72（60原＋1正式替代＋11新增）、排版30、TXT查找16、基础EPUB8及所有适用旧回归通过。105项输入与13项payload运行前后无变化，实际Desktop匹配，stderr空、无crash；5条ERROR逐项为故意夹具和已知PDF私有缓存回退，完整解释与未覆盖在[qa-report.md](qa-report.md)。leader目视同一候选原阻塞页8底栏9/12及pending9底栏10/12截图，与DB断言一致。

01:25:50复制批准同一EXE至publish/ComicVerse.exe，ZIP至dist/ComicVerse-Portable-v1.3.2-win-x64.zip，说明直接取批准ZIP条目。复制后EXE／ZIP哈希与上述批准值相同，说明SHA256=D780FC045480902BF818C8A5FB87214137B109F18ED857478308690C991F2D3E，未重新构建，未发现运行中的正式进程，无需关闭。详见[delivery.json](delivery.json)。QA正在独立只读做交付后核验，尚未据此标DONE；未推送／打标签／远端发布。

## 交付完成与结束

01:26:27独立QA交付后只读核验PASS：正式EXE v1.3.2、75,049,529字节，EXE／ZIP／说明与批准制品／ZIP条目相同；105项输入与manifest无变化，publish无版本子目录，无重建／重跑。证据为artifacts/opc-CV-20261103-02-qa-final/delivery-verification.json及本轮qa-report E-74。原阻塞和两项原需求均已在该同制品完整回归与本地交付中完成，旧rc1 FAIL／本轮71/72 FAIL和正式CR历史继续保留。

已将105项批准源输入和最终PASS／交付证据保存为候选目录ComicVerse-v1.3.2-approved-source.zip，109项归档，输入哈希0差异；SHA256=8CFD4E37C66EE179137ED7A593BFB75D8EE6B6FB49B30DB6F9C2326C3B15D124。此为源码溯源归档，没有再构建产品。正式EXE哈希35AE398E9470B9AF639129C2AE7AF11B604422241F72768104A8DD74CD6AC20E，ZIP哈希3AF65A824948D64632CA003DCD3691795B6CFA7CC9C77FE251C50418CFDE5355。

按本次默认单轮授权收尾，未用尽120分钟，QA返修1/2；停止原因是指定技术和本地交付目标达成。control回standby、无活动轮次，未创建后台自动化或新功能任务；未推送／标签／远端发布。真实用户价值仍待反馈，复杂真实格式、大库／网络盘及所有设备／DPI／字体／编解码器和极端竞态未穷尽，见QA范围。源码仍为未提交工作区改动，已由完整冻结清单和批准源码归档标识实际交付输入。