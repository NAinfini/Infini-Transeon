# 9. 设计系统与视觉方向

## 9.1 视觉方向候选(三选一)

| 方向 | 情绪 | 表面处理 | 密度 | 取舍 |
|---|---|---|---|---|
| **V1 Fluent 原生沉稳(推荐)** | 安静、工具感、与 Win11 同族 | Mica 底 + 卡片层;圆角 8;细描边 | 中密度,可切紧凑 | 差异化弱,但零违和、无障碍现成、实现最快 |
| V2 游戏工作台暗调 | 电竞感、深色优先 | 深色恒定 + 霓虹强调色 | 高密度 | 与"跟随系统/高对比度"红线冲突大,浅色模式二等公民,否决 |
| V3 极简单色编辑器 | 纸面感、大留白 | 无卡片,分隔线定界 | 低密度 | 对状态徽标密集的监控型内容(活动流/通道卡)层级不足,否决 |

**选定 V1**,理由:本产品的视觉主角是**游戏画面里的覆盖层**,应用窗口应当退后;Fluent 原生最大化利用系统控件的键盘/读屏/高对比度行为,把预算留给结构重构。差异化通过**状态色语义系统与覆盖预览的精致度**体现,不靠装饰。

V1 细则:图标 Segoe Fluent Icons;数据可视化(延迟/预算)用单色条形+语义色点缀,不引图表库;表单=SettingRow 式左标签右控件;表格=卡内网格(沿现术语表模式,加斑马纹与吸顶表头);动效仅透明度/位移 ≤150ms,尊重减少动画(现有红线)。

## 9.2 令牌(在现 DesignTokens.xaml 上增补/修复)

```text
保留:SpaceXS/S/M/L/XL/XXL(4/8/12/16/24/32)、TypeCaption..TypeTitle(12/14/16/20/28)、
     CardCornerRadius 8、ControlCorner 4、BadgeCornerRadius 10、MinHitTarget 32、五档语义状态色
新增:
  表面: SurfaceBackground / SurfaceCard / SurfaceCardHover / SurfaceSunken(映射系统画刷,集中引用点)
  强调: AccentDefault / AccentText(封装系统强调色,禁止页面直引系统 Brush 键)
  阴影: ElevationCard(2) / ElevationDialog(8) — ThemeShadow 档位
  动效: MotionFast 100ms / MotionNormal 150ms / EasingStandard;ReducedMotion 时归零
  字体: FontMono("Cascadia Mono",唯一引用点)/ FontWeightStrong(SemiBold)
  布局: ContentMaxWidth 1200 / FormMaxWidth 820 / PaneWidthWorkspaceNav 220 / PaneWidthInspector 360
修复:
  ThumbPlaceholder/Letterbox/OverlayPreview* 五画刷补浅色主题值
  StatusBadge 图标 11 → 12;状态胶囊 CornerRadius 12 → BadgeCornerRadius
  ControlStyles 全部字面 Padding/Margin 改令牌引用
```

## 9.3 布局与断点

- 窗口最小 960×600(AppWindow 强制);默认 1280×800。
- 全局断点(与工作台现三档统一):Wide ≥1120 / Compact 820–1119 / Narrow <820。
- 栅格:内容区 `ContentMaxWidth` 居中;卡网格 UniformGrid MinItemWidth 320,间距 SpaceL。
- 密度:Comfortable 默认;Compact(行高 -25%、CardPadding 16→12)为设置项,长表格页(术语/热键/活动)自动继承。

## 9.4 交互状态规则

- 焦点:全部交互元素可见焦点环(系统 FocusVisual,不自绘);对话框焦点圈定;工作区切节保持焦点入分节标题。
- 悬停/按下:仅系统 Reveal 行为;卡片可点击区悬停升 `SurfaceCardHover`。
- 禁用:必附原因(tooltip 或行内文案),来源于能力服务本地化短语——"禁用但不解释"视为缺陷。
- 选中:列表选中 = 强调色左条 + 背景,不只变色(高对比度可辨)。
- 破坏性动作:删除类按钮 Critical 前景;二段确认(ConfirmDialog);可逆操作(区域删除)优先 Undo 而非确认。

## 9.5 无障碍与国际化规则(发布门槛)

1. 全部交互控件:AutomationProperties.Name(显式或经 x:Uid),图标按钮零例外;ToggleSwitch 提供 On/Off 内容。
2. 状态三通道:图标 + 文本 + 颜色(RuntimeStatusPill 补图标;活动流严重度同此)。
3. 键盘:Tab 顺序 = 视觉顺序;画布全部操作有列表/数字框等价路径(保留现设计);Esc 语义统一(取消绘制/关对话框/退出工作区带脏检查)。
4. 文本缩放 200% 与 1280×720 下无裁剪(UI 自动化测试断言,沿前端计划);高对比度沿系统色。
5. 全部字符串入 `.resw`(修复语言 ComboBox 硬编码、CompositionErrorWindow 标题、"·"分隔拼接改格式化资源);长译文文案(de/ru)预留 1.4× 宽度。
6. 减少动画:动效令牌归零 + 骨架屏改静态。
7. 触控目标 ≥32epx(紧凑)/40epx(舒适)。
