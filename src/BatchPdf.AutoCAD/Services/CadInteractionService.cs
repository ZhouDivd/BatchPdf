using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcGi = Autodesk.AutoCAD.GraphicsInterface;
using BatchPdf.Core.Configuration;
using BatchPdf.Core.Models;

namespace BatchPdf.AutoCAD.Services
{
    public class CadInteractionService
    {
        private static readonly List<DBObject> ActiveOrderTransients = new List<DBObject>();

        /// <summary>
        /// Prompts the user to pick two opposite corners in the active editor to manually add a print frame.
        /// Transforms UCS picks to WCS and constructs a FrameCandidate.
        /// </summary>
        public FrameCandidate PromptManualWindowSelection(Document doc)
        {
            if (doc == null) return null;
            Editor ed = doc.Editor;

            var ppo1 = new PromptPointOptions("\n请指定手动打印图框的第一个角点: ");
            PromptPointResult ppr1 = ed.GetPoint(ppo1);
            if (ppr1.Status != PromptStatus.OK)
            {
                return null;
            }

            var pco2 = new PromptCornerOptions("\n请指定手动打印图框的对角点: ", ppr1.Value)
            {
                UseDashedLine = true
            };
            PromptPointResult ppr2 = ed.GetCorner(pco2);
            if (ppr2.Status != PromptStatus.OK)
            {
                return null;
            }

            Matrix3d ucsToWcs = ed.CurrentUserCoordinateSystem;
            Point3d wcs1 = ppr1.Value.TransformBy(ucsToWcs);
            Point3d wcs2 = ppr2.Value.TransformBy(ucsToWcs);

            double minX = Math.Min(wcs1.X, wcs2.X);
            double minY = Math.Min(wcs1.Y, wcs2.Y);
            double maxX = Math.Max(wcs1.X, wcs2.X);
            double maxY = Math.Max(wcs1.Y, wcs2.Y);

            double width = maxX - minX;
            double height = maxY - minY;
            if (width < 1e-2 || height < 1e-2)
            {
                ed.WriteMessage("\n手动框选范围过小，已取消。");
                return null;
            }

            Point2D[] corners = new Point2D[]
            {
                new Point2D(minX, minY),
                new Point2D(maxX, minY),
                new Point2D(maxX, maxY),
                new Point2D(minX, maxY)
            };

            return new FrameCandidate
            {
                FrameHandle = "MANUAL_" + Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant(),
                FrameType = FrameType.ManualWindow,
                LayerName = "(手动框选)",
                BlockName = "",
                Corners = corners,
                WcsBoundingBox = new Extents2D(minX, minY, maxX, maxY),
                WidthDrawingUnits = width,
                HeightDrawingUnits = height,
                RotationAngleRad = 0.0,
                Confidence = 1.0,
                NeedsManualCheck = false
            };
        }

        /// <summary>
        /// Implements "[ 从图中指定图层或图块 < ]": prompts the user to click a frame object in the CAD drawing,
        /// and extracts its Layer name and (if it is a BlockReference) its effective Block name.
        /// </summary>
        public bool PromptPickLayerOrBlockFromEntity(Document doc, out string layerName, out string blockName)
        {
            layerName = null;
            blockName = null;
            if (doc == null) return false;

            Editor ed = doc.Editor;
            var peo = new PromptEntityOptions("\n请点选图中的一个图框对象(多段线或图框块): ");
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK || per.ObjectId.IsNull)
            {
                return false;
            }

            Point3d wcsPicked = per.PickedPoint.TransformBy(ed.CurrentUserCoordinateSystem);

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                Entity ent = tr.GetObject(per.ObjectId, OpenMode.ForRead) as Entity;
                if (ent == null)
                {
                    return false;
                }

                ResolveEntityLayerAndBlock(tr, ent, wcsPicked, out layerName, out blockName);
                tr.Commit();
            }

