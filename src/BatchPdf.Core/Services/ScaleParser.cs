using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using BatchPdf.Core.Models;

namespace BatchPdf.Core.Services
{
    public static class ScaleParser
    {
        private static readonly Regex RatioRegex = new Regex(
            @"(?:(\d+(?:\.\d+)?)\s*[:/]\s*(\d+(?:\.\d+)?))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex NumberRegex = new Regex(
            @"^\s*(\d+(?:\.\d+)?)\s*$",
            RegexOptions.Compiled);

        private static readonly double[] PrimaryEngineeringScales = new double[]
        {
            100.0, 1.0, 50.0, 200.0, 150.0, 25.0, 20.0, 30.0, 40.0, 10.0,
            5.0, 2.0, 15.0, 60.0, 75.0, 80.0, 125.0, 250.0, 300.0,
            400.0, 500.0, 1000.0, 2000.0
        };

        private static readonly double[] SecondaryEngineeringScales = new double[]
        {
            3.0, 4.0, 6.0, 8.0, 12.0, 35.0, 45.0, 55.0, 65.0, 70.0,
            85.0, 90.0, 110.0, 120.0, 130.0, 140.0, 160.0, 175.0, 180.0,
            350.0, 600.0, 750.0, 800.0, 1500.0
        };

        /// <summary>
        /// GB/T 50001 & ISO standard and lengthened drawing sheet sizes (LongEdgeMm, ShortEdgeMm, ShortName).
        /// </summary>
        public static readonly Tuple<double, double, string>[] StandardGbSheetSizesMm = new Tuple<double, double, string>[]
        {
            Tuple.Create(1189.0, 841.0, "A0"),
            Tuple.Create(1486.0, 841.0, "A0+1/4"),
            Tuple.Create(1783.0, 841.0, "A0+1/2"),
            Tuple.Create(2080.0, 841.0, "A0+3/4"),
            Tuple.Create(2378.0, 841.0, "A0+1"),

            Tuple.Create(841.0, 594.0, "A1"),
            Tuple.Create(1051.0, 594.0, "A1+1/4"),
            Tuple.Create(1261.0, 594.0, "A1+1/2"),
            Tuple.Create(1471.0, 594.0, "A1+3/4"),
            Tuple.Create(1682.0, 594.0, "A1+1"),
            Tuple.Create(1892.0, 594.0, "A1+5/4"),
            Tuple.Create(2102.0, 594.0, "A1+3/2"),

            Tuple.Create(594.0, 420.0, "A2"),
            Tuple.Create(743.0, 420.0, "A2+1/4"),
            Tuple.Create(891.0, 420.0, "A2+1/2"),
            Tuple.Create(1041.0, 420.0, "A2+3/4"),
            Tuple.Create(1189.0, 420.0, "A2+1"),
            Tuple.Create(1338.0, 420.0, "A2+5/4"),
            Tuple.Create(1486.0, 420.0, "A2+3/2"),

            Tuple.Create(420.0, 297.0, "A3"),
            Tuple.Create(525.0, 297.0, "A3+1/4"),
            Tuple.Create(630.0, 297.0, "A3+1/2"),
            Tuple.Create(735.0, 297.0, "A3+3/4"),
            Tuple.Create(841.0, 297.0, "A3+1"),
            Tuple.Create(946.0, 297.0, "A3+5/4"),
            Tuple.Create(1051.0, 297.0, "A3+3/2"),
            Tuple.Create(1261.0, 297.0, "A3+2"),

            Tuple.Create(297.0, 210.0, "A4"),

            // GB/T 50001 inner border sizes (when outer border line is omitted, leaving the inner frame after 25mm/10mm margins)
            Tuple.Create(1154.0, 821.0, "A0(内框)"),
            Tuple.Create(806.0, 574.0, "A1(内框)"),
            Tuple.Create(1016.0, 574.0, "A1+1/4(内框)"),
            Tuple.Create(1226.0, 574.0, "A1+1/2(内框)"),
            Tuple.Create(559.0, 400.0, "A2(内框)"),
            Tuple.Create(708.0, 400.0, "A2+1/4(内框)"),
            Tuple.Create(856.0, 400.0, "A2+1/2(内框)"),
            Tuple.Create(1006.0, 400.0, "A2+3/4(内框)"),
            Tuple.Create(1154.0, 400.0, "A2+1(内框)"),
            Tuple.Create(385.0, 287.0, "A3(内框)")
        };

