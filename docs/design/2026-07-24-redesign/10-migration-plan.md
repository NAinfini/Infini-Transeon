# 10. 迁移计划(九阶段)

原则:每阶段可独立合并、可运行、测试绿;按 Epic 逐块硬切换,不设置新旧 UI 功能开关;替代物通过验收并合并时立即删除对应旧页、旧路由和死资源。

## 阶段 1 — 基础与设计系统

- **范围:** 令牌增补与修复(表面/强调/阴影/动效/字体/布局令牌;浅色画刷修复;字面值改令牌)、StatusBadge 修复(12px、ThemeResource)、PageShell/EmptyState/ErrorBar/SkeletonCard/ConfirmDialog/DialogService 组件、窗口最小尺寸。
- **依赖:** 无。
- **风险:** 令牌改动波及全部现页面视觉回归——用截图比对测试兜底。
- **影响文件:** `Theme/DesignTokens.xaml`、`Theme/ControlStyles.xaml`、`Controls/*`(新增)、`Shell/AppShell.xaml.cs`(AppWindow 尺寸)。
- **测试:** 令牌解析单测;浅/深/高对比三主题下新组件 UI 自动化冒烟;200% 缩放无裁剪断言。
- **完成定义:** 全部现页面在新令牌下无视觉回归;新组件库可被任意页面引用;浅色主题画刷缺陷关闭。

## 阶段 2 — 应用外壳、导航与托盘

- **范围:** 两态侧栏(全局态/工作区态)+ 面包屑 + 返回栈;NavigationMap 扩展覆盖工作区分节;深链协议与命令行参数;TrayHost(托盘图标/菜单/关窗托盘化 + 首次询问);运行状态胶囊重构(图标+深链)。
- **依赖:** 阶段 1;托盘依赖平台 spike 结论(`docs/architecture/platform-spike-results.md`,已有)。
- **风险:** 关窗语义变更(退出→托盘化)影响引擎生命周期与 DI 销毁时序——`shell.Closed` 处理器需重写为隐藏而非销毁;托盘菜单不得激活前台。
- **影响文件:** `Shell/*`、`App.xaml.cs`、`Program.cs`、新 `Tray/*`。
- **测试:** NavigationMap 一致性单测扩展;托盘化后引擎存活集成测试;焦点保持(FlaUI)断言托盘操作不夺前台。
- **完成定义:** 关窗后引擎继续运行且托盘可控;深链 `--profile {id} --start` 可用;新外壳验收通过后直接替换旧外壳,不保留切换开关。

## 阶段 3 — 数据/状态层(UI 支撑)

- **范围:** 四 Store 落地;`RuntimeStateStore` 接线(订阅 OcrResult/TranslationOutput 经准入入 Store);`IUiDispatcher`;差量集合更新;命令三态(Pending/Confirmed/RolledBack);模型可观察化与上帝模型拆分;`RealHistoryService` 档案关联修复;`DeleteAsync` 仓储化。
- **依赖:** 阶段 1(可与 2 并行)。
- **风险:** 引擎事件量级下 UI 线程派发节流(合帧 ≤10Hz);拆模型触碰工作台映射层,回归面大。
- **影响文件:** `State/*`、`Presentation/Services/Real*`、`Presentation/PresentationModels.cs`、`Core/Profiles` 仓储删除 API。
- **测试:** Store 单测(失效/差量/准入拒绝计数);既有 VM 测试改造;陈旧结果注入测试(迟到 TranslationOutput 被拒)。
- **完成定义:** 活动流在测试引擎下实时到达 UI;设置单页渲染仅一次加载;列表刷新保持选中。

## 阶段 4 — 核心域工作流:Home 与档案工作区骨架

