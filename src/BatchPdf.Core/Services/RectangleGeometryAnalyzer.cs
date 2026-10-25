using System;
using System.Collections.Generic;
using System.Linq;
using BatchPdf.Core.Models;

namespace BatchPdf.Core.Services
{
    public class RectangleGeometryAnalyzer
    {
        private const double RightAngleToleranceDegrees = 1.8;
        private const double CollinearToleranceDegrees = 1.2;
        private const double OppositeEdgeRelativeTolerance = 0.015;
        private const double MaxAllowedBulge = 0.01;
        private const double MinFrameAspectRatio = 1.12;
        private const double MaxFrameAspectRatio = 4.85;

        /// <summary>
        /// Analyzes a closed polyline's vertices and bulges to determine if it forms a valid rectangle
        /// (including rotated or lengthened rectangles).
        /// </summary>
        public bool TryAnalyzeClosedPolyline(
            IList<Point2D> vertices,
            IList<double> bulges,
            double minEdgeLength,
            out Point2D[] orderedCorners,
            out double width,
            out double height,
            out double rotationAngleRad,
            out List<string> warnings)
        {
            orderedCorners = null;
            width = 0;
            height = 0;
            rotationAngleRad = 0;
            warnings = new List<string>();

            if (vertices == null || vertices.Count < 4)
            {
                return false;
            }

            if (bulges != null)
            {
                for (int i = 0; i < bulges.Count; i++)
                {
                    if (Math.Abs(bulges[i]) > MaxAllowedBulge)
                    {
                        return false;
                    }
                }
            }

            List<Point2D> cleaned = RemoveDuplicateAndCollinearVertices(vertices);
            if (cleaned.Count != 4)
            {
                return false;
            }

            double d01 = cleaned[0].DistanceTo(cleaned[1]);
            double d12 = cleaned[1].DistanceTo(cleaned[2]);
            double d23 = cleaned[2].DistanceTo(cleaned[3]);
            double d30 = cleaned[3].DistanceTo(cleaned[0]);

            if (d01 < minEdgeLength || d12 < minEdgeLength || d23 < minEdgeLength || d30 < minEdgeLength)
            {
                return false;
            }

            double avgPair1 = (d01 + d23) * 0.5;
            double avgPair2 = (d12 + d30) * 0.5;
            if (Math.Abs(d01 - d23) / avgPair1 > OppositeEdgeRelativeTolerance ||
                Math.Abs(d12 - d30) / avgPair2 > OppositeEdgeRelativeTolerance)
            {
                return false;
            }

            for (int i = 0; i < 4; i++)
            {
                Point2D prev = cleaned[(i + 3) % 4];
                Point2D curr = cleaned[i];
                Point2D next = cleaned[(i + 1) % 4];
                double angleDeg = ComputeInteriorAngleDegrees(prev, curr, next);
                if (Math.Abs(angleDeg - 90.0) > RightAngleToleranceDegrees)
                {
                    return false;
                }
                if (Math.Abs(angleDeg - 90.0) > 0.5)
                {
                    warnings.Add(string.Format("角点直角偏差 {0:F2}°", Math.Abs(angleDeg - 90.0)));
                }
            }

            OrderCornersCanonically(cleaned, out orderedCorners, out width, out height, out rotationAngleRad);

            double longSide = Math.Max(width, height);
            double shortSide = Math.Min(width, height);
            double aspect = shortSide > 1e-6 ? longSide / shortSide : 999.0;
            if (aspect < MinFrameAspectRatio || aspect > MaxFrameAspectRatio)
            {
                return false;
            }

            return true;
        }

        public static bool IsValidSheetAspectRatio(double width, double height)
        {
            double longSide = Math.Max(width, height);
            double shortSide = Math.Min(width, height);
            if (shortSide <= 1e-6) return false;
            double aspect = longSide / shortSide;
            return aspect >= MinFrameAspectRatio && aspect <= MaxFrameAspectRatio;
        }