        /// <summary>
        /// Parses scale text such as "1:100", "1/50", "100", "1:1" into the scale denominator (e.g. 100.0).
        /// Returns null if the text cannot be reliably parsed.
        /// </summary>
        public static double? TryParseScaleValue(string scaleText)
        {
            if (string.IsNullOrWhiteSpace(scaleText))
            {
                return null;
            }

            string trimmed = scaleText.Trim();
            Match ratioMatch = RatioRegex.Match(trimmed);
            if (ratioMatch.Success)
            {
                if (double.TryParse(ratioMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double num) &&
                    double.TryParse(ratioMatch.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double den) &&
                    num > 0.0 && den > 0.0)
                {
                    return den / num;
                }
            }

            Match numMatch = NumberRegex.Match(trimmed);
            if (numMatch.Success)
            {
                if (double.TryParse(numMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double val) &&
                    val > 0.0)
                {
                    return val;
                }
            }

            return null;
        }

        /// <summary>
        /// Identifies the GB/T 50001 or ISO paper short name (e.g. "A1", "A2+1/4") for a given paper size in mm.
        /// </summary>
        public static string GetStandardPaperShortName(double widthMm, double heightMm, double toleranceMm = 8.0)
        {
            double longMm = Math.Max(widthMm, heightMm);
            double shortMm = Math.Min(widthMm, heightMm);

            foreach (var gb in StandardGbSheetSizesMm)
            {
                if (Math.Abs(longMm - gb.Item1) <= toleranceMm &&
                    Math.Abs(shortMm - gb.Item2) <= toleranceMm)
                {
                    return gb.Item3;
                }
            }

            return string.Format("{0:F0}x{1:F0}", longMm, shortMm);
        }

