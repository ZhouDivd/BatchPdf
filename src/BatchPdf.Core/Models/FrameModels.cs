using System;
using System.Collections.Generic;

namespace BatchPdf.Core.Models
{
    public class FrameCandidate
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string FrameHandle { get; set; }
        public FrameType FrameType { get; set; }
        public string LayerName { get; set; }
        public string BlockName { get; set; }

        /// <summary>
        /// Source space or layout description, e.g. "模型空间" or "布局: Layout1".
        /// </summary>
        public string Space { get; set; } = "模型空间";

        /// <summary>
        /// Layout name if in paper space (e.g. "Layout1"), or "Model" / "ModelSpace".
        /// </summary>
        public string LayoutName { get; set; } = "Model";

        /// <summary>
        /// True if this candidate originates from a PaperSpace layout rather than ModelSpace.
        /// </summary>
        public bool IsPaperSpace => !string.IsNullOrWhiteSpace(LayoutName) &&
                                    !LayoutName.Equals("Model", StringComparison.OrdinalIgnoreCase) &&
                                    !LayoutName.Equals("ModelSpace", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Four corners in WCS ordered along the frame's local coordinate system:
        /// [0] Bottom-Left, [1] Bottom-Right, [2] Top-Right, [3] Top-Left.
        /// </summary>
        public Point2D[] Corners { get; set; } = new Point2D[4];

        /// <summary>
        /// Axis-aligned bounding box in WCS.
        /// </summary>
        public Extents2D WcsBoundingBox { get; set; }

        /// <summary>
        /// Frame width along local X axis ([0] -> [1]) in drawing units.
        /// </summary>
        public double WidthDrawingUnits { get; set; }

        /// <summary>
        /// Frame height along local Y axis ([0] -> [3]) in drawing units.
        /// </summary>
        public double HeightDrawingUnits { get; set; }

        /// <summary>
        /// Rotation angle of local X axis ([0] -> [1]) in radians in [-PI, PI].
        /// </summary>
        public double RotationAngleRad { get; set; }

        /// <summary>
        /// Rotation angle in degrees.
        /// </summary>
        public double RotationAngleDeg => RotationAngleRad * 180.0 / Math.PI;

        /// <summary>
        /// True when rotation angle is not aligned with 0 degrees (within 0.1 deg tolerance).
        /// </summary>
        public bool IsRotated
        {
            get
            {
                double deg = NormalizeAngleDeg(RotationAngleDeg);
                return Math.Abs(deg) > 0.1;
            }
        }

        /// <summary>
        /// True when rotation angle is neither 0/180 nor 90/270 (i.e., oblique angle requiring view twist).
        /// </summary>
        public bool IsObliqueRotated
        {
            get
            {
                double mod90 = Math.Abs(RotationAngleDeg % 90.0);
                if (mod90 > 45.0) mod90 = 90.0 - mod90;
                return mod90 > 0.1;
            }
        }

        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string ExtractedDrawingNumber { get; set; }
        public string ExtractedDrawingName { get; set; }
        public string ExtractedScaleText { get; set; }
        public double? ExtractedScaleValue { get; set; }

        /// <summary>
        /// Geometric and rule confidence score [0.0, 1.0].
        /// </summary>
        public double Confidence { get; set; } = 1.0;

        public bool NeedsManualCheck { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();

        private static double NormalizeAngleDeg(double deg)
        {
            while (deg <= -180.0) deg += 360.0;
            while (deg > 180.0) deg -= 360.0;
            return deg;
        }
    }

    /// <summary>
    /// Geometric and textual representation of a text entity (DBText or MText) used for titleblock inference.
    /// </summary>
    public class TitleTextItem
    {
        public Point2D Position { get; set; }
        public string Text { get; set; }
        public double Height { get; set; }
        public string Layer { get; set; }
    }
}