        /// <summary>
        /// Orders 4 corners of a known rectangle (e.g. from a BlockReference or Polyline) into
        /// canonical local coordinates [0]=BL, [1]=BR, [2]=TR, [3]=TL, with RotationAngleRad in (-PI/4, PI/4]
        /// unless a specific base rotation angle is provided.
        /// </summary>
        public static void OrderCornersCanonically(
            IList<Point2D> fourCorners,
            out Point2D[] orderedCorners,
            out double width,
            out double height,
            out double rotationAngleRad,
            double? preferredRotationRad = null)
        {
            Point2D center = new Point2D(
                fourCorners.Average(p => p.X),
                fourCorners.Average(p => p.Y));

            double candidateAngleRad;
            if (preferredRotationRad.HasValue)
            {
                candidateAngleRad = NormalizeAngleMinusPiToPi(preferredRotationRad.Value);
                double mod90Rad = NormalizeToMinusPiOver4ToPiOver4(candidateAngleRad);
                if (Math.Abs(mod90Rad) < 0.001)
                {
                    candidateAngleRad = 0.0;
                }
                else
                {
                    candidateAngleRad = mod90Rad;
                }
            }
            else
            {
                double dx = fourCorners[1].X - fourCorners[0].X;
                double dy = fourCorners[1].Y - fourCorners[0].Y;
                double rawAngle = Math.Atan2(dy, dx);
                candidateAngleRad = NormalizeToMinusPiOver4ToPiOver4(rawAngle);
                if (Math.Abs(candidateAngleRad) < 0.001)
                {
                    candidateAngleRad = 0.0;
                }
            }

            rotationAngleRad = candidateAngleRad;

            Point2D[] localPts = new Point2D[4];
            for (int i = 0; i < 4; i++)
            {
                localPts[i] = fourCorners[i].RotateAround(center, -rotationAngleRad);
            }

            double minX = localPts.Min(p => p.X);
            double maxX = localPts.Max(p => p.X);
            double minY = localPts.Min(p => p.Y);
            double maxY = localPts.Max(p => p.Y);

            width = maxX - minX;
            height = maxY - minY;

            Point2D localBL = new Point2D(minX, minY);
            Point2D localBR = new Point2D(maxX, minY);
            Point2D localTR = new Point2D(maxX, maxY);
            Point2D localTL = new Point2D(minX, maxY);

            orderedCorners = new Point2D[4]
            {
                localBL.RotateAround(center, rotationAngleRad),
                localBR.RotateAround(center, rotationAngleRad),
                localTR.RotateAround(center, rotationAngleRad),
                localTL.RotateAround(center, rotationAngleRad)
            };
        }

