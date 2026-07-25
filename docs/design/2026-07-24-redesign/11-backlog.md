# 11. 实施积压清单(按优先级)

复杂度:S(≤1 天)/ M(2–4 天)/ L(1–2 周)/ XL(>2 周)。风险:低/中/高。
验收标准均隐含:三主题 × 三断点无回归、字符串入 `.resw`、交互控件有自动化名称、测试绿。

## Epic A — 设计系统基础(阶段 1)

| # | 任务 | 用户影响 | 技术影响 | 依赖 | 风险 | 复杂度 | 关键验收 |
|---|---|---|---|---|---|---|---|
| A1 | 令牌增补(表面/强调/阴影/动效/字体/布局)+ 浅色画刷修复 | 浅色主题渲染正确 | 全局样式基座 | — | 中 | M | 浅色下缩略图/预览画刷正确;新令牌可解析 |
| A2 | ControlStyles/AppShell 字面值改令牌 | 一致性 | 消除漂移 | A1 | 低 | S | grep 无硬编码 Padding/Corner 于样式文件 |
| A3 | StatusBadge 修复(12px、ThemeResource 即时换肤) | 主题切换即时生效 | — | A1 | 低 | S | 运行中切主题徽标即时重绘 |
| A4 | PageShell + EmptyState + ErrorBar + SkeletonCard | 三态统一强制 | 页面骨架 | A1 | 低 | M | IsEmpty 无 UI 缺陷类问题不可再现(模板必填插槽) |
| A5 | DialogService + ConfirmDialog + HotkeyCaptureDialog 组件化 | 对话框行为一致 | 消灭代码后置手搓 | A4 | 低 | M | 全部对话框经服务创建;Esc/焦点圈定一致 |
| A6 | 窗口最小尺寸 960×600 + 默认 1280×800 | 消除极窄破版 | AppWindow 接入 | — | 低 | S | 不可缩至 960 以下 |

## Epic B — 外壳/导航/托盘(阶段 2)

| # | 任务 | 用户影响 | 技术影响 | 依赖 | 风险 | 复杂度 | 关键验收 |
|---|---|---|---|---|---|---|---|
| B1 | 两态侧栏 + 面包屑 + 返回栈 | 导航孤儿根治 | NavigationMap 扩展 | A4 | 中 | L | 工作区内高亮正确;Alt+← 可退;一致性单测覆盖分节 |
| B2 | **托盘 + 关窗托盘化**(首次询问) | 关窗可玩(P0) | 生命周期重写 | 平台 spike | 高 | L | 关窗后引擎存活;托盘操作不夺游戏焦点(FlaUI 断言) |
| B3 | 深链协议 + `--profile {id} --start` | 快捷方式直启 | 激活路由 | B1 | 低 | M | 冷/热启动均正确路由 |
| B4 | 运行状态胶囊重构(图标+文本+点击深链) | 状态三通道 | — | A1 | 低 | S | 高对比度下可辨;读屏播报状态名 |

## Epic C — 数据/状态层(阶段 3)

| # | 任务 | 用户影响 | 技术影响 | 依赖 | 风险 | 复杂度 | 关键验收 |
|---|---|---|---|---|---|---|---|
| C1 | 四 Store + 差量集合更新 + IUiDispatcher | 列表不再丢选中;加载去重 | 替代逐页拉取 | — | 中 | L | 设置页渲染仅 1 次加载;刷新保持选中 |
| C2 | **RuntimeStateStore 接线**(OcrResult/TranslationOutput→准入→Store) | 实时状态可见(P0) | 孤儿转正 | C1 | 高 | L | 迟到结果被拒并计数;活动流实时到达 |
| C3 | 命令三态(Applying/Applied/回滚) | 消除假死;满足 250ms 约束 | 服务返回 Result | C1 | 中 | M | 暂停切换 250ms 内可见 Pending |
| C4 | 模型拆分(RegionDraft 六子记录;ProviderRow 拆分)+ 可观察化 | — | 消上帝模型 | C1 | 中 | L | 映射层单测全绿;枚举不再走字符串 |
| C5 | 历史档案关联修复 + DeleteAsync 仓储化 | 历史数据正确 | 层次修复 | — | 低 | S | 多档案下历史归属正确 |
| C6 | 能力上限接入 UI("已用 2/4"+ 禁用原因) | 上限可见 | 既有服务首用 | C1 | 低 | S | 通道/区域编辑器显示配额 |

