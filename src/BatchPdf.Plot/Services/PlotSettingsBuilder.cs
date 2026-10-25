using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using BatchPdf.Core.Configuration;
using BatchPdf.Core.Models;

namespace BatchPdf.Plot.Services
{
    public class PlotSettingsBuilder
    {
        private readonly PaperMediaCatalog _catalog;

        public PlotSettingsBuilder(PaperMediaCatalog catalog)
        {
            _catalog = catalog ?? new PaperMediaCatalog();
        }

        /// <summary>
        /// Builds and validates a PlotSettings and PlotInfo for the given PrintSheet and DCS window.
        /// Caller is responsible for disposing the returned PlotSettings and PlotInfo.
        /// </summary>
        public PlotSettings BuildPlotSettings(
            Database db,
            Layout layout,
            PrintSheet sheet,
            string exactDeviceName,
            Extents2d dcsWindow,
            IList<string> availablePlotStyles,
            BatchPdfSettings settings = null)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            if (sheet == null) throw new ArgumentNullException(nameof(sheet));
            if (string.IsNullOrWhiteSpace(exactDeviceName))
                throw new InvalidOperationException("未指定有效的 DWG To PDF.pc3 设备名称。");
            if (string.IsNullOrWhiteSpace(sheet.TargetPaperCanonicalName))
                throw new InvalidOperationException("图纸未指定有效的 PC3 纸张媒体名称。");

            PlotSettings ps = new PlotSettings(layout.ModelType);
            try
            {
                ps.CopyFrom(layout);

                PlotSettingsValidator psv = PlotSettingsValidator.Current;

                // 1. Set device and canonical media name
                psv.SetPlotConfigurationName(ps, exactDeviceName, sheet.TargetPaperCanonicalName);
                psv.RefreshLists(ps);
                psv.SetCanonicalMediaName(ps, sheet.TargetPaperCanonicalName);

                // 2. Set paper units to Millimeters
                psv.SetPlotPaperUnits(ps, PlotPaperUnit.Millimeters);

                // 3. Set window area in DCS before setting PlotType.Window
                psv.SetPlotWindowArea(ps, dcsWindow);
                psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);

                // 4. Set scale (explicit fixed scale vs explicit FitToPaper)
                if (sheet.FitToPaper)
                {
                    psv.SetUseStandardScale(ps, true);
                    psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                }
                else
                {
                    if (!sheet.ScaleValue.HasValue || sheet.ScaleValue.Value <= 0)
                    {
                        throw new InvalidOperationException("打印比例未确认，禁止静默使用适合纸张打印。");
                    }

                    double unitFactorMm = sheet.DrawingUnits.ToMillimetersFactor();
                    double drawingUnitsPerPaperMm = sheet.ScaleValue.Value / unitFactorMm;

                    psv.SetUseStandardScale(ps, false);
                    psv.SetCustomPrintScale(ps, new CustomScale(1.0, drawingUnitsPerPaperMm));
                }

                // 5. Determine paper rotation (Auto / Portrait / Landscape + UpsideDown)
                Point2d mediaSize = ps.PlotPaperSize;
                double dcsWidth = Math.Abs(dcsWindow.MaxPoint.X - dcsWindow.MinPoint.X);
                double dcsHeight = Math.Abs(dcsWindow.MaxPoint.Y - dcsWindow.MinPoint.Y);

                bool windowIsLandscape = dcsWidth >= dcsHeight;
                bool mediaIsLandscape = mediaSize.X >= mediaSize.Y;

                PlotOrientationPolicy policy = settings != null ? settings.OrientationPolicy : PlotOrientationPolicy.Auto;
                bool upsideDown = settings != null && settings.UpsideDown;

