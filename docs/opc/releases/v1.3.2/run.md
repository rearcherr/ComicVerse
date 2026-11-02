# v1.3.2 GitHub 上传与发布

用户新授权：“上传github吧”。已验证本机GitHub CLI账户rearcherr，仓库rearcherr/ComicVerse为公开仓库，默认main；远端main为a5a88574a68eec1f994d8e4478208509e94c3577，与本地origin/main记录一致。本地HEAD3a892fd领先5次提交，另有已验收v1.3.2和OPC配置未提交改动。本次提交和推送这些项目源码，并发布v1.3.2及批准制品；不改应用行为，不重新构建正式EXE。

批准资产：EXE 35AE398E9470B9AF639129C2AE7AF11B604422241F72768104A8DD74CD6AC20E；ZIP 3AF65A824948D64632CA003DCD3691795B6CFA7CC9C77FE251C50418CFDE5355。原rc2完整smoke113.57秒退出0，logic31/31、可靠性72/72及完整适用回归PASS，交付后独立核验PASS；证据见[产品QA](../../cycles/CV-20261103-02/qa-report.md)。原105冻结输入和QA证据保留历史原文。

## 分工与发布路径

- Core工程角色独占两份.github/workflows：取消标签自动重建公开发布，手动工作流仅生成待验收candidate artifact，主CI构建／测试使用既有独立Desktop runner。只写这两CI及implementation.md，不改产品或runner。
- 技术架构只读复核实施与GitHub官方事实，写review.md；leader另行交叉复核，作者不自签。
- 独立QA只写qa-report.md及artifacts/github-v1.3.2-qa*/，核对新发布冻结清单、仅CI预期差异、资产字节和必要完整实际App自检，并核对上传后远端下载哈希；不修产品或发布。
- leader负责新发布freeze／说明／SHA256SUMS、Git提交推送、草稿Release上传、待独立核验PASS后公开、复核远端结果。

先推送含安全CI配置的提交、等待云端检查，再创建v1.3.2标签和草稿Release；草稿仅上传已批准同字节EXE/ZIP及可追溯源归档／校验和，QA核对后公开。绝不使用tag触发的新构建继承本地PASS。两个CI文件属于旧105清单，改动后另建发布冻结及差异白名单，不改写旧manifest为零差异。

本机Windows构建／UI检查仍统一独立Win32 Desktop、独立数据及真实App --smoke，长网络命令有界超时和进度；等待角色每最多60秒核对存活。不操控用户桌面或真实书库，不联系他人，不泄露凭据，不强推或覆盖已有Release／标签。

## 发布前门禁

GitHub账户具备repo／workflow范围，main远端和本地origin记录一致，v1.3.2远端标签及Release均不存在；源码暂存62文件，未包含测试data、临时二进制或凭据文件。批准的EXE、Portable ZIP及实际批准source归档已同字节stage，并生成SHA256SUMS.txt。

新的发布freeze为artifacts/github-v1.3.2-release-freeze/release-freeze.json，SHA256=07659A2BBD8BA511117A1269753574230982B6AEF869B08F28ECA0DB227E22EF。旧105中恰5项为两CI及AGENTS／README／WORKFLOW发布控制改动，其余100项（App／tests／runner／样例等）保持原批准输入；旧清单未修改。另冻结5份发布snapshot和4个上传资产。架构非作者审查和独立YAML／16段PowerShell AST检查通过，未将静态结果称为云端实跑。

独立发布前QA已PASS：使用批准同EXE、未重build的完整隔离smoke112.19秒退出0，logic31／可靠性72及全部适用旧回归通过；实际Desktop互校、stderr空、无crash，5条fixture／PDF回退逐项解释。新freeze、来源、snapshot和asset运行后0差异，见qa-report.md及artifacts/github-v1.3.2-qa-final/qa-decision.json。接下来提交／推送，云CI与草稿资产远端核验尚待执行。
