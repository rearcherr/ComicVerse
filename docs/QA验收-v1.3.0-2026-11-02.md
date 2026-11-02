# ComicVerse v1.3.0 独立 QA 验收

日期：2026-11-02。执行角色：独立 QA；开发、构建、打包由 leader 及开发 agent 完成，QA 独立校验并运行冻结候选。

## 当前结论

**通过：只放行本报告指定哈希的候选三（RC3）v1.3.0。** 已对最终 ZIP 解包出的相同 EXE 独立运行全量隔离回归，核查关键截图和日志，并在运行前后校验源码与制品哈希一致。候选一、二曾因独立 QA 发现的问题被拒绝，修复后在 RC3 完整重验，没有沿用旧候选的通过项代替最终验收。

放行仅针对本轮排版扩展、TXT／基础 EPUB 正文查找及相关回归范围；未覆盖项见文末。正式交付必须复制下列同一制品，不得未经重验重新构建或替换哈希。

## RC3 最终制品与运行证据

| 项目 | 独立核验结果 |
| --- | --- |
| 版本／EXE大小 | 1.3.0／75,003,901 字节 |
| EXE | `artifacts/novel-candidate/ComicVerse.exe` |
| EXE SHA256 | `B57989B1553E9865DDC1ADBDE0FE43BAB25645C46771B106DB3AFCF96C7F7FF6` |
| ZIP | `artifacts/novel-candidate/ComicVerse-Portable-v1.3.0-win-x64.zip` |
| ZIP SHA256 | `083816A7C0506B906CD9F597574BE294335CDD4395D30436904C1D9A349EA493` |
| source-freeze SHA256 | `C84AA1B20E49280645A06E4C0C2E9280B189D21681E883BFC281A088B3ECB085` |
| ZIP与冻结检查 | 13项包内容完整；ZIP项流、解包EXE、候选EXE哈希相同；65源码运行前后均0差异；EXE／ZIP／manifest后检哈希均未变 |

证据根目录：`artifacts/qa-rc3-v1.3.0-84ca61e9/`。从ZIP独立解包，以包内samples执行 `ComicVerse.exe --smoke samples`；普通隔离沙箱、新DB、离屏WPF捕获，未控制用户桌面、鼠标、键盘或真实书库。2026-11-02 **22:41:31 至22:42:56（+08:00），84.7秒，退出码0，未达到300秒超时**。

证据文件包括 `candidate-verification.json`、`execution-start.json`、`execution-result.json`、`verification-after-run.json`、`qa-decision.json`、`smoke/summary.txt`、34张离屏截图和独立数据日志。summary SHA256：`EB6DAF0FAA619878F95EFBA15B3FE0DDA03C75AF9BA67273DA3FA9149D5140A6`。stderr为0字节，无smoke-crash或未处理异常；PDF安全目录回退到既有LocalAppData私有缓存有明确日志，后续PDF回归通过。

## RC3 实际验收结果