                PlotRotation baseRot;
                if (policy == PlotOrientationPolicy.Landscape)
                {
                    baseRot = mediaIsLandscape ? PlotRotation.Degrees000 : PlotRotation.Degrees090;
                }
                else if (policy == PlotOrientationPolicy.Portrait)
                {
                    baseRot = mediaIsLandscape ? PlotRotation.Degrees090 : PlotRotation.Degrees000;
                }
                else
                {
                    // Auto-align 0 or 90 so landscape window maps to landscape page and portrait maps to portrait page
                    baseRot = (windowIsLandscape == mediaIsLandscape)
                        ? PlotRotation.Degrees000
                        : PlotRotation.Degrees090;
                }

                if (upsideDown)
                {
                    baseRot = (baseRot == PlotRotation.Degrees000)
                        ? PlotRotation.Degrees180
                        : PlotRotation.Degrees270;
                }

                psv.SetPlotRotation(ps, baseRot);

                // 6. Center plot on paper vs custom X/Y offset
                bool centerPlot = settings == null || settings.CenterPlot;
                if (centerPlot)
                {
                    psv.SetPlotCentered(ps, true);
                }
                else
                {
                    psv.SetPlotCentered(ps, false);
                    psv.SetPlotOrigin(ps, new Point2d(settings.PlotOffsetX, settings.PlotOffsetY));
                }

                // 7. Lineweights & Plot style sheet (CTB/STB)
                ps.PrintLineweights = sheet.PrintLineweights;
                ps.ScaleLineweights = false;

                bool usePlotStyle = settings == null || settings.UsePlotStyle;
                string styleToUse = usePlotStyle ? ResolvePlotStyleSheet(db, sheet, availablePlotStyles) : null;
                if (!string.IsNullOrWhiteSpace(styleToUse))
                {
                    ps.PlotPlotStyles = true;
                    psv.SetCurrentStyleSheet(ps, styleToUse);
                }
                else
                {
                    ps.PlotPlotStyles = false;
                }

                return ps;
            }
            catch
            {
                ps.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Validates a PlotInfo against the given Layout and PlotSettings using PlotInfoValidator.
        /// </summary>
        public PlotInfo CreateAndValidatePlotInfo(ObjectId layoutId, PlotSettings plotSettings)
        {
            PlotInfo plotInfo = new PlotInfo
            {
                Layout = layoutId,
                OverrideSettings = plotSettings
            };

            using (PlotInfoValidator validator = new PlotInfoValidator())
            {
                validator.MediaMatchingPolicy = MatchingPolicy.MatchEnabled;
                validator.Validate(plotInfo);
            }

            return plotInfo;
        }

        private static string ResolvePlotStyleSheet(Database db, PrintSheet sheet, IList<string> availablePlotStyles)
        {
            if (availablePlotStyles == null || availablePlotStyles.Count == 0)
            {
                return null;
            }

            bool isCtbMode = db == null || db.PlotStyleMode;
            string requested = sheet.PlotStyle;

            if (isCtbMode)
            {
                if (sheet.ColorMode == ColorMode.Monochrome && string.IsNullOrWhiteSpace(requested))
                {
                    requested = "monochrome.ctb";
                }
                else if (sheet.ColorMode == ColorMode.Grayscale &&
                         (string.IsNullOrWhiteSpace(requested) || requested.Equals("monochrome.ctb", StringComparison.OrdinalIgnoreCase)))
                {
                    requested = "Grayscale.ctb";
                }
                else if (sheet.ColorMode == ColorMode.Color &&
                         !string.IsNullOrWhiteSpace(requested) &&
                         requested.Equals("monochrome.ctb", StringComparison.OrdinalIgnoreCase))
                {
                    requested = "acad.ctb";
                }
            }

            if (string.IsNullOrWhiteSpace(requested) ||
                requested.Equals("无", StringComparison.OrdinalIgnoreCase) ||
                requested.Equals("None", StringComparison.OrdinalIgnoreCase) ||
                requested.Equals("无 (None)", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            foreach (string avail in availablePlotStyles)
            {
                if (string.Equals(avail, requested, StringComparison.OrdinalIgnoreCase))
                {
                    return avail;
                }
            }

            return null;
        }
    }
}
