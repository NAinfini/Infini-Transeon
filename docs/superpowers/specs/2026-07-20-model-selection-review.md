# Infini-Transeon 模型选型评审

**日期：** 2026-07-20
**状态：** 候选已提名，待基准校准。本文档是后端 Task 6（OCR）、Task 14（本地翻译）、Task 16（发布门槛）的模型输入；阈值在硬件矩阵校准完成前均为候选值。

## 1. 目的与范围

实施计划定义了模型的"管道"（ONNX 会话池、readback ring、签名模型目录），但没有回答"用什么模型、怎么证明它够好"。本文档补齐：

1. OCR 检测/识别模型与本地翻译模型的候选与备选；
2. 全部模型及其附属资产（字典、分词器、字体）的许可证清单；
3. 质量与性能的候选门槛，以及校准与回写流程。

不在范围内：云端 OCR/翻译提供商（属于 Task 8 提供商矩阵）。

## 2. OCR 模型

### 2.1 候选

| 角色 | 候选 | 许可证 | 依据 |
|---|---|---|---|
| 文字检测 | PP-OCRv5 det（mobile 为默认，server 为用户可选大模型） | Apache-2.0 | v0.1.0（Python 版）已用 RapidOCR PP-OCRv5 ONNX 实际跑通游戏截图 |
| 文字识别 | PP-OCRv5 rec（中/日/韩/拉丁多语言变体） | Apache-2.0 | 同上；CJK 覆盖是硬需求 |
| 方向分类 | PP-OCRv5 cls | Apache-2.0 | 旋转/竖排元数据的前置 |
| 备选 | PP-OCRv4 mobile（更小更快，质量下限） | Apache-2.0 | 作为性能治理"较小 OCR 模型"降级项 |

新栈直接消费 ONNX 模型（ONNX Runtime CPU 基线），不依赖 RapidOCR Python 包装；需要固化模型转换来源、opset 与输入形状，并 pin 进 `Directory.Packages.props` 与模型清单。

### 2.2 语言与版式结论（必须在 M1 前落档）

- v1 必须支持：简体中文、繁体中文、日文、韩文、英文及常见拉丁文字。
- 竖排日文（縦書き）必须给出明确结论：支持到什么程度，或明确不支持并在 UI 如实告知。不允许"未测试但默认可用"。
- 描边字、阴影字、像素字是游戏场景的常态，不是边缘情况；fixture 集必须按此构建（见 §5）。

### 2.3 候选质量门槛（校准后固化进 Task 16）

在 §5 的 fixture 集上，按每个模型分别测量：

| 场景 | CER（候选） | 行准确率（候选） |
|---|---:|---:|
| 清晰字幕（1080p，常规字号） | ≤ 1% | ≥ 98% |
| 描边/阴影文字 | ≤ 3% | ≥ 95% |
| 小字（物理高度 ≤ 16 px） | ≤ 5% | ≥ 90% |
| 漏检/误检（全 fixture） | 漏检 ≤ 2%、误检 ≤ 3% | — |

### 2.4 候选性能门槛

- 单裁剪（≤ 512×128）CPU 识别 P95 ≤ 80 ms（基准机按 Task 16 矩阵记录）。
- 检测面（长边 1920）单次检测 P95 ≤ 150 ms。
- 与 `RuntimeCapabilities` 的 `MaxOcrSessions`、`MaxOcrTensorWorkspaceBytes` 对账：模型固定工作区实测值必须小于合同上限。

## 3. 本地翻译模型

### 3.1 候选

| 角色 | 候选 | 许可证 | 依据 |
|---|---|---|---|
| 本地 NMT 默认 | MADLAD-400 3B INT8 | Apache-2.0 | v0.1.0 已验证；~4 GB VRAM/RAM 档位 |
| 本地 NMT 可选 | MADLAD-400 7B INT8 | Apache-2.0 | 用户显式下载；~8 GB 档位 |
| 分词 | SentencePiece（MADLAD 附带模型） | Apache-2.0 | 随模型目录分发 |

### 3.2 推理运行时（开放问题，M4 前必须裁决）

两条路线，需要小型基准后二选一：

1. **CTranslate2（MIT，C++）经 P/Invoke**：v0.1.0 验证过的成熟路线，INT8 快；风险是在 AppContainer/LPAC 沙箱内的 DLL 加载、内存分配行为需要实测。
2. **导出 ONNX 走 ONNX Runtime**：与 OCR 共用运行时、打包简单；风险是 MADLAD（T5 架构）ONNX 导出的质量/速度损耗未知。

裁决标准：沙箱内可运行、同精度下吞吐、常驻内存、打包体积。

### 3.3 容量合同缺口（需回写架构规格）

`RuntimeCapabilities v1` 未包含 ModelWorker 的内存上限（`MaxEngineCommittedBytes` 只约束 EngineHost）。3B INT8 常驻约 4 GB，必须作为独立预算项进入容量合同（v1 修订或 v2），否则 admission control 对本地翻译不设防。

### 3.4 质量门槛

本地翻译与 v0.1.0 同源模型，不设新绝对阈值；以固定测试集记录 BLEU/chrF 基线，后续模型或运行时变更不得低于基线（回归门槛而非绝对门槛）。

## 4. 许可证清单

| 资产 | 许可证 | 义务 |
|---|---|---|
| PP-OCRv5 / PP-OCRv4 模型 | Apache-2.0 | NOTICE 署名 |
| PaddleOCR 字典文件 | Apache-2.0 | NOTICE 署名 |
| ONNX Runtime | MIT | NOTICE 署名 |
| MADLAD-400 权重 | Apache-2.0 | NOTICE 署名；模型目录内独立 LICENSE |
| CTranslate2（若选用） | MIT | NOTICE 署名 |
| SentencePiece | Apache-2.0 | NOTICE 署名 |
| 覆盖层 CJK 回退字体（待选，如 Noto Sans CJK） | SIL OFL 1.1 | 随包分发需附 OFL 全文；不得单独出售 |
| fixture 用游戏字体 | 逐字体核验 | 仅用许可允许合成渲染的字体；实拍截图不入库 |

CI 许可证审计（Task 1/15 已定义）必须覆盖上表全部条目；模型许可证与应用 Apache-2.0 分开审计。

## 5. 验证计划

- **Fixture 构建：** 用许可字体合成渲染游戏风格文本（清晰、描边、阴影、小字、移动、打字机；CJK+拉丁；720p–4K），叠加真实游戏 UI 风格背景。自采实拍截图仅本地评测使用，不进入仓库。
- **机器矩阵：** 引用后端 Task 16 的发布矩阵；每次基准记录 CPU/GPU/RAM、分辨率、目标数、采样窗口与 P50/P95/P99。
- **指标：** CER、行准确率、漏检/误检、单裁剪与检测面延迟、模型工作区实测字节。
- **回写：** 校准完成后，把最终阈值固化进后端 Task 16 与 `docs/testing/backend-release-checklist.md`，本文档状态改为"已校准"。

## 6. 开放问题清单

- [ ] ModelWorker 推理运行时裁决（CTranslate2 vs ONNX 导出），含沙箱内实测（§3.2）
- [ ] 竖排日文支持结论（§2.2）
- [ ] ModelWorker 内存上限进入容量合同（§3.3，需回写架构规格）
- [ ] 覆盖层 CJK 回退字体选定与 OFL 合规（§4）
- [ ] PP-OCRv5 ONNX 转换来源与 opset 固化（§2.1）
- [ ] 阈值校准完成后回写 Task 16（§5）
