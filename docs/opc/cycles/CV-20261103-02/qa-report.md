# CV-20261103-02 独立QA记录

当前结论：**PASS，仅放行以下指定哈希的 v1.3.2-rc2，允许复制同一制品本地交付，禁止另行重建。** 执行者qa_acceptance未参与生产修复。原rc1最终FAIL和本轮原71/72 FAIL保留，未将旧失败改写为通过。

基线及测试矩阵见[qa-plan.md](qa-plan.md)。本轮启动时保留原logic31与既有场景，准备新增受控pending意图验证；其后具体运行、唯一合同更正和最终包结果按时间顺序记录如下。**leader已本地复制交付，独立QA交付后只读核验通过**，正式EXE／ZIP／说明均为批准同一制品，未重新构建。

## E-CV-20261103-02-70：早期原逻辑专项（PASS，仅专项）

证据`artifacts/opc-CV-20261103-02-qa-logic1/`。真实App经统一runner，独立数据／Desktop `ComicVerse-QA-a5570884082b415084347ff6588accfd`与App互校一致；18.13秒退出0、未超时。**31/31通过**，原阻塞实际`page8 / range(7,8) / saved webtoon8/0.683`。旧logic文件SHA256仍`02BF05F370123C35158A3B34D440DCC08F2C34253779703C5623048C0FA29E8E`。9项输入运行前后0差异、stderr0、无smoke故障标记。

## E-CV-20261103-02-71：早期OPC专项（FAIL，验收合同冲突待判定）

证据`artifacts/opc-CV-20261103-02-qa-opc1/`。独立PowerShell、数据和Desktop `ComicVerse-QA-a19f531722f64eb0b2bec314633df1ef`，与App实际记录一致；32.03秒退出1、未达到120秒上限。**72项71通过、1失败**；9项输入0差异、stderr0、无故障标记。

唯一失败：原61中的“慢请求切双页从成功2进入且旧完成无回灌”。操作是成功paged2→受控未失败的显式pending4→double；旧断言要求原成功组2-3／DB2，而本轮已批准的合同要求有效pending4跨mode继承4。实际明细为`entered=True;changed=False;left2=False;right3=False;page=4;mode=double;progress=1.0000`。这表明DB已到4，旧断言未记录4/5的实际像素；须在明确口径后的复测直接确认目标组，不以推测替代像素证据。

此处是明确的新旧验收语义冲突，已交leader、架构和产品判定，QA不自行改期望。可讨论的修订为“释放前flush必须保存成功2，释放后真实目标4-5与DB4”，保留数据安全并验证pending转交；未批准前仍维持本次FAIL，不能说原61原文全过或最终包可交付。

新直接关联场景均通过：pending8、奇数pending9（旧webtoon fraction真实恢复0.2后最终9）、旧成功／失败晚到但C仍pending、无成功历史阻塞重试关闭、近邻buffer1真实offset665、末页11真实bottom clamp7221.5（CurrentPage10且fraction1）、同位置真实Home后手动滚至C2再切paged仍2。已有failed目标切mode、书签、关闭、防抖、PAX等其余原场景通过。早期专项不能替代新rc2最终包完整回归。

## CR-CV-20261103-02-01 已获正式批准，等待复测

leader读具体场景后确认pending4有效且未失败，批准更正唯一矛盾期望；技术架构独立确认，产品规划已在requirements中记录CR及AC-02R-07。QA据此将该判据改为“成功2先flush→pending4时flush与书签仍2→释放且完整双页4/5真实像素成功后才存DB4/double/1.0，旧晚到后仍稳定”，相邻场景显式Jump2重新建立起点。生产一字未因该合同更正而修改，failed B回成功A的断言不变。

原E-71的71/72 FAIL完整保留；不能说原61原文全过。CR后专项构成为**原60条＋1条正式替代＋本轮新增**，判据总数以实际运行结果为准。当前QA partial SHA256 `E35953BFF473B794CC6E1D663B542A1CA2BDDCF172ABE09D3E2469FE91E18327`，待leader统一重建后重新运行OPC；本轮已登记QA返修1/2。最终仍需新候选完整回归，早期旧31 PASS不转移给未经验收的重建包。

