using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using BatchPdf.Core.Configuration;
using BatchPdf.Core.Models;
using BatchPdf.Core.Services;
using BatchPdf.Infrastructure.Services;

namespace BatchPdf.Plot.Services
{
    public class PlotJobRunner
    {
        private readonly PaperMediaCatalog _catalog;
        private readonly PlotSettingsBuilder _settingsBuilder;
        private readonly NamingService _namingService;
        private readonly LogService _logService;
        private readonly PdfPageCombiner _pdfCombiner;
        private readonly PdfPageCropper _pdfCropper;

        public PlotJobRunner(
            PaperMediaCatalog catalog,
            PlotSettingsBuilder settingsBuilder,
            NamingService namingService,
            LogService logService)
        {
            _catalog = catalog ?? new PaperMediaCatalog();
            _settingsBuilder = settingsBuilder ?? new PlotSettingsBuilder(_catalog);
            _namingService = namingService ?? new NamingService();
            _logService = logService ?? new LogService();
            _pdfCombiner = new PdfPageCombiner();
            _pdfCropper = new PdfPageCropper();
        }

        /// <summary>
        /// Mode A: Plots each selected sheet to its own separate PDF file using DWG To PDF.pc3.
        /// Single-sheet failure does not abort remaining sheets.
        /// </summary>
        public JobResult PlotEachSheetToPdf(
            Document doc,
            IList<PrintSheet> sheets,
            BatchPdfSettings settings,
            Action<int, int, PrintSheet> onProgress = null,
            Func<bool> isCancelled = null,
            Func<string, bool> confirmOverwriteCallback = null)
        {
            string effectiveOutputDir = ResolveEffectiveOutputDirectory(doc, settings);
            var jobResult = new JobResult
            {
                OutputMode = OutputMode.SinglePdfPerSheet,
                OutputDirectory = effectiveOutputDir
            };

            if (!ValidatePreconditions(doc, sheets, effectiveOutputDir, jobResult, out DeviceCheckResult devCheck, out List<PrintSheet> snapshots))
            {
                _logService.LogJobSummary(jobResult);
                return jobResult;
            }

            Database db = doc.Database;
            List<string> availableStyles = _catalog.GetAvailablePlotStyleSheets(db);
            var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            object savedBgPlot = null;
            try
            {
                using (DocumentLock docLock = doc.LockDocument())
                using (var viewResolver = new RotatedFramePlotResolver(doc))
                {
                    savedBgPlot = TrySetBackgroundPlotZero();
                    ObjectId modelLayoutId = GetModelSpaceLayoutId(db);

                    for (int i = 0; i < snapshots.Count; i++)
                    {
                        var snapshot = snapshots[i];
                        var liveSheet = sheets.FirstOrDefault(s => s.Id == snapshot.Id);

                        if (isCancelled != null && isCancelled())
                        {
                            jobResult.WasCancelled = true;
                            MarkSheetResult(jobResult, snapshot, liveSheet, devCheck.MatchedDeviceName, null, SheetStatus.Skipped, "用户已取消任务");
                            continue;
                        }

                        onProgress?.Invoke(i + 1, snapshots.Count, liveSheet ?? snapshot);

                        if (!ValidateSheetReadyForPlot(snapshot, out string validationErr))
                        {
                            MarkSheetResult(jobResult, snapshot, liveSheet, devCheck.MatchedDeviceName, null, SheetStatus.Failed, validationErr);
                            continue;
                        }

                        string desiredName = !string.IsNullOrWhiteSpace(snapshot.OutputFileName)
                            ? snapshot.OutputFileName
                            : _namingService.FormatSheetFileName(
                                settings.FileNameTemplate,
                                doc.Name,
                                snapshot.OrderIndex,
                                snapshot.DrawingNumber,
                                snapshot.DrawingName);

                        string targetFilePath = _namingService.ResolveOutputFilePath(
                            effectiveOutputDir,
                            desiredName,
                            settings.DuplicatePolicy,
                            reservedPaths,
                            confirmOverwriteCallback);

                        if (string.IsNullOrEmpty(targetFilePath))
                        {
                            MarkSheetResult(jobResult, snapshot, liveSheet, devCheck.MatchedDeviceName, null, SheetStatus.Skipped, "目标文件已存在，按重名策略跳过");
                            continue;
                        }

                        if (!EnsureFileCanBeWritten(targetFilePath, out string ioErr))
                        {
                            MarkSheetResult(jobResult, snapshot, liveSheet, devCheck.MatchedDeviceName, targetFilePath, SheetStatus.Failed, ioErr);
                            continue;
                        }

                        try
                        {
                            if (liveSheet != null) liveSheet.Status = SheetStatus.Plotting;

                            ObjectId targetLayoutId = ResolveLayoutIdForSheet(db, snapshot);

                            PlotSingleSheetInternal(
                                doc,
                                db,
                                targetLayoutId,
                                viewResolver,
                                snapshot,
                                devCheck.MatchedDeviceName,
                                availableStyles,
                                targetFilePath,
                                settings);

                            if (!File.Exists(targetFilePath) || new FileInfo(targetFilePath).Length == 0)
                            {
                                MarkSheetResult(jobResult, snapshot, liveSheet, devCheck.MatchedDeviceName, targetFilePath, SheetStatus.Failed, "PlotEngine 未生成有效 PDF 文件");
                            }
                            else
                            {
                                if (settings != null && settings.AutoCropNonStandardPdf)
                                {
                                    TryAutoCropSheetPdf(targetFilePath, snapshot, liveSheet, settings);
                                }
                                MarkSheetResult(jobResult, snapshot, liveSheet, devCheck.MatchedDeviceName, targetFilePath, SheetStatus.Succeeded, null);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logService.LogError("SingleSheetPlotException", ex.Message, ex, snapshot);
                            MarkSheetResult(jobResult, snapshot, liveSheet, devCheck.MatchedDeviceName, targetFilePath, SheetStatus.Failed, ex.Message);
                        }
                    }
                }
            }
            finally
            {
                RestoreBackgroundPlot(savedBgPlot);
            }

            _logService.LogJobSummary(jobResult);
            return jobResult;
        }

        /// <summary>
        /// Mode B: Plots all selected sheets into a single multi-page PDF file using native AutoCAD PlotEngine
        /// and DWG To PDF.pc3. Supports mixed paper sizes across pages and preserves exact task order.
        /// </summary>
        public JobResult PlotMultiSheetPdf(
            Document doc,
            IList<PrintSheet> sheets,
            string outputPdfPath,
            BatchPdfSettings settings,
            Action<int, int, PrintSheet> onProgress = null,
            Func<bool> isCancelled = null,
            Func<string, bool> confirmSkipFailedPrecheckCallback = null)
        {
            string effectiveOutputDir = Path.GetDirectoryName(outputPdfPath);
            if (string.IsNullOrWhiteSpace(effectiveOutputDir))
            {
                effectiveOutputDir = ResolveEffectiveOutputDirectory(doc, settings);
                outputPdfPath = Path.Combine(effectiveOutputDir, outputPdfPath ?? "Merged.pdf");
            }

            var jobResult = new JobResult
            {
                OutputMode = OutputMode.MergedMultiPagePdf,
                OutputDirectory = effectiveOutputDir,
                MergedOutputFilePath = outputPdfPath,
                MergedFileIsComplete = false
            };

            if (!ValidatePreconditions(doc, sheets, effectiveOutputDir, jobResult, out DeviceCheckResult devCheck, out List<PrintSheet> snapshots))
            {
                _logService.LogJobSummary(jobResult);
                return jobResult;
            }

            if (!EnsureFileCanBeWritten(outputPdfPath, out string fileErr))
            {
                jobResult.FatalError = fileErr;
                _logService.LogJobSummary(jobResult);
                return jobResult;
            }

            // Pre-check all sheets before starting the multi-page PlotEngine document
            var validSnapshots = new List<PrintSheet>();
            foreach (var snap in snapshots)
            {
                var liveSheet = sheets.FirstOrDefault(s => s.Id == snap.Id);
                if (!ValidateSheetReadyForPlot(snap, out string err))
                {
                    bool skipAndContinue = confirmSkipFailedPrecheckCallback == null ||
                                           confirmSkipFailedPrecheckCallback(string.Format("图纸 #{0} ({1}) 预检未通过：{2}。是否跳过该页并继续合并其余图纸？",
                                               snap.OrderIndex, snap.OutputFileName ?? snap.FrameHandle, err));
                    if (!skipAndContinue)
                    {
                        jobResult.WasCancelled = true;
                        jobResult.FatalError = "因单页预检未通过，用户中止了合并 PDF 打印。";
                        _logService.LogJobSummary(jobResult);
                        return jobResult;
                    }

                    MarkSheetResult(jobResult, snap, liveSheet, devCheck.MatchedDeviceName, outputPdfPath, SheetStatus.Failed, err);
                }
                else
                {
                    validSnapshots.Add(snap);
                }
            }

            if (validSnapshots.Count == 0)
            {
                jobResult.FatalError = "没有通过预检的可打印图纸，未生成合并 PDF。";
                _logService.LogJobSummary(jobResult);
                return jobResult;
            }

            Database db = doc.Database;
            List<string> availableStyles = _catalog.GetAvailablePlotStyleSheets(db);
            object savedBgPlot = null;
            string tempMergeDir = Path.Combine(Path.GetTempPath(), "BatchPdf_Merge_" + jobResult.JobId);

            try
            {
                Directory.CreateDirectory(tempMergeDir);
                var generatedPageFiles = new List<string>();
                var generatedBookmarks = new List<string>();

                using (DocumentLock docLock = doc.LockDocument())
                using (var viewResolver = new RotatedFramePlotResolver(doc))
                {
                    savedBgPlot = TrySetBackgroundPlotZero();
                    ObjectId modelLayoutId = GetModelSpaceLayoutId(db);

                    if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                    {
                        jobResult.FatalError = "AutoCAD 绘图引擎当前正忙 (ProcessPlotState != NotPlotting)，请稍后重试。";
                        _logService.LogJobSummary(jobResult);
                        return jobResult;
                    }

                    for (int i = 0; i < validSnapshots.Count; i++)
                    {
                        var snap = validSnapshots[i];
                        var liveSheet = sheets.FirstOrDefault(s => s.Id == snap.Id);

                        if (isCancelled != null && isCancelled())
                        {
                            jobResult.WasCancelled = true;
                            MarkSheetResult(jobResult, snap, liveSheet, devCheck.MatchedDeviceName, outputPdfPath, SheetStatus.Skipped, "用户取消合并任务");
                            continue;
                        }

                        onProgress?.Invoke(i + 1, validSnapshots.Count, liveSheet ?? snap);
                        if (liveSheet != null) liveSheet.Status = SheetStatus.Plotting;

                        string tempPagePath = Path.Combine(tempMergeDir, string.Format("Page_{0:D4}.pdf", i + 1));

                        try
                        {
                            ObjectId targetLayoutId = ResolveLayoutIdForSheet(db, snap);

                            PlotSingleSheetInternal(
                                doc,
                                db,
                                targetLayoutId,
                                viewResolver,
                                snap,
                                devCheck.MatchedDeviceName,
                                availableStyles,
                                tempPagePath,
                                settings);

                            if (File.Exists(tempPagePath) && new FileInfo(tempPagePath).Length > 0)
                            {
                                if (settings != null && settings.AutoCropNonStandardPdf)
                                {
                                    TryAutoCropSheetPdf(tempPagePath, snap, liveSheet, settings);
                                }
                                generatedPageFiles.Add(tempPagePath);
                                generatedBookmarks.Add(FormatSheetBookmarkTitle(snap));
                                MarkSheetResult(jobResult, snap, liveSheet, devCheck.MatchedDeviceName, outputPdfPath, SheetStatus.Succeeded, null);
                            }
                            else
                            {
                                MarkSheetResult(jobResult, snap, liveSheet, devCheck.MatchedDeviceName, outputPdfPath, SheetStatus.Failed, "PlotEngine 未生成该页有效 PDF 数据");
                            }
                        }
                        catch (Exception pageEx)
                        {
                            _logService.LogError("MultiSheetPagePlotException", pageEx.Message, pageEx, snap);
                            MarkSheetResult(jobResult, snap, liveSheet, devCheck.MatchedDeviceName, outputPdfPath, SheetStatus.Failed, pageEx.Message);
                        }
                    }
                }

                if (generatedPageFiles.Count > 0)
                {
                    _pdfCombiner.CombineSinglePagePdfs(generatedPageFiles, outputPdfPath, generatedBookmarks);
                }

                bool fileCreated = File.Exists(outputPdfPath) && new FileInfo(outputPdfPath).Length > 0;
                jobResult.MergedFileIsComplete = fileCreated &&
                                                 !jobResult.WasCancelled &&
                                                 jobResult.FailedCount == 0 &&
                                                 jobResult.SucceededCount == snapshots.Count;
                if (!fileCreated && string.IsNullOrEmpty(jobResult.FatalError))
                {
                    jobResult.FatalError = "合并打印结束但未找到有效输出 PDF 文件。";
                }
            }
            catch (Exception ex)
            {
                jobResult.FatalError = "合并多页 PDF 异常: " + ex.Message;
                jobResult.MergedFileIsComplete = false;
                _logService.LogError("MultiSheetPlotException", ex.Message, ex);
            }
            finally
            {
                RestoreBackgroundPlot(savedBgPlot);
                try
                {
                    if (Directory.Exists(tempMergeDir))
                    {
                        Directory.Delete(tempMergeDir, recursive: true);
                    }
                }
                catch
                {
                }
            }

            _logService.LogJobSummary(jobResult);
            return jobResult;
        }

        /// <summary>
        /// Launches AutoCAD native vector print preview window for the specified sheet using PlotFactory.CreatePreviewEngine.
        /// Zero file disk footprint; user dismisses the full-screen vector preview with ESC or clicking close.
        /// </summary>
        public bool PreviewSingleSheet(Document doc, PrintSheet sheet, BatchPdfSettings settings = null)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (sheet == null) throw new ArgumentNullException(nameof(sheet));

            DeviceCheckResult devCheck = _catalog.EnsurePdfDeviceAvailable();
            if (!devCheck.IsAvailable)
            {
                throw new InvalidOperationException("DWG To PDF.pc3 不可用: " + devCheck.DiagnosticMessage);
            }

            Database db = doc.Database;
            List<string> availableStyles = _catalog.GetAvailablePlotStyleSheets(db);
            ObjectId targetLayoutId = ResolveLayoutIdForSheet(db, sheet);

            object savedBgPlot = null;
            try
            {
                using (DocumentLock docLock = doc.LockDocument())
                using (var viewResolver = new RotatedFramePlotResolver(doc))
                {
                    savedBgPlot = TrySetBackgroundPlotZero();
                    Extents2d dcsWindow = viewResolver.ResolvePlotWindowInDcs(sheet);

                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        Layout layout = (Layout)tr.GetObject(targetLayoutId, OpenMode.ForRead);
                        using (PlotSettings ps = _settingsBuilder.BuildPlotSettings(
                            db, layout, sheet, devCheck.MatchedDeviceName, dcsWindow, availableStyles, settings))
                        using (PlotInfo plotInfo = _settingsBuilder.CreateAndValidatePlotInfo(targetLayoutId, ps))
                        {
                            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                            {
                                throw new InvalidOperationException("AutoCAD 绘图引擎当前正忙，无法启动预览。");
                            }

                            PlotEngine previewEngine = PlotFactory.CreatePreviewEngine((int)PreviewEngineFlags.Plot);
                            try
                            {
                                previewEngine.BeginPlot(null, null);
                                previewEngine.BeginDocument(plotInfo, doc.Name, null, 1, false, null);

                                using (PlotPageInfo pageInfo = new PlotPageInfo())
                                {
                                    previewEngine.BeginPage(pageInfo, plotInfo, true, null);
                                    previewEngine.BeginGenerateGraphics(null);
                                    previewEngine.EndGenerateGraphics(null);
                                    previewEngine.EndPage(null);
                                }

                                previewEngine.EndDocument(null);
                                previewEngine.EndPlot(null);
                                return true;
                            }
                            finally
                            {
                                previewEngine.Dispose();
                            }
                        }
                    }
                }
            }
            finally
            {
                RestoreBackgroundPlot(savedBgPlot);
            }
        }

