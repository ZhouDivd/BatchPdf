# AutoCAD 批量打印 PDF 插件开发计划（边界冻结版）

> 版本：V1.1（需求边界确认）  
> 日期：2026-10-24  
> 技术路线：C# / AutoCAD .NET Managed API / `NETLOAD` 加载 DLL  
> 首发版本：AutoCAD 2020、AutoCAD 2022  
> 输出设备：只使用 AutoCAD 自带的 `DWG To PDF.pc3`（以当前 CAD 实际枚举到的设备名称为准）

---

## 0. 已确认需求与开发边界

本表是开发、测试和 Codex 实现时的约束基线。除非后续明确变更，不要擅自扩大或改变范围。

| 项目 | 已确定要求 | 实现约束 |
|---|---|---|
| AutoCAD 版本 | **只支持 AutoCAD 2020 和 AutoCAD 2022** | 为两个版本分别构建和验证 DLL；不承诺兼容 2021、2023 及更高版本，也不支持 ZWCAD/浩辰/BricsCAD |
| 插件加载 | 用户通过 `NETLOAD` 加载 DLL | 第一版不做安装程序、Ribbon、自启动注册；命令入口打开插件界面 |
| 图框类型 | **闭合矩形多段线 + 图框块，两种都支持** | 允许设置图框图层、块名和属性标签；自动识别不确定时可手动补选/修正 |
| 图框形态 | **支持旋转图框、加长图框** | 旋转框必须输出方向正确且边界正确；加长纸张必须存在于 `DWG To PDF.pc3` 的可用纸张列表中 |
| 批打范围 | 第一版处理当前打开 DWG 的模型空间图框 | 跨多个 DWG、布局空间批打放到后续版本，不作为首版前置条件 |
| PDF 输出模式 | **每张图单独输出 PDF / 全部合并为一个多页 PDF，两种都支持** | 允许同一个合并 PDF 中不同页面使用不同纸张尺寸；任务顺序与 PDF 页序一致 |
| PDF 绘图仪 | **固定使用 CAD 自带 `DWG To PDF.pc3`** | 不自动切换到 `AutoCAD PDF (General Documentation).pc3` 或其他设备；若找不到指定 PC3，应给出明确错误并停止出图 |
| 纸张列表 | **完全读取该 PC3 当前提供的纸张尺寸** | 纸张下拉框不得硬编码 A0/A1/A2/A3/A4；以 CAD API 枚举到的媒体名称为准 |
| 自动匹配纸张 | **提供可勾选的“自动匹配纸张大小”选项** | 勾选时按图框物理尺寸、比例、方向和 PC3 媒体尺寸匹配；取消勾选时由用户从 PC3 纸张列表手动选择 |
| 自定义/加长纸张 | 通过 `DWG To PDF.pc3` 的打印机配置预先定义，再由插件读取使用 | 插件不自行伪造纸张、不假设每台电脑都已有所有加长尺寸 |
| 图名、图号 | 支持读取图框块属性；可配置字段映射 | 首版不依赖 OCR；普通文字区域识别作为可选增强，不可因识别不到就阻止用户手工命名 |
| 源图保护 | 不修改原始 DWG 内容 | 临时视图/打印状态必须恢复；不自动改图元、图层、块属性、图框或布局 |

### 首版成功标准

用户打开包含多张图框的 DWG，执行 `NETLOAD` 并打开插件，扫描并检查图框，确认各图纸的纸张与比例，选择输出方式后，插件用 `DWG To PDF.pc3` 生成正确的单页 PDF 或多页 PDF。遇到无法匹配纸张、缺少 PC3 或比例不确定时，必须让用户看见问题，不可静默猜测后输出错误图纸。

## 1. 产品目标

开发一个服务于建筑、结构设计出图的轻量 AutoCAD 插件，主要减少以下重复操作：逐张框选打印窗口、判断 A 系列或加长纸张、手动设定比例、确认旋转方向、逐张输入文件名，以及多张图纸合并 PDF。

设计优先级如下：

1. **出图正确性优先。** 纸张、图框窗口、打印比例、方向和裁切必须正确。
2. **PC3 是纸张事实来源。** 插件不维护与 CAD 不一致的另一套纸张定义。
3. **自动化必须可审核。** 每张图在打印前显示识别框、图幅、比例来源、选用纸张和目标文件名。
4. **操作可恢复。** 单张失败不应让整个批次静默中止；支持日志与重试。
5. **不修改源图。** 除临时打印上下文外，不修改原始 DWG 数据。
6. **首版足够小。** 先完成当前 DWG 模型空间，不提前开发跨 DWG 后台批处理、Ribbon 或自动安装。

## 2. 功能需求清单

### 2.1 必须实现（MVP）

- `BPDF` 命令打开主界面。
- 扫描当前 DWG 模型空间中的矩形闭合多段线与图框块。
- 支持旋转图框、加长图框；自动识别失败时允许手动框选补充。
- 图框清单：序号、图号、图名、图框类型、几何尺寸、旋转角、比例、纸张、输出文件名、状态和错误提示。
- 支持选择/反选、排序、删除误识别图框、手动覆盖图号/图名/比例/纸张。
- 固定使用 `DWG To PDF.pc3`；启动时检测设备是否可用。
- 从该 PC3 读取当前实际可用的纸张媒体名称和物理尺寸。
- 提供可勾选的“自动匹配纸张大小”。
- 未勾选自动匹配时，可从 PC3 实际可用纸张下拉列表中手动选择。
- 支持按图框实际窗口打印，以及明确的比例设置；不能把“适合纸张”暗中当作固定比例。
- 独立 PDF 输出模式：每个图框一个文件。
- 合并 PDF 输出模式：一个多页 PDF，页序与任务清单一致，支持不同页面尺寸。
- CTB/STB 打印样式、线宽和彩色/黑白输出选项（按当前 DWG 和 CAD 实际配置提供）。
- 输出目录选择、文件名规则、重名保护、打印进度、成功/失败状态、日志与失败重试。
- 保存默认配置及图框规则，下一次能复用。