## Epic D — Home 与工作区骨架(阶段 4)

| # | 任务 | 用户影响 | 技术影响 | 依赖 | 风险 | 复杂度 | 关键验收 |
|---|---|---|---|---|---|---|---|
| D1 | Home(档案网格+置顶/取消置顶+运行面板+活动摘要) | 启动 3 步内;常用档案优先可见 | 替代两页 | B1,C1 | 中 | L | 启动/暂停/停止全在 Home 完成;失败落卡行内;置顶状态持久化且排序稳定 |
| D2 | ProfileWorkspace 容器 + StickySaveBar + 工作区级脏模型 | 无模态保存流 | 五节共享草稿 | B1,C1 | 高 | L | 跨节切换保留脏状态;离开统一询问 |
| D3 | 概览节(就绪清单深链 + 目标行 + 目标级设置归位) | 修复入口一步达 | 作用域归位 | D2 | 中 | M | 每个未就绪项点击直达修复位 |
| D4 | 真实缩略图链(运行→缓存→图标) | 卡片可辨识 | 缩略图缓存 | C1 | 低 | M | 三级回退可测 |

## Epic E — 复杂表单(阶段 5)

| # | 任务 | 用户影响 | 技术影响 | 依赖 | 风险 | 复杂度 | 关键验收 |
|---|---|---|---|---|---|---|---|
| E1 | RegionCanvas/RegionListPane 控件提取 | — | 解剖 1100 行代码后置 | D2 | 高 | XL | 键盘等价路径全保留(UI 自动化) |
| E2 | capture 节(检查器减负至 3 组) | 找控件不再翻 6 折叠区 | — | E1 | 中 | M | 区域级/目标级零混杂 |
| E3 | ChannelPipelineCard + BudgetPreviewBar(行内管线) | 通道编辑无模态;预算可见 | 删通道对话框 | D2,C6 | 中 | L | 1–4 通道/≤2 润色/防循环约束行内强制 |
| E4 | OverlayStyleEditor(恢复丢失控件 + 溢出策略 + 六状态预览) | 所见即所得 | 复用 OverlayPreviewRenderer | D2 | 中 | L | 字号/描边/对比恢复;六结果状态可切换 |
| E5 | language 节(术语行内编辑 + 空态 + 提示词拆卡) | 术语可编辑 | 拆 GlossaryPage | D2 | 低 | M | 行内编辑;零词条有引导 |
| E6 | `/profiles/new` 引导流(门禁/可点步骤条/逐步实测/草稿) | 首次成功率 | 复用工作区模型与画布 | E1–E4 | 高 | XL | 每步门禁生效;步 3 可绘制;草稿可续 |

## Epic F — 次级页面(阶段 6)

| # | 任务 | 用户影响 | 技术影响 | 依赖 | 风险 | 复杂度 | 关键验收 |
|---|---|---|---|---|---|---|---|
| F1 | ActivityFeed(实时流+筛选+报告) | 排障 3 步定向 | 替代诊断页 | C2 | 中 | L | 1k 事件流畅;RecoveryAction 真实填充 |
| F2 | 历史增强(日期分组 + **保存修正** + 入术语表) | 闭环修正 | 新语义(需产品确认) | E5 | 中 | L | 修正不回写已显示结果;作用域明示 |
| F3 | providers 重构(分组+行内端点+离线横幅) | 离线不再静默失败 | 凭据对话框瘦身 | C1 | 低 | M | 离线时受影响操作均有解释 |
| F4 | settings 分节+搜索+热键作用域+语言资源化 | 设置可寻 | 拆单列长滚动 | C1 | 低 | L | 搜索命中高亮;热键冲突行内可见 |

## Epic G — 收口(阶段 7–9)

| # | 任务 | 依赖 | 风险 | 复杂度 |
|---|---|---|---|---|
| G1 | 三断点全页走查与修版 | D–F | 低 | M |
| G2 | 无障碍全量落实(9.5 规则清点) | D–F | 中 | L |
| G3 | 旧页/开关/孤儿资源删除 | G1,G2 | 低 | M |
| G4 | 端到端验收(产品 §10 UI 项)+ 性能预算 + 稳定化 | G3 | 中 | L |

