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
                HashSet<string> ruleLayers = GetRuleAllowedLayers(rules, layerStates);
                HashSet<string> targetBlocks = ParseCsvList(settings.ActiveFrameBlockName);

                if (settings.RecognitionMode == FrameRecognitionMode.ByBlock)
                {
                    // 1. Scan blocks matching ActiveFrameBlockName or FrameRules.BlockNames / mapped attributes
                    rawCandidates.AddRange(ScanBlocksInternal(
                        tr, modelSpace, layerStates, rules, targetBlocks,
                        allowedLayers: null, requireExplicitMatch: true,
                        includeNonPlottable: includeNonPlottable, minEdge: minEdge,
                        searchRegion: activeSearchRegion));

                    // 2. If ActiveFrameBlockName was stale (from another DWG) and blocked rule/attribute matches, retry with rules only
                    if (rawCandidates.Count == 0 && targetBlocks.Count > 0)
                    {
                        rawCandidates.AddRange(ScanBlocksInternal(
                            tr, modelSpace, layerStates, rules, targetBlockNames: null,
                            allowedLayers: null, requireExplicitMatch: true,
                            includeNonPlottable: includeNonPlottable, minEdge: minEdge,
                            searchRegion: activeSearchRegion));
                    }

                    // 3. Fallback: scan sheet-sized BlockReferences on rule/frame layers or all eligible layers
                    if (rawCandidates.Count == 0)
                    {
                        HashSet<string> autoFrameLayers = DetectFrameLayersInDrawing(layerStates, rules);
                        rawCandidates.AddRange(ScanBlocksInternal(
                            tr, modelSpace, layerStates, rules, targetBlockNames: null,
                            allowedLayers: autoFrameLayers.Count > 0 ? autoFrameLayers : null,
                            requireExplicitMatch: false,
                            includeNonPlottable: includeNonPlottable, minEdge: minEdge,
                            searchRegion: activeSearchRegion,
                            requireSheetLikeSizeWhenUnmatched: true));
                    }

                    if (rawCandidates.Count == 0)
                    {
                        rawCandidates.AddRange(ScanBlocksInternal(
                            tr, modelSpace, layerStates, rules, targetBlockNames: null,
                            allowedLayers: null, requireExplicitMatch: false,
                            includeNonPlottable: includeNonPlottable, minEdge: minEdge,
                            searchRegion: activeSearchRegion,
                            requireSheetLikeSizeWhenUnmatched: true));
                    }
                }
                else
                {
                    // Default / ByLayer mode:
                    // 1. Try user's specified ActiveFrameLayer(s) combined with FrameRules.AllowedLayers
                    HashSet<string> userLayers = ParseCsvList(settings.ActiveFrameLayer);
                    foreach (string rl in ruleLayers)
                    {
                        userLayers.Add(rl);
                    }
                    HashSet<string> presentUserLayers = FilterPresentFrameLayers(userLayers, layerStates);

                    if (presentUserLayers.Count > 0)
                    {
                        rawCandidates.AddRange(ScanLayersForFrames(
                            tr, modelSpace, layerStates, rules, presentUserLayers,
                            includeNonPlottable, minEdge, activeSearchRegion, targetBlocks));
                    }

                    // 2. Also include any BlockReferences that explicitly match FrameRules.BlockNames
                    // (even if placed on Layer "0" or another layer not in presentUserLayers)
                    if (HasExplicitBlockNameRules(rules))
                    {
                        rawCandidates.AddRange(ScanBlocksInternal(
                            tr, modelSpace, layerStates, rules, targetBlockNames: null,
                            allowedLayers: null, requireExplicitMatch: true,
                            includeNonPlottable: includeNonPlottable, minEdge: minEdge,
                            searchRegion: activeSearchRegion));
                    }

                    // 3. If user's specified layer is not in this DWG or yielded 0 frames,
                    // automatically fall back to built-in frame layers present in this DWG (e.g. BORDER, TK, PUB_TITLE, 图框)
                    if (rawCandidates.Count == 0)
                    {
                        HashSet<string> autoFrameLayers = DetectFrameLayersInDrawing(layerStates, rules);
                        if (autoFrameLayers.Count > 0)
                        {
                            rawCandidates.AddRange(ScanLayersForFrames(
                                tr, modelSpace, layerStates, rules, autoFrameLayers,
                                includeNonPlottable, minEdge, activeSearchRegion, targetBlocks));
                        }
                    }

                    // 4. Fallback to explicit titleblock BlockReferences matching FrameRules or sheet-sized blocks
                    if (rawCandidates.Count == 0)
                    {
                        rawCandidates.AddRange(ScanBlocksInternal(
                            tr, modelSpace, layerStates, rules, targetBlockNames: targetBlocks.Count > 0 ? targetBlocks : null,
                            allowedLayers: null, requireExplicitMatch: true,
                            includeNonPlottable: includeNonPlottable, minEdge: minEdge,
                            searchRegion: activeSearchRegion));
                    }

                    if (rawCandidates.Count == 0)
                    {
                        rawCandidates.AddRange(ScanBlocksInternal(
                            tr, modelSpace, layerStates, rules, targetBlockNames: null,
                            allowedLayers: null, requireExplicitMatch: false,
                            includeNonPlottable: includeNonPlottable, minEdge: minEdge,
                            searchRegion: activeSearchRegion,
                            requireSheetLikeSizeWhenUnmatched: true));
                    }
                }

                // Filter by FrameRules MinFrameWidth / MinFrameHeight
                rawCandidates = rawCandidates
                    .Where(c => MeetsMinFrameDimensions(c.WidthDrawingUnits, c.HeightDrawingUnits, rules, isPaperSpace: false))
                    .ToList();

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
                HashSet<string> ruleLayers = GetRuleAllowedLayers(rules, layerStates);
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
                    HashSet<string> targetBlocks = ParseCsvList(settings.ActiveFrameBlockName);

                    // 1. Scan for explicit frame polylines, lines, or blocks in this layout's BlockTableRecord
                    if (settings.RecognitionMode == FrameRecognitionMode.ByBlock)
                    {
                        rawInLayout.AddRange(ScanBlocksInternal(
                            tr, layoutBtr, layerStates, rules, targetBlocks,
                            allowedLayers: null, requireExplicitMatch: true,
                            includeNonPlottable: includeNonPlottable, minEdge: minEdge,
                            searchRegion: null));

                        if (rawInLayout.Count == 0 && targetBlocks.Count > 0)
                        {
                            rawInLayout.AddRange(ScanBlocksInternal(
                                tr, layoutBtr, layerStates, rules, targetBlockNames: null,
                                allowedLayers: null, requireExplicitMatch: true,
                                includeNonPlottable: includeNonPlottable, minEdge: minEdge,
                                searchRegion: null));
                        }

                        if (rawInLayout.Count == 0)
                        {
                            rawInLayout.AddRange(ScanBlocksInternal(
                                tr, layoutBtr, layerStates, rules, targetBlockNames: null,
                                allowedLayers: null, requireExplicitMatch: false,
                                includeNonPlottable: includeNonPlottable, minEdge: minEdge,
                                searchRegion: null,
                                requireSheetLikeSizeWhenUnmatched: true));
                        }
                    }
                    else
                    {
                        HashSet<string> userLayers = ParseCsvList(settings.ActiveFrameLayer);
                        foreach (string rl in ruleLayers)
                        {
                            userLayers.Add(rl);
                        }
                        HashSet<string> presentUserLayers = FilterPresentFrameLayers(userLayers, layerStates);

                        if (presentUserLayers.Count > 0)
                        {
                            rawInLayout.AddRange(ScanLayersForFrames(
                                tr, layoutBtr, layerStates, rules, presentUserLayers,
                                includeNonPlottable, minEdge, null, targetBlocks));
                        }

                        if (HasExplicitBlockNameRules(rules))
                        {
                            rawInLayout.AddRange(ScanBlocksInternal(
                                tr, layoutBtr, layerStates, rules, targetBlockNames: null,
                                allowedLayers: null, requireExplicitMatch: true,
                                includeNonPlottable: includeNonPlottable, minEdge: minEdge,
                                searchRegion: null));
                        }

                        if (rawInLayout.Count == 0)
                        {
                            HashSet<string> autoFrameLayers = DetectFrameLayersInDrawing(layerStates, rules);
                            if (autoFrameLayers.Count > 0)
                            {
                                rawInLayout.AddRange(ScanLayersForFrames(
                                    tr, layoutBtr, layerStates, rules, autoFrameLayers,
                                    includeNonPlottable, minEdge, null, targetBlocks));
                            }
                        }

                        if (rawInLayout.Count == 0)
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
            Extents2D? searchRegion = null,
            HashSet<string> targetBlockNames = null)
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

                bool onTargetLayer = targetLayers != null && targetLayers.Contains(layer);
                if (!onTargetLayer)
                {
                    if (ent is BlockReference brCheck)
                    {
                        string effName = GetEffectiveBlockName(tr, brCheck);
                        string rawName = brCheck.Name ?? "";
                        bool matchesExplicitRuleOrTarget =
                            MatchesAnyBlockNameRule(effName, rawName, rules) ||
                            (targetBlockNames != null && targetBlockNames.Any(p => WildcardMatch(effName, p) || WildcardMatch(rawName, p)));

                        if (matchesExplicitRuleOrTarget ||
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

                    // On an explicit frame layer (or with internal curves on the frame layer or matching block rule),
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
            Extents2D? searchRegion = null,
            bool requireSheetLikeSizeWhenUnmatched = false)
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
                    requireSheetLikeSizeOnLayer: requireSheetLikeSizeWhenUnmatched);
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

            bool matchesTargetBlock = false;
            if (targetBlockNames != null && targetBlockNames.Count > 0)
            {
                matchesTargetBlock = targetBlockNames.Any(pattern =>
                    WildcardMatch(effectiveName, pattern) || WildcardMatch(rawName, pattern));
            }

            bool matchesBlockNameRule = MatchesAnyBlockNameRule(effectiveName, rawName, rules);

            // Never treat anonymous AutoCAD paste blocks (A$C...) as full sheet frames unless matched by targetBlockNames or FrameRules.BlockNames
            if (effectiveName.StartsWith("A$C", StringComparison.OrdinalIgnoreCase) &&
                !matchesTargetBlock && !matchesBlockNameRule)
            {
                return null;
            }

            // If caller specified targetBlockNames, accept blocks matching either targetBlockNames OR explicit FrameRules.BlockNames
            if (targetBlockNames != null && targetBlockNames.Count > 0 && !matchesTargetBlock && !matchesBlockNameRule)
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

            if (!MeetsMinFrameDimensions(width, height, rules, isPaperSpace: minEdge <= 180.0))
            {
                return null;
            }

            if (requireSheetLikeSizeOnLayer && !matchesTargetBlock && !matchesBlockNameRule && !hasMappedAttributes)
            {
                // On a frame layer, if a block is a small titlebar or detail symbol,
                // its standard/lengthened sheet dimension check will reject it so only true full-sheet blocks pass.
                if (!ScaleParser.TryInferScaleFromAvailableMedia(
                        width, height, DrawingUnits.Millimeters, null, 10.0, out _, out _) &&
                    !IsPlausibleLengthenedOrStandardSheetSize(width, height))
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
        /// Scans DBText, MText, and inner/outer BlockReferences (both attributes and internal DBText/MText)
        /// located inside each accepted outer frame to automatically populate DrawingName, DrawingNumber, and Scale
        /// using spatial proximity and geometric heuristics.
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
                    AddDbTextItem(dbText, Matrix3d.Identity, layer, textItems);
                }
                else if (ent is MText mText)
                {
                    AddMTextItems(mText, Matrix3d.Identity, layer, rules, textItems);
                }
                else if (ent is BlockReference br)
                {
                    // Check if this BlockReference is one of the outer frames or an inner titleblock inside one of the outer frames
                    Point2D brPos = new Point2D(br.Position.X, br.Position.Y);
                    Extents2D? brBox = null;
                    try
                    {
                        Extents3d bExt = br.GeometricExtents;
                        brPos = new Point2D((bExt.MinPoint.X + bExt.MaxPoint.X) * 0.5, (bExt.MinPoint.Y + bExt.MaxPoint.Y) * 0.5);
                        brBox = new Extents2D(bExt.MinPoint.X, bExt.MinPoint.Y, bExt.MaxPoint.X, bExt.MaxPoint.Y);
                    }
                    catch { }

                    var ownerFrame = frames.FirstOrDefault(f =>
                        string.Equals(f.FrameHandle, br.Handle.ToString(), StringComparison.OrdinalIgnoreCase) ||
                        (f.WcsBoundingBox.MinX <= brPos.X && brPos.X <= f.WcsBoundingBox.MaxX &&
                         f.WcsBoundingBox.MinY <= brPos.Y && brPos.Y <= f.WcsBoundingBox.MaxY) ||
                        (brBox.HasValue && f.WcsBoundingBox.Intersects(brBox.Value, 1.0)));

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

                        // Collect AttributeReferences and internal DBText/MText from this BlockReference into textItems
                        // so TitleBlockTextAnalyzer can pair labels ("Drawing\nTitle :") and values ("GROUND FLOOR LAYOUT PLAN")
                        // even when they are inside a block or have unmapped attribute tags!
                        CollectTextsFromBlockReference(tr, br, Matrix3d.Identity, layer, layerStates, rules, textItems, depth: 0);

                        // Also check if an inner block's effective name itself is a drawing title (e.g. "15-r-1层梁平法施工图")
                        if (!string.Equals(ownerFrame.FrameHandle, br.Handle.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
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
            }

            if (textItems.Count == 0) return;

            foreach (var frame in frames)
            {
                Extents2D box = frame.WcsBoundingBox;
                double tol = Math.Max(box.Width, box.Height) * 0.01;
                var inFrameTexts = textItems
                    .Where(t => (box.MinX - tol) <= t.Position.X && t.Position.X <= (box.MaxX + tol) &&
                                (box.MinY - tol) <= t.Position.Y && t.Position.Y <= (box.MaxY + tol))
                    .ToList();

                _textAnalyzer.EnrichFrame(frame, inFrameTexts, rules);
            }
        }

        private static void AddDbTextItem(
            DBText dbText,
            Matrix3d transform,
            string effectiveLayer,
            List<TitleTextItem> textItems)
        {
            if (dbText == null) return;
            string txt = (dbText.TextString ?? "").Trim();
            if (string.IsNullOrWhiteSpace(txt)) return;

            string cleaned = TitleBlockTextAnalyzer.CleanCadFormatting(txt);
            if (string.IsNullOrWhiteSpace(cleaned)) return;

            Point3d pos = dbText.Position;
            try
            {
                Extents3d ext = dbText.GeometricExtents;
                pos = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) * 0.5, (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5, 0);
            }
            catch { }

            double height = dbText.Height;
            if (transform != Matrix3d.Identity)
            {
                pos = pos.TransformBy(transform);
                try
                {
                    height = new Vector3d(0, dbText.Height, 0).TransformBy(transform).Length;
                }
                catch { }
            }

            textItems.Add(new TitleTextItem
            {
                Position = new Point2D(pos.X, pos.Y),
                Text = cleaned,
                Height = Math.Max(height, 1.0),
                Layer = effectiveLayer
            });
        }

        private static void AddMTextItems(
            MText mText,
            Matrix3d transform,
            string effectiveLayer,
            IList<FrameRule> rules,
            List<TitleTextItem> textItems)
        {
            if (mText == null) return;
            string raw = (mText.Contents ?? mText.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw)) return;

            Point3d pos = mText.Location;
            try
            {
                Extents3d ext = mText.GeometricExtents;
                pos = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) * 0.5, (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5, 0);
            }
            catch { }

            double height = mText.TextHeight;
            if (transform != Matrix3d.Identity)
            {
                pos = pos.TransformBy(transform);
                try
                {
                    height = new Vector3d(0, mText.TextHeight, 0).TransformBy(transform).Length;
                }
                catch { }
            }
            height = Math.Max(height, 1.0);

            string[] rawLines = raw.Split(new[] { "\r\n", "\n", "\\P", "\\p" }, StringSplitOptions.RemoveEmptyEntries);
            var cleanedLines = new List<string>();
            foreach (string line in rawLines)
            {
                string c = TitleBlockTextAnalyzer.CleanCadFormatting(line);
                if (!string.IsNullOrWhiteSpace(c))
                {
                    cleanedLines.Add(c);
                }
            }

            if (cleanedLines.Count == 0) return;

            if (cleanedLines.Count == 1)
            {
                textItems.Add(new TitleTextItem
                {
                    Position = new Point2D(pos.X, pos.Y),
                    Text = cleanedLines[0],
                    Height = height,
                    Layer = effectiveLayer
                });
                return;
            }

            // When MText has 2-3 short lines (e.g. "Drawing\PTitle :" or "GROUND FLOOR\PLAYOUT PLAN"),
            // join them into a single coherent phrase first so multi-line labels and multi-line titles stay intact!
            string joined = string.Join(" ", cleanedLines).Trim();
            if (cleanedLines.Count <= 3 && joined.Length <= 70)
            {
                textItems.Add(new TitleTextItem
                {
                    Position = new Point2D(pos.X, pos.Y),
                    Text = joined,
                    Height = height,
                    Layer = effectiveLayer
                });

                // If the joined text is itself a label (like "Drawing Title :" or "图名 DRAWING TITLE"),
                // do NOT add the split fragments ("Drawing", "Title :") as separate items at the same point.
                if (TitleBlockTextAnalyzer.IsAnyLabel(joined, rules) ||
                    TitleBlockTextAnalyzer.IsPureLabelEnglishTranslation(joined))
                {
                    return;
                }
            }

            for (int i = 0; i < cleanedLines.Count; i++)
            {
                string line = cleanedLines[i];
                if (TitleBlockTextAnalyzer.IsPureLabelEnglishTranslation(line) && cleanedLines.Count > 1)
                {
                    continue;
                }
                textItems.Add(new TitleTextItem
                {
                    Position = new Point2D(pos.X, pos.Y - i * height * 1.2),
                    Text = line,
                    Height = height,
                    Layer = effectiveLayer
                });
            }
        }

        private void CollectTextsFromBlockReference(
            Transaction tr,
            BlockReference br,
            Matrix3d parentTransform,
            string parentLayer,
            Dictionary<string, LayerStateInfo> layerStates,
            IList<FrameRule> rules,
            List<TitleTextItem> textItems,
            int depth)
        {
            if (br == null || depth > 2) return;

            string brLayer = (!string.IsNullOrWhiteSpace(br.Layer) && br.Layer != "0") ? br.Layer : parentLayer;

            // 1. Collect AttributeReferences attached to this BlockReference
            if (br.AttributeCollection != null && br.AttributeCollection.Count > 0)
            {
                foreach (ObjectId attId in br.AttributeCollection)
                {
                    if (attId.IsErased) continue;
                    AttributeReference attRef = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                    if (attRef == null || !attRef.Visible) continue;

                    string attLayer = (!string.IsNullOrWhiteSpace(attRef.Layer) && attRef.Layer != "0") ? attRef.Layer : brLayer;
                    if (layerStates.TryGetValue(attLayer, out LayerStateInfo lsi) && (lsi.IsOff || lsi.IsFrozen))
                    {
                        continue;
                    }

                    string rawTxt = (attRef.IsMTextAttribute && attRef.MTextAttribute != null)
                        ? (attRef.MTextAttribute.Contents ?? attRef.TextString ?? "")
                        : (attRef.TextString ?? "");
                    string cleaned = TitleBlockTextAnalyzer.CleanCadFormatting(rawTxt);
                    if (string.IsNullOrWhiteSpace(cleaned)) continue;

                    Point3d pos = attRef.Position;
                    try
                    {
                        Extents3d ext = attRef.GeometricExtents;
                        pos = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) * 0.5, (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5, 0);
                    }
                    catch { }

                    double height = attRef.Height;
                    if (parentTransform != Matrix3d.Identity)
                    {
                        pos = pos.TransformBy(parentTransform);
                        try
                        {
                            height = new Vector3d(0, attRef.Height, 0).TransformBy(parentTransform).Length;
                        }
                        catch { }
                    }

                    textItems.Add(new TitleTextItem
                    {
                        Position = new Point2D(pos.X, pos.Y),
                        Text = cleaned,
                        Height = Math.Max(height, 1.0),
                        Layer = attLayer
                    });
                }
            }

            // 2. Collect DBText, MText, and nested BlockReferences inside the BlockTableRecord
            if (br.BlockTableRecord.IsNull || br.BlockTableRecord.IsErased) return;
            BlockTableRecord btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null) return;

            Matrix3d combinedTransform = parentTransform * br.BlockTransform;

            foreach (ObjectId childId in btr)
            {
                if (childId.IsErased) continue;
                Entity child = tr.GetObject(childId, OpenMode.ForRead) as Entity;
                if (child == null || !child.Visible || child is AttributeDefinition) continue;

                string childLayer = (!string.IsNullOrWhiteSpace(child.Layer) && child.Layer != "0") ? child.Layer : brLayer;
                if (layerStates.TryGetValue(childLayer, out LayerStateInfo lsi) && (lsi.IsOff || lsi.IsFrozen))
                {
                    continue;
                }

                if (child is DBText dbText)
                {
                    AddDbTextItem(dbText, combinedTransform, childLayer, textItems);
                }
                else if (child is MText mText)
                {
                    AddMTextItems(mText, combinedTransform, childLayer, rules, textItems);
                }
                else if (child is BlockReference nestedBr)
                {
                    CollectTextsFromBlockReference(tr, nestedBr, combinedTransform, childLayer, layerStates, rules, textItems, depth + 1);
                }
            }
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

            double minCurveX = double.MaxValue;
            double minCurveY = double.MaxValue;
            double maxCurveX = double.MinValue;
            double maxCurveY = double.MinValue;
            bool hasCurveExtents = false;

            double minAllX = double.MaxValue;
            double minAllY = double.MaxValue;
            double maxAllX = double.MinValue;
            double maxAllY = double.MinValue;
            bool hasAllExtents = false;

            var localLines = new List<Tuple<Point2D, Point2D, string, string>>();

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
                else if (ent is Line ln)
                {
                    Point3d p1 = ln.StartPoint.TransformBy(transform);
                    Point3d p2 = ln.EndPoint.TransformBy(transform);
                    if (p1.DistanceTo(p2) >= minEdge * 0.5)
                    {
                        localLines.Add(Tuple.Create(
                            new Point2D(p1.X, p1.Y),
                            new Point2D(p2.X, p2.Y),
                            br.Layer ?? "0",
                            ln.Handle.ToString()));
                    }
                }

                try
                {
                    Extents3d ext = ent.GeometricExtents;
                    if (ext.MinPoint.X < minAllX) minAllX = ext.MinPoint.X;
                    if (ext.MinPoint.Y < minAllY) minAllY = ext.MinPoint.Y;
                    if (ext.MaxPoint.X > maxAllX) maxAllX = ext.MaxPoint.X;
                    if (ext.MaxPoint.Y > maxAllY) maxAllY = ext.MaxPoint.Y;
                    hasAllExtents = true;

                    if (ent is Curve || ent is BlockReference)
                    {
                        if (ext.MinPoint.X < minCurveX) minCurveX = ext.MinPoint.X;
                        if (ext.MinPoint.Y < minCurveY) minCurveY = ext.MinPoint.Y;
                        if (ext.MaxPoint.X > maxCurveX) maxCurveX = ext.MaxPoint.X;
                        if (ext.MaxPoint.Y > maxCurveY) maxCurveY = ext.MaxPoint.Y;
                        hasCurveExtents = true;
                    }
                }
                catch
                {
                }
            }

            if (localLines.Count >= 4)
            {
                var lineRects = _geometryAnalyzer.ReconstructRectanglesFromLines(localLines, minEdge);
                foreach (var lr in lineRects)
                {
                    double area = lr.WidthDrawingUnits * lr.HeightDrawingUnits;
                    if (area > bestRectArea)
                    {
                        bestRectArea = area;
                        bestWcsRectCorners = lr.Corners;
                    }
                }
            }

            Point2D[] extentsWcsCorners = null;
            double extentsWcsArea = 0;

            bool useCurveBox = hasCurveExtents && (maxCurveX > minCurveX) && (maxCurveY > minCurveY);
            double minX = useCurveBox ? minCurveX : minAllX;
            double minY = useCurveBox ? minCurveY : minAllY;
            double maxX = useCurveBox ? maxCurveX : maxAllX;
            double maxY = useCurveBox ? maxCurveY : maxAllY;

            if ((useCurveBox || hasAllExtents) && (maxX > minX) && (maxY > minY))
            {
                Point3d p0 = new Point3d(minX, minY, 0).TransformBy(transform);
                Point3d p1 = new Point3d(maxX, minY, 0).TransformBy(transform);
                Point3d p2 = new Point3d(maxX, maxY, 0).TransformBy(transform);
                Point3d p3 = new Point3d(minX, maxY, 0).TransformBy(transform);

                extentsWcsCorners = new Point2D[]
                {
                    new Point2D(p0.X, p0.Y),
                    new Point2D(p1.X, p1.Y),
                    new Point2D(p2.X, p2.Y),
                    new Point2D(p3.X, p3.Y)
                };
                extentsWcsArea = p0.DistanceTo(p1) * p1.DistanceTo(p2);
            }

            Point2D[] rawWcsCorners = null;
            // Only use an internal closed polyline if its area is at least 50% of the block's curve extents area
            // (so a small closed polyline around a titleblock cell never overrides the full sheet border!)
            if (bestWcsRectCorners != null && (extentsWcsArea <= 0 || bestRectArea >= extentsWcsArea * 0.50))
            {
                rawWcsCorners = bestWcsRectCorners;
            }
            else if (extentsWcsCorners != null)
            {
                rawWcsCorners = extentsWcsCorners;
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

        private static HashSet<string> GetRuleAllowedLayers(
            IList<FrameRule> rules,
            Dictionary<string, LayerStateInfo> layerStates)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rules == null) return set;
            foreach (var r in rules)
            {
                if (r?.AllowedLayers == null) continue;
                foreach (string l in r.AllowedLayers)
                {
                    if (string.IsNullOrWhiteSpace(l)) continue;
                    string trimmed = l.Trim();
                    if (layerStates == null || layerStates.ContainsKey(trimmed))
                    {
                        set.Add(trimmed);
                    }
                }
            }
            return set;
        }

        private static bool HasExplicitBlockNameRules(IList<FrameRule> rules)
        {
            if (rules == null) return false;
            return rules.Any(r => r?.BlockNames != null && r.BlockNames.Any(b => !string.IsNullOrWhiteSpace(b)));
        }

        private static bool MeetsMinFrameDimensions(
            double width,
            double height,
            IList<FrameRule> rules,
            bool isPaperSpace)
        {
            if (isPaperSpace)
            {
                return width >= 150.0 && height >= 150.0;
            }
            if (rules == null || rules.Count == 0)
            {
                return width >= 200.0 && height >= 200.0;
            }

            double ruleMinW = rules.Min(r => r.MinFrameWidth > 0 ? r.MinFrameWidth : 200.0);
            double ruleMinH = rules.Min(r => r.MinFrameHeight > 0 ? r.MinFrameHeight : 200.0);
            double reqLong = Math.Max(ruleMinW, ruleMinH);
            double reqShort = Math.Min(ruleMinW, ruleMinH);

            double actualLong = Math.Max(width, height);
            double actualShort = Math.Min(width, height);

            return actualLong >= reqLong * 0.95 && actualShort >= reqShort * 0.95;
        }

        private static bool IsPlausibleLengthenedOrStandardSheetSize(double width, double height)
        {
            double shortEdge = Math.Min(width, height);
            double longEdge = Math.Max(width, height);
            if (shortEdge <= 0) return false;
            double ratio = longEdge / shortEdge;
            if (ratio < 1.15 || ratio > 4.5) return false;

            double[] standardShortEdgesMm = { 210.0, 297.0, 420.0, 594.0, 841.0 };
            double[] standardScales = { 1.0, 2.0, 5.0, 10.0, 20.0, 25.0, 30.0, 40.0, 50.0, 60.0, 75.0, 80.0, 100.0, 120.0, 125.0, 150.0, 200.0, 250.0, 300.0, 400.0, 500.0, 1000.0 };

            foreach (double sc in standardScales)
            {
                double normShort = shortEdge / sc;
                foreach (double stdMm in standardShortEdgesMm)
                {
                    if (Math.Abs(normShort - stdMm) <= stdMm * 0.05)
                    {
                        return true;
                    }
                }
            }
            return false;
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
            if (br == null || br.AttributeCollection == null) return dict;

            // Map Tag -> Prompt from AttributeDefinitions in the BlockTableRecord if available
            var tagToPrompt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!br.BlockTableRecord.IsNull && !br.BlockTableRecord.IsErased)
                {
                    BlockTableRecord btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                    if (btr != null)
                    {
                        foreach (ObjectId entId in btr)
                        {
                            if (entId.IsErased) continue;
                            AttributeDefinition attDef = tr.GetObject(entId, OpenMode.ForRead) as AttributeDefinition;
                            if (attDef != null && !string.IsNullOrWhiteSpace(attDef.Tag) && !string.IsNullOrWhiteSpace(attDef.Prompt))
                            {
                                string cleanPrompt = attDef.Prompt.Trim().TrimEnd(':', '：');
                                if (!string.IsNullOrWhiteSpace(cleanPrompt))
                                {
                                    tagToPrompt[attDef.Tag.Trim()] = cleanPrompt;
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            foreach (ObjectId attId in br.AttributeCollection)
            {
                if (attId.IsErased) continue;
                AttributeReference attRef = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                if (attRef != null && !string.IsNullOrWhiteSpace(attRef.Tag))
                {
                    string tag = attRef.Tag.Trim();
                    string rawVal = (attRef.IsMTextAttribute && attRef.MTextAttribute != null)
                        ? (attRef.MTextAttribute.Contents ?? attRef.TextString ?? string.Empty)
                        : (attRef.TextString ?? string.Empty);
                    string val = TitleBlockTextAnalyzer.CleanCadFormatting(rawVal);

                    if (!dict.ContainsKey(tag) || string.IsNullOrWhiteSpace(dict[tag]))
                    {
                        dict[tag] = val;
                    }

                    if (tagToPrompt.TryGetValue(tag, out string promptKey) &&
                        (!dict.ContainsKey(promptKey) || string.IsNullOrWhiteSpace(dict[promptKey])))
                    {
                        dict[promptKey] = val;
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
                    drawingNumber = FindFirstMatchingTagValue(
                        attributes,
                        rule.SheetNumberTags,
                        val => !TitleBlockTextAnalyzer.IgnoredTitleTexts.Contains(val) &&
                               !TitleBlockTextAnalyzer.IsPureLabelEnglishTranslation(val) &&
                               !TitleBlockTextAnalyzer.IsAnyLabel(val, rules) &&
                               (TitleBlockTextAnalyzer.IsDrawingNumberText(val) || Regex.IsMatch(val, @"\d")));
                }
                if (string.IsNullOrWhiteSpace(drawingName) && rule.SheetNameTags != null)
                {
                    drawingName = FindFirstMatchingTagValue(
                        attributes,
                        rule.SheetNameTags,
                        val => !TitleBlockTextAnalyzer.IgnoredTitleTexts.Contains(val) &&
                               !TitleBlockTextAnalyzer.IsPureLabelEnglishTranslation(val) &&
                               !TitleBlockTextAnalyzer.IsAnyLabel(val, rules) &&
                               !TitleBlockTextAnalyzer.IsEngineeringDetailOrRebarAnnotation(val) &&
                               val.Length >= 2);
                }
                if (string.IsNullOrWhiteSpace(scaleText) && rule.ScaleTags != null)
                {
                    scaleText = FindFirstMatchingTagValue(
                        attributes,
                        rule.ScaleTags,
                        val => !TitleBlockTextAnalyzer.IgnoredTitleTexts.Contains(val) &&
                               !TitleBlockTextAnalyzer.IsPureLabelEnglishTranslation(val) &&
                               ScaleParser.TryParseScaleValue(val).HasValue);
                }
            }

            if (!string.IsNullOrWhiteSpace(scaleText))
            {
                scaleValue = ScaleParser.TryParseScaleValue(scaleText);
            }
        }

        private static string FindFirstMatchingTagValue(
            Dictionary<string, string> attributes,
            IList<string> candidateTags,
            Func<string, bool> valueValidator = null)
        {
            if (attributes == null || candidateTags == null) return null;

            // Pass 1: Exact tag/prompt match
            foreach (string tagPattern in candidateTags)
            {
                if (string.IsNullOrWhiteSpace(tagPattern)) continue;
                string key = tagPattern.Trim();
                if (attributes.TryGetValue(key, out string exactVal) && !string.IsNullOrWhiteSpace(exactVal))
                {
                    string trimmed = exactVal.Trim();
                    if (valueValidator == null || valueValidator(trimmed))
                    {
                        return trimmed;
                    }
                }
            }

            // Pass 2: Substring match for tags of length >= 2 (excluding project/company tags when matching title/number)
            foreach (string tagPattern in candidateTags)
            {
                if (string.IsNullOrWhiteSpace(tagPattern)) continue;
                string key = tagPattern.Trim();
                if (key.Length < 2) continue;

                foreach (var kvp in attributes)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Value)) continue;
                    string attrKey = kvp.Key.Trim();

                    // Avoid matching "PROJECT_NAME" / "工程名称" / "子项名称" when pattern is "NAME" or "TITLE"
                    if ((attrKey.IndexOf("PROJ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         attrKey.IndexOf("工程", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         attrKey.IndexOf("项目", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         attrKey.IndexOf("子项", StringComparison.OrdinalIgnoreCase) >= 0) &&
                        key.IndexOf("PROJ", StringComparison.OrdinalIgnoreCase) < 0 &&
                        key.IndexOf("工程", StringComparison.OrdinalIgnoreCase) < 0 &&
                        key.IndexOf("项目", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    // For short 2-char ASCII tags like "NO" or "BL", require word boundary so "NO" doesn't match "NOTE"
                    bool isMatch;
                    if (key.Length <= 2 && Regex.IsMatch(key, @"^[A-Za-z]+$"))
                    {
                        isMatch = Regex.IsMatch(attrKey, @"(?:^|[_\-\s])" + Regex.Escape(key) + @"(?:$|[_\-\s\d])", RegexOptions.IgnoreCase);
                    }
                    else
                    {
                        isMatch = attrKey.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
                    }

                    if (isMatch)
                    {
                        string trimmed = kvp.Value.Trim();
                        if (valueValidator == null || valueValidator(trimmed))
                        {
                            return trimmed;
                        }
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