### 2.2 首版明确不做

- 扫描并批量打开多个 DWG 文件。
- 布局空间、图纸集、外部参照跨文件自动管理。
- OCR 读取任意位置的图名/图号。
- 插件自动创建、修改或分发 PC3/PMP/CTB/STB 文件。
- 插件自行生成新纸张定义。自定义/加长纸张应由用户先在 `DWG To PDF.pc3` 中配置。
- 云打印、在线服务、用户账户、授权系统。
- 自动修改图纸内容、图框或原始布局设置。
- 面向 AutoCAD 2021/2023+ 或其他 CAD 品牌的兼容性承诺。

## 3. 市场功能对标与取舍

本节是功能规划参考，不是对不同产品进行统一环境的速度或准确率测试。

| 产品/工具 | 公开资料中常见功能 | 对本插件的启发 | 本项目处理方式 |
|---|---|---|---|
| AutoCAD Publish / 原生 Plot | 布局和图纸集发布、PDF 输出、页面设置 | 应优先使用 CAD 原生绘图引擎 | 复用 AutoCAD Plot API，并固定所需 PDF PC3 |
| Advanced Batch Plot | 图框识别、任务排序、命名规则、设置保存、多页合并 | 清单、排序与配置复用很重要 | 纳入首版的单 DWG 图框任务清单 |
| 国产批量打印工具 | 模型空间识图、图名图号提取、CTB/STB、PDF 合并 | 贴合国内工程图框和交付习惯 | 提供图框块属性映射，不依赖 OCR |
| 易出图等工程出图插件 | 标准/加长/旋转图幅、自动命名、批量输出 | 旋转和加长纸张不能只作为后续装饰功能 | 作为首版需要验证的核心边界 |
| BPPlot 等开源项目 | 模型空间图框识别、精确比例、合并 PDF 等 | 可用作理解算法和组织方式的参考 | 不直接照搬其 CAD 版本兼容性结论 |

### 功能取舍原则

首版不追求“功能数量最多”，而追求“图框能识别、纸张匹配有依据、打印设置可解释、PDF 输出可靠”。市场上成熟产品的图纸集管理、跨 DWG、书签、OCR、无人值守批处理等能力可以在稳定性验证后再评估。

参考资料见第 12 节。

## 4. 技术方案

### 4.1 总体技术栈

