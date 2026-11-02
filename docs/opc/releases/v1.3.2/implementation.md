# v1.3.2 发布工程实现交接

日期：2026-11-03（+08:00）；实现者：core_engineer，按leader明确分配兼任本次发布工程。用户已授权上传GitHub；远端commit／push／tag／Release和正式资产上传仍由leader统一执行。本文不是独立QA放行，也不是远端已运行证明。

## 已批准产品制品保持原样

本次读过`CV-20261103-02/qa-report.md`的rc2完整PASS及交付核验。独立QA的产品结论只对应：

- `publish/ComicVerse.exe`，1.3.2，75,049,529字节，SHA256 `35AE398E9470B9AF639129C2AE7AF11B604422241F72768104A8DD74CD6AC20E`。
- `dist/ComicVerse-Portable-v1.3.2-win-x64.zip`，SHA256 `3AF65A824948D64632CA003DCD3691795B6CFA7CC9C77FE251C50418CFDE5355`。

没有修改产品源码、测试、隔离runner、已批准EXE／ZIP，也没有执行build、publish或App。上传时须使用上述同一字节，不将后续CI重建包冒充本地PASS。两份workflow属于原产品冻结清单内文件；本次有意变更应进入新的发布工程清单和独立QA，不篡改原105项冻结记录或声称它仍与当前workflow相同。

## 工作流改动

唯一实现文件为`.github/workflows/build-release.yml`和`.github/workflows/ci.yml`；没有新建辅助执行器。

1. `build-release`移除tag push触发，只保留`workflow_dispatch`，权限降为`contents: read`，删除public Release动作。CI只生成需要独立QA的候选artifact，不授予任何上传public资产的路径。
2. build Core、build App、build tests、Core测试、publish和真实App smoke分别使用独立step，每step只调用一次原`tools/Invoke-IsolatedProcess.ps1`并传播真实退出码。构建／发布600秒、测试／smoke300秒；runner输出实际退出、超时、Desktop和日志，不修改主机WER或注册表。
3. 手动候选流程先从XML读取并校验项目版本、记录commit和源文件SHA256清单，再隔离构建／测试／发布。将EXE、说明、LICENSE、样例打包ZIP，解包到独立候选目录后，以包内EXE和样例运行完整实际App自检。
4. smoke之后单独验证App写出的Desktop、PID、SmokeProcess和ErrorMode与runner一致，失败／超时或crash标记拒绝通过；再次检查冻结来源、候选与解包EXE和ZIP哈希。候选状态固定为`ci_candidate_requires_independent_qa`，不会因为CI通过自动转为QA放行。
5. CI普通push／PR仍触发检查，Windows构建／Core／App均改为统一独立桌面入口；原默认桌面的`dotnet run`和直接App启动已移除。
6. 两条流程都用`actions/upload-artifact@v4`在`always()`步骤保留全部runner证据；候选资产仅在前序全部成功后上传。沿用checkout／setup-dotnet的v4，不更改模型、权限或产品范围。
7. 非作者架构复核后补充：候选运行后先验证`source-freeze.json`自身SHA256，再逐项复核文件；候选及证据根目录均包含`run_id`和`run_attempt`，每次重跑有独立目录。

## 开发静态验证及限制

- Python 3.9／PyYAML实际解析2份YAML成功：触发器、只读权限、无Release动作、每执行step单次runner、超时与退出传播、always证据artifact的结构断言通过。
- 用本机PowerShell官方AST解析器逐一解析16个嵌入run脚本，0语法错误；`git diff --check`退出0。早期静态检查的测试匹配器把“Verify actual...”误归为执行step导致一次断言失败，已修正匹配器后重查；没有为此改变工作流验收目标。目录初始化使用运行时环境变量写入GITHUB_ENV，没有在不支持runner上下文的job env层引用runner.temp。
- 没有在本机重建应用，也没有运行GitHub-hosted Windows job。独立桌面在云runner上的实际创建权限、网络restore、所有远端Actions调度与artifact上传要以首次真实CI结果为准；静态通过不能代替这些实际执行证据。
- 不使用临时WPF宿主，未操作用户桌面或真实书库，未自行commit／push／tag／Release。

下一位：tech_architect非作者复核 → 独立QA验收发布工程文件和同制品上传计划 → leader冻结发布工程输入并上传批准产品资产、核对远端下载SHA256。

## 官方资料核对

核对日期2026-11-03，已实际打开官方页面：

- [GitHub workflow语法](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)：手动触发器、权限、step条件及超时语法。
- [GitHub workflow artifacts](https://docs.github.com/en/actions/tutorials/store-and-share-data)：`upload-artifact@v4`及保留期。workflow artifact是CI候选和证据存储，本次public Release由leader上传已批准资产。
- [GitHub上下文可用位置](https://docs.github.com/en/actions/reference/workflows-and-actions/contexts)：runner上下文用于step而非job env；运行时路径由独立初始化step计算。
