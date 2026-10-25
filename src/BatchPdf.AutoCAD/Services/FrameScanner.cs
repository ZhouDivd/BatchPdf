using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdf.Core.Configuration;
using BatchPdf.Core.Models;
using BatchPdf.Core.Services;

namespace BatchPdf.AutoCAD.Services
{
    public class FrameScanner
    {
        private readonly RectangleGeometryAnalyzer _geometryAnalyzer = new RectangleGeometryAnalyzer();
        private readonly TitleBlockTextAnalyzer _textAnalyzer = new TitleBlockTextAnalyzer();

        private static readonly string[] BuiltInFrameLayerNames = new string[]
        {
            "TK", "BORDER", "图框", "PUB_TITLE", "TWT_TITLE", "JWS-TK",
            "0-图框", "A-BORDER", "S-BORDER", "C-BORDER", "G-ANNO-TTLB",
            "FRAME", "TITLE", "TITLEBLOCK", "TSSD_TK", "PM_TK", "YJK_TK"
        };

        private static readonly string[] BuiltInFrameLayerKeywords = new string[]
        {
            "图框", "BORDER", "PUB_TITLE", "TWT_TITLE", "TITLE", "TK", "FRAME"
        };

        private static readonly HashSet<string> IgnoredTitleTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "图名", "图 名", "图纸名称", "图号", "图 号", "比例", "比 例", "日期", "日 期",
            "设计", "设 计", "制图", "制 图", "校对", "校 对", "审核", "审 核", "审定", "审 定",
            "专业", "专 业", "阶段", "阶 段", "工程名称", "项目名称", "建设单位", "设计单位",
            "注册章", "出图章", "会签", "建筑", "结构", "给排水", "电气", "暖通", "施工图",
            "DWG NO", "DWG NO.", "DRAWING NO", "DRAWING NO.", "DWG NAME", "DRAWING NAME", "TITLE", "SCALE", "BL",
            "DRAWING TITLE", "(DRAWING TITLE)", "（DRAWING TITLE）",
            "(DRAWING NAME)", "（DRAWING NAME）",
            "(DRAWING NO)", "(DRAWING NO.)", "（DRAWING NO.）", "（DRAWING NO）",
            "(SCALE)", "（SCALE）", "(PROJECT NAME)", "（PROJECT NAME）"
        };

        private static readonly Regex DrawingNumberTextRegex = new Regex(
            @"^(?:结施|建施|水施|电施|暖施|通施|总施|结|建|水|电|暖|G|S|J|A|DL|SL|NL|ZS)[\-\s—_]*\d{1,3}(?:[\-\.]\d{1,2})?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex DrawingScaleTextRegex = new Regex(
            @"^1\s*[:/]\s*\d{1,4}$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Unified frame scanner that scans Model Space and (if enabled) Paper Space Layouts.
        /// </summary>
        public List<FrameCandidate> ScanAllFrames(
            Database db,
            BatchPdfSettings settings,
            Extents2D? searchRegion = null)
        {
            if (db == null) return new List<FrameCandidate>();
            if (settings == null) settings = BatchPdfSettings.CreateDefault();

            var candidates = ScanModelSpaceInternal(db, settings, searchRegion);

            // If layout scanning is enabled and user has not picked a specific manual search window in model space,
            // scan paper space layouts
            if (settings.IncludePaperSpaceLayouts && !searchRegion.HasValue)
            {
                var layoutCandidates = ScanPaperSpaceLayouts(db, settings);
                if (layoutCandidates != null && layoutCandidates.Count > 0)
                {
                    candidates.AddRange(layoutCandidates);
                }
            }

            return candidates;
        }

        /// <summary>
        /// Scans Model Space using the full BatchPdfSettings (recognition mode, active layer/block,
        /// non-plottable filter, optional search region, sort order, X/Y reverse, and titleblock text extraction).
        /// If settings.IncludePaperSpaceLayouts is true and searchRegion is null, paper space layouts are included.
        /// </summary>
        public List<FrameCandidate> ScanModelSpaceFrames(
            Database db,
            BatchPdfSettings settings,
            Extents2D? searchRegion = null)
        {
            return ScanAllFrames(db, settings, searchRegion);
        }

        /// <summary>
        /// Scans only Model Space frames without querying paper space layouts.
        /// </summary>
        public List<FrameCandidate> ScanModelSpaceOnly(
            Database db,
            BatchPdfSettings settings,
            Extents2D? searchRegion = null)
        {
            return ScanModelSpaceInternal(db, settings, searchRegion);
        }

        private List<FrameCandidate> ScanModelSpaceInternal(
            Database db,
            BatchPdfSettings settings,
            Extents2D? searchRegion = null)
        {
            if (db == null) return new List<FrameCandidate>();
            if (settings == null) settings = BatchPdfSettings.CreateDefault();

            var rules = settings.FrameRules ?? BatchPdfSettings.CreateDefault().FrameRules;
            double minEdge = Math.Max(GetMinEdgeLength(rules), 200.0);
            bool includeNonPlottable = settings.IncludeNonPlottableLayers;
            Extents2D? activeSearchRegion = (searchRegion.HasValue && searchRegion.Value.Width > 1.0 && searchRegion.Value.Height > 1.0)
                ? searchRegion
                : null;

            var rawCandidates = new List<FrameCandidate>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                var layerStates = ReadLayerStates(tr, db);

                if (settings.RecognitionMode == FrameRecognitionMode.ByBlock)
                {
                    HashSet<string> targetBlocks = ParseCsvList(settings.ActiveFrameBlockName);
                    rawCandidates.AddRange(ScanBlocksInternal(
                        tr, modelSpace, layerStates, rules, targetBlocks,
                        allowedLayers: null, requireExplicitMatch: true,
                        includeNonPlottable: includeNonPlottable, minEdge: minEdge,
                        searchRegion: activeSearchRegion));
                }
                else
                {
                    // Default / ByLayer mode:
                    // 1. Try user's specified ActiveFrameLayer(s) first
                    HashSet<string> userLayers = ParseCsvList(settings.ActiveFrameLayer);
                    HashSet<string> presentUserLayers = FilterPresentFrameLayers(userLayers, layerStates);
                    bool isSingleSpecificUserLayer = IsSingleSpecificUserLayer(settings.ActiveFrameLayer, userLayers, presentUserLayers);

                    if (presentUserLayers.Count > 0)
                    {
                        rawCandidates.AddRange(ScanLayersForFrames(
                            tr, modelSpace, layerStates, rules, presentUserLayers,
                            includeNonPlottable, minEdge, activeSearchRegion));
                    }

                    // 2. If user's specified layer is not in this DWG or yielded 0 frames,
                    // automatically fall back to built-in frame layers present in this DWG (e.g. BORDER, TK, PUB_TITLE, 图框)
                    // unless the user explicitly picked a single specific layer that exists in this DWG.
                    if (rawCandidates.Count == 0 && !isSingleSpecificUserLayer)
                    {
                        HashSet<string> autoFrameLayers = DetectFrameLayersInDrawing(layerStates, rules);
                        if (autoFrameLayers.Count > 0)
                        {
                            rawCandidates.AddRange(ScanLayersForFrames(
                                tr, modelSpace, layerStates, rules, autoFrameLayers,
                                includeNonPlottable, minEdge, activeSearchRegion));
                        }
                    }

                    // 3. Also check if there are explicit titleblock BlockReferences matching FrameRules (e.g. *TK*, *BORDER*, *TITLE*)
                    // on any visible layer if no polyline/block frames were found on frame layers
                    if (rawCandidates.Count == 0 && !isSingleSpecificUserLayer)
                    {
                        rawCandidates.AddRange(ScanBlocksInternal(
                            tr, modelSpace, layerStates, rules, targetBlockNames: null,
                            allowedLayers: null, requireExplicitMatch: true,
                            includeNonPlottable: includeNonPlottable, minEdge: minEdge,
                            searchRegion: activeSearchRegion));
                    }
                }

                // Filter by search region if user selected a search area
                if (activeSearchRegion.HasValue)
                {
                    Extents2D region = activeSearchRegion.Value;
                    rawCandidates = rawCandidates
                        .Where(c => IsWithinSearchRegion(c.WcsBoundingBox, region))
                        .ToList();
                }

                // Filter out stray tiny boxes that are < 5% of the largest frame area in the drawing
                if (rawCandidates.Count > 1)
                {
                    double maxArea = rawCandidates.Max(c => c.WidthDrawingUnits * c.HeightDrawingUnits);
                    double minPlausibleSheetArea = maxArea * 0.05;
                    rawCandidates = rawCandidates
                        .Where(c => (c.WidthDrawingUnits * c.HeightDrawingUnits) >= minPlausibleSheetArea)
                        .ToList();
                }

                // Deduplicate nested borders/blocks and sort according to settings
                var ranked = _geometryAnalyzer.DeduplicateAndRank(
                    rawCandidates,
                    settings.SortOrderMode,
                    settings.ReverseX,
                    settings.ReverseY);

                // Enrich accepted outer frames with DBText/MText/BlockAttributes inside their titleblock zones
                if (ranked.Count > 0)
                {
                    EnrichFramesWithTitleblockTexts(tr, modelSpace, layerStates, ranked, rules);
                }

                foreach (var c in ranked)
                {
                    c.Space = "模型空间";
                    c.LayoutName = "Model";
                }

                tr.Commit();
                return ranked;
            }
        }