| 范围 | 结果与关键证据 |
| --- | --- |
| 排版扩展 | **30/30通过**。新范围、精确输入、校验、钳制、有效／非法草稿直接关闭、0值、按书恢复均通过。900×560最大边距下实际ContentBox高91.8；字号32×行距2.5时高240，真实行高80；立即关闭小窗重开仍保存请求240且版心可读。滚动模式恢复完整请求上下边距 |
| 双向resize与保存 | **通过**。15→7页后内部索引／实际页号／DB为6／7／6；7→15页后为13／14／13。旧model仍在新页数有效范围内的15→7场景为4／5／4。底栏均与实际Master一致，截图确认此前14/15却标第7页的问题已消除 |
| 原生分页控件 | **通过**。公开NavigationCommands.NextPage／PreviousPage直接驱动WPF查看器，两步均验证Master、模型、真实底栏和flush后的DB一致；没有借用Reader自身Next绕过原生路径。深色英文页号与箭头清楚可辨 |
| TXT正文查找 | **16/16通过**。两章后部中文标点字面查询、跨章准确选区／偏移／实际后页与存盘、同一已选结果单击及Enter重定位、上一处、滚动定位、空／无匹配、500截断提示、新查询取消旧查询、关阅读器清理均通过 |
| 超长单段落 | **通过**。唯一末部命中准确选择，字符偏移与生成前缀长度一致，真实滚动比例0.97，起始字符整行位于ScrollContentPresenter视口内，截图可见目标词 |
| 基础EPUB UI端到端 | **8/8通过**。QA独立生成合法基本结构的两章EPUB3，包含标题、实际解码的96×64 PNG前置块与70段正文；实际搜索跳到第2章12/14页，BlockIndex62、UTF16偏移7（前缀含emoji）、Selection及保存位置准确；滚动命中整行真实可见。没有把Core服务测试当成UI证据 |
| 原有小说 | **通过**。7项排版恢复、立即关闭保存与全局默认隔离；真实分页、章末接章、短篇完成、滚动接章及三章长篇0.50位置重开恢复；滚轮整格／半格／四格／反向为144／72／576／-144 px，跨章与分页忽略正确 |
| 漫画／书架／PDF | **通过**。漫画翻页、条漫、双页、远跳、RTL、缩放按书记忆、异高页切换、拖动／横向平移、单击释放捕获；书架筛选／继续阅读／空库和无结果提示／导入刷新；包内与200页生成PDF均通过 |

小样例的小说子模式恢复因本章无法滚动而保留skipped说明，随后生成三章长篇验证了实际滚动模式、章节与位置恢复，不将skipped本身当作独立通过项。leader另提供35项Core测试通过的开发证据，不代替本次最终EXE验收。

QA目视核查RC3的 `qa-spacing-minsize-paged.png`、`qa-spacing-reshrunk-paged.png`、`qa-spacing-valid-model-resize.png`、`qa-spacing-native-navigation.png`、`qa-spacing-font32-paged.png`、`qa-spacing-minsize-reopened.png`、`qa-spacing-minsize-scroll.png`、`qa-novel-search-results.png`、`qa-long-paragraph-search.png`、`qa-epub-search-results.png`、`qa-epub-search-paged.png`、`qa-epub-search-scroll.png`。确认内容可读、实际页与底栏一致、搜索标题／摘要／数量清楚、目标词实际在对应页或视口中。

本机生成样例中条漫远跳约0.2秒、内容覆盖100%／空白条0；200页PDF约2.0秒打开、最大停顿132ms；整个阅读回归最大UI停顿233ms。数据只描述本次测试，不作为所有文件或机器的性能承诺。

## 交付后独立核验（通过）

2026-11-02 **22:45:50（+08:00）**，leader复制交付后，QA再次独立核验文件字节，无重建、无重复运行全量回归。

- 原位置 `publish/ComicVerse.exe` SHA256为 `B57989B1553E9865DDC1ADBDE0FE43BAB25645C46771B106DB3AFCF96C7F7FF6`，与已通过RC3及ZIP内EXE完全一致。
- `dist/ComicVerse-Portable-v1.3.0-win-x64.zip` SHA256为 `083816A7C0506B906CD9F597574BE294335CDD4395D30436904C1D9A349EA493`，与已批准ZIP完全一致。
- `publish/使用说明.txt`与批准ZIP内同名条目逐字节哈希一致，SHA256均为 `6540E61CACBD64ACB1D3ACAE4E177C7D41E55BF0B42B465C633C509B7A9C70CB`。
- source-freeze仍为65文件，差异0；manifest SHA256仍为 `C84AA1B20E49280645A06E4C0C2E9280B189D21681E883BFC281A088B3ECB085`。
- `publish/`实际仅有 `ComicVerse.exe`与`使用说明.txt`，没有新增版本目录。

核验证据已保存于本次RC3证据目录的 `delivery-verification.json`。交付制品满足“QA通过后复制同一制品、不另行重建”的门禁要求。

## 历史候选与缺陷闭环记录