            ed.WriteMessage(string.Format(
                "\n已拾取图框对象 -> 图层: [{0}]{1}",
                layerName,
                !string.IsNullOrWhiteSpace(blockName) ? string.Format(", 图块: [{0}]", blockName) : ""));
            return true;
        }

        /// <summary>
        /// Combined 2-step interactive workflow:
        /// Step 1: Pick a frame entity to specify the frame layer/block (or press Enter to keep current).
        /// Step 2: Immediately pick two corners to specify the search region (or press Enter to search full drawing).
        /// </summary>
        public bool PromptPickLayerOrBlockAndSearchRegion(
            Document doc,
            FrameRecognitionMode mode,
            string currentLayer,
            string currentBlock,
            out string pickedLayer,
            out string pickedBlock,
            out Extents2D? searchRegion)
        {
            pickedLayer = currentLayer ?? "";
            pickedBlock = currentBlock ?? "";
            searchRegion = null;
            if (doc == null) return false;

            Editor ed = doc.Editor;
            string promptMsg = mode == FrameRecognitionMode.ByBlock
                ? "\n[步骤1/2] 请在图中点选一个图框图块 (或按回车直接使用当前图块 [" + currentBlock + "]): "
                : "\n[步骤1/2] 请在图中点选一个图框对象以指定图框图层 (或按回车直接使用当前图层 [" + currentLayer + "]): ";

            var peo = new PromptEntityOptions(promptMsg)
            {
                AllowNone = true
            };

            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status == PromptStatus.OK && !per.ObjectId.IsNull)
            {
                Point3d wcsPicked = per.PickedPoint.TransformBy(ed.CurrentUserCoordinateSystem);
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    Entity ent = tr.GetObject(per.ObjectId, OpenMode.ForRead) as Entity;
                    if (ent != null)
                    {
                        ResolveEntityLayerAndBlock(tr, ent, wcsPicked, out string entLayer, out string entBlock);
                        if (!string.IsNullOrWhiteSpace(entLayer))
                        {
                            pickedLayer = entLayer;
                        }
                        if (!string.IsNullOrWhiteSpace(entBlock))
                        {
                            pickedBlock = entBlock;
                        }
                    }
                    tr.Commit();
                }

                ed.WriteMessage(
                    "\n已指定图框图层: [" + pickedLayer + "]" +
                    (!string.IsNullOrWhiteSpace(pickedBlock) ? ", 图块: [" + pickedBlock + "]" : ""));
            }
            else if (per.Status == PromptStatus.None)
            {
                pickedLayer = currentLayer;
                pickedBlock = currentBlock;
            }
            else
            {
                return false;
            }

            // Step 2: Immediately prompt the user to select the search region
            var ppo1 = new PromptPointOptions("\n[步骤2/2] 请框选搜图范围 - 指定第一个角点 (直接按回车则搜索全图): ")
            {
                AllowNone = true
            };
            PromptPointResult ppr1 = ed.GetPoint(ppo1);
            if (ppr1.Status == PromptStatus.None)
            {
                searchRegion = null;
                return true;
            }
            if (ppr1.Status != PromptStatus.OK)
            {
                return false;
            }

            var pco2 = new PromptCornerOptions("\n[步骤2/2] 请指定搜图范围的对角点: ", ppr1.Value)
            {
                UseDashedLine = true
            };
            PromptPointResult ppr2 = ed.GetCorner(pco2);
            if (ppr2.Status != PromptStatus.OK)
            {
                return false;
            }

            Matrix3d ucsToWcs = ed.CurrentUserCoordinateSystem;
            Point3d wcs1 = ppr1.Value.TransformBy(ucsToWcs);
            Point3d wcs2 = ppr2.Value.TransformBy(ucsToWcs);

            double minX = Math.Min(wcs1.X, wcs2.X);
            double minY = Math.Min(wcs1.Y, wcs2.Y);
            double maxX = Math.Max(wcs1.X, wcs2.X);
            double maxY = Math.Max(wcs1.Y, wcs2.Y);

            if (maxX - minX >= 1.0 && maxY - minY >= 1.0)
            {
                searchRegion = new Extents2D(minX, minY, maxX, maxY);
            }
            else
            {
                searchRegion = null;
            }

            return true;
        }

        private static void ResolveEntityLayerAndBlock(
            Transaction tr,
            Entity ent,
            Point3d? wcsPickedPoint,
            out string layerName,
            out string blockName)
        {
            layerName = ent?.Layer ?? "";
            blockName = null;
            if (ent == null) return;

            if (ent is BlockReference br)
            {
                blockName = FrameScanner.GetEffectiveBlockName(tr, br);

                if (string.IsNullOrWhiteSpace(layerName) ||
                    string.Equals(layerName, "0", StringComparison.OrdinalIgnoreCase))
                {
                    string innerLayer = TryResolveBlockDominantCurveLayer(tr, br, wcsPickedPoint);
                    if (!string.IsNullOrWhiteSpace(innerLayer))
                    {
                        layerName = innerLayer;
                    }
                }
            }
        }

        private static string TryResolveBlockDominantCurveLayer(
            Transaction tr,
            BlockReference br,
            Point3d? wcsPickedPoint)
        {
            if (br == null || br.BlockTableRecord.IsNull) return null;

            BlockTableRecord btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null) return null;

            Point3d? localPicked = null;
            if (wcsPickedPoint.HasValue)
            {
                try
                {
                    localPicked = wcsPickedPoint.Value.TransformBy(br.BlockTransform.Inverse());
                }
                catch
                {
                }
            }

            var layerScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            string closestLayer = null;
            double minDistance = double.MaxValue;

            foreach (ObjectId childId in btr)
            {
                if (childId.IsErased) continue;
                Entity child = tr.GetObject(childId, OpenMode.ForRead) as Entity;
                if (child == null || !child.Visible || !(child is Curve curve)) continue;

                string cLayer = (child.Layer ?? "").Trim();
                if (string.IsNullOrWhiteSpace(cLayer) ||
                    string.Equals(cLayer, "0", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cLayer, "Defpoints", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                double weight = 100.0;
                try
                {
                    Extents3d cExt = child.GeometricExtents;
                    double diag = cExt.MinPoint.DistanceTo(cExt.MaxPoint);
                    if (diag > 1.0) weight = diag;
                }
                catch
                {
                }

                if (cLayer.IndexOf("TK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cLayer.IndexOf("BORDER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cLayer.IndexOf("TITLE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cLayer.IndexOf("图框", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cLayer.IndexOf("FRAME", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    weight *= 4.0;
                }

                if (child is Polyline pl && pl.NumberOfVertices >= 4)
                {
                    weight *= 2.0;
                }

                if (layerScores.TryGetValue(cLayer, out double existing))
                {
                    layerScores[cLayer] = existing + weight;
                }
                else
                {
                    layerScores[cLayer] = weight;
                }

                if (localPicked.HasValue)
                {
                    try
                    {
                        Point3d closestPt = curve.GetClosestPointTo(localPicked.Value, false);
                        double dist = closestPt.DistanceTo(localPicked.Value);
                        if (dist < minDistance)
                        {
                            minDistance = dist;
                            closestLayer = cLayer;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            if (layerScores.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(closestLayer) && minDistance <= 50.0)
            {
                return closestLayer;
            }

            return layerScores
                .OrderByDescending(kvp => kvp.Value)
                .First()
                .Key;
        }

        /// <summary>
        /// Implements "[ 选择搜图区域 ]": prompts the user to pick two corners in CAD to define a rectangular search area.
        /// </summary>
        public Extents2D? PromptSelectSearchRegion(Document doc)
        {
            if (doc == null) return null;
            Editor ed = doc.Editor;

            var ppo1 = new PromptPointOptions("\n请指定搜图区域的第一个角点(按ESC取消限制并扫描全图): ");
            PromptPointResult ppr1 = ed.GetPoint(ppo1);
            if (ppr1.Status != PromptStatus.OK)
            {
                return null;
            }

            var pco2 = new PromptCornerOptions("\n请指定搜图区域的对角点: ", ppr1.Value)
            {
                UseDashedLine = true
            };
            PromptPointResult ppr2 = ed.GetCorner(pco2);
            if (ppr2.Status != PromptStatus.OK)
            {
                return null;
            }

            Matrix3d ucsToWcs = ed.CurrentUserCoordinateSystem;
            Point3d wcs1 = ppr1.Value.TransformBy(ucsToWcs);
            Point3d wcs2 = ppr2.Value.TransformBy(ucsToWcs);

            double minX = Math.Min(wcs1.X, wcs2.X);
            double minY = Math.Min(wcs1.Y, wcs2.Y);
            double maxX = Math.Max(wcs1.X, wcs2.X);
            double maxY = Math.Max(wcs1.Y, wcs2.Y);

            if (maxX - minX < 1.0 || maxY - minY < 1.0)
            {
                return null;
            }

            return new Extents2D(minX, minY, maxX, maxY);
        }

        /// <summary>
        /// Implements "点选图框": allows the user to select one or more frame entities in CAD (or pick a 2-point window).
        /// </summary>
        public List<FrameCandidate> PromptPickFrameEntities(
            Document doc,
            FrameScanner scanner,
            BatchPdfSettings settings)
        {
            var results = new List<FrameCandidate>();
            if (doc == null || scanner == null) return results;

            Editor ed = doc.Editor;
            var pso = new PromptSelectionOptions
            {
                MessageForAdding = "\n请选择图框对象(支持点选/框选多段线或图框块，直接回车改为两点拉框): "
            };

            PromptSelectionResult psr = ed.GetSelection(pso);
            if (psr.Status == PromptStatus.OK && psr.Value != null && psr.Value.Count > 0)
            {
                using (DocumentLock docLock = doc.LockDocument())
                {
                    ObjectId[] ids = psr.Value.GetObjectIds();
                    results = scanner.ExtractCandidatesFromObjectIds(doc.Database, ids, settings);
                }
            }

            if (results.Count == 0)
            {
                var manualWin = PromptManualWindowSelection(doc);
                if (manualWin != null)
                {
                    results.Add(manualWin);
                }
            }

            return results;
        }

        /// <summary>
        /// Creates and displays transient graphics in the CAD viewport for the search region,
        /// recognized sheet boundaries, large #01/#02 order labels, and directional arrows between consecutive sheets.
        /// </summary>
        public void UpdateSheetOrderTransients(Document doc, IList<PrintSheet> sheets, Extents2D? searchRegion = null)
        {
            ClearSheetOrderTransients(doc);
            if (doc == null) return;

            var tm = AcGi.TransientManager.CurrentTransientManager;
            var viewportNumbers = new IntegerCollection();

            try
            {
                using (DocumentLock docLock = doc.LockDocument())
                {
                    Database db = doc.Database;

                    // 1. Search region boundary rectangle (color 6 = magenta)
                    if (searchRegion.HasValue && searchRegion.Value.Width > 1.0 && searchRegion.Value.Height > 1.0)
                    {
                        Extents2D reg = searchRegion.Value;
                        var regPoly = new Polyline(4);
                        if (db != null) regPoly.SetDatabaseDefaults(db);
                        regPoly.AddVertexAt(0, new Point2d(reg.MinX, reg.MinY), 0.0, 0.0, 0.0);
                        regPoly.AddVertexAt(1, new Point2d(reg.MaxX, reg.MinY), 0.0, 0.0, 0.0);
                        regPoly.AddVertexAt(2, new Point2d(reg.MaxX, reg.MaxY), 0.0, 0.0, 0.0);
                        regPoly.AddVertexAt(3, new Point2d(reg.MinX, reg.MaxY), 0.0, 0.0, 0.0);
                        regPoly.Closed = true;
                        regPoly.ColorIndex = 6;
                        RegisterTransientObject(tm, regPoly, viewportNumbers);
                    }

                    if (sheets != null && sheets.Count > 0)
                    {
                        PrintSheet prevSelectedSheet = null;

                        for (int i = 0; i < sheets.Count; i++)
                        {
                            PrintSheet sheet = sheets[i];
                            if (sheet == null || sheet.FrameExtents.Width <= 0 || sheet.FrameExtents.Height <= 0)
                            {
                                continue;
                            }

                            Point2D[] c = sheet.Corners;
                            if (c == null || c.Length < 4)
                            {
                                Extents2D b = sheet.FrameExtents;
                                c = new Point2D[]
                                {
                                    new Point2D(b.MinX, b.MinY),
                                    new Point2D(b.MaxX, b.MinY),
                                    new Point2D(b.MaxX, b.MaxY),
                                    new Point2D(b.MinX, b.MaxY)
                                };
                            }

                            double shortEdge = Math.Min(sheet.FrameExtents.Width, sheet.FrameExtents.Height);

                            // 2a. Sheet border rectangle Polyline (color 3 = green if selected, 8 = gray if unselected)
                            var borderPoly = new Polyline(4);
                            if (db != null) borderPoly.SetDatabaseDefaults(db);
                            borderPoly.AddVertexAt(0, new Point2d(c[0].X, c[0].Y), 0.0, 0.0, 0.0);
                            borderPoly.AddVertexAt(1, new Point2d(c[1].X, c[1].Y), 0.0, 0.0, 0.0);
                            borderPoly.AddVertexAt(2, new Point2d(c[2].X, c[2].Y), 0.0, 0.0, 0.0);
                            borderPoly.AddVertexAt(3, new Point2d(c[3].X, c[3].Y), 0.0, 0.0, 0.0);
                            borderPoly.Closed = true;
                            borderPoly.ColorIndex = sheet.IsSelected ? 3 : 8;
                            RegisterTransientObject(tm, borderPoly, viewportNumbers);

                            // 2b. Sheet order badge box and large DBText (#01, #02, ...)
                            Point2D center2d = sheet.FrameExtents.Center;
                            Point3d centerPt = new Point3d(center2d.X, center2d.Y, 0.0);
                            double textHeight = Math.Max(shortEdge * 0.14, 100.0);
                            int displayIndex = sheet.OrderIndex > 0 ? sheet.OrderIndex : (i + 1);

                            double badgeHalfW = textHeight * 1.35;
                            double badgeHalfH = textHeight * 0.72;
                            var badgePoly = new Polyline(4);
                            if (db != null) badgePoly.SetDatabaseDefaults(db);
                            badgePoly.AddVertexAt(0, new Point2d(centerPt.X - badgeHalfW, centerPt.Y - badgeHalfH), 0.0, 0.0, 0.0);
                            badgePoly.AddVertexAt(1, new Point2d(centerPt.X + badgeHalfW, centerPt.Y - badgeHalfH), 0.0, 0.0, 0.0);
                            badgePoly.AddVertexAt(2, new Point2d(centerPt.X + badgeHalfW, centerPt.Y + badgeHalfH), 0.0, 0.0, 0.0);
                            badgePoly.AddVertexAt(3, new Point2d(centerPt.X - badgeHalfW, centerPt.Y + badgeHalfH), 0.0, 0.0, 0.0);
                            badgePoly.Closed = true;
                            badgePoly.ColorIndex = sheet.IsSelected ? 1 : 8;
                            RegisterTransientObject(tm, badgePoly, viewportNumbers);

                            var orderText = new DBText();
                            if (db != null) orderText.SetDatabaseDefaults(db);
                            orderText.TextString = string.Format("#{0:D2}", displayIndex);
                            orderText.Height = textHeight;
                            orderText.ColorIndex = sheet.IsSelected ? 2 : 8;
                            orderText.Position = centerPt;
                            orderText.HorizontalMode = TextHorizontalMode.TextCenter;
                            orderText.VerticalMode = TextVerticalMode.TextVerticalMid;
                            orderText.AlignmentPoint = centerPt;
                            try
                            {
                                if (db != null) orderText.AdjustAlignment(db);
                            }
                            catch
                            {
                            }
                            RegisterTransientObject(tm, orderText, viewportNumbers);

                            // 2c. Connecting line & arrowhead between consecutive selected sheets
                            if (sheet.IsSelected)
                            {
                                if (prevSelectedSheet != null)
                                {
                                    Point2D prevC = prevSelectedSheet.FrameExtents.Center;
                                    Point2D currC = center2d;
                                    double dx = currC.X - prevC.X;
                                    double dy = currC.Y - prevC.Y;
                                    double dist = Math.Sqrt(dx * dx + dy * dy);

                                    if (dist > 1e-3)
                                    {
                                        var connLine = new Line(
                                            new Point3d(prevC.X, prevC.Y, 0.0),
                                            new Point3d(currC.X, currC.Y, 0.0));
                                        if (db != null) connLine.SetDatabaseDefaults(db);
                                        connLine.ColorIndex = 2;
                                        RegisterTransientObject(tm, connLine, viewportNumbers);

                                        // Arrowhead at 75% along the connecting line pointing toward current sheet
                                        Point3d tip = new Point3d(prevC.X + dx * 0.75, prevC.Y + dy * 0.75, 0.0);
                                        double ux = dx / dist;
                                        double uy = dy / dist;
                                        double arrowLen = Math.Max(shortEdge * 0.08, Math.Min(dist * 0.12, 500.0));
                                        double cosA = Math.Cos(Math.PI / 6.5);
                                        double sinA = Math.Sin(Math.PI / 6.5);

                                        double w1x = -ux * cosA + uy * sinA;
                                        double w1y = -uy * cosA - ux * sinA;
                                        double w2x = -ux * cosA - uy * sinA;
                                        double w2y = -uy * cosA + ux * sinA;

                                        var wing1 = new Line(
                                            tip,
                                            new Point3d(tip.X + w1x * arrowLen, tip.Y + w1y * arrowLen, 0.0));
                                        if (db != null) wing1.SetDatabaseDefaults(db);
                                        wing1.ColorIndex = 2;
                                        RegisterTransientObject(tm, wing1, viewportNumbers);

                                        var wing2 = new Line(
                                            tip,
                                            new Point3d(tip.X + w2x * arrowLen, tip.Y + w2y * arrowLen, 0.0));
                                        if (db != null) wing2.SetDatabaseDefaults(db);
                                        wing2.ColorIndex = 2;
                                        RegisterTransientObject(tm, wing2, viewportNumbers);
                                    }
                                }

                                prevSelectedSheet = sheet;
                            }
                        }
                    }
                }

                doc.Editor.UpdateScreen();
            }
            catch
            {
            }
        }

        private static void RegisterTransientObject(
            AcGi.TransientManager tm,
            DBObject obj,
            IntegerCollection viewportNumbers)
        {
            if (tm == null || obj == null) return;
            tm.AddTransient(obj, AcGi.TransientDrawingMode.Main, 128, viewportNumbers);
            ActiveOrderTransients.Add(obj);
        }

        /// <summary>
        /// Erases and disposes all active sheet order transient graphics from the CAD viewport.
        /// </summary>
        public void ClearSheetOrderTransients(Document doc = null)
        {
            if (ActiveOrderTransients.Count > 0)
            {
                var tm = AcGi.TransientManager.CurrentTransientManager;
                var viewportNumbers = new IntegerCollection();

                foreach (DBObject obj in ActiveOrderTransients)
                {
                    if (obj == null) continue;
                    try
                    {
                        tm.EraseTransient(obj, viewportNumbers);
                    }
                    catch
                    {
                    }
                    try
                    {
                        obj.Dispose();
                    }
                    catch
                    {
                    }
                }

                ActiveOrderTransients.Clear();
            }

            try
            {
                doc?.Editor.UpdateScreen();
            }
            catch
            {
            }
        }

        /// <summary>
        /// Backwards-compatible overload for highlighting recognized sheets in the active viewport.
        /// </summary>
        public void HighlightSheetsInViewport(Document doc, IList<PrintSheet> sheets)
        {
            HighlightSheetsInViewport(doc, sheets, null);
        }

        /// <summary>
        /// Implements "[ 亮显图框 ]": zooms the viewport to show all recognized sheets and draws transient
        /// highlight borders, #01/#02 order labels, and connecting arrows in the CAD editor.
        /// </summary>
        public void HighlightSheetsInViewport(Document doc, IList<PrintSheet> sheets, Extents2D? searchRegion = null)
        {
            if (doc == null || sheets == null || sheets.Count == 0) return;
            Editor ed = doc.Editor;

            var validSheets = sheets.Where(s => s != null && s.FrameExtents.Width > 0 && s.FrameExtents.Height > 0).ToList();
            if (validSheets.Count == 0) return;

            try
            {
                double minX = validSheets.Min(s => s.FrameExtents.MinX);
                double minY = validSheets.Min(s => s.FrameExtents.MinY);
                double maxX = validSheets.Max(s => s.FrameExtents.MaxX);
                double maxY = validSheets.Max(s => s.FrameExtents.MaxY);

                if (searchRegion.HasValue && searchRegion.Value.Width > 1.0 && searchRegion.Value.Height > 1.0)
                {
                    minX = Math.Min(minX, searchRegion.Value.MinX);
                    minY = Math.Min(minY, searchRegion.Value.MinY);
                    maxX = Math.Max(maxX, searchRegion.Value.MaxX);
                    maxY = Math.Max(maxY, searchRegion.Value.MaxY);
                }

                Extents2D allExt = new Extents2D(minX, minY, maxX, maxY);

                using (DocumentLock docLock = doc.LockDocument())
                {
                    using (ViewTableRecord view = ed.GetCurrentView())
                    {
                        Point2D center = allExt.Center;
                        Matrix3d worldToEye = Matrix3d.WorldToPlane(view.ViewDirection) *
                                              Matrix3d.Displacement(Point3d.Origin - view.Target) *
                                              Matrix3d.Rotation(view.ViewTwist, view.ViewDirection, view.Target);

                        Point3d dcsCenter = new Point3d(center.X, center.Y, 0).TransformBy(worldToEye);
                        view.CenterPoint = new Point2d(dcsCenter.X, dcsCenter.Y);
                        view.Width = allExt.Width * 1.18;
                        view.Height = allExt.Height * 1.18;
                        ed.SetCurrentView(view);
                    }
                }

                UpdateSheetOrderTransients(doc, validSheets, searchRegion);

                ed.WriteMessage("\n已在图中亮显识别到的图框及排列顺序 (#01 -> #02 -> ...)。\n");
            }
            catch
            {
            }
        }

        /// <summary>
        /// Zooms the active viewport temporarily to highlight/preview a selected PrintSheet without modifying DWG entities.
        /// </summary>
        public void ZoomToSheet(Document doc, PrintSheet sheet)
        {
            if (doc == null || sheet == null) return;
            Editor ed = doc.Editor;

            try
            {
                Extents2D ext = sheet.FrameExtents;
                if (ext.Width <= 0 || ext.Height <= 0) return;

                using (DocumentLock docLock = doc.LockDocument())
                {
                    string targetLayout = ResolveLayoutName(sheet);
                    if (!string.Equals(LayoutManager.Current.CurrentLayout, targetLayout, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            LayoutManager.Current.CurrentLayout = targetLayout;
                        }
                        catch { }
                    }

                    using (ViewTableRecord view = ed.GetCurrentView())
                    {
                        Point2D center = ext.Center;
                        Matrix3d worldToEye = Matrix3d.WorldToPlane(view.ViewDirection) *
                                              Matrix3d.Displacement(Point3d.Origin - view.Target) *
                                              Matrix3d.Rotation(view.ViewTwist, view.ViewDirection, view.Target);

                        Point3d dcsCenter = new Point3d(center.X, center.Y, 0).TransformBy(worldToEye);
                        view.CenterPoint = new Point2d(dcsCenter.X, dcsCenter.Y);
                        view.Width = ext.Width * 1.15;
                        view.Height = ext.Height * 1.15;
                        ed.SetCurrentView(view);
                    }

                    Matrix3d wcsToUcs = ed.CurrentUserCoordinateSystem.Inverse();
                    DrawSheetHighlightBox(ed, sheet, wcsToUcs, colorIndex: 1);
                }
            }
            catch
            {
            }
        }

        private static void DrawSheetHighlightBox(Editor ed, PrintSheet sheet, Matrix3d wcsToUcs, int colorIndex)
        {
            Point2D[] c = sheet.Corners;
            if (c == null || c.Length < 4)
            {
                Extents2D b = sheet.FrameExtents;
                c = new Point2D[]
                {
                    new Point2D(b.MinX, b.MinY),
                    new Point2D(b.MaxX, b.MinY),
                    new Point2D(b.MaxX, b.MaxY),
                    new Point2D(b.MinX, b.MaxY)
                };
            }

            Point3d p0 = new Point3d(c[0].X, c[0].Y, 0).TransformBy(wcsToUcs);
            Point3d p1 = new Point3d(c[1].X, c[1].Y, 0).TransformBy(wcsToUcs);
            Point3d p2 = new Point3d(c[2].X, c[2].Y, 0).TransformBy(wcsToUcs);
            Point3d p3 = new Point3d(c[3].X, c[3].Y, 0).TransformBy(wcsToUcs);

            ed.DrawVector(p0, p1, colorIndex, true);
            ed.DrawVector(p1, p2, colorIndex, true);
            ed.DrawVector(p2, p3, colorIndex, true);
            ed.DrawVector(p3, p0, colorIndex, true);
            // Draw diagonal cross to make frame unmistakably visible
            ed.DrawVector(p0, p2, colorIndex, false);
            ed.DrawVector(p1, p3, colorIndex, false);
        }

        private static string ResolveLayoutName(PrintSheet sheet)
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
    }
}