        /// <summary>
        /// Deduplicates overlapping frames and inner borders/sub-blocks contained within outer frames,
        /// and sorts remaining frames according to the requested ordering mode.
        /// </summary>
        public List<FrameCandidate> DeduplicateAndRank(
            IEnumerable<FrameCandidate> candidates,
            SheetSortOrderMode sortOrderMode = SheetSortOrderMode.RowFirst,
            bool reverseX = false,
            bool reverseY = false)
        {
            if (candidates == null)
            {
                return new List<FrameCandidate>();
            }

            var indexedInput = candidates
                .Where(c => c != null &&
                            c.WidthDrawingUnits > 1e-2 &&
                            c.HeightDrawingUnits > 1e-2 &&
                            IsValidSheetAspectRatio(c.WidthDrawingUnits, c.HeightDrawingUnits))
                .Select((c, idx) => new { Candidate = c, OriginalIndex = idx })
                .ToList();

            // Sort by Area descending FIRST so outer sheet frames are always accepted before any inner borders or sub-blocks!
            // When two candidates have virtually the same area (within 2%), prefer BlockReference with attributes.
            var sortedByOuterFirst = indexedInput
                .OrderByDescending(x => Math.Round((x.Candidate.WidthDrawingUnits * x.Candidate.HeightDrawingUnits) / 1000.0))
                .ThenByDescending(x => x.Candidate.FrameType == FrameType.BlockReference ? 1 : 0)
                .ThenByDescending(x => x.Candidate.Attributes != null ? x.Candidate.Attributes.Count : 0)
                .ThenByDescending(x => x.Candidate.Confidence)
                .ThenByDescending(x => x.Candidate.WidthDrawingUnits * x.Candidate.HeightDrawingUnits)
                .ToList();

            var accepted = new List<Tuple<FrameCandidate, int>>();

            foreach (var item in sortedByOuterFirst)
            {
                var candidate = item.Candidate;
                bool isDuplicateOrContained = false;
                Point2D candCenter = candidate.WcsBoundingBox.Center;
                double candArea = Math.Max(1.0, candidate.WcsBoundingBox.Area);

                foreach (var existTuple in accepted)
                {
                    var existing = existTuple.Item1;
                    Point2D existCenter = existing.WcsBoundingBox.Center;
                    double centerDist = candCenter.DistanceTo(existCenter);
                    double maxDim = Math.Max(existing.WidthDrawingUnits, existing.HeightDrawingUnits);

                    // 1. Near-identical frame (duplicate polyline or polyline overlapping block outer border)
                    if (centerDist < maxDim * 0.04 &&
                        Math.Abs(candidate.WidthDrawingUnits - existing.WidthDrawingUnits) < maxDim * 0.03 &&
                        Math.Abs(candidate.HeightDrawingUnits - existing.HeightDrawingUnits) < maxDim * 0.03)
                    {
                        MergeMetadata(existing, candidate);
                        isDuplicateOrContained = true;
                        break;
                    }

                    // 2. Candidate is geometrically contained inside an already-accepted larger outer frame,
                    // or overlaps more than 55% of candidate's area with the outer frame (e.g. inner border, titleblock, internal table)
                    double containmentTol = maxDim * 0.025;
                    double overlapArea = existing.WcsBoundingBox.IntersectionArea(candidate.WcsBoundingBox);
                    if (existing.WcsBoundingBox.Contains(candidate.WcsBoundingBox, containmentTol) ||
                        overlapArea >= candArea * 0.55)
                    {
                        MergeMetadata(existing, candidate);
                        isDuplicateOrContained = true;
                        break;
                    }
                }

                if (!isDuplicateOrContained)
                {
                    accepted.Add(Tuple.Create(candidate, item.OriginalIndex));
                }
            }

            return SortAcceptedCandidates(accepted, sortOrderMode, reverseX, reverseY);
        }

        public static string GetSortOrderDescription(SheetSortOrderMode mode, bool reverseX = false, bool reverseY = false)
        {
            return mode.GetSortOrderDescription(reverseX, reverseY);
        }

        /// <summary>
        /// Sorts an existing list of PrintSheet items according to sortOrderMode, reverseX, and reverseY
        /// using their FrameExtents (and updates OrderIndex = 1..N), without losing any existing PrintSheet properties.
        /// </summary>
        public List<PrintSheet> SortPrintSheets(
            IList<PrintSheet> sheets,
            SheetSortOrderMode sortOrderMode = SheetSortOrderMode.RowFirst,
            bool reverseX = false,
            bool reverseY = false)
        {
            if (sheets == null || sheets.Count == 0)
            {
                return new List<PrintSheet>();
            }

            var validSheets = sheets.Where(s => s != null).ToList();
            List<PrintSheet> sorted;

            switch (sortOrderMode)
            {
                case SheetSortOrderMode.ColumnFirst:
                    sorted = ClusterAndSortColumns(validSheets, GetSheetExtents, reverseX, reverseY);
                    break;
                case SheetSortOrderMode.SelectionOrder:
                    sorted = validSheets.ToList();
                    break;
                case SheetSortOrderMode.RowFirst:
                default:
                    sorted = ClusterAndSortRows(validSheets, GetSheetExtents, reverseX, reverseY);
                    break;
            }

            for (int i = 0; i < sorted.Count; i++)
            {
                sorted[i].OrderIndex = i + 1;
            }

            return sorted;
        }

