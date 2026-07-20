# ADR-002：最低支持平台提升至 Windows 11

**日期：** 2026-07-20
**状态：** 已接受
**决策者：** 项目所有者
**取代：** ADR-001 中"Windows 10 19041 技术基线"的平台条款

## 背景

ADR-001 与四份规划文档最初把 Windows 10 build 19041 定为技术 API 基线、Windows 10 22H2/ESU 定为技术兼容矩阵。审查发现维持该基线的实际成本：

1. **WGC 捕获边框。** `GraphicsCaptureSession.IsBorderRequired` 在 Windows 10 19041 上不可用，系统捕获黄框在该基线上无法关闭，直接冲击沉浸感合同，且只能"接受并告知"，无法修复。
2. **双矩阵测试成本。** Win10 需要独立的测试环境（VM/实机）、纯色材质回退路径、DWM 行为差异矩阵和独立的发布门槛项。
3. **生命周期现实。** Windows 10 已于 2025-10 结束主流支持，处于付费 ESU 期；对 2026 年立项、开发周期以年计的新产品，发布时 Win10 存量将进一步收缩。

## 决策

1. v1 最低支持平台为 **Windows 11 x64**，API 基线 **build 22621（22H2）**；发布测试矩阵覆盖当前仍受微软支持的 Windows 11 版本。
2. **不支持 Windows 10**。安装器与便携版启动时必须检测并明确拒绝不受支持的系统版本，给出本地化提示，不得静默降级运行。
3. 捕获边框策略随之确定：通过 `IsBorderRequired = false` 关闭边框，前置 `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` 一次性用户授权；拒绝授权时边框保留并在 UI 如实告知。`graphicsCaptureWithoutBorder` 属于受限能力，安装版与便携版都必须先建立 package identity 并在 manifest 中声明该能力。授权流程、拒绝行为、重启持久性以及便携版身份的注册、升级、移动目录与清理生命周期由后端 Task 0 spike 实测落档。
4. 较新的 Windows 11 API（高于 22621 基线的能力）仍必须运行时检测，不得静态假设。
5. 便携版 package identity 是发布前置条件而非可选优化；若普通用户权限下不能可靠建立该身份，必须修订便携分发承诺，不得静默降级为带捕获边框的“便携版”。部署身份不得渗透到 Core、Contracts 或 Engine.Native 的业务合同。

## 影响范围（已同步修订）

- 产品文档：§5 沉浸感合同第 10 条、§7.1 推荐栈、§10 验收场景 10。
- 运行时架构规格：目标句、§1 已确认范围、§15 实施映射。
- 后端计划：全局约束、Task 0（边框授权与两种分发物 package identity 生命周期验证）、Task 1（部署身份隔离与 manifest 检查）、Task 6（移除 Win10 承诺表述）、Task 15（安装器拒绝不受支持系统）、Task 16（实验室矩阵）。
- 前端计划：目标句、全局约束、Task 0/1/2/14 的 Win10 回退与测试项改为"合成材质不可用时的纯色回退"（远程会话、禁用透明效果）。
- 里程碑：M0 退出标准更新为边框授权流程实测。

## 后果

**正面：** 消除不可修复的黄框问题；删除整条 Win10 测试矩阵与材质回退分支；开发无需 Win10 环境；API 基线上移后运行时检测面缩小。

**负面与代价：**
- 放弃仍在 Windows 10 上的游戏玩家群体（该群体持续缩小但非零）；README/发布页需明确标注系统要求，安装被拒的用户会产生支持咨询。
- 纯色回退路径并未完全消失——远程会话与禁用透明效果的场景仍需保留该分支，只是不再与操作系统版本绑定。
- 便携版不再等同于“完全无注册痕迹”：为获得受限捕获能力，首次运行可能需要为当前用户建立可清理的 package identity；具体用户提示与移除方式由 Task 0 spike 结论确定。

## 关联文档

- `docs/adr/2026-07-20-adr-001-windows-first-native-rewrite.md`
- `docs/product/2026-07-19-product-ux-architecture-review.md`
- `docs/superpowers/specs/2026-07-19-runtime-architecture-design.md`
