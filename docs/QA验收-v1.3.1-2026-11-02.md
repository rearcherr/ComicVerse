# ComicVerse v1.3.1 独立 QA 验收

审查起始日：2026-11-02；最终独立放行结论日期：2026-11-03（+08:00）。文件名保留审查起始日，完整验收实际运行于11月2日23:55:21–23:57:00。基线：`0a51eb9`／v1.3.0。执行角色：独立 QA；产品修复、统一构建和打包由 leader 及开发 agent 负责。

## 当前结论

**PASS：放行下列指定哈希的 v1.3.1 最终候选。** QA已对批准ZIP独立解包出的同一EXE执行完整隔离回归，核查实际桌面、异常日志、关键截图及运行前后的70项源码／工具哈希。不是以开发专项或旧版smoke通过代替最终制品验收。只允许复制相同字节交付，不得未经重新验收另行重建。

## 最终制品与完整验收

| 项目 | 独立核验结果 |
| --- | --- |
| 版本／EXE大小 | 1.3.1／75,026,799字节 |
| 候选EXE | `artifacts/logic-candidate/ComicVerse.exe` |
| EXE SHA256 | `C9F0D6AAC0D269B38FE4C5D56E26275517AACCF007C07900C1212EECAABBBC26` |
| 候选ZIP | `artifacts/logic-candidate/ComicVerse-Portable-v1.3.1-win-x64.zip` |
| ZIP SHA256 | `C5262B7ACC1CB917A3B059F6B9376A9EAA6247F986FEC44C115B9B1048DB2CFA` |
| 冻结manifest SHA256 | `DACFB93C0F90154C5FC4727B7FE9931EC7F212838E19F7CBD75DBD56005BBB32` |
| 一致性 | 13项包内容完整，ZIP内EXE项流／解包EXE／候选EXE哈希相同；70项源码与工具运行前后均0差异，制品和manifest后检哈希均未变 |
| runner SHA256 | `4C6AA0867F7DFF1E41A6148F6A7DDD64E296A47BA0BF5561DA14C28289067868` |

完整证据目录：`artifacts/qa-v1.3.1-final-9183da6c/`。使用解包EXE、包内samples、新DB，通过统一runner执行完整 `--smoke`。**2026-11-02 23:55:21至23:57:00（+08:00），98.2秒，退出0，未达到300秒限制。** App实际写出的Desktop为 `ComicVerse-QA-d0b50dbb15e445af905e79ab4ece240c`，与启动器一致；ErrorMode32771，SmokeProcess=true。没有使用用户桌面或真实书库。

完整结果：**新逻辑31/31、排版30/30、TXT正文查找16/16、基础EPUB UI8/8及超长段精确选区／真实视口均通过**。原有漫画翻页／条漫／双页、RTL、缩放与模式恢复、书架筛选／重置／继续阅读／导入刷新、小说分页接章／滚动恢复／滚轮和按书排版、短篇完成、原生分页导航、PDF等回归通过。原不可滚动小样例的子模式项仍注明skipped，随后以生成三章长篇实际验证滚动模式、章节和0.50位置恢复，没有把skipped本身当作通过证据。

本次生成51张离屏截图。QA目视核对了混合双页回退、完整末对、奇数竖页反向、边界命令、首次条漫模式竞态、已存条漫初始化中断、书签删除后面板、设置恢复、单张长条漫进度、小说resize与原生导航、EPUB实际第12/14页等关键截图，实际内容、底栏和DB断言一致。此前已PASS项目也在这一同哈希制品上重跑。

日志核验：stderr为0字节，无 `smoke-crash.txt` 或其他smoke故障标记、无非预期ERROR。两条ERROR逐项核对如下：

- `23:55:22`拒绝位于独立数据目录内的pdfium提取位置，随后日志明确切到LocalAppData私有缓存，PDF回归通过。这是已知安全目录回退，不能当作纯净系统首次提取已验证。
- `23:56:58`的 `novel_settings_book_27` 为本次设置失败fixture的具体键，异常内容精确为 `SQLite Error 19: 'QA requested settings failure'`。对应UI／DB断言确认失败时DB37、会话113，移除触发器后同值关闭、重新建服务和打开阅读器，DB／实际文档113。该明确注入和恢复闭环允许此条日志，不泛化忽略其他设置失败。

