# v1.3.2 GitHub 发布方案：非作者架构复核

角色：tech_architect。日期：2026-11-03（Asia/Hong_Kong）。本文件只读审查发布方案并记录门禁；未修改产品、旧冻结输入、CI或工具，未提交、推送、打标签、上传或对外发消息，也没有重新构建EXE。CI实施由leader另行指定的core_engineer负责，独立QA审查新的发布控制候选。

## 授权与批准制品

leader明确交接用户新指令“上传github吧”，本次范围为提交／推送已验收v1.3.2源码，并将已批准同一EXE／ZIP作为GitHub Release资产上传。不是授权用CI重建包代替批准制品。

本地批准来源为[第二轮最终独立QA E-73及交付后E-74](../../cycles/CV-20261103-02/qa-report.md)，不是上轮失败rc1：

|对象|批准值|
|---|---|
|版本／EXE大小|1.3.2／75,049,529字节|
|EXE SHA256|35AE398E9470B9AF639129C2AE7AF11B604422241F72768104A8DD74CD6AC20E|
|ZIP SHA256|3AF65A824948D64632CA003DCD3691795B6CFA7CC9C77FE251C50418CFDE5355|
|旧source-freeze.json SHA256|4BBCD6DAB65A14FD2071A81522353307452E962EA743512F61C3CBB7221A8300|

本次直接使用`publish/ComicVerse.exe`及`dist/ComicVerse-Portable-v1.3.2-win-x64.zip`的批准字节，不重新publish或重压ZIP，不把GitHub自动生成的源码归档混称已验收免安装包。

## 原发布链路的具体冲突

1. 当前`.github/workflows/build-release.yml`在`push.tags: v*`触发，使用`contents:write`并通过`softprops/action-gh-release`发布新构建EXE／ZIP。推标签会绕开“批准同一制品”而公开未经独立验收的另一包。
2. 两份旧workflow都直接在默认desktop构建／跑WPF，和项目统一独立Win32 Desktop的规则不符。单独设置数据目录或隐藏窗口不能替代desktop隔离。
3. 旧105项freeze确实包含两份workflow，不能修改它们后仍声称旧105项全部0差异；必须另建发布控制freeze并由独立QA核验明确差异。旧应用完整包验收记录及原freeze应原样保留。

## 最小充分改动及影响

**方案意见：通过，下列门禁满足后可以发布同一批准制品。当前不是实施后的QA PASS。**

- `build-release.yml`移除tag push触发、所有Release上传步骤及`contents:write`；仅保留`workflow_dispatch`，生成清楚标为候选的Actions artifact。候选自测通过不自动提升为正式Release，后续如要发布该新候选必须重新独立验收。
- 常规CI继续在批准的branch／PR／手动事件检查源码，构建、Core tests、实际App的`--smoke`一律通过现有`tools/Invoke-IsolatedProcess.ps1`。可以构建CI自己的输出用于回归，但不能触碰／替换本地已批准交付文件，也不能将CI重建文件加入本次Release。
- 依赖setup与checkout可在runner正常步骤完成；进入dotnet构建／测试／publish及WPF运行的进程需在独立Desktop。独立Desktop创建失败必须显式失败，不回退默认Desktop。
- 每次调用独立证据目录，设置明确超时，保存真实退出码／超时／Desktop和日志；smoke还需核对App实际`smoke-environment.json`与runner一致，无未处理异常标记。证据用`if:always()`上传，包括失败路径。
- **调用细节：现runner末尾使用`exit`。一个CI step内直接调用runner后再写其它命令可能在第一项即退出。** 应使用独立steps，或每项用独立`pwsh -File`子进程调用并立即检查其`$LASTEXITCODE`；不能让第一项成功掩盖未运行的后续构建／测试。具体实现由作者负责，QA核验所有计划阶段确有execution-result。
- 候选命名／说明应体现版本和运行来源，清楚区分“workflow候选”与“批准Release制品”。Version解析失败应给可诊断错误，不使用tag／branch文本拼接成命令。
- README中“推v*自动构建发布”的旧描述需同步为新实际流程，避免下一次发布误操作。此类发布说明变化也必须计入新freeze差异清单。

影响面只限发布控制、CI和相应说明；不变更产品行为、数据库schema、测试判据或已批准EXE／ZIP。移除自动tag发布后，正式发布由leader明确管理的已验收资产上传流程承担；手动workflow仍可产生待验收候选。

## 发布控制独立验收

新release freeze应包括实施后的两份workflow、相关发布说明／验证脚本和本次review；逐项记录SHA256及旧105项的对应关系：