        /// <summary>
        /// Scans all paper space layouts in the database (excluding "Model"), detecting explicit frames
        /// or synthesizing sheet candidates from layout plot extents and paper settings.
        /// </summary>
        public List<FrameCandidate> ScanPaperSpaceLayouts(
            Database db,
            BatchPdfSettings settings)
        {
            if (db == null) return new List<FrameCandidate>();
            if (settings == null) settings = BatchPdfSettings.CreateDefault();

            var rules = settings.FrameRules ?? BatchPdfSettings.CreateDefault().FrameRules;
            double minEdge = 150.0; // In PaperSpace (1:1 mm), A4 is 210x297, A3 is 420x297
            bool includeNonPlottable = settings.IncludeNonPlottableLayers;
            var layoutCandidates = new List<Tuple<int, FrameCandidate>>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var layerStates = ReadLayerStates(tr, db);
                DBDictionary layoutDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);

                foreach (DBDictionaryEntry entry in layoutDict)
                {
                    string layoutName = entry.Key;
                    if (string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ObjectId layoutId = entry.Value;
                    Layout layout = tr.GetObject(layoutId, OpenMode.ForRead) as Layout;
                    if (layout == null) continue;

                    ObjectId btrId = layout.BlockTableRecordId;
                    if (btrId.IsNull || btrId.IsErased) continue;

                    BlockTableRecord layoutBtr = tr.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord;
                    if (layoutBtr == null) continue;

                    // Check if layout is completely empty (no visible plottable entities besides default paper viewport)
                    int entityCount = 0;
                    foreach (ObjectId childId in layoutBtr)
                    {
                        if (childId.IsErased) continue;
                        Entity child = tr.GetObject(childId, OpenMode.ForRead) as Entity;
                        if (child == null || !child.Visible) continue;
                        if (child is Viewport vp && vp.Number == 1) continue;
                        entityCount++;
                    }

                    if (entityCount == 0)
                    {
                        continue;
                    }

                    var rawInLayout = new List<FrameCandidate>();

                    // 1. Scan for explicit frame polylines, lines, or blocks in this layout's BlockTableRecord
                    if (settings.RecognitionMode == FrameRecognitionMode.ByBlock)
                    {
                        HashSet<string> targetBlocks = ParseCsvList(settings.ActiveFrameBlockName);
                        rawInLayout.AddRange(ScanBlocksInternal(
                            tr, layoutBtr, layerStates, rules, targetBlocks,
                            allowedLayers: null, requireExplicitMatch: true,
                            includeNonPlottable: includeNonPlottable, minEdge: minEdge,
                            searchRegion: null));
                    }
                    else
                    {
                        HashSet<string> userLayers = ParseCsvList(settings.ActiveFrameLayer);
                        HashSet<string> presentUserLayers = FilterPresentFrameLayers(userLayers, layerStates);
                        bool isSingleSpecificUserLayer = IsSingleSpecificUserLayer(settings.ActiveFrameLayer, userLayers, presentUserLayers);

                        if (presentUserLayers.Count > 0)
                        {
                            rawInLayout.AddRange(ScanLayersForFrames(
                                tr, layoutBtr, layerStates, rules, presentUserLayers,
                                includeNonPlottable, minEdge, null));
                        }

                        if (rawInLayout.Count == 0 && !isSingleSpecificUserLayer)
                        {
                            HashSet<string> autoFrameLayers = DetectFrameLayersInDrawing(layerStates, rules);
                            if (autoFrameLayers.Count > 0)
                            {
                                rawInLayout.AddRange(ScanLayersForFrames(
                                    tr, layoutBtr, layerStates, rules, autoFrameLayers,
                                    includeNonPlottable, minEdge, null));
                            }
                        }

                        if (rawInLayout.Count == 0 && !isSingleSpecificUserLayer)
                        {
                            rawInLayout.AddRange(ScanBlocksInternal(
                                tr, layoutBtr, layerStates, rules, targetBlockNames: null,
                                allowedLayers: null, requireExplicitMatch: true,
                                includeNonPlottable: includeNonPlottable, minEdge: minEdge,
                                searchRegion: null));
                        }
                    }

                    var rankedInLayout = _geometryAnalyzer.DeduplicateAndRank(
                        rawInLayout,
                        settings.SortOrderMode,
                        settings.ReverseX,
                        settings.ReverseY);

                    if (rankedInLayout.Count > 0)
                    {
                        EnrichFramesWithTitleblockTexts(tr, layoutBtr, layerStates, rankedInLayout, rules);
                        foreach (var cand in rankedInLayout)
                        {
                            cand.Space = "布局: " + layout.LayoutName;
                            cand.LayoutName = layout.LayoutName;
                            if (!cand.ExtractedScaleValue.HasValue)
                            {
                                cand.ExtractedScaleValue = 1.0;
                                cand.ExtractedScaleText = "1:1";
                            }
                            layoutCandidates.Add(Tuple.Create(layout.TabOrder, cand));
                        }
                    }
                    else
                    {
                        // 2. If no explicit frame polyline/block found, synthesize a candidate from layout's PaperSize or Extents
                        var synthCand = TryCreateCandidateFromLayout(tr, layout, layoutBtr, layerStates, rules);
                        if (synthCand != null)
                        {
                            layoutCandidates.Add(Tuple.Create(layout.TabOrder, synthCand));
                        }
                    }
                }

                tr.Commit();
            }