## E-CV-20261103-02-72：CR后OPC复测（PASS，仅专项）

证据`artifacts/opc-CV-20261103-02-qa-opc2/`。2026-11-03 01:18:18–01:18:38 +08:00，20.20秒退出0、未超时。**72/72通过：原60条＋1条正式替代＋11条本轮直接关联新增**。9项输入运行前后0差异，独立runner与App实际Desktop均为`ComicVerse-QA-4c8f443ebdd7497d850cf5bec46fdc9c`，ErrorMode32771／SmokeProcess=true，stderr0，无smoke故障标记。

替代判据明细：`success2=True;gate=True;bookmark2=True;pendingSave2=True;complete45=True;left4=True;right5=True;footer=5-6 / 6;page=4;mode=double;progress=1.0000`。QA目视`opc-pending4-double-complete.png`确认真实完整4/5像素与底栏5-6/6，并目视`opc-pending-9-final.png`底栏10/12和当前画面；与DB／JSON断言一致。新增pending8/9、旧fraction、晚到旧成功/失败、无成功历史、近邻offset、末页clamp及同位置Home全部通过。

同生产逻辑在E-70已取得旧logic31/31，且旧logic源原hash不变；此次只改QA的获批唯一合同及相邻场景隔离，未为该修订修改生产。当前可冻结新候选，最终仍须ZIP独立解包EXE完整回归并再次核对来源／制品哈希，不能把两份早期专项PASS拼接成新包已验收。

## E-CV-20261103-02-73：最终 v1.3.2-rc2 完整包独立验收（PASS）

证据目录：`artifacts/opc-CV-20261103-02-qa-final/`。执行者未实现产品修复；先独立验证冻结输入、解包并使用包内样例和新DB，未以早期专项或作者自测代替此次完整运行。

|对象|批准制品与独立核验结果|
|---|---|
|版本／EXE大小|1.3.2／75,049,529字节|
|EXE及ZIP内EXE／独立解包EXE SHA256|`35AE398E9470B9AF639129C2AE7AF11B604422241F72768104A8DD74CD6AC20E`|
|ZIP SHA256|`3AF65A824948D64632CA003DCD3691795B6CFA7CC9C77FE251C50418CFDE5355`|
|source-freeze.json SHA256|`4BBCD6DAB65A14FD2071A81522353307452E962EA743512F61C3CBB7221A8300`|
|独立QA核验脚本SHA256|`27C3D00ADFB7F76469DD948482ADD7BE5BB614E37653C0439784E72EBACF5988`|
|隔离runner SHA256|`4C6AA0867F7DFF1E41A6148F6A7DDD64E296A47BA0BF5561DA14C28289067868`|
|完整summary SHA256|`75C041A173FE443AC8B91684D8660C717131CAE4F25859107602EBE624C7EE43`|

105项源码／工具／构建与打包输入包含4份新轮不可变PRD／交互／架构／review快照（含正式CR）。独立库存核对当前src/tests/tools无漏项，13个ZIP条目无重复或越界路径，逐项与声明输入字节一致，EXE、LICENSE、说明、样例完整。`verification-before.json`及`verification-after.json`记录运行前后全部105项和13项包内容**0差异**，EXE／ZIP／manifest和解包内容哈希均未变。

实际命令为统一runner启动`qa-final/bundle/ComicVerse.exe --smoke <qa-final/bundle/samples>`。**2026-11-03 01:21:24–01:23:17 +08:00，113.57秒，退出0，未达到300秒限制。** PID32664，App实际Desktop `ComicVerse-QA-8dba9bc860b44d1a9e838cc3944a9532`与runner一致，ErrorMode32771、SmokeProcess=true；全新独立数据目录，无用户桌面输入或真实书库。

### 结果与回归闭环