- 语言：C#。
- CAD 扩展方式：AutoCAD .NET Managed API，用户通过 `NETLOAD` 加载 DLL。
- UI：WinForms；首版用普通窗体即可，不先开发 Ribbon/PaletteSet。
- PDF 出图：AutoCAD 原生 `PlotEngine` + 固定 `DWG To PDF.pc3`。
- 打印设置：`PlotSettings`、`PlotSettingsValidator`、`PlotInfo`、`PlotInfoValidator`。
- 图形读取：`Document`、`Database`、`Transaction`、`Polyline`、`BlockReference`、块定义和属性。
- 配置：JSON，存放在 `%APPDATA%\BatchPdf\settings.json`。
- 日志：文本或 JSON Lines，存放在 `%LOCALAPPDATA%\BatchPdf\Logs\`。
- 测试：纯几何、纸张匹配、命名和配置使用单元测试；真实出图必须在 AutoCAD 2020/2022 中集成测试。

### 4.2 AutoCAD 2020 / 2022 兼容性策略

Autodesk 官方兼容性表指出：AutoCAD 2020 对应 .NET Framework 4.7；AutoCAD 2022 对应 .NET Framework 4.8。两者使用的 AutoCAD Managed API SDK 版本不同。因此本项目不以“编译一份 DLL 后未经验证地兼容两个版本”为前提。

| 构建产物 | 编译目标 | AutoCAD API 引用 | 加载方式 |
|---|---|---|---|
| `BatchPdf.Acad2020.dll` | .NET Framework 4.7 | 从 AutoCAD 2020 安装目录引用对应 `AcMgd.dll`、`AcDbMgd.dll`、`AcCoreMgd.dll` 等 | 在 AutoCAD 2020 执行 `NETLOAD` |
| `BatchPdf.Acad2022.dll` | .NET Framework 4.8 | 从 AutoCAD 2022 安装目录引用对应版本的 Managed DLL | 在 AutoCAD 2022 执行 `NETLOAD` |

共用的纯业务代码可以放入同一 Core 项目；AutoCAD API 适配层分别以对应版本编译。AutoCAD Managed DLL 的引用应设置为 `Copy Local = False`，避免将 CAD 自带程序集复制到插件目录而产生版本冲突。

**必须在两种目标 CAD 中分别做实际加载和打印回归。** 不得仅凭 C# 编译通过认定兼容。首次开发应先选一个版本打通最小打印闭环，再构建另一个版本。

官方参考：[About Managed .NET Compatibility](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Customization/files/GUID-A6C680F2-DE2E-418A-A182-E4884073338A.htm)。

### 4.3 建议项目结构

```text
BatchPdf.sln
├─ src/
│  ├─ BatchPdf.Core/                  # 图框/纸张/任务模型、匹配算法、命名规则
│  ├─ BatchPdf.AutoCAD/               # 事务、实体扫描、AutoCAD API 适配器
│  ├─ BatchPdf.Plot/                  # PC3 检测、PlotSettings、PlotEngine 出图
│  ├─ BatchPdf.UI/                    # WinForms 主窗体和任务清单
│  ├─ BatchPdf.Infrastructure/        # JSON 配置、日志、路径和文件操作
│  ├─ BatchPdf.Plugin.Acad2020/       # AutoCAD 2020 编译入口
│  └─ BatchPdf.Plugin.Acad2022/       # AutoCAD 2022 编译入口
├─ tests/
│  ├─ BatchPdf.Core.Tests/            # 纯业务逻辑测试
│  └─ TestDrawings/                   # 有人工预期结果的 DWG 测试图
├─ docs/
└─ README.md
```

第一阶段允许先压缩为 `BatchPdf.Core` + 一个版本的 `BatchPdf.Plugin`，但不能把图框几何算法、UI、PC3 纸张匹配和 PlotEngine 调用全部写进一个大方法。

## 5. PC3 与纸张尺寸：硬性实现规范

### 5.1 设备必须固定为 `DWG To PDF.pc3`

初始化时调用 `PlotSettingsValidator.Current.GetPlotDeviceList()` 获取 AutoCAD 当前可用绘图仪列表，刷新设备/媒体缓存后，查找显示名称对应 `DWG To PDF.pc3` 的设备。设备名大小写可用于定位时做不区分大小写比较，但真正传给 CAD API 的参数必须使用枚举列表返回的**原始设备名称**。

检测结果处理：

- 找到：继续读取这个设备的媒体列表。
- 找不到：停止打印并提示用户检查 `PLOTTERMANAGER`/Plotters 配置；不允许自动改用其他 PC3。
- 媒体列表为空或刷新失败：停止自动出图，显示设备配置错误和日志位置。

注意：某些用户可能改名、删除或替换了默认 PC3。插件不能假设所有电脑上的名称和内容绝对相同，必须运行时验证。

### 5.2 纸张下拉列表必须来自 PC3

纸张名称和尺寸采用以下流程读取：

1. 在临时 `PlotSettings` 上设置设备为当前枚举到的 `DWG To PDF.pc3` 名称。
2. 调用 `PlotSettingsValidator.RefreshLists(...)` 刷新设置。
3. 通过 `GetCanonicalMediaNameList(...)` 读取该 PC3 的可用标准媒体名称。
4. 通过 `GetLocaleMediaName(...)` 获取给用户显示的本地化名称；内部保存 canonical media name，避免把显示名称直接作为 API 标识。
5. 对每个媒体项设置 canonical media name，并读取 `PlotSettings.PlotPaperSize` 及 `PlotPaperMargins` 等 API 提供的真实尺寸数据。
6. 纸张列表、自动匹配和最终打印设置都只能使用这份媒体数据。

禁止：

- 在代码中硬编码“当前 PC3 一定有 A0/A1/A2/A3/A4”。
- 只凭纸张显示名称里的 `A1`、`841x594` 等文字推断尺寸。
- 在所选纸张不存在时静默换到其他绘图仪或其他纸张。
- 假定用户电脑里一定配置了某种加长纸张。

### 5.3 “自动匹配纸张大小”选项

主界面必须有复选框：`自动匹配纸张大小`。建议默认勾选，但用户可取消。

**勾选时：**

1. 先确定图框打印窗口、绘图单位以及有效打印比例。比例优先取用户明确设定值，其次取已配置的图框块属性/字段规则；无法可靠确定比例时应提示用户确认，不得把猜测当作确定结果。
2. 根据图框尺寸、比例和单位换算得到预期纸面宽高。
3. 遍历 `DWG To PDF.pc3` 暴露的纸张媒体，以 API 实际返回的物理宽高进行匹配；横向/纵向和允许的 90° 旋转都参与候选比较。
4. 优先选取纸面宽高与预期图幅最吻合的媒体，同时检查可打印边距和图框是否会被裁切。
5. 若无足够接近的媒体，任务状态标记为“未匹配/需确认”，显示预期尺寸和可用候选纸张。不能静默缩放到任意纸张。
6. 用户确认后，可手动选择当前 PC3 的某个媒体继续打印，或者先在 PC3 配置正确的标准/加长纸张再刷新列表。

**取消勾选时：**

- 禁用自动纸张选择。
- 纸张下拉框显示从该 PC3 动态读取的所有媒体，用户手动指定。
- 插件依然要用指定媒体的实际尺寸和边距做打印范围校验，至少警告明显裁切风险。

**自动匹配和“适合纸张”不是一回事：**
自动匹配负责选媒体尺寸；打印比例仍遵循明确比例设置。若产品提供“适合纸张”模式，必须把它做成独立的明确选项，且显示这会改变输出比例。不能为了让纸张匹配成功而暗中勾选 Fit to Paper。

### 5.4 加长与自定义纸张

只要用户在 `DWG To PDF.pc3` 中配置了自定义/加长纸张，且 CAD API 将其列入可用媒体，插件就应把它纳入同一纸张列表和自动匹配候选。未配置的尺寸不由插件凭空创建。

如果用户需要新加长尺寸，帮助提示应指导其通过 AutoCAD 的打印机管理/PC3 配置流程添加纸张，完成后重新刷新媒体列表。可参考 Autodesk 官方资料：[Create or Modify a DWG to PDF Configuration File](https://help.autodesk.com/cloudhelp/2025/ENU/DWGTrueView/files/GUID-AA7FA0D6-AB26-4FD6-B254-BB8A7E467501.htm) 和 [Cannot add custom paper size](https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/Can-t-add-custom-paper-size-for-system-printers-in-AutoCAD.html)。

## 6. 图框扫描和旋转/加长图幅识别

### 6.1 第一版识别对象

**A. 闭合矩形多段线**

- 支持闭合 `Polyline`，优先识别四边或可简化为四边矩形的几何轮廓。
- 验证边长、直角误差、闭合状态和矩形面积，过滤一般图元、符号框及明显不合格边界。
- 保存四个角点、宽高、旋转角、图层、对象句柄和几何置信状态。

**B. 图框块（BlockReference）**

- 支持块参照边界识别和属性读取。
- 允许按块名/有效块名配置过滤规则，以减少把普通块误识别为图框。
- 读取块属性中的图号、图名、比例等字段，并允许映射不同的属性标签。
- 对块参照的变换矩阵、旋转角与缩放因子做处理，不能直接假定块处于 WCS 原点、零旋转、1:1 缩放。
- 动态块、嵌套块如不能稳定识别，必须给出“需检查”，不可生成错误打印窗口。

### 6.2 旋转图框处理

旋转图框属于首版范围，不允许只取 WCS 轴对齐包围盒后直接打印，因为这可能把图框外的邻近内容一起输出或造成纸张方向不对。

技术要求：

- 识别四个实际角点和图框局部坐标系。
- 计算图框方向角与打印方向。
- AutoCAD 的 `PlotWindowArea` 使用 DCS（显示坐标系）窗口坐标；在构建打印窗口前，按目标 CAD API 要求把图框角点从 WCS 转换到相应 DCS/绘图视图坐标。
- 对旋转的图框，用临时、可恢复的绘图视图/视图扭转处理窗口方向；必要时将此能力封装为 `RotatedFramePlotResolver`。不得永久改变当前 UCS、视图或 DWG。
- 仅在输出纸张允许的 0°/90° 纸张方向切换，不要把“任意角度旋转图框”和“纸张横竖向交换”混为一谈。
- 旋转打印必须在 AutoCAD 2020 与 2022 都做真实 PDF 回归，核对四边、图签位置、比例和周围内容是否裁切。

这是一个高风险技术点，必须在早期小原型中验证，不能等整个 UI 写完后才测试。Autodesk 文档说明窗口打印坐标需要使用 DCS，参考：[PlotterValidator.SetPlotWindowArea](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-ManagedRefGuide/files/OREFNET-Autodesk_AutoCAD_DatabaseServices_PlotSettingsValidator_SetPlotWindowArea_PlotSettings_Extents2d.html)。

### 6.3 加长图幅处理

- 不用固定 `A0-A4` 枚举作为唯一纸张集合。
- 图框的目标纸面尺寸按图框宽高、比例和单位换算；纸张候选取自 PC3 的动态媒体列表。
- 如果图框比例未知，清单中将比例显示为“待确认”，暂停自动纸张匹配或要求用户指定比例。
- 如果图框比例已知但 PC3 没有对应加长尺寸，提示“PC3 缺少匹配纸张”，不自动使用更小纸张裁切。
- 加长纸张的验收要覆盖长边尺寸、旋转方向、纸张边距、PDF 页面实际尺寸及边界完整性。

### 6.4 图框识别与图名图号分离

先识别矩形范围，再读取语义属性。图框几何识别失败不能通过猜图名来补救；图名/图号读取失败也不应该删除一个几何正确的候选图框。

建议属性映射配置：

```json
{
  "frameRules": [
    {
      "ruleName": "CompanyStandardFrame",
      "blockNames": ["COMPANY_FRAME_A1", "COMPANY_FRAME_LONG"],
      "sheetNumberTags": ["图号", "DRAWING_NO"],
      "sheetNameTags": ["图名", "DRAWING_NAME"],
      "scaleTags": ["比例", "SCALE"]
    }
  ]
}
```

上面是配置示例，不代表用户实际图框块名或字段名。真实映射由使用者在插件设置里配置。

## 7. 打印任务模型、命名和输出

### 7.1 内部模型 `PrintSheet`

每张被识别图纸至少包含：

- `Id`：本次任务内的稳定 ID。
- `SourceDrawingPath`：源 DWG 完整路径。
- `SpaceName`：首版固定为模型空间。
- `FrameHandle`：对象句柄（只用于当前 DWG 任务定位，不作为跨文件持久身份）。
- `FrameType`：Polyline / BlockReference / ManualWindow。
- `Corners` / `FrameExtents` / `RotationAngle`：几何信息。
- `DrawingUnits`、`ScaleValue`、`ScaleSource`：单位和比例，以及比例来源。
- `TargetPaperCanonicalName`、`PaperWidth`、`PaperHeight`、`PaperMargins`：来自 `DWG To PDF.pc3` 的纸张信息。
- `PlotRotation`、`PlotStyle`、`ColorMode`、`PrintLineweights`：打印参数。
- `DrawingNumber`、`DrawingName`、`OutputFileName`。
- `Status`、`WarningMessages`、`ErrorMessage`。

打印执行阶段应使用已确定的 `PrintSheet` 快照，避免用户编辑清单时改变正在执行的参数。

### 7.2 文件命名

默认规则：`{图号}_{图名}.pdf`。

- 图号或图名缺失时依次回退到 `{DWG名}_{序号}.pdf`。
- 清理 Windows 文件名非法字符并限制文件名长度。
- 重名策略：自动追加序号、覆盖前确认、跳过三种，默认自动追加序号。
- 任务清单允许打印前直接编辑目标文件名。
- 合并 PDF 使用用户指定文件名，例如 `{DWG名}_合并.pdf`；页面顺序取任务清单当前排序。

### 7.3 两种输出模式

**模式 A：每张图单独 PDF**

- 每个 `PrintSheet` 创建一个输出文件。
- 单张失败只标记这一张失败，其他图纸可继续。
- 输出目录和文件名冲突均需事先检查。

**模式 B：合并为一个多页 PDF**

- 全批使用一个输出 PDF 文件。
- 通过 AutoCAD 原生 `PlotEngine` 的多页打印流程输出，目标仍然是 `DWG To PDF.pc3`。
- 每张图纸对应 PDF 的一页；页面顺序与清单顺序一致。
- 允许不同页面使用不同的纸张媒体（如 A1、A3、加长纸张混排）；这是强制测试项。
- 若单页在预检阶段失败，提供是否跳过并继续的选择；日志记录最终 PDF 实际包含哪些图纸，不能让任务清单与合并文件内容不一致。
- 不优先引入第三方 PDF 合并库。只有在原生多页 PlotEngine 验证确实无法满足需求时才评估外部合并库，并先核实它对 .NET Framework 4.7/4.8 的支持和许可证。

### 7.4 打印设置

- 设备固定为 PC3 实际枚举到的 `DWG To PDF.pc3` 名称。
- 纸张使用 canonical media name，显示名只用于 UI 展示。
- 打印样式从当前 DWG 实际可用 CTB/STB 列表读取；不得将不存在的样式写入设置。
- 线宽、打印样式、颜色模式与比例要在清单或打印设置区有明确的可见状态。
- 图框窗口、纸张、比例、打印旋转在进入 `PlotInfoValidator` 前全部配置完毕并校验。
- 需要校验设备和纸张列表时调用刷新接口，避免读取 CAD 启动后已过期的媒体缓存。

## 8. 用户界面与命令设计

### 8.1 主界面布局

**顶部操作栏**

- 扫描图框
- 手动选框
- 全选 / 反选
- 删除选中项
- 上移 / 下移或排序
- 预览选中图纸
- 开始打印
- 打开输出目录

**图纸清单列**

| 列 | 作用 |
|---|---|
| 选择 | 决定是否纳入此次输出 |
| 序号 | 最终打印顺序 |
| 图号 / 图名 | 自动读取并允许修改 |
| 类型 | 多段线 / 图框块 / 手动范围 |
| 尺寸与旋转 | 展示图框几何尺寸、方向角、是否旋转 |
| 比例 | 明确显示数值与来源；未知时标记需检查 |
| 目标纸张 | PC3 的显示纸张名；自动匹配失败时显示警告 |
| 输出文件名 | 单独 PDF 的文件名或合并 PDF 的页面标题信息 |
| 状态 / 信息 | 待打印、打印中、成功、失败、需确认 |

**打印参数区**

- 输出方式：`每张图单独 PDF` / `合并为一个多页 PDF`。
- 绘图仪：只显示并使用 `DWG To PDF.pc3`；若不可用，显示错误，不给其他设备的可选项。
- 纸张自动匹配：`[✓] 自动匹配纸张大小`。
- 手动纸张下拉框：从 PC3 可用纸张列表读取；自动匹配关闭时启用。
- 比例方式：明确比例 / 适合纸张（若启用 fit，必须明确提示会改变比例）。
- 打印样式、线宽、颜色模式。
- 输出路径、文件命名模式、重名策略。

**底部状态区**

- 总张数、待打印、成功、失败、已跳过。
- 当前任务和进度。
- 日志文件路径与错误摘要。

### 8.2 命令入口

| 命令 | 作用 | 版本范围 |
|---|---|---|
| `BPDF` | 打开批量打印窗体 | 首版 |
| `BPDFSCAN` | 扫描当前 DWG 模型空间图框 | 首版，可由主窗体调用 |
| `BPDFTEST` | 检测 PC3/媒体列表并执行单张测试 | 开发与诊断命令 |
| `BPDFSET` | 打开默认设置与图框属性映射 | 首版或紧接首版 |
| `BPDFLOG` | 打开最近任务日志 | 首版 |

命令由 `CommandMethod` 注册；用户使用 `NETLOAD` 加载对应 AutoCAD 年份的 DLL 后输入 `BPDF`。

## 9. 关键模块与接口建议

```text
FrameScanner
  ├─ ScanClosedRectangularPolylines(Database) -> FrameCandidate[]
  ├─ ScanFrameBlocks(Database, FrameRules) -> FrameCandidate[]
  └─ DeduplicateAndRank(candidates) -> FrameCandidate[]

