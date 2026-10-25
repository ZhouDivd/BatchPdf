using System;
using System.Collections.Generic;

namespace BatchPdf.Core.Models
{
    public class DeviceCheckResult
    {
        public const string RequiredDeviceTargetName = "DWG To PDF.pc3";

        public bool IsAvailable { get; set; }
        public string MatchedDeviceName { get; set; }
        public string DiagnosticMessage { get; set; }
        public List<string> AllAvailableDevices { get; set; } = new List<string>();
    }

    [Serializable]
    public class PaperMedia
    {
        /// <summary>
        /// Canonical media name returned by PlotSettingsValidator.GetCanonicalMediaNameList.
        /// Used when calling SetCanonicalMediaName.
        /// </summary>
        public string CanonicalName { get; set; }

        /// <summary>
        /// Localized display name returned by PlotSettingsValidator.GetLocaleMediaName.
        /// Used for UI display only.
        /// </summary>
        public string LocaleName { get; set; }

        /// <summary>
        /// Physical paper width in mm (from PlotSettings.PlotPaperSize.X).
        /// </summary>
        public double WidthMm { get; set; }

        /// <summary>
        /// Physical paper height in mm (from PlotSettings.PlotPaperSize.Y).
        /// </summary>
        public double HeightMm { get; set; }

        /// <summary>
        /// Printable margins in mm (from PlotSettings.PlotPaperMargins).
        /// </summary>
        public PaperMargins MarginsMm { get; set; }

        public double PrintableWidthMm => Math.Max(0.0, WidthMm - MarginsMm.Left - MarginsMm.Right);
        public double PrintableHeightMm => Math.Max(0.0, HeightMm - MarginsMm.Bottom - MarginsMm.Top);

        public double LongEdgeMm => Math.Max(WidthMm, HeightMm);
        public double ShortEdgeMm => Math.Min(WidthMm, HeightMm);

        public bool IsCustomOrLengthened { get; set; }

        public string DisplayLabel
        {
            get
            {
                string name = string.IsNullOrWhiteSpace(LocaleName) ? CanonicalName : LocaleName;
                return string.Format("{0} ({1:F0}x{2:F0} mm)", name, WidthMm, HeightMm);
            }
        }

        public override string ToString()
        {
            return DisplayLabel;
        }
    }

    public class TargetPaperSize
    {
        public double ExpectedWidthMm { get; set; }
        public double ExpectedHeightMm { get; set; }
        public bool IsLandscape => ExpectedWidthMm >= ExpectedHeightMm;
        public double LongEdgeMm => Math.Max(ExpectedWidthMm, ExpectedHeightMm);
        public double ShortEdgeMm => Math.Min(ExpectedWidthMm, ExpectedHeightMm);
        public bool IsValid => ExpectedWidthMm > 0.1 && ExpectedHeightMm > 0.1;
    }

    public class PaperMatchResult
    {
        public bool IsMatched { get; set; }
        public PaperMedia MatchedMedia { get; set; }
        public PlotRotationMode RecommendedPlotRotation { get; set; }
        public double WidthDifferenceMm { get; set; }
        public double HeightDifferenceMm { get; set; }
        public double TotalDeviationMm => Math.Abs(WidthDifferenceMm) + Math.Abs(HeightDifferenceMm);
        public bool HasClippingRisk { get; set; }
        public string Message { get; set; }
        public List<PaperMedia> ClosestCandidates { get; set; } = new List<PaperMedia>();
    }

    public class PrintableAreaValidationResult
    {
        public bool IsValid { get; set; }
        public bool HasClippingRisk { get; set; }
        public double ExpectedPaperWidthMm { get; set; }
        public double ExpectedPaperHeightMm { get; set; }
        public double PrintableWidthMm { get; set; }
        public double PrintableHeightMm { get; set; }
        public string WarningMessage { get; set; }
    }
}
