using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdf.Core.Models
{
    [Serializable]
    public class PrintSheet
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public bool IsSelected { get; set; } = true;
        public int OrderIndex { get; set; }

        public string SourceDrawingPath { get; set; }
        public string SpaceName { get; set; } = "Model";
        public string Space { get; set; } = "模型空间";
        public string FrameHandle { get; set; }
        public FrameType FrameType { get; set; }
        public string LayerName { get; set; }
        public string BlockName { get; set; }

        /// <summary>
        /// 4 ordered corners in WCS: [0]=BL, [1]=BR, [2]=TR, [3]=TL.
        /// </summary>
        public Point2D[] Corners { get; set; } = new Point2D[4];
        public Extents2D FrameExtents { get; set; }
        public double FrameWidthUnits { get; set; }
        public double FrameHeightUnits { get; set; }
        public double RotationAngleDeg { get; set; }
        public double RotationAngleRad => RotationAngleDeg * Math.PI / 180.0;

        public DrawingUnits DrawingUnits { get; set; } = DrawingUnits.Millimeters;
        /// <summary>
        /// Denominator of scale, e.g., 100.0 for 1:100, 1.0 for 1:1. Null when unknown.
        /// </summary>
        public double? ScaleValue { get; set; }
        public ScaleSource ScaleSource { get; set; } = ScaleSource.Unknown;
        public bool FitToPaper { get; set; }

        /// <summary>
        /// Canonical media name from DWG To PDF.pc3.
        /// </summary>
        public string TargetPaperCanonicalName { get; set; }
        /// <summary>
        /// Localized display name from DWG To PDF.pc3.
        /// </summary>
        public string TargetPaperLocaleName { get; set; }
        public double PaperWidthMm { get; set; }
        public double PaperHeightMm { get; set; }
        public PaperMargins PaperMarginsMm { get; set; }

        public PlotRotationMode PlotRotation { get; set; } = PlotRotationMode.Degrees0;
        public string PlotStyle { get; set; } = "monochrome.ctb";
        public ColorMode ColorMode { get; set; } = ColorMode.Monochrome;
        public bool PrintLineweights { get; set; } = true;

        public string DrawingNumber { get; set; }
        public string DrawingName { get; set; }
        public string OutputFileName { get; set; }

        public SheetStatus Status { get; set; } = SheetStatus.Ready;
        public List<string> WarningMessages { get; set; } = new List<string>();
        public string ErrorMessage { get; set; }

        public bool IsRotated => Math.Abs(NormalizeAngle(RotationAngleDeg)) > 0.1;

        public string FormattedGeometryInfo
        {
            get
            {
                string rotTag = IsRotated ? string.Format(" 旋转:{0:F1}°", RotationAngleDeg) : "";
                return string.Format("{0:F0}x{1:F0}{2}", FrameWidthUnits, FrameHeightUnits, rotTag);
            }
        }

        public string FormattedScaleInfo
        {
            get
            {
                if (FitToPaper)
                {
                    return "适合纸张 (非固定比例)";
                }
                if (!ScaleValue.HasValue || ScaleValue.Value <= 0)
                {
                    return "待确认";
                }
                return string.Format("1:{0:G} ({1})", ScaleValue.Value, ScaleSource.ToDisplayText());
            }
        }

        public string FormattedStatusSummary
        {
            get
            {
                string baseStatus = Status.ToDisplayText();
                if (!string.IsNullOrWhiteSpace(ErrorMessage))
                {
                    return string.Format("{0}: {1}", baseStatus, ErrorMessage);
                }
                if (WarningMessages != null && WarningMessages.Count > 0)
                {
                    return string.Format("{0} ({1})", baseStatus, string.Join("; ", WarningMessages));
                }
                return baseStatus;
            }
        }

        /// <summary>
        /// Creates an immutable-like snapshot for plot execution so UI edits cannot mutate an in-flight job.
        /// </summary>
        public PrintSheet CloneSnapshot()
        {
            return new PrintSheet
            {
                Id = Id,
                IsSelected = IsSelected,
                OrderIndex = OrderIndex,
                SourceDrawingPath = SourceDrawingPath,
                SpaceName = SpaceName,
                Space = Space,
                FrameHandle = FrameHandle,
                FrameType = FrameType,
                LayerName = LayerName,
                BlockName = BlockName,
                Corners = Corners != null ? (Point2D[])Corners.Clone() : new Point2D[4],
                FrameExtents = FrameExtents,
                FrameWidthUnits = FrameWidthUnits,
                FrameHeightUnits = FrameHeightUnits,
                RotationAngleDeg = RotationAngleDeg,
                DrawingUnits = DrawingUnits,
                ScaleValue = ScaleValue,
                ScaleSource = ScaleSource,
                FitToPaper = FitToPaper,
                TargetPaperCanonicalName = TargetPaperCanonicalName,
                TargetPaperLocaleName = TargetPaperLocaleName,
                PaperWidthMm = PaperWidthMm,
                PaperHeightMm = PaperHeightMm,
                PaperMarginsMm = PaperMarginsMm,
                PlotRotation = PlotRotation,
                PlotStyle = PlotStyle,
                ColorMode = ColorMode,
                PrintLineweights = PrintLineweights,
                DrawingNumber = DrawingNumber,
                DrawingName = DrawingName,
                OutputFileName = OutputFileName,
                Status = Status,
                WarningMessages = WarningMessages != null ? WarningMessages.ToList() : new List<string>(),
                ErrorMessage = ErrorMessage
            };
        }

        private static double NormalizeAngle(double deg)
        {
            while (deg <= -180.0) deg += 360.0;
            while (deg > 180.0) deg -= 360.0;
            return deg;
        }
    }
}