- **原logic31/31通过**，文件SHA256仍`02BF05F370123C35158A3B34D440DCC08F2C34253779703C5623048C0FA29E8E`。原阻塞同回合序列实际回到零基页8，range(7,8)、DB webtoon8/0.683，目视底栏9/12，与rc1失败时的1/12形成闭环。
- **OPC72/72通过**，组成严格为**原60＋CR正式替代1＋关联新增11**，不是原61原文无修改。替代判据完整核对成功2先保存／pending书签仍2／完整4-5像素成功后DB4。failed B切mode回A、防抖关闭、首次失败、双页整组原子性、PAX path/size和安全拒绝仍通过。
- pending8与奇数9、旧fraction、旧成功／失败晚到、新目标未成功时的进度保护、无成功历史、buffer近邻实际offset665、末页clamp7221.5且DB跟实际视口、同位置真实Home后手动滚到C2再切mode均通过。
- 排版30/30、TXT正文查找16/16、基础EPUB UI8/8、超长段精确选区／真实视口、漫画分页／条漫／双页／RTL／缩放、书架筛选／导入／继续／重置、小说分页／滚动／跨章／恢复、PDF等适用回归通过。短样例不可滚动子模式明确skipped，另有生成长篇实际验证，不把skipped自身当通过。

本次生成64张离屏PNG。QA目视核对原阻塞条漫页8、CR后4-5完整组、pending9、末页clamp、同位置Home后paged2及返回双页失败仍保留原完整组等关键截图；颜色／页号／可见范围／错误提示／DB断言一致。每gate≤3秒的源包装及reflection仅为合成故障／时序注入，不是用户真实文件热替换能力，也不声称穷尽所有竞态。

### 日志与未覆盖

stderr0字节，无`smoke-crash.txt`及其它smoke故障标记。应用5条ERROR逐项解释：01:21:24 pdfium拒绝测试data下不安全提取位置并回退私有缓存（PDF过）；01:22:59精确fixture键`novel_settings_book_27`的SQLite故障注入并随后恢复；01:23:02三条明确非法PAX样例（记录长度／截断／>1MiB声明）安全拒绝。没有泛化忽略其它错误。标准库对非法UTF8的安全处理／替换仍实测可完整显示，未宣称严格UTF8拒绝。

未覆盖：所有真实复杂／损坏商业格式、海量书库／网络盘、全部设备时序／DPI／字体／编解码器、纯净Windows首次PDF原生提取、PAX global path/size/BINARY/稀疏全部扩展、无成功首个Init加载的精确受控延迟，以及真实用户留存／满意度。当前通过仅限指定制品和记录范围。

机器结论为`qa-decision.json`。leader只可复制同一EXE到原`publish/ComicVerse.exe`及同ZIP到`dist/`，不得再次构建；正式替换后还需QA只读核对EXE／ZIP／说明和批准制品一致。本报告未授权远端推送／发布。

## E-CV-20261103-02-74：正式交付后独立只读核验（PASS）

2026-11-03 **01:26:27 +08:00**，leader报告01:25:50已复制后，QA独立执行文件读取与哈希核验，没有重新构建或运行产品。证据`artifacts/opc-CV-20261103-02-qa-final/delivery-verification.json`。

- 原位置`publish/ComicVerse.exe`版本1.3.2、75,049,529字节、SHA256 `35AE398E9470B9AF639129C2AE7AF11B604422241F72768104A8DD74CD6AC20E`，与批准候选及已交付ZIP内EXE项流相同。
- `dist/ComicVerse-Portable-v1.3.2-win-x64.zip` SHA256 `3AF65A824948D64632CA003DCD3691795B6CFA7CC9C77FE251C50418CFDE5355`，与批准ZIP字节相同。
- `publish/使用说明.txt` SHA256 `D780FC045480902BF818C8A5FB87214137B109F18ED857478308690C991F2D3E`，与批准ZIP内同名条目相同。
- 105项冻结来源／工具／快照仍0差异，manifest仍为`4BBCD6DAB65A14FD2071A81522353307452E962EA743512F61C3CBB7221A8300`；`publish/`没有版本子目录。

交付满足“最终包独立验收→同字节复制→独立交付后核验”的门禁。该PASS限定本地上述制品和已记录测试范围，不代表远端已经发布或真实用户价值已验证。
