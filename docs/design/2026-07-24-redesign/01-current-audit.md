# 1. 现状审计

审查方式:对 `src/InfiniTranseon.App/` 全部 XAML、代码后置、ViewModel、Presentation 服务与组合根做源码级通读(未运行应用)。运行时渲染效果、真实响应式表现、实际数据状态无法直接观察,涉及处均标注 **[假设]**。

## 1.1 屏幕清单(实际审查范围)

| 屏幕 | 文件 | 在导航中 | 审查深度 |
|---|---|---|---|
| AppShell(导航壳) | `Shell/AppShell.xaml(.cs)` | — | 全量 |
| 档案中心 ProfileCenter | `Features/ProfileCenter/` | ✅ 首项默认 | 全量 |
| 运行目标 RunningTargets | `Features/RuntimeControls/` | ✅ | 全量 |
| 历史 History | `Features/History/` | ✅ | 全量 |
| 服务与模型 ServicesModels | `Features/Settings/ServicesModelsPage.*` | ✅(页脚) | 全量 |
| 诊断 Diagnostics | `Features/Diagnostics/` | ✅(页脚) | 全量 |
| 设置 Settings | `Features/Settings/SettingsPage.*` | ✅(页脚) | 全量 |
| 设置向导 SetupWizard | `Features/SetupWizard/` | ❌ 孤儿(仅经"新建档案"进入) | 全量 |
| 光学工作台 OpticalWorkbench | `Features/Workbench/` | ❌ 孤儿(仅经档案卡"编辑"进入) | 全量 |
| 术语表 Glossary | `Features/Glossary/` | ❌ 孤儿(仅经档案卡溢出菜单进入) | 全量 |
| 覆盖样式 OverlayStyles | HEAD 存在、工作树已删除 | ❌ 已删 | 经 git HEAD 审查 |
| 凭据对话框 ProviderCredentialDialog | `Features/Settings/ProviderCredentialDialog.cs` | —(模态) | 全量 |
| 启动失败窗口 CompositionErrorWindow | `Shell/CompositionErrorWindow.xaml` | — | 全量 |
| 通道编辑对话框 | `OpticalWorkbenchPage.xaml.cs` 内代码构建 | —(模态) | 全量 |
| 主题/令牌/控件样式 | `Theme/DesignTokens.xaml`、`ControlStyles.xaml` | — | 全量 |
| 无法审查 | 运行中的覆盖层渲染(EngineHost 原生侧)、托盘(不存在)、真实缩放/高对比度表现 | | **[未覆盖]** |

## 1.2 当前路由图

```text
AppShell (NavigationView, Left, 220px, 无返回按钮, 无深链)
├─ [主导航]
│  ├─ profiles  → ProfileCenterPage(默认)
│  │    ├─ "New profile" → SetupWizardPage(孤儿页)
│  │    │     └─ "Save and start test" → RunningTargetsPage(携带 profileId 自动启动)
│  │    ├─ 卡片 "Edit" → OpticalWorkbenchPage(孤儿页)
│  │    ├─ 溢出菜单 → GlossaryPage(孤儿页)/ 导出 / 删除
│  │    └─ 主按钮 → RunningTargetsPage
│  ├─ running   → RunningTargetsPage
│  └─ history   → HistoryPage
└─ [页脚]
   ├─ services    → ServicesModelsPage
   ├─ diagnostics → DiagnosticsPage
   └─ settings    → SettingsPage
```

Frame 历史无限累积但永不回退(返回按钮 Collapsed、无 `GoBack` 调用);孤儿页进入后只能靠再点导航项离开,导航高亮与当前页面脱节(导航仍停在 profiles 项,内容已是向导/工作台)。

## 1.3 功能清单与用户角色

单用户桌面应用,无多角色。等效"权限维度"为:捕获授权状态(package identity / `graphicsCaptureWithoutBorder`)、严格离线模式、凭据是否存在、包身份是否可用。当前 UI 只在启动日志和错误文本中体现,无一等公民的权限状态呈现。

功能域:档案 CRUD 与导入导出、四步向导、区域绘制/检查器编辑、翻译通道(≤4/区域,≤2 润色)、覆盖样式、术语表 + 风格提示词版本、运行时启停/暂停/覆盖显隐/手动 OCR、全局热键、历史、诊断导出与崩溃目录、提供商凭据与端点、本地模型安装/移除、REST 适配器导入、更新检查/下载、主题/语言/性能预设/历史保留/离线模式。

## 1.4 问题清单(按严重度)

### P0 — 结构性缺陷(与产品红线冲突)