以下为被拒绝候选及开发专项过程的历史证据，均不能替代上述RC3放行记录。

## 候选二：最终整包独立验收未通过

| 项目 | 独立核验结果 |
| --- | --- |
| 版本／EXE大小 | 1.3.0／75,000,066 字节 |
| EXE SHA256 | `9554834886DEDD1F8EF8C16F9CC75F4690C239486D71A74B78F06480D0ED8695` |
| ZIP SHA256 | `66F3F170856BFEA702108864380F36EC92DD42E33D3B92C870C3AF6957C30416` |
| source-freeze SHA256 | `14610EC11ABFBA008294159A0006A52779A0917FC7E7788C1EA3B15B71462B46` |
| ZIP内外EXE | ZIP项流／解包EXE／候选EXE均为上述相同哈希；13项包内容完整 |
| 冻结检查 | 65源码运行前后均0差异，EXE／ZIP／manifest运行后哈希均未变 |

证据根目录：`artifacts/qa-final-v1.3.0-467ad3b2/`。普通隔离沙箱，新DB，使用ZIP解包EXE和包内samples全量 `--smoke`，2026-11-02 22:26:11 至 22:27:35，**83.7秒，退出码0，未超时**。生成32张离屏截图；stderr为0字节，无smoke-crash。`smoke/summary.txt` SHA256：`9917DAA8CFBF07081385D3DABD81AFCCFD2CCFCC0E092B19F4DBF64A834E99F2`。

自动结果：排版23/23、TXT查找16/16、EPUB UI8/8、长段真实选择与视口，以及漫画／书架／小说／PDF全量回归均通过。当前测试只针对 resize 页数收缩断言游标，反向增长覆盖有缺口，QA目视发现后未放行。

**阻塞证据：`smoke/qa-spacing-reshrunk-paged.png` 原生实际页号为 `14 of 15`，应用底栏却为“第1/1章 · 第7页”。** 操作序列为900×560末页→放大900×900后实际页数15缩为7并对齐末页→再次缩小900×560，实际页数增回15，WPF将显示位置带到第14页，但模型仍为旧第7页。只读审查 `ReaderWindow.NovelPageLayout.cs` 确认只在页数收缩且模型越界时同步，反向增长或模型仍在有效区间时不处理；正常保存读取模型，存在保存错位风险。不是要求 resize 前后精确文本必须不变，而是要求稳定后显示、内部状态和保存页号一致。

已经目视候选二的最小尺寸、最大边距、大字、重开、grow／reshrink、滚动、TXT结果、长段、EPUB结果／分页／滚动截图。最大边距与高字号版心、深色原生导航对比度均已修复；EPUB目标位于12/14页、Block62、UTF16偏移7，真实命中行可见。整段阅读回归最大停顿901ms，200页PDF约2.0秒打开、最大停顿132ms，条漫远跳约0.2秒；这些仅是本机生成样例数据，不能宣称所有真实书库均流畅。

候选二日志中PDF安全目录回退信息与候选一相同：拒绝独立数据根目录内native提取位置，改用LocalAppData私有缓存，PDF回归随后通过。不能因此声称已经覆盖纯净Windows首次提取。

候选二修复后的开发专项复测：`artifacts/qa-sync-dev-bb1e2ac3/`，App DLL SHA256 `C2B166B3D5DFF5C5CC6B4B71E1C011D0137960E7BED9CD944845075ACB66C3A0`。2026-11-02 22:39:47 至 22:40:00，12.3秒，退出码0；排版 **30/30**、TXT **16/16**、EPUB **8/8**、长段真实视口均通过。

新增反向增长断言和目视截图确认 `7→15` 页后模型／实际显示／DB为 **13／14／13**，底栏第14页与原生14/15一致。旧模型仍在有效区间的独立场景中，页数15→7，旧模型4仍小于新页数，模型／实际显示／DB为 **4／5／4**。使用公开 `NavigationCommands.NextPage`／`PreviousPage` 驱动原生查看器，两步均检查实际Master、模型、真实底栏与DB一致，未借用Reader自身Next实现或桌面输入。QA目视reshrunk、valid-model和native截图确认一致；无stderr/crash。专项通过不构成最终制品放行，等待候选三完整整包验收。

