using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using BatchPdf.AutoCAD.Services;
using BatchPdf.Core.Configuration;
using BatchPdf.Core.Models;
using BatchPdf.Core.Services;
using BatchPdf.Infrastructure.Services;
using BatchPdf.Plot.Services;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace BatchPdf.Plugin
{
    /// <summary>
    /// Headless job description (written by tools\headless\BatchPdfHeadless.ps1, one file per DWG).
    /// Every property is optional; null means "use the value saved in the GUI settings.json".
    /// </summary>
    public class HeadlessJob
    {
        /// <summary>"plot" (default) = scan + plot PDFs; "scan" = scan only and report (no PDF output).</summary>
        public string Mode { get; set; }
        /// <summary>Informational: the DWG that the driver opened with accoreconsole /i.</summary>
        public string DwgPath { get; set; }
        /// <summary>Overrides output directory (also disables "save next to DWG").</summary>
        public string OutputDirectory { get; set; }
        /// <summary>"single" = one PDF per sheet, "merged" = one multi-page PDF per DWG.</summary>
        public string OutputMode { get; set; }
        public bool? AutoCrop { get; set; }
        public double? CropMarginMm { get; set; }
        public string PlotStyle { get; set; }
        /// <summary>Alternate settings.json (default: %APPDATA%\BatchPdf\settings.json, shared with the GUI).</summary>
        public string SettingsPath { get; set; }
        /// <summary>Where the per-DWG result JSON is written for the driver script.</summary>
        public string ResultPath { get; set; }
    }

    public class HeadlessSheetReport
    {
        public int Index { get; set; }
        public string Handle { get; set; }
        public string FrameType { get; set; }
        public string Layer { get; set; }
        public string Block { get; set; }
        public string Size { get; set; }
        public double RotationDeg { get; set; }
        public string DrawingNumber { get; set; }
        public string DrawingName { get; set; }
        public string Scale { get; set; }
        public string Paper { get; set; }
        public string PaperCanonical { get; set; }
        public double PaperWidthMm { get; set; }
        public double PaperHeightMm { get; set; }
        public bool NonStandard { get; set; }
        public string Status { get; set; }
        public string OutputFile { get; set; }
        public string Error { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class HeadlessReport
    {
        public string Version { get; set; } = "1";
        public string DwgPath { get; set; }
        public string Mode { get; set; }
        public string OutputMode { get; set; }
        public string OutputDirectory { get; set; }
        public string MergedPdf { get; set; }
        public string Units { get; set; }
        public string Device { get; set; }
        public int MediaCount { get; set; }
        public int FrameCount { get; set; }
        public int ReadyCount { get; set; }
        public int NeedsConfirmationCount { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
        public string LogFile { get; set; }
        public string StartedAt { get; set; }
        public string FinishedAt { get; set; }
        public List<HeadlessSheetReport> Sheets { get; set; } = new List<HeadlessSheetReport>();
    }

    /// <summary>
    /// Headless (accoreconsole.exe) batch entry point. Must never touch acmgd-only APIs
    /// (WinForms dialogs, TransientManager, AcadApp, etc.) because accoreconsole cannot load them.
    /// Uses exactly the same scan / paper match / plot / crop pipeline as the GUI so it doubles as a
    /// regression harness.
    /// </summary>
    public class HeadlessCommands
    {
        public const string JobEnvVar = "BPDF_JOB";

        [CommandMethod("BPDFBATCH", CommandFlags.Modal)]
        public void RunHeadlessBatch()
        {
            Document doc = CoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            var report = new HeadlessReport
            {
                DwgPath = doc.Name,
                StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            HeadlessJob job = null;
            string jobPath = null;
            try
            {
                jobPath = Environment.GetEnvironmentVariable(JobEnvVar);
                if (string.IsNullOrWhiteSpace(jobPath))
                {
                    var pso = new PromptStringOptions("\n[BPDFBATCH] 任务文件路径 (回车=使用已保存设置直接打印): ") { AllowSpaces = true };
                    PromptResult pr = ed.GetString(pso);
                    if (pr.Status == PromptStatus.OK) jobPath = (pr.StringResult ?? "").Trim().Trim('"');
                }

                job = LoadJob(jobPath);
                report.Mode = string.IsNullOrWhiteSpace(job.Mode) ? "plot" : job.Mode.Trim().ToLowerInvariant();
                Execute(doc, job, report);
            }
            catch (System.Exception ex)
            {
                report.Success = false;
                report.Error = ex.GetType().Name + ": " + ex.Message;
                Say(ed, "[BPDFBATCH][异常] " + ex);
            }
            finally
            {
                report.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                WriteReport(ed, job, report);
            }
        }

        private static void Execute(Document doc, HeadlessJob job, HeadlessReport report)
        {
            Editor ed = doc.Editor;
            Database db = doc.Database;

            var configService = string.IsNullOrWhiteSpace(job.SettingsPath)
                ? new ConfigService()
                : new ConfigService(job.SettingsPath);
            BatchPdfSettings settings = configService.LoadSettings();
            ApplyOverrides(settings, job);

            var logService = new LogService();
            report.LogFile = logService.CurrentLogFilePath;
            logService.LogInfo("HeadlessJobStart", "BPDFBATCH started for " + doc.Name, job);

            Say(ed, "================ [BPDFBATCH 无头批量打印] ================");
            Say(ed, "图纸: " + doc.Name);

            var catalog = new PaperMediaCatalog();
            List<PaperMedia> mediaList = catalog.RefreshMediaList(out DeviceCheckResult devCheck);

            report.Device = devCheck.MatchedDeviceName;
            report.MediaCount = mediaList.Count;
            if (!devCheck.IsAvailable || mediaList.Count == 0)
            {
                report.Error = "DWG To PDF.pc3 不可用: " + devCheck.DiagnosticMessage;
                Say(ed, "[错误] " + report.Error);
                return;
            }

            var scanner = new FrameScanner();
            var prep = new SheetPreparationService();
            List<PrintSheet> sheets;
            DrawingUnits units;

            using (doc.LockDocument())
            {
                var candidates = scanner.ScanModelSpaceFrames(db, settings);
                units = FrameScanner.ReadDatabaseDrawingUnits(db);
                sheets = prep.CreatePrintSheets(candidates, doc.Name, units, settings, mediaList);
            }

            report.Units = units.ToDisplayText();
            report.FrameCount = sheets.Count;
            report.OutputMode = settings.OutputMode == OutputMode.MergedMultiPagePdf ? "merged" : "single";
            report.OutputDirectory = PlotJobRunner.ResolveEffectiveOutputDirectory(doc, settings);

            // Headless runs unattended: sheets that would need manual confirmation in the GUI are skipped and reported.
            foreach (var s in sheets)
            {
                bool unresolved = string.IsNullOrWhiteSpace(s.TargetPaperCanonicalName) ||
                                  (!s.FitToPaper && (!s.ScaleValue.HasValue || s.ScaleValue.Value <= 0));
                if (unresolved)
                {
                    s.IsSelected = false;
                    report.NeedsConfirmationCount++;
                }
            }
            report.ReadyCount = sheets.Count(s => s.IsSelected);
            Say(ed, string.Format("识别图框 {0} 个 | 可直接打印 {1} | 需确认(跳过) {2} | 单位 {3} | 输出 {4} -> {5}",
                report.FrameCount, report.ReadyCount, report.NeedsConfirmationCount, report.Units,
                report.OutputMode, report.OutputDirectory));

            if (report.Mode == "scan")
            {
                FillSheetReports(report, sheets, null);
                report.Success = sheets.Count > 0;
                if (sheets.Count == 0) report.Error = "未识别到任何图框";
                return;
            }

            if (report.ReadyCount == 0)
            {
                FillSheetReports(report, sheets, null);
                report.Error = sheets.Count == 0 ? "未识别到任何图框" : "所有图框均需人工确认比例/纸张，已全部跳过";
                Say(ed, "[警告] " + report.Error);
                return;
            }

            var naming = new NamingService();
            var runner = new PlotJobRunner(catalog, new PlotSettingsBuilder(catalog), naming, logService);
            Action<int, int, PrintSheet> progress = (i, n, s) =>
                Say(ed, string.Format("  打印 {0}/{1}: #{2} {3} {4}", i, n, s.OrderIndex, s.DrawingNumber, s.DrawingName));

            JobResult result;
            if (settings.OutputMode == OutputMode.MergedMultiPagePdf)
            {
                string mergedName = naming.EnsureValidPdfFileName(naming.FormatMergedFileName(settings.MergedFileNameTemplate, doc.Name));
                string mergedPath = naming.ResolveOutputFilePath(
                    report.OutputDirectory, mergedName, settings.DuplicatePolicy,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase), _ => true);
                if (string.IsNullOrEmpty(mergedPath))
                {
                    report.Error = "合并 PDF 目标文件已存在且重名策略为跳过";
                    FillSheetReports(report, sheets, null);
                    return;
                }
                result = runner.PlotMultiSheetPdf(doc, sheets, mergedPath, settings, progress, null, _ => true);
                report.MergedPdf = result.MergedOutputFilePath;
            }
            else
            {
                result = runner.PlotEachSheetToPdf(doc, sheets, settings, progress, null, _ => true);
            }

            report.Succeeded = result.SucceededCount;
            report.Failed = result.FailedCount;
            report.Skipped = result.SkippedCount;
            if (!string.IsNullOrWhiteSpace(result.FatalError)) report.Error = result.FatalError;
            report.Success = string.IsNullOrWhiteSpace(result.FatalError) && result.FailedCount == 0 && result.SucceededCount > 0;
            FillSheetReports(report, sheets, result);

            Say(ed, string.Format("完成: 成功 {0} | 失败 {1} | 跳过 {2}{3}",
                report.Succeeded, report.Failed, report.Skipped,
                string.IsNullOrEmpty(report.MergedPdf) ? "" : " | 合并PDF: " + report.MergedPdf));
        }

        private static void ApplyOverrides(BatchPdfSettings settings, HeadlessJob job)
        {
            if (!string.IsNullOrWhiteSpace(job.OutputDirectory))
            {
                settings.OutputDirectory = job.OutputDirectory.Trim();
                settings.SaveToDwgDirectory = false;
            }

            if (!string.IsNullOrWhiteSpace(job.OutputMode))
            {
                string m = job.OutputMode.Trim().ToLowerInvariant();
                if (m == "merged" || m == "merge") settings.OutputMode = OutputMode.MergedMultiPagePdf;
                else if (m == "single") settings.OutputMode = OutputMode.SinglePdfPerSheet;
            }

            if (job.AutoCrop.HasValue) settings.AutoCropNonStandardPdf = job.AutoCrop.Value;
            if (job.CropMarginMm.HasValue) settings.PdfCropMarginMm = Math.Max(0.0, job.CropMarginMm.Value);
            if (!string.IsNullOrWhiteSpace(job.PlotStyle)) settings.DefaultPlotStyle = job.PlotStyle.Trim();

            // Never block on interactive confirmations in headless mode.
            settings.ConfirmPageCountBeforePlot = false;
            settings.OpenReportAfterPlot = false;
        }

        private static void FillSheetReports(HeadlessReport report, List<PrintSheet> sheets, JobResult result)
        {
            report.Sheets.Clear();
            foreach (var s in sheets.OrderBy(x => x.OrderIndex))
            {
                SheetPlotResult r = result?.SheetResults.FirstOrDefault(x => x.SheetId == s.Id);
                string status;
                if (!s.IsSelected) status = "NeedsConfirmation";
                else if (r != null) status = r.Status.ToString();
                else status = s.Status.ToString();

                report.Sheets.Add(new HeadlessSheetReport
                {
                    Index = s.OrderIndex,
                    Handle = s.FrameHandle,
                    FrameType = s.FrameType.ToString(),
                    Layer = s.LayerName,
                    Block = s.BlockName,
                    Size = s.FormattedGeometryInfo,
                    RotationDeg = Math.Round(s.RotationAngleDeg, 3),
                    DrawingNumber = s.DrawingNumber,
                    DrawingName = s.DrawingName,
                    Scale = s.FormattedScaleInfo,
                    Paper = s.TargetPaperLocaleName,
                    PaperCanonical = s.TargetPaperCanonicalName,
                    PaperWidthMm = Math.Round(s.PaperWidthMm, 2),
                    PaperHeightMm = Math.Round(s.PaperHeightMm, 2),
                    NonStandard = PlotJobRunner.IsSheetNonStandardPaperMatch(s),
                    Status = status,
                    OutputFile = r?.OutputFilePath,
                    Error = r?.ErrorMessage ?? s.ErrorMessage,
                    Warnings = s.WarningMessages != null ? s.WarningMessages.ToList() : new List<string>()
                });
            }
        }

        private static HeadlessJob LoadJob(string jobPath)
        {
            if (string.IsNullOrWhiteSpace(jobPath) || !File.Exists(jobPath))
            {
                return new HeadlessJob();
            }

            string json = File.ReadAllText(jobPath, Encoding.UTF8);
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return serializer.Deserialize<HeadlessJob>(json) ?? new HeadlessJob();
        }

        private static void WriteReport(Editor ed, HeadlessJob job, HeadlessReport report)
        {
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                string json = serializer.Serialize(report);

                string path = job?.ResultPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    path = Path.Combine(LogService.GetDefaultLogDirectory(), "headless_last_result.json");
                }

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, json, new UTF8Encoding(false));
                Say(ed, "[BPDFBATCH] 结果文件: " + path);
            }
            catch (System.Exception ex)
            {
                Say(ed, "[BPDFBATCH] 写入结果文件失败: " + ex.Message);
            }
        }

        private static void Say(Editor ed, string line)
        {
            try { ed.WriteMessage("\n" + line); } catch { }
        }
    }
}
