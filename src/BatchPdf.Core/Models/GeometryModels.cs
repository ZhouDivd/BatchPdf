using System;

namespace BatchPdf.Core.Models
{
    [Serializable]
    public struct Point2D : IEquatable<Point2D>
    {
        public double X { get; set; }
        public double Y { get; set; }

        public Point2D(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double DistanceTo(Point2D other)
        {
            double dx = other.X - X;
            double dy = other.Y - Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public Point2D RotateAround(Point2D origin, double angleRadians)
        {
            double cos = Math.Cos(angleRadians);
            double sin = Math.Sin(angleRadians);
            double dx = X - origin.X;
            double dy = Y - origin.Y;
            return new Point2D(
                origin.X + dx * cos - dy * sin,
                origin.Y + dx * sin + dy * cos);
        }

        public bool Equals(Point2D other)
        {
            return Math.Abs(X - other.X) < 1e-6 && Math.Abs(Y - other.Y) < 1e-6;
        }

        public override bool Equals(object obj)
        {
            return obj is Point2D other && Equals(other);
        }

        public override int GetHashCode()
        {
            return X.GetHashCode() ^ (Y.GetHashCode() * 397);
        }

        public override string ToString()
        {
            return string.Format("({0:F2}, {1:F2})", X, Y);
        }
    }

    [Serializable]
    public struct Extents2D
    {
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }

        public Extents2D(double minX, double minY, double maxX, double maxY)
        {
            MinX = Math.Min(minX, maxX);
            MinY = Math.Min(minY, maxY);
            MaxX = Math.Max(minX, maxX);
            MaxY = Math.Max(minY, maxY);
        }

        public double Width => Math.Max(0.0, MaxX - MinX);
        public double Height => Math.Max(0.0, MaxY - MinY);
        public double Area => Width * Height;
        public Point2D Center => new Point2D((MinX + MaxX) * 0.5, (MinY + MaxY) * 0.5);

        public bool Contains(Extents2D inner, double tolerance = 1.0)
        {
            return inner.MinX >= MinX - tolerance &&
                   inner.MaxX <= MaxX + tolerance &&
                   inner.MinY >= MinY - tolerance &&
                   inner.MaxY <= MaxY + tolerance;
        }

        public bool Intersects(Extents2D other, double tolerance = 0.0)
        {
            return !(other.MaxX < MinX - tolerance ||
                     other.MinX > MaxX + tolerance ||
                     other.MaxY < MinY - tolerance ||
                     other.MinY > MaxY + tolerance);
        }

        public double IntersectionArea(Extents2D other)
        {
            double ix = Math.Max(0.0, Math.Min(MaxX, other.MaxX) - Math.Max(MinX, other.MinX));
            double iy = Math.Max(0.0, Math.Min(MaxY, other.MaxY) - Math.Max(MinY, other.MinY));
            return ix * iy;
        }

        public static Extents2D FromPoints(Point2D[] points)
        {
            if (points == null || points.Length == 0)
            {
                return new Extents2D(0, 0, 0, 0);
            }

            double minX = points[0].X;
            double minY = points[0].Y;
            double maxX = points[0].X;
            double maxY = points[0].Y;

            for (int i = 1; i < points.Length; i++)
            {
                if (points[i].X < minX) minX = points[i].X;
                if (points[i].Y < minY) minY = points[i].Y;
                if (points[i].X > maxX) maxX = points[i].X;
                if (points[i].Y > maxY) maxY = points[i].Y;
            }

            return new Extents2D(minX, minY, maxX, maxY);
        }

        public override string ToString()
        {
            return string.Format("[({0:F2},{1:F2}) - ({2:F2},{3:F2}), {4:F1}x{5:F1}]",
                MinX, MinY, MaxX, MaxY, Width, Height);
        }
    }

    [Serializable]
    public struct PaperMargins
    {
        public double Left { get; set; }
        public double Bottom { get; set; }
        public double Right { get; set; }
        public double Top { get; set; }

        public PaperMargins(double left, double bottom, double right, double top)
        {
            Left = left;
            Bottom = bottom;
            Right = right;
            Top = top;
        }

        public override string ToString()
        {
            return string.Format("L:{0:F1} B:{1:F1} R:{2:F1} T:{3:F1}", Left, Bottom, Right, Top);
        }
    }

    public enum FrameType
    {
        Polyline = 0,
        BlockReference = 1,
        ManualWindow = 2,
        LineLoop = 3
    }

    public enum FrameRecognitionMode
    {
        ByLayer = 0,
        ByBlock = 1,
        ManualPick = 2
    }

    public static class FrameRecognitionModeExtensions
    {
        public static string ToDisplayText(this FrameRecognitionMode mode)
        {
            switch (mode)
            {
                case FrameRecognitionMode.ByLayer: return "按图层识别";
                case FrameRecognitionMode.ByBlock: return "按图块识别";
                case FrameRecognitionMode.ManualPick: return "手动框选";
                default: return mode.ToString();
            }
        }
    }

