# ADR-001：放弃 Python 跨平台代码库，Windows-first 原生重写

**日期：** 2026-07-20
**状态：** 已接受
**决策者：** 项目所有者

## 背景

v0.1.0（git tag `v0.1.0`，commit `e7a77cf`）是 Python 3.12 跨平台骨架：Qt 覆盖层、RapidOCR PP-OCRv5（ONNX Runtime）、MADLAD-400 + CTranslate2 本地翻译、mss/平台特定捕获、PyInstaller 打包，声明支持 Windows/macOS/Linux。

实际暴露的结构性问题（部分见 v0.1.0 提交记录）：

1. **打包脆弱且巨大。** PyInstaller 冻结包的 CUDA DLL 解析多次出问题（cublas 定位失败导致静默回退 CPU，需手工干预 `sys._MEIPASS` 搜索路径）；带 CUDA 的便携包达 2.0 GB，且 cublasLt 等依赖无法裁剪。
2. **覆盖层质量达不到沉浸感合同。** 点击穿透、捕获排除（`WDA_EXCLUDEFROMCAPTURE`）、不激活、不进 Alt+Tab、DirectComposition 级渲染延迟，这些在 Qt 抽象层上要么绕过框架直接调 Win32，要么根本做不到可靠。
3. **性能架构受限。** GIL 约束多目标流水线并发；无法精细控制 D3D 纹理生命周期、fence 与 readback，新架构的有界资源池/admission control 在 Python 里无法诚实实现。
4. **跨平台承诺没有兑现路径。** macOS（ScreenCaptureKit、辅助功能权限）与 Wayland（受限捕获协议）各自需要大量平台专属工作，三平台并行只会让每个平台都做不好。

## 决策

1. **全量重写**：C#/.NET 10 WinUI 3 App + C++20 EngineHost（+ 可选 ModelWorker），按 `docs/superpowers/specs/2026-07-19-runtime-architecture-design.md` 执行。
2. **v1 仅支持 Windows**（初始定为 Windows 10 19041 技术基线 / Windows 11 主动支持；平台下限其后由 ADR-002 提升为仅 Windows 11）。macOS/Linux 无限期推迟，且在其立项前不做任何对外承诺。
3. **Python 代码库冻结于 tag `v0.1.0`**：仅作算法与参数参考（OCR 预处理、分段、术语表、资源治理器阈值），不再维护、不再发布；main 分支归新实现所有。
4. 模型层资产（PP-OCRv5、MADLAD-400）沿用旧验证结论，选型评审见 `docs/superpowers/specs/2026-07-20-model-selection-review.md`。

## 已考虑的备选方案

| 方案 | 结论 | 原因 |
|---|---|---|
| 继续演进 Python 代码库 | 否 | 上述四点为结构性问题，修补成本高于重写 |
| Electron / WebView 外壳 + 原生捕获 | 否 | 覆盖层性能与点击穿透不可靠，基础内存远超 400 MB 目标 |
| Rust 或纯 C++ 全栈 | 否 | UI/无障碍/本地化生态弱于 WinUI，v1 交付风险更高 |
| 保留跨平台抽象、只先发 Windows | 否 | 为不存在的平台付抽象税；架构规格已按 Windows 专属 API 深度设计 |

## 后果

**正面：** 覆盖层与捕获走原生路径，沉浸感合同可实现；进程/信任边界清晰（App / EngineHost / ModelWorker）；打包体积与更新链可控；性能预算可诚实测量。

**负面与代价：**
- 平台锁定：macOS/Linux 用户流失；README 与仓库描述需在新结构落地时同步修正平台承诺（当前 v0.1.0 README 仍写跨平台）。
- 技能面：同时要求 C#/WinUI 与 C++/D3D 能力。
- 旧版本无升级路径：v0.1.0 为 pre-alpha，无档案迁移义务，可接受。
- 全部功能重新实现，无渐进迁移可言——由 `docs/superpowers/plans/2026-07-20-milestones.md` 的里程碑管理该风险。

## 关联文档

- 产品与体验：`docs/product/2026-07-19-product-ux-architecture-review.md`
- 运行时架构：`docs/superpowers/specs/2026-07-19-runtime-architecture-design.md`
- 实施计划：`docs/superpowers/plans/2026-07-19-backend-implementation-plan.md`、`docs/superpowers/plans/2026-07-19-frontend-implementation-plan.md`