        public static List<FrameCandidate> SortAcceptedCandidates(
            IList<Tuple<FrameCandidate, int>> acceptedWithIndex,
            SheetSortOrderMode sortOrderMode,
            bool reverseX,
            bool reverseY)
        {
            if (acceptedWithIndex == null || acceptedWithIndex.Count == 0)
            {
                return new List<FrameCandidate>();
            }

            if (sortOrderMode == SheetSortOrderMode.SelectionOrder)
            {
                return acceptedWithIndex.OrderBy(t => t.Item2).Select(t => t.Item1).ToList();
            }

            var items = acceptedWithIndex.Select(t => t.Item1).ToList();

            if (sortOrderMode == SheetSortOrderMode.ColumnFirst)
            {
                return ClusterAndSortColumns(items, GetCandidateExtents, reverseX, reverseY);
            }

            return ClusterAndSortRows(items, GetCandidateExtents, reverseX, reverseY);
        }

        public static List<FrameCandidate> ClusterAndSortRows(
            IList<FrameCandidate> items,
            bool reverseX,
            bool reverseY)
        {
            return ClusterAndSortRows(items, GetCandidateExtents, reverseX, reverseY);
        }

        public static List<FrameCandidate> ClusterAndSortColumns(
            IList<FrameCandidate> items,
            bool reverseX,
            bool reverseY)
        {
            return ClusterAndSortColumns(items, GetCandidateExtents, reverseX, reverseY);
        }

        public static List<PrintSheet> ClusterAndSortRows(
            IList<PrintSheet> items,
            bool reverseX,
            bool reverseY)
        {
            return ClusterAndSortRows(items, GetSheetExtents, reverseX, reverseY);
        }

        public static List<PrintSheet> ClusterAndSortColumns(
            IList<PrintSheet> items,
            bool reverseX,
            bool reverseY)
        {
            return ClusterAndSortColumns(items, GetSheetExtents, reverseX, reverseY);
        }

        public static List<T> ClusterAndSortRows<T>(
            IList<T> items,
            Func<T, Extents2D> getExtents,
            bool reverseX,
            bool reverseY)
        {
            if (items == null || items.Count == 0)
            {
                return new List<T>();
            }

            var wrapped = items
                .Select((item, idx) => new { Item = item, Box = getExtents(item), Index = idx })
                .ToList();

            // RowFirst (先行后列):
            // reverseY == false -> top-to-bottom (MaxY descending)
            // reverseY == true  -> bottom-to-top (MinY ascending)
            var remaining = (reverseY
                ? wrapped.OrderBy(x => x.Box.MinY).ThenBy(x => x.Box.Center.Y).ThenBy(x => x.Index)
                : wrapped.OrderByDescending(x => x.Box.MaxY).ThenByDescending(x => x.Box.Center.Y).ThenBy(x => x.Index)).ToList();

            var result = new List<T>(wrapped.Count);

            while (remaining.Count > 0)
            {
                var rowSeed = remaining[0];
                Extents2D seedBox = rowSeed.Box;

                var rowItems = remaining
                    .Where(x => AreInSameRow(seedBox, x.Box))
                    .ToList();

                // Within row:
                // reverseX == false -> left-to-right (MinX ascending)
                // reverseX == true  -> right-to-left (MinX descending)
                rowItems = (reverseX
                    ? rowItems.OrderByDescending(x => x.Box.MinX).ThenByDescending(x => x.Box.Center.X).ThenBy(x => x.Index)
                    : rowItems.OrderBy(x => x.Box.MinX).ThenBy(x => x.Box.Center.X).ThenBy(x => x.Index)).ToList();

                foreach (var rowItem in rowItems)
                {
                    result.Add(rowItem.Item);
                    remaining.Remove(rowItem);
                }
            }

            return result;
        }

