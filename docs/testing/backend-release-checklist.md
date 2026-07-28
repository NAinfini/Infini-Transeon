# 后端发布检查清单

所有条目必须附机器可读报告或人工证据；`未运行`、`受阻`不得改写为通过。

## 自动化阻断项

- [x] `dotnet test` Debug/Release 全部通过。
- [ ] CMake Debug/Release、CTest 和 ASan 配置全部通过。
- [x] SBOM 生成成功，未知/不兼容许可证为零；应用与可选模型许可证分开报告。
- [ ] 严格离线网络拒绝覆盖完整进程树，DNS/TCP 尝试为零。
- [ ] provider delta 连续性、唯一终态、取消、429/5xx、重定向、慢响应、超限 SSE 全部通过。
- [ ] 两目标 4K 30 分钟 soak：有界队列、公平调度、内存稳定、无旧 overlay generation、无焦点窃取。
- [ ] 真实提交 CPU/GPU 字节不超过当前容量合同；设备重置新旧 epoch 共存峰值也计入。
- [ ] 安装器和便携包均验证 canonical manifest、Ed25519、SHA-256；当前 MSI 必须在签名清单中明确声明 `codeSigning: unsigned`，应用与 GitHub Release 均显示未知发布者警告。
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

- [ ] 当前无签名发布必须明确显示并记录“无包身份，保留系统捕获边框”；接入受信任包签名后，再对 borderless capture 授权同意、拒绝、重启持久化、撤销做完整实测。
- [ ] `WDA_EXCLUDEFROMCAPTURE` 对窗口/显示器捕获均实测；失败时 overlay 明确停用。
- [ ] 4K、300% DPI、混合 DPI/HDR、多显示器、设备移除与恢复。
- [ ] 窗口化/无边框目标移动、缩放、最小化、虚拟桌面和关闭。
- [ ] 全局热键不抢焦点；overlay 非激活、穿透、Alt+Tab/任务栏隐藏。
- [ ] 两个真实适配器与多 GPU 组合完成。

## 本地模型（仅用户主动安装时）

- [x] 首发运行时固定为 CTranslate2 4.8.1 + SentencePiece 0.2.1、CPU-first INT8；
  原生提交、ABI 与发布依赖已固定并通过自动验证。
- [x] ModelWorker 的内存/单进程 Job 限制已进入容量合同，按档案引用惰性启动并在空闲后退出。
- [ ] AppContainer 内 DNS/TCP 实际拒绝；仅 bootstrap read handle 被继承。
- [x] 首发 MADLAD-400 3B 权重、SentencePiece、CTranslate2 及静态依赖许可证已独立审计；
  安装包仅包含签名目录元数据和许可证，不包含权重。
- [ ] 缺失、取消、磁盘满、哈希失败、目录回滚、worker 崩溃/重启与旧 epoch 全部通过。

## 当前已知外部门槛

- Task 0 真实硬件/授权矩阵尚未运行。
- 2026-07-24 真机首启发现：用于无签名包的 organization-ID 发布者会被 Win32 激活上下文拒绝；
  改用普通证书主题虽可启动 EXE，但微软要求终端用户的外部位置身份包必须由受信任证书签名。
  因此当前无签名发布不再包含或注册身份包，应用明确记录降级并保留系统捕获边框。取得受信任
  包签名后，才能恢复 package identity 与无边框授权路径。
  同意、拒绝、修复、卸载和移动便携目录仍须真机验收；不得宣称 unsigned 便携版可静默
  非管理员注册。
- OCR 模型权重按产品要求不自动下载，真实质量门槛必须在用户/实验室显式安装后运行。
- 本地翻译运行时、签名目录、断点续传和按需 Worker 已完成；约 2.95 GB 的真实
  MADLAD-400 权重仍须在用户/实验室明确下载后完成 AppContainer 网络隔离、吞吐、内存与
  游戏内质量验收，验收前不得宣称本地翻译硬件门槛已通过。
- 外部位置身份包的首次提升注册、重启、更新、移动目录恢复与卸载仍须在普通用户 Win11
  机器实测；当前发布策略明确为 Authenticode 未签名。

## 2026-07-28 `0.1.0` 候选证据

- 托管 Release 全量测试 886/886 通过，0 失败、0 跳过。
- 启用本地模型运行时的全新原生 Release 构建成功；当前 OneDrive/Codex 路径下 CTest
  12/13，唯一未运行项为被路径执行权限阻止启动的 ABI 可执行文件，不把它记为通过。
  先前 `C:\tmp` 干净目录的同一套件为 13/13；发布前仍要求 GitHub Actions 复验。