## 候选一制品与冻结校验

| 项目 | 独立核验结果 |
| --- | --- |
| 产品版本 | 1.3.0 |
| EXE | `artifacts/novel-candidate/ComicVerse.exe`，74,992,955 字节 |
| EXE SHA256 | `CB08391A867F2387C90FD531E79B04B6599DFA1208735EDCAFE58ADC4C017FB1` |
| ZIP | `artifacts/novel-candidate/ComicVerse-Portable-v1.3.0-win-x64.zip` |
| ZIP SHA256 | `4A746905A05BCC6D3F48AB5881A529B986F3A218D9EA405093549EF3ACD56D06` |
| ZIP 内 EXE | 对 ZIP 项流独立求 SHA256，与上述 EXE 一致；解包后再次一致 |
| 源码冻结 | `source-freeze.json` 的 64 个文件均一致，运行前后差异均为 0 |

QA 测试的是 ZIP 解包出的同一哈希 EXE，没有重新编译测试程序。最终交付必须与最终通过的制品一致。

为保留候选一证据，leader 后续将上述旧制品精确改名归档为 `ComicVerse-rc1.exe`、`ComicVerse-Portable-v1.3.0-win-x64-rc1.zip`、`source-freeze-rc1.json`，均位于原 `artifacts/novel-candidate/`，原哈希保持不变。

## 执行方法与证据

- 普通隔离沙箱执行，未申请提权，未操作用户桌面、鼠标或键盘，未使用真实书库。
- 解包目录：`artifacts/qa-v1.3.0-771c66ac/bundle/`。使用包内 `samples` 执行 `ComicVerse.exe --smoke samples`。
- 独立书库：同一 QA 目录下 `data/`；截图与结果：`smoke/`。WPF 控件事件和文档状态驱动发生在本次测试进程内。
- 启动于 2026-11-02 01:29:23 +08:00，结束于 01:30:43，约 80.7 秒。退出码 **0**，未达到 300 秒超时。
- `execution-start.json`、`execution-result.json`、`candidate-verification.json`、`verification-after-run.json` 保存启动、运行与制品校验记录。
- `smoke/summary.txt` SHA256：`D62C1BB66F9BE54EF6572FF57855E79EADEB92FB660D65F2F4DAE6EDA2D61A98`。
- `stderr.log` 为 0 字节，未生成 `smoke-crash.txt`。`data/logs/app.log` 的一条 ERROR 来自 PDF 原生库目录安全校验：独立数据目录不在 LocalAppData，程序拒绝该提取位置并回退私有缓存；后续 PDF 测试通过。其余日志无未处理异常。

## 候选一结果

| 范围 | 结果与实际判据 |
| --- | --- |
| 新排版范围 | **11/11 通过**：段距 0–120、页边距 0–240，精确数值生效；空值／非数字不破坏已生效设置，越界钳制并提示；有效 0 草稿直接关窗后恢复，非法草稿关闭保留旧值 |
| 900 宽窗口与侧栏 | 先打开目录再打开排版；在段距 120、页边距 240 下布局稳定，正文净宽约 127 DIP；目录与排版互斥，截图中标题可正常换行，排版控件与底栏可见。高度为默认 900，**未覆盖最小高度 560** |
| 正文搜索 | **16/16 通过**：两章后部中文字面查询（含方括号、点和星号），两处准确结果；唯一搜索窗口；跨章跳转、精确选中及字符偏移正确；实际位于第 7 页，显示页号与内部页号一致；定位进度写入书库 |
| 搜索重复激活 | 离开搜索位置后，重新单击同一已选结果以及 Enter 再次激活均返回正确章节与位置；“上一处”按钮真正导航正文 |
| 搜索异常与异步 | 空查询、无匹配、超过 500 处的截断提示、新查询覆盖旧查询，以及阅读器关窗取消导航并关闭搜索窗口均通过 |
| 超长单段落 | 后部唯一命中准确选中；字符偏移与生成文本前缀一致，真实滚动比例 0.97；选择起始行完整落入真实视口（字符 Y=189.625，高 22.86；视口高 758.5） |
| 原有小说回归 | 按书恢复 7 项排版设置、立即关闭恢复、不污染全局默认；滚轮整格／半格／多格为 144／72／576 px，反向为 -144 px，跨章边界与分页模式忽略均通过；长篇滚动位置重开恢复 0.50；真实分页、章末接章、短篇完成与方向键通过 |
| 漫画与书架回归 | 翻页、条漫、双页、RTL、缩放按书记忆、异高页切换、远跳、条漫拖动与横向平移、单击释放鼠标、书架空库／筛选／继续阅读／导入刷新通过；条漫接缝空白行数为 0 |
| PDF 与响应 | 包内 PDF 及生成的 200 页 PDF 通过；后者打开约 2.0 秒、最大停顿 147 ms；整个阅读回归最大停顿 326 ms。仅代表本机生成样例，不作为普遍性能承诺 |

