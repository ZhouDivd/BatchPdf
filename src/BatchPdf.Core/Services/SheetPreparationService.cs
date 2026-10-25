using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BatchPdf.Core.Configuration;
using BatchPdf.Core.Models;

namespace BatchPdf.Core.Services
{
    public class SheetPreparationService
    {
        private readonly PaperMatcher _paperMatcher;
        private readonly NamingService _namingService;

        public SheetPreparationService(PaperMatcher paperMatcher = null, NamingService namingService = null)
        {
            _paperMatcher = paperMatcher ?? new PaperMatcher();
            _namingService = namingService ?? new NamingService();
        }

        /// <summary>
        /// Converts a list of scanned FrameCandidates into PrintSheets and evaluates scale, paper match,
        /// file naming, and confirmation status.
        /// </summary>
        public List<PrintSheet> CreatePrintSheets(
            IList<FrameCandidate> candidates,
            string dwgPath,
            DrawingUnits drawingUnits,
            BatchPdfSettings settings,
            IList<PaperMedia> availableMedia)
        {
            var sheets = new List<PrintSheet>();
            if (candidates == null) return sheets;

            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                var sheet = new PrintSheet
                {
                    Id = c.Id ?? Guid.NewGuid().ToString("N"),
                    IsSelected = true,
                    OrderIndex = i + 1,
                    SourceDrawingPath = dwgPath,
                    SpaceName = !string.IsNullOrWhiteSpace(c.LayoutName) ? c.LayoutName : "Model",
                    Space = !string.IsNullOrWhiteSpace(c.Space) ? c.Space : "模型空间",
                    FrameHandle = c.FrameHandle,
                    FrameType = c.FrameType,
                    LayerName = c.LayerName,
                    BlockName = c.BlockName,
                    Corners = c.Corners != null ? (Point2D[])c.Corners.Clone() : new Point2D[4],
                    FrameExtents = c.WcsBoundingBox,
                    FrameWidthUnits = c.WidthDrawingUnits,
                    FrameHeightUnits = c.HeightDrawingUnits,
                    RotationAngleDeg = c.RotationAngleDeg,
                    DrawingUnits = drawingUnits,
                    DrawingNumber = c.ExtractedDrawingNumber ?? "",
                    DrawingName = c.ExtractedDrawingName ?? "",
                    FitToPaper = settings.UseFitToPaper,
                    PlotStyle = settings.DefaultPlotStyle,
                    ColorMode = settings.DefaultColorMode,
                    PrintLineweights = settings.PrintLineweights
                };

                if (c.Warnings != null && c.Warnings.Count > 0)
                {
                    sheet.WarningMessages.AddRange(c.Warnings);
                }

                // Resolve initial scale
                if (settings.UseFitToPaper)
                {
                    sheet.ScaleValue = null;
                    sheet.ScaleSource = ScaleSource.FitToPaper;
                }
                else if (settings.UseCustomFixedScale && settings.CustomScaleDenominator > 0 && settings.CustomScaleNumerator > 0)
                {
                    sheet.ScaleValue = settings.CustomScaleDenominator / settings.CustomScaleNumerator;
                    sheet.ScaleSource = ScaleSource.UserSpecified;
                }
                else if (c.IsPaperSpace)
                {
                    // In PaperSpace Layout, sheet units are already 1:1 in millimeters on paper
                    sheet.ScaleValue = c.ExtractedScaleValue.HasValue && c.ExtractedScaleValue.Value > 0
                        ? c.ExtractedScaleValue.Value
                        : 1.0;
                    sheet.ScaleSource = c.ExtractedScaleValue.HasValue
                        ? ScaleSource.BlockAttribute
                        : ScaleSource.UserSpecified;
                }
                else if (c.ExtractedScaleValue.HasValue && c.ExtractedScaleValue.Value > 0)
                {
                    if (!ScaleParser.MatchesStandardGbOrCatalogPaper(
                            c.WidthDrawingUnits,
                            c.HeightDrawingUnits,
                            drawingUnits,
                            c.ExtractedScaleValue.Value,
                            availableMedia,
                            settings.PaperMatchToleranceMm) &&
                        settings.InferScaleFromStandardPaper &&
                        ScaleParser.TryInferScaleFromAvailableMedia(
                            c.WidthDrawingUnits,
                            c.HeightDrawingUnits,
                            drawingUnits,
                            availableMedia,
                            settings.PaperMatchToleranceMm,
                            out double geomScale,
                            out _))
                    {
                        sheet.ScaleValue = geomScale;
                        sheet.ScaleSource = ScaleSource.InferredFromPaper;
                    }
                    else
                    {
                        sheet.ScaleValue = c.ExtractedScaleValue.Value;
                        sheet.ScaleSource = ScaleSource.BlockAttribute;
                    }
                }
                else if (settings.UseDefaultScaleWhenMissing && settings.DefaultScaleValue > 0)
                {
                    sheet.ScaleValue = settings.DefaultScaleValue;
                    sheet.ScaleSource = ScaleSource.RuleDefault;
                }
                else if (settings.InferScaleFromStandardPaper &&
                         ScaleParser.TryInferScaleFromAvailableMedia(
                             c.WidthDrawingUnits,
                             c.HeightDrawingUnits,
                             drawingUnits,
                             availableMedia,
                             settings.PaperMatchToleranceMm,
                             out double inferredScale,
                             out _))
                {
                    sheet.ScaleValue = inferredScale;
                    sheet.ScaleSource = ScaleSource.InferredFromPaper;
                }
                else
                {
                    sheet.ScaleValue = null;
                    sheet.ScaleSource = ScaleSource.Unknown;
                }

                RefreshSheetPaperAndStatus(sheet, settings, availableMedia, preserveCustomFileName: false);
                sheets.Add(sheet);
            }