    public enum SheetSortOrderMode
    {
        RowFirst = 0,       // 先行后列
        ColumnFirst = 1,    // 先列后行
        SelectionOrder = 2  // 选择时的顺序
    }

    public static class SheetSortOrderModeExtensions
    {
        public static string ToDisplayText(this SheetSortOrderMode mode)
        {
            switch (mode)
            {
                case SheetSortOrderMode.RowFirst: return "先行后列";
                case SheetSortOrderMode.ColumnFirst: return "先列后行";
                case SheetSortOrderMode.SelectionOrder: return "按选择时的顺序";
                default: return mode.ToString();
            }
        }

        public static string GetSortOrderDescription(this SheetSortOrderMode mode, bool reverseX = false, bool reverseY = false)
        {
            if (mode == SheetSortOrderMode.SelectionOrder)
            {
                return "按选择时的顺序";
            }

            string xDir = reverseX ? "从右往左 (←)" : "从左往右 (→)";
            string yDir = reverseY ? "从下往上 (↑)" : "从上往下 (↓)";

            if (mode == SheetSortOrderMode.ColumnFirst)
            {
                return string.Format("先列后行：{0}，{1}", yDir, xDir);
            }

            return string.Format("先行后列：{0}，{1}", xDir, yDir);
        }
    }

    public enum PlotOrientationPolicy
    {
        Auto = 0,       // 自适应
        Portrait = 1,   // 纵向
        Landscape = 2   // 横向
    }

    public enum DrawingUnits
    {
        Millimeters = 0,
        Centimeters = 1,
        Meters = 2,
        Inches = 3,
        Unitless = 4
    }

    public static class DrawingUnitsExtensions
    {
        public static double ToMillimetersFactor(this DrawingUnits units)
        {
            switch (units)
            {
                case DrawingUnits.Millimeters:
                    return 1.0;
                case DrawingUnits.Centimeters:
                    return 10.0;
                case DrawingUnits.Meters:
                    return 1000.0;
                case DrawingUnits.Inches:
                    return 25.4;
                case DrawingUnits.Unitless:
                default:
                    return 1.0;
            }
        }

        public static string ToDisplayText(this DrawingUnits units)
        {
            switch (units)
            {
                case DrawingUnits.Millimeters: return "毫米(mm)";
                case DrawingUnits.Centimeters: return "厘米(cm)";
                case DrawingUnits.Meters: return "米(m)";
                case DrawingUnits.Inches: return "英寸(inch)";
                case DrawingUnits.Unitless: return "无单位(按mm计)";
                default: return units.ToString();
            }
        }
    }

    public enum ScaleSource
    {
        Unknown = 0,
        UserSpecified = 1,
        BlockAttribute = 2,
        RuleDefault = 3,
        InferredFromPaper = 4,
        FitToPaper = 5
    }

    public static class ScaleSourceExtensions
    {
        public static string ToDisplayText(this ScaleSource source)
        {
            switch (source)
            {
                case ScaleSource.UserSpecified: return "自定义比例";
                case ScaleSource.BlockAttribute: return "图框属性";
                case ScaleSource.RuleDefault: return "设定比例";
                case ScaleSource.InferredFromPaper: return "自动推算";
                case ScaleSource.FitToPaper: return "布满图纸";
                case ScaleSource.Unknown:
                default:
                    return "待确认";
            }
        }
    }

    public enum SheetStatus
    {
        Ready = 0,
        NeedsConfirmation = 1,
        Plotting = 2,
        Succeeded = 3,
        Failed = 4,
        Skipped = 5
    }

    public static class SheetStatusExtensions
    {
        public static string ToDisplayText(this SheetStatus status)
        {
            switch (status)
            {
                case SheetStatus.Ready: return "待打印";
                case SheetStatus.NeedsConfirmation: return "需确认";
                case SheetStatus.Plotting: return "打印中";
                case SheetStatus.Succeeded: return "成功";
                case SheetStatus.Failed: return "失败";
                case SheetStatus.Skipped: return "已跳过";
                default: return status.ToString();
            }
        }
    }

    public enum ColorMode
    {
        Monochrome = 0,
        Color = 1,
        Grayscale = 2,
        AsDisplayed = 3
    }

    public enum OutputMode
    {
        SinglePdfPerSheet = 0,
        MergedMultiPagePdf = 1
    }

    public enum DuplicateFilePolicy
    {
        AutoAppendIndex = 0,
        ConfirmOverwrite = 1,
        Skip = 2
    }

    public enum PlotRotationMode
    {
        Degrees0 = 0,
        Degrees90 = 1,
        Degrees180 = 2,
        Degrees270 = 3
    }
}