PaperMediaCatalog
  ├─ EnsurePdfDeviceAvailable() -> DeviceResult
  ├─ RefreshMediaList() -> PaperMedia[]
  └─ ReadPaperPhysicalSize(canonicalMediaName) -> PaperSize

PaperMatcher
  ├─ EstimateTargetPaper(frame, drawingUnits, scale) -> TargetPaperSize
  ├─ MatchAvailableMedia(target, mediaList, rotation) -> MatchResult
  └─ ValidatePrintableArea(frame, media, plotScale) -> ValidationResult

PlotSettingsBuilder
  ├─ BuildPlotSettings(PrintSheet) -> PlotSettings
  └─ Validate(PlotInfo) -> ValidatedPlotInfo

PlotJobRunner
  ├─ PlotEachSheetToPdf(sheets, outputFolder) -> JobResult
  └─ PlotMultiSheetPdf(sheets, outputPdfPath) -> JobResult

ConfigService / NamingService / LogService / BatchPdfForm
```

**重要：** AutoCAD 数据库对象、事务和 Plot API 应在 CAD 允许的命令/文档上下文中操作，不得把 `DBObject` 或 `Transaction` 交给后台线程并行操作。首版串行打印，UI 只更新进度。

## 10. 分阶段开发路线与验收

### 阶段 0：环境固定与单张打印原型（优先级 P0）

任务：

1. 在开发机确认 AutoCAD 2020、2022 的安装路径、产品更新号和 .NET Framework 开发包。
2. 建立两个构建目标；确保 DLL 可在对应 CAD 中通过 `NETLOAD` 加载。
3. 建立 `BPDFTEST` 命令。
4. 从当前 CAD 查询绘图设备列表，验证能找到 `DWG To PDF.pc3`。
5. 从 PC3 读取 canonical media/localized media 列表、纸张尺寸和边距。
6. 手动选择两个窗口角点，使用指定 PC3 输出一张 PDF。
7. 验证固定比例、纸张、方向、CTB/STB、文件路径和 PlotEngine 正常结束。
8. 在 AutoCAD 2020 与 2022 各做一次相同测试。

验收：两个 CAD 年份都可加载 DLL；没有指定 PC3 时能明确报错；输出 PDF 尺寸和内容经人工核对正确。

### 阶段 1：图框识别与预览清单（P0）

任务：

1. 识别闭合矩形多段线。
2. 识别图框块，并读取块属性。
3. 计算旋转角、边界、宽高、图层、块名和对象句柄。
4. 过滤重复框和明显非矩形框。
5. 支持手动框选添加、删除误识别项、修改文件名。
6. 在图纸清单中标记不确定图框。
7. 不修改 DWG 内容。

验收：标准图框、块图框、相邻图框、重复边界、旋转图框均进入可审核列表；无关图元不应被静默打印。

### 阶段 2：纸张目录与自动匹配（P0）

任务：

1. 完成 PC3 设备检查与媒体目录刷新。
2. 用 API 真实读取媒体尺寸及边距。
3. 实现“自动匹配纸张大小”复选框。
4. 实现关闭自动匹配时的手动纸张选择。
5. 支持标准媒体与用户 PC3 中已有的自定义/加长媒体。
6. 将比例来源、单位换算、方向选择和纸张匹配分开处理。
7. 找不到合适媒体时标记“需确认”，不允许自动降级为小纸张裁切。
8. 单独验证旋转图框从 WCS 到 DCS 的转换和打印视图扭转方式。

验收：自动选择的纸张来自当前 PC3 的媒体列表；手动选择不会保存无效纸张名；加长纸张候选尺寸正确；旋转图框打印内容与纸张方向正确。

### 阶段 3：批量出图与两种 PDF 模式（P0）

任务：

1. 配置统一的 `PlotSettings` / `PlotInfo` / `PlotInfoValidator` 流程。
2. 实现每个图框单独输出 PDF。
3. 实现一个 PDF 内包含多个图框页面的原生多页打印。
4. 检查混合纸张页面（例如 A1 + A3 + 加长纸张）在一个 PDF 中的真实结果。
5. 按任务清单顺序输出并确保实际 PDF 页序一致。
6. 加入文件名清理、重名保护、输出文件验证、进度和每张状态。
7. 单张错误不影响其他任务；合并输出错误须告知合并文件是否完整。
8. 对异常或取消路径正确关闭 PlotEngine 并恢复临时 CAD 状态。

验收：独立 PDF 模式和合并模式都能正确输出；合并文件页数、页序、纸张尺寸和内容与清单一致；单张出错能明确追踪。

### 阶段 4：设置持久化、日志和工程稳定性（P1）

任务：

- 保存默认打印模式、输出目录、重名规则、自动匹配开关、打印样式和图框属性映射。
- 提供图框规则配置界面。
- 检查缺少 PC3、媒体列表为空、比例未知、CTB/STB 缺失、输出目录只读、PDF 被占用等情况。
- 提供成功、失败、跳过、重试的摘要。
- 记录每张图的图框信息、使用设备、canonical paper 名、纸张物理尺寸、比例、打印样式、输出路径和错误。

验收：关闭并重新打开 AutoCAD 后设置仍在；日志足以定位失败原因；程序不会静默覆盖用户文件或改变 CAD 环境。

### 阶段 5：后续扩展（P2，首版不做）

按用户使用反馈再决定是否增加：跨 DWG 队列、布局空间批打、图纸集、PDF 书签、图纸目录 CSV/Excel、嵌套块深度识别、自动加载、Ribbon、安装包。

## 11. 测试矩阵

| 类别 | 必测用例 |
|---|---|
| CAD 版本 | AutoCAD 2020、AutoCAD 2022 分别 NETLOAD、扫描、出 PDF |
| 设备 | `DWG To PDF.pc3` 存在、设备被改名、PC3 缺失、媒体刷新失败 |
| 纸张来源 | 当前 PC3 有标准纸张、没有某标准纸张、配置有自定义纸张、配置加长纸张 |
| 自动匹配 | 勾选自动匹配；取消自动匹配；精确尺寸；近似尺寸；无合适候选；比例未知 |
| 图框 | 闭合矩形多段线、图框块、带属性块、旋转框、非 1:1 缩放块、加长框、相邻框、重复框、非矩形轮廓 |
| 方向 | 横向、纵向、旋转 90°、非 90° 的图框角度 |
| 比例 | 1:1、1:50、1:100、自定义比例、属性读取失败、单位不明确 |
| 输出模式 | 每框一个 PDF；合并多页 PDF；任务排序后页序同步；混合纸张多页文件 |
| 输出错误 | 文件已存在、非法字符、路径太长、只读目录、PDF 被其他程序占用、磁盘空间不足 |
| 图形样式 | 当前 CTB、STB 图纸、黑白/彩色、线宽开关、中文文本和字体 |
| 恢复能力 | 单页失败、合并中断、用户取消、PlotEngine 异常、重试失败任务 |
| 源图保护 | 打印前后图元/图层/块属性不变；UCS、视图和系统变量恢复 |

### 首版验收标准

- 两个指定 AutoCAD 年份均通过集成测试，不声明其他年份兼容。
- 输出设备只能是 `DWG To PDF.pc3`，缺失时明确停止而非切换设备。
- 纸张列表和物理尺寸由 PC3/CAD API 动态提供。
- 自动匹配关闭时可以手动选择当前 PC3 支持的纸张。
- 自动匹配开启时纸张选择可解释；不能匹配时显式警告。
- 旋转图框边界准确，加长图纸不被裁切；打印结果要用真实 PDF 抽检。
- 独立 PDF 和合并 PDF 两种方式都能运行，合并模式的页序和纸张尺寸与清单一致。
- 任意单张失败均记录状态和日志，不能静默漏图或误覆盖。
- 打印结束后不遗留打印引擎状态，不永久改动当前视图/UCS/系统变量。
- 至少使用 30 张真实工程图纸，覆盖标准图框、块图框、旋转框、加长纸张及不同比例，完成首轮人工抽检。

## 12. 风险与应对

| 风险 | 应对方案 |
|---|---|
| 两版 DLL 运行时/API 差异 | 分别引用 2020/2022 Managed API；两个版本独立编译和测试 |
| 用户 PC3 缺失或改名 | 启动检测；严格不回退到其他设备；显示修复路径 |
| PC3 纸张列表因电脑配置不同 | 媒体列表运行时刷新，所有纸张用 canonical name 标识；不硬编码纸张清单 |
| 自动匹配错误 | 显示尺寸、比例来源、匹配差值；无精确候选时要求确认；预览抽检 |
| 任意角度旋转图框窗口不准确 | 在阶段 0/2 先验证 DCS 与视图扭转方法；未通过前不进入批量开发 |
| 加长纸张未配置 | 给出 PC3 配置提示；不自动选小纸张，也不自造介质 |
| 合并 PDF 中混合纸张失败 | 在两个目标 CAD 上验证 PlotEngine 多页流程和不同媒体；失败状态明确记录，不产出假成功结果 |
| 图框误识别 | 图框清单人工审核、可手动添加/移除、支持图层与块名规则 |
| 外部文件/API 依赖冲突 | 首版尽量只依赖 AutoCAD 自身打印能力；外部 PDF 库非必需，不提前引入 |
| 打印后 CAD 状态被改变 | 保存并恢复临时视图、UCS、系统变量；出图流程用 `try/finally` 清理资源 |

## 13. Codex 实施任务拆分（可按顺序逐条执行）

### Task 01 — 创建解决方案与双版本构建

- 建立 Core 项目和 AutoCAD 2020/2022 两个插件入口项目。
- 设置目标框架：2020 为 .NET Framework 4.7；2022 为 .NET Framework 4.8。
- 从各自 AutoCAD 安装目录引用 Managed API 程序集，`Copy Local=False`。
- 实现 `BPDFTEST`，验证两版 DLL 都可被 `NETLOAD` 加载。
- 暂不写打印界面和复杂图框算法。

**交付：** 两套 DLL、README 中的构建和加载步骤、两版实测结果。

### Task 02 — PC3 设备与媒体探测

- 实现 `PaperMediaCatalog`。
- 设备必须定位到运行环境中的 `DWG To PDF.pc3`。
- 调用 `RefreshLists`，枚举媒体 canonical name 和本地化显示名。
- 为每个媒体读取真实 `PlotPaperSize` 和 `PlotPaperMargins`。
- 在 `BPDFTEST` 中输出纸张列表和尺寸；设备不存在时输出可读诊断。

**交付：** 两个 AutoCAD 版本下的 PC3/纸张枚举结果，不打印错误时不能静默回退。

### Task 03 — 单张窗口打印

- 手动选择两个角点。
- 在临时 `PlotSettings` 中设置 PC3、媒体、窗口、比例、旋转、样式、线宽。
- 通过 `PlotInfoValidator` 验证后用 `PlotEngine` 输出单张 PDF。
- 确保成功、异常、取消都正确结束绘图引擎。

**交付：** 可复现的单张 PDF；人工核对比例、页面尺寸和裁切。

### Task 04 — 图框识别

- 实现闭合矩形多段线识别与去重。
- 实现图框块候选识别、变换和属性读取。
- 保留四角点、旋转角、宽高和识别警告。
- 实现手动框选补充。

**交付：** `FrameCandidate` 列表和一组可回归测试图。

### Task 05 — 自动/手动纸张匹配

- 主界面提供“自动匹配纸张大小”复选框。
- 勾选时按真实媒体尺寸、图框尺寸、单位、比例与方向匹配。
- 取消时显示 PC3 的实际媒体下拉列表。
- 缺失匹配项、比例未知、边距不足必须提示用户。

**交付：** 单元测试覆盖精确、旋转、加长、未知比例和无匹配媒体情况。

### Task 06 — 两种批量 PDF 输出

- 做每张图一个 PDF 的串行队列。
- 做一个多页 PDF 的 PlotEngine 队列。
- 页面排序、混合纸张尺寸和单页失败行为必须测试。
- 不得使用外部 PDF 引擎来替代用户指定的 PC3 出图流程。

**交付：** 独立输出样例和混合纸张多页 PDF 样例，附结果清单。

### Task 07 — 设置、日志与稳定化

- 任务表格、进度、错误状态、重试。
- JSON 配置、图框规则映射、命名规则和重名防护。
- 测试 CAD 环境及打印任务结束后的状态恢复。

**交付：** MVP 候选版、测试报告、已知限制列表。

### 给 Codex 的最高优先级规则

1. 每完成一个 Task 就编译、加载并验证，不要一次性生成整套系统后再找错误。
2. 第一优先级是 AutoCAD 2020 和 2022 的单张打印闭环。
3. 纸张媒体必须来自 `DWG To PDF.pc3` 动态枚举；禁止硬编码纸张作为真实数据源。
4. 旋转图框、加长纸张、合并多页 PDF 必须以真实 PDF 测试结果为验收，不接受只有代码层面的“理论支持”。
5. 所有不确定的纸张、比例或图框都显示需确认，避免错误出图。
6. 不要新增未要求的多 DWG、OCR、自动安装、Ribbon 或云端功能，除非用户后续明确提出。

## 14. 参考资料

1. Autodesk — [About Managed .NET Compatibility（含 AutoCAD 2020/2022 框架版本表）](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Customization/files/GUID-A6C680F2-DE2E-418A-A182-E4884073338A.htm)
2. Autodesk — [Paper Size and Units (.NET)](https://help.autodesk.com/cloudhelp/2026/DEU/OARX-DevGuide-Managed/files/GUID-946669A9-E813-4D12-828A-44986E388AA2.htm)
3. Autodesk — [Plot Device (.NET)](https://help.autodesk.com/cloudhelp/2021/ENU/OARX-DevGuide-Managed/files/GUID-96E2EFD0-B16B-4A3A-BA6A-A9277BBB3610.htm)
4. Autodesk — [PlotSettings.PlotPaperSize Property](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/GUID-Autodesk_AutoCAD_DatabaseServices_PlotSettings_PlotPaperSize.html)
5. Autodesk — [PlotSettingsValidator Methods](https://help.autodesk.com/cloudhelp/2024/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-__MEMBERTYPE_Methods_Autodesk_AutoCAD_DatabaseServices_PlotSettingsValidator.html)
6. Autodesk — [Plot Area (.NET)](https://help.autodesk.com/cloudhelp/2026/ESP/OARX-DevGuide-Managed/files/GUID-9F7F854D-5172-4F42-A6C5-E7E54DF6E0B6.htm)
7. Autodesk — [PlotterValidator.SetPlotWindowArea](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-ManagedRefGuide/files/OREFNET-Autodesk_AutoCAD_DatabaseServices_PlotSettingsValidator_SetPlotWindowArea_PlotSettings_Extents2d.html)
8. Autodesk — [PlotEngine Class](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/GUID-Autodesk_AutoCAD_PlottingServices_PlotEngine.html)
9. Autodesk — [How to add a new AutoCAD DWG To PDF PC3](https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/How-to-add-a-new-AutoCAD-DWG-to-PDF-PC3-driver.html)
10. Autodesk — [Cannot add custom paper size for system printers in AutoCAD](https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/Can-t-add-custom-paper-size-for-system-printer-in-AutoCAD.html)
11. Autodesk App Store — [Advanced Batch Plot](https://apps.autodesk.com/ACD/en/Detail/HelpDoc?appId=3786088757909240118&appLang=en&os=Win64)
12. 中望开发者生态 — [易出图](https://zdn.zwsoft.cn/index.php/home/product/detail/id/145.html)
13. 产品说明 — [CAD 批量打印插件使用说明](https://cad-auto-print.cn/CAD%E6%89%B9%E9%87%8F%E6%89%93%E5%8D%B0%E6%8F%92%E4%BB%B6-%E4%BD%BF%E7%94%A8%E8%AF%B4%E6%98%8E.html)
14. GitHub — [BPPlot](https://github.com/hwdemtv/batchplot-plugin)
15. GitHub — [LA 批量打印](https://github.com/Edwardhehe/batchPrintZWCAD)
