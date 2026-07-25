# Infini-Transeon UI/UX 全面重设计提案

**日期:** 2026-07-24
**状态:** 已批准(2026-07-24 全部决定确认,见 00-decisions.md;实施按第 10 章迁移计划执行,切换策略为硬切换)
**审查方式:** 源码级审计(全部 XAML/代码后置/ViewModel/服务层),未运行应用。凡无法从源码确定的运行时表现均已在文中标注为假设。
**上游权威输入:** `docs/product/2026-07-19-product-ux-architecture-review.md`(产品规则)、`docs/superpowers/plans/2026-07-19-frontend-implementation-plan.md`(前端约束)。本提案不改变已确认的产品规则,只重构 UI 结构去兑现它们。

## 文档目录

| 章 | 文件 | 内容 |
|---|---|---|
| 0 | [00-decisions.md](00-decisions.md) | **已确认决定记录(权威,覆盖各章 [假设])** |
| 1 | [01-current-audit.md](01-current-audit.md) | 现状审计:屏幕清单、路由图、问题分级 |
| 2 | [02-product-analysis.md](02-product-analysis.md) | 产品与工作流分析:用户、任务、现结构与真实需求的错位 |
| 3 | [03-concepts.md](03-concepts.md) | 三套结构性重设计方案对比 |
| 4 | [04-recommended-direction-and-ia.md](04-recommended-direction-and-ia.md) | 推荐方向 + 新信息架构(路由树、页面去留决定) |
| 5 | [05-page-specs.md](05-page-specs.md) | 逐页规格与文字线框 |
| 6 | [06-user-flows.md](06-user-flows.md) | 核心用户流:现状 vs 新流程、步数削减 |
| 7 | [07-data-state-architecture.md](07-data-state-architecture.md) | 支撑 UI 的数据与状态架构 |
| 8 | [08-component-architecture.md](08-component-architecture.md) | 组件体系:复用/重构/合并/删除清单 |
| 9 | [09-design-system.md](09-design-system.md) | 设计系统:令牌、布局、交互、无障碍规则 |
| 10 | [10-migration-plan.md](10-migration-plan.md) | 九阶段迁移计划 |
| 11 | [11-backlog.md](11-backlog.md) | 优先级排序的实施积压清单 |

## 一句话结论

当前 UI 是"按后端模块切页面"的骨架:六项平铺导航 + 三个导航孤儿页 + 一个 33 控件巨型检查器,且关窗即杀运行时、引擎实时事件从未到达 UI。重设计将其重组为**"启动台(Home)+ 档案工作区(Profile Workspace)+ 全局维护区"**的实体优先结构:高频动作(启动/暂停)一步可达,深度配置收进按任务分节的档案工作区,托盘成为游戏期间的唯一常驻表面。

## 三条不可违背的产品红线(重申)

1. 游戏运行期间零模态、零抢焦点、零逐句选择。
2. 每区域 1–4 条翻译通道,结果固定槽位自动并行显示;通道最多 2 个显式二次润色步骤。
3. 高级自由度收进工作台;首次设置只要求最小可运行路径。