1. 应用`src/`、`tests/`、隔离runner和原二轮需求／架构冻结snapshot保持批准字节；若这些应用来源改变，本地旧完整包PASS不能直接覆盖新源码／重建制品。
2. 对旧105中有意修改的workflow／README等，记录旧hash、新hash及原因，由QA审查这份发布控制候选；不能重写旧freeze为新值或把预期变化说成零差异。
3. 本次应用资产依旧匹配批准EXE／ZIP及ZIP内EXE。单纯修改CI触发／发布控制不要求重新构建已批准EXE，但新控制候选须独立QA明确结论；若出现应用来源或批准二进制意外变化则停止发布核查。
4. 独立核验workflow YAML和PowerShell语法、触发事件／权限／无Release写路径、所有dotnet／WPF命令经过隔离runner、失败／超时非零、证据不缺失。静态审查不能宣称GitHub hosted runner上的执行已成功。

## 同制品GitHub发布顺序

以下由leader执行，QA独立核验；本角色不执行远端动作。

1. 明确提交范围并核对实际源码／发布控制freeze，不把测试data或临时产物混入源码提交。推送前核对origin、目标branch及已有tag／Release状态，遇到已有同名对象先比较而不强制覆盖。
2. 提交并非强制推送到授权branch，记录完整commit。确认包含新workflow控制的远端commit后，才创建／推送指向该commit的`v1.3.2`标签，避免旧tag自动发布链路竞跑。
3. 通过GitHub CLI以`--draft`和`--verify-tag`创建草稿Release，明确资产路径为批准同一EXE／ZIP，并使用实际换行的`--notes-file`。`--verify-tag`避免缺tag时CLI自动在默认分支创建非预期tag；完整目标commit与远端tag解引用需核对。
4. 在公开前读取草稿资产清单，核对名称、数量、大小、上传状态及SHA256 digest；必要时独立下载二进制重新计算哈希。仅看到文件名或上传成功不等于同一制品。若digest／下载hash与批准值不符，草稿保持未发布并处理，不用重建资产凑版本号。
5. 独立QA明确通过发布控制及资产一致性后，再把这份草稿公开；发布后重新核对tag commit、Release URL和两份资产digest。CI可验证同一提交的源码，但其重建输出不作为此次下载资产，也不借用本地PASS。

GitHub Release的create、上传、publish并非一个原子操作，因此明确保留草稿核验阶段；不能创建时自动公开后才检查文件是否正确。若仓库启用immutable releases，发布后资产和tag保护生效，更应在草稿阶段核对完毕。

## 官方事实核对（2026-11-03，一手来源）