        public static List<T> ClusterAndSortColumns<T>(
            IList<T> items,
            Func<T, Extents2D> getExtents,
            bool reverseX,
            bool reverseY)
        {
            if (items == null || items.Count == 0)
            {
                return new List<T>();
            }

            var wrapped = items
                .Select((item, idx) => new { Item = item, Box = getExtents(item), Index = idx })
                .ToList();

            // ColumnFirst (先列后行):
            // reverseX == false -> left-to-right (MinX ascending)
            // reverseX == true  -> right-to-left (MinX descending)
            var remaining = (reverseX
                ? wrapped.OrderByDescending(x => x.Box.MinX).ThenByDescending(x => x.Box.Center.X).ThenBy(x => x.Index)
                : wrapped.OrderBy(x => x.Box.MinX).ThenBy(x => x.Box.Center.X).ThenBy(x => x.Index)).ToList();

            var result = new List<T>(wrapped.Count);

            while (remaining.Count > 0)
            {
                var colSeed = remaining[0];
                Extents2D seedBox = colSeed.Box;

                var colItems = remaining
                    .Where(x => AreInSameColumn(seedBox, x.Box))
                    .ToList();

                // Within column:
                // reverseY == false -> top-to-bottom (MaxY descending)
                // reverseY == true  -> bottom-to-top (MinY ascending)
                colItems = (reverseY
                    ? colItems.OrderBy(x => x.Box.MinY).ThenBy(x => x.Box.Center.Y).ThenBy(x => x.Index)
                    : colItems.OrderByDescending(x => x.Box.MaxY).ThenByDescending(x => x.Box.Center.Y).ThenBy(x => x.Index)).ToList();

                foreach (var colItem in colItems)
                {
                    result.Add(colItem.Item);
                    remaining.Remove(colItem);
                }
            }

            return result;
        }

        private static bool AreInSameRow(Extents2D box1, Extents2D box2)
        {
            double h1 = Math.Max(box1.Height, 1e-6);
            double h2 = Math.Max(box2.Height, 1e-6);
            double avgHeight = (h1 + h2) * 0.5;
            double rowTolerance = avgHeight * 0.45;

            if (Math.Abs(box1.Center.Y - box2.Center.Y) <= rowTolerance)
            {
                return true;
            }

            double verticalOverlap = Math.Max(0.0, Math.Min(box1.MaxY, box2.MaxY) - Math.Max(box1.MinY, box2.MinY));
            double minHeight = Math.Min(h1, h2);
            return (verticalOverlap / minHeight) >= 0.40;
        }

        private static bool AreInSameColumn(Extents2D box1, Extents2D box2)
        {
            double w1 = Math.Max(box1.Width, 1e-6);
            double w2 = Math.Max(box2.Width, 1e-6);
            double avgWidth = (w1 + w2) * 0.5;
            double colTolerance = avgWidth * 0.45;

            if (Math.Abs(box1.Center.X - box2.Center.X) <= colTolerance)
            {
                return true;
            }

            double horizontalOverlap = Math.Max(0.0, Math.Min(box1.MaxX, box2.MaxX) - Math.Max(box1.MinX, box2.MinX));
            double minWidth = Math.Min(w1, w2);
            return (horizontalOverlap / minWidth) >= 0.40;
        }

        private static Extents2D GetCandidateExtents(FrameCandidate c)
        {
            if (c == null) return new Extents2D(0, 0, 0, 0);
            if (c.WcsBoundingBox.Width > 1e-6 && c.WcsBoundingBox.Height > 1e-6)
            {
                return c.WcsBoundingBox;
            }
            if (c.Corners != null && c.Corners.Length > 0)
            {
                var fromCorners = Extents2D.FromPoints(c.Corners);
                if (fromCorners.Width > 1e-6 && fromCorners.Height > 1e-6)
                {
                    return fromCorners;
                }
            }
            return new Extents2D(
                c.WcsBoundingBox.MinX,
                c.WcsBoundingBox.MinY,
                c.WcsBoundingBox.MinX + Math.Max(c.WidthDrawingUnits, 1.0),
                c.WcsBoundingBox.MinY + Math.Max(c.HeightDrawingUnits, 1.0));
        }

        private static Extents2D GetSheetExtents(PrintSheet s)
        {
            if (s == null) return new Extents2D(0, 0, 0, 0);
            if (s.FrameExtents.Width > 1e-6 && s.FrameExtents.Height > 1e-6)
            {
                return s.FrameExtents;
            }
            if (s.Corners != null && s.Corners.Length > 0)
            {
                var fromCorners = Extents2D.FromPoints(s.Corners);
                if (fromCorners.Width > 1e-6 && fromCorners.Height > 1e-6)
                {
                    return fromCorners;
                }
            }
            return new Extents2D(
                s.FrameExtents.MinX,
                s.FrameExtents.MinY,
                s.FrameExtents.MinX + Math.Max(s.FrameWidthUnits, 1.0),
                s.FrameExtents.MinY + Math.Max(s.FrameHeightUnits, 1.0));
        }