## 远期(不入首发)

- 命令面板(Ctrl+K)全局搜索/跳转(方案 C 遗产)。
- 档案收藏;删除需输名确认。(档案置顶已纳入首发 Epic D1。)
- 多引擎并行会话 UI 完整态(依赖引擎侧多会话支持;UI 模型已按多会话预留)。
- `RetranslateCurrent` 独立引擎命令后启用对应热键行。

## 实施补记(2026-07-24 交互一致性审计)

硬切换实施过程中发现的、本清单原先未覆盖的缺陷与规格缺口,已修复并记录于此,避免后续 Epic 重复踩坑。

| 发现 | 性质 | 处置 |
|---|---|---|
| 工作区五节各自注册 transient `WorkbenchViewModel`,切节即重新加载,未保存编辑无声丢失;脏状态守卫挂在错误的 Frame 上,永远观察不到脏 | 架构缺陷(D2 前提被破坏) | VM 改单例 + `EnsureLoadedAsync` + 外壳级离开守卫 + `DiscardChangesAsync` 按重载真放弃;页面订阅改为 Loaded/Unloaded 成对 |
| 概览就绪清单三项恒为绿勾 | 假成功路径 | 由 `MatchSeverity`/`Languages`/`RegionCount`/`ChannelCount` 真实推导,图标+颜色+文本三通道,每项深链至修复位(D3 关键验收) |
| 未就绪时"启动"按钮点击无任何反馈 | 静默无操作 | 禁用并给出 ToolTip 原因;强行触发时明示不可用 |
| 删除/导出档案随 `ProfileCenterPage` 一并删除后无入口,而 `IProfileService` 仍有实现 | 能力回归 | Home 档案卡溢出菜单恢复导出/删除,删除走确认对话框并同步清理置顶列表 |
| 源/目标语言仅创建向导可设,§5.7 未给出创建后的归属分区 | 规格缺口 | 落位 language 节语境卡;校验规则与向导一致 |
| 语境卡片非数据绑定,编辑不写回共享草稿 | 静默丢失 + 陈旧覆盖 | 改为逐键回写并标脏,纳入离开守卫 |
| `ValidateForSave` 不拒绝"源语言 = 目标语言" | 不变式只存在于 UI 层 | 下沉至 VM,所有保存路径统一拒绝 |
| 置顶/删除/导入后清空重填整个档案集合 | 丢选中与滚动位置 | 改为原地对账(Move/按值替换/截尾) |
| 新增术语对话框在源或目标为空时静默关闭 | 与保存成功不可区分 | 两项非空前禁用主按钮 |
| 保存命令只存在于 capture 节;channel/overlay/language 编辑无法落盘 | 能力不可达 | **D2 StickySaveBar** 落在 `ProfileWorkspacePage` 容器:脏/保存中/成功(含 Applied 语义)/失败四态,放弃走确认对话框;capture 节工具栏保存按钮删除,Ctrl+S 保留 |
| 托盘菜单在未运行时仍可点击暂停/覆盖层/手动 OCR | 无效命令可点 | 非运行态置灰(`MF_GRAYED`),图标与 Tip 区分暂停态 |

**D2 状态:** 容器 + StickySaveBar + 工作区级脏模型已落地(跨节保留脏、离开统一询问、保存结果含 Applied 语义)。面包屑仍属 B1 未完成部分。

## 全量缺陷清扫(2026-07-24 第二轮)