1. **无托盘、关窗即杀运行时。** `App.xaml.cs` 的 `shell.Closed` 直接 `runtime.StopAsync()` 并销毁 DI 容器;产品文档 §3.2 要求"主窗口可以关闭到托盘;运行时不要求保持设置窗口可见"。当前用户必须保持设置窗口打开才能玩游戏。
2. **单引擎限制暴露在 UI 模型上。** `RealRuntimeControlService.StartAsync` 在已运行时抛 `alreadyRunning`;RunningTargets 页只有一个全局档案 ComboBox + 全局启停。产品 P0 要求多窗口/多显示器/桌面固定区域同时运行。UI 的"运行目标列表卡片"展示的多目标只是同一档案内的目标,无逐目标控制。
3. **引擎实时事件从未到达 UI。** `RealRuntimeControlService` 不订阅 `OcrResultReceived`/`TranslationOutputReceived`;`RuntimeStateStore`(过期结果准入机制)已注册 DI 但零调用方。`RunningTarget.LatencyP95` 硬编码 `"—"`。"运行中"页面无法展示任何正在发生的翻译。
4. **三个孤儿页面破坏导航模型。** 向导/工作台/术语表不在导航与 `NavigationMap.Destinations` 中,进入后导航高亮错位、无返回路径;`.resw` 中却存有 `NavGlossary`、`NavWorkbench`、`NavOverlayStyles` 标签(未使用)。

### P1 — 重大 UX 缺陷

5. **工作台检查器过载。** 320px(紧凑 280px)右栏塞 6 个 Expander 共约 33 个控件;"性能与降级"区混杂区域级(识别间隔)与目标级(检测长边、剩余区域扫描)作用域;通道编辑是代码后置构建的模态 `ContentDialog`(三个 ComboBox);云 OCR 同意、放弃修改确认各自独立模态——同页三条模态链。
6. **向导无逐步门禁。** Next 恒可用,可空手走到第 4 步才由只读"就绪清单"揭示未完成项;步骤指示器外观可点实不可点;第 3 步添加区域仅有名称/优先级/上下文角色,**无几何绘制**——区域坐标全靠默认值,与产品 §3.1"用户绘制一个或多个区域"不符;"测试"实为直接启动真实运行时,无逐步探测(OCR 试跑、延迟测试、覆盖预览均缺失)。
7. **设置碎片化为三个表面。** SettingsPage(外观/隐私/性能/热键/关于+更新共 6 节单列长滚动)、ServicesModelsPage(提供商/模型)、凭据对话框(端点编辑藏在其中)。"离线模式"开关在 SettingsPage,却静默禁用 ServicesModels 的模型安装与关于区的更新,后者无任何提示。
8. **历史/术语表功能残缺。** 历史无"保存人工修正"(产品 §3.4 要求)、无日期分组、保留策略控件在设置页;术语表无行内编辑(只有添加-覆盖 + 删除)、作用域硬编码、与风格提示词编辑器挤在同页;有档案但零词条时**无空状态提示**。
9. **档案卡缩略图恒为占位符**;运行状态不在档案卡上(需切页查看)。

### P2 — 设计系统与一致性

10. 令牌缺口:无阴影/动效/强调色/中性表面令牌;`ThumbPlaceholderBrush` 等 5 个画刷浅色模式沿用深色值(浅色主题渲染错误);`ControlStyles.xaml` 与 AppShell 大量字面值(`Padding="16,12"`、状态胶囊 `CornerRadius="12"` vs 令牌 10);StatusBadge 图标 11px 低于自设 12epx 下限。
11. 无窗口最小尺寸、`PaneDisplayMode="Left"` 固定不随宽度收缩;仅工作台自带三档自适应(≥1120/≥820/窄),全应用无统一断点。
12. 无障碍不均:凭据对话框显式设置了自动化名称,页面级图标按钮依赖 `x:Uid` 待验证;运行状态胶囊仅靠颜色+小圆点区分状态(无图标);语言 ComboBox 项硬编码字面量("English (US)"、"简体中文")未走资源;ToggleSwitch On/Off 内容为空。
13. 状态缺失矩阵:工作台 `IsEmpty` 无对应 UI;ServicesModels 空提供商无空状态;诊断/历史一次性加载后不刷新(陈旧数据);所有切换无乐观/进行中状态(违背前端计划"250ms 内显示 Applying"约束)。

### P3 — 数据层(影响 UI 可实现性,详见第 7 章)

14. 除 `HotkeyEditorRow` 外所有呈现模型无变更通知,靠 `Clear()`+重加 刷新列表(丢失选中/滚动位置);`WorkbenchRegionDraft` 33 个位置参数的上帝模型;设置单次页面渲染重复加载 2+ 次;诊断经磁盘 JSONL 往返;`RealProfileService.DeleteAsync` 绕过仓储直写 SQL;`ManualOcr` 与 `RetranslateCurrent` 热键映射同一实现。

## 1.5 现状可取之处(重设计中保留)

- `NavigationMap.EnsureConsistent` 启动快速失败;组合失败走恢复窗口不假装成功。
- StatusBadge 图标+文字双通道、自动化名称随文本更新。
- 工作台"区域列表 = 画布的完整键盘/读屏替代"这一设计意图;Ctrl+S/Z/Y、方向键微移。
- 凭据只写 OS 凭据库、内存即时清除;导入导出的大小/条目数上限校验。
- `.resw` 双语覆盖完整(en-US/zh-CN),仅个别硬编码漏网。
- 工作台保存时"加载旧文档合并"策略,元数据保存不会重置已校准区域。