原有小样例的“小说子模式持久化”一项因样例不可滚动而标注 skipped；全量测试随后以生成的三章长篇验证滚动模式、章节及真实位置恢复，不把该 skipped 项当成单独通过证据。

QA 已目视检查：`qa-spacing-minwidth.png`、`qa-novel-search-results.png`、`qa-novel-search-paged.png`、`qa-novel-search-scroll.png`、`qa-long-paragraph-search.png`、`reader-webtoon.png`、`shelf-minwidth.png`。搜索结果窗口的查询、两章标题、摘要、数量与导航状态清楚；命中词实际出现在目标页和滚动视口内。精确选中由真实 WPF Selection 与字符偏移额外核验，不仅依赖截图。

非阻塞外观记录：深色主题下 WPF FlowDocumentPageViewer 自带的英文页号和左右箭头接近黑色，不易辨认；应用自己的中文页号与翻页底栏仍清楚、可用。该项列为后续界面改进，不据此判本轮正文导航失败。

## 补充边界回归：已确认的阻塞问题

在 leader 授权下，QA 仅补充自己的测试文件，leader 统一编译开发构建（不是新的最终制品）。普通隔离沙箱执行 `--smoke --novel-expansion-only samples`，2026-11-02 01:33:46 至 01:34:04，18.0 秒，退出码 **1**。

开发 App DLL SHA256：`894D9D877AC9435DCAAC1C256FC6D488AFE378C45A9FA09285812D2AA97D57C8`。证据：`artifacts/qa-minheight-dev-8f1a9e20/` 的运行记录、`smoke/novel-expansion-summary.txt`、`qa-spacing-minsize-paged.png`、`qa-spacing-minsize-scroll.png`。

复现：打开生成的三段短篇，段距 120、页边距 240，宽 900、高 560，排版侧栏打开；通过正文搜索定位末段。

- 排版 **14/15**，仅“最小窗口真实内容区可见”失败；搜索 **16/16** 与超长段落仍通过。
- WPF 实际页大小 `607×392.1`，`DocumentPage.ContentBox=(240,240,127,0.003333)`；本段实际设置行高 `30.6`。经过真实视觉坐标变换仍是约 `0.003333` 高，未使用声明 Padding 推算。
- QA 目视截图确认：不是全空或卡死，但短篇被拆成 9 页，当前页仅一行文字，命中词所在句跨页。WPF 将字形溢出到近乎为零的内容区外，不能将此结果认作正常版心。
- 同组合滚动模式的命中行完整在视口内：字符 Y≈117.31、高22.86；视口高418.5，因此滚动边界测试通过。

已将失败证据交给 leader 组织开发修复；QA 不修改产品代码。下一候选还将补充最小合法两章 EPUB 的图文及搜索 UI 正常路径，不能沿用候选一的通过项直接放行新哈希。

## 修复开发回归记录（不构成最终放行）

