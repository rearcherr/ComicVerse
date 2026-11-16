# CV-20261116-OPT-01

2026-11-16（Asia/Shanghai）。用户指令：“根据现有的流程继续优化项目”。按 OPC 默认启动一轮，120 分钟、最多 3 项需求、最多 2 轮 QA 修复；不自动开启后续迭代。

## 基线与证据

- Git 基线 `37ade220ca356f1cee40150b9ef7dc6335b91de2`；前轮 PR #2 已合并。
- 主分支 CI `43828722336`、手动候选构建 `43828964613` 均实际 SUCCESS。新候选不能直接继承此前版本的独立 QA。
- 当前本地克隆未放置 `publish/ComicVerse.exe`；最终若产品验收通过，将按既有规则把同一批准制品交付该位置。
- 候选需求来源：BACKLOG IDEA-003 和 2026-11-02 产品研究的精确页号定位建议。当前底栏为页码显示和比例滑块，尚无用户页码输入。用户价值是可检验的设计假设，不声称已取得访谈或真实满意度数据。

## 九阶段适用与分工

1–3 复用现有 Windows 本地阅读定位、历史竞品文档和用户任务，不重新声称当前竞品实测。4–5 产品规划形成小范围 PRD；6 技术架构核对现有导航入口和进度不变量，独立 QA 确认边界；7 桌面开发实现，非作者复核；8 冻结打包并独立完整回归后同制品交付；9 记录测试观察和待用户反馈。

leader 负责基线、范围决策、整合与交付；product_planner 独占 requirements.md；tech_architect 独占 technical-review.md；后续开发、QA 按明确文件分工。各角色不再派生子角色。

当前拟定范围：漫画分页、双页和条漫精确页码跳转。缩略图、卷间导航、小说文字锚点均不在本轮。范围待产品与技术验证后冻结。

Windows 测试只通过现有隔离 runner 和实际 App --smoke；不操作用户桌面或真实书库。构建使用已验证可用的 GitHub Windows CI，远端只产生候选，不公开 Release。结果与验收状态待更新。

## 范围决策与开发分工

16:58 leader采纳产品规划，批准单项 `REQ-CV-20261116-OPT-01-01`，候选版本 1.4.0。依据当前导航入口、`RenderDoubleSpread` 内部规范组页与既有成功事实机制，技术方案采用入口薄层；不改引擎和数据库。交互见 [interaction.md](interaction.md)，验收合同见 [requirements.md](requirements.md)。阶段6结论：复用通路可执行，独立QA已明确真实控件、焦点及边界的验证方式，进入READY后立即IN_PROGRESS；架构详细记录及非作者复核仍在推进，任何阻塞发现回开发。

- desktop_engineer 独占 `Windows/ReaderWindow.xaml`、`ReaderWindow.xaml.cs`、新 `ReaderWindow.ComicPageJump.cs`；负责产品实现。
- qa_acceptance 独占新 `SmokeTest.ComicPageJump.cs` 与本轮 `qa-report.md`、`artifacts/opt-01-qa/`；独立编写验收夹具并在最终同一包执行，不实现产品功能。
- leader 独占 `SmokeTest.cs` 集成、App.csproj版本、README、使用说明、本轮控制和交互/交付文档；统一云构建、候选和交付。
- tech_architect 只读生产文件，维护 `technical-review.md`，负责异步导航风险和非作者代码复核。

架构报告已确认三文件复用方案可行。TA-B01 条漫以视口到达而非图片解码提交的历史边界，已由PRD明确定义：本轮坏图保成功位置合同限定单/双页，条漫按真实offset验收，不扩大为三模式解码保护宣传。该静态风险未实际复现，保留后续稳定性调查，不改变现有标准；新入口引入的回归仍为阻塞。

17:05 生产实现三文件冻结。桌面与leader完成XML语法/事件接线/差异空白检查，tech_architect非作者静态复核通过，无生产阻塞缺陷。其TA-R01要求区分Ctrl+G绑定/命令验证与OS组合键注入（本轮不接管桌面）；TA-R02新入口pending证据由独立QA夹具补齐。当前尚无本轮运行PASS；不以静态复核代替构建或最终QA。