| 发现 | 性质 | 处置 |
|---|---|---|
| 界面语言设置只写库不生效:全仓无任何代码设置 `PrimaryLanguageOverride`,选中中文后持久化成功、下次进入设置页仍显示中文,但界面一个字符串都不变 | **假成功路径**(最严重) | 启动时按存储语言设置 `Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride`(在创建外壳之前,首帧即生效);切换时立即应用并显式提示"已打开页面需重启才全部生效",不隐瞒局部生效的事实 |
| `AppCrashReporter.Install()` 只挂 `AppDomain` 与 `TaskScheduler`,未挂 WinUI 的 `Application.UnhandledException`;UI 线程异常(全部 34 个 `async void` 处理器)走该通道后进程 fail-fast,崩溃报告全部丢失 | 诊断能力缺失 | 新增 `ReportFatal`,在 `App.OnLaunched` 订阅 `UnhandledException` 并同步写报告;**不设 `Handled=true`**,崩溃仍然崩溃,只是不再无声 |
| 8 个交互控件有可见标签却无程序化关联(设置页 4 个下拉、术语页版本下拉、向导 2 个列表、合成错误窗详情框),读屏只播报控件类型 | 无障碍缺陷(§9.5) | 全部改用 `AutomationProperties.LabeledBy` 指向既有标签元素,不新增重复字符串 |
| 53 组资源键在源码中不再被引用(已删页面 Diagnostics/RunningTargets/ProfileCenter 遗留等) | 孤儿资源(G3) | 两语言各删 54 行;运行时按名拼接的 `WorkbenchApply*`/`Hotkey{Action,Status,Scope}_*` 四族已识别并保留 |
| 工作区面包屑按资源名拼接取分区标题,键被改名会静默渲染成空 | 静默降级风险 | 新增测试断言六个 `NavWorkspace*.Content` 在两语言均存在 |

**未做且不谎报:** G1(三断点视觉走查)与 G2 的其余部分需要运行中的应用逐页目测,静态扫描只能覆盖到上表这些;本轮未发现固定列宽在 960 最小宽度下溢出的页面(最宽的 overlay 节为 360 + \*,余量约 330px)。设计令牌 `MotionFast`/`MotionNormal`/`SpaceXXL`/`PaneWidthInspector`/`SurfaceBackground`/`SurfaceCardHover` 目前无人引用,属第 9 章已定义但尚未落地的动效/布局规格,不按孤儿删除。

## 运行时缺陷清扫(2026-07-24 第三轮:真跑起来)

前两轮只做静态扫描与单元测试。本轮实际构建并运行应用,用 UI Automation 逐条走遍导航目的地
(全局 4 项 + 设置 5 个分区 + 活动 3 个页签 + 新建向导 + 工作区 6 节),把每次进程死亡与崩溃报告
的元数据令牌解析回方法名后定位根因。**下列缺陷在此之前无人发现,因为这些页面从未被打开过。**

| 发现 | 性质 | 根因 | 处置 |
|---|---|---|---|
| 打开"设置"页必崩(`NullReferenceException`) | 崩溃 | `ListViewItem IsSelected="True"` 在解析该项时同步触发 `SelectionChanged`,`ShowSection` 去引用文件后半段尚未创建的五个面板 | 初始选中改到构造函数 `InitializeComponent()` 之后 |
| 打开"活动"页必崩(`NullReferenceException`) | 崩溃 | 同类:`ComboBox SelectedIndex="0"` 解析期触发 `ApplyFilter`,引用后面才声明的空状态与 repeater。原有 `if (ActivityKindFilter is null) return;` 是症状级挡板,挡不住真正为 null 的那两个元素 | 初始选中移入构造函数;删除那条无效挡板,让同类过早调用继续显式崩溃 |
| 打开新建向导必崩(`ERROR_MRM_NAMED_RESOURCE_NOT_FOUND`) | 崩溃 | 代码侧 `GetString("SetupStep1Caption.Text")` 用了 x:Uid 的点号写法;MRT 运行时按路径解析,必须用 `/` | 4 处改为 `/Text`;`ChannelsSectionPage` 同类 5 处一并修正 |
| 打开"捕获与区域"节必崩(`XamlParseException`) | 崩溃 | `Icon="FitPage"` 不是 `Symbol` 枚举成员(`Icon="Down"` 同样不存在,会在区域列表控件上二次触发) | 两处改用等价 Segoe 字形 `FontIcon`(E9A6 / E74B) |
| 打开"覆盖层样式"节必崩(`XamlParseException` on `RangeBase.Minimum`) | 崩溃 | 编译型 XAML 在元素创建时就挂事件,晚于它的 `Minimum=` 赋值即触发 `ValueChanged`;`WorkbenchViewModel` 是单例,先逛过 capture 节就已有选中区域,提交路径于是对半成品可视树执行 | 构造期用既有 `_updating` 标志抑制提交,`try/finally` 恢复 |
| "保存草稿并退出"写库成功但界面不动 | 静默无操作 | 外壳处理全局导航请求时只写 `Nav.SelectedItem`;向导是从 Home 打开的,选中项本就是 Home,同值赋值不触发 `SelectionChanged`,Frame 永不导航 | 选中项已等于目标时直接 `ContentFrame.Navigate` |
| 捕获目标列表每一项的无障碍名是 `CaptureProbeTarget { TargetId = …, NativeHandle = …, ProcessName = … }` 记录 `ToString()` | 无障碍缺陷 + 信息外溢 | 未设 `AutomationProperties.Name`,项容器回退到数据对象 | 模板根节点绑定 `DisplayName` |

