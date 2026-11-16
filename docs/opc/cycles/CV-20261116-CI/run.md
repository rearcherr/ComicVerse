# CV-20261116-CI：修复 GitHub 冒烟失败

2026-11-16（Asia/Shanghai）。用户在完成 CI 诊断后明确要求“修复”。范围为压缩图片尺寸读取的流生命周期、相应回归及 CI 失败诊断；不新增产品功能，不公开 Release。

## 证据与验收

- E-01：基线 `078c75a7b1bd6552c07fef2f4150978a5b522eb1` 的 [CI attempt 2](https://github.com/rearcherr/ComicVerse/actions/runs/41495173260/attempts/2) 重现失败。构建和 Core 测试通过；实际 App 在 107.309 秒退出 1，未超时，App/runner 的 Desktop、PID 一致。
- E-02：`artifacts/ci-diagnosis-41495173260/attempt-2/ui-smoke/smoke/smoke-crash.txt` 记录未观察 Task 异常，内部为 `DeflateStream.ReadAsyncMemory` 空引用；App 的既有异常门禁随后退出 1。原证据保留。
- BUG-01／REQ-01：修复非 seek 压缩条目流进入 WPF 延迟解码后提前释放的风险。正常、坏图片、限流和调用者资源所有权需回归；既有完整 App smoke 必须通过，未处理异常仍须失败。
- BUG-02／REQ-02：两条 workflow 的失败报告不能依赖会过期的附件。始终输出可用诊断，保持原始退出码、超时和独立桌面校验。

已有明确复现，按工作流走精简研究路径；市场、用户与交互结论沿用既有产品范围，不编造新研究。一次迭代，预算 120 分钟，最多两轮 QA 修复。

## 文件归属与验证

- leader：环境准备、整合、冻结、构建、打包、控制记录和最终交付核验。
- core_engineer：`ImageHelper.cs`、必要的 `ZipComicSource.cs`、Core 回归测试。
- desktop_engineer（本轮 CI 工程）：仅两条 `.github/workflows/` 配置。
- qa_acceptance：独立 QA 计划、候选回归和报告；不实现被验收产品代码。

Windows 构建及运行统一使用 `tools/Invoke-IsolatedProcess.ps1`，独立测试数据和实际 App `--smoke`。本机最初无 .NET SDK，使用项目忽略目录内的 portable SDK，不更改系统安装。最终候选须冻结来源与制品哈希，独立 QA 通过后才可交付同一制品。远端不公开发布。

## 已完成的实现与云端验证

修复提交 `df5ea7bd2c6b3ca698b89fcdd16825c5af1c3078`，分支 `codex/fix-ci-zip-stream-lifetime`。

- `ImageHelper.GetDimensions` 对非 seek 输入先用 `PageReadGuard` 同步限流缓冲，随后只向 WPF 传可 seek 内存；原始流仍归调用者，正常 prefix 的 384 KiB 预算保持原样。
- 新增正常/坏 PNG、坏 DEFLATE、短读、调用者所有权、256 MiB 限制和 prefix 预算回归；Core 日志明确 UTF-8。
- 两 workflow 在 `always()` 步骤逐项打印执行结果、stdout/stderr、summary 和 crash，缺失/读取失败继续诊断；附件保留 30 天。原失败退出码和隔离门禁保留。
- 独立静态审查与诊断 fixture：92/92 PASS。
- [CI](https://github.com/rearcherr/ComicVerse/actions/runs/43825773730)：SUCCESS，Core 43/43、实际 App 完整 smoke 113.263 秒退出 0，无超时。
- [候选构建](https://github.com/rearcherr/ComicVerse/actions/runs/43825809001)：SUCCESS，包含自包含 EXE 打包及包内实际 App 完整 smoke。

本机 SDK 下载未完成，改由 GitHub Windows runner 执行实际构建，未将本地静态检查冒充构建。Git 原始字节已通过 archive 冻结在 `artifacts/ci-fix-final/source-df5ea7b.zip`，SHA256 `1D1962E46C79959F0479D713E68AA9F4E0708CA7DDCF3F2B7C3AD6BC5AA5F87F`。云端证据已下载至 `artifacts/ci-fix-final/`，最终候选独立 QA 继续执行。

## 候选与交付范围

- 候选 EXE SHA256：`8D801293B79BD6C69A5851CCE30D3B9C498A586F2FDAD3CCDEEC07D894AA2C4B`。
- Portable ZIP SHA256：`D855900E8AC188D838F4A0626E364731108A446E5E612539A8EF4D955EEF62C8`。
- 构建清单 88 项输入与冻结 Git 原始字节完全一致；不使用旧版本本地 QA 为新包背书。
- 同一候选包本机独立完整 smoke：115.269 秒，exit 0；Dispatcher 和未观察 Task 两类故障注入均 exit 1、未超时。运行前后来源及制品哈希一致，独立签认见 [QA 报告](qa-report.md)。
- 交付为修复代码与验证证据：[PR #2](https://github.com/rearcherr/ComicVerse/pull/2)。当前等待合并；未公开 Release、未替换正式 EXE。候选包保存在本地证据目录，后续若交付程序须使用同一已验收字节。

技术验证覆盖本次失败路径与既有回归；真实用户体验、海量书库及全部 Windows/DPI/编解码器组合未覆盖。未启动后续产品迭代。
