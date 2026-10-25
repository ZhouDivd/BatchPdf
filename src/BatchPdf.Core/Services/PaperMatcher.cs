using System;
using System.Collections.Generic;
using System.Linq;
using BatchPdf.Core.Models;

namespace BatchPdf.Core.Services
{
    public class PaperMatcher
    {
        /// <summary>
        /// Calculates the expected physical paper dimensions in millimeters given the frame size in drawing units,
        /// the drawing unit type, and the scale denominator (e.g. 100 for 1:100).
        /// </summary>
        public TargetPaperSize EstimateTargetPaper(
            double frameWidthUnits,
            double frameHeightUnits,
            DrawingUnits drawingUnits,
            double? scaleValue)
        {
            if (frameWidthUnits <= 0 || frameHeightUnits <= 0 || !scaleValue.HasValue || scaleValue.Value <= 0)
            {
                return new TargetPaperSize
                {
                    ExpectedWidthMm = 0,
                    ExpectedHeightMm = 0
                };
            }

            double unitFactorMm = drawingUnits.ToMillimetersFactor();
            double expectedWidthMm = (frameWidthUnits * unitFactorMm) / scaleValue.Value;
            double expectedHeightMm = (frameHeightUnits * unitFactorMm) / scaleValue.Value;

            return new TargetPaperSize
            {
                ExpectedWidthMm = expectedWidthMm,
                ExpectedHeightMm = expectedHeightMm
            };
        }

        /// <summary>
        /// Matches the target paper size against the dynamic list of PaperMedia returned by DWG To PDF.pc3.
        /// Considers both 0-degree and 90-degree paper orientations.
        /// Never fabricates paper sizes or downgrades to a smaller paper that would clip the drawing.
        /// </summary>
        public PaperMatchResult MatchAvailableMedia(
            TargetPaperSize target,
            IList<PaperMedia> mediaList,
            double toleranceMm = 8.0)
        {
            var result = new PaperMatchResult();

            if (target == null || !target.IsValid)
            {
                result.IsMatched = false;
                result.Message = "比例未知或图框尺寸无效，无法计算目标图幅尺寸，请先确认比例。";
                return result;
            }

            if (mediaList == null || mediaList.Count == 0)
            {
                result.IsMatched = false;
                result.Message = "DWG To PDF.pc3 纸张列表为空，请检查绘图仪配置。";
                return result;
            }

            PaperMedia bestMedia = null;
            PlotRotationMode bestRotation = PlotRotationMode.Degrees0;
            double bestScore = double.MaxValue;
            double bestWidthDiff = 0;
            double bestHeightDiff = 0;
            bool bestHasClipping = false;

            var rankedCandidates = new List<Tuple<double, PaperMedia>>();

            foreach (var media in mediaList)
            {
                if (media == null || media.WidthMm <= 0 || media.HeightMm <= 0)
                {
                    continue;
                }

                // Candidate Orientation A: Direct (0 deg) -> target.ExpectedWidthMm vs media.WidthMm
                double diffW0 = media.WidthMm - target.ExpectedWidthMm;
                double diffH0 = media.HeightMm - target.ExpectedHeightMm;
                double absDev0 = Math.Abs(diffW0) + Math.Abs(diffH0);

                // Candidate Orientation B: Rotated 90 deg -> target.ExpectedWidthMm vs media.HeightMm
                double diffW90 = media.HeightMm - target.ExpectedWidthMm;
                double diffH90 = media.WidthMm - target.ExpectedHeightMm;
                double absDev90 = Math.Abs(diffW90) + Math.Abs(diffH90);

                bool use90 = absDev90 < absDev0;
                double diffW = use90 ? diffW90 : diffW0;
                double diffH = use90 ? diffH90 : diffH0;
                double absDev = use90 ? absDev90 : absDev0;
                PlotRotationMode rot = use90 ? PlotRotationMode.Degrees90 : PlotRotationMode.Degrees0;

                rankedCandidates.Add(Tuple.Create(absDev, media));

                if (Math.Abs(diffW) <= toleranceMm && Math.Abs(diffH) <= toleranceMm)
                {
                    double printableW = use90 ? media.PrintableHeightMm : media.PrintableWidthMm;
                    double printableH = use90 ? media.PrintableWidthMm : media.PrintableHeightMm;

                    bool clipsMargins = (target.ExpectedWidthMm - printableW > 1.5) ||
                                        (target.ExpectedHeightMm - printableH > 1.5);

                    bool isFullBleed = (media.CanonicalName ?? "").IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       (media.LocaleName ?? "").IndexOf("full bleed", StringComparison.OrdinalIgnoreCase) >= 0;

                    double score = absDev
                        + (clipsMargins ? 50.0 : 0.0)
                        - (isFullBleed ? 0.2 : 0.0)
                        + (use90 ? 0.05 : 0.0);

                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestMedia = media;
                        bestRotation = rot;
                        bestWidthDiff = diffW;
                        bestHeightDiff = diffH;
                        bestHasClipping = clipsMargins;
                    }
                }
            }

