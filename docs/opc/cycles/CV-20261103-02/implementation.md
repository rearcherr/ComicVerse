# BUG-CV-20261103-01-01 实现交接

角色：desktop_engineer。日期：2026-11-03（Asia/Hong_Kong）。本轮继承未交付v1.3.2-rc1，只修显式跳页在快速模式切换中丢失的阻塞回归；正式程序仍是v1.3.1。依据、原始FAIL与需求见本轮run、architecture、interaction和requirements记录，不把源码设计当作运行通过。

## 根因与改动

上轮成功呈现快照正确地防止坏页目标覆盖进度，但模式入口一律回到快照，把仍有效的pending显式目标也当作失败游标丢弃。本轮将三种状态分开：

|状态|实现|用途|
|---|---|---|
|有效pending意图|`ComicNavigationIntent`包含唯一generation、原始页、可选属于同意图的条漫fraction；活跃对象保存在`_comicPendingNavigation`|模式转交的目标。显式请求创建，current成功／失败终结，过期／模式取消不终结新意图|
|请求游标|原`_page`／`_spreadStart`及双页队列|失败后的Next／Prev和重试仍能绕过坏页；不能直接保存或跨模式复活已失败请求|
|成功呈现快照|原`ComicPresentation`保持不变|底栏、持久化、创建书签及无pending时的回退，首次无成功仍保留已有DB|

模式入口`GetComicModeEntryIntent`优先转交同一个pending对象，没有pending才从稳定快照／初始历史创建派生入口。显式单页导航、重试、已解算双页命令、slider、Home／End和书签跳转经原有请求入口开始新意图；新显式页默认不继承旧fraction。RTL重新呈现及模式切换沿用已有pending，不以派生请求覆盖更明确的意图。

单页／双页异步dispatch除原有mode和load version外，还检查当前意图身份。只有仍current的有效位图／完整双页才提交快照并终结该意图；current失败仅撤销自己的pending，保留请求游标和成功快照；取消与旧回调不撤销已经转交或更新的意图。双页Canonical计算不重写pending原始页，故未完成的奇数页9可继续转交9，成功后仍按既有完整组起点8稳定保存。

条漫入口仅使用同一意图携带的fraction或当前有效历史恢复，不能让旧savedFraction覆盖新显式页。其初始化Task仍共用，entryVersion和意图身份同时约束回调，恢复完成后才终结意图。已就绪条漫的导航由实际目标滚动offset到达触发消费，保存与底栏仍读实际视口事实。被动旧视口不消费未到达的新目标。

架构复核发现条漫`VisibleFirst/Last`实际上是预取缓冲，不能作为完成判据。leader另授权`WebtoonViewer.xaml.cs`只读helper `HasReachedPageOffset`：精确复用`ScrollToPage`的`_tops[index]`与`_total - ViewportHeight`夹紧公式，实际offset在1DIP内才算到达，支持末页夹紧；fraction同样以实际ScrollViewer可滚高度和1DIP偏差判断。不改控制渲染、布局或输入。Reader在pending未到达时不把旧条漫viewport当作新成功快照，恢复回调也不再无条件消费；实际布局结算后由相同到达判据提交／消费，未到达意图仍可继续跨模式转交。

leader复核补充的边界：普通Window键盘与控件在条漫未就绪／恢复期已拦截导航，但自检或程序化Slider可能触发新意图。此时`NavigateWebtoonToPage/Fraction`重派新的`ShowWebtoon`入口，转交新意图并等待同一个初始化Task；不放宽旧回调的entry／意图守卫，避免恢复标志悬挂。关闭仍先保存成功快照，再清pending并取消后台请求。

R2-04架构复核修正：已ready的条漫若Home／fraction请求恰好就是当前offset，底层不发位置变化事件。两种Navigate在调用Scroll后主动执行一次`UpdateComicInfo`，沿同一个实际offset判据采集／终结到达的同位置意图；尚未应用的目标仍保留pending。这样后续滚轮移动不会被悬挂的旧同位置意图挡住。不加Timer、不放宽旧callback守卫。

## 文件及验证

- 修改`src/ComicVerse.App/Windows/ReaderWindow.ComicPresentation.cs`和`ReaderWindow.xaml.cs`。
- 经leader追加唯一owner授权，`WebtoonViewer.xaml.cs`仅增加上述只读到达判据，没有改控制渲染／布局／输入逻辑。
- `ReaderWindow.ComicNavigation.cs`无需修改：双页队列在实际target计算完成后调用原渲染入口开始新意图，尺寸解算期间换模式仍按既有合同取消队列。
- 未修改XAML、Core、QA partial、旧LogicAudit31判据、版本、状态文件、正式EXE或候选制品。
- 静态`git diff --check`对三份生产文件退出0；没有自行构建或运行WPF。源码已交非作者技术架构复核，后续由leader统一隔离构建、独立QA专项和最终rc2整包验收。

需运行证明的反例包括：原样旧logic首次初始化模式竞态、有效pending8跨模式、已失败B回退成功A、pending保存／书签仍A、旧成功／失败不能清新C、奇数pending9、首次历史加载中关闭以及恢复期新意图重新派发。上轮可靠性61项、旧逻辑31项及完整适用回归继续保留；本实现文档不代签PASS。
