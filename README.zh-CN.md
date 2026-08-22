# Infini-Transeon

[English](README.md) | [简体中文](README.zh-CN.md)

Infini-Transeon 是一款开源的 Windows 游戏翻译覆盖工具。它可以捕获窗口、
显示器或桌面固定区域，识别可见文字，通过用户选择的在线服务或本地模型进行
翻译，再将译文覆盖在原文位置或显示在原文附近。

> [!WARNING]
> 当前 MSI 安装包和应用程序均未进行 Authenticode 签名。Windows 会显示
> **未知发布者**，SmartScreen 也可能发出警告。请仅从本仓库的
> [GitHub Releases](https://github.com/NAinfini/Infini-Transeon/releases)
> 下载构建。应用内更新程序会在提供安装包前验证 Ed25519 签名清单、文件大小
> 和 SHA-256 哈希。

## 系统要求

- Windows 11 x64，系统版本 22621（22H2）或更高
- 使用窗口化或无边框全屏的游戏；不支持独占全屏
- 在线 OCR、翻译或 LLM 服务需要用户自行提供凭据
- 仅在用户明确确认后下载可选的本地模型

应用会明确拒绝 Windows 10 和不受支持的 Windows 11 版本，不会静默降级运行。

## 主要功能

- 同时捕获多个窗口、显示器和桌面固定区域
- 为不同游戏建立独立档案，并配置多个可命名、按比例缩放的捕获区域
- 用户自定义高优先级区域与全窗口自动扫描可以同时使用
- 每个区域可独立设置 OCR 频率、优先级、换行规则、翻译组，以及一至四个固定
  译文槽位
- 支持并行翻译器、携带上下文的 LLM 直接翻译，以及可选的 LLM 二次润色
- 内置国际及中国大陆可用的翻译服务，同时支持 OpenAI 兼容接口和可配置 REST
  适配器
- 支持原位覆盖、半透明或模糊背景、偏移译文和悬浮面板等覆盖模式
- 支持浅色、深色、高对比度、英语和简体中文界面
- 提供全局热键和系统托盘菜单，尽量避免用户切出游戏
- 支持版本化档案导入和导出，自动排除 API 密钥、历史记录、截图、模型和个人路径
- 可按档案保存历史，并设置保存时间和存储容量上限
- 所有性能降级均会明确显示和记录，也可以锁定指定区域禁止降级

## 安装与使用

1. 从 [Releases](https://github.com/NAinfini/Infini-Transeon/releases)
   下载 `Infini-Transeon.msi` 或 `Infini-Transeon-portable.zip`。
2. 仅在确认文件来自本仓库时接受“未知发布者”警告。
3. 添加至少一个翻译服务及其凭据。
4. 创建档案，选择一个或多个捕获目标，然后定义区域或启用全窗口扫描。
5. 为各区域配置翻译通道和覆盖策略。
6. 启动档案；游戏过程中可使用全局热键或系统托盘菜单进行控制。

便携版将用户数据保存在 `%LOCALAPPDATA%\InfiniTranseon`，不会把凭据或历史记录
存放在可执行文件旁边。

## 隐私与费用

云端 OCR 和翻译功能会将配置的文字或图像裁剪发送给用户选择的服务商。严格
离线模式会阻止这些请求。Infini-Transeon 不运行崩溃日志收集服务器，本地模型
既不会随应用捆绑，也不会自动下载。诊断日志只记录应用状态和错误，不记录 OCR
文字或翻译内容。

## 从源码构建

构建本仓库需要 .NET 10 SDK、CMake、安装了“使用 C++ 的桌面开发”工作负载的
Visual Studio 2026，以及 Windows 11 SDK。

```powershell
dotnet tool restore
dotnet restore InfiniTranseon.sln
dotnet test --solution InfiniTranseon.sln --configuration Release --no-restore
cmake --preset windows-x64 -DINFINI_ENABLE_LOCAL_MODEL_RUNTIME=ON
cmake --build --preset windows-x64-release
ctest --test-dir artifacts/cmake/windows-x64 -C Release --output-on-failure
```

### 在源码构建中使用本地模型

源码构建不包含本地模型。签名模型目录、模型工作进程和原生翻译库属于发布构件，
应用会在自身可执行文件旁查找这三项内容。将它们装配到构建输出一次后，即可在
“设置”中安装本地翻译和本地 OCR 包：

```powershell
./scripts/build-signed-model-catalog.ps1 -TemplatePath packaging/model-catalog.template.json -OutputPath packaging/model-catalog.json -CatalogSequence 1
./scripts/prepare-local-model-runtime.ps1 -Configuration Release
```

第一条命令会为模型目录签名，需要 `RELEASE_ED25519_PRIVATE_KEY` 和
`RELEASE_ED25519_KEY_ID`；只有密钥持有者可以执行，生成的文件不会进入版本控制。
持有已签名模型目录的用户可以跳过该命令，改用 `-CatalogPath` 参数。目录序号设为
1 是有意为之：正式发布生成的目录具有更高序号，因此开发目录无法使正式目录回滚。

发布打包和未签名构建的详细说明见
[docs/release/github-release.md](docs/release/github-release.md)。硬件验收记录和剩余的
手动检查见
[docs/testing/hardware-acceptance-2026-07-23.md](docs/testing/hardware-acceptance-2026-07-23.md)。

## 许可证

本项目采用 [Apache License 2.0](LICENSE)。每个发布版本都会附带第三方声明。
