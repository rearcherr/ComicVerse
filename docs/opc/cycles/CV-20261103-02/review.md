# CV-20261103-02 非作者代码复核

角色：tech_architect；未参与本轮生产实现。日期：2026-11-03（+08:00）。当前为draft审查，生产仍在收敛；本文件不代签QA，没有运行构建／产品／测试，也没有修改旧LogicAudit判据或任何生产文件。

## Draft发现与交接

|编号|源码场景及状态|建议／门槛|
|---|---|---|
|R2-01 已修复，非作者源码确认|条漫初始化／恢复期间，程序化跳页或可达输入调用NavigateWebtoonToPage/Fraction，新intent替换旧intent；旧entry因身份不再current退出，若新路径只Scroll而不重派，restoring可能不清。普通导航键及按钮已有ready守卫，但仍应防御合法程序化入口|新Navigate在未ready/restoring时调用ShowWebtoon，递增entry epoch，转交同一新intent并复用初始化Task；旧callback身份／版本guard保留。实际初始化同回合替换用例仍由QA验证。|
|R2-02 已修复，非作者源码确认|原CompletePresentedWebtoonNavigation以VisibleFirst/VisibleLast包含target作为reached，但该区间是预取渲染区，可能包含实际视口外目标|只读HasReachedPageOffset与ScrollToPage使用相同clamp目标，实际offset容差1DIP；notready／无视口为false，末页bottom clamp有效。fraction同样用真实ScrollViewer目标offset容差1DIP。buffer范围已移除成功判断，未改渲染或输入。|
|R2-03 已修复，非作者源码确认|原ShowWebtoon的ContextIdle回调无条件Finish(intent)，仅执行到回调不等于实际目标已到|无条件Finish已删除；Capture在pending且HasReached为false时直接返回，Complete也仅实际命中才consume。ContextIdle可解除restoring允许后续ScrollChanged完成，而未到时UpdateComicInfo／SaveNow均不会把旧viewport采成新成功；首次null快照保持历史，已有快照仍flush A。不是依靠UpdateLayout必然命中的假设。|
|R2-04 已修复，非作者最终源码确认|ready条漫本来在0时Home建立intent0，ScrollToPage0没有offset／CurrentPage变化，NotifyCurrent及Resync不发事件；原代码pending0无人消费，之后滚动到C可能被旧pending阻止采集。仅源码反例，未冒称运行复现|两个ready Navigate方法在Scroll后主动UpdateComicInfo一次；实际offset／fraction命中时才消费，同位置不依赖事件，尚未到达仍保留pending。restoring重派仍return，无Timer／新宿主／guard放宽。QA将补直接关联的可观察场景。|

## 已审查且目前符合方案的部分

- ComicNavigationIntent与成功ComicPresentation独立；GetComicModeEntryIntent优先pending，失败只Finish自己仍current的intent，取消／过期分支不撤销新意图。
- 单页完成增加intent身份+原有mode/page/version守卫，完整成功后才提交并consume；双页同样保留mode/version/cts及intent身份，Canonical规范化不提前修改intent原始页。
- 同意图跨模式可复用身份，但各dispatch仍有自己的版本或entry epoch；不能仅凭generation相同提交旧完成。
- SaveNow、创建书签、底栏继续读取成功快照；首次尚未成功保护历史、失败后Next／Retry沿请求游标发起新意图仍保留。Core本轮不改。
- 条漫Fraction仅从同一pending或当前有效历史／成功条漫快照继承，显式新单页请求产生无旧fraction的新意图。
- 双页方向队列仍在actual target算出后开始意图；计算中切mode取消旧队列这一既有合同没有被扩展。

## 当前结论及后续

**最终源码复核：PASS，可进入统一构建、专项及旧逻辑回归、新候选冻结与独立整包QA；本轮未发现剩余阻塞源码问题。** R2-01至04已按上表闭环。这个PASS仅为非作者代码复核，不能用于替换正式程序，也不代表实际回归或最终制品已通过。