**新增回归测试(共 3 条,App 套件 248 → 251):**

- `DesignTokensTests.Resource_references_match_the_declared_token_type` —— 全仓 XAML 逐属性核对令牌类型,`x:Double` 赋给 `Thickness` 属性即失败(第二轮修的两处 Padding 崩溃就是这一类)。已用故意注入缺陷验证该测试确实会红。
- `DesignTokensTests.Symbol_icon_names_are_valid_enum_members` —— `Icon="…"` 必须是真实 `Symbol` 成员。
- `LocalizationParityTests.Every_literal_resource_lookup_resolves` —— 代码里每个字面量 `GetString("…")` 必须不含 `.` 且在 resw 中存在。

**原生侧:** `artifacts/cmake/windows-x64` 的旧缓存里 `INFINI_ENABLE_LOCAL_MODEL_RUNTIME=ON`(该选项默认 OFF,预设也不设),导致 `cmake --preset windows-x64` 进入 vendored ctranslate2/spdlog 并在 MSVC 14.51 上编译失败——失败发生在第三方代码,不是本仓代码。用 `-DINFINI_ENABLE_LOCAL_MODEL_RUNTIME=OFF` 重新配置后构建通过,CTest **12/12 全绿**(含两条 PowerShell 引擎宿主测试)。

**仍未验证:** G1 三断点视觉走查(需人眼),以及任何需要真实翻译服务凭据的端到端路径(G4)。UIA 走查只证明"页面能打开且进程不死",不证明布局与视觉正确。

## 生产就绪评审(2026-07-25 第四轮:CI 与发布链路)

前三轮全部在本机验证。本轮改看**别人的机器**能不能构建、发布流水线能不能跑。

| 发现 | 性质 | 根因 | 处置 |
|---|---|---|---|
| `main` 从干净检出编译不过:`GitHubReleaseUpdateService.cs(4,27): error CS0234 'Artifacts' 命名空间不存在` | **阻断级**(本机全绿,CI 全红) | `.gitignore` 里 `artifacts/` 未锚定,匹配任意层级同名目录;Windows Git 大小写不敏感,于是 `src/InfiniTranseon.Core/Artifacts/VerifiedArtifactWriter.cs` 从未被提交 | 全部只在根目录出现的输出改为 `/artifacts/`、`/build/`、`/out/` 等锚定写法(`bin/`/`obj/`/`TestResults/` 按项目分布必须保持不锚定,已注释说明);补提交该文件 |
| `INFINI_ENABLE_LOCAL_MODEL_RUNTIME=ON` 时 Debug 构建必失败(vendored `spdlog/fmt/bundled/format.h` 报 C2653/C2061/C3613/C3646/C2988/C2059) | 配置不可用 | 该 pin 的 fmt 8.x 在 `_SECURE_SCL` 打开时(即每个 MSVC Debug 构建)走 `stdext::checked_array_iterator`,该类型已从 MSVC STL 移除(14.51 中不存在)。Release 走 `#else` 分支所以一直没事 | `cmake/patches/ctranslate2-vendored-fmt-msvc.cmake` 作为 `PATCH_COMMAND` 幂等禁用该分支(上游 fmt 后续版本同样删除了它,`#else` 正是 Release 一直在编的代码);pin 变动时脚本 `FATAL_ERROR` 而非静默跳过 |
| `build-release.yml` 用 `-DINFINI_ENABLE_LOCAL_MODEL_RUNTIME=ON`,而 `quality.yml` 用默认 OFF | 覆盖盲区 | 发布产物里的 CTranslate2/SentencePiece 运行时从未被 CI 编译过一次,发布作业将是第一次 | `quality.yml` 原生配置改为 ON,与发布配置一致(ASAN 预设保持 OFF,已注释理由) |