            return sheets;
        }

        /// <summary>
        /// Re-evaluates paper matching, printable area validation, output file name, and status
        /// for an existing PrintSheet when settings, scale, or paper options change.
        /// </summary>
        public void RefreshSheetPaperAndStatus(
            PrintSheet sheet,
            BatchPdfSettings settings,
            IList<PaperMedia> availableMedia,
            bool preserveCustomFileName = true)
        {
            if (sheet == null || settings == null) return;

            var baseWarnings = sheet.WarningMessages
                .Where(w => !w.StartsWith("PC3 ") &&
                            !w.StartsWith("已匹配") &&
                            !w.StartsWith("识别为加长图幅") &&
                            !w.StartsWith("裁切风险") &&
                            !w.StartsWith("边距提示") &&
                            !w.StartsWith("比例待确认") &&
                            !w.StartsWith("比例 1:") &&
                            !w.StartsWith("当前启用") &&
                            !w.StartsWith("未选择"))
                .ToList();

            sheet.WarningMessages = baseWarnings;
            sheet.ErrorMessage = null;

            bool needsConfirm = false;

            if (sheet.DrawingUnits == DrawingUnits.Unitless)
            {
                if (!sheet.WarningMessages.Any(w => w.Contains("INSUNITS")))
                {
                    sheet.WarningMessages.Add("当前图纸 INSUNITS 为无单位，已按毫米(mm)换算");
                }
            }

            if (sheet.FitToPaper)
            {
                sheet.ScaleSource = ScaleSource.FitToPaper;
                sheet.WarningMessages.Add("当前启用“布满图纸(适合纸张)”，输出比例将随纸张缩放");
            }
            else if (!sheet.ScaleValue.HasValue || sheet.ScaleValue.Value <= 0)
            {
                sheet.ScaleSource = ScaleSource.Unknown;
                sheet.WarningMessages.Add("比例待确认：未读取到有效比例，请手动输入比例");
                needsConfirm = true;
            }
            else if (sheet.ScaleSource == ScaleSource.InferredFromPaper)
            {
                TargetPaperSize est = _paperMatcher.EstimateTargetPaper(
                    sheet.FrameWidthUnits, sheet.FrameHeightUnits, sheet.DrawingUnits, sheet.ScaleValue);
                string gbName = ScaleParser.GetStandardPaperShortName(est.ExpectedWidthMm, est.ExpectedHeightMm);
                sheet.WarningMessages.Add(string.Format("比例 1:{0:G} 由标准图幅 {1} 自动推算", sheet.ScaleValue.Value, gbName));
            }

            // Paper resolution: AutoMatch vs Manual selection
            if (settings.AutoMatchPaperSize)
            {
                double? scaleForPaperMatch = sheet.ScaleValue;
                if ((!scaleForPaperMatch.HasValue || scaleForPaperMatch.Value <= 0) && sheet.FitToPaper)
                {
                    if (ScaleParser.TryInferScaleFromAvailableMedia(
                        sheet.FrameWidthUnits, sheet.FrameHeightUnits, sheet.DrawingUnits,
                        availableMedia, settings.PaperMatchToleranceMm,
                        out double fitInferredScale, out _))
                    {
                        scaleForPaperMatch = fitInferredScale;
                    }
                    else if (settings.DefaultScaleValue > 0)
                    {
                        scaleForPaperMatch = settings.DefaultScaleValue;
                    }
                }

                if (!scaleForPaperMatch.HasValue || scaleForPaperMatch.Value <= 0)
                {
                    if (sheet.FitToPaper)
                    {
                        PaperMedia fallback = FindMediaByCanonicalName(availableMedia, sheet.TargetPaperCanonicalName)
                                           ?? FindMediaByCanonicalName(availableMedia, settings.ManualPaperCanonicalName)
                                           ?? availableMedia?.FirstOrDefault();
                        if (fallback != null)
                        {
                            ApplyMediaToSheet(sheet, fallback);
                        }
                        else
                        {
                            ClearMediaOnSheet(sheet);
                            needsConfirm = true;
                        }
                    }
                    else
                    {
                        ClearMediaOnSheet(sheet);
                        needsConfirm = true;
                    }
                }
                else
                {
                    TargetPaperSize target = _paperMatcher.EstimateTargetPaper(
                        sheet.FrameWidthUnits,
                        sheet.FrameHeightUnits,
                        sheet.DrawingUnits,
                        scaleForPaperMatch);

                    PaperMatchResult match = _paperMatcher.MatchAvailableMedia(
                        target,
                        availableMedia,
                        settings.PaperMatchToleranceMm);

                    if (match.IsMatched && match.MatchedMedia != null)
                    {
                        ApplyMediaToSheet(sheet, match.MatchedMedia);
                        sheet.PlotRotation = match.RecommendedPlotRotation;
                        if (match.HasClippingRisk && !sheet.FitToPaper)
                        {
                            sheet.WarningMessages.Add(match.Message);
                        }
                    }
                    else if (_paperMatcher.TryMatchEnclosingStandardMedia(
                        target,
                        availableMedia,
                        sheet.FitToPaper,
                        out PaperMedia enclosingMedia,
                        out PlotRotationMode enclosingRot,
                        out string enclosingNote))
                    {
                        ApplyMediaToSheet(sheet, enclosingMedia);
                        sheet.PlotRotation = enclosingRot;
                        sheet.WarningMessages.Add(enclosingNote);
                    }
                    else
                    {
                        ClearMediaOnSheet(sheet);
                        sheet.WarningMessages.Add(match.Message);
                        needsConfirm = true;
                    }
                }
            }
            else
            {
                // Manual paper selection mode
                string desiredCanonical = !string.IsNullOrWhiteSpace(sheet.TargetPaperCanonicalName)
                    ? sheet.TargetPaperCanonicalName
                    : settings.ManualPaperCanonicalName;

                PaperMedia manualMedia = FindMediaByCanonicalName(availableMedia, desiredCanonical)
                                      ?? availableMedia?.FirstOrDefault();
                if (manualMedia != null)
                {
                    ApplyMediaToSheet(sheet, manualMedia);

                    PrintableAreaValidationResult val = _paperMatcher.ValidatePrintableArea(
                        sheet.FrameWidthUnits,
                        sheet.FrameHeightUnits,
                        sheet.DrawingUnits,
                        manualMedia,
                        sheet.ScaleValue,
                        sheet.FitToPaper);

                    if (!val.IsValid)
                    {
                        sheet.WarningMessages.Add(val.WarningMessage);
                        needsConfirm = true;
                    }
                    else if (val.HasClippingRisk && !string.IsNullOrWhiteSpace(val.WarningMessage))
                    {
                        sheet.WarningMessages.Add(val.WarningMessage);
                    }
                }
                else
                {
                    ClearMediaOnSheet(sheet);
                    sheet.WarningMessages.Add("未选择有效 PC3 纸张，请在下拉框中指定纸张");
                    needsConfirm = true;
                }
            }

            if (!preserveCustomFileName || string.IsNullOrWhiteSpace(sheet.OutputFileName))
            {
                string basePdfName = _namingService.FormatSheetFileName(
                    settings.FileNameTemplate,
                    sheet.SourceDrawingPath,
                    sheet.OrderIndex,
                    sheet.DrawingNumber,
                    sheet.DrawingName);

                if (settings.AppendPaperSizeToFileName)
                {
                    double wMm = sheet.PaperWidthMm;
                    double hMm = sheet.PaperHeightMm;
                    if (sheet.ScaleValue.HasValue && sheet.ScaleValue.Value > 0)
                    {
                        var est = _paperMatcher.EstimateTargetPaper(
                            sheet.FrameWidthUnits, sheet.FrameHeightUnits, sheet.DrawingUnits, sheet.ScaleValue);
                        if (est.IsValid)
                        {
                            wMm = est.ExpectedWidthMm;
                            hMm = est.ExpectedHeightMm;
                        }
                    }

                    if (wMm > 0 && hMm > 0)
                    {
                        string paperShort = ScaleParser.GetStandardPaperShortName(wMm, hMm).Replace("/", "-");
                        string withoutExt = Path.GetFileNameWithoutExtension(basePdfName);
                        basePdfName = _namingService.EnsureValidPdfFileName(withoutExt + "_" + paperShort + ".pdf");
                    }
                }

                sheet.OutputFileName = basePdfName;
            }
            else
            {
                sheet.OutputFileName = _namingService.EnsureValidPdfFileName(sheet.OutputFileName);
            }

            sheet.Status = needsConfirm ? SheetStatus.NeedsConfirmation : SheetStatus.Ready;
        }

        public static void ApplyMediaToSheet(PrintSheet sheet, PaperMedia media)
        {
            if (sheet == null || media == null) return;
            sheet.TargetPaperCanonicalName = media.CanonicalName;
            sheet.TargetPaperLocaleName = media.LocaleName;
            sheet.PaperWidthMm = media.WidthMm;
            sheet.PaperHeightMm = media.HeightMm;
            sheet.PaperMarginsMm = media.MarginsMm;
        }

        private static void ClearMediaOnSheet(PrintSheet sheet)
        {
            sheet.TargetPaperCanonicalName = "";
            sheet.TargetPaperLocaleName = "(未匹配/需确认)";
            sheet.PaperWidthMm = 0;
            sheet.PaperHeightMm = 0;
            sheet.PaperMarginsMm = new PaperMargins();
        }

        private static PaperMedia FindMediaByCanonicalName(IList<PaperMedia> mediaList, string canonicalName)
        {
            if (mediaList == null || string.IsNullOrWhiteSpace(canonicalName)) return null;
            return mediaList.FirstOrDefault(m =>
                string.Equals(m.CanonicalName, canonicalName, StringComparison.OrdinalIgnoreCase));
        }
    }
}