最终复读确认：ready两个Navigate在Scroll后主动采集，Capture和Complete仍共用真实offset／fraction门槛；未命中不采旧viewport、不消费pending。ShowWebtoon保持entry epoch和intent身份双重守卫，初始化共享Task，新intent重派新entry，ctxIdle去掉无条件Finish；解除restoring让后续事件结算，不依赖UpdateLayout必然命中来保证安全。首次无成功快照且目标未到时，SaveNow不能从旧viewport产生伪成功，历史DB受保护；已有A仍可flush。双页原始奇数意图在完整成功前保持，旧dispatch不清转交／更新意图。

|最终审查文件|SHA256|
|---|---|
|ReaderWindow.ComicPresentation.cs|8E73FC28C5B85276DDB2574C177B0E1E8EFC0AE03D99D61834255752C7722A99|
|ReaderWindow.xaml.cs|9389C6AD06D1B52B45F18D4563EC41E4821582C614C396FA3F262460DD6B7533|
|WebtoonViewer.xaml.cs|2E9D61CDB01668DCF184DFEB38F2A09D6B2093A9483AA6014C8F2426EA933354|
|SmokeTest.LogicAudit.cs（旧判据未改）|02BF05F370123C35158A3B34D440DCC08F2C34253779703C5623048C0FA29E8E|
|TarComicSource.cs（本轮Core未改）|A5B958D6899AF13AC3A2B607A0761D4F81D744E9F0AE2444FC3643DD1A0BFD42|

三份生产文件的`git diff --check`退出0。架构角色没有构建或运行测试，未修改生产／QA代码。QA须保留旧最终FAIL，原样通过logic31并保持可靠性61；新受控用例验证pending B跨模式、failed B回成功A、晚到B不覆盖C、奇数9、无成功历史及同位置命令后手动滚动。旧专项61通过不能移借给新候选；被审查文件若再改，须核对增量和新哈希。

该记录只陈述源码复核和交接；没有把尚未运行的反例写成真实产品复现，也没有以新增专项或源码审查替代最终指定候选整包PASS。

## 早期专项后的唯一旧判据合同更正

来源：[本轮QA E-CV-20261103-02-71](qa-report.md)，早期OPC专项72项71通过、1失败，原始运行仍为FAIL。旧logic31／31通过，其文件未改。此处没有重写原失败记录，也不以本段文字把失败候选变为通过。

唯一冲突的旧用例原操作为：成功paged2→受控显式pending4（尚未failed）→切double；旧期望为成功组2-3／DB2。新轮已批准的AC-02R-02明确有效pending8／9跨mode继续原始目标，page4也是同一种有效pending状态，不能只按页号对4与8／9做相反处理。实际明细`page=4, mode=double`与新合同方向一致，但该旧用例没有断言4／5实际像素，不能据此先宣称新合同已经运行验证。

leader在专项失败后明确批准**正式更正这唯一pending用例的合同**，产品规划须在本轮PRD记录supersession：

1. 目标4尚未完整成功时，显式flush和新书签仍取已成功paged2；不允许提前持久化4，也不允许新旧半组混合。
2. 受控放行并且双页4-5完整真实成功后，像素、底栏、DB页4／double及相应完成比例一致；旧晚回调不能回灌2。
3. 已明确failed的B切mode仍回成功A，原有失败保护不变；旧LogicAudit31和上轮冻结14条AC原文／snapshot不改。

架构只读确认此更正有一致的状态语义依据，并非忽略一次已确认产品FAIL或把QA阈值调松。新的判据增加“成功前保护A、成功后继承B”两阶段观察，修复的是旧测试将pending与failed混为一谈的矛盾；不是删除数据安全要求。不得宣称“旧61原文全过”，应按本轮PRD明确区分60个未改原判据及1个正式修订判据，并保留此前61／61和本次71／72运行各自对应的源码与判据版本。

下一步由独立QA在分配的partial内修订这唯一用例、保留原始FAIL证据、记录新文件哈希，再由leader统一构建／冻结／复测；最终包仍须独立完整PASS。本角色没有因此修改生产或测试代码，最终生产源码PASS哈希仍为上表三文件；若生产实际再改则需增量复核。
