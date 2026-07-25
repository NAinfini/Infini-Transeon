# 8. 组件体系

## 8.1 新组件树

```text
AppShell(重构:两态侧栏 + 返回栈 + 面包屑 + 最小窗口/自适应)
├─ TrayHost(新:Shell_NotifyIcon + 隐藏消息窗口 + 状态染色图标 + 菜单)
├─ PageShell(新:页头(标题/副标题/命令区) + 状态插槽(加载/错误/空) + 内容)
│    ├─ EmptyState(新:图标+标题+正文+主 CTA,全应用统一)
│    ├─ ErrorBar(新:InfoBar 封装 + 重试命令 + 详情展开)
│    └─ SkeletonCard(新:卡片骨架屏)
├─ Home
│    ├─ RunningTargetBar(新:会话行 + 行内命令 + 指标)
│    ├─ ProfileCard(重构:缩略图真实化 + 状态徽标 + 行内启动/修复)
│    └─ ActivityListItem(新:严重度徽标 + 时间 + 文案 + 恢复动作)
├─ ProfileWorkspace(新容器:工作区侧栏 + 面包屑 + StickySaveBar)
│    ├─ StickySaveBar(新:脏状态 + 放弃/保存并应用 + Applying/Applied 徽标)
│    ├─ ReadinessChecklist(新:可点击就绪项)
│    ├─ TargetRow(新:目标行 + 展开的目标级设置)
│    ├─ RegionCanvas(提取:自工作台代码后置抽出的独立控件——
│    │   预览/绘制/拖拽/缩放/键盘微移/吸附;向导与 capture 节共用)
│    ├─ RegionListPane(提取:键盘/读屏替代列表 + 增删排序命令栏)
│    ├─ InspectorForm(新:检查器分组容器,声明式字段组)
│    ├─ ChannelPipelineCard(新:初译→润色链行内编辑 + 展开高级项)
│    ├─ BudgetPreviewBar(新:最坏请求/延迟/费用)
│    ├─ OverlayStyleEditor(新:控件列 + OverlayPreviewRenderer 宿主 + 状态切换)
│    ├─ GlossaryTable(重构:行内编辑 + 空态)
│    └─ StylePromptEditor(拆分自 GlossaryPage)
├─ ActivityFeed(新:虚拟化事件流 + 严重度/作用域筛选)
├─ ProviderGroupList / ProviderCard(重构:分组 + 行内端点 + 模型操作)
├─ SettingsShell(新:分节导航 + 设置内搜索 + SettingRow 复用)
└─ 对话框族(统一 DialogService 创建,消灭代码后置手搓)
     ├─ ConfirmDialog(通用确认:删除/放弃/同意)
     ├─ ProviderCredentialDialog(瘦身保留)
     └─ HotkeyCaptureDialog(保留现捕获逻辑,组件化)
```

## 8.2 现有组件处置清单

| 现组件/代码 | 处置 | 说明 |
|---|---|---|
| StatusBadge | **复用** | 修复:图标 12px、`ThemeResource` 绑定使主题切换即时生效 |
| 标题栏 RuntimeStatusPill | **重构** | 加图标(非仅颜色)、自动化名称、点击深链 Home 运行面板 |
| CardBorderStyle / SettingRowStyle / SectionHeaderStyle | **重构** | 字面值改令牌引用(第 9 章) |
| PageViewModelBase | **重构** | 保留守卫模式;命令返回 Result;IsEmpty 必须有绑定 UI(模板强制) |
| 工作台 .xaml.cs(~1100 行) | **拆解删除** | 画布逻辑 → RegionCanvas 控件;对话框 → DialogService;同步逻辑 → Store 差量 |
| 通道编辑 ContentDialog(代码构建) | **删除** | 由 ChannelPipelineCard 行内编辑替代 |
| SetupWizardPage 四步可见性切换结构 | **删除重建** | 引导流复用工作区组件 |
| RunningTargetsPage 全套 | **删除** | 并入 Home |
| GlossaryPage 页级容器 | **删除** | 表格与提示词编辑器组件化后迁入工作区 |
| DiagnosticsPage 静态列表 | **替换** | ActivityFeed |
| OverlayStyles 孤儿资源(`.resw` OverlayStyles*/OverlayMode* 等) | **清理** | 随 overlay 节新文案重建 |
| FakeContentServices / FakeProbes | **复用** | 继续支撑 VM 测试与设计态数据 |
| NavigationMap.EnsureConsistent | **复用扩展** | 覆盖两态侧栏与工作区分节 |

## 8.3 复用边界原则

- 一个模式出现 ≥2 处才抽组件(RegionCanvas、EmptyState、ConfirmDialog 均满足);检查器字段组用 `InspectorForm` 声明式拼装,但**不**造万能表单引擎——字段类型就是 WinUI 原生控件 + 标签/提示/校验插槽。
- 页面骨架(PageShell)强制统一加载/错误/空三态插槽:空状态从"可选善后"变为编译期必填,杜绝再现"IsEmpty 无 UI"缺陷。
- 对话框只经 DialogService 创建(注入 XamlRoot),保证焦点圈定与 Esc 行为一致。
