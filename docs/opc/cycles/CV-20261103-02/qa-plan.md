# CV-20261103-02 独立QA计划

范围：只修复已验证的BUG-CV-20261103-01-01及相关导航意图守卫，保留v1.3.2前轮失败导航/PAX修复与全部适用回归。qa_acceptance独立于生产实现；只拥有`SmokeTest.OpcReliability.cs`、本轮QA文档和专属artifacts。原`SmokeTest.LogicAudit.cs`保持SHA256 `02BF05F370123C35158A3B34D440DCC08F2C34253779703C5623048C0FA29E8E`，旧31判据原文不改。经下述正式CR，OPC为原60条+唯一替代判据+本轮新增，不能宣称原61原文全过。

## 基线与门禁

直接使用上轮rc1最终FAIL作为实际基线：完整120.21秒退出1、旧logic30/31、新61/61；显式零基页8经同回合多模式切换后变0。证据保留`artifacts/opc-CV-20261103-01-qa-final/`，不重跑挑绿。本轮新的执行记录使用E-CV-20261103-02-70起编号。

构建由leader统一调度。开发输出准备后分别运行真实App `--smoke --logic-audit-only`与`--smoke --opc-reliability-only`，前者完整旧31，后者既有61加最小相关补充，早于候选冻结；专项均通过后才冻结新rc2。最终另对ZIP独立解包EXE+包内样例完整`--smoke`，每次独立Win32 Desktop与数据目录，核对实际App桌面/PID、退出码、超时、日志、故障标记与截图。

专项每次120秒，完整包300秒；受控source每gate最多3秒。两轮QA返修额度从0起计；FAIL保留证据并交开发，不能改判据或重复跑到通过。最终PASS绑定新候选完整哈希，运行前后核验来源/工具/快照/制品；只交付同字节。未PASS时正式v1.3.1保持基线。

## 可观察场景

|场景|操作与期望|证据边界|
|---|---|---|
|原阻塞序列|分页0成功→同回合条漫→分页→显式8→double→条漫，最终真实范围/底栏/DB为8|旧logic31原文保留|
|有效pending8跨模式|A成功，阻塞显式8，未成功double→webtoon，释放后实际8；释放前flush和书签仍A|真实App+独立cache/prefetch0，控制图源时序|
|奇数pending9与旧fraction|先真实恢复历史webtoon2/0.2，再paged A2→阻塞显式9→double未成功→webtoon，最终9而非canonical8/旧fraction位置|旧fraction真实从DB恢复，非改私有字段冒充|
|晚到旧成功与旧失败|阻塞旧B8，再以第二gate阻塞新C6；先放B让旧完成处理，C仍pending；double→webtoon，再放C，最终6；C未成功前DB仍A|双gate分别最多3秒，确认两个入口均实际触发|
|failed目标回成功页|已失败B切mode从成功A；重试/Next正常；保存和书签不指向失败B|复用既有61，不能当新能力|
|无成功画面历史|首次坏页已有历史2/42%后，受控阻塞重试，再切mode并关闭，历史页/比例/模式/zoom不变|真实首次失败后的pending重试；不声称注入第一次Init加载|
|真实offset完成边界|已ready条漫真实Next到buffer近邻1，再连续Next至末页11，核真实ScrollViewer offset；同位置真实Home后手动ScrollBy至C2，再切paged保存2|12页240×600源，zoom0.2令末页真实高度小于视口从而确实clamp；页高由实际canvas宽/240×600独立计算。末页DB跟实际CurrentPage/fraction，不强求top页号11|
|整体保护|旧logic31、既有61、新相关判据、原漫画/小说/排版/搜索/书架/PDF全部适用回归|专项不能代替最终候选完整回归|

测试包装器仅用于合成漫画页流，复用真实Reader导航、实际位图、底栏、DB和重开，reflection只安装独立测试loader。已初始化WebtoonView与Reader同步引用同一测试loader，保留真实layout/历史位置，不留下已Dispose的旧loader引用；loader由Reader统一结束，finally释放所有gate并关闭Reader。不添加生产delay、临时WPF宿主、真实桌面操作或真实书库。当前partial哈希`E35953BFF473B794CC6E1D663B542A1CA2BDDCF172ABE09D3E2469FE91E18327`，CR修改后尚未重新编译/运行。

## CR-CV-20261103-02-01：唯一旧pending4判据正式替代

早期71/72 FAIL证明原判据把“成功2→未失败且有效的pending4→double”要求为2-3，与本轮已批准pending8/9跨mode继承合同矛盾。leader批准，架构独立确认，产品在本轮requirements登记CR与AC-02R-07修订；原始失败、旧31、旧14快照均保留。

QA仅替代该一条为三段：成功2先flush；受控pending4未呈现时flush及书签仍2；放行后真实4/5像素、底栏5-6/6、DBdouble4/1.0，并等待旧回调后仍稳定。相邻下一场景显式Jump2建立独立起点。failed B切mode回成功A所有断言不改；这不是将失败判据删除或降低为“任意目标可读”。新专项须重新跑，最终仍独立验收新制品。

未覆盖：穷尽输入时序、复杂商业文件、所有DPI/字体/编解码器、大库/网络盘和真实用户满意度；无成功首个加载的精确受控延迟不能通过当前入口注入，保留旧Show即Close/初始化中断与新真实首次失败重试的互补证据，不伪造完全覆盖。
