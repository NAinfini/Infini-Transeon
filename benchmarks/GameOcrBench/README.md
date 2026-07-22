# GameOcrBench

此工具有两类模式：**评分汇总**（既有）与**合成 fixture 生成器**（新增）。二者都不下载或生成受版权限制的游戏截图。

## 模式一：评分汇总（CI 冒烟合同，保持不变）

输入 JSON 必须为 `BenchmarkInput`：每个样本记录 fixture、语言、期望/实际文字、检测/识别/本地管线延迟、检测真值和实际提交的 CPU/GPU 字节；还可记录独立的网络提供商延迟、缓存命中、提供商失败和估算成本。输出 schema v2 包含按 Unicode 标量计算的 CER、行准确率、漏检/误检率、P50/P95/P99、内存峰值、缓存/错误率和总估算成本，并保留机器、分辨率和目标数。

```powershell
dotnet run --project benchmarks/GameOcrBench -- samples.json report.json
```

发布数据集必须覆盖：720p/1080p/1440p/4K，CJK/Latin，清晰/描边/阴影/小字/移动/打字机。模型、字体及字典许可证须进入独立模型资产报告。网络提供商延迟不得混入 `LocalPipelineMilliseconds`。

## 模式二：合成 fixture 生成器

按模型选型评审 §2.3 门槛表构建确定性合成 fixture，用于后续 CER / 行准确率评分。仅使用 Windows 自带字体合成渲染，绝不使用实拍截图。

```powershell
# 生成完整 fixture 集
dotnet run --project benchmarks/GameOcrBench -- generate-fixtures <output-dir> [--seed N] [--scenarios a,b] [--languages a,b]

# 自检：生成微型集，校验 manifest schema、包围盒合法性、小字高度与确定性，违规即非零退出
dotnet run --project benchmarks/GameOcrBench -- --self-check
```

- **场景**：`clear-subtitle`（1080p 常规字号）、`outlined`（描边）、`drop-shadow`（阴影）、`small-text`（物理字高 ≤16px）、`typewriter`（同一行按前缀递增的 N 帧）、`moving`（同一行按位移的 N 帧）。
- **语言**：`zh-Hans`、`zh-Hant`、`ja`（含假名+汉字）、`ko`、`en`；语料内嵌于源码，无网络访问。
- **分辨率**：1280×720、1920×1080、3840×2160。
- **字体**（逐语言解析，缺失即非零退出，无静默回退）：Segoe UI、Microsoft YaHei、Yu Gothic UI、Malgun Gothic。
- **渲染**：System.Drawing（`net10.0-windows`）；描边走 `GraphicsPath` + 加宽画笔，阴影走偏移暗色描绘，背景为程序化生成的渐变游戏 UI 面板。
- **真值**：每图一条 JSON 记录（图片路径、场景、语言、分辨率、DPI 无关字高、逐行 `{text, 紧致包围盒}`）；包围盒由 `GraphicsPath.GetBounds()` 实测，非估算。整轮输出 `manifest.json`（schema 版本化，记录 seed）。
- **确定性**：相同 seed + 参数在同一主机上重跑，`manifest.json` 逐字节一致。渲染 PNG 像素字节与字形轮廓几何依赖字体版本与 OS 光栅器，**不纳入**确定性保证；评分只对齐 `manifest.json` 真值，绝不比对图片哈希（详见 manifest 头 `determinismCaveat`）。

### 重要：生成产物不得入库

生成器本身（源码）入库；**生成的图片与 manifest 不得提交到仓库**——它们体积大且依赖本机字体渲染。请生成到仓库外目录或已被忽略的 `artifacts/` 子目录，仅在本地/实验室评分时使用。