证据：`artifacts/qa-spacing-fix-dev-f9b72c46/`。开发 App DLL SHA256：`C80BC3EA00A78906AFB80FC4A3CD17D5176D6B2556D617504768FFF2EE738BE1`。2026-11-02 22:22:10 至 22:22:21，10.9 秒，退出码 **1**。

- 排版 **23/23**、TXT 搜索 **16/16**、长段精确选择和真实视口通过。
- 最小窗口实际版心高度已由约 `0.003333` 恢复为 `91.8`，实际上下留白约 `150.15`。放大至 900 高后实际上下留白恢复 `240`，再缩小仍可读；请求设置保持 240。
- 实际页数从 **15 缩为 7** 后，内部索引／显示页号／书库保存索引为 **6／7／6**，均一致。
- 字号 32、行距 2.5 时真实行高 80、ContentBox 高 240；立即关闭后以 900×560 新窗口重开仍保存请求 240，真实版心高 240。滚动模式恢复完整请求上下留白，命中行完整可见。
- QA 目视检查了 minsize、grown、font32、scroll、reopened 和 TXT paged 截图：文字可读；原生英文页号和左右箭头已改为清楚的浅色，候选一的外观问题在开发构建中修复。
- 本次失败来自 QA 新增 EPUB 夹具：`PngBitmapEncoder.Save` 直接写不支持 Seek 的 ZipEntry 流，抛出 `NotSupportedException`，尚未进入 EPUB 产品步骤。已在 QA 所有的测试文件中改为先编码到 MemoryStream 再写入 ZIP；没有降低产品断言。等待统一构建后重跑。

修正夹具后全新独立数据目录重跑：`artifacts/qa-epub-dev-6c8122fd/`。开发 App DLL SHA256：`1B5F712DF3223C461CA49AECE327DDAE1F89F90AE9413BCF7AF552E1FA76FA10`。2026-11-02 22:23:42 至 22:23:53，11.8 秒，退出码 **0**。排版 **23/23**、TXT 查找 **16/16**、超长段选区及真实视口、EPUB UI **8/8** 均通过。

EPUB 是 QA 独立生成的最小 EPUB 3 两章文件，包含 OPF/spine/nav、每章标题与前置 PNG 图块、各 70 段正文。实际 UI 搜索后跨到第 2 章第 **12/14** 页，图文后的 BlockIndex **62**、含 emoji 前缀的 UTF-16 字符偏移 **7**、真实 Selection 文字、进度保存均正确；滚动命中字符行 Y=189.625、高22.86，完整处于758.5高视口。QA 目视 `qa-epub-search-results.png`、`qa-epub-search-paged.png`、`qa-epub-search-scroll.png` 确认查询摘要、目标词与页码；96×64 PNG Source 已实际解码加载，第一章首页仅标题，图片块在后续分页，不据此宣称复杂图片排版已覆盖。

上述开发构建通过不能替代最终制品的完整隔离验收；等待新的冻结 EXE／ZIP 后再给最终放行结论。

## 未覆盖与明确未实施的范围

- RC3已覆盖TXT及独立生成的正常两章EPUB UI搜索、跨章定位和视口；未用商业出版物或复杂CSS／图片布局EPUB全面验证。leader提供的35项Core基础服务测试与本次实际UI证据分别记录。
- 竞品仅依据官方材料调研，未安装实际体验。
- 完整 EPUB CSS 还原、竖排、TTS 未实施，不能宣称已验收。
- 未穷尽大规模真实书库、网络盘、各 Windows／DPI／字体组合、真实 RAR／7Z 大包、复杂或损坏 EPUB、缺失系统编解码器、历史书库升级等专项场景。
- 未单独验证纯净Windows首次PDF原生库提取；本次使用既有私有PDF缓存。未穷尽导航进行中叠加连续resize等所有快速竞态组合。
- 本轮验收仅覆盖已选定的排版扩展、正文查找和必要回归范围，不代表任意文件与全部功能均无缺陷。