        /// <summary>
        /// Checks whether a frame at the given scale matches any standard GB/T 50001 / ISO paper size or PC3 paper size.
        /// </summary>
        public static bool MatchesStandardGbOrCatalogPaper(
            double frameWidthUnits,
            double frameHeightUnits,
            DrawingUnits drawingUnits,
            double scaleValue,
            IList<PaperMedia> availableMedia,
            double toleranceMm = 10.0)
        {
            if (frameWidthUnits <= 0 || frameHeightUnits <= 0 || scaleValue <= 0)
            {
                return false;
            }

            double unitToMm = drawingUnits.ToMillimetersFactor();
            double targetLong = Math.Max(frameWidthUnits, frameHeightUnits) * unitToMm / scaleValue;
            double targetShort = Math.Min(frameWidthUnits, frameHeightUnits) * unitToMm / scaleValue;

            foreach (var gb in StandardGbSheetSizesMm)
            {
                if (Math.Abs(gb.Item1 - targetLong) <= toleranceMm &&
                    Math.Abs(gb.Item2 - targetShort) <= toleranceMm)
                {
                    return true;
                }
            }

            if (availableMedia != null)
            {
                foreach (var media in availableMedia)
                {
                    if (Math.Abs(media.LongEdgeMm - targetLong) <= toleranceMm &&
                        Math.Abs(media.ShortEdgeMm - targetShort) <= toleranceMm)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Attempts to infer whether a frame's dimensions in drawing units correspond to an exact standard scale
        /// against any available paper in the PC3 catalog OR any standard GB/T 50001 / ISO sheet size.
        /// </summary>
        public static bool TryInferScaleFromAvailableMedia(
            double frameWidthUnits,
            double frameHeightUnits,
            DrawingUnits drawingUnits,
            IList<PaperMedia> availableMedia,
            double toleranceMm,
            out double inferredScale,
            out PaperMedia matchedMedia)
        {
            inferredScale = 0;
            matchedMedia = null;

            if (frameWidthUnits <= 0 || frameHeightUnits <= 0)
            {
                return false;
            }

            double unitToMm = drawingUnits.ToMillimetersFactor();
            double frameLongMmAt1To1 = Math.Max(frameWidthUnits, frameHeightUnits) * unitToMm;
            double frameShortMmAt1To1 = Math.Min(frameWidthUnits, frameHeightUnits) * unitToMm;

            // 1. Try primary scales against PC3 availableMedia and GB sizes
            bool hasPrimary = TryMatchScaleAgainstCatalogAndGb(
                frameLongMmAt1To1, frameShortMmAt1To1,
                PrimaryEngineeringScales, availableMedia, toleranceMm,
                out double primaryScale, out PaperMedia primaryMedia, out double primaryDev);

            if (hasPrimary && primaryDev <= 1.2)
            {
                inferredScale = primaryScale;
                matchedMedia = primaryMedia;
                return true;
            }

            // 2. Try secondary scales (e.g. 1:55, 1:45, 1:120) against PC3 availableMedia and GB sizes
            bool hasSecondary = TryMatchScaleAgainstCatalogAndGb(
                frameLongMmAt1To1, frameShortMmAt1To1,
                SecondaryEngineeringScales, availableMedia, toleranceMm,
                out double secondaryScale, out PaperMedia secondaryMedia, out double secondaryDev);

            if (hasPrimary && hasSecondary)
            {
                if (secondaryDev + 1.5 < primaryDev)
                {
                    inferredScale = secondaryScale;
                    matchedMedia = secondaryMedia;
                    return true;
                }

                inferredScale = primaryScale;
                matchedMedia = primaryMedia;
                return true;
            }

            if (hasPrimary)
            {
                inferredScale = primaryScale;
                matchedMedia = primaryMedia;
                return true;
            }

            if (hasSecondary)
            {
                inferredScale = secondaryScale;
                matchedMedia = secondaryMedia;
                return true;
            }

            return false;
        }

        private static bool TryMatchScaleAgainstCatalogAndGb(
            double frameLongMmAt1To1,
            double frameShortMmAt1To1,
            double[] candidateScales,
            IList<PaperMedia> availableMedia,
            double toleranceMm,
            out double inferredScale,
            out PaperMedia matchedMedia,
            out double bestDeviation)
        {
            inferredScale = 0;
            matchedMedia = null;
            bestDeviation = double.MaxValue;

            double bestScale = 0;
            PaperMedia bestMedia = null;

            foreach (double candidateScale in candidateScales)
            {
                double targetLong = frameLongMmAt1To1 / candidateScale;
                double targetShort = frameShortMmAt1To1 / candidateScale;

                if (availableMedia != null)
                {
                    foreach (var media in availableMedia)
                    {
                        double dLong = Math.Abs(media.LongEdgeMm - targetLong);
                        double dShort = Math.Abs(media.ShortEdgeMm - targetShort);
                        if (dLong <= toleranceMm && dShort <= toleranceMm)
                        {
                            double dev = dLong + dShort;
                            if (dev < bestDeviation - 0.1)
                            {
                                bestDeviation = dev;
                                bestScale = candidateScale;
                                bestMedia = media;
                            }
                        }
                    }
                }

                // Also check standard GB/T 50001 sizes (including A2+1/4 743x420, A1+1/4 1051x594, etc.)
                foreach (var gb in StandardGbSheetSizesMm)
                {
                    double dLong = Math.Abs(gb.Item1 - targetLong);
                    double dShort = Math.Abs(gb.Item2 - targetShort);
                    if (dLong <= toleranceMm && dShort <= toleranceMm)
                    {
                        double innerPenalty = gb.Item3.Contains("内框") ? 2.5 : 0.05;
                        double dev = dLong + dShort + innerPenalty;
                        if (dev < bestDeviation - 0.1)
                        {
                            bestDeviation = dev;
                            bestScale = candidateScale;
                        }
                    }
                }
            }

            if (bestScale > 0)
            {
                inferredScale = bestScale;
                matchedMedia = bestMedia;
                return true;
            }

            return false;
        }
    }
}
