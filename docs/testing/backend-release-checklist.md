# 后端发布检查清单

所有条目必须附机器可读报告或人工证据；`未运行`、`受阻`不得改写为通过。

## 自动化阻断项

- [ ] `dotnet test` Debug/Release 全部通过。
- [x] CMake Debug/Release、CTest 和 ASan 配置全部通过。
- [ ] SBOM 生成成功，未知/不兼容许可证为零；应用与可选模型许可证分开报告。
- [ ] 严格离线网络拒绝覆盖完整进程树，DNS/TCP 尝试为零。
- [ ] provider delta 连续性、唯一终态、取消、429/5xx、重定向、慢响应、超限 SSE 全部通过。
- [ ] 两目标 4K 30 分钟 soak：有界队列、公平调度、内存稳定、无旧 overlay generation、无焦点窃取。
- [ ] 真实提交 CPU/GPU 字节不超过当前容量合同；设备重置新旧 epoch 共存峰值也计入。
- [ ] 安装器和便携包均验证 canonical manifest、Ed25519、SHA-256；MSI 额外验证 Authenticode 发布者。
- [ ] Windows build 22621 以下安装被明确拒绝。

## OCR 质量与性能校准

- [ ] PP-OCRv5 mobile det/rec/cls 的 ONNX 来源、opset、输入形状和许可证已冻结。
- [ ] 简中、繁中、日文、韩文、英文及常见拉丁 fixture 已运行。
- [ ] 竖排日文支持范围已有实测结论。
- [ ] 清晰字幕 CER ≤1%、行准确率 ≥98%。
- [ ] 描边/阴影 CER ≤3%、行准确率 ≥95%。
- [ ] 物理高度 ≤16px 小字 CER ≤5%、行准确率 ≥90%。
- [ ] 全集漏检 ≤2%、误检 ≤3%。
- [ ] 单裁剪 CPU OCR P95 ≤80ms；1920 长边检测面 P95 ≤150ms。
- [ ] 未缓存 P0 本地管线（检测至 provider dispatch，不含网络）P95 ≤300ms。

## 交互式 Win11 GPU/DWM 实验室

- [ ] borderless capture 授权同意、拒绝、重启持久化、撤销全部实测。
- [ ] `WDA_EXCLUDEFROMCAPTURE` 对窗口/显示器捕获均实测；失败时 overlay 明确停用。
- [ ] 4K、300% DPI、混合 DPI/HDR、多显示器、设备移除与恢复。
- [ ] 窗口化/无边框目标移动、缩放、最小化、虚拟桌面和关闭。
- [ ] 全局热键不抢焦点；overlay 非激活、穿透、Alt+Tab/任务栏隐藏。
- [ ] 两个真实适配器与多 GPU 组合完成。

## 本地模型（仅用户主动安装时）

- [ ] CTranslate2 与 ONNX 导出在无网络 AppContainer/LPAC 沙箱内完成同精度基准并裁决。
- [ ] ModelWorker 的 CPU-first INT8、内存/进程 Job 限制进入容量合同。
- [ ] AppContainer 内 DNS/TCP 实际拒绝；仅 bootstrap read handle 被继承。
- [ ] MADLAD-400 3B/7B 权重、SentencePiece 与运行时许可证独立审计。
- [ ] 缺失、取消、磁盘满、哈希失败、目录回滚、worker 崩溃/重启与旧 epoch 全部通过。

## 当前已知外部门槛

- Task 0 真实硬件/授权矩阵尚未运行。
- OCR 模型权重按产品要求不自动下载，真实质量门槛必须在用户/实验室显式安装后运行。
- 本地翻译运行时裁决与 AppContainer 实测尚未完成时，不得宣称本地翻译可发布。
- 没有前端 `InfiniTranseon.App` 项目时，release workflow 必须明确失败，不生成残缺安装包。

## 2026-07-21 本地自动化证据

- 托管 Debug 与 Release 各发现 328 项：327 通过、0 失败、1 项动态跳过。跳过项为真实 WGC 主显示器捕获，本机服务返回 `0x80070424`；因此上方 `dotnet test` 阻断项仍未勾选。
- 原生 Debug、Release 与 ASan `RelWithDebInfo` 各 13/13 通过；ASan 目标目录包含 `clang_rt.asan_dynamic-x86_64.dll`，普通 Debug/Release 不依赖该运行库。
- `GameOcrBench` 确定性 smoke：2 个样本，CER 0、行准确率 1、漏检率 0、误检率 0、本地管线 P95 48 ms、CPU 峰值 128 MiB、GPU 峰值 64 MiB。该结果仅验证报告合同，不替代真实模型质量或 4K 实验室校准。
- `git diff --check` 无空白错误；仅有工作树行尾转换提示。
- 当前沙箱无法读取用户级 NuGet 配置，`dotnet tool restore` 因访问被拒而无法生成 CycloneDX SBOM；许可证/SBOM 阻断项保持未通过。