            result.ClosestCandidates = rankedCandidates
                .OrderBy(t => t.Item1)
                .Take(3)
                .Select(t => t.Item2)
                .ToList();

            if (bestMedia != null)
            {
                result.IsMatched = true;
                result.MatchedMedia = bestMedia;
                result.RecommendedPlotRotation = bestRotation;
                result.WidthDifferenceMm = bestWidthDiff;
                result.HeightDifferenceMm = bestHeightDiff;
                result.HasClippingRisk = bestHasClipping;
                result.Message = bestHasClipping
                    ? string.Format("已匹配 {0}，但纸张可打印边距可能裁切外框（建议选用 full_bleed 无边距纸张）", bestMedia.DisplayLabel)
                    : string.Format("已匹配 {0} (偏差 {1:F1}x{2:F1}mm)", bestMedia.DisplayLabel, bestWidthDiff, bestHeightDiff);
            }
            else
            {
                result.IsMatched = false;
                string closestHint = result.ClosestCandidates.Count > 0
                    ? string.Join(", ", result.ClosestCandidates.Select(c => c.DisplayLabel))
                    : "无";
                result.Message = string.Format(
                    "PC3 缺少匹配纸张：预期纸面尺寸 {0:F0}x{1:F0} mm。最接近候选：{2}。请在 DWG To PDF.pc3 中添加对应自定义/加长纸张或手动确认。",
                    target.ExpectedWidthMm,
                    target.ExpectedHeightMm,
                    closestHint);
            }

            return result;
        }