- 本地生成的 MSI 与便携包通过载荷、依赖、DPI 清单、无模型权重、SBOM/许可证、
  `NotSigned` 声明和 Ed25519 模型目录签名检查；九个固定模型文件 URL 均返回 HTTP 200
  且字节数匹配。
- 确定性基准 smoke 的两个合成样本通过（CER 0、行准确率 1、本地 P95 48 ms），不替代
  真模型、多语言、4K 或长时间 soak 门槛。
- 真实游戏、混合 DPI 迁移、双窗口、捕获排除像素和带凭据的中美云服务仍未补跑；除非
  发布所有者明确接受该风险，否则不得把本清单标为硬件发布通过。

## 2026-07-21 本地自动化证据

- 托管 Debug 与 Release 各发现 328 项：327 通过、0 失败、1 项动态跳过。跳过项为真实 WGC 主显示器捕获，本机服务返回 `0x80070424`；因此上方 `dotnet test` 阻断项仍未勾选。
- 原生 Debug、Release 与 ASan `RelWithDebInfo` 各 13/13 通过；ASan 目标目录包含 `clang_rt.asan_dynamic-x86_64.dll`，普通 Debug/Release 不依赖该运行库。
- `GameOcrBench` 确定性 smoke：2 个样本，CER 0、行准确率 1、漏检率 0、误检率 0、本地管线 P95 48 ms、CPU 峰值 128 MiB、GPU 峰值 64 MiB。该结果仅验证报告合同，不替代真实模型质量或 4K 实验室校准。
- `git diff --check` 无空白错误；仅有工作树行尾转换提示。
- 当前沙箱无法读取用户级 NuGet 配置，`dotnet tool restore` 因访问被拒而无法生成 CycloneDX SBOM；许可证/SBOM 阻断项保持未通过。

## 2026-07-23 本地自动化证据

- 托管 Release 完整运行 667 项：667 通过、0 失败、0 跳过；`InfiniTranseon.sln`
  的 App、Core、Integration 与 Bench 项目均参与。
- 完整 WinUI App Release 构建通过：0 warning、0 error。
- 启动链在获得外部位置包身份后调用
  `GraphicsCaptureAccess.RequestAccessAsync(Borderless)`；允许、用户拒绝、系统拒绝和清单
  未声明均有自动测试，拒绝不会被伪装成允许。真实同意/拒绝/重启/撤销仍须 Win11 真机
  验证，因此交互式授权门槛保持未通过。
- 应用已接入 1,024 项有界结构化状态通道，并在退出时排空到轮转 JSONL；当前记录启动
  授权、EngineHost 生命周期、捕获目标生命周期和运行诊断。日志校验明确禁止 OCR 文本、
  译文、截图、密钥、个人路径与任意自由文本。
- 应用进程已安装本地元数据崩溃报告器：默认最多保留 20 份/30 天，报告不包含异常消息、
  源码路径、环境变量、内存转储或上传端点，仅保留稳定栈标识、模块版本和有界类型状态。
  自动测试验证私密异常消息与个人路径不落盘，且数量上限生效。
- `dotnet-CycloneDX` 生成托管依赖 SBOM；`verify-sbom-licenses.ps1` 同时合并
  CTranslate2、SentencePiece 及其静态依赖许可证清单，并验证可选模型许可。所有许可证
  均须通过显式 allowlist，未知/不兼容许可证会阻断发布。
- 云 OCR 已从固定 `unsupported` 诊断接入实际 Google Vision 路由；EngineHost 创建的
  OCR execution token 会被托管路由器原样接纳，逐区域同意、strict-offline、deadline、
  重定向拒绝和失败回传仍由既有合同强制。
- 在线运行时目录当前包含 DeepL、Baidu Translate、Alibaba Cloud Translation、
  Azure AI Translator、OpenAI、DeepSeek V4 Flash、Qwen 3.7 Plus、ERNIE 5.0、
  Claude Sonnet 5 与 Gemini 3.6 Flash；提供器 HTTP 客户端按 origin/proxy 隔离，并
  禁用自动重定向与 cookie。模型 ID 采用 2026-07-23 官方在役名称。
- 百度翻译和阿里云机器翻译的多字段凭据已使用唯一引用与 origin/auth/proxy 绑定进入
  Windows Credential Manager；运行时目录、设置目录和实际提供器的绑定有自动一致性测试。
  档案仍保存 BCP-47 语言标签，提供器边界会把默认 `ja → zh-Hans` 正确转换为百度的
  `jp → zh` 和阿里云的 `ja → zh`，缓存键不受提供器别名污染。