完整summary SHA256：`932C799A64BB7AA115AA8584915A086D70048EC0D6F89B175A3C501D71E1A252`。证据包括 `candidate-verification.json`、`execution-start.json`、`execution-result.json`、App的`smoke-environment.json`、`verification-after-run.json`、`qa-decision.json`、summary、31项明细、截图和独立日志。

本机生成样例的条漫远跳约0.3秒、空白条0；200页PDF打开约2.0秒；整个阅读回归最大UI停顿221ms。这些仅描述本次样例与机器，不是普遍性能承诺。

## 交付后独立核验（通过）

2026-11-03 **00:03:03（+08:00）**，leader复制交付后，QA仅做只读文件核验，没有重新构建或运行程序。

- 原位置 `publish/ComicVerse.exe` 版本1.3.1，SHA256为 `C9F0D6AAC0D269B38FE4C5D56E26275517AACCF007C07900C1212EECAABBBC26`，与批准EXE及ZIP内项流完全相同。
- `dist/ComicVerse-Portable-v1.3.1-win-x64.zip` SHA256为 `C5262B7ACC1CB917A3B059F6B9376A9EAA6247F986FEC44C115B9B1048DB2CFA`，与批准ZIP完全相同。
- `publish/使用说明.txt`与批准ZIP内同名条目哈希均为 `675CCFE52991F5DCECD99EBCC1E6D626F9B62E33A5628CCEAD99A13AD28C6C66`。
- 70项源码／工具仍0差异，manifest仍为 `DACFB93C0F90154C5FC4727B7FE9931EC7F212838E19F7CBD75DBD56005BBB32`。
- `publish/`实际仅有EXE和使用说明，没有版本子目录。

核验证据为完整QA证据目录中的 `delivery-verification.json`。交付满足“最终制品先独立验收，再复制同一字节”的门禁。

## 测试隔离

本轮运行只使用 `tools/Invoke-IsolatedProcess.ps1` 创建的独立 Windows Desktop，不切换用户桌面，不注入用户桌面输入；每次运行使用独立数据目录与生成的 fixture。禁止临时 WPF 宿主和直接启动旧测试程序。runner 通过 Job 约束进程树，超时和非零退出保留为失败；另从实际 App 写出的 `smoke-environment.json` 核验 Desktop 与 ErrorMode，不仅信启动器声明。

用户报告 dotnet 原生错误弹窗后，团队曾暂停全部 WPF／dotnet 运行并排查；恢复前 leader 验证了独立桌面和故障退出。QA 没有遗留自行启动的测试进程，也没有操控用户桌面处理弹窗。这是测试隔离流程修正，不代表已诊断或修复所有 Windows／WPF 原生故障。

## 原版漫画独立复现

证据：`artifacts/qa-logic-baseline-c5d8f413/`。当时 Core 和小说部分已有修复，漫画双页与页内保存产品逻辑仍为原版，不能混称整个 App 都是原版。App DLL SHA256：`2BCB29092B643CCA57A305366F9ABD9368EE4E056E2BEF1A38BA13E86238AE71`。

2026-11-02 23:34:44 至23:34:55，runner退出1、11.3秒、未超时。实际Desktop `ComicVerse-QA-9a544b7dbf594536975b4ae12683b0db`，ErrorMode32771；无stderr或崩溃标记。

| 确认问题 | 实际证据 |
| --- | --- |
| 混合横竖双页回退错误 | 竖0-1→横2→竖3-4后回退，实际落到竖1+横2；从横2回退也落到1+2。与先前正向分组不一致 |
| 横页与邻接竖页被错误合并／跳过 | 竖0+横1被组合显示，Next直接跳到2-3，横页不能独立阅读 |
| 单张长条漫页内位置未自动保存 | 真实视口滚到0.6500，仍为page0且窗口保持打开，等待2.6秒后DB仍为0.0000；滚动后未手动flush，关闭保存不能替代这一证据 |

漫画原8条判据3过5失败，代表上述两类产品机制问题，不声称发现5个彼此独立的根因。QA目视相关双页与长条漫截图确认实际内容和状态相符。

同一次运行的小说模式保位2项通过；其余小说组被测试夹具内容重复触发正常导入去重，未进入产品步骤。QA修正为每组文本标题包含唯一名称后重跑，没有把夹具错误当成产品缺陷，也没有绕过导入器。