            return layoutCandidates
                .OrderBy(t => t.Item1)
                .Select(t => t.Item2)
                .ToList();
        }

        private FrameCandidate TryCreateCandidateFromLayout(
            Transaction tr,
            Layout layout,
            BlockTableRecord layoutBtr,
            Dictionary<string, LayerStateInfo> layerStates,
            IList<FrameRule> rules)
        {
            if (layout == null) return null;

            double width = layout.PlotPaperSize.X;
            double height = layout.PlotPaperSize.Y;

            if (layout.PlotPaperUnits == PlotPaperUnit.Inches)
            {
                width *= 25.4;
                height *= 25.4;
            }

            if (width < 100.0 || height < 100.0)
            {
                try
                {
                    Extents3d ext = layout.Extents;
                    double extW = Math.Abs(ext.MaxPoint.X - ext.MinPoint.X);
                    double extH = Math.Abs(ext.MaxPoint.Y - ext.MinPoint.Y);
                    if (extW >= 100.0 && extH >= 100.0)
                    {
                        width = extW;
                        height = extH;
                    }
                }
                catch { }
            }

            if (width < 100.0 || height < 100.0)
            {
                width = 420.0;
                height = 297.0;
            }

            Point2D[] corners = new Point2D[]
            {
                new Point2D(0, 0),
                new Point2D(width, 0),
                new Point2D(width, height),
                new Point2D(0, height)
            };

            var cand = new FrameCandidate
            {
                FrameHandle = layout.Handle.ToString(),
                FrameType = FrameType.Polyline,
                LayerName = "0",
                BlockName = "",
                Space = "布局: " + layout.LayoutName,
                LayoutName = layout.LayoutName,
                Corners = corners,
                WcsBoundingBox = Extents2D.FromPoints(corners),
                WidthDrawingUnits = width,
                HeightDrawingUnits = height,
                RotationAngleRad = 0.0,
                ExtractedScaleValue = 1.0,
                ExtractedScaleText = "1:1",
                Confidence = 0.88
            };

            var frames = new List<FrameCandidate> { cand };
            EnrichFramesWithTitleblockTexts(tr, layoutBtr, layerStates, frames, rules);

            if (string.IsNullOrWhiteSpace(cand.ExtractedDrawingName))
            {
                cand.ExtractedDrawingName = layout.LayoutName;
            }

            return cand;
        }

        /// <summary>
        /// Backwards-compatible overload for scanning Model Space with a list of FrameRules.
        /// </summary>
        public List<FrameCandidate> ScanModelSpaceFrames(Database db, IList<FrameRule> rules)
        {
            var settings = BatchPdfSettings.CreateDefault();
            if (rules != null && rules.Count > 0)
            {
                settings.FrameRules = rules.ToList();
            }
            return ScanModelSpaceFrames(db, settings, null);
        }

        /// <summary>
        /// Extracts FrameCandidates from a set of manually selected ObjectIds in Model Space.
        /// </summary>
        public List<FrameCandidate> ExtractCandidatesFromObjectIds(
            Database db,
            IEnumerable<ObjectId> objectIds,
            BatchPdfSettings settings)
        {
            var rawCandidates = new List<FrameCandidate>();
            if (db == null || objectIds == null) return rawCandidates;
            if (settings == null) settings = BatchPdfSettings.CreateDefault();

            var rules = settings.FrameRules ?? BatchPdfSettings.CreateDefault().FrameRules;
            double minEdge = Math.Max(GetMinEdgeLength(rules), 150.0);
            var lineSegments = new List<Tuple<Point2D, Point2D, string, string>>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                var layerStates = ReadLayerStates(tr, db);

                foreach (ObjectId objId in objectIds)
                {
                    if (objId.IsNull || objId.IsErased) continue;
                    Entity ent = tr.GetObject(objId, OpenMode.ForRead) as Entity;
                    if (ent == null || !ent.Visible) continue;

                    if (ent is Polyline pline)
                    {
                        var cand = TryExtractPolylineCandidate(pline, minEdge);
                        if (cand != null) rawCandidates.Add(cand);
                    }
                    else if (ent is Polyline2d pline2d)
                    {
                        var cand = TryExtractPolyline2dCandidate(tr, pline2d, minEdge);
                        if (cand != null) rawCandidates.Add(cand);
                    }
                    else if (ent is BlockReference br)
                    {
                        var cand = TryExtractBlockCandidate(tr, br, rules, minEdge, requireRuleOrNameMatch: false, targetBlockNames: null);
                        if (cand != null) rawCandidates.Add(cand);
                    }
                    else if (ent is Line line)
                    {
                        lineSegments.Add(Tuple.Create(
                            new Point2D(line.StartPoint.X, line.StartPoint.Y),
                            new Point2D(line.EndPoint.X, line.EndPoint.Y),
                            line.Layer ?? "",
                            line.Handle.ToString()));
                    }
                }

                if (lineSegments.Count >= 4)
                {
                    rawCandidates.AddRange(_geometryAnalyzer.ReconstructRectanglesFromLines(lineSegments, minEdge));
                }

                var ranked = _geometryAnalyzer.DeduplicateAndRank(
                    rawCandidates,
                    settings.SortOrderMode,
                    settings.ReverseX,
                    settings.ReverseY);

                if (ranked.Count > 0)
                {
                    EnrichFramesWithTitleblockTexts(tr, modelSpace, layerStates, ranked, rules);
                }

                tr.Commit();
                return ranked;
            }
        }

        public static List<string> GetAllLayerNames(Database db, bool includeNonPlottable = true)
        {
            var list = new List<string>();
            if (db == null) return list;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId lid in lt)
                {
                    if (lid.IsErased) continue;
                    LayerTableRecord ltr = tr.GetObject(lid, OpenMode.ForRead) as LayerTableRecord;
                    if (ltr == null || ltr.IsOff || ltr.IsFrozen) continue;
                    if (!includeNonPlottable && !ltr.IsPlottable) continue;
                    if (!string.IsNullOrWhiteSpace(ltr.Name))
                    {
                        list.Add(ltr.Name);
                    }
                }
                tr.Commit();
            }

            return list.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<string> GetModelSpaceBlockNames(Database db)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (db == null) return new List<string>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId objId in modelSpace)
                {
                    if (objId.IsErased) continue;
                    BlockReference br = tr.GetObject(objId, OpenMode.ForRead) as BlockReference;
                    if (br == null || !br.Visible) continue;
                    string name = GetEffectiveBlockName(tr, br);
                    if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith("*", StringComparison.OrdinalIgnoreCase))
                    {
                        set.Add(name);
                    }
                }
                tr.Commit();
            }

            return set.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static DrawingUnits ReadDatabaseDrawingUnits(Database db)
        {
            if (db == null) return DrawingUnits.Millimeters;

            switch (db.Insunits)
            {
                case UnitsValue.Millimeters:
                    return DrawingUnits.Millimeters;
                case UnitsValue.Centimeters:
                    return DrawingUnits.Centimeters;
                case UnitsValue.Meters:
                    return DrawingUnits.Meters;
                case UnitsValue.Inches:
                    return DrawingUnits.Inches;
                case UnitsValue.Undefined:
                default:
                    return DrawingUnits.Unitless;
            }
        }

        public static bool IsWithinSearchRegion(Extents2D box, Extents2D region)
        {
            if (region.Contains(box))
            {
                return true;
            }

            Point2D center = box.Center;
            if (center.X >= region.MinX && center.X <= region.MaxX &&
                center.Y >= region.MinY && center.Y <= region.MaxY)
            {
                return true;
            }

            return region.IntersectionArea(box) >= Math.Max(1.0, box.Area) * 0.35;
        }

        private static bool IsEntityInSearchRegion(Extents2D ext, Extents2D region)
        {
            return IsWithinSearchRegion(ext, region);
        }

        private static bool TryGetEntityExtents2D(Entity ent, out Extents2D ext2d)
        {
            ext2d = default(Extents2D);
            if (ent == null) return false;
            try
            {
                Extents3d ext = ent.GeometricExtents;
                ext2d = new Extents2D(ext.MinPoint.X, ext.MinPoint.Y, ext.MaxPoint.X, ext.MaxPoint.Y);
                return ext2d.Width > 0.0 || ext2d.Height > 0.0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLineInSearchRegion(Point2D p1, Point2D p2, Extents2D region)
        {
            Extents2D lineExt = new Extents2D(p1.X, p1.Y, p2.X, p2.Y);
            return region.Contains(lineExt) || region.Intersects(lineExt, 1.0);
        }

        private static bool IsSingleSpecificUserLayer(
            string rawActiveFrameLayer,
            HashSet<string> userLayers,
            HashSet<string> presentUserLayers)
        {
            if (string.IsNullOrWhiteSpace(rawActiveFrameLayer)) return false;
            if (userLayers == null || presentUserLayers == null) return false;
            return userLayers.Count == 1 && presentUserLayers.Count == 1;
        }

        private static bool BlockDefinitionContainsCurvesOnLayers(
            Transaction tr,
            BlockReference br,
            HashSet<string> allowedLayers,
            Dictionary<string, LayerStateInfo> layerStates,
            bool includeNonPlottable,
            Dictionary<ObjectId, bool> btrCache)
        {
            if (br == null || br.BlockTableRecord.IsNull || allowedLayers == null || allowedLayers.Count == 0)
            {
                return false;
            }

            if (btrCache != null && btrCache.TryGetValue(br.BlockTableRecord, out bool cached))
            {
                return cached;
            }

            bool found = false;
            BlockTableRecord btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
            if (btr != null)
            {
                foreach (ObjectId childId in btr)
                {
                    if (childId.IsErased) continue;
                    Entity child = tr.GetObject(childId, OpenMode.ForRead) as Entity;
                    if (child == null || !child.Visible) continue;

                    if (child is Curve)
                    {
                        string childLayer = child.Layer ?? "";
                        if (allowedLayers.Contains(childLayer) &&
                            IsLayerEligible(childLayer, layerStates, includeNonPlottable))
                        {
                            found = true;
                            break;
                        }
                    }
                    else if (child is BlockReference nestedBr)
                    {
                        string nestedLayer = nestedBr.Layer ?? "";
                        if (allowedLayers.Contains(nestedLayer) &&
                            IsLayerEligible(nestedLayer, layerStates, includeNonPlottable))
                        {
                            found = true;
                            break;
                        }
                    }
                }
            }

            if (btrCache != null)
            {
                btrCache[br.BlockTableRecord] = found;
            }
            return found;
        }

        private List<FrameCandidate> ScanLayersForFrames(
            Transaction tr,
            BlockTableRecord modelSpace,
            Dictionary<string, LayerStateInfo> layerStates,
            IList<FrameRule> rules,
            HashSet<string> targetLayers,
            bool includeNonPlottable,
            double minEdge,
            Extents2D? searchRegion = null)
        {
            var candidates = new List<FrameCandidate>();
            var lineSegments = new List<Tuple<Point2D, Point2D, string, string>>();
            var btrCurveLayerCache = new Dictionary<ObjectId, bool>();

            foreach (ObjectId objId in modelSpace)
            {
                if (objId.IsErased) continue;
                Entity ent = tr.GetObject(objId, OpenMode.ForRead) as Entity;
                if (ent == null || !ent.Visible) continue;

                string layer = ent.Layer ?? "";
                if (!IsLayerEligible(layer, layerStates, includeNonPlottable)) continue;

                bool onTargetLayer = targetLayers.Contains(layer);
                if (!onTargetLayer)
                {
                    if (ent is BlockReference brCheck &&
                        BlockDefinitionContainsCurvesOnLayers(
                            tr, brCheck, targetLayers, layerStates, includeNonPlottable, btrCurveLayerCache))
                    {
                        onTargetLayer = true;
                    }
                    else
                    {
                        continue;
                    }
                }

                if (ent is Polyline pline)
                {
                    if (searchRegion.HasValue &&
                        TryGetEntityExtents2D(pline, out Extents2D ext) &&
                        !IsEntityInSearchRegion(ext, searchRegion.Value))
                    {
                        continue;
                    }

                    var cand = TryExtractPolylineCandidate(pline, minEdge);
                    if (cand != null && (!searchRegion.HasValue || IsEntityInSearchRegion(cand.WcsBoundingBox, searchRegion.Value)))
                    {
                        candidates.Add(cand);
                    }
                }
                else if (ent is Polyline2d pline2d)
                {
                    if (searchRegion.HasValue &&
                        TryGetEntityExtents2D(pline2d, out Extents2D ext) &&
                        !IsEntityInSearchRegion(ext, searchRegion.Value))
                    {
                        continue;
                    }

                    var cand = TryExtractPolyline2dCandidate(tr, pline2d, minEdge);
                    if (cand != null && (!searchRegion.HasValue || IsEntityInSearchRegion(cand.WcsBoundingBox, searchRegion.Value)))
                    {
                        candidates.Add(cand);
                    }
                }
                else if (ent is BlockReference br)
                {
                    if (searchRegion.HasValue &&
                        TryGetEntityExtents2D(br, out Extents2D ext) &&
                        !IsEntityInSearchRegion(ext, searchRegion.Value))
                    {
                        continue;
                    }

                    // On an explicit frame layer (or with internal curves on the frame layer),
                    // accept BlockReferences that have a valid rectangular sheet border
                    var cand = TryExtractBlockCandidate(
                        tr, br, rules, minEdge,
                        requireRuleOrNameMatch: false,
                        targetBlockNames: null,
                        requireSheetLikeSizeOnLayer: true);
                    if (cand != null && (!searchRegion.HasValue || IsEntityInSearchRegion(cand.WcsBoundingBox, searchRegion.Value)))
                    {
                        candidates.Add(cand);
                    }
                }
                else if (ent is Line line)
                {
                    double len = line.StartPoint.DistanceTo(line.EndPoint);
                    if (len >= minEdge)
                    {
                        Point2D p1 = new Point2D(line.StartPoint.X, line.StartPoint.Y);
                        Point2D p2 = new Point2D(line.EndPoint.X, line.EndPoint.Y);
                        if (!searchRegion.HasValue || IsLineInSearchRegion(p1, p2, searchRegion.Value))
                        {
                            lineSegments.Add(Tuple.Create(
                                p1,
                                p2,
                                layer,
                                line.Handle.ToString()));
                        }
                    }
                }
            }

            if (lineSegments.Count >= 4)
            {
                var lineRects = _geometryAnalyzer.ReconstructRectanglesFromLines(lineSegments, minEdge);
                if (searchRegion.HasValue)
                {
                    lineRects = lineRects
                        .Where(c => IsEntityInSearchRegion(c.WcsBoundingBox, searchRegion.Value))
                        .ToList();
                }
                candidates.AddRange(lineRects);
            }

            return candidates;
        }

        private List<FrameCandidate> ScanBlocksInternal(
            Transaction tr,
            BlockTableRecord modelSpace,
            Dictionary<string, LayerStateInfo> layerStates,
            IList<FrameRule> rules,
            HashSet<string> targetBlockNames,
            HashSet<string> allowedLayers,
            bool requireExplicitMatch,
            bool includeNonPlottable,
            double minEdge,
            Extents2D? searchRegion = null)
        {
            var results = new List<FrameCandidate>();
            var btrCurveLayerCache = new Dictionary<ObjectId, bool>();

            foreach (ObjectId objId in modelSpace)
            {
                if (objId.IsErased) continue;
                BlockReference br = tr.GetObject(objId, OpenMode.ForRead) as BlockReference;
                if (br == null || !br.Visible) continue;

                string layer = br.Layer ?? "";
                if (!IsLayerEligible(layer, layerStates, includeNonPlottable)) continue;
                if (allowedLayers != null && allowedLayers.Count > 0 &&
                    !allowedLayers.Contains(layer) &&
                    !BlockDefinitionContainsCurvesOnLayers(
                        tr, br, allowedLayers, layerStates, includeNonPlottable, btrCurveLayerCache))
                {
                    continue;
                }

                if (searchRegion.HasValue &&
                    TryGetEntityExtents2D(br, out Extents2D ext) &&
                    !IsEntityInSearchRegion(ext, searchRegion.Value))
                {
                    continue;
                }

                var cand = TryExtractBlockCandidate(
                    tr, br, rules, minEdge,
                    requireRuleOrNameMatch: requireExplicitMatch,
                    targetBlockNames: targetBlockNames,
                    requireSheetLikeSizeOnLayer: false);
                if (cand != null && (!searchRegion.HasValue || IsEntityInSearchRegion(cand.WcsBoundingBox, searchRegion.Value)))
                {
                    results.Add(cand);
                }
            }

            return results;
        }

        private FrameCandidate TryExtractPolylineCandidate(Polyline pline, double minEdge)
        {
            if (pline.NumberOfVertices < 4) return null;

            bool isClosed = pline.Closed;
            if (!isClosed)
            {
                Point2d first = pline.GetPoint2dAt(0);
                Point2d last = pline.GetPoint2dAt(pline.NumberOfVertices - 1);
                if (first.GetDistanceTo(last) <= 1.0)
                {
                    isClosed = true;
                }
            }

            if (!isClosed) return null;

            var vertices = new List<Point2D>(pline.NumberOfVertices);
            var bulges = new List<double>(pline.NumberOfVertices);

            for (int i = 0; i < pline.NumberOfVertices; i++)
            {
                Point3d wcsPt = pline.GetPoint3dAt(i);
                vertices.Add(new Point2D(wcsPt.X, wcsPt.Y));
                bulges.Add(pline.GetBulgeAt(i));
            }

            if (!_geometryAnalyzer.TryAnalyzeClosedPolyline(
                    vertices,
                    bulges,
                    minEdge,
                    out Point2D[] orderedCorners,
                    out double width,
                    out double height,
                    out double rotationRad,
                    out List<string> warnings))
            {
                return null;
            }

            return new FrameCandidate
            {
                FrameHandle = pline.Handle.ToString(),
                FrameType = FrameType.Polyline,
                LayerName = pline.Layer,
                BlockName = "",
                Corners = orderedCorners,
                WcsBoundingBox = Extents2D.FromPoints(orderedCorners),
                WidthDrawingUnits = width,
                HeightDrawingUnits = height,
                RotationAngleRad = rotationRad,
                Confidence = warnings.Count == 0 ? 0.92 : 0.78,
                NeedsManualCheck = warnings.Count > 0,
                Warnings = warnings
            };
        }

        private FrameCandidate TryExtractPolyline2dCandidate(Transaction tr, Polyline2d pline2d, double minEdge)
        {
            var vertices = new List<Point2D>();
            var bulges = new List<double>();

            foreach (ObjectId vId in pline2d)
            {
                Vertex2d v = tr.GetObject(vId, OpenMode.ForRead) as Vertex2d;
                if (v != null && v.VertexType != Vertex2dType.SplineControlVertex)
                {
                    Point3d wcsPt = pline2d.VertexPosition(v);
                    vertices.Add(new Point2D(wcsPt.X, wcsPt.Y));
                    bulges.Add(v.Bulge);
                }
            }

            if (vertices.Count < 4) return null;
            bool isClosed = pline2d.Closed || vertices[0].DistanceTo(vertices[vertices.Count - 1]) <= 1.0;
            if (!isClosed) return null;

            if (!_geometryAnalyzer.TryAnalyzeClosedPolyline(
                    vertices,
                    bulges,
                    minEdge,
                    out Point2D[] orderedCorners,
                    out double width,
                    out double height,
                    out double rotationRad,
                    out List<string> warnings))
            {
                return null;
            }

            return new FrameCandidate
            {
                FrameHandle = pline2d.Handle.ToString(),
                FrameType = FrameType.Polyline,
                LayerName = pline2d.Layer,
                BlockName = "",
                Corners = orderedCorners,
                WcsBoundingBox = Extents2D.FromPoints(orderedCorners),
                WidthDrawingUnits = width,
                HeightDrawingUnits = height,
                RotationAngleRad = rotationRad,
                Confidence = warnings.Count == 0 ? 0.90 : 0.75,
                NeedsManualCheck = warnings.Count > 0,
                Warnings = warnings
            };
        }

        private FrameCandidate TryExtractBlockCandidate(
            Transaction tr,
            BlockReference br,
            IList<FrameRule> rules,
            double minEdge,
            bool requireRuleOrNameMatch,
            HashSet<string> targetBlockNames,
            bool requireSheetLikeSizeOnLayer = false)
        {
            string effectiveName = GetEffectiveBlockName(tr, br);
            string rawName = br.Name ?? "";

            // Never treat anonymous AutoCAD paste blocks (A$C...) as full sheet frames unless explicitly selected by handle
            if (effectiveName.StartsWith("A$C", StringComparison.OrdinalIgnoreCase) &&
                (targetBlockNames == null || !targetBlockNames.Contains(effectiveName)))
            {
                return null;
            }

            var attributes = ReadBlockAttributes(tr, br);

            ExtractMappedMetadata(
                attributes,
                rules,
                out string drawingNo,
                out string drawingName,
                out string scaleText,
                out double? scaleVal);

            bool matchesTargetBlock = false;
            if (targetBlockNames != null && targetBlockNames.Count > 0)
            {
                matchesTargetBlock = targetBlockNames.Any(pattern =>
                    WildcardMatch(effectiveName, pattern) || WildcardMatch(rawName, pattern));
                if (!matchesTargetBlock)
                {
                    return null;
                }
            }

            bool matchesBlockNameRule = MatchesAnyBlockNameRule(effectiveName, rawName, rules);
            bool hasMappedAttributes = !string.IsNullOrWhiteSpace(drawingNo) ||
                                       !string.IsNullOrWhiteSpace(drawingName) ||
                                       !string.IsNullOrWhiteSpace(scaleText);

            if (requireRuleOrNameMatch && !matchesTargetBlock && !matchesBlockNameRule && !hasMappedAttributes)
            {
                return null;
            }

            if (!TryComputeBlockBoundaryInWcs(
                    tr,
                    br,
                    minEdge,
                    out Point2D[] orderedCorners,
                    out double width,
                    out double height,
                    out double rotationRad,
                    out List<string> geomWarnings))
            {
                return null;
            }

            if (!RectangleGeometryAnalyzer.IsValidSheetAspectRatio(width, height))
            {
                return null;
            }

            if (requireSheetLikeSizeOnLayer && !matchesBlockNameRule && !hasMappedAttributes)
            {
                // On a frame layer, if a block is a small titlebar (e.g. 7220x40000 or 18000x5000),
                // its aspect ratio or standard size check will reject it so only true full-sheet blocks pass.
                if (!ScaleParser.TryInferScaleFromAvailableMedia(
                        width, height, DrawingUnits.Millimeters, null, 10.0, out _, out _))
                {
                    return null;
                }
            }

            double sx = Math.Abs(br.ScaleFactors.X);
            double sy = Math.Abs(br.ScaleFactors.Y);
            if (sx > 1e-6 && sy > 1e-6 && Math.Abs(sx - sy) / Math.Max(sx, sy) > 0.01)
            {
                geomWarnings.Add(string.Format("块参照 X/Y 缩放比例不一致 ({0:F2}, {1:F2})，需检查", sx, sy));
            }

            bool needsCheck = geomWarnings.Count > 0;
            double confidence = (matchesTargetBlock || (matchesBlockNameRule && hasMappedAttributes)) ? 0.98
                : (matchesBlockNameRule || hasMappedAttributes) ? 0.92
                : 0.85;

            return new FrameCandidate
            {
                FrameHandle = br.Handle.ToString(),
                FrameType = FrameType.BlockReference,
                LayerName = br.Layer,
                BlockName = effectiveName,
                Corners = orderedCorners,
                WcsBoundingBox = Extents2D.FromPoints(orderedCorners),
                WidthDrawingUnits = width,
                HeightDrawingUnits = height,
                RotationAngleRad = rotationRad,
                Attributes = attributes,
                ExtractedDrawingNumber = drawingNo,
                ExtractedDrawingName = drawingName,
                ExtractedScaleText = scaleText,
                ExtractedScaleValue = scaleVal,
                Confidence = confidence,
                NeedsManualCheck = needsCheck,
                Warnings = geomWarnings
            };
        }

        /// <summary>
        /// Explicit entry point for extracting title text metadata from text entities in a space record.
        /// </summary>
        public void ExtractModelSpaceTitleTextMetadata(
            Transaction tr,
            BlockTableRecord spaceRecord,
            IList<FrameCandidate> frames,
            IList<FrameRule> rules)
        {
            if (spaceRecord == null || frames == null || frames.Count == 0) return;
            var layerStates = ReadLayerStates(tr, spaceRecord.Database);
            EnrichFramesWithTitleblockTexts(tr, spaceRecord, layerStates, frames, rules);
        }

        public void ExtractModelSpaceTitleTextMetadata(
            Transaction tr,
            BlockTableRecord spaceRecord,
            Dictionary<string, LayerStateInfo> layerStates,
            IList<FrameCandidate> frames,
            IList<FrameRule> rules)
        {
            EnrichFramesWithTitleblockTexts(tr, spaceRecord, layerStates, frames, rules);
        }

        /// <summary>
        /// Scans DBText, MText, and inner titlebar BlockReferences located inside each accepted outer frame
        /// to automatically populate DrawingName, DrawingNumber, and Scale using spatial proximity
        /// and geometric heuristics.
        /// </summary>
        private void EnrichFramesWithTitleblockTexts(
            Transaction tr,
            BlockTableRecord spaceRecord,
            Dictionary<string, LayerStateInfo> layerStates,
            IList<FrameCandidate> frames,
            IList<FrameRule> rules)
        {
            if (frames == null || frames.Count == 0 || spaceRecord == null) return;

            var textItems = new List<TitleTextItem>();

            foreach (ObjectId objId in spaceRecord)
            {
                if (objId.IsErased) continue;
                Entity ent = tr.GetObject(objId, OpenMode.ForRead) as Entity;
                if (ent == null || !ent.Visible) continue;

                string layer = ent.Layer ?? "";
                if (layerStates.TryGetValue(layer, out LayerStateInfo lsi) && (lsi.IsOff || lsi.IsFrozen))
                {
                    continue;
                }

                if (ent is DBText dbText)
                {
                    string txt = (dbText.TextString ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(txt))
                    {
                        Point3d pos = dbText.Position;
                        try
                        {
                            Extents3d ext = dbText.GeometricExtents;
                            pos = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) * 0.5, (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5, 0);
                        }
                        catch { }

                        textItems.Add(new TitleTextItem
                        {
                            Position = new Point2D(pos.X, pos.Y),
                            Text = TitleBlockTextAnalyzer.CleanCadFormatting(txt),
                            Height = dbText.Height,
                            Layer = layer
                        });
                    }
                }
                else if (ent is MText mText)
                {
                    string txt = (mText.Text ?? mText.Contents ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(txt))
                    {
                        Point3d pos = mText.Location;
                        try
                        {
                            Extents3d ext = mText.GeometricExtents;
                            pos = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) * 0.5, (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5, 0);
                        }
                        catch { }

                        foreach (string line in txt.Split(new[] { "\r\n", "\n", "\\P" }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string cleaned = TitleBlockTextAnalyzer.CleanCadFormatting(line);
                            if (!string.IsNullOrWhiteSpace(cleaned))
                            {
                                textItems.Add(new TitleTextItem
                                {
                                    Position = new Point2D(pos.X, pos.Y),
                                    Text = cleaned,
                                    Height = mText.TextHeight,
                                    Layer = layer
                                });
                            }
                        }
                    }
                }
                else if (ent is BlockReference br)
                {
                    // Check if this is an inner titleblock BlockReference inside one of the outer frames
                    Point2D brPos = new Point2D(br.Position.X, br.Position.Y);
                    try
                    {
                        Extents3d bExt = br.GeometricExtents;
                        brPos = new Point2D((bExt.MinPoint.X + bExt.MaxPoint.X) * 0.5, (bExt.MinPoint.Y + bExt.MaxPoint.Y) * 0.5);
                    }
                    catch { }

                    var ownerFrame = frames.FirstOrDefault(f =>
                        f.WcsBoundingBox.MinX <= brPos.X && brPos.X <= f.WcsBoundingBox.MaxX &&
                        f.WcsBoundingBox.MinY <= brPos.Y && brPos.Y <= f.WcsBoundingBox.MaxY);

                    if (ownerFrame != null)
                    {
                        if (br.AttributeCollection != null && br.AttributeCollection.Count > 0)
                        {
                            var attrs = ReadBlockAttributes(tr, br);
                            ExtractMappedMetadata(attrs, rules, out string bNo, out string bName, out string bScaleTxt, out double? bScaleVal);
                            if (string.IsNullOrWhiteSpace(ownerFrame.ExtractedDrawingNumber) && !string.IsNullOrWhiteSpace(bNo))
                                ownerFrame.ExtractedDrawingNumber = bNo;
                            if (string.IsNullOrWhiteSpace(ownerFrame.ExtractedDrawingName) && !string.IsNullOrWhiteSpace(bName))
                                ownerFrame.ExtractedDrawingName = bName;
                            if (!ownerFrame.ExtractedScaleValue.HasValue && bScaleVal.HasValue)
                            {
                                ownerFrame.ExtractedScaleValue = bScaleVal;
                                ownerFrame.ExtractedScaleText = bScaleTxt;
                            }
                        }

                        // Also check if the block's effective name itself is a drawing title (e.g. "15-r-1层梁平法施工图")
                        string effName = GetEffectiveBlockName(tr, br);
                        if (!string.IsNullOrWhiteSpace(effName) && TitleBlockTextAnalyzer.IsLikelyDrawingTitleText(effName))
                        {
                            textItems.Add(new TitleTextItem
                            {
                                Position = brPos,
                                Text = effName,
                                Height = 400.0,
                                Layer = layer
                            });
                        }
                    }
                }
            }

            if (textItems.Count == 0) return;

            foreach (var frame in frames)
            {
                Extents2D box = frame.WcsBoundingBox;
                var inFrameTexts = textItems
                    .Where(t => box.MinX <= t.Position.X && t.Position.X <= box.MaxX &&
                                box.MinY <= t.Position.Y && t.Position.Y <= box.MaxY)
                    .ToList();

                _textAnalyzer.EnrichFrame(frame, inFrameTexts, rules);
            }
        }

        private static bool IsTitleLayer(string layer)
        {
            if (string.IsNullOrWhiteSpace(layer)) return false;
            return layer.IndexOf("图框", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   layer.IndexOf("TITLE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   layer.IndexOf("TK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   layer.IndexOf("BORDER", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsLikelyDrawingTitleText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            if (s.Length < 4 || s.Length > 42) return false;
            if (IgnoredTitleTexts.Contains(s)) return false;

            // Reject pure numbers, scales, dates, or standard notes sentences
            if (s.Contains("：") || s.Contains(":") || s.Contains("。") || s.Contains("；")) return false;
            if ( s.StartsWith("注") || s.StartsWith("1.") || s.StartsWith("2.") || s.StartsWith("3.")) return false;
            if (s.IndexOf("有限公司", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("设计院", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("专用章", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("未盖", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return EndsWithDrawingKeyword(s) ||
                   s.Contains("平面") ||
                   s.Contains("配筋") ||
                   s.Contains("布置") ||
                   s.Contains("详图") ||
                   s.Contains("大样") ||
                   s.Contains("系统图") ||
                   s.Contains("剖面") ||
                   s.Contains("立面") ||
                   s.Contains("说明") ||
                   s.Contains("目录");
        }

        private static bool EndsWithDrawingKeyword(string s)
        {
            return s.EndsWith("图", StringComparison.Ordinal) ||
                   s.EndsWith("表", StringComparison.Ordinal) ||
                   s.EndsWith("说明", StringComparison.Ordinal) ||
                   s.EndsWith("大样", StringComparison.Ordinal) ||
                   s.EndsWith("目录", StringComparison.Ordinal);
        }

        private static string CleanCadFormatting(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string s = Regex.Replace(raw, @"\\A\d+;|\\f[^;]+;|\\H[^;]+;|\\W[^;]+;|\\C\d+;|\\T[^;]+;|\\Q[^;]+;|\\[LlOoKk]", "");
            s = s.Replace("{", "").Replace("}", "").Replace("%%c", "Φ").Replace("%%C", "Φ").Replace("%%d", "°").Replace("%%p", "±");
            return s.Trim();
        }

        private bool TryComputeBlockBoundaryInWcs(
            Transaction tr,
            BlockReference br,
            double minEdge,
            out Point2D[] orderedCorners,
            out double width,
            out double height,
            out double rotationRad,
            out List<string> warnings)
        {
            orderedCorners = null;
            width = 0;
            height = 0;
            rotationRad = 0;
            warnings = new List<string>();

            BlockTableRecord btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null) return false;

            Matrix3d transform = br.BlockTransform;
            double bestRectArea = 0;
            Point2D[] bestWcsRectCorners = null;

            double minLocalX = double.MaxValue;
            double minLocalY = double.MaxValue;
            double maxLocalX = double.MinValue;
            double maxLocalY = double.MinValue;
            bool hasLocalExtents = false;

            foreach (ObjectId entId in btr)
            {
                if (entId.IsErased) continue;
                Entity ent = tr.GetObject(entId, OpenMode.ForRead) as Entity;
                if (ent == null || !ent.Visible || ent is AttributeDefinition) continue;

                if (ent is Polyline pl && pl.NumberOfVertices >= 4)
                {
                    var localVerts = new List<Point2D>(pl.NumberOfVertices);
                    var bulges = new List<double>(pl.NumberOfVertices);
                    for (int i = 0; i < pl.NumberOfVertices; i++)
                    {
                        Point3d wcsPt = pl.GetPoint3dAt(i).TransformBy(transform);
                        localVerts.Add(new Point2D(wcsPt.X, wcsPt.Y));
                        bulges.Add(pl.GetBulgeAt(i));
                    }

                    bool isClosed = pl.Closed || localVerts[0].DistanceTo(localVerts[localVerts.Count - 1]) <= 1.0;
                    if (isClosed &&
                        _geometryAnalyzer.TryAnalyzeClosedPolyline(
                            localVerts,
                            bulges,
                            minEdge,
                            out Point2D[] rectCorners,
                            out double rw,
                            out double rh,
                            out _,
                            out _))
                    {
                        double area = rw * rh;
                        if (area > bestRectArea)
                        {
                            bestRectArea = area;
                            bestWcsRectCorners = rectCorners;
                        }
                    }
                }

                try
                {
                    Extents3d ext = ent.GeometricExtents;
                    if (ext.MinPoint.X < minLocalX) minLocalX = ext.MinPoint.X;
                    if (ext.MinPoint.Y < minLocalY) minLocalY = ext.MinPoint.Y;
                    if (ext.MaxPoint.X > maxLocalX) maxLocalX = ext.MaxPoint.X;
                    if (ext.MaxPoint.Y > maxLocalY) maxLocalY = ext.MaxPoint.Y;
                    hasLocalExtents = true;
                }
                catch
                {
                }
            }

            Point2D[] rawWcsCorners = null;
            if (bestWcsRectCorners != null)
            {
                rawWcsCorners = bestWcsRectCorners;
            }
            else if (hasLocalExtents && (maxLocalX > minLocalX) && (maxLocalY > minLocalY))
            {
                Point3d p0 = new Point3d(minLocalX, minLocalY, 0).TransformBy(transform);
                Point3d p1 = new Point3d(maxLocalX, minLocalY, 0).TransformBy(transform);
                Point3d p2 = new Point3d(maxLocalX, maxLocalY, 0).TransformBy(transform);
                Point3d p3 = new Point3d(minLocalX, maxLocalY, 0).TransformBy(transform);

                rawWcsCorners = new Point2D[]
                {
                    new Point2D(p0.X, p0.Y),
                    new Point2D(p1.X, p1.Y),
                    new Point2D(p2.X, p2.Y),
                    new Point2D(p3.X, p3.Y)
                };
            }
            else
            {
                try
                {
                    Extents3d wcsExt = br.GeometricExtents;
                    rawWcsCorners = new Point2D[]
                    {
                        new Point2D(wcsExt.MinPoint.X, wcsExt.MinPoint.Y),
                        new Point2D(wcsExt.MaxPoint.X, wcsExt.MinPoint.Y),
                        new Point2D(wcsExt.MaxPoint.X, wcsExt.MaxPoint.Y),
                        new Point2D(wcsExt.MinPoint.X, wcsExt.MaxPoint.Y)
                    };
                }
                catch
                {
                    return false;
                }
            }

            RectangleGeometryAnalyzer.OrderCornersCanonically(
                rawWcsCorners,
                out orderedCorners,
                out width,
                out height,
                out rotationRad,
                br.Rotation);

            if (width < minEdge || height < minEdge)
            {
                return false;
            }

            return true;
        }

        private static Dictionary<string, LayerStateInfo> ReadLayerStates(Transaction tr, Database db)
        {
            var dict = new Dictionary<string, LayerStateInfo>(StringComparer.OrdinalIgnoreCase);
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId lid in lt)
            {
                if (lid.IsErased) continue;
                LayerTableRecord ltr = tr.GetObject(lid, OpenMode.ForRead) as LayerTableRecord;
                if (ltr == null || string.IsNullOrWhiteSpace(ltr.Name)) continue;
                dict[ltr.Name] = new LayerStateInfo
                {
                    Name = ltr.Name,
                    IsOff = ltr.IsOff,
                    IsFrozen = ltr.IsFrozen,
                    IsPlottable = ltr.IsPlottable
                };
            }
            return dict;
        }

        private static bool IsLayerEligible(
            string layerName,
            Dictionary<string, LayerStateInfo> layerStates,
            bool includeNonPlottable)
        {
            if (string.IsNullOrWhiteSpace(layerName)) return true;
            if (!layerStates.TryGetValue(layerName, out LayerStateInfo info)) return true;
            if (info.IsOff || info.IsFrozen) return false;
            if (!includeNonPlottable && !info.IsPlottable) return false;
            return true;
        }

        private static HashSet<string> ParseCsvList(string raw)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return set;
            foreach (string part in raw.Split(new[] { ',', ';', '，', '；', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    set.Add(trimmed);
                }
            }
            return set;
        }

        private static HashSet<string> FilterPresentFrameLayers(
            HashSet<string> requestedLayers,
            Dictionary<string, LayerStateInfo> layerStates)
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (requestedLayers == null) return present;
            foreach (string req in requestedLayers)
            {
                if (layerStates.ContainsKey(req))
                {
                    present.Add(req);
                }
            }
            return present;
        }

        private static HashSet<string> DetectFrameLayersInDrawing(
            Dictionary<string, LayerStateInfo> layerStates,
            IList<FrameRule> rules)
        {
            var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Exact match with built-in frame layer names
            foreach (string builtIn in BuiltInFrameLayerNames)
            {
                if (layerStates.ContainsKey(builtIn))
                {
                    detected.Add(builtIn);
                }
            }

            // 2. Layers configured in FrameRules
            if (rules != null)
            {
                foreach (var r in rules)
                {
                    if (r.AllowedLayers == null) continue;
                    foreach (string l in r.AllowedLayers)
                    {
                        if (!string.IsNullOrWhiteSpace(l) && layerStates.ContainsKey(l.Trim()))
                        {
                            detected.Add(l.Trim());
                        }
                    }
                }
            }

            // 3. Keyword match on layer names in drawing (e.g. "0-图框", "A-BORDER", "PUB_TITLE")
            foreach (var kvp in layerStates)
            {
                string layerName = kvp.Key;
                foreach (string kw in BuiltInFrameLayerKeywords)
                {
                    if (layerName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        detected.Add(layerName);
                        break;
                    }
                }
            }

            return detected;
        }

        public static string GetEffectiveBlockName(Transaction tr, BlockReference br)
        {
            try
            {
                if (br.IsDynamicBlock && !br.DynamicBlockTableRecord.IsNull)
                {
                    BlockTableRecord dynBtr = tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                    if (dynBtr != null && !string.IsNullOrWhiteSpace(dynBtr.Name))
                    {
                        return dynBtr.Name;
                    }
                }
            }
            catch
            {
            }
            return br.Name ?? string.Empty;
        }

        private static Dictionary<string, string> ReadBlockAttributes(Transaction tr, BlockReference br)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (br.AttributeCollection == null) return dict;

            foreach (ObjectId attId in br.AttributeCollection)
            {
                if (attId.IsErased) continue;
                AttributeReference attRef = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                if (attRef != null && !string.IsNullOrWhiteSpace(attRef.Tag))
                {
                    string tag = attRef.Tag.Trim();
                    string val = (attRef.TextString ?? string.Empty).Trim();
                    if (!dict.ContainsKey(tag) || string.IsNullOrWhiteSpace(dict[tag]))
                    {
                        dict[tag] = val;
                    }
                }
            }
            return dict;
        }

        private static void ExtractMappedMetadata(
            Dictionary<string, string> attributes,
            IList<FrameRule> rules,
            out string drawingNumber,
            out string drawingName,
            out string scaleText,
            out double? scaleValue)
        {
            drawingNumber = null;
            drawingName = null;
            scaleText = null;
            scaleValue = null;

            if (attributes == null || attributes.Count == 0) return;

            var activeRules = (rules != null && rules.Count > 0)
                ? rules
                : BatchPdfSettings.CreateDefault().FrameRules;

            foreach (var rule in activeRules)
            {
                if (string.IsNullOrWhiteSpace(drawingNumber) && rule.SheetNumberTags != null)
                {
                    drawingNumber = FindFirstMatchingTagValue(attributes, rule.SheetNumberTags);
                }
                if (string.IsNullOrWhiteSpace(drawingName) && rule.SheetNameTags != null)
                {
                    drawingName = FindFirstMatchingTagValue(attributes, rule.SheetNameTags);
                }
                if (string.IsNullOrWhiteSpace(scaleText) && rule.ScaleTags != null)
                {
                    scaleText = FindFirstMatchingTagValue(attributes, rule.ScaleTags);
                }
            }

            if (!string.IsNullOrWhiteSpace(scaleText))
            {
                scaleValue = ScaleParser.TryParseScaleValue(scaleText);
            }
        }

        private static string FindFirstMatchingTagValue(Dictionary<string, string> attributes, IList<string> candidateTags)
        {
            foreach (string tagPattern in candidateTags)
            {
                if (string.IsNullOrWhiteSpace(tagPattern)) continue;
                if (attributes.TryGetValue(tagPattern.Trim(), out string exactVal) && !string.IsNullOrWhiteSpace(exactVal))
                {
                    return exactVal.Trim();
                }

                foreach (var kvp in attributes)
                {
                    if (kvp.Key.IndexOf(tagPattern.Trim(), StringComparison.OrdinalIgnoreCase) >= 0 &&
                        !string.IsNullOrWhiteSpace(kvp.Value))
                    {
                        return kvp.Value.Trim();
                    }
                }
            }
            return null;
        }

        private static bool MatchesAnyBlockNameRule(string effectiveName, string rawName, IList<FrameRule> rules)
        {
            if (rules == null || rules.Count == 0) return false;

            foreach (var rule in rules)
            {
                if (rule.BlockNames == null) continue;
                foreach (string pattern in rule.BlockNames)
                {
                    if (string.IsNullOrWhiteSpace(pattern)) continue;
                    if (WildcardMatch(effectiveName, pattern) || WildcardMatch(rawName, pattern))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool WildcardMatch(string input, string pattern)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(pattern)) return false;
            string regexPattern = "^" + Regex.Escape(pattern.Trim())
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
        }

        private static double GetMinEdgeLength(IList<FrameRule> rules)
        {
            if (rules == null || rules.Count == 0) return 200.0;
            double minW = rules.Min(r => r.MinFrameWidth > 0 ? r.MinFrameWidth : 200.0);
            double minH = rules.Min(r => r.MinFrameHeight > 0 ? r.MinFrameHeight : 200.0);
            return Math.Min(minW, minH);
        }

        public class LayerStateInfo
        {
            public string Name { get; set; }
            public bool IsOff { get; set; }
            public bool IsFrozen { get; set; }
            public bool IsPlottable { get; set; }
        }
    }
}
