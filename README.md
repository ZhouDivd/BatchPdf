# BatchPdf (CADBPLOT) - AutoCAD 极速批量打印与智能识别出图利器

[![AutoCAD](https://img.shields.io/badge/AutoCAD-2019~2024-blue.svg)](https://www.autodesk.com/products/autocad)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-lightgrey.svg)](https://microsoft.com/windows)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.7.2%20%7C%204.8-green.svg)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> **专门针对国内工程设计院（建筑、结构、机电）复杂图纸量身打造的 AutoCAD 批量打印与智能出图利器。**  
> 突破传统批量打印工具的技术局限，支持**非标加长图幅自动无损裁白边**、**免开 CAD 极速无头打印**、**中英文双语图签智能识别**、**多页 PDF 层级书签自动生成**与**一键绘制原生图纸目录表格**。

---

## 🌟 核心特性一览

| 核心特性 | 传统批量打印插件 | **BatchPdf (本项目)** |
| :--- | :--- | :--- |
| **加长非标图幅 ($A2+1/2$ 等)** | 必须在系统打印机手动加纸，否则留大片白边 | **自动以标准大纸打印并纯矢量无损裁切白边 (Auto-Crop)** |
| **出图速度 (多文件批量)** | 必须在 CAD 界面单线程等待逐张渲染 | **`accoreconsole` 极速无头打印，7张图纸单 DWG 仅需 15 秒** |
| **中英文图签识别** | 经常将 `图名:(DRAWING TITLE)` 误提取为图名 | **智能双语语义过滤，精准提取真实图名、图号与比例** |
| **复杂钢筋/大样图干扰** | 误把特大字号的钢筋标注、构件截面当成图名 | **工程图学先验启发式算法，自动过滤钢筋详图等标注** |
| **PDF 多页合并与书签** | 仅支持单页，或合并后没有层级书签导航 | **自主实现 PDF 对象树合并，生成规范的中文层级书签大纲** |
| **图纸目录绘制** | 不支持或格式杂乱 | **一键生成 AutoCAD 原生表格（含图号、图名、出图日期）** |
| **安装与分发部署** | 繁琐的 APPLOAD / NETLOAD / 菜单宏配置 | **双击 `BatchPdfSetup.exe` 一键部署，启动 CAD 自动加载** |

---

## 🚀 快速安装与使用

### 方式一：一键安装程序（推荐，非开发人员首选）
1. 从 [Releases](../../releases) 页面下载 `BatchPdfSetup.exe`（单文件仅 ~660 KB）；
2. 双击运行 `BatchPdfSetup.exe`，点击 **【一键安装到本机】**；
3. 打开 AutoCAD（2019 ~ 2024），命令行输入 `BPDF` 即可使用！同时桌面会自动生成免开 CAD 极速打印图标。

### 方式二：绿色免安装部署
1. 下载或解压 `BatchPdf.bundle` 文件夹；
2. 将其复制到 Windows 官方插件目录：
   ```text
   C:\ProgramData\Autodesk\ApplicationPlugins\BatchPdf.bundle
   ```
3. 打开 AutoCAD，系统将自动识别并加载对应版本。

---

## 🛠️ 功能详解

### 1. 模型空间与布局空间智能图框识别
* **多形态图框支持**：自动扫描图框块（`BlockReference`）、闭合多段线（`Polyline`/`LwPolyline`）；
* **嵌套与同心内框智能去重**：自动剔除图签表格线、内侧装订线，仅保留最外层真实出图范围；
* **旋转图框自动校正**：支持 $30^\circ$、$45^\circ$、$90^\circ$ 任意旋转图框，自动计算 DCS 视图扭转并无失真还原。

### 2. 非标图幅自动无损裁白边 (PDF Auto-Crop Engine)
针对设计院常见的非标加长图幅（如 $A2+1/2$、$A1+1/4$ 等）：
1. 插件自动在 `DWG To PDF.pc3` 中匹配**可完整容纳该图幅的标准大纸**（如 A0/A1）；
2. 打印生成后，通过内置的高性能底层 PDF 语法解析器，动态调整 PDF 页面字典中的 `/MediaBox` 与 `/CropBox`；
3. **100% 保持纯矢量数据**，不经过任何位图栅格化重新压缩，速度快至 50 毫秒/页，精准无缝裁切多余空白边距。

### 3. 免开 CAD 极速批量打印 (Headless Batch Plot)
* 彻底摆脱图形界面渲染瓶颈，直接调用 AutoCAD 内核控制台 `accoreconsole.exe`；
* **使用方式**：
  - **拖拽出图**：直接将 DWG 文件或文件夹拖拽到桌面「【BatchPdf】极速批量打印」图标上；
  - **右键菜单出图**：在任意 DWG 文件或包含 DWG 的文件夹上点击鼠标右键 -> 选择 **「使用 BatchPdf 批量打印 (免开CAD)」**；
  - **自动生成报告**：批量任务执行完毕后，自动在图纸目录输出详细的出图 CSV 审计报表。

### 4. 智能多页合并与 PDF 书签目录
* 支持将同一 DWG 中的多张图纸（甚至混杂 A1 横向、A2 纵向、旋转加长图框）无缝合并为一个多页 PDF；
* 自动在 PDF 内部构建 UTF-16BE 编码的标准层级书签导航（如 `[结-01] 一层结构平面布置图`），支持 Adobe Acrobat、Edge、Chrome 即时跳转。

### 5. 一键生成 CAD 原生图纸目录表格 (`BPTABLE`)
* 批量识别完成后，点击“生成图纸目录”，在当前 DWG 模型空间中直接插入 AutoCAD 原生 Table 对象；
* 包含列：`序号`、`图纸编号`、`图纸名称`、`出图比例`、`图幅`、`出图日期 (yyyy.MM)`、`备注`；
* 遵循国标制图规范，文字样式、行高、对齐方式与图层自适应匹配。

---

## ⌨️ 常用命令与快捷键

在 AutoCAD 命令行直接输入以下命令：

| 快捷命令 | 说明 |
| :--- | :--- |
| **`BPDF`** 或 **`BPLOT`** | 打开 BatchPdf 批量打印与智能识别主界面 |
| **`BPPREVIEW`** | 打开当前选定图框的矢量交互预览窗口（支持缩放平移） |
| **`BPTEACH`** | 图框拾取学习模式（点选任意图框，自动提取图层、块名、图号图名位置并记忆） |
| **`BPTABLE`** | 在当前图纸模型空间中快速绘制标准图纸目录表格 |
| **`BPDFBATCH`** | 命令行/无头脚本批量打印执行命令 |
| **`BPDFSET`** | 打开高级规则过滤器与属性映射设置窗口 |
| **`BPDFLOG`** | 快速打开最近一次批量出图的详细日志文件 |

---

## 🏗️ 开发者编译构建指南

本项目完全自主实现，**除 AutoCAD 官方 SDK 基础库外零外部依赖**。

### 1. 解决方案结构
```text
BatchPdf.sln
├─ Directory.Build.props            # 全局构建属性与 CAD SDK 回退路径配置
├─ lib/                            # 预置 AutoCAD 2020/2022 SDK 引用库 (离线编译支持)
├─ src/
│  ├─ BatchPdf.Core/               # 核心领域模型、几何算法、图签启发式分析器、命名服务
│  ├─ BatchPdf.Infrastructure/     # JSON持久化、PDF底层解析器、矢量裁边器、多页书签合并器
│  ├─ BatchPdf.AutoCAD/            # AutoCAD 对象扫描器、瞬态交互高亮、原生表格服务
│  ├─ BatchPdf.Plot/               # PC3 媒体目录枚举、视图扭转解析、PlotEngine 出图引擎
│  ├─ BatchPdf.UI/                 # WinForms 交互主界面、多文件批量面板、规则配置窗体
│  ├─ BatchPdf.Plugin.Acad2020/    # AutoCAD 2019-2020 (.NET 4.7.2) 独立发行程序集
│  └─ BatchPdf.Plugin.Acad2022/    # AutoCAD 2021-2024 (.NET 4.8) 独立发行程序集
├─ tools/
│  ├─ BatchPdf.Installer/          # 一键安装引导程序 (BatchPdfSetup.exe)
│  └─ headless/                    # 无头打印驱动脚本与上下文菜单注册脚本
└─ tests/
   └─ BatchPdf.Core.Tests/         # 24 项核心算法与几何分析全量自动化单元测试
```

### 2. 编译命令
打开 Developer Command Prompt 或 PowerShell，执行以下命令即可编译全部工程与安装包：

```powershell
& "MSBuild.exe" BatchPdf.sln /p:Configuration=Release /p:Platform=x64
```

编译生成文件位于：
* **单文件安装器**：`bin/Release/Installer/BatchPdfSetup.exe`
* **Bundle 插件包**：`bin/Release/BatchPdf.bundle/`
* **核心单元测试**：`bin/Release/Tests/BatchPdf.Core.Tests.exe`

### 3. 运行自动化单元测试
```powershell
& "bin/Release/Tests/BatchPdf.Core.Tests.exe"
```
确保几何分析、加长图幅匹配、双语图签过滤等 24 项单元测试全部通过。

---

## 📄 开源许可证

本项目基于 [MIT License](LICENSE) 开源发布，欢迎设计院同仁、CAD 二次开发工程师贡献代码与提出 Issue。