        private void PlotSingleSheetInternal(
            Document doc,
            Database db,
            ObjectId modelLayoutId,
            RotatedFramePlotResolver viewResolver,
            PrintSheet snapshot,
            string exactDeviceName,
            IList<string> availableStyles,
            string targetFilePath,
            BatchPdfSettings settings = null)
        {
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            {
                throw new InvalidOperationException("AutoCAD 绘图引擎当前正忙 (ProcessPlotState != NotPlotting)。");
            }

            Extents2d dcsWindow = viewResolver.ResolvePlotWindowInDcs(snapshot);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Layout layout = (Layout)tr.GetObject(modelLayoutId, OpenMode.ForRead);
                using (PlotSettings ps = _settingsBuilder.BuildPlotSettings(
                    db, layout, snapshot, exactDeviceName, dcsWindow, availableStyles, settings))
                {
                    using (PlotInfo plotInfo = _settingsBuilder.CreateAndValidatePlotInfo(modelLayoutId, ps))
                    {
                        PlotEngine engine = PlotFactory.CreatePublishEngine();
                        try
                        {
                            bool isHeadless = System.Diagnostics.Process.GetCurrentProcess().ProcessName.IndexOf("accoreconsole", StringComparison.OrdinalIgnoreCase) >= 0;
                            PlotProgressDialog progressDlg = null;
                            if (!isHeadless)
                            {
                                try
                                {
                                    progressDlg = new PlotProgressDialog(false, 1, true);
                                    progressDlg.LowerPlotProgressRange = 0;
                                    progressDlg.UpperPlotProgressRange = 100;
                                    progressDlg.PlotProgressPos = 0;
                                    progressDlg.OnBeginPlot();
                                    progressDlg.IsVisible = false;
                                }
                                catch
                                {
                                    progressDlg = null;
                                }
                            }

                            try
                            {
                                engine.BeginPlot(progressDlg, null);
                                int copies = (settings != null && settings.PlotCopies > 1) ? settings.PlotCopies : 1;
                                engine.BeginDocument(plotInfo, doc.Name, null, copies, true, targetFilePath);

                                using (PlotPageInfo pageInfo = new PlotPageInfo())
                                {
                                    progressDlg?.OnBeginSheet();
                                    engine.BeginPage(pageInfo, plotInfo, true, null);
                                    engine.BeginGenerateGraphics(null);
                                    engine.EndGenerateGraphics(null);
                                    engine.EndPage(null);
                                    progressDlg?.OnEndSheet();
                                }

                                engine.EndDocument(null);
                                engine.EndPlot(null);
                                progressDlg?.OnEndPlot();
                            }
                            finally
                            {
                                if (progressDlg != null)
                                {
                                    try { progressDlg.Dispose(); } catch { }
                                }
                            }
                        }
                        finally
                        {
                            try { engine.Destroy(); } catch { }
                            try { engine.Dispose(); } catch { }
                        }
                    }
                }
                tr.Commit();
            }
        }

        public static string ResolveEffectiveOutputDirectory(Document doc, BatchPdfSettings settings)
        {
            if (settings != null && settings.SaveToDwgDirectory && doc != null && !string.IsNullOrWhiteSpace(doc.Name))
            {
                try
                {
                    if (Path.IsPathRooted(doc.Name))
                    {
                        string dwgDir = Path.GetDirectoryName(doc.Name);
                        if (!string.IsNullOrWhiteSpace(dwgDir) && Directory.Exists(dwgDir))
                        {
                            return dwgDir;
                        }
                    }
                }
                catch
                {
                }
            }

            return settings != null ? settings.OutputDirectory : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        /// <summary>
        /// Checks whether a sheet's scaled frame dimensions differ from its assigned PC3 paper size by more than 6 mm,
        /// indicating a non-standard or lengthened frame plotted onto a larger paper that has excess white borders.
        /// </summary>
        public static bool IsSheetNonStandardPaperMatch(PrintSheet sheet)
        {
            if (sheet == null || sheet.FitToPaper || !sheet.ScaleValue.HasValue || sheet.ScaleValue.Value <= 0)
            {
                return false;
            }

            double unitToMm = sheet.DrawingUnits.ToMillimetersFactor();
            double fwMm = sheet.FrameWidthUnits * unitToMm / sheet.ScaleValue.Value;
            double fhMm = sheet.FrameHeightUnits * unitToMm / sheet.ScaleValue.Value;
            double longFrame = Math.Max(fwMm, fhMm);
            double shortFrame = Math.Min(fwMm, fhMm);
            double longPaper = Math.Max(sheet.PaperWidthMm, sheet.PaperHeightMm);
            double shortPaper = Math.Min(sheet.PaperWidthMm, sheet.PaperHeightMm);

            return longPaper > 0 && (Math.Abs(longPaper - longFrame) > 6.0 || Math.Abs(shortPaper - shortFrame) > 6.0);
        }

        /// <summary>
        /// Crops excess white borders on already-plotted PDF file(s) (either a merged multi-page PDF or individual sheet PDFs).
        /// Standard frames whose dimensions match the paper size are automatically skipped.
        /// </summary>
        public PdfCropResult CropPlottedSheets(IList<PrintSheet> sheets, string mergedPdfPath, BatchPdfSettings settings)
        {
            var aggregate = new PdfCropResult();
            double marginMm = settings != null ? Math.Max(0.0, settings.PdfCropMarginMm) : 0.0;

            if (!string.IsNullOrWhiteSpace(mergedPdfPath) && File.Exists(mergedPdfPath))
            {
                double? fallbackW = null;
                double? fallbackH = null;
                var selected = sheets != null ? sheets.Where(s => s != null && s.IsSelected).ToList() : new List<PrintSheet>();
                if (selected.Count == 1)
                {
                    GetExpectedFrameSizeMm(selected[0], out fallbackW, out fallbackH);
                }

                PdfCropResult res = _pdfCropper.CropPdfFile(mergedPdfPath, marginMm, fallbackW, fallbackH);
                aggregate.TotalPages += res.TotalPages;
                aggregate.CroppedPages += res.CroppedPages;
                aggregate.SkippedStandardPages += res.SkippedStandardPages;
                aggregate.PageDetails.AddRange(res.PageDetails);

                if (res.CroppedPages > 0)
                {
                    _logService.LogInfo("PdfCropOnDemand", string.Format("Merged PDF cropped: {0}/{1} pages", res.CroppedPages, res.TotalPages), new
                    {
                        PdfPath = mergedPdfPath,
                        MarginMm = marginMm,
                        Details = res.PageDetails
                    });
                }

                return aggregate;
            }

            if (sheets == null || sheets.Count == 0)
            {
                return aggregate;
            }

            foreach (var sheet in sheets.Where(s => s != null && s.IsSelected))
            {
                string pdfPath = ResolveExistingSheetPdfPath(sheet, settings);
                if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                {
                    continue;
                }

                GetExpectedFrameSizeMm(sheet, out double? fwMm, out double? fhMm);
                PdfCropResult res = _pdfCropper.CropPdfFile(pdfPath, marginMm, fwMm, fhMm);
                aggregate.TotalPages += res.TotalPages;
                aggregate.CroppedPages += res.CroppedPages;
                aggregate.SkippedStandardPages += res.SkippedStandardPages;
                aggregate.PageDetails.AddRange(res.PageDetails);

                if (res.CroppedPages > 0 && res.PageDetails.Count > 0)
                {
                    string note = res.PageDetails[0];
                    if (sheet.WarningMessages == null) sheet.WarningMessages = new List<string>();
                    sheet.WarningMessages.RemoveAll(w => w != null && w.StartsWith("页面尺寸由 ", StringComparison.Ordinal));
                    sheet.WarningMessages.Add(note);
                    _logService.LogInfo("PdfCropOnDemand", string.Format("Sheet #{0} [{1}] -> {2}", sheet.OrderIndex, sheet.FrameHandle, note), new
                    {
                        PdfPath = pdfPath,
                        MarginMm = marginMm
                    });
                }
            }

            return aggregate;
        }

        private void TryAutoCropSheetPdf(string targetPdfPath, PrintSheet snapshot, PrintSheet liveSheet, BatchPdfSettings settings)
        {
            try
            {
                GetExpectedFrameSizeMm(snapshot, out double? fwMm, out double? fhMm);
                double marginMm = settings != null ? Math.Max(0.0, settings.PdfCropMarginMm) : 0.0;
                PdfCropResult cropRes = _pdfCropper.CropPdfFile(targetPdfPath, marginMm, fwMm, fhMm);
                if (cropRes != null && cropRes.CroppedPages > 0 && cropRes.PageDetails.Count > 0)
                {
                    string note = cropRes.PageDetails[0];
                    if (snapshot.WarningMessages == null) snapshot.WarningMessages = new List<string>();
                    snapshot.WarningMessages.RemoveAll(w => w != null && w.StartsWith("页面尺寸由 ", StringComparison.Ordinal));
                    snapshot.WarningMessages.Add(note);

                    if (liveSheet != null)
                    {
                        if (liveSheet.WarningMessages == null) liveSheet.WarningMessages = new List<string>();
                        liveSheet.WarningMessages.RemoveAll(w => w != null && w.StartsWith("页面尺寸由 ", StringComparison.Ordinal));
                        liveSheet.WarningMessages.Add(note);
                    }

                    _logService.LogInfo(
                        "PdfAutoCrop",
                        string.Format("Sheet #{0} [{1}] -> {2}", snapshot.OrderIndex, snapshot.FrameHandle, note),
                        new
                        {
                            PdfPath = targetPdfPath,
                            MarginMm = marginMm
                        });
                }
            }
            catch (Exception ex)
            {
                _logService.LogWarning("PdfAutoCropWarning", "裁剪非标 PDF 白边时发生异常: " + ex.Message, snapshot);
            }
        }

        private static void GetExpectedFrameSizeMm(PrintSheet sheet, out double? fwMm, out double? fhMm)
        {
            fwMm = null;
            fhMm = null;
            if (sheet != null && !sheet.FitToPaper && sheet.ScaleValue.HasValue && sheet.ScaleValue.Value > 0 &&
                sheet.FrameWidthUnits > 0 && sheet.FrameHeightUnits > 0)
            {
                double unitToMm = sheet.DrawingUnits.ToMillimetersFactor();
                fwMm = sheet.FrameWidthUnits * unitToMm / sheet.ScaleValue.Value;
                fhMm = sheet.FrameHeightUnits * unitToMm / sheet.ScaleValue.Value;
            }
        }

        private string ResolveExistingSheetPdfPath(PrintSheet sheet, BatchPdfSettings settings)
        {
            if (sheet == null) return null;

            string fileName = sheet.OutputFileName;
            if (string.IsNullOrWhiteSpace(fileName) && settings != null)
            {
                fileName = _namingService.FormatSheetFileName(
                    settings.FileNameTemplate,
                    sheet.SourceDrawingPath ?? "Drawing.dwg",
                    sheet.OrderIndex,
                    sheet.DrawingNumber,
                    sheet.DrawingName);
            }

            if (string.IsNullOrWhiteSpace(fileName)) return null;
            if (Path.IsPathRooted(fileName) && File.Exists(fileName)) return fileName;

            if (settings != null && settings.SaveToDwgDirectory && !string.IsNullOrWhiteSpace(sheet.SourceDrawingPath))
            {
                try
                {
                    string dwgDir = Path.GetDirectoryName(sheet.SourceDrawingPath);
                    if (!string.IsNullOrWhiteSpace(dwgDir))
                    {
                        string candidate = Path.Combine(dwgDir, fileName);
                        if (File.Exists(candidate)) return candidate;
                    }
                }
                catch
                {
                }
            }

            if (settings != null && !string.IsNullOrWhiteSpace(settings.OutputDirectory))
            {
                try
                {
                    string candidate = Path.Combine(settings.OutputDirectory, fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                }
            }

            return null;
        }

        private bool ValidatePreconditions(
            Document doc,
            IList<PrintSheet> sheets,
            string effectiveOutputDirectory,
            JobResult jobResult,
            out DeviceCheckResult devCheck,
            out List<PrintSheet> selectedSnapshots)
        {
            selectedSnapshots = new List<PrintSheet>();
            devCheck = _catalog.EnsurePdfDeviceAvailable();
            jobResult.DeviceName = devCheck.MatchedDeviceName;

            if (!devCheck.IsAvailable)
            {
                jobResult.FatalError = devCheck.DiagnosticMessage;
                return false;
            }

            if (doc == null)
            {
                jobResult.FatalError = "当前没有活动的 AutoCAD 文档。";
                return false;
            }

            if (sheets == null || sheets.Count == 0)
            {
                jobResult.FatalError = "图纸任务清单为空。";
                return false;
            }

            selectedSnapshots = sheets
                .Where(s => s != null && s.IsSelected)
                .OrderBy(s => s.OrderIndex)
                .Select(s => s.CloneSnapshot())
                .ToList();

            jobResult.TotalCount = selectedSnapshots.Count;
            if (selectedSnapshots.Count == 0)
            {
                jobResult.FatalError = "未勾选任何要打印的图纸。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(effectiveOutputDirectory))
            {
                jobResult.FatalError = "输出目录不能为空。";
                return false;
            }

            try
            {
                if (!Directory.Exists(effectiveOutputDirectory))
                {
                    Directory.CreateDirectory(effectiveOutputDirectory);
                }
            }
            catch (Exception ex)
            {
                jobResult.FatalError = "无法创建输出目录: " + ex.Message;
                return false;
            }

            return true;
        }

        private static bool ValidateSheetReadyForPlot(PrintSheet sheet, out string error)
        {
            error = null;
            if (sheet == null)
            {
                error = "图纸对象为空";
                return false;
            }
            if (string.IsNullOrWhiteSpace(sheet.TargetPaperCanonicalName))
            {
                error = "未匹配或未指定 PC3 纸张尺寸";
                return false;
            }
            if (!sheet.FitToPaper && (!sheet.ScaleValue.HasValue || sheet.ScaleValue.Value <= 0))
            {
                error = "打印比例未知（需确认比例）";
                return false;
            }
            if (sheet.FrameWidthUnits <= 0 || sheet.FrameHeightUnits <= 0)
            {
                error = "图框几何尺寸无效";
                return false;
            }
            return true;
        }

        private static bool EnsureFileCanBeWritten(string filePath, out string error)
        {
            error = null;
            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
                return true;
            }
            catch (IOException ioEx)
            {
                error = string.Format("目标 PDF 文件可能被其他程序占用或只读 ({0}): {1}", Path.GetFileName(filePath), ioEx.Message);
                return false;
            }
            catch (UnauthorizedAccessException uaEx)
            {
                error = string.Format("无权限写入目标文件 ({0}): {1}", Path.GetFileName(filePath), uaEx.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = string.Format("检查输出路径失败 ({0}): {1}", Path.GetFileName(filePath), ex.Message);
                return false;
            }
        }

        private void MarkSheetResult(
            JobResult jobResult,
            PrintSheet snapshot,
            PrintSheet liveSheet,
            string deviceName,
            string outputFilePath,
            SheetStatus status,
            string errorMessage)
        {
            if (liveSheet != null)
            {
                liveSheet.Status = status;
                liveSheet.ErrorMessage = errorMessage;
            }

            if (status == SheetStatus.Succeeded) jobResult.SucceededCount++;
            else if (status == SheetStatus.Failed) jobResult.FailedCount++;
            else if (status == SheetStatus.Skipped) jobResult.SkippedCount++;

            var sheetRes = new SheetPlotResult
            {
                SheetId = snapshot.Id,
                OrderIndex = snapshot.OrderIndex,
                FrameHandle = snapshot.FrameHandle,
                DrawingNumber = snapshot.DrawingNumber,
                DrawingName = snapshot.DrawingName,
                DeviceName = deviceName,
                CanonicalMediaName = snapshot.TargetPaperCanonicalName,
                LocaleMediaName = snapshot.TargetPaperLocaleName,
                PaperWidthMm = snapshot.PaperWidthMm,
                PaperHeightMm = snapshot.PaperHeightMm,
                ScaleValue = snapshot.ScaleValue,
                FitToPaper = snapshot.FitToPaper,
                PlotStyle = snapshot.PlotStyle,
                OutputFilePath = outputFilePath,
                Status = status,
                ErrorMessage = errorMessage
            };

            jobResult.SheetResults.Add(sheetRes);
            _logService.LogSheetResult(jobResult.JobId, sheetRes);
        }

        private static ObjectId GetModelSpaceLayoutId(Database db)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                ObjectId layoutId = ms.LayoutId;
                tr.Commit();
                return layoutId;
            }
        }

        private static ObjectId ResolveLayoutIdForSheet(Database db, PrintSheet sheet)
        {
            if (db == null) return ObjectId.Null;
            string layoutName = RotatedFramePlotResolver.ResolveLayoutName(sheet);

            if (string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase))
            {
                return GetModelSpaceLayoutId(db);
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBDictionary layoutDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                if (layoutDict.Contains(layoutName))
                {
                    ObjectId layoutId = layoutDict.GetAt(layoutName);
                    tr.Commit();
                    return layoutId;
                }
                tr.Commit();
            }

            return GetModelSpaceLayoutId(db);
        }

        private static object TrySetBackgroundPlotZero()
        {
            try
            {
                // Use the core (accoremgd) Application so this also works in headless accoreconsole.exe
                object prev = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("BACKGROUNDPLOT");
                Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("BACKGROUNDPLOT", (short)0);
                return prev;
            }
            catch
            {
                return null;
            }
        }

        private static void RestoreBackgroundPlot(object savedValue)
        {
            if (savedValue != null)
            {
                try
                {
                    Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("BACKGROUNDPLOT", savedValue);
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// Formats drawing number and drawing name into a clean bookmark title, e.g. "[建施-01] 一层平面图".
        /// Falls back to sheet output filename or order index if drawing number/name are missing.
        /// </summary>
        public static string FormatSheetBookmarkTitle(PrintSheet sheet)
        {
            if (sheet == null)
            {
                return "未命名图纸";
            }

            string drawingNum = sheet.DrawingNumber?.Trim();
            string drawingName = sheet.DrawingName?.Trim();

            bool hasNum = !string.IsNullOrEmpty(drawingNum);
            bool hasName = !string.IsNullOrEmpty(drawingName);

            if (hasNum && hasName)
            {
                return string.Format("[{0}] {1}", drawingNum, drawingName);
            }
            if (hasNum)
            {
                return string.Format("[{0}]", drawingNum);
            }
            if (hasName)
            {
                return drawingName;
            }

            if (!string.IsNullOrWhiteSpace(sheet.OutputFileName))
            {
                return Path.GetFileNameWithoutExtension(sheet.OutputFileName);
            }

            return string.Format("图纸 #{0}", sheet.OrderIndex > 0 ? sheet.OrderIndex : 1);
        }
    }
}