**前一轮的判断需要更正:** 第三轮记为"stale cache 导致 ON 编译失败"并不准确。清空缓存重新配置后,ON 的 **Release** 构建与测试完全正常(13/13);真正失败的只有 **Debug**,原因如上表第二行。

**验证:** 打补丁后 Debug/Release 双配置 `ON` 均构建通过,CTest **13/13**(新增 `infini_model_runtime_abi_tests`);`dotnet test InfiniTranseon.sln -c Release` **784/784**;`build-release.yml` 从 `_deps` 拷贝的九份第三方许可证路径逐一确认存在。

**仍未验证(与本轮无关,继续挂账):** `build-release.yml` 全流程**一次都没跑过**(MSI 打包、SBOM、模型目录签名、release-manifest 签名、五个 `verify-*.ps1`);任何需要真实凭据的端到端翻译路径(G4);G1 三断点视觉走查。**"这个程序能翻译"目前仍是未经证实的断言。**

### 第四轮续:CI 变红的第二个原因

修掉 `.gitignore` 后 CI 推进到基准冒烟步骤才失败:

```
dotnet run --project benchmarks/GameOcrBench/GameOcrBench.csproj -c Release --no-build
Unhandled exception: ...\bin\Release\net10.0-windows\GameOcrBench.exe ... 系统找不到指定的文件
```

| 发现 | 性质 | 根因 | 处置 |
|---|---|---|---|
| `GameOcrBench` 与 `InfiniTranseon.PlatformSpike` 被 `8c1e225` 从 `InfiniTranseon.sln` 中删除(提交说明未提及,应为 IDE 保存解决方案的副作用) | 回归 | 解决方案构建按 `CurrentSolutionConfigurationContents` 为 `ProjectReference` 分配配置;**不在 sln 里的项目拿不到映射,回落到默认的 `Debug`**。于是 `dotnet test -c Release` 经 `Bench.Tests` 把 GameOcrBench 编成了 Debug,而 `dotnet run -c Release --no-build` 去 Release 目录找,找不到 | 两个项目重新加回 sln(各自恢复原有平台映射);未改 CI 命令——去掉 `--no-build` 只会掩盖 sln 的问题 |

**验证:** 清空 `benchmarks/GameOcrBench` 与 `tests/InfiniTranseon.Bench.Tests` 的 `bin`/`obj` 后,按 CI 原样跑 `dotnet restore` → `dotnet test -c Release --no-restore`(784/784)→ Release 产物出现 → `dotnet run --no-build` 冒烟 **exit 0**,`artifacts/game-ocr-bench-smoke.json` 正常写出。

**教训:** 这两条(`.gitignore` 吞源码、sln 掉项目)都属于"本机全绿、别人全红"的同一类——本机磁盘上的既有状态掩盖了仓库内容的缺失。第三轮之前从未有人看过 CI 结果。

## 第五轮:200 用户模拟发现的问题与修复(2026-07-25)

上一轮把"能不能构建、能不能发布"修绿了。本轮问的是**用户能不能把这个程序用起来**:沿着「下载 → 启动 → 建档案 → 选目标 → 配服务商 → 划区域 → 试 OCR → 启动 → 看到译文」逐关做代码级判读,找出会让人卡死的断点。

### 已修(代码验证)