- 腾讯云在 2026-07-08 的机器翻译 API 更新中删除 `TextTranslate`；首发矩阵已移除
  `translation.tencent-tmt`，应用不会向用户展示或运行已退役接口。隔离的旧适配器仅保留
  合同测试，Tencent Cloud OCR 不受影响。
- `Local MADLAD-400 3B INT8` 已接入 CTranslate2/SentencePiece 原生运行时和无网络
  ModelWorker；未安装时仍明确显示“仅在用户主动请求后安装”，只有完整安装且运行时 ABI
  可用时才能被档案选择。
- 本地模型管理现已接入生产依赖图：只有应用目录中的 Ed25519 签名
  `model-catalog.json` 验证通过且序列未回滚时，设置页才显示可下载包；用户必须再次确认，
  strict-offline 会在创建 HTTP 客户端前拒绝。下载使用签名限定的 HTTPS origin、逐文件大小
  与 SHA-256 校验、私有暂存目录和原子发布。安装包可明确显示未安装、已安装但运行时不可用、
  损坏或已从目录移除四种状态；目录缺失/损坏时，应用仍枚举并允许移除自己管理的孤立包。
  目录元数据现包含可选显示名和源/目标语言列表，页面显示版本、大小、许可证、运行时、语言
  覆盖、安装进度、取消、安装与移除确认。Release 工作流会从固定模板生成 Ed25519 签名
  目录；目录绑定不可变 Hugging Face 提交、逐文件字节数与 SHA-256，但不携带权重。
- 云 OCR 运行时目录包含 Google Cloud Vision、Baidu OCR 与 Tencent Cloud OCR。百度和
  腾讯的双字段凭据使用独立绑定；只有全部字段存在才报告已连接，缺失凭据与“凭据存在但
  绑定元数据不符”现在是两个不同状态。
- 外部位置身份清单已具备 `AllowExternalContent`、`runFullTrust`、
  `unvirtualizedResources`、`graphicsCaptureWithoutBorder` 与匹配 EXE `msix` 元数据。
  发布脚本的版本/Publisher 生成验证通过；首次启动注册、缺包、旧系统拒绝、UAC helper
  和明确重启路径已有自动合同覆盖。真机注册仍未运行，因此硬件发布门槛保持未通过。
- Windows SDK `MakeAppx 10.0.26100.8249` 已实际生成未签名验证包；清单打包验证通过。
- 不签名的本地 Release 干跑已成功组合自包含 App、静态 CRT EngineHost、单文件
  ModelWorker 与原生模型运行时 DLL。PE 依赖阻断脚本确认四个发布二进制均不依赖
  `clang_rt`、`MSVCP140`、`VCRUNTIME140` 或 debug UCRT。
- 新增便携布局阻断脚本。工作流会在归档前删除 PDB，并验证应用、EngineHost、ModelWorker、
  原生模型运行时、签名模型目录、许可证和全部排除模式；模型权重目录仍必须为空。
- WiX 7 因交互式 OSMF EULA 无法在 GitHub Actions 无人值守构建，发布链已固定到
  WiX 5.0.2。MSI 改为带稳定 UpgradeCode 的 per-machine Program Files 安装，并使用
  advertised Start Menu shortcut；实际构建得到 523 个文件、8 个必需载荷，ICE 验证
  0 error、2 个已定位的 Windows App SDK XAML DLL 语言字段溢出 warning。`e_sqlite3.dll`
  已改为应用 EXE 的 companion file，原 ICE60 warning 已消除。新布局脚本会拒绝过去
  只含 App EXE 的 110 KiB 空壳 MSI。
- 原生 Release 全部目标可由 MSVC/Windows SDK 编译。在 OneDrive 外的干净
  `C:\tmp` 目录重建后，CTest 13/13 全部通过，证明此前 ABI 与坐标映射测试的
  `operation not permitted` 是路径执行策略而非断言失败。
- EngineHost 与 CaptureSpike 已嵌入 Per-Monitor-V2 原生清单；新增发布阻断脚本会用
  Windows SDK `mt.exe` 从最终 PE 资源反向提取并验证清单。本机实测显示器 API 已从错误
  的统一 `96x96` 修正为主屏 `168/175%`、副屏 `120/125%`。清单重建后的第二次 CTest
  中，本 Codex 环境把 EngineHost ACL 改为只读且不可执行，结果为 12/13；未绕过该策略，
  最终打包二进制仍须由 GitHub Actions 与干净 Win11 机器复验。