        /// <summary>
        /// Reconstructs closed rectangular frames from individual Line segments on a frame layer.
        /// </summary>
        public List<FrameCandidate> ReconstructRectanglesFromLines(
            IList<Tuple<Point2D, Point2D, string, string>> lineSegments,
            double minEdgeLength)
        {
            var results = new List<FrameCandidate>();
            if (lineSegments == null || lineSegments.Count < 4)
            {
                return results;
            }

            // Filter segments by minimum length
            var validLines = lineSegments
                .Where(s => s.Item1.DistanceTo(s.Item2) >= minEdgeLength)
                .ToList();

            if (validLines.Count < 4)
            {
                return results;
            }

            // Group by layer so lines on the same layer form loops
            foreach (var layerGroup in validLines.GroupBy(s => s.Item3 ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var segs = layerGroup.ToList();
                if (segs.Count < 4 || segs.Count > 400) continue;

                // Separate roughly horizontal and roughly vertical segments
                var horizontals = new List<Tuple<Point2D, Point2D, string, string>>();
                var verticals = new List<Tuple<Point2D, Point2D, string, string>>();

                foreach (var seg in segs)
                {
                    double dx = Math.Abs(seg.Item2.X - seg.Item1.X);
                    double dy = Math.Abs(seg.Item2.Y - seg.Item1.Y);
                    if (dy <= dx * 0.15)
                        horizontals.Add(seg);
                    else if (dx <= dy * 0.15)
                        verticals.Add(seg);
                }

                for (int i = 0; i < horizontals.Count; i++)
                {
                    for (int j = i + 1; j < horizontals.Count; j++)
                    {
                        var h1 = horizontals[i];
                        var h2 = horizontals[j];
                        double lenH1 = h1.Item1.DistanceTo(h1.Item2);
                        double lenH2 = h2.Item1.DistanceTo(h2.Item2);
                        if (Math.Abs(lenH1 - lenH2) > lenH1 * 0.02) continue;

                        double tol = Math.Max(lenH1 * 0.005, 5.0);
                        Point2D h1Left = h1.Item1.X <= h1.Item2.X ? h1.Item1 : h1.Item2;
                        Point2D h1Right = h1.Item1.X <= h1.Item2.X ? h1.Item2 : h1.Item1;
                        Point2D h2Left = h2.Item1.X <= h2.Item2.X ? h2.Item1 : h2.Item2;
                        Point2D h2Right = h2.Item1.X <= h2.Item2.X ? h2.Item2 : h2.Item1;

                        Point2D bl = h1Left.Y <= h2Left.Y ? h1Left : h2Left;
                        Point2D tl = h1Left.Y <= h2Left.Y ? h2Left : h1Left;
                        Point2D br = h1Right.Y <= h2Right.Y ? h1Right : h2Right;
                        Point2D tr = h1Right.Y <= h2Right.Y ? h2Right : h1Right;

                        bool hasLeftVert = verticals.Any(v =>
                            (v.Item1.DistanceTo(bl) <= tol && v.Item2.DistanceTo(tl) <= tol) ||
                            (v.Item1.DistanceTo(tl) <= tol && v.Item2.DistanceTo(bl) <= tol));
                        if (!hasLeftVert) continue;

                        bool hasRightVert = verticals.Any(v =>
                            (v.Item1.DistanceTo(br) <= tol && v.Item2.DistanceTo(tr) <= tol) ||
                            (v.Item1.DistanceTo(tr) <= tol && v.Item2.DistanceTo(br) <= tol));
                        if (!hasRightVert) continue;

                        var cornersList = new List<Point2D> { bl, br, tr, tl };
                        if (TryAnalyzeClosedPolyline(
                            cornersList,
                            null,
                            minEdgeLength,
                            out Point2D[] orderedCorners,
                            out double width,
                            out double height,
                            out double rotRad,
                            out List<string> warnings))
                        {
                            results.Add(new FrameCandidate
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                FrameHandle = h1.Item4,
                                FrameType = FrameType.LineLoop,
                                LayerName = layerGroup.Key,
                                BlockName = "",
                                Corners = orderedCorners,
                                WcsBoundingBox = Extents2D.FromPoints(orderedCorners),
                                WidthDrawingUnits = width,
                                HeightDrawingUnits = height,
                                RotationAngleRad = rotRad,
                                Confidence = 0.85,
                                Warnings = warnings
                            });
                        }
                    }
                }
            }

            return results;
        }