- [GitHub workflow语法](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)：push的branch／tag过滤彼此独立；仅branch过滤不会触发tag，移除push后保留workflow_dispatch即为手动执行。本次还要直接审查当前文件，不能仅凭口头计划认定旧触发器已删除。
- [gh release create](https://cli.github.com/manual/gh_release_create)：支持draft、verify-tag、notes-file；缺tag默认可自动创建，附资产的创建／上传／发布不是单一原子请求，draft不会自动公开。
- [GitHub Release assets REST API](https://docs.github.com/en/rest/releases/assets)：资产返回大小、状态及digest，可读取资产二进制；用来核对远端是否仍为批准同一字节。
- [GitHub Actions artifacts](https://docs.github.com/en/actions/tutorials/store-and-share-data)：workflow artifact用于保存构建／测试输出，和公开Release资产是不同交付动作；可以保留候选及证据而不直接发布。

## 交接与尚未验证

交core_engineer实现最小workflow调整；交独立QA核验新发布freeze及资产；交leader统一提交／推送／标签／草稿及最终公开。本文件只给方案意见，尚未审读实施后的最终workflow，尚未在hosted runner执行新CI，也尚未核对远端tag／Release／资产。

实际实施定稿后需补非作者增量复核结果及hash；最终远端交付状态由leader和QA以真实操作／远端结果记录，不能以本方案意见冒称已经上传或发布。

## 实施后第一次只读复核

已逐段读取两份实施后的workflow。当前确认：`build-release`仅workflow_dispatch，contents:read，无tag／Release创建上传路径；常规CI仅branch push／PR／manual，tag不会触发；所有dotnet构建／Core tests／publish及实际App smoke分别在独立step调用一次既有runner，保留非零／超时。candidate先记录源码，再publish、ZIP打包、独立解包同EXE并用包内samples运行smoke；App的ProcessId／Desktop／ErrorMode与runner互校，执行后核源码条目及EXE／ZIP，候选和always证据仅进Actions artifact。

发现并已交作者两项定稿建议：

|编号|发现与处理要求|状态|
|---|---|---|
|REL-R01|candidate.json保存了SourceManifestSHA256，但原执行后验证仅按当前source-freeze.Files循环，未重算manifest自身并与已存值比较|已修复并非作者确认：在解析Files前先重算source-freeze.json与candidate.SourceManifestSHA256，不同即throw|
|REL-R02|原临时evidence／candidate根目录使用固定名称；artifact名已有run_id和run_attempt。当前hosted新VM并非已确认覆盖问题，建议路径也包含重跑标识|已强化并非作者确认：初始化run step使用RUNNER_TEMP与GITHUB_RUN_ID／GITHUB_RUN_ATTEMPT计算目录并写GITHUB_ENV；后续所有step共用，always证据路径按同标识一致拼接|

GitHub官方[runner context](https://docs.github.com/en/actions/reference/workflows-and-actions/contexts)说明runner.temp在job前后清理，[hosted runner说明](https://docs.github.com/en/actions/how-tos/manage-runners/github-hosted-runners/use-github-hosted-runners)说明每job新VM；因此REL-R02按建议而非虚构现有云端缺陷记录。任何静态核对都不代表新CI已在hosted Windows实际运行成功。

初读hash为ci.yml `DE2E8C476532B4534DF168CC19E3C2115A0835A3AA8F02314967E5C9D4E5858F`、build-release.yml `B84A8BB65436976533DE035F3C6261309F6A296182CD9E2AFAAE30C01C196F0B`，不是最终freeze。leader已明确旧105允许差异为两CI加README／AGENTS／WORKFLOW共5文件；应用src/tests/tools/samples与批准EXE／ZIP不得变，QA需独立核对该精确映射。旧105及原App完整包PASS保持历史原文。

## 实施定稿非作者复核

**PASS：发布控制源码／方案复核通过，可冻结这份候选交独立QA；未发现剩余阻塞控制代码问题。** 这不是云端CI成功、远端资产验收或公开发布完成的结论。

已重新逐段读取最终两CI并核对以下条件：

- build-release触发器只有workflow_dispatch、contents:read，无tag push、Release创建／上传action、CLI release写路径；candidate artifact名称和JSON状态明确需要独立QA。
- 常规CI branch push／PR／manual仍正常，tag不触发常规CI；每个构建／Core tests／publish／真实App smoke独立step只调用runner一次，有单命令超时及job上限，不因runner exit跳过同step计划中的后续项目。
- candidate完整路径包含本次run_id／attempt，初始化实际run步骤才使用RUNNER_TEMP，避免在不支持runner context的job.env位置引用runner.temp；通过GITHUB_ENV向后续steps传递。always证据upload使用对应相同根目录，artifact名称也有attempt。
- candidate先freeze可追溯commit／version／源码输入；publish与package分开，ZIP独立解包后的EXE和包内samples用于smoke。实际App.ProcessId／Desktop／ErrorMode与runner交叉核验，失败、超时及smoke-crash明确失败。
- 执行后先核source-freeze自身hash，再核输入条目和package／smoke-bundle EXE、ZIP批准候选hash，全部适用检查成功才upload候选；always日志／隔离证据独立保留。候选不会被当作本次批准Release资产。
- README、AGENTS、WORKFLOW当前描述已同步为手动候选、标签不自动重建公开发布、草稿同制品核验后公开；原最终制品独立QA门禁仍完整。

|定稿文件|独立只读核对SHA256|
|---|---|
|.github/workflows/ci.yml|215A75E1AE54AAD7DB78E6D00CAFC35E72D9874983B0B91903E5CA1F53737752|
|.github/workflows/build-release.yml|CFFC0FDD627B20017C9C737BE6D0610C9176E24581EED520CA76C9A6C553CC5D|
|发布implementation.md|8FE053A68A7AB0044FCBA4FFB3DB4DFA8CB6655D541D0ECE59F3EE1B6FEF4C23|

作者报告两份YAML和16段PowerShell AST及diff检查通过；本角色独立做上述源码逐段复读与hash核验，没有自行执行云端或本地新构建／WPF。发布QA仍须验证新freeze、5项白名单差异、应用来源不变、批准资产同字节；若附加源码归档或校验和资产，也须纳入新发布freeze单独核验，不能移借产品EXE的旧PASS。

leader可以继续组织提交／推送并观察实际云端CI；正式Release仍按前述草稿核验门禁，上传已批准EXE／ZIP。新CI重建输出即使自检成功也不是此次批准资产，远端结果须另记录。当前尚未查看任何本次云端运行或草稿资产，静态PASS不代替这些实际步骤。
