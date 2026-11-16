# CV-20261116-CI 最终独立 QA

**PASS，无阻塞缺陷。** qa_acceptance 未参与生产实现。2026-11-16 完成独立验收，仅放行以下同一产品候选；未构建、发布或复制到 publish。

候选 `comicverse-candidate-1.3.2-43825809001-1`，源码提交 `df5ea7bd2c6b3ca698b89fcdd16825c5af1c3078`。

|对象|SHA256|
|---|---|
|EXE／ZIP内EXE／QA独立解包EXE|`8D801293B79BD6C69A5851CCE30D3B9C498A586F2FDAD3CCDEEC07D894AA2C4B`|
|便携ZIP|`D855900E8AC188D838F4A0626E364731108A446E5E612539A8EF4D955EEF62C8`|
|source-freeze.json|`45988C969097C637B2578755316806E6A6EC2EE4A8A82C6FA49239F49A2CAEEF`|

独立核对云端 manifest 的 **88项**原样 LF 源码、测试、工具、workflow、样例，与 `artifacts/ci-fix-final/source/` 长度及SHA256全匹配，无遗漏；便携包13文件完整且对应声明材料。GitHub artifact摘要及解包字节匹配。运行前135/135、运行后136/136核验通过，**415项来源／制品／解包／evidence快照前后0差异**。

|验收路径|实际结果|
|---|---|
|云端 ci 43825773730|同提交；Core **43/43**（新增三组流回归具名执行）；真实App完整smoke **113.263秒，exit0，未超时**|
|云端 build-release 43825809001|同提交；Core **43/43，10.372秒，exit0**；同包自包含App smoke **117.536秒，exit0，未超时**；打包与隔离门禁通过|
|本机同包EXE完整 `--smoke`|**115.269秒，exit0，未超时**；15:53:48–15:55:44 +08:00；独立解包、包内样例、新DB；无smoke-crash，stderr0|
|同EXE `--verify-failure-exit`|**0.545秒，exit1，未超时**；准确记录 intentional dispatcher failure，门禁PASS|
|同EXE `--verify-task-failure-exit`|**0.522秒，exit1，未超时**；准确记录 intentional unobserved task failure，门禁PASS|
|两workflow诊断fixtures|每条8场景，**92/92检查PASS**；失败、超时、缺失、残缺、不可读证据均能诊断|

本机三次实际App均经 `tools/Invoke-IsolatedProcess.ps1` 串行启动，使用不同独立Desktop/data；App与runner的PID/Desktop一致，ErrorMode32771、SmokeProcess=true。完整smoke PID45160，Desktop `ComicVerse-QA-8e2409f865264f069a26c4b7e6251c27`。无用户桌面接管或临时WPF宿主。

完整回归：logic **31/31**、OPC **72/72**、排版 **30/30**、查找 **16/16**、EPUB UI **8/8**及漫画、小说、书架、PDF适用路径通过。短样例不可滚动子场景明确skipped，生成长篇实际滚动恢复通过。目视核对目标页8的9/12条漫、完整双页4/5及PDF三张离屏截图。完整日志5条ERROR逐项解释为PDF私有缓存回退、设置写失败注入及3条非法PAX拒绝；无未处理异常。原有72项正式口径不变。

最终证据：[final-43825809001-1](../../../../artifacts/ci-fix-qa/final-43825809001-1/)，含三次真实执行、环境、crash反例、summary、明细、截图、snapshot-before/after、`final-runs-verification.json`及`qa-decision.json`。专项记录：[云端CI](../../../../artifacts/ci-fix-qa/cloud-ci-report.md)、[workflow fixtures](../../../../artifacts/ci-fix-qa/workflow-fixtures-report.md)、[静态复核](../../../../artifacts/ci-fix-qa/static-review.md)。QA脚本误报／语法修正前记录保留在artifact，未改产品或场景判据。

原始GitHub attempt-2 **FAIL**保留：[DeflateStream未观察异常](../../../../artifacts/ci-diagnosis-41495173260/attempt-2/ui-smoke/smoke/smoke-crash.txt)，107.309秒exit1；未改写原失败。

未覆盖：所有复杂／损坏第三方格式、海量／网络书库、全部DPI／字体／编解码器和时序、纯净Windows首次原生库提取、真实用户满意度；DecodeFrozen任意外部非seek输入不属本轮复现范围。Core采用已独立核验的真实GitHub执行，未声称本机重复运行。

交接leader：本轮QA完成，可提交记录及PR。仅文档／控制记录变更无需重建候选；产品、工具或包字节变化须重新验收。本次未替换正式EXE或发布。