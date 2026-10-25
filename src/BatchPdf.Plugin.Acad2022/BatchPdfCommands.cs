using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using BatchPdf.AutoCAD.Services;
using BatchPdf.Core.Configuration;
using BatchPdf.Core.Models;
using BatchPdf.Core.Services;
using BatchPdf.Infrastructure.Services;
using BatchPdf.Plot.Services;
using BatchPdf.UI.Forms;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace BatchPdf.Plugin
{
    public class BatchPdfCommands
    {
        private static BatchPdfForm _activeForm;

        /// <summary>
        /// Opens the main Batch PDF WinForms dialog in Modeless mode so the user can freely interact with the CAD drawing.
        /// </summary>
        [CommandMethod("BPDF", CommandFlags.Modal)]
        [CommandMethod("BPLOT", CommandFlags.Modal)]
        public void OpenBatchPdfDialog()
        {
            Document doc = CoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            // Ensure top-level menu is up to date
            try { CadMenuService.EnsureMenuInstalled(); } catch { }

            var configService = new ConfigService();
            var logService = new LogService();

            try
            {
                if (_activeForm != null && !_activeForm.IsDisposed)
                {
                    if (_activeForm.WindowState == FormWindowState.Minimized)
                    {
                        _activeForm.WindowState = FormWindowState.Normal;
                    }
                    _activeForm.AttachToDocument(doc);
                    _activeForm.Show();
                    _activeForm.Activate();
                    return;
                }

                _activeForm = new BatchPdfForm(doc, configService, logService);
                _activeForm.FormClosed += (s, e) => _activeForm = null;
                AcadApp.ShowModelessDialog(AcadApp.MainWindow.Handle, _activeForm, false);
            }
            catch (System.Exception ex)
            {
                logService.LogError("BPDFCommandError", ex.Message, ex);
                doc.Editor.WriteMessage("\n[BatchPdf] 打开主界面失败: " + ex.Message + "\n");
            }
        }

        /// <summary>
        /// Internal command bridge invoked by the Modeless BatchPdfForm when interactive Editor selection or PlotEngine execution requires Command Context.
        /// </summary>
        [CommandMethod("BPDF_EXEC", CommandFlags.Modal | CommandFlags.NoHistory | CommandFlags.NoUndoMarker)]
        public void ExecuteModelessCommandBridge()
        {
            BatchPdfForm.ExecutePendingCommandAction();
        }

        /// <summary>
        /// Scans current DWG model space for closed rectangular polylines and frame blocks and outputs summary to command line.
        /// </summary>
        [CommandMethod("BPDFSCAN", CommandFlags.Modal)]
        public void ScanFramesCommand()
        {
            Document doc = CoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            var configService = new ConfigService();
            var settings = configService.LoadSettings();
            var catalog = new PaperMediaCatalog();
            var mediaList = catalog.RefreshMediaList(out DeviceCheckResult devCheck);

            var scanner = new FrameScanner();
            var prep = new SheetPreparationService();

            var candidates = scanner.ScanModelSpaceFrames(doc.Database, settings);
            DrawingUnits units = FrameScanner.ReadDatabaseDrawingUnits(doc.Database);
            var sheets = prep.CreatePrintSheets(candidates, doc.Name, units, settings, mediaList);

            var outLines = new List<string>();
            outLines.Add("================ [BPDFSCAN 图框扫描结果] ================");
            outLines.Add(string.Format("当前图纸: {0} | 单位: {1} | 识别图框总数: {2}",
                doc.Name, units.ToDisplayText(), sheets.Count));

            foreach (var s in sheets)
            {
                outLines.Add(string.Format(
                    "  #{0:D2} | 图层:{1} | 类型:{2} | 句柄:{3} | 尺寸:{4} | 图号:{5} | 图名:{6} | 比例:{7} | 纸张:{8} | 文件名:{9} | 状态:{10}",
                    s.OrderIndex,
                    s.LayerName,
                    s.FrameType,
                    s.FrameHandle,
                    s.FormattedGeometryInfo,
                    s.DrawingNumber,
                    s.DrawingName,
                    s.FormattedScaleInfo,
                    string.IsNullOrEmpty(s.TargetPaperCanonicalName) ? "(未匹配)" : s.TargetPaperLocaleName,
                    s.OutputFileName,
                    s.FormattedStatusSummary));
            }
            outLines.Add("========================================================");

            foreach (string l in outLines)
            {
                ed.WriteMessage("\n" + l);
            }
            ed.WriteMessage("\n");
        }

        /// <summary>
        /// Diagnostic and single-sheet test command (Task 01/02/03):
        /// 1. Checks DWG To PDF.pc3 availability and enumerates all paper media with physical dimensions and margins.
        /// 2. Allows user to pick a window (or uses existing/generated frame) to plot a single test PDF.
        /// </summary>
        [CommandMethod("BPDFTEST", CommandFlags.Modal)]
        public void RunDiagnosticAndSinglePlotTest()
        {
            Document doc = CoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            var logService = new LogService();

            ed.WriteMessage("\n================ [BPDFTEST 诊断与单张打印测试] ================");
            var catalog = new PaperMediaCatalog();
            List<PaperMedia> mediaList = catalog.RefreshMediaList(out DeviceCheckResult devResult);

            if (!devResult.IsAvailable)
            {
                ed.WriteMessage("\n[错误] " + devResult.DiagnosticMessage);
                ed.WriteMessage("\n当前 AutoCAD 枚举到的绘图设备列表: " + string.Join(", ", devResult.AllAvailableDevices));
                ed.WriteMessage("\n==============================================================\n");
                return;
            }

            ed.WriteMessage(string.Format("\n[成功] 已定位固定设备: {0}", devResult.MatchedDeviceName));
            ed.WriteMessage(string.Format("\n[成功] 动态读取到可用纸张媒体数量: {0}", mediaList.Count));

            int previewCount = Math.Min(15, mediaList.Count);
            for (int i = 0; i < previewCount; i++)
            {
                var m = mediaList[i];
                ed.WriteMessage(string.Format(
                    "\n  [{0:D2}] Canonical=\"{1}\" | Locale=\"{2}\" | 尺寸={3:F1}x{4:F1}mm | 可打印区域={5:F1}x{6:F1}mm | 边距=({7})",
                    i + 1, m.CanonicalName, m.LocaleName, m.WidthMm, m.HeightMm,
                    m.PrintableWidthMm, m.PrintableHeightMm, m.MarginsMm));
            }
            if (mediaList.Count > previewCount)
            {
                ed.WriteMessage(string.Format("\n  ... (其余 {0} 种纸张已省略，完整列表见主界面下拉框)", mediaList.Count - previewCount));
            }

            // Prompt user whether to pick two corners for single-sheet test plot
            var pko = new PromptKeywordOptions("\n是否框选两个角点执行单张测试打印? [是(Y)/否(N)] <Y>: ", "Yes No");
            pko.Keywords.Default = "Yes";
            pko.AllowNone = true;
            PromptResult pkr = ed.GetKeywords(pko);
            if (pkr.Status == PromptStatus.OK && string.Equals(pkr.StringResult, "No", StringComparison.OrdinalIgnoreCase))
            {
                ed.WriteMessage("\n已完成 PC3 与纸张媒体诊断。\n");
                return;
            }

            var interaction = new CadInteractionService();
            FrameCandidate picked = interaction.PromptManualWindowSelection(doc);
            if (picked == null)
            {
                ed.WriteMessage("\n未框选范围，单张测试打印已结束。\n");
                return;
            }

            var configService = new ConfigService();
            var settings = configService.LoadSettings();
            var prep = new SheetPreparationService();
            DrawingUnits units = FrameScanner.ReadDatabaseDrawingUnits(doc.Database);

            var sheets = prep.CreatePrintSheets(new List<FrameCandidate> { picked }, doc.Name, units, settings, mediaList);
            PrintSheet testSheet = sheets[0];

            // If auto-match didn't find exact paper for arbitrary manual box, use FitToPaper + A4/A3 for diagnostic test
            if (string.IsNullOrEmpty(testSheet.TargetPaperCanonicalName))
            {
                PaperMedia fallbackMedia = mediaList.FirstOrDefault(m =>
                    m.LocaleName.IndexOf("A3", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.LocaleName.IndexOf("A4", StringComparison.OrdinalIgnoreCase) >= 0) ?? mediaList[0];
                SheetPreparationService.ApplyMediaToSheet(testSheet, fallbackMedia);
                testSheet.FitToPaper = true;
                testSheet.Status = SheetStatus.Ready;
                ed.WriteMessage(string.Format("\n[提示] 手动框选为任意尺寸，测试采用纸张: {0} (FitToPaper)", fallbackMedia.DisplayLabel));
            }
            else
            {
                ed.WriteMessage(string.Format("\n[提示] 自动匹配纸张: {0} | 比例: {1}", testSheet.TargetPaperLocaleName, testSheet.FormattedScaleInfo));
            }

            testSheet.OutputFileName = string.Format("BPDFTEST_{0:yyyyMMdd_HHmmss}.pdf", DateTime.Now);
            var runner = new PlotJobRunner(catalog, new PlotSettingsBuilder(catalog), new NamingService(), logService);
            JobResult job = runner.PlotEachSheetToPdf(doc, new List<PrintSheet> { testSheet }, settings);

            if (job.SucceededCount == 1 && job.SheetResults.Count > 0)
            {
                ed.WriteMessage("\n[成功] 单张测试 PDF 已生成: " + job.SheetResults[0].OutputFilePath + "\n");
            }
            else
            {
                string err = job.FatalError ?? (job.SheetResults.FirstOrDefault()?.ErrorMessage ?? "未知错误");
                ed.WriteMessage("\n[失败] 单张测试 PDF 输出失败: " + err + "\n");
            }
        }

        /// <summary>
        /// Opens the Frame Rules and default settings configuration dialog.
        /// </summary>
        [CommandMethod("BPDFSET", CommandFlags.Modal)]
        public void OpenSettingsCommand()
        {
            Document doc = CoreApp.DocumentManager.MdiActiveDocument;
            var configService = new ConfigService();
            var settings = configService.LoadSettings();

            using (var dlg = new FrameRulesForm(settings))
            {
                if (AcadApp.ShowModalDialog(dlg) == DialogResult.OK)
                {
                    configService.SaveSettings(settings);
                    doc?.Editor.WriteMessage("\n[BatchPdf] 已保存图框识别与属性映射设置至: " + configService.SettingsFilePath + "\n");
                }
            }
        }

        /// <summary>
        /// Opens the latest log file or log directory.
        /// </summary>
        [CommandMethod("BPDFLOG", CommandFlags.Modal)]
        public void OpenLogCommand()
        {
            Document doc = CoreApp.DocumentManager.MdiActiveDocument;
            var logService = new LogService();
            string logPath = logService.CurrentLogFilePath;

            doc?.Editor.WriteMessage("\n[BatchPdf] 日志文件路径: " + logPath + "\n");
            try
            {
                if (File.Exists(logPath))
                {
                    Process.Start("notepad.exe", logPath);
                }
                else if (Directory.Exists(logService.LogDirectory))
                {
                    Process.Start("explorer.exe", logService.LogDirectory);
                }
            }
            catch (System.Exception ex)
            {
                doc?.Editor.WriteMessage("\n[BatchPdf] 打开日志文件失败: " + ex.Message + "\n");
            }
        }

        /// <summary>
        /// Command to refresh or re-inject the CADBPLOT menu into the AutoCAD MenuBar.
        /// </summary>
        [CommandMethod("BPDFMENU", CommandFlags.Modal)]
        public void ReinstallMenuCommand()
        {
            var doc = CoreApp.DocumentManager.MdiActiveDocument;
            try
            {
                CadMenuService.EnsureMenuInstalled();
                doc?.Editor.WriteMessage("\n[CAD批量打印] 顶部菜单栏已成功刷新修复。\n");
            }
            catch (System.Exception ex)
            {
                doc?.Editor.WriteMessage("\n[CAD批量打印] 菜单栏刷新异常: " + ex.Message + "\n");
            }
        }

        /// <summary>
        /// Single-sheet vector print preview using AutoCAD native PlotFactory.CreatePreviewEngine.
        /// User can pick a frame on screen, or press Enter to automatically preview the largest/first frame.
        /// </summary>
        [CommandMethod("BPPREVIEW", CommandFlags.Modal)]
        public void PreviewSingleSheetCommand()
        {
            Document doc = CoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            var configService = new ConfigService();
            var settings = configService.LoadSettings();
            var catalog = new PaperMediaCatalog();
            var mediaList = catalog.RefreshMediaList(out DeviceCheckResult devCheck);

            if (!devCheck.IsAvailable || mediaList.Count == 0)
            {
                ed.WriteMessage("\n[BPPREVIEW 错误] DWG To PDF.pc3 打印设备不可用: " + devCheck.DiagnosticMessage + "\n");
                return;
            }

            var peo = new PromptEntityOptions("\n请点选要预览的图框 (直接回车 = 自动识别当前最大图框): ");
            peo.AllowNone = true;
            PromptEntityResult per = ed.GetEntity(peo);

            var scanner = new FrameScanner();
            var prep = new SheetPreparationService();
            DrawingUnits units = FrameScanner.ReadDatabaseDrawingUnits(doc.Database);
            List<PrintSheet> sheets;

            if (per.Status == PromptStatus.OK)
            {
                var candidates = scanner.ExtractCandidatesFromObjectIds(doc.Database, new List<ObjectId> { per.ObjectId }, settings);
                if (candidates.Count == 0)
                {
                    ed.WriteMessage("\n[BPPREVIEW] 点选的图元未匹配为有效图框。\n");
                    return;
                }
                sheets = prep.CreatePrintSheets(candidates, doc.Name, units, settings, mediaList);
            }
            else if (per.Status == PromptStatus.None)
            {
                var candidates = scanner.ScanModelSpaceFrames(doc.Database, settings);
                if (candidates.Count == 0)
                {
                    ed.WriteMessage("\n[BPPREVIEW] 模型空间中未识别到任何图框。\n");
                    return;
                }
                sheets = prep.CreatePrintSheets(candidates, doc.Name, units, settings, mediaList);
            }
            else
            {
                return;
            }

            var targetSheet = sheets.OrderByDescending(s => s.FrameWidthUnits * s.FrameHeightUnits).FirstOrDefault(s => s.Status == SheetStatus.Ready)
                              ?? sheets.OrderByDescending(s => s.FrameWidthUnits * s.FrameHeightUnits).FirstOrDefault();

            if (targetSheet == null)
            {
                ed.WriteMessage("\n[BPPREVIEW] 未找到可用图框。\n");
                return;
            }

            if (string.IsNullOrWhiteSpace(targetSheet.TargetPaperCanonicalName))
            {
                var fallback = mediaList.FirstOrDefault(m => m.LocaleName.IndexOf("A3", StringComparison.OrdinalIgnoreCase) >= 0) ?? mediaList[0];
                SheetPreparationService.ApplyMediaToSheet(targetSheet, fallback);
                targetSheet.FitToPaper = true;
            }

            ed.WriteMessage(string.Format("\n[BPPREVIEW] 正在调起打印预览: #{0} {1} {2} | 纸张: {3} | 比例: {4} ... (按 ESC 退出预览)\n",
                targetSheet.OrderIndex, targetSheet.DrawingNumber, targetSheet.DrawingName,
                targetSheet.TargetPaperLocaleName, targetSheet.FormattedScaleInfo));

            var runner = new PlotJobRunner(catalog, new PlotSettingsBuilder(catalog), new NamingService(), new LogService());
            try
            {
                runner.PreviewSingleSheet(doc, targetSheet, settings);
                ed.WriteMessage("\n[BPPREVIEW] 预览已正常结束。\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[BPPREVIEW 失败] " + ex.Message + "\n");
            }
        }

        /// <summary>
        /// Interactive frame block teaching: User clicks an existing title block in the drawing,
        /// and the plugin automatically learns the block name and attribute tags (DrawingNo, DrawingTitle, Scale, etc.),
        /// updating the configuration so that all subsequent scans recognize this frame style.
        /// </summary>
        [CommandMethod("BPTEACH", CommandFlags.Modal)]
        public void TeachFrameBlockCommand()
        {
            Document doc = CoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            var peo = new PromptEntityOptions("\n请在图纸中点选一个图框块: ");
            peo.SetRejectMessage("\n请点选一个图框块参照 (BlockReference)。");
            peo.AddAllowedClass(typeof(BlockReference), true);
            PromptEntityResult per = ed.GetEntity(peo);

            if (per.Status != PromptStatus.OK) return;

            var configService = new ConfigService();
            var settings = configService.LoadSettings();
            if (settings.FrameRules == null || settings.FrameRules.Count == 0)
            {
                settings.FrameRules = BatchPdfSettings.CreateDefault().FrameRules;
            }
            var rule = settings.FrameRules[0];

            string effectiveBlockName;
            var numTags = new List<string>();
            var nameTags = new List<string>();
            var scaleTags = new List<string>();
            var allTags = new List<string>();

            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var br = tr.GetObject(per.ObjectId, OpenMode.ForRead) as BlockReference;
                if (br == null)
                {
                    ed.WriteMessage("\n[BPTEACH] 所选图元不是图块。闭合多段线矩形图框已默认支持自动识别，无需学习。\n");
                    return;
                }

                effectiveBlockName = FrameScanner.GetEffectiveBlockName(tr, br);

                foreach (ObjectId attId in br.AttributeCollection)
                {
                    var ar = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                    if (ar == null || string.IsNullOrWhiteSpace(ar.Tag)) continue;

                    string tag = ar.Tag.Trim();
                    allTags.Add(tag);
                    string upper = tag.ToUpperInvariant();

                    if (IsDrawingNumberTag(tag, upper))
                    {
                        if (!numTags.Contains(tag)) numTags.Add(tag);
                    }
                    else if (IsDrawingNameTag(tag, upper))
                    {
                        if (!nameTags.Contains(tag)) nameTags.Add(tag);
                    }
                    else if (IsScaleTag(tag, upper))
                    {
                        if (!scaleTags.Contains(tag)) scaleTags.Add(tag);
                    }
                }
                tr.Commit();
            }

            bool changed = false;
            if (!rule.BlockNames.Any(b => string.Equals(b, effectiveBlockName, StringComparison.OrdinalIgnoreCase)))
            {
                rule.BlockNames.Add(effectiveBlockName);
                changed = true;
            }

            foreach (var t in numTags)
            {
                if (!rule.SheetNumberTags.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                {
                    rule.SheetNumberTags.Add(t);
                    changed = true;
                }
            }

            foreach (var t in nameTags)
            {
                if (!rule.SheetNameTags.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                {
                    rule.SheetNameTags.Add(t);
                    changed = true;
                }
            }

            foreach (var t in scaleTags)
            {
                if (!rule.ScaleTags.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                {
                    rule.ScaleTags.Add(t);
                    changed = true;
                }
            }

            if (changed)
            {
                configService.SaveSettings(settings);
            }

            ed.WriteMessage("\n================ [BPTEACH 图框学习成功] ================");
            ed.WriteMessage(string.Format("\n  图框块名: [{0}]", effectiveBlockName));
            ed.WriteMessage(string.Format("\n  识别图号属性: {0}", numTags.Count > 0 ? string.Join(", ", numTags) : "(未检测到)"));
            ed.WriteMessage(string.Format("\n  识别图名属性: {0}", nameTags.Count > 0 ? string.Join(", ", nameTags) : "(未检测到)"));
            ed.WriteMessage(string.Format("\n  识别比例属性: {0}", scaleTags.Count > 0 ? string.Join(", ", scaleTags) : "(未检测到)"));
            ed.WriteMessage(string.Format("\n  包含全部标签: {0}", string.Join(", ", allTags)));
            ed.WriteMessage(string.Format("\n  设置已保存至: {0}", configService.SettingsFilePath));
            ed.WriteMessage("\n  后续执行 BPDF / BPDFSCAN 或无头打印将自动识别此类图框。");
            ed.WriteMessage("\n=======================================================\n");
        }

        private static bool IsDrawingNumberTag(string tag, string upper)
        {
            if (upper.Contains("TITLE") || upper.Contains("NAME") || tag.Contains("图名") || tag.Contains("名称")) return false;
            return tag.Contains("图号") || tag.Contains("编号") || upper.Contains("DWGNO") || upper.Contains("DWG_NO") ||
                   upper.Contains("SHEETNO") || upper.Contains("SHEET_NO") || upper.Contains("DRAWINGNO") || upper.Contains("DRAWING_NO") ||
                   upper == "NO" || upper == "NUM";
        }

        private static bool IsDrawingNameTag(string tag, string upper)
        {
            if (upper.Contains("FILE") || upper.Contains("BLOCK") || upper.Contains("DWGNO")) return false;
            return tag.Contains("图名") || tag.Contains("名称") || upper.Contains("TITLE") || upper.Contains("NAME") ||
                   upper.Contains("DWGNAME") || upper.Contains("DWG_NAME") || upper.Contains("SHEETNAME") || upper.Contains("SHEET_NAME") ||
                   upper.Contains("DRAWINGNAME") || upper.Contains("DRAWING_NAME");
        }

        private static bool IsScaleTag(string tag, string upper)
        {
            return tag.Contains("比例") || upper.Contains("SCALE") || upper == "BL";
        }

        /// <summary>
        /// Generates an AutoCAD native Table entity (AcDbTable) listing all recognized drawings and sheet info.
        /// </summary>
        [CommandMethod("BPTABLE", CommandFlags.Modal)]
        public void GenerateDrawingIndexTableCommand()
        {
            Document doc = CoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            var configService = new ConfigService();
            var settings = configService.LoadSettings();
            var catalog = new PaperMediaCatalog();
            var mediaList = catalog.RefreshMediaList(out DeviceCheckResult devCheck);

            var scanner = new FrameScanner();
            var prep = new SheetPreparationService();
            DrawingUnits units = FrameScanner.ReadDatabaseDrawingUnits(doc.Database);

            List<PrintSheet> sheets;
            using (doc.LockDocument())
            {
                var candidates = scanner.ScanModelSpaceFrames(doc.Database, settings);
                sheets = prep.CreatePrintSheets(candidates, doc.Name, units, settings, mediaList);
            }

            if (sheets == null || sheets.Count == 0)
            {
                ed.WriteMessage("\n[BPTABLE] 当前模型空间未识别到任何图框，无法生成图纸目录表。\n");
                return;
            }

            var ppo = new PromptPointOptions("\n请指定图纸目录左上角插入基点: ");
            PromptPointResult ppr = ed.GetPoint(ppo);
            if (ppr.Status != PromptStatus.OK) return;

            if (CadTableService.GenerateDrawingIndexTable(doc, sheets, settings, ppr.Value, out string msg))
            {
                ed.WriteMessage("\n[BPTABLE 成功] " + msg + "\n");
            }
            else
            {
                ed.WriteMessage("\n[BPTABLE 失败] " + msg + "\n");
            }
        }

        /// <summary>
        /// Automated end-to-end regression test command for AutoCAD 2022/2020 verification:
        /// Creates standard polylines, block frames with attributes, rotated frames (30 deg & 90 deg),
        /// lengthened frames, inner duplicate borders, and distractor entities in a scratch DWG,
        /// then verifies PC3 media catalog, FrameScanner, PaperMatcher, Single-PDF output,
        /// Multi-page Mixed-Paper PDF output, and source DWG state preservation.
        /// </summary>
        [CommandMethod("BPDFREGTEST", CommandFlags.Modal)]
        public void RunAutomatedRegressionSuite()
        {
            Document doc = CoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            string testRoot = Path.Combine(
                Path.GetDirectoryName(typeof(BatchPdfCommands).Assembly.Location) ?? Environment.CurrentDirectory,
                "RegressionOutput");
            Directory.CreateDirectory(testRoot);

            string reportPath = Path.Combine(testRoot, "RegressionReport.txt");
            var reportLines = new List<string>();
            void LogLine(string line)
            {
                ed.WriteMessage("\n" + line);
                reportLines.Add(line);
            }

            try
            {
                LogLine("=== BatchPdf AutoCAD 2022 End-to-End Regression Test ===");
                LogLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                // Step 1: Verify DWG To PDF.pc3 & Dynamic Media Catalog
                var catalog = new PaperMediaCatalog();
                var mediaList = catalog.RefreshMediaList(out DeviceCheckResult devCheck);
                LogLine(string.Format("[Step 1] Device Check: IsAvailable={0}, MatchedDevice=\"{1}\", MediaCount={2}",
                    devCheck.IsAvailable, devCheck.MatchedDeviceName, mediaList.Count));

                if (!devCheck.IsAvailable || mediaList.Count == 0)
                {
                    LogLine("[FAIL] DWG To PDF.pc3 unavailable: " + devCheck.DiagnosticMessage);
                    File.WriteAllLines(reportPath, reportLines);
                    return;
                }

                // Log sample media
                foreach (var m in mediaList.Take(10))
                {
                    LogLine(string.Format("  Media: Canonical=\"{0}\" | Locale=\"{1}\" | {2:F1}x{3:F1}mm | Margins={4}",
                        m.CanonicalName, m.LocaleName, m.WidthMm, m.HeightMm, m.MarginsMm));
                }

                // Step 2: Populate current test drawing with diverse test frames
                PopulateRegressionTestEntities(db);
                string savedTestDwg = Path.Combine(testRoot, "TestFrames_Acad2022.dwg");
                try { db.SaveAs(savedTestDwg, DwgVersion.Current); } catch { }
                LogLine("[Step 2] Created test frames in DWG: " + savedTestDwg);

                // Record pre-plot state (entity count, view twist, UCS)
                int entityCountBefore = CountModelSpaceEntities(db);
                double viewTwistBefore;
                using (ViewTableRecord v = ed.GetCurrentView())
                {
                    viewTwistBefore = v.ViewTwist;
                }

                // Step 3: Scan Model Space Frames
                var settings = BatchPdfSettings.CreateDefault();
                settings.OutputDirectory = testRoot;
                var scanner = new FrameScanner();
                var candidates = scanner.ScanModelSpaceFrames(db, settings.FrameRules);
                LogLine(string.Format("[Step 3] Scanned Candidates Count: {0} (Expected 5 valid frames; inner border & small box filtered)", candidates.Count));

                var prep = new SheetPreparationService();
                var sheets = prep.CreatePrintSheets(candidates, savedTestDwg, DrawingUnits.Millimeters, settings, mediaList);

                foreach (var s in sheets)
                {
                    LogLine(string.Format(
                        "  Sheet #{0}: Type={1} | Size={2:F0}x{3:F0} | Rot={4:F1}deg | Scale={5} | Paper={6} ({7:F0}x{8:F0}) | File={9} | Status={10}",
                        s.OrderIndex, s.FrameType, s.FrameWidthUnits, s.FrameHeightUnits, s.RotationAngleDeg,
                        s.FormattedScaleInfo, s.TargetPaperCanonicalName, s.PaperWidthMm, s.PaperHeightMm,
                        s.OutputFileName, s.FormattedStatusSummary));
                }

                // Step 4: Plot Ready Sheets in Single-PDF-Per-Sheet Mode (includes standard polyline, block frame, 30-deg rotated frame, 90-deg portrait frame)
                var readySheets = sheets.Where(s => s.Status == SheetStatus.Ready).ToList();
                var logService = new LogService(Path.Combine(testRoot, "Logs"));
                var runner = new PlotJobRunner(catalog, new PlotSettingsBuilder(catalog), new NamingService(), logService);

                string singleOutDir = Path.Combine(testRoot, "SinglePdfs");
                settings.OutputDirectory = singleOutDir;
                JobResult singleJob = runner.PlotEachSheetToPdf(doc, readySheets, settings);
                LogLine(string.Format("[Step 4] Single PDF Mode: Total={0}, Succeeded={1}, Failed={2}",
                    singleJob.TotalCount, singleJob.SucceededCount, singleJob.FailedCount));

                foreach (var sr in singleJob.SheetResults)
                {
                    long bytes = (!string.IsNullOrEmpty(sr.OutputFilePath) && File.Exists(sr.OutputFilePath))
                        ? new FileInfo(sr.OutputFilePath).Length
                        : 0;
                    LogLine(string.Format("  -> Single Output #{0}: Status={1} | Size={2} bytes | Path={3} | Err={4}",
                        sr.OrderIndex, sr.Status, bytes, sr.OutputFilePath, sr.ErrorMessage));
                }

                // Step 5: Plot Ready Sheets in Merged Multi-Page PDF Mode (mixed paper sizes: A1 + A2 + A3 + Rotated A1)
                foreach (var s in readySheets)
                {
                    s.Status = SheetStatus.Ready;
                    s.ErrorMessage = null;
                }
                string mergedPdfPath = Path.Combine(testRoot, "Merged_MixedPaper_Test.pdf");
                JobResult mergedJob = runner.PlotMultiSheetPdf(doc, readySheets, mergedPdfPath, settings);
                long mergedBytes = File.Exists(mergedPdfPath) ? new FileInfo(mergedPdfPath).Length : 0;
                LogLine(string.Format("[Step 5] Merged Multi-Page PDF Mode: Total={0}, Succeeded={1}, Failed={2}, Complete={3}, Bytes={4}",
                    mergedJob.TotalCount, mergedJob.SucceededCount, mergedJob.FailedCount, mergedJob.MergedFileIsComplete, mergedBytes));

                // Step 6: Verify Source DWG Protection (entity count & view twist restored)
                int entityCountAfter = CountModelSpaceEntities(db);
                double viewTwistAfter;
                using (ViewTableRecord v = ed.GetCurrentView())
                {
                    viewTwistAfter = v.ViewTwist;
                }
                LogLine(string.Format("[Step 6] Source Protection: Entities Before={0}, After={1} | ViewTwist Before={2:F4}, After={3:F4}",
                    entityCountBefore, entityCountAfter, viewTwistBefore, viewTwistAfter));

                LogLine("=== Regression Suite Finished ===");
            }
            catch (System.Exception ex)
            {
                LogLine("[EXCEPTION] " + ex);
            }

            File.WriteAllLines(reportPath, reportLines);
        }

        private static void PopulateRegressionTestEntities(Database db)
        {
            db.Insunits = UnitsValue.Millimeters;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Ensure Standard TextStyle uses TrueType font (SimSun / Microsoft YaHei) so Chinese characters render properly in PDF
                TextStyleTable tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                if (tst.Has("Standard"))
                {
                    TextStyleTableRecord stdStyle = (TextStyleTableRecord)tr.GetObject(tst["Standard"], OpenMode.ForWrite);
                    stdStyle.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor("SimSun", false, false, 134, 2);
                }

                // Clear existing entities in scratch drawing
                foreach (ObjectId id in ms)
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    ent?.Erase();
                }

                // 1. Frame 1: Closed rectangular Polyline A1 Landscape (841 x 594) at (0, 2000) + concentric inner border (811 x 574)
                AddRectanglePolyline(ms, tr, new Point2d(0, 2000), 841, 594, 0.0);
                AddRectanglePolyline(ms, tr, new Point2d(15, 2010), 811, 574, 0.0); // Inner border -> should be deduplicated
                AddCrossMarkerAndText(ms, tr, new Point2d(420.5, 2297), 300, 0.0, "SHEET 1: A1 LANDSCAPE POLYLINE 841x594");

                // 2. Frame 2: BlockReference TitleBlock A2 Landscape (59400 x 42000 at 1:100 scale) at (1500, 2000)
                ObjectId frameBlockId = EnsureTitleBlockDefinition(bt, tr, "COMPANY_FRAME_A2", 594.0, 420.0);
                InsertTitleBlockReference(
                    ms, tr, frameBlockId,
                    new Point3d(1500, 2000, 0),
                    scaleFactor: 100.0,
                    rotationRad: 0.0,
                    drawingNo: "S-01",
                    drawingName: "二层结构布置图",
                    scaleText: "1:100");

                // 3. Frame 3: Closed rectangular Polyline A3 Portrait (297 x 420) at (0, 0)
                AddRectanglePolyline(ms, tr, new Point2d(0, 0), 297, 420, 0.0);
                AddCrossMarkerAndText(ms, tr, new Point2d(148.5, 210), 120, 0.0, "SHEET 3: A3 PORTRAIT 297x420");

                // 4. Frame 4: Oblique Rotated (30 degrees) A1 Polyline (841 x 594) at (1200, 0)
                double rot30Rad = 30.0 * Math.PI / 180.0;
                AddRectanglePolyline(ms, tr, new Point2d(1200, 0), 841, 594, rot30Rad);
                Point2D rotCenter = new Point2D(1200 + 420.5, 297).RotateAround(new Point2D(1200, 0), rot30Rad);
                AddCrossMarkerAndText(ms, tr, new Point2d(rotCenter.X, rotCenter.Y), 250, rot30Rad, "SHEET 4: ROTATED 30 DEG A1 841x594");

                // 5. Frame 5: Rotated BlockReference (35 degrees) A3 Landscape (42000 x 29700 at 1:100) at (95000, 2000) (well separated from Frame 2)
                ObjectId frameBlockA3Id = EnsureTitleBlockDefinition(bt, tr, "COMPANY_FRAME_A3", 420.0, 297.0);
                double rot35Rad = 35.0 * Math.PI / 180.0;
                InsertTitleBlockReference(
                    ms, tr, frameBlockA3Id,
                    new Point3d(95000, 2000, 0),
                    scaleFactor: 100.0,
                    rotationRad: rot35Rad,
                    drawingNo: "J-02",
                    drawingName: "NodeDetail35Deg",
                    scaleText: "1:100");

                // 6. Frame 6: Unmatched Lengthened Frame (e.g. 1682 x 594) at (3000, 0) -> should be flagged NeedsConfirmation if PC3 lacks 1682x594
                AddRectanglePolyline(ms, tr, new Point2d(3000, 0), 1682, 594, 0.0);
                AddCrossMarkerAndText(ms, tr, new Point2d(3841, 297), 250, 0.0, "SHEET 6: LENGTHENED 1682x594");

                // 7. Distractor small symbol rectangle (50 x 30) -> should be filtered out by MinFrameWidth/MinFrameHeight
                AddRectanglePolyline(ms, tr, new Point2d(-300, 0), 50, 30, 0.0);

                tr.Commit();
            }
        }

        private static void AddRectanglePolyline(
            BlockTableRecord ms,
            Transaction tr,
            Point2d baseOrigin,
            double width,
            double height,
            double rotationRad)
        {
            var origin = new Point2D(baseOrigin.X, baseOrigin.Y);
            Point2D p0 = origin;
            Point2D p1 = new Point2D(baseOrigin.X + width, baseOrigin.Y).RotateAround(origin, rotationRad);
            Point2D p2 = new Point2D(baseOrigin.X + width, baseOrigin.Y + height).RotateAround(origin, rotationRad);
            Point2D p3 = new Point2D(baseOrigin.X, baseOrigin.Y + height).RotateAround(origin, rotationRad);

            var pl = new Polyline(4);
            pl.AddVertexAt(0, new Point2d(p0.X, p0.Y), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(p1.X, p1.Y), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(p2.X, p2.Y), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(p3.X, p3.Y), 0, 0, 0);
            pl.Closed = true;

            ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
        }

        private static void AddCrossMarkerAndText(
            BlockTableRecord ms,
            Transaction tr,
            Point2d center,
            double size,
            double rotationRad,
            string label)
        {
            var circle = new Circle(new Point3d(center.X, center.Y, 0), Vector3d.ZAxis, size * 0.35);
            ms.AppendEntity(circle);
            tr.AddNewlyCreatedDBObject(circle, true);

            var text = new DBText
            {
                Position = new Point3d(center.X - size * 0.6, center.Y, 0),
                Height = size * 0.08,
                TextString = label,
                Rotation = rotationRad
            };
            ms.AppendEntity(text);
            tr.AddNewlyCreatedDBObject(text, true);
        }

        private static ObjectId EnsureTitleBlockDefinition(BlockTable bt, Transaction tr, string blockName, double width, double height)
        {
            if (bt.Has(blockName))
            {
                return bt[blockName];
            }

            var btr = new BlockTableRecord { Name = blockName };
            ObjectId btrId = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);

            // Outer border polyline
            var outer = new Polyline(4);
            outer.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
            outer.AddVertexAt(1, new Point2d(width, 0), 0, 0, 0);
            outer.AddVertexAt(2, new Point2d(width, height), 0, 0, 0);
            outer.AddVertexAt(3, new Point2d(0, height), 0, 0, 0);
            outer.Closed = true;
            btr.AppendEntity(outer);
            tr.AddNewlyCreatedDBObject(outer, true);

            // Inner border polyline
            var inner = new Polyline(4);
            inner.AddVertexAt(0, new Point2d(25, 10), 0, 0, 0);
            inner.AddVertexAt(1, new Point2d(width - 10, 10), 0, 0, 0);
            inner.AddVertexAt(2, new Point2d(width - 10, height - 10), 0, 0, 0);
            inner.AddVertexAt(3, new Point2d(25, height - 10), 0, 0, 0);
            inner.Closed = true;
            btr.AppendEntity(inner);
            tr.AddNewlyCreatedDBObject(inner, true);

            // Attribute definitions: 图号, 图名, 比例
            AddAttributeDef(btr, tr, "图号", new Point3d(width - 120, 20, 0), 5.0);
            AddAttributeDef(btr, tr, "图名", new Point3d(width - 120, 35, 0), 6.0);
            AddAttributeDef(btr, tr, "比例", new Point3d(width - 120, 50, 0), 4.0);

            return btrId;
        }

        private static void AddAttributeDef(BlockTableRecord btr, Transaction tr, string tag, Point3d pos, double height)
        {
            var attDef = new AttributeDefinition(pos, "", tag, tag, ObjectId.Null)
            {
                Height = height
            };
            btr.AppendEntity(attDef);
            tr.AddNewlyCreatedDBObject(attDef, true);
        }

        private static void InsertTitleBlockReference(
            BlockTableRecord ms,
            Transaction tr,
            ObjectId btrId,
            Point3d insertPt,
            double scaleFactor,
            double rotationRad,
            string drawingNo,
            string drawingName,
            string scaleText)
        {
            var br = new BlockReference(insertPt, btrId)
            {
                ScaleFactors = new Scale3d(scaleFactor, scaleFactor, scaleFactor),
                Rotation = rotationRad
            };
            ms.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);

            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
            {
                AttributeDefinition attDef = tr.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                if (attDef != null && !attDef.Constant)
                {
                    var attRef = new AttributeReference();
                    attRef.SetAttributeFromBlock(attDef, br.BlockTransform);
                    if (attDef.Tag == "图号") attRef.TextString = drawingNo;
                    else if (attDef.Tag == "图名") attRef.TextString = drawingName;
                    else if (attDef.Tag == "比例") attRef.TextString = scaleText;

                    br.AttributeCollection.AppendAttribute(attRef);
                    tr.AddNewlyCreatedDBObject(attRef, true);
                }
            }
        }

        private static int CountModelSpaceEntities(Database db)
        {
            int count = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!id.IsErased) count++;
                }
                tr.Commit();
            }
            return count;
        }
    }
}