- **范围:** Home(档案网格+运行面板+活动摘要)、ProfileWorkspace 容器(侧栏/面包屑/StickySaveBar/脏模型)、概览节(就绪清单/目标行/目标级设置归位)、真实缩略图。
- **依赖:** 阶段 2、3。
- **风险:** Home 与工作区概览信息重叠——以"Home 只读聚合、概览可操作"为界;多引擎(7.5)若引擎侧未就绪,运行面板先按单会话渲染,UI 模型即按多会话建。
- **影响文件:** 新 `Features/Home/*`、`Features/Workspace/*`;新页面验收并合并时删除被替代的 `ProfileCenterPage`/`RunningTargetsPage`。
- **测试:** 就绪清单深链跳转;启动失败落卡行内;缩略图缓存回退链(运行→缓存→图标)。
- **完成定义:** 启动/停止/暂停、进入工作区全部经新 UI 完成;被替代的旧入口、路由与页面已删除。

## 阶段 5 — 复杂表单:捕获画布、通道、覆盖、语言语境、新建引导流

- **范围:** RegionCanvas/RegionListPane 提取;capture 节(减负检查器);ChannelPipelineCard + BudgetPreviewBar;OverlayStyleEditor(恢复字号/描边/对比 + 溢出策略 + 状态可切预览);language 节(术语行内编辑/空态/风格提示词拆卡);`/profiles/new` 引导流(门禁/逐步实测/草稿)。
- **依赖:** 阶段 4。
- **风险:** 本阶段体量最大,按节拆 PR(capture → channels → overlay → language → new);画布提取是 1100 行代码后置的解剖手术,先建控件级 UI 测试再迁移。
- **影响文件:** `Features/Workbench/*`(拆解)、`Features/SetupWizard/*`(重建)、新 `Controls/RegionCanvas*`。
- **测试:** 画布键盘等价路径 UI 自动化;通道上限/预算预览单测;向导每步门禁与草稿续跑;覆盖预览六状态渲染。
- **完成定义:** 工作台/向导旧页的全部能力在新分节可用且补齐丢失控件;通道编辑无模态。

## 阶段 6 — 次级页面:活动、提供商、设置

- **范围:** ActivityFeed(实时流/筛选/跨档案历史/报告);历史"保存修正"(含入术语表);providers 分组+行内端点+离线横幅;settings 分节导航+搜索+热键作用域列+语言下拉资源化。
- **依赖:** 阶段 3(活动流)、阶段 4(工作区历史节位)。
- **风险:** 修正语义属新需求([假设] 已标注),先与产品确认再实现入术语表分支。
- **测试:** 事件流虚拟化性能(1k 事件滚动);修正保存作用域;设置搜索命中高亮。
- **完成定义:** 诊断/历史/服务/设置四个旧页面的全部能力在新页可用。

## 阶段 7 — 响应式与无障碍收口

- **范围:** 三断点全页面走查(Wide/Compact/Narrow);工作区三栏堆叠;紧凑密度;9.5 节无障碍规则全量落实(自动化名称清点、ToggleSwitch 内容、状态三通道、Esc 语义)。
- **依赖:** 阶段 4–6。
- **测试:** 每页三断点 × 三主题截图矩阵;FlaUI 键盘全流程(建档→启动→修正);读屏冒烟(Narrator 手测清单)。
- **完成定义:** 前端计划的无障碍发布门槛全绿。

## 阶段 8 — 全局清理与一致性核验

- **范围:** 核对阶段 2–6 已随各 Epic 删除的旧页面、路由与资源;清理跨 Epic 才能确认的孤儿 `.resw`(OverlayStyles*/NavWorkbench 等)与死代码(旧通道对话框、`App.GlobalHotkeys` 直伸手路径);确认仓库不存在新旧 UI 切换开关。
- **依赖:** 阶段 7 完成。
- **风险:** 跨 Epic 资源键误删——以编译期资源引用扫描兜底。
- **完成定义:** 无不可达页面/资源;`NavigationMap.EnsureConsistent` 覆盖全部现存目的地。

## 阶段 9 — 测试与稳定化

- **范围:** 产品文档 §10 十二条端到端验收场景中 UI 相关项全量执行;性能预算(启动 <2s 至可交互、活动流帧率、画布拖拽 60fps [假设:候选门槛]);缺陷冻结与回归。
- **完成定义:** 验收场景通过;两个语言包全量走查;发布检查单更新(`docs/testing/backend-release-checklist.md` 增补 UI 项)。