| # | 断点 | 根因 | 处置 |
|---|---|---|---|
| 1 | 向导里按「测试翻译」测的不是用户选的服务商 | `PresentationComposition` 在组合期把 `ITranslationProbe` 写死成 DeepL 的 provider id + 凭据绑定,`SelectedProvider` 完全没有参与 | 新增 `CatalogTranslationProbe`:按 `TranslationProbeRequest.ProviderId` 路由,并逐条检查该服务商声明的**全部**凭据(Yandex/Baidu/Youdao/Alibaba 都是多凭据) |
| 2 | DeepL 只提供 Pro 端点 | `BuiltInProviderDefinitions.DeepL(freeEndpoint: false)`。DeepL API Free 密钥(免信用卡、最容易拿到的一把)在 `api.deepl.com` 上一律 403 | Free 独立成 `translation.deepl-free` 服务商。**凭据引用必须全局唯一**(测试 `CatalogCredentialReferencesAreGloballyUniqueAndBoundToTheirProvider` 当场抓到):共用 `api-key` 槽位会让填入的 Free 密钥直接覆盖已保存的 Pro 密钥 |
| 3 | 错误码原样进 UI(`translation.probe.credentialMissing`) | 636 条 resw 里没有任何一条覆盖探针/服务商错误码 | `ProbeErrorPresenter` **按族**映射(`.authorization` / `.rateLimited` / `http5xx` …),新服务商自动继承正确文案;**机器码始终附在句子后面**,截图仍可诊断 |
| 4 | 工作区就绪清单没有凭据行 | `WorkspaceOverviewPage` 只查目标/语言/区域三项 | 新增凭据行 + `IProfileService.GetTranslationProviderIdsAsync`(含 fallback 与精修步骤引用的服务商);未就绪时启动按钮禁用并指向「服务商」页 |
| 5 | 只配了「扫描剩余区域」的档案报 0 个区域、拒绝启动 | `RealProfileService.ToCard` 的 `RegionCount` 漏掉 `RemainingAreaRegion` | 区域数与通道数都改为包含剩余区域 |
| 6 | 向导第 1 步永远出不了图,第 3 步在空白矩形上划区域,「试跑 OCR」按钮**结构上不可能成功** | 三者都走 `RequestThumbnailAsync`,而它只对**引擎正在捕获**的目标返回像素——新建档案永远不是 | 新增 `IStillFrameProbe` / `StillFrameProbe`:进程内 GDI 取一帧(窗口用 `PrintWindow` + `PW_RENDERFULLCONTENT`,显示器用 `BitBlt` + `CAPTUREBLT`,`HALFTONE` 缩放)。同一帧同时供预览、区域画布底图与 OCR 裁剪 |
| 7 | 无边框捕获不可用时全程零解释 | 结论只写进了 status log | 向导第 1 步(选目标的地方)直接说明三种情形,并说清"仍能工作,只是有黄框" |
| 8 | 身份注册助手 `catch { ExitCode = 1; }` | 吞掉唯一的诊断信息 | 异常写 stderr 后再置退出码 |

**`StillFrameProbe` 的真实验证**(不是代码走读):对当前桌面全量枚举后逐个抓帧 —— 8 个窗口成功 6 个、2 个显示器全部成功,非黑像素占比 62.9%–100%。两个失败的正是文档写明的限制:其中一个是**真实 DirectX 游戏(燕云十六声)**,`PrintWindow` 对它永远返回空帧。这类目标抛 `capture.stillFrame.targetRefusedToRender`,UI 明确告诉用户改用显示器捕获——**黑屏预览和"这程序坏了"在用户眼里没有区别**,所以不允许静默显示空帧。

### 有意不修(附理由)

- **向导里不加「扫描剩余区域」开关。** 该开关在工作区「捕获」页已完整可用,而向导现在能出真图,划区域不再是盲划;把它塞进向导需要把 `RealWorkbenchService` 里那段按模板复制翻译通道、保留 ChannelId/StageId 的逻辑复制一份到 `RealProfileService`,收益不抵重复。
- **未改 `RequestThumbnailAsync` 让它自己回落到静帧。** 那会把两条语义不同的路径(引擎实时帧 / 进程内静帧)混成一条,调用方再也分不清拿到的是什么。页面显式先问引擎、再问静帧探针。

### 仍然挡在 80% 前面、且不是代码能解决的

| 项 | 影响 | 需要谁决定 |
|---|---|---|
| 二进制未签名 | SmartScreen「Windows 已保护你的电脑」,首次运行流失最大的单一来源 | 购买 EV/OV 证书 |
| `build-release.yml` 一次都没跑过 | 没有任何可下载的产物;tag 正则 `^v\d+\.\d+\.\d+$` 还拒绝 `-rc` 后缀 | 推一个 tag |
| Windows 10 直接拒绝启动(要求 build 22621) | 按当前份额约三分之一的潜在用户在第 0 关出局 | 产品决策 |
| 端到端「捕获→OCR→翻译→叠加」从未跑通过一次 | **"这个程序能翻译"仍是未经证实的断言** | 需要真实凭据 + 一次真实运行 |

**因此:"80% 用户能达成目的"在当前状态下无法只靠改代码达成**,上表四项里前三项都在我的权限之外。本轮把**代码层面**会导致卡死的断点清完了;剩下的是发布与验证工作。