## 修复后开发专项

证据：`artifacts/qa-logic-fix-4ce77ba2/`。App v1.3.1 DLL SHA256：`EBFA0861CC1A2F636B7354B92B546F56EFF544A4B41088DB4147231AA55BE780`。测试文件 SHA256：`02BF05F370123C35158A3B34D440DCC08F2C34253779703C5623048C0FA29E8E`。

2026-11-02 23:50:42 至23:51:00，**31/31通过，runner退出0，18.8秒，未超时**。实际App Desktop `ComicVerse-QA-185a3993d2c14b9b8a59d4a82e8e2eb9`与runner一致、ErrorMode32771、SmokeProcess=true；stderr为0字节，无crash／fault／exception标记。

| 范围 | 实际结果 |
| --- | --- |
| 混合横竖双页 | 原5条失败全部闭环；前进与回退回到相同逻辑组，横页不和竖页合并 |
| 快速模式与双页命令 | 连续Next两次到3-4、独立连续Prev两次到0-1；真实PNG像素、左右图像与DB一致。Next未落地后切paged并明确末页4，最终可见内容及DB仍为paged/4 |
| 奇数竖页与边界 | 竖竖竖横竖竖正向为[0,1]→[2]→[3]→[4,5]，反向回原组；完整末对Next不重复末页，保存进度1.0000；页首页尾Prev/Next逐次执行，不以净计数抵消 |
| 单张长条漫自动保存 | 同page0的真实视口0.6500，仍开窗2.6秒后DB从0.0000变为0.6500，未依赖关闭保存 |
| 小说模式保位 | 翻页8/14（0.538）切scroll实际0.538；scroll70%切回10/14（0.692），接受分页量化误差，未复用旧第8页 |
| 自动滚动停止 | 无任何先行wheel，启动后正文真实PreviewMouseDown路由使toggle关闭；offset1121.1在随后400ms保持不变 |
| 书签删除与空白 | 选中其他章书签，在删除按钮内glyph发出真实预览隧道路由，不跳章、不隐藏面板；Click只删目标，剩余书签ID正确；空白区域不激活已选行 |
| 章末意图 | scroll End确实到最后章末；换模式、换章并重进末章回首页；上一章末页请求被同回合跳章中断后，稍后重进该章仍为首页 |
| 写入失败与重试 | 只对fixture书籍settings key注入SQLite失败触发器：DB37／会话113；移除触发器后同值113直接关闭，重新建Settings并重开Reader，DB与真实文档均113。未反射dirty私有状态，最终清理触发器 |
| 新增异步守卫 | 首次条漫初始化被模式连续切换打断后，最终实际page8与DB一致；漫画／TXT Show立即Close保持已有历史值；已存条漫未ready就切paged仍保留page8。这些是新守卫回归，旧版没有做对应实际复现，不列为原版已复现缺陷 |

本次唯一ERROR为故意注入的 `SQLite Error 19: QA requested settings failure`，来源为已知fixture书籍设置写入；恢复后持久化成功。它属于预期故障注入证据，不是未处理异常，不能据此忽略其他错误日志。

QA目视检查了混合双页回退、完整末对、边界队列、书签删除后面板、设置重开和快速切paged等截图，并与实际图像像素、控件状态、真实视口和DB断言交叉核验。leader另提供39项Core测试通过的开发证据（含BOM、写入重试与后台扫描取消），不替代最终制品验收。

## 范围与未覆盖

- 已声明的书签能力是页／章节级，scroll书签不保存精确像素位置；本轮未把该声明限制误报成缺陷。
- 未穷尽所有书库移除／重导／重置和打开窗口的组合、所有编码重载／搜索／关窗竞态、所有设备输入顺序；现有回归和源码审查不等于穷尽证明。
- 未穷尽海量真实书库、网络盘、复杂／损坏商业EPUB、RAR／7Z、缺失编解码器、Windows／DPI／字体组合、历史数据升级或纯净Windows首次PDF原生库提取。
- 竞品未安装实测；完整EPUB CSS、竖排和TTS未实施，不能宣称已验收。

上述未覆盖范围仍然适用；通过结论只针对本次已记录的制品及验收范围。
