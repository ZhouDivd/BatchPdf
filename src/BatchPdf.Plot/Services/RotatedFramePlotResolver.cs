using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdf.Core.Models;

namespace BatchPdf.Plot.Services
{
    /// <summary>
    /// Resolves WCS frame corners to DCS plot window extents, applying a temporary reversible view twist
    /// when plotting rotated frames so that rotated frames are plotted without clipping or neighbor bleed.
    /// Restores the original view and UCS state on Dispose.
    /// </summary>
    public class RotatedFramePlotResolver : IDisposable
    {
        private readonly Document _doc;
        private readonly Editor _ed;
        private ViewTableRecord _savedView;
        private Matrix3d _savedUcs;
        private string _savedLayout;
        private bool _viewWasModified;
        private bool _layoutWasSwitched;
        private bool _disposed;

        public RotatedFramePlotResolver(Document doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _ed = doc.Editor;
            CaptureOriginalState();
        }

        /// <summary>
        /// Configures the active viewport if needed for the sheet's rotation angle and returns
        /// the exact Extents2d in DCS for PlotSettingsValidator.SetPlotWindowArea.
        /// </summary>
        public Extents2d ResolvePlotWindowInDcs(PrintSheet sheet)
        {
            if (sheet == null) throw new ArgumentNullException(nameof(sheet));

            string targetLayout = ResolveLayoutName(sheet);
            if (!string.Equals(LayoutManager.Current.CurrentLayout, targetLayout, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    LayoutManager.Current.CurrentLayout = targetLayout;
                    _layoutWasSwitched = true;
                }
                catch { }
            }

            double targetTwistRad = -sheet.RotationAngleRad;
            while (targetTwistRad <= -Math.PI) targetTwistRad += 2.0 * Math.PI;
            while (targetTwistRad > Math.PI) targetTwistRad -= 2.0 * Math.PI;

            using (ViewTableRecord currentView = _ed.GetCurrentView())
            {
                bool isTopDown = currentView.ViewDirection.IsParallelTo(Vector3d.ZAxis, new Tolerance(1e-4, 1e-4)) &&
                                 currentView.ViewDirection.Z > 0;
                double currentTwist = currentView.ViewTwist;
                while (currentTwist <= -Math.PI) currentTwist += 2.0 * Math.PI;
                while (currentTwist > Math.PI) currentTwist -= 2.0 * Math.PI;

                bool needsTwistUpdate = !isTopDown || Math.Abs(currentTwist - targetTwistRad) > 1e-4;

                if (needsTwistUpdate)
                {
                    Point2D centerWcs = sheet.FrameExtents.Center;
                    currentView.ViewDirection = Vector3d.ZAxis;
                    currentView.Target = Point3d.Origin;
                    currentView.ViewTwist = targetTwistRad;

                    Point2d dcsCenter = TransformWcsToDcs(new Point3d(centerWcs.X, centerWcs.Y, 0), currentView);
                    currentView.CenterPoint = dcsCenter;
                    currentView.Width = Math.Max(sheet.FrameWidthUnits * 1.1, 100.0);
                    currentView.Height = Math.Max(sheet.FrameHeightUnits * 1.1, 100.0);

                    _ed.SetCurrentView(currentView);
                    _ed.UpdateTiledViewportsInDatabase();
                    _viewWasModified = true;
                }
            }

            using (ViewTableRecord activeView = _ed.GetCurrentView())
            {
                Point2D[] corners = (sheet.Corners != null && sheet.Corners.Length == 4)
                    ? sheet.Corners
                    : new Point2D[]
                    {
                        new Point2D(sheet.FrameExtents.MinX, sheet.FrameExtents.MinY),
                        new Point2D(sheet.FrameExtents.MaxX, sheet.FrameExtents.MinY),
                        new Point2D(sheet.FrameExtents.MaxX, sheet.FrameExtents.MaxY),
                        new Point2D(sheet.FrameExtents.MinX, sheet.FrameExtents.MaxY)
                    };

                double minX = double.MaxValue;
                double minY = double.MaxValue;
                double maxX = double.MinValue;
                double maxY = double.MinValue;

                for (int i = 0; i < corners.Length; i++)
                {
                    Point2d dcsPt = TransformWcsToDcs(new Point3d(corners[i].X, corners[i].Y, 0.0), activeView);
                    if (dcsPt.X < minX) minX = dcsPt.X;
                    if (dcsPt.Y < minY) minY = dcsPt.Y;
                    if (dcsPt.X > maxX) maxX = dcsPt.X;
                    if (dcsPt.Y > maxY) maxY = dcsPt.Y;
                }

                return new Extents2d(new Point2d(minX, minY), new Point2d(maxX, maxY));
            }
        }

        /// <summary>
        /// Converts a WCS 3D point to 2D DCS coordinates using the active ViewTableRecord's parameters.
        /// Follows Autodesk's official WCS-to-DCS transformation matrix definition.
        /// </summary>
        public static Point2d TransformWcsToDcs(Point3d wcsPoint, ViewTableRecord view)
        {
            Matrix3d matWcs2Dcs = Matrix3d.PlaneToWorld(view.ViewDirection);
            matWcs2Dcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * matWcs2Dcs;
            matWcs2Dcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * matWcs2Dcs;
            matWcs2Dcs = matWcs2Dcs.Inverse();

            Point3d transformed = wcsPoint.TransformBy(matWcs2Dcs);
            return new Point2d(transformed.X, transformed.Y);
        }

        private void CaptureOriginalState()
        {
            try
            {
                _savedLayout = LayoutManager.Current.CurrentLayout;
                _savedView = _ed.GetCurrentView();
                _savedUcs = _ed.CurrentUserCoordinateSystem;
            }
            catch
            {
            }
        }

        public void RestoreOriginalState()
        {
            if (_layoutWasSwitched && !string.IsNullOrWhiteSpace(_savedLayout))
            {
                try
                {
                    if (!string.Equals(LayoutManager.Current.CurrentLayout, _savedLayout, StringComparison.OrdinalIgnoreCase))
                    {
                        LayoutManager.Current.CurrentLayout = _savedLayout;
                    }
                    _layoutWasSwitched = false;
                }
                catch
                {
                }
            }

            if (_savedView != null)
            {
                try
                {
                    if (_viewWasModified)
                    {
                        _ed.SetCurrentView(_savedView);
                        _ed.CurrentUserCoordinateSystem = _savedUcs;
                        _ed.UpdateTiledViewportsInDatabase();
                        _viewWasModified = false;
                    }
                }
                catch
                {
                }
                finally
                {
                    _savedView.Dispose();
                    _savedView = null;
                }
            }
        }

        public static string ResolveLayoutName(PrintSheet sheet)
        {
            if (sheet == null || string.IsNullOrWhiteSpace(sheet.SpaceName) ||
                sheet.SpaceName.Equals("Model", StringComparison.OrdinalIgnoreCase) ||
                sheet.SpaceName.Equals("模型空间", StringComparison.OrdinalIgnoreCase))
            {
                return "Model";
            }

            string clean = sheet.SpaceName;
            if (clean.StartsWith("布局: ", StringComparison.OrdinalIgnoreCase))
                return clean.Substring(4).Trim();
            if (clean.StartsWith("布局：", StringComparison.OrdinalIgnoreCase))
                return clean.Substring(3).Trim();
            if (clean.StartsWith("Layout: ", StringComparison.OrdinalIgnoreCase))
                return clean.Substring(8).Trim();
            return clean;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                RestoreOriginalState();
                _disposed = true;
            }
        }
    }
}
