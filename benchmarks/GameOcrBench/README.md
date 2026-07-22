# GameOcrBench

此工具只汇总已采集的、许可明确的合成 OCR 样本，不下载或生成受版权限制的游戏截图。

输入 JSON 必须为 `BenchmarkInput`：每个样本记录 fixture、语言、期望/实际文字、检测/识别/本地管线延迟、检测真值和实际提交的 CPU/GPU 字节；还可记录独立的网络提供商延迟、缓存命中、提供商失败和估算成本。输出 schema v2 包含按 Unicode 标量计算的 CER、行准确率、漏检/误检率、P50/P95/P99、内存峰值、缓存/错误率和总估算成本，并保留机器、分辨率和目标数。

```powershell
dotnet run --project benchmarks/GameOcrBench -- samples.json report.json
```

发布数据集必须覆盖：720p/1080p/1440p/4K，CJK/Latin，清晰/描边/阴影/小字/移动/打字机。模型、字体及字典许可证须进入独立模型资产报告。网络提供商延迟不得混入 `LocalPipelineMilliseconds`。
