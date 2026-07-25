# 7. 数据与状态架构(支撑 UI 的最小重构)

定位:本章只重构**呈现层**数据流以支撑第 5 章 UI;引擎/Core 层契约不动(`IEngineRuntime`、`ProfileDocument`、探针、凭据均保留)。

## 7.1 状态归属决定表

| 状态类别 | 归属 | 说明 |
|---|---|---|
| 服务器态(SQLite/引擎) | 四个 Store 单例(下节) | 唯一真源,VM 只投影 |
| 导航/URL 态 | 路由参数(profileId、分节名、活动页筛选) | 深链可恢复;筛选态进导航参数,重启不保留 |
| 本地 UI 态 | VM(选中项、展开态、搜索框文本) | 不入 Store |
| 表单/草稿态 | `ProfileWorkspaceStore` 草稿 + 脏标记 | 工作区五节共享;向导复用同模型 |
| 跨页共享态 | Store 事件 | 禁止再有 `App.GlobalHotkeys` 式全局直伸手 |
| 秘密 | 永不入呈现层(现状保留) | 只有引用与"已存"布尔 |

## 7.2 Store 层(新增,替代"每页各自拉取")

四个 UI 线程亲和的可观察 Store 单例(普通 C# 类 + `INotifyPropertyChanged`/typed 事件,不引 WinUI 类型,保持可测):

```text
ProfileStore      档案卡列表缓存 + 变更事件(Saved/Deleted/Imported)
RuntimeStore      引擎状态/暂停/覆盖/运行目标 + 实时指标;订阅 IRuntimeControlService
ProviderStore     提供商行 + 凭据就绪投影;设置只加载一次,失效驱动刷新
ActivityStore     环形缓冲事件流(内存事件总线) + 历史查询门面
SettingsStore     ApplicationSettings 缓存 + 变更事件(并入 ProviderStore 依赖)
```

规则:
- **单次加载 + 失效刷新:** Store 首次访问加载,之后仅在自身 mutation 或引擎事件后失效局部刷新。消灭"设置一页加载 2+ 次""每次导航重列档案"。
- **差量更新集合:** Store 输出带 key 的集合,VM 用 diff(按 Id 增删改)更新 `ObservableCollection`,不再 `Clear()`+重加(保住选中/滚动位置)。
- **呈现模型可观察化:** 列表项模型(ProfileCard、RunningTarget、ProviderRow、HotkeyEditorRow)改为 `ObservableObject` 分部类;一次性查询结果(HistoryEvent 等)保持不可变 record。
- **上帝模型拆分:** `WorkbenchRegionDraft`(33 位置参数)拆为 `RegionGeometry` / `RegionOcrSettings` / `RegionChannels` / `RegionLineLayout` / `RegionOverlayStyle` / `RegionPerformance` 六个子记录的组合;`ProviderRow` 拆出 `LocalModelInfo` 与 `CredentialInfo`。字符串携带的枚举(OverlayMode 等)改为枚举本体,映射层负责序列化。

## 7.3 实时事件通路(修复 P0-3)

```text
IEngineRuntime 事件                     UI
 StatusChanged ──────────┐
 TargetsChanged ─────────┤
 BudgetUpdated ──────────┼─► RealRuntimeControlService
 DiagnosticRaised ───────┤      │ 记 StatusEvent(JSONL, 持久化不变)
 OcrResultReceived ──────┤      ▼
 TranslationOutputReceived──► RuntimeStateStore(准入: 按 generation/channel/stage
                                拒绝过期结果 — 现有实现,首次接线)
                                │ Accepted
                                ▼
                             ActivityStore / RuntimeStore(UI 线程派发)
                                │ typed 事件
                                ▼
                             Home 运行面板指标 · 活动页事件流 · 档案卡状态
```

- `RuntimeStateStore` 从孤儿转正:所有到达 UI 的流式/迟到结果先过准入;被拒结果计数进诊断。
- 诊断页数据源从"磁盘 JSONL 轮询"改为 ActivityStore 推送;JSONL 只作持久化与导出源。`RecoveryAction` 由事件源真实填充(引擎 `DiagnosticRaised` 已携带)。
- 派发统一走 `DispatcherQueue` 注入的 `IUiDispatcher`(替代构造时捕获 `SynchronizationContext` 的脆弱模式,并消除同线程同步重入)。

## 7.4 变更(Mutation)策略

- **命令三态:** 每个引擎交互命令(启停/暂停/覆盖/热应用)在 Store 标记 `Pending → Confirmed | Rolled back`。UI 250ms 内显示 Applying(前端计划硬约束),确认后 Applied,拒绝则恢复持久值并暴露重试(现状为"await 期间界面假死")。
- **保存即失效:** `ProfileStore.SaveAsync` 成功后精确失效该卡;工作区 SaveAndApply 结果(HotApplied/Restarted/SavedOnly)进粘性栏徽标。
- **错误传播:** `RunGuardedAsync` 保留页级兜底,但命令改为返回 `Result`(成功/失败+原因),VM 可编程响应;修复 `DeleteAsync` 绕仓储直写 SQL(仓储补删除 API)。
- **历史正确关联:** `RealHistoryService` 以运行中/指定档案 Id 查询,替换 `profiles.FirstOrDefault()`;"空"与"已禁用"返回可区分结果。

## 7.5 多引擎与作用域(支撑 P0-2)

`IRuntimeControlService` 升级为多会话:`StartAsync(profileId)` 返回 `RuntimeSessionId`;`RunningTarget` 携带会话 Id;暂停/覆盖/手动 OCR 接受可选会话作用域(缺省=全部,对应热键作用域语义)。UI 侧 Home 运行面板按会话渲染多行。`RetranslateCurrent` 与 `ManualOcr` 分离为两个引擎命令(现映射同一实现,需引擎侧补齐;在此之前 UI 禁用并标注"即将推出"——显式可见,不假装成功)。

## 7.6 轮询与实时的边界

| 数据 | 机制 |
|---|---|
| 引擎状态/目标/指标/事件 | 事件推送(7.3) |
| 工作台画布缩略图 | 保留 1s 定时拉取(受控低频,符合隐私边界) |
| 更新检查 | 保留"仅主界面可见且无活动捕获"策略 |
| 历史/术语/设置 | 查询式 + mutation 失效 |

## 7.7 权限/能力对 UI 的投影

`RuntimeCapabilitiesService`(已存在但从未展示)接入:通道上限、区域上限、目标上限在对应编辑器就地显示("已用 2/4");禁用原因本地化短语由服务提供,UI 不自行推导上限(前端计划既有约束,首次落实)。