        /// <summary>
        /// When a drawing uses a GB/T 50001 lengthened frame (e.g. A2+1/4 = 743x420mm) and the current PC3
        /// only has standard ISO media (A0/A1/A2/A3/A4), selects the smallest enclosing full-bleed media
        /// (or closest base media when FitToPaper is enabled) so plotting works immediately without blocking.
        /// </summary>
        public bool TryMatchEnclosingStandardMedia(
            TargetPaperSize target,
            IList<PaperMedia> mediaList,
            bool fitToPaper,
            out PaperMedia fallbackMedia,
            out PlotRotationMode rotation,
            out string warningNote)
        {
            fallbackMedia = null;
            rotation = PlotRotationMode.Degrees0;
            warningNote = null;

            if (target == null || !target.IsValid || mediaList == null || mediaList.Count == 0)
            {
                return false;
            }

            string gbShortName = ScaleParser.GetStandardPaperShortName(target.ExpectedWidthMm, target.ExpectedHeightMm);
            double bestArea = double.MaxValue;

            foreach (var media in mediaList)
            {
                if (media == null || media.WidthMm <= 0 || media.HeightMm <= 0) continue;

                // Orientation 0 deg
                bool encloses0 = media.WidthMm + 2.0 >= target.ExpectedWidthMm &&
                                 media.HeightMm + 2.0 >= target.ExpectedHeightMm;
                // Orientation 90 deg
                bool encloses90 = media.HeightMm + 2.0 >= target.ExpectedWidthMm &&
                                  media.WidthMm + 2.0 >= target.ExpectedHeightMm;

                if (!encloses0 && !encloses90)
                {
                    continue;
                }

                bool isFullBleed = (media.CanonicalName ?? "").IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   (media.LocaleName ?? "").IndexOf("full bleed", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isIsoA = (media.CanonicalName ?? "").IndexOf("ISO", StringComparison.OrdinalIgnoreCase) >= 0 &&
                              ((media.CanonicalName ?? "").IndexOf("_A", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               (media.LocaleName ?? "").IndexOf(" A", StringComparison.OrdinalIgnoreCase) >= 0);

                double areaScore = (media.WidthMm * media.HeightMm)
                                 + (media.MarginsMm.Left + media.MarginsMm.Right + media.MarginsMm.Top + media.MarginsMm.Bottom) * 5000.0
                                 - (isFullBleed ? 200000.0 : 0.0)
                                 - (isIsoA ? 800000.0 : 0.0);

                if (areaScore < bestArea)
                {
                    bestArea = areaScore;
                    fallbackMedia = media;
                    rotation = encloses0 ? PlotRotationMode.Degrees0 : PlotRotationMode.Degrees90;
                }
            }

            if (fallbackMedia != null)
            {
                warningNote = string.Format(
                    "识别为加长图幅 {0} ({1:F0}x{2:F0}mm)，当前 PC3 无该尺寸，已自动匹配可容纳纸张: {3}",
                    gbShortName,
                    target.ExpectedWidthMm,
                    target.ExpectedHeightMm,
                    fallbackMedia.DisplayLabel);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Validates whether the chosen paper media and scale can accommodate the frame without clipping.
        /// Used for both auto-matched and manually selected media.
        /// </summary>
        public PrintableAreaValidationResult ValidatePrintableArea(
            double frameWidthUnits,
            double frameHeightUnits,
            DrawingUnits drawingUnits,
            PaperMedia media,
            double? scaleValue,
            bool fitToPaper)
        {
            var res = new PrintableAreaValidationResult();

            if (media == null)
            {
                res.IsValid = false;
                res.WarningMessage = "未选择有效纸张媒体。";
                return res;
            }

            if (fitToPaper)
            {
                res.IsValid = true;
                res.HasClippingRisk = false;
                res.PrintableWidthMm = media.PrintableWidthMm;
                res.PrintableHeightMm = media.PrintableHeightMm;
                res.WarningMessage = "当前启用“适合纸张(Fit to Paper)”，实际输出比例将被改变。";
                return res;
            }

            if (!scaleValue.HasValue || scaleValue.Value <= 0)
            {
                res.IsValid = false;
                res.WarningMessage = "打印比例未确定，请设置固定比例或确认比例来源。";
                return res;
            }

            TargetPaperSize target = EstimateTargetPaper(frameWidthUnits, frameHeightUnits, drawingUnits, scaleValue);
            res.ExpectedPaperWidthMm = target.ExpectedWidthMm;
            res.ExpectedPaperHeightMm = target.ExpectedHeightMm;

            double mediaLong = media.LongEdgeMm;
            double mediaShort = media.ShortEdgeMm;
            double printableLong = Math.Max(media.PrintableWidthMm, media.PrintableHeightMm);
            double printableShort = Math.Min(media.PrintableWidthMm, media.PrintableHeightMm);

            res.PrintableWidthMm = printableLong;
            res.PrintableHeightMm = printableShort;

            if (target.LongEdgeMm > mediaLong + 2.0 || target.ShortEdgeMm > mediaShort + 2.0)
            {
                res.IsValid = false;
                res.HasClippingRisk = true;
                res.WarningMessage = string.Format(
                    "裁切风险：按比例 1:{0:G} 计算图幅为 {1:F0}x{2:F0}mm，超出所选纸张物理尺寸 {3:F0}x{4:F0}mm！",
                    scaleValue.Value,
                    target.ExpectedWidthMm,
                    target.ExpectedHeightMm,
                    media.WidthMm,
                    media.HeightMm);
                return res;
            }

            if (target.LongEdgeMm > printableLong + 2.0 || target.ShortEdgeMm > printableShort + 2.0)
            {
                res.IsValid = true;
                res.HasClippingRisk = true;
                res.WarningMessage = string.Format(
                    "边距提示：图幅 {0:F0}x{1:F0}mm 超出纸张可打印区域 {2:F0}x{3:F0}mm，外框边线可能被页边距裁切。",
                    target.ExpectedWidthMm,
                    target.ExpectedHeightMm,
                    printableLong,
                    printableShort);
                return res;
            }

            res.IsValid = true;
            res.HasClippingRisk = false;
            return res;
        }
    }
}
