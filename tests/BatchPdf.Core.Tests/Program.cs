using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BatchPdf.Core.Configuration;
using BatchPdf.Core.Models;
using BatchPdf.Core.Services;
using BatchPdf.Infrastructure.Services;

namespace BatchPdf.Core.Tests
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            Console.WriteLine("=== Running BatchPdf.Core & Infrastructure Unit Tests ===");

            RunTest("RectangleGeometryAnalyzer_StandardAndCollinearRectangles", TestStandardAndCollinearRectangles);
            RunTest("RectangleGeometryAnalyzer_RotatedRectangle30Deg", TestRotatedRectangle30Deg);
            RunTest("RectangleGeometryAnalyzer_RejectsNonRectangleAndBulge", TestRejectsNonRectangleAndBulge);
            RunTest("RectangleGeometryAnalyzer_DeduplicatesInnerBorderAndSortsReadingOrder", TestDeduplicateAndRank);
            RunTest("RectangleGeometryAnalyzer_AllSortOrderDirectionsAndPrintSheetResort", TestAllSortOrderDirections);
            RunTest("ScaleParser_ParsesValidAndInvalidScales", TestScaleParser);
            RunTest("PaperMatcher_ExactAndScaledAndPortraitMatch", TestPaperMatcherStandard);
            RunTest("PaperMatcher_LengthenedPaperMatchedAndUnmatched", TestPaperMatcherLengthened);
            RunTest("PaperMatcher_UnknownScaleRequiresConfirmation", TestPaperMatcherUnknownScale);
            RunTest("SheetPreparationService_AutoAndManualPaperModes", TestSheetPreparationService);
            RunTest("NamingService_FormattingSanitizingAndDuplicateResolution", TestNamingService);
            RunTest("ConfigService_RoundTripJsonPersistence", TestConfigServiceRoundTrip);
            RunTest("PdfPageCombiner_CombinesMultiPageWithMixedMediaBoxes", TestPdfPageCombiner);
            RunTest("PdfPageCombiner_EncodePdfTextString_AsciiAndUnicode", TestPdfPageCombiner_EncodePdfTextString_AsciiAndUnicode);
            RunTest("NamingService_FormatSheetBookmarkTitle", TestNamingService_FormatSheetBookmarkTitle);
            RunTest("PdfPageCombiner_CombinesWithBookmarks_GeneratesValidOutlinesAndPageMode", TestPdfPageCombiner_CombinesWithBookmarks_GeneratesValidOutlinesAndPageMode);
            RunTest("PdfPageCropper_CropsLengthenedAndSkipsStandard", TestPdfPageCropper_CropsLengthenedAndSkipsStandard);
            RunTest("TitleBlockTextAnalyzer_LabelValueSpatialProximity", TestTitleBlockTextAnalyzer_LabelValueSpatialProximity);
            RunTest("TitleBlockTextAnalyzer_DirectInTextColonLabelValue", TestTitleBlockTextAnalyzer_DirectInTextColonLabelValue);
            RunTest("TitleBlockTextAnalyzer_BottomRightLargestTextHeightHeuristic", TestTitleBlockTextAnalyzer_BottomRightLargestTextHeightHeuristic);
            RunTest("TitleBlockTextAnalyzer_EnhancedDrawingNumberRegexPatterns", TestTitleBlockTextAnalyzer_EnhancedDrawingNumberRegexPatterns);
            RunTest("TitleBlockTextAnalyzer_RejectsRebarAndDetailAnnotationsInFavorOfRealTitle", TestTitleBlockTextAnalyzer_RejectsRebarAndDetailAnnotationsInFavorOfRealTitle);
            RunTest("TitleBlockTextAnalyzer_BilingualLabelsAndCentering", TestTitleBlockTextAnalyzer_BilingualLabelsAndCentering);
            RunTest("TitleBlockTextAnalyzer_EnglishCellCenteredAndDrawingContentExclusion", TestTitleBlockTextAnalyzer_EnglishCellCenteredAndDrawingContentExclusion);
            RunTest("TitleBlockTextAnalyzer_RealWorldKeyPlanAndStackedTitleLabel", TestTitleBlockTextAnalyzer_RealWorldKeyPlanAndStackedTitleLabel);
            RunTest("TitleBlockTextAnalyzer_RejectsStructuralMemberCodesAsDrawingNumber", TestTitleBlockTextAnalyzer_RejectsStructuralMemberCodesAsDrawingNumber);
            RunTest("TitleBlockTextAnalyzer_CustomFrameRuleTags", TestTitleBlockTextAnalyzer_CustomFrameRuleTags);
            RunTest("DrawingIndexTableLayoutService_AdaptiveColumnWidthAndRowHeight", TestDrawingIndexTableLayoutService_AdaptiveColumnWidthAndRowHeight);
            RunTest("SheetPreparationService_PaperSpaceLayoutSupport", TestSheetPreparationService_PaperSpaceLayoutSupport);

            Console.WriteLine("---------------------------------------------------------");
            Console.WriteLine(string.Format("Total: {0}, Passed: {1}, Failed: {2}", _passed + _failed, _passed, _failed));
            return _failed == 0 ? 0 : 1;
        }

        private static void RunTest(string name, Action testAction)
        {
            try
            {
                testAction();
                _passed++;
                Console.WriteLine("[PASS] " + name);
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("[FAIL] " + name + " -> " + ex.Message);
            }
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void AssertClose(double actual, double expected, double tolerance, string message)
        {
            if (Math.Abs(actual - expected) > tolerance)
            {
                throw new InvalidOperationException(string.Format("{0} (Expected {1}, Actual {2})", message, expected, actual));
            }
        }

        private static void TestStandardAndCollinearRectangles()
        {
            var analyzer = new RectangleGeometryAnalyzer();

            // 6 vertices with 2 collinear midpoints on an 841x594 rectangle
            var verts = new List<Point2D>
            {
                new Point2D(0, 0),
                new Point2D(400, 0),
                new Point2D(841, 0),
                new Point2D(841, 594),
                new Point2D(300, 594),
                new Point2D(0, 594)
            };

            bool ok = analyzer.TryAnalyzeClosedPolyline(
                verts, null, 100.0,
                out Point2D[] corners, out double w, out double h, out double rot, out _);

            AssertTrue(ok, "Should recognize 6-vertex collinear closed polyline as rectangle");
            AssertClose(w, 841.0, 0.1, "Width should be 841");
            AssertClose(h, 594.0, 0.1, "Height should be 594");
            AssertClose(rot, 0.0, 0.01, "Rotation should be 0");
            AssertTrue(corners.Length == 4, "Should output 4 ordered corners");
        }

        private static void TestRotatedRectangle30Deg()
        {
            var analyzer = new RectangleGeometryAnalyzer();
            double angleRad = 30.0 * Math.PI / 180.0;
            Point2D origin = new Point2D(1000, 500);

            var verts = new List<Point2D>
            {
                origin,
                new Point2D(origin.X + 841, origin.Y).RotateAround(origin, angleRad),
                new Point2D(origin.X + 841, origin.Y + 594).RotateAround(origin, angleRad),
                new Point2D(origin.X, origin.Y + 594).RotateAround(origin, angleRad)
            };

            bool ok = analyzer.TryAnalyzeClosedPolyline(
                verts, null, 100.0,
                out Point2D[] corners, out double w, out double h, out double rot, out _);

            AssertTrue(ok, "Should recognize 30-deg rotated rectangle");
            AssertClose(w, 841.0, 0.1, "Rotated width should be 841");
            AssertClose(h, 594.0, 0.1, "Rotated height should be 594");
            AssertClose(rot * 180.0 / Math.PI, 30.0, 0.1, "Rotation angle should be 30 deg");
        }

        private static void TestRejectsNonRectangleAndBulge()
        {
            var analyzer = new RectangleGeometryAnalyzer();

            // Trapezoid
            var trap = new List<Point2D>
            {
                new Point2D(0, 0),
                new Point2D(841, 0),
                new Point2D(700, 594),
                new Point2D(0, 594)
            };
            AssertTrue(!analyzer.TryAnalyzeClosedPolyline(trap, null, 100, out _, out _, out _, out _, out _),
                "Trapezoid should be rejected");

            // Rectangle with arc bulge
            var rect = new List<Point2D>
            {
                new Point2D(0, 0),
                new Point2D(841, 0),
                new Point2D(841, 594),
                new Point2D(0, 594)
            };
            var bulges = new List<double> { 0.5, 0, 0, 0 };
            AssertTrue(!analyzer.TryAnalyzeClosedPolyline(rect, bulges, 100, out _, out _, out _, out _, out _),
                "Curved polyline with bulge should be rejected");
        }

        private static void TestDeduplicateAndRank()
        {
            var analyzer = new RectangleGeometryAnalyzer();

            var outer = new FrameCandidate
            {
                FrameHandle = "OUTER",
                FrameType = FrameType.Polyline,
                WcsBoundingBox = new Extents2D(0, 0, 841, 594),
                WidthDrawingUnits = 841,
                HeightDrawingUnits = 594
            };
            var inner = new FrameCandidate
            {
                FrameHandle = "INNER",
                FrameType = FrameType.Polyline,
                WcsBoundingBox = new Extents2D(25, 10, 831, 584),
                WidthDrawingUnits = 806,
                HeightDrawingUnits = 574
            };
            var topLeftSheet = new FrameCandidate
            {
                FrameHandle = "TOP_LEFT",
                FrameType = FrameType.Polyline,
                WcsBoundingBox = new Extents2D(0, 1000, 420, 1297),
                WidthDrawingUnits = 420,
                HeightDrawingUnits = 297
            };

            var ranked = analyzer.DeduplicateAndRank(new[] { inner, outer, topLeftSheet });
            AssertTrue(ranked.Count == 2, "Inner concentric border should be deduplicated, leaving 2 frames");
            AssertTrue(ranked[0].FrameHandle == "TOP_LEFT", "Top row frame should be ordered first");
            AssertTrue(ranked[1].FrameHandle == "OUTER", "Outer frame should be kept over inner border");
        }

        private static void TestAllSortOrderDirections()
        {
            var analyzer = new RectangleGeometryAnalyzer();

            // Mixed-size engineering frames in a 2x2 grid (A1 and A2 aligned on edges):
            // TL: A1 at (0, 1000) - (841, 1594)
            // TR: A2 at (1000, 1000) - (1594, 1420) (bottom-aligned with TL on top row)
            // BL: A2 at (0, 0) - (594, 420) (left-aligned with TL in left column)
            // BR: A1 at (1000, 0) - (1841, 594) (left-aligned with TR in right column)
            var tl = new FrameCandidate
            {
                FrameHandle = "TL",
                FrameType = FrameType.Polyline,
                WcsBoundingBox = new Extents2D(0, 1000, 841, 1594),
                WidthDrawingUnits = 841,
                HeightDrawingUnits = 594
            };
            var tr = new FrameCandidate
            {
                FrameHandle = "TR",
                FrameType = FrameType.Polyline,
                WcsBoundingBox = new Extents2D(1000, 1000, 1594, 1420),
                WidthDrawingUnits = 594,
                HeightDrawingUnits = 420
            };
            var bl = new FrameCandidate
            {
                FrameHandle = "BL",
                FrameType = FrameType.Polyline,
                WcsBoundingBox = new Extents2D(0, 0, 594, 420),
                WidthDrawingUnits = 594,
                HeightDrawingUnits = 420
            };
            var br = new FrameCandidate
            {
                FrameHandle = "BR",
                FrameType = FrameType.Polyline,
                WcsBoundingBox = new Extents2D(1000, 0, 1841, 594),
                WidthDrawingUnits = 841,
                HeightDrawingUnits = 594
            };

            var input = new[] { br, tl, bl, tr };

            // 1. RowFirst + (LeftToRight, TopToBottom)
            AssertOrder(analyzer.DeduplicateAndRank(input, SheetSortOrderMode.RowFirst, false, false),
                new[] { "TL", "TR", "BL", "BR" }, "RowFirst(L->R, T->B)");

            // 2. RowFirst + (RightToLeft, TopToBottom)
            AssertOrder(analyzer.DeduplicateAndRank(input, SheetSortOrderMode.RowFirst, true, false),
                new[] { "TR", "TL", "BR", "BL" }, "RowFirst(R->L, T->B)");

            // 3. RowFirst + (LeftToRight, BottomToTop)
            AssertOrder(analyzer.DeduplicateAndRank(input, SheetSortOrderMode.RowFirst, false, true),
                new[] { "BL", "BR", "TL", "TR" }, "RowFirst(L->R, B->T)");

            // 4. RowFirst + (RightToLeft, BottomToTop)
            AssertOrder(analyzer.DeduplicateAndRank(input, SheetSortOrderMode.RowFirst, true, true),
                new[] { "BR", "BL", "TR", "TL" }, "RowFirst(R->L, B->T)");

            // 5. ColumnFirst + (LeftToRight, TopToBottom)
            AssertOrder(analyzer.DeduplicateAndRank(input, SheetSortOrderMode.ColumnFirst, false, false),
                new[] { "TL", "BL", "TR", "BR" }, "ColumnFirst(L->R, T->B)");

            // 6. ColumnFirst + (RightToLeft, TopToBottom)
            AssertOrder(analyzer.DeduplicateAndRank(input, SheetSortOrderMode.ColumnFirst, true, false),
                new[] { "TR", "BR", "TL", "BL" }, "ColumnFirst(R->L, T->B)");

            // 7. ColumnFirst + (LeftToRight, BottomToTop)
            AssertOrder(analyzer.DeduplicateAndRank(input, SheetSortOrderMode.ColumnFirst, false, true),
                new[] { "BL", "TL", "BR", "TR" }, "ColumnFirst(L->R, B->T)");

            // 8. ColumnFirst + (RightToLeft, BottomToTop)
            AssertOrder(analyzer.DeduplicateAndRank(input, SheetSortOrderMode.ColumnFirst, true, true),
                new[] { "BR", "TR", "BL", "TL" }, "ColumnFirst(R->L, B->T)");

            // 9. SelectionOrder preserves original pick order regardless of reverseX/reverseY
            AssertOrder(analyzer.DeduplicateAndRank(input, SheetSortOrderMode.SelectionOrder, true, true),
                new[] { "BR", "TL", "BL", "TR" }, "SelectionOrder");

            // 10. SortPrintSheets re-sorting existing List<PrintSheet> and updating OrderIndex
            var sheets = new List<PrintSheet>
            {
                new PrintSheet { FrameHandle = "BR", DrawingNumber = "D-04", FrameExtents = br.WcsBoundingBox, FrameWidthUnits = br.WidthDrawingUnits, FrameHeightUnits = br.HeightDrawingUnits },
                new PrintSheet { FrameHandle = "TL", DrawingNumber = "D-01", FrameExtents = tl.WcsBoundingBox, FrameWidthUnits = tl.WidthDrawingUnits, FrameHeightUnits = tl.HeightDrawingUnits },
                new PrintSheet { FrameHandle = "BL", DrawingNumber = "D-03", FrameExtents = bl.WcsBoundingBox, FrameWidthUnits = bl.WidthDrawingUnits, FrameHeightUnits = bl.HeightDrawingUnits },
                new PrintSheet { FrameHandle = "TR", DrawingNumber = "D-02", FrameExtents = tr.WcsBoundingBox, FrameWidthUnits = tr.WidthDrawingUnits, FrameHeightUnits = tr.HeightDrawingUnits }
            };

            var sortedRowStandard = analyzer.SortPrintSheets(sheets, SheetSortOrderMode.RowFirst, false, false);
            AssertSheetOrder(sortedRowStandard, new[] { "TL", "TR", "BL", "BR" }, "SortPrintSheets RowFirst(L->R, T->B)");
            AssertTrue(sortedRowStandard[0].DrawingNumber == "D-01", "PrintSheet properties should be preserved after SortPrintSheets");

            var sortedColRev = analyzer.SortPrintSheets(sheets, SheetSortOrderMode.ColumnFirst, true, true);
            AssertSheetOrder(sortedColRev, new[] { "BR", "TR", "BL", "TL" }, "SortPrintSheets ColumnFirst(R->L, B->T)");

            // 11. Verify Chinese sort description helper
            AssertTrue(SheetSortOrderMode.RowFirst.GetSortOrderDescription(false, false) == "先行后列：从左往右 (→)，从上往下 (↓)", "RowFirst default description");
            AssertTrue(SheetSortOrderMode.RowFirst.GetSortOrderDescription(true, false) == "先行后列：从右往左 (←)，从上往下 (↓)", "RowFirst reverseX description");
            AssertTrue(SheetSortOrderMode.ColumnFirst.GetSortOrderDescription(false, false) == "先列后行：从上往下 (↓)，从左往右 (→)", "ColumnFirst default description");
            AssertTrue(SheetSortOrderMode.SelectionOrder.GetSortOrderDescription(true, true) == "按选择时的顺序", "SelectionOrder description");
        }

        private static void AssertOrder(IList<FrameCandidate> actual, string[] expectedHandles, string label)
        {
            AssertTrue(actual != null && actual.Count == expectedHandles.Length, label + ": count mismatch");
            for (int i = 0; i < expectedHandles.Length; i++)
            {
                if (actual[i].FrameHandle != expectedHandles[i])
                {
                    throw new InvalidOperationException(string.Format(
                        "{0}: index {1} expected {2} but got {3}",
                        label, i, expectedHandles[i], actual[i].FrameHandle));
                }
            }
        }

        private static void AssertSheetOrder(IList<PrintSheet> actual, string[] expectedHandles, string label)
        {
            AssertTrue(actual != null && actual.Count == expectedHandles.Length, label + ": count mismatch");
            for (int i = 0; i < expectedHandles.Length; i++)
            {
                if (actual[i].FrameHandle != expectedHandles[i])
                {
                    throw new InvalidOperationException(string.Format(
                        "{0}: index {1} expected {2} but got {3}",
                        label, i, expectedHandles[i], actual[i].FrameHandle));
                }
                AssertTrue(actual[i].OrderIndex == i + 1, label + ": OrderIndex should be updated to 1..N");
            }
        }

        private static void TestScaleParser()
        {
            AssertClose(ScaleParser.TryParseScaleValue("1:100").Value, 100.0, 1e-6, "1:100");
            AssertClose(ScaleParser.TryParseScaleValue(" 1 / 50 ").Value, 50.0, 1e-6, "1/50");
            AssertClose(ScaleParser.TryParseScaleValue("200").Value, 200.0, 1e-6, "200");
            AssertTrue(ScaleParser.TryParseScaleValue("无比例") == null, "Non-numeric scale should return null");
        }

        private static List<PaperMedia> CreateSampleCatalog()
        {
            return new List<PaperMedia>
            {
                new PaperMedia
                {
                    CanonicalName = "ISO_full_bleed_A1_(841.00_x_594.00_MM)",
                    LocaleName = "ISO full bleed A1 (841.00 x 594.00 毫米)",
                    WidthMm = 841.0,
                    HeightMm = 594.0,
                    MarginsMm = new PaperMargins(0, 0, 0, 0)
                },
                new PaperMedia
                {
                    CanonicalName = "ISO_full_bleed_A2_(594.00_x_420.00_MM)",
                    LocaleName = "ISO full bleed A2 (594.00 x 420.00 毫米)",
                    WidthMm = 594.0,
                    HeightMm = 420.0,
                    MarginsMm = new PaperMargins(0, 0, 0, 0)
                },
                new PaperMedia
                {
                    CanonicalName = "ISO_full_bleed_A3_(420.00_x_297.00_MM)",
                    LocaleName = "ISO full bleed A3 (420.00 x 297.00 毫米)",
                    WidthMm = 420.0,
                    HeightMm = 297.0,
                    MarginsMm = new PaperMargins(0, 0, 0, 0)
                },
                new PaperMedia
                {
                    CanonicalName = "UserDefinedMetric (1261.00 x 841.00毫米)",
                    LocaleName = "A1+1/2加长 (1261.00 x 841.00 毫米)",
                    WidthMm = 1261.0,
                    HeightMm = 841.0,
                    MarginsMm = new PaperMargins(0, 0, 0, 0),
                    IsCustomOrLengthened = true
                }
            };
        }

        private static void TestPaperMatcherStandard()
        {
            var matcher = new PaperMatcher();
            var catalog = CreateSampleCatalog();

            // 1:100 scaled A1 landscape (84100 x 59400)
            var targetA1 = matcher.EstimateTargetPaper(84100, 59400, DrawingUnits.Millimeters, 100.0);
            var matchA1 = matcher.MatchAvailableMedia(targetA1, catalog);
            AssertTrue(matchA1.IsMatched, "84100x59400 at 1:100 should match A1");
            AssertTrue(matchA1.MatchedMedia.CanonicalName.Contains("A1"), "Should pick A1 media");
            AssertTrue(matchA1.RecommendedPlotRotation == PlotRotationMode.Degrees0, "Landscape on landscape A1 is 0 deg");

            // Portrait A3 (297 x 420 at 1:1) matched against 420x297 media -> 90 deg rotation
            var targetA3Portrait = matcher.EstimateTargetPaper(297, 420, DrawingUnits.Millimeters, 1.0);
            var matchA3 = matcher.MatchAvailableMedia(targetA3Portrait, catalog);
            AssertTrue(matchA3.IsMatched, "297x420 portrait should match A3 media via 90-deg rotation");
            AssertTrue(matchA3.RecommendedPlotRotation == PlotRotationMode.Degrees90, "Portrait on landscape media requires Degrees90");
        }

        private static void TestPaperMatcherLengthened()
        {
            var matcher = new PaperMatcher();
            var catalog = CreateSampleCatalog();

            // Configured lengthened paper (1261 x 841)
            var targetConfigured = matcher.EstimateTargetPaper(1261, 841, DrawingUnits.Millimeters, 1.0);
            var matchOk = matcher.MatchAvailableMedia(targetConfigured, catalog);
            AssertTrue(matchOk.IsMatched, "Configured lengthened paper 1261x841 should match");

            // Unconfigured lengthened paper (1892 x 841) -> must NOT match or silently downgrade to A1
            var targetUnconfigured = matcher.EstimateTargetPaper(1892, 841, DrawingUnits.Millimeters, 1.0);
            var matchFail = matcher.MatchAvailableMedia(targetUnconfigured, catalog);
            AssertTrue(!matchFail.IsMatched, "Unconfigured lengthened paper must NOT silently match a smaller paper");
            AssertTrue(matchFail.Message.Contains("PC3 缺少匹配纸张"), "Should explain missing paper in PC3");
        }

        private static void TestPaperMatcherUnknownScale()
        {
            var matcher = new PaperMatcher();
            var catalog = CreateSampleCatalog();

            var targetUnknown = matcher.EstimateTargetPaper(84100, 59400, DrawingUnits.Millimeters, null);
            var match = matcher.MatchAvailableMedia(targetUnknown, catalog);
            AssertTrue(!match.IsMatched, "Unknown scale must not guess paper");
        }

        private static void TestSheetPreparationService()
        {
            var prep = new SheetPreparationService();
            var catalog = CreateSampleCatalog();
            var settings = BatchPdfSettings.CreateDefault();
            settings.InferScaleFromStandardPaper = false;

            var candidateUnknownScale = new FrameCandidate
            {
                FrameHandle = "H1",
                FrameType = FrameType.Polyline,
                WidthDrawingUnits = 50000,
                HeightDrawingUnits = 35000
            };

            var sheets = prep.CreatePrintSheets(
                new List<FrameCandidate> { candidateUnknownScale },
                @"D:\Project\StructPlan.dwg",
                DrawingUnits.Millimeters,
                settings,
                catalog);

            AssertTrue(sheets.Count == 1, "Should create 1 PrintSheet");
            AssertTrue(sheets[0].Status == SheetStatus.NeedsConfirmation, "Unknown scale sheet must be marked NeedsConfirmation");
            AssertTrue(string.IsNullOrEmpty(sheets[0].TargetPaperCanonicalName), "Should pause auto paper match when scale is unknown");
        }

        private static void TestNamingService()
        {
            var naming = new NamingService();

            string name1 = naming.FormatSheetFileName("{图号}_{图名}.pdf", @"C:\Test\PlanA.dwg", 1, "S-01", "一层柱配筋图");
            AssertTrue(name1 == "S-01_一层柱配筋图.pdf", "Expected S-01_一层柱配筋图.pdf, got " + name1);

            string fallback = naming.FormatSheetFileName("{图号}_{图名}.pdf", @"C:\Test\PlanA.dwg", 3, "", "");
            AssertTrue(fallback == "PlanA_03.pdf", "Fallback should be PlanA_03.pdf, got " + fallback);

            string sanitized = naming.FormatSheetFileName("{图号}_{图名}.pdf", @"C:\Test\PlanA.dwg", 1, "S/01*A", "梁:配筋?图");
            AssertTrue(sanitized.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, "Should strip invalid filename chars");

            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string tempDir = Path.Combine(Path.GetTempPath(), "BatchPdfTest_" + Guid.NewGuid().ToString("N"));
            string p1 = naming.ResolveOutputFilePath(tempDir, "Sheet.pdf", DuplicateFilePolicy.AutoAppendIndex, reserved);
            string p2 = naming.ResolveOutputFilePath(tempDir, "Sheet.pdf", DuplicateFilePolicy.AutoAppendIndex, reserved);
            AssertTrue(!string.Equals(p1, p2, StringComparison.OrdinalIgnoreCase), "Duplicate in batch should auto-append index");
            AssertTrue(p2.EndsWith("Sheet_1.pdf", StringComparison.OrdinalIgnoreCase), "Second file should be Sheet_1.pdf");
        }

        private static void TestConfigServiceRoundTrip()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "BatchPdf_TestSettings_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var svc = new ConfigService(tempFile);
                var settings = BatchPdfSettings.CreateDefault();
                settings.OutputMode = OutputMode.MergedMultiPagePdf;
                settings.PaperMatchToleranceMm = 12.5;
                svc.SaveSettings(settings);

                var loaded = svc.LoadSettings();
                AssertTrue(loaded.OutputMode == OutputMode.MergedMultiPagePdf, "OutputMode should persist");
                AssertClose(loaded.PaperMatchToleranceMm, 12.5, 0.01, "PaperMatchToleranceMm should persist");
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        private static void TestPdfPageCombiner()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "BatchPdf_CombinerTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string page1 = Path.Combine(tempDir, "p1.pdf");
                string page2 = Path.Combine(tempDir, "p2.pdf");
                string merged = Path.Combine(tempDir, "merged.pdf");

                File.WriteAllText(page1, CreateMinimalSinglePagePdf(2384, 1684, "Page1Stream"));
                File.WriteAllText(page2, CreateMinimalSinglePagePdf(1191, 842, "Page2Stream"));

                var combiner = new PdfPageCombiner();
                combiner.CombineSinglePagePdfs(new[] { page1, page2 }, merged);

                AssertTrue(File.Exists(merged), "Merged PDF should exist");
                string mergedText = File.ReadAllText(merged);
                AssertTrue(mergedText.Contains("/Count 2"), "Merged PDF should have /Count 2");
                AssertTrue(mergedText.Contains("/MediaBox [0 0 2384 1684]"), "Merged PDF should preserve Page 1 A1 MediaBox");
                AssertTrue(mergedText.Contains("/MediaBox [0 0 1191 842]"), "Merged PDF should preserve Page 2 A3 MediaBox");
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
        }

        private static void TestPdfPageCombiner_EncodePdfTextString_AsciiAndUnicode()
        {
            string empty = PdfPageCombiner.EncodePdfTextString("");
            AssertTrue(empty == "()", "Empty string should encode as ()");

            string ascii = PdfPageCombiner.EncodePdfTextString("[A-01] First Floor");
            AssertTrue(ascii == "([A-01] First Floor)", "Safe ASCII should encode as literal string, got: " + ascii);

            string paren = PdfPageCombiner.EncodePdfTextString("Plan (Rev.1)");
            AssertTrue(paren.StartsWith("<FEFF"), "String with parentheses should encode as <FEFF...>, got: " + paren);

            string chinese = PdfPageCombiner.EncodePdfTextString("[建施-01] 一层平面图");
            AssertTrue(chinese.StartsWith("<FEFF") && chinese.EndsWith(">"), "Chinese string should encode as <FEFF...>, got: " + chinese);

            // Verify that the hex payload decodes back to the exact original Chinese string
            string hexData = chinese.Substring(5, chinese.Length - 6); // strip <FEFF and >
            byte[] bytes = new byte[hexData.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hexData.Substring(i * 2, 2), 16);
            }
            string decoded = Encoding.BigEndianUnicode.GetString(bytes);
            AssertTrue(decoded == "[建施-01] 一层平面图", "Decoded UTF-16BE hex should match original string, got: " + decoded);
        }

        private static void TestNamingService_FormatSheetBookmarkTitle()
        {
            var naming = new NamingService();

            var sheetBoth = new PrintSheet { DrawingNumber = "建施-01", DrawingName = "一层平面图" };
            AssertTrue(naming.FormatSheetBookmarkTitle(sheetBoth) == "[建施-01] 一层平面图", "Should format both drawing number and name");

            var sheetNumOnly = new PrintSheet { DrawingNumber = "建施-02", DrawingName = "" };
            AssertTrue(naming.FormatSheetBookmarkTitle(sheetNumOnly) == "[建施-02]", "Should format number only");

            var sheetNameOnly = new PrintSheet { DrawingNumber = null, DrawingName = "立面图" };
            AssertTrue(naming.FormatSheetBookmarkTitle(sheetNameOnly) == "立面图", "Should format name only");

            var sheetFileName = new PrintSheet { OutputFileName = "Building_Elev_01.pdf", OrderIndex = 3 };
            AssertTrue(naming.FormatSheetBookmarkTitle(sheetFileName) == "Building_Elev_01", "Should fall back to file name without extension");

            var sheetOrder = new PrintSheet { OrderIndex = 5 };
            AssertTrue(naming.FormatSheetBookmarkTitle(sheetOrder) == "图纸 #5", "Should fall back to order index");
        }

        private static void TestPdfPageCombiner_CombinesWithBookmarks_GeneratesValidOutlinesAndPageMode()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "BatchPdf_BookmarksTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string page1 = Path.Combine(tempDir, "p1.pdf");
                string page2 = Path.Combine(tempDir, "p2.pdf");
                string page3 = Path.Combine(tempDir, "p3.pdf");
                string merged = Path.Combine(tempDir, "merged_bookmarks.pdf");

                File.WriteAllText(page1, CreateMinimalSinglePagePdf(2384, 1684, "Page1Content"));
                File.WriteAllText(page2, CreateMinimalSinglePagePdf(1191, 842, "Page2Content"));
                File.WriteAllText(page3, CreateMinimalSinglePagePdf(842, 595, "Page3Content"));

                var bookmarks = new List<string>
                {
                    "[建施-01] 一层平面图",
                    "[建施-02] 二层平面图",
                    "[建施-03] 屋顶平面图"
                };

                var combiner = new PdfPageCombiner();
                combiner.CombineSinglePagePdfs(new[] { page1, page2, page3 }, merged, bookmarks);

                AssertTrue(File.Exists(merged), "Merged PDF with bookmarks should exist");
                byte[] mergedBytes = File.ReadAllBytes(merged);
                string mergedText = Encoding.GetEncoding("iso-8859-1").GetString(mergedBytes);

                // 1. Verify /Catalog has /PageMode /UseOutlines and /Outlines reference
                Match catMatch = Regex.Match(mergedText, @"<<\s*/Type\s*/Catalog\s+[^>]*>>");
                AssertTrue(catMatch.Success, "Merged PDF should have /Catalog object");
                string catContent = catMatch.Value;
                AssertTrue(catContent.Contains("/PageMode /UseOutlines"), "Catalog must have /PageMode /UseOutlines");
                Match outlinesRefMatch = Regex.Match(catContent, @"/Outlines\s+(\d+)\s+0\s+R\b");
                AssertTrue(outlinesRefMatch.Success, "Catalog must reference /Outlines");
                int outlineRootId = int.Parse(outlinesRefMatch.Groups[1].Value);

                // 2. Verify Outline Root Dictionary
                Match rootMatch = Regex.Match(mergedText, string.Format(@"(?:^|\r|\n)\s*{0}\s+0\s+obj\s*<<(.*?)>>\s*endobj", outlineRootId), RegexOptions.Singleline);
                AssertTrue(rootMatch.Success, "Outline Root object should exist");
                string rootContent = rootMatch.Groups[1].Value;
                AssertTrue(rootContent.Contains("/Type /Outlines"), "Outline Root must have /Type /Outlines");
                AssertTrue(rootContent.Contains("/Count 3"), "Outline Root must have /Count 3");

                Match firstMatch = Regex.Match(rootContent, @"/First\s+(\d+)\s+0\s+R\b");
                Match lastMatch = Regex.Match(rootContent, @"/Last\s+(\d+)\s+0\s+R\b");
                AssertTrue(firstMatch.Success && lastMatch.Success, "Outline Root must have /First and /Last");
                int firstItemId = int.Parse(firstMatch.Groups[1].Value);
                int lastItemId = int.Parse(lastMatch.Groups[1].Value);

                // 3. Verify each Outline Item
                // First item (0): has /Next, no /Prev, /Parent points to root
                Match item0Match = Regex.Match(mergedText, string.Format(@"(?:^|\r|\n)\s*{0}\s+0\s+obj\s*<<(.*?)>>\s*endobj", firstItemId), RegexOptions.Singleline);
                AssertTrue(item0Match.Success, "Item 0 object must exist");
                string item0Content = item0Match.Groups[1].Value;
                AssertTrue(item0Content.Contains(string.Format("/Parent {0} 0 R", outlineRootId)), "Item 0 /Parent must point to root");
                AssertTrue(!item0Content.Contains("/Prev"), "Item 0 must NOT have /Prev");
                Match next0Match = Regex.Match(item0Content, @"/Next\s+(\d+)\s+0\s+R\b");
                AssertTrue(next0Match.Success, "Item 0 must have /Next");
                int secondItemId = int.Parse(next0Match.Groups[1].Value);
                AssertTrue(item0Content.Contains("/Dest ["), "Item 0 must have /Dest");
                AssertTrue(item0Content.Contains("/Title <FEFF"), "Item 0 must have UTF-16BE /Title");

                // Middle item (1): has /Prev, has /Next
                Match item1Match = Regex.Match(mergedText, string.Format(@"(?:^|\r|\n)\s*{0}\s+0\s+obj\s*<<(.*?)>>\s*endobj", secondItemId), RegexOptions.Singleline);
                AssertTrue(item1Match.Success, "Item 1 object must exist");
                string item1Content = item1Match.Groups[1].Value;
                AssertTrue(item1Content.Contains(string.Format("/Prev {0} 0 R", firstItemId)), "Item 1 /Prev must point to Item 0");
                Match next1Match = Regex.Match(item1Content, @"/Next\s+(\d+)\s+0\s+R\b");
                AssertTrue(next1Match.Success, "Item 1 must have /Next");
                AssertTrue(int.Parse(next1Match.Groups[1].Value) == lastItemId, "Item 1 /Next must point to Item 2 (last)");

                // Last item (2): has /Prev, no /Next
                Match item2Match = Regex.Match(mergedText, string.Format(@"(?:^|\r|\n)\s*{0}\s+0\s+obj\s*<<(.*?)>>\s*endobj", lastItemId), RegexOptions.Singleline);
                AssertTrue(item2Match.Success, "Item 2 object must exist");
                string item2Content = item2Match.Groups[1].Value;
                AssertTrue(item2Content.Contains(string.Format("/Prev {0} 0 R", secondItemId)), "Item 2 /Prev must point to Item 1");
                AssertTrue(!item2Content.Contains("/Next"), "Item 2 must NOT have /Next");

                // 4. Verify Single Page PDF with Bookmarks works as well
                string singleMerged = Path.Combine(tempDir, "single_merged.pdf");
                combiner.CombineSinglePagePdfs(new[] { page1 }, singleMerged, new[] { "[建施-01] 封面图" });
                AssertTrue(File.Exists(singleMerged), "Single page PDF with bookmark should exist");
                string singleText = File.ReadAllText(singleMerged, Encoding.GetEncoding("iso-8859-1"));
                AssertTrue(singleText.Contains("/PageMode /UseOutlines"), "Single page PDF must have /PageMode /UseOutlines");
                AssertTrue(singleText.Contains("/Count 1"), "Single page PDF must have /Count 1");

                // 5. Verify xref offsets are exact
                Match xrefMatch = Regex.Match(mergedText, @"xref\r?\n0\s+(\d+)\r?\n");
                AssertTrue(xrefMatch.Success, "Merged PDF should have xref table");
                int xrefCount = int.Parse(xrefMatch.Groups[1].Value);
                int xrefDataStart = xrefMatch.Index + xrefMatch.Length;
                string[] xrefLines = mergedText.Substring(xrefDataStart).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                for (int objId = 1; objId < xrefCount; objId++)
                {
                    string entry = xrefLines[objId];
                    if (entry.EndsWith("n"))
                    {
                        long offset = long.Parse(entry.Substring(0, 10));
                        string objHeaderAtOffset = Encoding.GetEncoding("iso-8859-1").GetString(mergedBytes, (int)offset, 20);
                        AssertTrue(objHeaderAtOffset.StartsWith(objId.ToString() + " 0 obj"),
                            string.Format("Xref offset for obj {0} should point to '{0} 0 obj', got '{1}'", objId, objHeaderAtOffset));
                    }
                }
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
        }

        private static void TestPdfPageCropper_CropsLengthenedAndSkipsStandard()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "BatchPdf_CropperTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var cropper = new PdfPageCropper();

                // 1. Non-standard / lengthened frame (743x420mm) plotted centered on A1 paper (841x594mm) with /BBox [138 246 2244 1437]
                string lengthenedPdf = Path.Combine(tempDir, "lengthened.pdf");
                File.WriteAllText(lengthenedPdf, CreateMinimalSinglePagePdfWithViewportBBox(
                    2384, 1684, "138 246 2244 1437", "LengthenedFrameStream"));

                PdfCropResult res1 = cropper.CropPdfFile(lengthenedPdf, marginMm: 0.0);
                AssertTrue(res1.TotalPages == 1, "Should detect 1 page");
                AssertTrue(res1.CroppedPages == 1, "Non-standard frame on larger paper should be cropped");
                AssertTrue(res1.SkippedStandardPages == 0, "SkippedStandardPages should be 0");

                string croppedText = File.ReadAllText(lengthenedPdf);
                AssertTrue(
                    croppedText.Contains("/MediaBox [138.00 246.00 2244.00 1437.00]"),
                    "Cropped PDF should update /MediaBox to [138.00 246.00 2244.00 1437.00]");
                AssertTrue(
                    croppedText.Contains("/CropBox [138.00 246.00 2244.00 1437.00]"),
                    "Cropped PDF should set matching /CropBox");
                AssertTrue(
                    croppedText.Contains("LengthenedFrameStream"),
                    "Cropped PDF should preserve stream payload");

                // 1b. Re-crop the same PDF with a new marginMm = 25.4 mm (72.00 pt) -> [66.00 174.00 2316.00 1509.00]
                PdfCropResult resReCrop = cropper.CropPdfFile(lengthenedPdf, marginMm: 25.4);
                AssertTrue(resReCrop.CroppedPages == 1, "Re-cropping with a different margin should succeed via /OrigMediaBox");
                string recroppedText = File.ReadAllText(lengthenedPdf);
                AssertTrue(
                    recroppedText.Contains("/MediaBox [66.00 174.00 2316.00 1509.00]"),
                    "Re-cropped PDF should update /MediaBox with 25.4mm (72pt) margin");

                // 2. Standard A1 frame on A1 paper (/MediaBox [0 0 2384 1684], /BBox [0 0 2384 1684]) -> must NOT crop
                string standardPdf = Path.Combine(tempDir, "standard_a1.pdf");
                File.WriteAllText(standardPdf, CreateMinimalSinglePagePdfWithViewportBBox(
                    2384, 1684, "0 0 2384 1684", "StandardA1Stream"));

                PdfCropResult res2 = cropper.CropPdfFile(standardPdf, marginMm: 0.0);
                AssertTrue(res2.TotalPages == 1, "Should detect 1 page in standard PDF");
                AssertTrue(res2.CroppedPages == 0, "Standard A1 frame on A1 paper must NOT be cropped");
                AssertTrue(res2.SkippedStandardPages == 1, "Standard A1 frame should be counted as SkippedStandardPages == 1");

                string standardText = File.ReadAllText(standardPdf);
                AssertTrue(
                    standardText.Contains("/MediaBox [0 0 2384 1684]"),
                    "Standard PDF MediaBox must remain untouched");
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
        }

        private static string CreateMinimalSinglePagePdfWithViewportBBox(int widthPt, int heightPt, string bboxCoords, string streamPayload)
        {
            return string.Format(
@"%PDF-1.7
1 0 obj
<< /Type /OCG /Name (0) >>
endobj
2 0 obj
<< /Length {3} >>
stream
{4}
endstream
endobj
3 0 obj
<< /Type /Page /Parent 4 0 R /MediaBox [0 0 {0} {1}] /Contents [2 0 R] /VP [ << /Type /Viewport /BBox [{2}] >> ] >>
endobj
4 0 obj
<< /Type /Pages /Kids [3 0 R] /Count 1 >>
endobj
5 0 obj
<< /Type /Catalog /Pages 4 0 R >>
endobj
xref
0 6
0000000000 65535 f 
0000000010 00000 n 
0000000050 00000 n 
0000000110 00000 n 
0000000240 00000 n 
0000000290 00000 n 
trailer
<< /Size 6 /Root 5 0 R >>
startxref
340
%%EOF", widthPt, heightPt, bboxCoords, streamPayload.Length, streamPayload);
        }

        private static string CreateMinimalSinglePagePdf(int widthPt, int heightPt, string streamPayload)
        {
            return string.Format(
@"%PDF-1.7
1 0 obj
<< /Type /OCG /Name (0) >>
endobj
2 0 obj
<< /Length {2} >>
stream
{3}
endstream
endobj
3 0 obj
<< /Type /Page /Parent 4 0 R /MediaBox [0 0 {0} {1}] /Contents [2 0 R] >>
endobj
4 0 obj
<< /Type /Pages /Kids [3 0 R] /Count 1 >>
endobj
5 0 obj
<< /Type /Catalog /Pages 4 0 R >>
endobj
xref
0 6
0000000000 65535 f 
0000000010 00000 n 
0000000050 00000 n 
0000000110 00000 n 
0000000190 00000 n 
0000000240 00000 n 
trailer
<< /Size 6 /Root 5 0 R >>
startxref
290
%%EOF", widthPt, heightPt, streamPayload.Length, streamPayload);
        }
        private static void TestTitleBlockTextAnalyzer_LabelValueSpatialProximity()
        {
            var analyzer = new TitleBlockTextAnalyzer();
            var frame = new FrameCandidate
            {
                Corners = new Point2D[]
                {
                    new Point2D(0, 0),
                    new Point2D(1000, 0),
                    new Point2D(1000, 700),
                    new Point2D(0, 700)
                },
                WcsBoundingBox = new Extents2D(0, 0, 1000, 700),
                WidthDrawingUnits = 1000.0,
                HeightDrawingUnits = 700.0
            };

            // Horizontal proximity: Label at left, value at right
            var textsHorizontal = new List<TitleTextItem>
            {
                new TitleTextItem { Position = new Point2D(800, 50), Text = "图名", Height = 10 },
                new TitleTextItem { Position = new Point2D(860, 50), Text = "首层及二层结构平面布置图", Height = 12 },
                new TitleTextItem { Position = new Point2D(800, 30), Text = "图号", Height = 10 },
                new TitleTextItem { Position = new Point2D(860, 30), Text = "结-01", Height = 10 },
                new TitleTextItem { Position = new Point2D(800, 10), Text = "比例", Height = 10 },
                new TitleTextItem { Position = new Point2D(860, 10), Text = "1:100", Height = 10 }
            };

            analyzer.EnrichFrame(frame, textsHorizontal, null);
            AssertTrue(frame.ExtractedDrawingName == "首层及二层结构平面布置图", "Drawing name should be inferred from right neighbor: " + frame.ExtractedDrawingName);
            AssertTrue(frame.ExtractedDrawingNumber == "结-01", "Drawing number should be inferred from right neighbor: " + frame.ExtractedDrawingNumber);
            AssertTrue(frame.ExtractedScaleValue == 100.0, "Scale should be inferred from right neighbor: " + frame.ExtractedScaleValue);

            // Vertical proximity: Label on top, value below
            var frame2 = new FrameCandidate
            {
                Corners = new Point2D[]
                {
                    new Point2D(0, 0),
                    new Point2D(1000, 0),
                    new Point2D(1000, 700),
                    new Point2D(0, 700)
                },
                WcsBoundingBox = new Extents2D(0, 0, 1000, 700),
                WidthDrawingUnits = 1000.0,
                HeightDrawingUnits = 700.0
            };

            var textsVertical = new List<TitleTextItem>
            {
                new TitleTextItem { Position = new Point2D(800, 70), Text = "图  名", Height = 10 },
                new TitleTextItem { Position = new Point2D(800, 52), Text = "基础桩位平面布置图", Height = 14 },
                new TitleTextItem { Position = new Point2D(800, 35), Text = "图  号", Height = 10 },
                new TitleTextItem { Position = new Point2D(800, 20), Text = "2023-ZS-05", Height = 10 },
                new TitleTextItem { Position = new Point2D(800, 12), Text = "比  例", Height = 10 },
                new TitleTextItem { Position = new Point2D(800, 2), Text = "1:50", Height = 10 }
            };

            analyzer.EnrichFrame(frame2, textsVertical, null);
            AssertTrue(frame2.ExtractedDrawingName == "基础桩位平面布置图", "Drawing name should be inferred from bottom neighbor: " + frame2.ExtractedDrawingName);
            AssertTrue(frame2.ExtractedDrawingNumber == "2023-ZS-05", "Drawing number should be inferred from bottom neighbor: " + frame2.ExtractedDrawingNumber);
            AssertTrue(frame2.ExtractedScaleValue == 50.0, "Scale should be inferred from bottom neighbor: " + frame2.ExtractedScaleValue);
        }

        private static void TestTitleBlockTextAnalyzer_DirectInTextColonLabelValue()
        {
            var analyzer = new TitleBlockTextAnalyzer();
            var frame = new FrameCandidate
            {
                Corners = new Point2D[]
                {
                    new Point2D(0, 0),
                    new Point2D(84100, 0),
                    new Point2D(84100, 59400),
                    new Point2D(0, 59400)
                },
                WcsBoundingBox = new Extents2D(0, 0, 84100, 59400),
                WidthDrawingUnits = 84100.0,
                HeightDrawingUnits = 59400.0
            };

            var texts = new List<TitleTextItem>
            {
                new TitleTextItem { Position = new Point2D(70000, 5000), Text = "图名: 屋面梁板配筋图", Height = 350 },
                new TitleTextItem { Position = new Point2D(70000, 3000), Text = "图纸编号：J-02", Height = 300 },
                new TitleTextItem { Position = new Point2D(70000, 1500), Text = "出图比例: 1:100", Height = 250 }
            };

            analyzer.EnrichFrame(frame, texts, null);
            AssertTrue(frame.ExtractedDrawingName == "屋面梁板配筋图", "Should extract in-text colon drawing name");
            AssertTrue(frame.ExtractedDrawingNumber == "J-02", "Should extract in-text colon drawing number");
            AssertTrue(frame.ExtractedScaleValue == 100.0, "Should extract in-text colon scale");
        }

        private static void TestTitleBlockTextAnalyzer_BottomRightLargestTextHeightHeuristic()
        {
            var analyzer = new TitleBlockTextAnalyzer();
            // A1 frame at 1:100 scale: 84100 x 59400
            var frame = new FrameCandidate
            {
                Corners = new Point2D[]
                {
                    new Point2D(0, 0),
                    new Point2D(84100, 0),
                    new Point2D(84100, 59400),
                    new Point2D(0, 59400)
                },
                WcsBoundingBox = new Extents2D(0, 0, 84100, 59400),
                WidthDrawingUnits = 84100.0,
                HeightDrawingUnits = 59400.0
            };

            // Scattered exploded text entities in the bottom-right quadrant:
            // Notice no "图名" label exists here!
            var texts = new List<TitleTextItem>
            {
                new TitleTextItem { Position = new Point2D(72000, 12000), Text = "中国华西建筑设计研究院有限公司", Height = 300 },
                new TitleTextItem { Position = new Point2D(75000, 10000), Text = "出图专用章", Height = 350 },
                new TitleTextItem { Position = new Point2D(71000, 8000), Text = "审定: 李四", Height = 250 },
                new TitleTextItem { Position = new Point2D(71000, 6000), Text = "校对: 王五", Height = 250 },
                new TitleTextItem { Position = new Point2D(65000, 4500), Text = "车库基础顶板配筋及详图", Height = 700 }, // Largest font in bottom-right!
                new TitleTextItem { Position = new Point2D(78000, 3000), Text = "A1-01", Height = 350 },
                new TitleTextItem { Position = new Point2D(78000, 1500), Text = "1:100", Height = 250 }
            };

            analyzer.EnrichFrame(frame, texts, null);
            AssertTrue(frame.ExtractedDrawingName == "车库基础顶板配筋及详图", "Should pick text with largest font height in bottom-right: " + frame.ExtractedDrawingName);
            AssertTrue(frame.ExtractedDrawingNumber == "A1-01", "Should pick drawing number regex A1-01: " + frame.ExtractedDrawingNumber);
            AssertTrue(frame.ExtractedScaleValue == 100.0, "Should pick scale 1:100: " + frame.ExtractedScaleValue);
        }

        private static void TestTitleBlockTextAnalyzer_EnhancedDrawingNumberRegexPatterns()
        {
            // Valid design institute drawing number patterns
            string[] validNumbers = new string[]
            {
                "结-01", "J-02", "A1-01", "2023-ZS-05", "结施-01",
                "2022-J-01", "结-01-1", "S-01.1", "结施—02", "J_03",
                "建-01-A", "A1-01#1", "初设-01", "电-03", "水施-12", "暖-05"
            };

            foreach (var num in validNumbers)
            {
                AssertTrue(TitleBlockTextAnalyzer.IsDrawingNumberText(num),
                    string.Format("'{0}' should be recognized as a valid drawing number", num));
            }

            // Non-drawing numbers
            string[] invalidNumbers = new string[]
            {
                "2023-10-25", // date
                "2022.05",    // date
                "图纸目录",    // title
                "1:100",      // scale
                "说明"        // notes
            };

            foreach (var invalid in invalidNumbers)
            {
                AssertTrue(!TitleBlockTextAnalyzer.IsDrawingNumberText(invalid),
                    string.Format("'{0}' should NOT be recognized as a drawing number", invalid));
            }
        }

        private static void TestTitleBlockTextAnalyzer_RejectsRebarAndDetailAnnotationsInFavorOfRealTitle()
        {
            // 1. Check unit filter
            AssertTrue(TitleBlockTextAnalyzer.IsEngineeringDetailOrRebarAnnotation("8%%13222(实心)+6%%13220(空心)"), "Rebar code with font codes and count must be recognized as rebar annotation");
            AssertTrue(TitleBlockTextAnalyzer.IsEngineeringDetailOrRebarAnnotation("4Φ20"), "Φ symbol must be recognized as rebar annotation");
            AssertTrue(TitleBlockTextAnalyzer.IsEngineeringDetailOrRebarAnnotation("%%1328@100/200"), "Spacing @ pattern must be recognized as rebar annotation");
            AssertTrue(TitleBlockTextAnalyzer.IsEngineeringDetailOrRebarAnnotation("KZ1 600x600"), "Column tag KZ1 must be recognized as structural detail");
            AssertTrue(TitleBlockTextAnalyzer.IsEngineeringDetailOrRebarAnnotation("KL2(3) 300x700"), "Beam tag KL2 must be recognized as structural detail");
            AssertTrue(TitleBlockTextAnalyzer.IsEngineeringDetailOrRebarAnnotation("▽+3.600"), "Elevation mark must be recognized as detail annotation");

            AssertTrue(!TitleBlockTextAnalyzer.IsEngineeringDetailOrRebarAnnotation("基顶~一层板面墙柱大样图(一)"), "Real drawing title with (一) should not be rejected");
            AssertTrue(!TitleBlockTextAnalyzer.IsEngineeringDetailOrRebarAnnotation("基顶~一层板面墙柱定位图"), "Real drawing title should not be rejected");
            AssertTrue(!TitleBlockTextAnalyzer.IsEngineeringDetailOrRebarAnnotation("剪力墙身表"), "Real drawing title table should not be rejected");
            AssertTrue(!TitleBlockTextAnalyzer.IsEngineeringDetailOrRebarAnnotation("屋面梁板配筋图"), "Real drawing title should not be rejected");

            // 2. Test candidate ranking: Ensure rebar annotation with huge font height is REJECTED
            // and the real drawing title with parentheses like (一) is selected.
            var analyzer = new TitleBlockTextAnalyzer();
            var frame = new FrameCandidate
            {
                Corners = new Point2D[]
                {
                    new Point2D(0, 0),
                    new Point2D(84100, 0),
                    new Point2D(84100, 59400),
                    new Point2D(0, 59400)
                },
                WcsBoundingBox = new Extents2D(0, 0, 84100, 59400),
                WidthDrawingUnits = 84100.0,
                HeightDrawingUnits = 59400.0
            };

            var texts = new List<TitleTextItem>
            {
                // Rebar annotation text with font height 800 (much larger than drawing title!)
                new TitleTextItem { Position = new Point2D(70000, 8000), Text = "8%%13222(实心)+6%%13220(空心)", Height = 800, Layer = "0-STRUCT-REBAR" },
                // Section label with height 650
                new TitleTextItem { Position = new Point2D(72000, 6000), Text = "KZ1 600x600", Height = 650, Layer = "0-STRUCT-REBAR" },
                // Real drawing title with font height 450 in titleblock corner
                new TitleTextItem { Position = new Point2D(76000, 3000), Text = "基顶~一层板面墙柱大样图(一)", Height = 450, Layer = "0-图框-文字" },
                // Drawing number
                new TitleTextItem { Position = new Point2D(78000, 1500), Text = "结-02", Height = 300, Layer = "0-图框-文字" },
                // Scale
                new TitleTextItem { Position = new Point2D(78000, 800), Text = "1:100", Height = 250, Layer = "0-图框-文字" }
            };

            analyzer.EnrichFrame(frame, texts, null);
            AssertTrue(frame.ExtractedDrawingName == "基顶~一层板面墙柱大样图(一)",
                "ExtractedDrawingName should be '基顶~一层板面墙柱大样图(一)' instead of rebar text, got: " + frame.ExtractedDrawingName);
            AssertTrue(frame.ExtractedDrawingNumber == "结-02", "Drawing number should be 结-02");
            AssertTrue(frame.ExtractedScaleValue == 100.0, "Scale should be 1:100");

            // 3. Test drawing title ending in '表' (e.g. 剪力墙身表)
            var frame2 = new FrameCandidate
            {
                Corners = new Point2D[]
                {
                    new Point2D(0, 0),
                    new Point2D(84100, 0),
                    new Point2D(84100, 59400),
                    new Point2D(0, 59400)
                },
                WcsBoundingBox = new Extents2D(0, 0, 84100, 59400),
                WidthDrawingUnits = 84100.0,
                HeightDrawingUnits = 59400.0
            };

            var texts2 = new List<TitleTextItem>
            {
                new TitleTextItem { Position = new Point2D(68000, 10000), Text = "4Φ25+2Φ22", Height = 750, Layer = "REBAR" },
                new TitleTextItem { Position = new Point2D(75000, 4000), Text = "剪力墙身表", Height = 450, Layer = "TITLE" },
                new TitleTextItem { Position = new Point2D(78000, 1500), Text = "结施-03", Height = 300, Layer = "TITLE" }
            };

            analyzer.EnrichFrame(frame2, texts2, null);
            AssertTrue(frame2.ExtractedDrawingName == "剪力墙身表",
                "ExtractedDrawingName should be '剪力墙身表', got: " + frame2.ExtractedDrawingName);
        }

        private static void TestTitleBlockTextAnalyzer_BilingualLabelsAndCentering()
        {
            var analyzer = new TitleBlockTextAnalyzer();
            var frame = new FrameCandidate
            {
                Corners = new Point2D[]
                {
                    new Point2D(0, 0),
                    new Point2D(84100, 0),
                    new Point2D(84100, 59400),
                    new Point2D(0, 59400)
                },
                WcsBoundingBox = new Extents2D(0, 0, 84100, 59400),
                WidthDrawingUnits = 84100.0,
                HeightDrawingUnits = 59400.0
            };

            // Test scenario matching real user title block:
            // Cell 1: Label "图名：（DRAWING TITLE）" at top-left of cell.
            //         Below it, centered in cell, title "基顶~一层板面墙柱详图".
            // Cell 2: Label "图号：（DRAWING NO.）". Below it: "结-05".
            // Cell 3: Label "比例：（SCALE）". Right of it: "1:100".
            var texts = new List<TitleTextItem>
            {
                // Title cell: label at X=70000, Y=3500; title below at X=73500 (centered, dx=3500), Y=2200
                new TitleTextItem { Position = new Point2D(70000, 3500), Text = "图名：（DRAWING TITLE）", Height = 250, Layer = "0-TITLE" },
                new TitleTextItem { Position = new Point2D(73500, 2200), Text = "基顶~一层板面墙柱详图", Height = 400, Layer = "0-TITLE" },

                // Drawing number cell: label at X=70000, Y=1600; number below at X=72000, Y=800
                new TitleTextItem { Position = new Point2D(70000, 1600), Text = "图号：（DRAWING NO.）", Height = 250, Layer = "0-TITLE" },
                new TitleTextItem { Position = new Point2D(72000, 800), Text = "结-05", Height = 300, Layer = "0-TITLE" },

                // Scale cell: label at X=78000, Y=800; value right of it at X=81000, Y=800
                new TitleTextItem { Position = new Point2D(78000, 800), Text = "比例：（SCALE）", Height = 250, Layer = "0-TITLE" },
                new TitleTextItem { Position = new Point2D(81000, 800), Text = "1:100", Height = 250, Layer = "0-TITLE" }
            };

            analyzer.EnrichFrame(frame, texts, null);

            AssertTrue(frame.ExtractedDrawingName == "基顶~一层板面墙柱详图",
                "Drawing title should be extracted from cell below bilingual label, got: " + frame.ExtractedDrawingName);
            AssertTrue(frame.ExtractedDrawingNumber == "结-05",
                "Drawing number should be extracted from cell below bilingual label, got: " + frame.ExtractedDrawingNumber);
            AssertTrue(frame.ExtractedScaleValue == 100.0,
                "Scale should be extracted from cell next to bilingual label, got: " + frame.ExtractedScaleValue);
        }

        private static void TestSheetPreparationService_PaperSpaceLayoutSupport()
        {
            var prep = new SheetPreparationService();
            var settings = BatchPdfSettings.CreateDefault();
            settings.IncludePaperSpaceLayouts = true;

            var candidates = new List<FrameCandidate>
            {
                new FrameCandidate
                {
                    FrameHandle = "1A2B",
                    FrameType = FrameType.Polyline,
                    Space = "布局: 平面布置图",
                    LayoutName = "平面布置图",
                    WidthDrawingUnits = 841.0,
                    HeightDrawingUnits = 594.0,
                    ExtractedDrawingNumber = "结-01",
                    ExtractedDrawingName = "二层结构平面布置图"
                }
            };

            var mediaList = new List<PaperMedia>
            {
                new PaperMedia
                {
                    CanonicalName = "ISO_A1_(841.00_x_594.00_MM)",
                    LocaleName = "ISO A1 (841 x 594 mm)",
                    WidthMm = 841.0,
                    HeightMm = 594.0
                }
            };

            var sheets = prep.CreatePrintSheets(candidates, "test.dwg", DrawingUnits.Millimeters, settings, mediaList);
            AssertTrue(sheets.Count == 1, "Should create 1 sheet");
            var sheet = sheets[0];
            AssertTrue(sheet.Space == "布局: 平面布置图", "Should preserve Space: " + sheet.Space);
            AssertTrue(sheet.SpaceName == "平面布置图", "Should preserve SpaceName: " + sheet.SpaceName);
            AssertTrue(sheet.ScaleValue == 1.0, "Paper space should default scale to 1:1");
            AssertTrue(sheet.ScaleSource == ScaleSource.UserSpecified, "ScaleSource should be UserSpecified");
            AssertTrue(sheet.TargetPaperCanonicalName == "ISO_A1_(841.00_x_594.00_MM)", "Should match ISO A1");
            AssertTrue(sheet.DrawingNumber == "结-01", "Drawing number should match");
            AssertTrue(sheet.DrawingName == "二层结构平面布置图", "Drawing name should match");
        }

        private static void TestTitleBlockTextAnalyzer_EnglishCellCenteredAndDrawingContentExclusion()
        {
            var analyzer = new TitleBlockTextAnalyzer();
            // A1 frame at 1:100 scale: 84100 x 59400
            var frame = new FrameCandidate
            {
                Corners = new Point2D[]
                {
                    new Point2D(0, 0),
                    new Point2D(84100, 0),
                    new Point2D(84100, 59400),
                    new Point2D(0, 59400)
                },
                WcsBoundingBox = new Extents2D(0, 0, 84100, 59400),
                WidthDrawingUnits = 84100.0,
                HeightDrawingUnits = 59400.0
            };

            // Matches user's screenshot:
            // Top-left of titleblock cell has "Drawing" (Height=180) and "Title :" (Height=180)
            // Center of the cell (dx ≈ 20.6 * refH, dy ≈ 21.5 * refH) has "GROUND FLOOR LAYOUT PLAN" (Height=460)
            // Meanwhile, the interior drawing content canvas has various Chinese & English notes/captions that must NOT be picked!
            var texts = new List<TitleTextItem>
            {
                // Drawing content area distractions (middle of sheet):
                new TitleTextItem { Position = new Point2D(25000, 35000), Text = "图10.4.3-1 现浇板留洞构造一", Height = 500, Layer = "S-TEXT" },
                new TitleTextItem { Position = new Point2D(30000, 30000), Text = "配筋详平面", Height = 500, Layer = "S-TEXT" },
                new TitleTextItem { Position = new Point2D(35000, 28000), Text = "除二a类环境类别外的所有构件", Height = 450, Layer = "S-TEXT" },
                new TitleTextItem { Position = new Point2D(40000, 40000), Text = "二层结构平面图", Height = 600, Layer = "S-TEXT" },

                // Titleblock cell at bottom-right:
                new TitleTextItem { Position = new Point2D(68000, 8200), Text = "Drawing", Height = 180, Layer = "S-ANNO-TTLB" },
                new TitleTextItem { Position = new Point2D(68000, 7900), Text = "Title :", Height = 180, Layer = "S-ANNO-TTLB" },
                new TitleTextItem { Position = new Point2D(71700, 4030), Text = "GROUND FLOOR LAYOUT PLAN", Height = 460, Layer = "S-ANNO-TTLB" },

                // Drawing number cell with "设计阶段" nearby (must not pick "设计阶段" as drawing number):
                new TitleTextItem { Position = new Point2D(76000, 3500), Text = "Drawing No.", Height = 180, Layer = "S-ANNO-TTLB" },
                new TitleTextItem { Position = new Point2D(76200, 3000), Text = "设计阶段", Height = 200, Layer = "S-ANNO-TTLB" },
                new TitleTextItem { Position = new Point2D(78500, 2500), Text = "S-101", Height = 300, Layer = "S-ANNO-TTLB" }
            };

            analyzer.EnrichFrame(frame, texts, null);
            AssertTrue(frame.ExtractedDrawingName == "GROUND FLOOR LAYOUT PLAN",
                "Expected 'GROUND FLOOR LAYOUT PLAN', got: " + frame.ExtractedDrawingName);
            AssertTrue(frame.ExtractedDrawingNumber == "S-101",
                "Expected 'S-101' (and never '设计阶段'), got: " + frame.ExtractedDrawingNumber);
        }

        private static void TestTitleBlockTextAnalyzer_CustomFrameRuleTags()
        {
            var analyzer = new TitleBlockTextAnalyzer();
            var frame = new FrameCandidate
            {
                Corners = new Point2D[]
                {
                    new Point2D(0, 0),
                    new Point2D(84100, 0),
                    new Point2D(84100, 59400),
                    new Point2D(0, 59400)
                },
                WcsBoundingBox = new Extents2D(0, 0, 84100, 59400),
                WidthDrawingUnits = 84100.0,
                HeightDrawingUnits = 59400.0
            };

            var rules = new List<FrameRule>
            {
                new FrameRule
                {
                    SheetNameTags = new List<string> { "CUSTOM_T_LABEL" },
                    SheetNumberTags = new List<string> { "CUSTOM_NO_LABEL" },
                    ScaleTags = new List<string> { "CUSTOM_SC_LABEL" }
                }
            };

            var texts = new List<TitleTextItem>
            {
                new TitleTextItem { Position = new Point2D(70000, 5000), Text = "CUSTOM_T_LABEL", Height = 200, Layer = "TK" },
                new TitleTextItem { Position = new Point2D(73000, 5000), Text = "ROOF FRAMING PLAN", Height = 350, Layer = "TK" },
                new TitleTextItem { Position = new Point2D(70000, 3000), Text = "CUSTOM_NO_LABEL: ST-205", Height = 200, Layer = "TK" },
                new TitleTextItem { Position = new Point2D(70000, 1500), Text = "CUSTOM_SC_LABEL: 1:150", Height = 200, Layer = "TK" }
            };

            analyzer.EnrichFrame(frame, texts, rules);
            AssertTrue(frame.ExtractedDrawingName == "ROOF FRAMING PLAN",
                "Custom SheetNameTags should match 'ROOF FRAMING PLAN', got: " + frame.ExtractedDrawingName);
            AssertTrue(frame.ExtractedDrawingNumber == "ST-205",
                "Custom SheetNumberTags should match 'ST-205', got: " + frame.ExtractedDrawingNumber);
            AssertClose(frame.ExtractedScaleValue ?? 0, 150.0, 0.01, "Custom ScaleTags should match 1:150");
        }

        private static void TestTitleBlockTextAnalyzer_RealWorldKeyPlanAndStackedTitleLabel()
        {
            var analyzer = new TitleBlockTextAnalyzer();
            var frame = new FrameCandidate
            {
                Corners = new Point2D[]
                {
                    new Point2D(-32994.6, 747713.1),
                    new Point2D(323805.4, 747713.1),
                    new Point2D(323805.4, 915913.1),
                    new Point2D(-32994.6, 915913.1)
                },
                WcsBoundingBox = new Extents2D(-32994.6, 747713.1, 323805.4, 915913.1),
                WidthDrawingUnits = 356800.0,
                HeightDrawingUnits = 168200.0
            };

            var rules = BatchPdfSettings.CreateDefault().FrameRules;

            // Exact reproduction of real DWG FRAME0 where "Key plan" on layer "图框" previously beat
            // "UPPER ROOF FLOOR LAYOUT PLAN" on layer "S-ANNO-TEXT" because "Title :" captured ":" in Step 1 regex.
            var texts = new List<TitleTextItem>
            {
                new TitleTextItem { Position = new Point2D(293974.5, 911285.2), Text = "Legend", Height = 400.0, Layer = "图框" },
                new TitleTextItem { Position = new Point2D(295197.5, 884519.2), Text = "Key plan", Height = 400.0, Layer = "图框" },
                new TitleTextItem { Position = new Point2D(294423.5, 782023.7), Text = "Drawing", Height = 400.0, Layer = "图框" },
                new TitleTextItem { Position = new Point2D(293926.4, 781375.5), Text = "Title :", Height = 400.0, Layer = "图框" },
                new TitleTextItem { Position = new Point2D(294062.7, 792023.7), Text = "Project:", Height = 400.0, Layer = "图框" },
                new TitleTextItem { Position = new Point2D(306847.7, 787881.3), Text = "XXXXX", Height = 1500.0, Layer = "图框" },
                new TitleTextItem { Position = new Point2D(300922.7, 798801.3), Text = "10 Anson Road, #05-01, International Plaza, Singapore 079903", Height = 400.0, Layer = "图框" },
                new TitleTextItem { Position = new Point2D(305530.2, 827225.1), Text = "60% TENDER DRAWING FOR CLIENT REVIEW", Height = 372.8, Layer = "明细表图形" },
                new TitleTextItem { Position = new Point2D(293806.2, 753597.0), Text = "No. :", Height = 400.0, Layer = "图框" },
                new TitleTextItem { Position = new Point2D(301055.0, 756282.3), Text = "IRIS3-DD-STR-DRG-250", Height = 800.0, Layer = "S-ANNO-TEXT" },
                new TitleTextItem { Position = new Point2D(306876.9, 775437.4), Text = "UPPER ROOF FLOOR LAYOUT PLAN", Height = 1000.0, Layer = "S-ANNO-TEXT" }
            };

            analyzer.EnrichFrame(frame, texts, rules);
            AssertTrue(frame.ExtractedDrawingName == "UPPER ROOF FLOOR LAYOUT PLAN",
                "Should extract 'UPPER ROOF FLOOR LAYOUT PLAN' and never 'Key plan' or 'XXXXX', got: " + frame.ExtractedDrawingName);
            AssertTrue(frame.ExtractedDrawingNumber == "IRIS3-DD-STR-DRG-250",
                "Should extract 'IRIS3-DD-STR-DRG-250', got: " + frame.ExtractedDrawingNumber);

            // Also verify Step 3 fallback (without any "Title :" label) does not pick "Key plan"
            var frameNoLabel = new FrameCandidate
            {
                Corners = frame.Corners,
                WcsBoundingBox = frame.WcsBoundingBox,
                WidthDrawingUnits = frame.WidthDrawingUnits,
                HeightDrawingUnits = frame.HeightDrawingUnits
            };
            var textsNoLabel = texts.FindAll(t => t.Text != "Title :" && t.Text != "Drawing");
            analyzer.EnrichFrame(frameNoLabel, textsNoLabel, rules);
            AssertTrue(frameNoLabel.ExtractedDrawingName == "UPPER ROOF FLOOR LAYOUT PLAN",
                "Fallback without 'Title :' label should still extract 'UPPER ROOF FLOOR LAYOUT PLAN', got: " + frameNoLabel.ExtractedDrawingName);
        }

        private static void TestDrawingIndexTableLayoutService_AdaptiveColumnWidthAndRowHeight()
        {
            string[] headers = { "序号", "图号", "图纸名称", "图幅", "比例", "出图日期", "备注" };

            // Case 1: Real English + Chinese drawing names at 1:200
            var rows = new List<string[]>
            {
                new[] { "1", "IRIS3-DD-STR-DRG-250", "UPPER ROOF FLOOR LAYOUT PLAN", "A1", "1:200", "2026.11", "" },
                new[] { "2", "IRIS3-DD-STR-DRG-200", "PILE CAP & FOOTING PLAN", "A1", "1:200", "2026.11", "" },
                new[] { "3", "结-03", "地下室顶板结构平面布置图", "A1加长", "1:200", "2026.11", "" }
            };

            var layout = DrawingIndexTableLayoutService.ComputeLayout(headers, rows, 200.0);
            AssertTrue(layout.ColumnWidths.Length == 7, "Should compute 7 column widths");
            AssertTrue(layout.ColumnWidths[1] > 35.0 * 200.0,
                "Drawing number column should widen past legacy 7000, got: " + layout.ColumnWidths[1]);
            AssertTrue(layout.ColumnWidths[2] > 65.0 * 200.0,
                "Drawing name column should widen past legacy 13000, got: " + layout.ColumnWidths[2]);
            AssertTrue(layout.RowMaxLineCounts[1] == 1,
                "UPPER ROOF FLOOR LAYOUT PLAN should fit on 1 line, got lines=" + layout.RowMaxLineCounts[1]);

            // Case 2: Ultra-long title exceeds column cap and multi-line explicit break must expand row height cleanly
            var longRows = new List<string[]>
            {
                new[] { "1", "S-101", "GROUND FLOOR PLAN", "A1", "1:100", "2026.11", "" },
                new[] { "2", "S-102", "地下室二层人防核心筒及主楼转换层大跨度预应力梁板结构平面布置图（含节点详图与后浇带构造大样及配筋说明）", "A1", "1:100", "2026.11", "" },
                new[] { "3", "S-103", "ROOF FLOOR PLAN\n(PART A & PART B)", "A1", "1:100", "2026.11", "" }
            };

            var longLayout = DrawingIndexTableLayoutService.ComputeLayout(headers, longRows, 100.0);
            AssertTrue(longLayout.ColumnWidths[2] <= 185.0 * 100.0 + 0.01,
                "Title column width should respect max cap 18500, got: " + longLayout.ColumnWidths[2]);
            AssertTrue(longLayout.RowMaxLineCounts[2] >= 2,
                "Ultra-long title should report >=2 wrapped lines, got: " + longLayout.RowMaxLineCounts[2]);
            AssertTrue(longLayout.RowHeights[2] > longLayout.RowHeights[1] * 1.4,
                string.Format("Wrapped row height ({0}) must expand significantly over single-line row ({1})", longLayout.RowHeights[2], longLayout.RowHeights[1]));
            AssertTrue(longLayout.RowMaxLineCounts[3] >= 2 && longLayout.RowHeights[3] > longLayout.RowHeights[1] * 1.4,
                "Explicit multi-line title must expand row height");
        }

        private static void TestTitleBlockTextAnalyzer_RejectsStructuralMemberCodesAsDrawingNumber()
        {
            AssertTrue(!TitleBlockTextAnalyzer.IsDrawingNumberText("GKZ40-1"), "GKZ40-1 must not be treated as drawing number");
            AssertTrue(!TitleBlockTextAnalyzer.IsDrawingNumberText("GKL60-1"), "GKL60-1 must not be treated as drawing number");
            AssertTrue(!TitleBlockTextAnalyzer.IsDrawingNumberText("GL40-1"), "GL40-1 must not be treated as drawing number");
            AssertTrue(TitleBlockTextAnalyzer.IsDrawingNumberText("xxx-CD-STR-DRG-200"), "xxx-CD-STR-DRG-200 should be valid drawing number");

            var analyzer = new TitleBlockTextAnalyzer();
            var frame = new FrameCandidate
            {
                Corners = new Point2D[]
                {
                    new Point2D(-255208.6, 1982.4),
                    new Point2D(-34408.6, 1982.4),
                    new Point2D(-34408.6, 91082.4),
                    new Point2D(-255208.6, 91082.4)
                },
                WcsBoundingBox = new Extents2D(-255208.6, 1982.4, -34408.6, 91082.4),
                WidthDrawingUnits = 220800.0,
                HeightDrawingUnits = 89100.0
            };

            var texts = new List<TitleTextItem>
            {
                // Structural tags in bottom/right of plan canvas (first in list, must be skipped!)
                new TitleTextItem { Position = new Point2D(-160000.0, 10000.0), Text = "GKZ40-1", Height = 250.0, Layer = "平立面图梁柱撑编号" },
                new TitleTextItem { Position = new Point2D(-65000.0, 12000.0), Text = "GKL60-1", Height = 250.0, Layer = "平立面图梁柱撑编号" },
                // Job No. cell at bottom
                new TitleTextItem { Position = new Point2D(-56962.2, 6881.5), Text = "Job", Height = 300.0, Layer = "图框" },
                new TitleTextItem { Position = new Point2D(-56908.1, 6395.3), Text = "No. :", Height = 300.0, Layer = "图框" },
                new TitleTextItem { Position = new Point2D(-52127.2, 5728.8), Text = "2026-JOB-99", Height = 600.0, Layer = "图框" },
                // Drawing Number cell above Job No.
                new TitleTextItem { Position = new Point2D(-56445.1, 9011.5), Text = "Drawing", Height = 300.0, Layer = "图框" },
                new TitleTextItem { Position = new Point2D(-56391.0, 8525.3), Text = "Number :", Height = 300.0, Layer = "图框" },
                new TitleTextItem { Position = new Point2D(-51033.8, 8388.5), Text = "xxx-CD-STR-DRG-200", Height = 600.0, Layer = "S-ANNO-TEXT" },
                // Title cell
                new TitleTextItem { Position = new Point2D(-56445.1, 27715.4), Text = "Drawing", Height = 300.0, Layer = "图框" },
                new TitleTextItem { Position = new Point2D(-56817.9, 27229.2), Text = "Title :", Height = 300.0, Layer = "图框" },
                new TitleTextItem { Position = new Point2D(-47832.1, 22596.8), Text = "1-ROOF FLOOR LAYOUT PLAN", Height = 750.0, Layer = "S-ANNO-TEXT" }
            };

            analyzer.EnrichFrame(frame, texts, BatchPdfSettings.CreateDefault().FrameRules);
            AssertTrue(frame.ExtractedDrawingNumber == "xxx-CD-STR-DRG-200",
                "Expected drawing number 'xxx-CD-STR-DRG-200' (not GKZ40-1 or Job No.), got: " + frame.ExtractedDrawingNumber);
            AssertTrue(frame.ExtractedDrawingName == "1-ROOF FLOOR LAYOUT PLAN",
                "Expected drawing name '1-ROOF FLOOR LAYOUT PLAN', got: " + frame.ExtractedDrawingName);
        }
    }
}