        private static void MergeMetadata(FrameCandidate target, FrameCandidate source)
        {
            if (target == null || source == null) return;
            if (string.IsNullOrWhiteSpace(target.ExtractedDrawingNumber) && !string.IsNullOrWhiteSpace(source.ExtractedDrawingNumber))
                target.ExtractedDrawingNumber = source.ExtractedDrawingNumber;
            if (string.IsNullOrWhiteSpace(target.ExtractedDrawingName) && !string.IsNullOrWhiteSpace(source.ExtractedDrawingName))
                target.ExtractedDrawingName = source.ExtractedDrawingName;
            if (!target.ExtractedScaleValue.HasValue && source.ExtractedScaleValue.HasValue)
                target.ExtractedScaleValue = source.ExtractedScaleValue;
            if (string.IsNullOrWhiteSpace(target.ExtractedScaleText) && !string.IsNullOrWhiteSpace(source.ExtractedScaleText))
                target.ExtractedScaleText = source.ExtractedScaleText;
        }

        private static List<Point2D> RemoveDuplicateAndCollinearVertices(IList<Point2D> vertices)
        {
            var nonDup = new List<Point2D>();
            for (int i = 0; i < vertices.Count; i++)
            {
                Point2D pt = vertices[i];
                if (nonDup.Count == 0 || nonDup[nonDup.Count - 1].DistanceTo(pt) > 1e-2)
                {
                    nonDup.Add(pt);
                }
            }

            if (nonDup.Count > 1 && nonDup[0].DistanceTo(nonDup[nonDup.Count - 1]) <= 1e-2)
            {
                nonDup.RemoveAt(nonDup.Count - 1);
            }

            if (nonDup.Count <= 4)
            {
                return nonDup;
            }

            bool changed = true;
            while (changed && nonDup.Count > 4)
            {
                changed = false;
                for (int i = 0; i < nonDup.Count; i++)
                {
                    Point2D prev = nonDup[(i + nonDup.Count - 1) % nonDup.Count];
                    Point2D curr = nonDup[i];
                    Point2D next = nonDup[(i + 1) % nonDup.Count];

                    double angle = ComputeInteriorAngleDegrees(prev, curr, next);
                    if (Math.Abs(angle - 180.0) <= CollinearToleranceDegrees)
                    {
                        nonDup.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }

            return nonDup;
        }

        private static double ComputeInteriorAngleDegrees(Point2D prev, Point2D curr, Point2D next)
        {
            double v1x = prev.X - curr.X;
            double v1y = prev.Y - curr.Y;
            double v2x = next.X - curr.X;
            double v2y = next.Y - curr.Y;

            double len1 = Math.Sqrt(v1x * v1x + v1y * v1y);
            double len2 = Math.Sqrt(v2x * v2x + v2y * v2y);
            if (len1 < 1e-9 || len2 < 1e-9)
            {
                return 180.0;
            }

            double cos = (v1x * v2x + v1y * v2y) / (len1 * len2);
            if (cos > 1.0) cos = 1.0;
            if (cos < -1.0) cos = -1.0;
            return Math.Acos(cos) * 180.0 / Math.PI;
        }

        private static double NormalizeAngleMinusPiToPi(double rad)
        {
            while (rad <= -Math.PI) rad += 2.0 * Math.PI;
            while (rad > Math.PI) rad -= 2.0 * Math.PI;
            return rad;
        }

        private static double NormalizeToMinusPiOver4ToPiOver4(double rad)
        {
            double deg = rad * 180.0 / Math.PI;
            while (deg <= -45.0) deg += 90.0;
            while (deg > 45.0) deg -= 90.0;
            return deg * Math.PI / 180.0;
        }
    }
}
