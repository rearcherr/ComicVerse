# ComicVerse v1.4.0 GitHub 发布

2026-11-22，用户确认发布 `v1.4.0`。发布使用 [候选构建 43837235527，第 1 次运行](https://github.com/rearcherr/ComicVerse/actions/runs/43837235527) 和 [独立验收报告](../../cycles/CV-20261116-OPT-01/qa-report.md) 绑定的同一制品。报告记录完整隔离冒烟 121.487 秒、退出 0，页码跳转 60/60、原逻辑 31/31、可靠性 72/72 以及适用的旧功能回归。覆盖限制见该报告。

从 Actions artifact `14211677138` 恢复候选，原归档摘要 `3D77D8D4798DB2B1866318170A82B05A581B7CEE37C104B95693ABA40DC1C830` 一致。取出已验收 EXE 和 ZIP。`git archive` 重现了原验收源码 ZIP 的字节，155 个文件中 90 个冻结输入逐项通过清单核验。签注标签 `v1.4.0` 指向批准源码 `bfc7da90a9c2d8df79050068ed2030793d88eee0`。

|资产|大小（字节）|SHA-256|
|---|---:|---|
|ComicVerse.exe|75085372|`598D5FFB5457F912D3C9BB07EB838405A39040A89540839D41AE1C992258FB0C`|
|ComicVerse-Portable-v1.4.0-win-x64.zip|69720945|`BD57370801722522C7B669AC4D3A0D2FF1D13A8E2567E7B04AC6897D18219EB8`|
|ComicVerse-v1.4.0-approved-source.zip|987287|`D9584C886CED9696D99B08822D0DCB5D97D5ECEDA5A504F9045C54588830FD70`|
|SHA256SUMS.txt|293|`9F36CE0B454E6F1B0A636D79F3251908A72B0EAB8C603E1A8F859DD0E71844C6`|

独立 QA 从草稿 `454449683` 实际下载四项资产，29.871 秒退出 0，332/332 项通过。校验覆盖资产大小与哈希、包内 EXE、13 项便携包材料、155 个源码文件和 90 个冻结输入。首次归档检查的顶层目录假设错误已更正，原结果与脚本保存在本地 `artifacts/github-v1.4.0-qa/`。最终放行记录为同目录 `draft-qa-decision.json`，SHA-256 `EF22C8C4C4C16221AD8DE715AB620AD695FFD19D8809DACD99FA50C2925B34FD`。

21:03:20（UTC+8）公开同一草稿并设为 [GitHub 最新 Release](https://github.com/rearcherr/ComicVerse/releases/tag/v1.4.0)。公开后核对标签目标和四项资产 ID、大小、摘要，均与草稿一致。未登录下载的 SHA256SUMS.txt 返回 HTTP 200，内容哈希一致。本次发布使用原验收字节，没有重新构建或替换本机程序。

独立 QA 公开后复核 PASS。四项资产重新下载耗时 27.436 秒，退出 0，332/332 项通过。匿名 Latest API、四项下载地址和校验和文件共 6/6 项通过。最终证据 `artifacts/github-v1.4.0-qa/qa-decision.json` 的 SHA-256 为 `CC81ADA801A41E3112091324DB62E0B7BC505D0FCDBA626BCD9A1A23269EF7B8`。
