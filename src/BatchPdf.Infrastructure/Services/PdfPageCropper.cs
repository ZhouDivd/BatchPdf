using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BatchPdf.Infrastructure.Services
{
    public class PdfCropResult
    {
        public int TotalPages { get; set; }
        public int CroppedPages { get; set; }
        public int SkippedStandardPages { get; set; }
        public List<string> PageDetails { get; set; } = new List<string>();
    }

    /// <summary>
    /// Automatically detects and crops excess white borders on PDF pages plotted from non-standard or lengthened CAD frames,
    /// while leaving standard frames (whose frame dimensions already match the paper size) untouched.
    /// Preserves all vector streams, fonts, and OCG layers byte-for-byte without external libraries.
    /// </summary>
    public class PdfPageCropper
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding("iso-8859-1");

        private static readonly Regex ObjHeaderRegex = new Regex(
            @"(?:^|\r|\n)\s*(\d+)\s+0\s+obj\b",
            RegexOptions.Compiled);

        private static readonly Regex TypePageRegex = new Regex(
            @"/Type\s*/Page\b(?!s)",
            RegexOptions.Compiled);

        private static readonly Regex TypeCatalogRegex = new Regex(
            @"/Type\s*/Catalog\b",
            RegexOptions.Compiled);

        private static readonly Regex MediaBoxRegex = new Regex(
            @"/MediaBox\s*\[\s*([-\d\.]+)\s+([-\d\.]+)\s+([-\d\.]+)\s+([-\d\.]+)\s*\]",
            RegexOptions.Compiled);

        private static readonly Regex OrigMediaBoxRegex = new Regex(
            @"/OrigMediaBox\s*\[\s*([-\d\.]+)\s+([-\d\.]+)\s+([-\d\.]+)\s+([-\d\.]+)\s*\]",
            RegexOptions.Compiled);

        private static readonly Regex CropBoxRegex = new Regex(
            @"/CropBox\s*\[[^\]]+\]",
            RegexOptions.Compiled);

        private static readonly Regex ViewportBBoxRegex = new Regex(
            @"/BBox\s*\[\s*([-\d\.]+)\s+([-\d\.]+)\s+([-\d\.]+)\s+([-\d\.]+)\s*\]",
            RegexOptions.Compiled);

        private class PdfObjectRecord
        {
            public int ObjectId { get; set; }
            public string HeaderPart { get; set; }
            public string RawStreamBlock { get; set; }
        }

        public PdfCropResult CropPdfFile(
            string pdfPath,
            double marginMm = 0.0,
            double? fallbackFrameWidthMm = null,
            double? fallbackFrameHeightMm = null,
            double standardToleranceMm = 6.0)
        {
            var result = new PdfCropResult();
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                return result;
            }

            byte[] rawBytes = File.ReadAllBytes(pdfPath);
            if (rawBytes.Length == 0)
            {
                return result;
            }

            string rawContent = Latin1.GetString(rawBytes);
            List<PdfObjectRecord> objects = ExtractObjectsFromPdf(rawContent);
            if (objects.Count == 0)
            {
                return result;
            }

            int rootObjId = -1;
            int infoObjId = -1;
            int trailerPos = rawContent.LastIndexOf("trailer", StringComparison.Ordinal);
            string trailerSection = trailerPos >= 0 ? rawContent.Substring(trailerPos) : rawContent;

            Match rootMatch = Regex.Match(trailerSection, @"/Root\s+(\d+)\s+0\s+R\b");
            if (rootMatch.Success)
            {
                int.TryParse(rootMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out rootObjId);
            }

            Match infoMatch = Regex.Match(trailerSection, @"/Info\s+(\d+)\s+0\s+R\b");
            if (infoMatch.Success)
            {
                int.TryParse(infoMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out infoObjId);
            }

            if (rootObjId <= 0)
            {
                var catalogObj = objects.FirstOrDefault(o => TypeCatalogRegex.IsMatch(o.HeaderPart));
                if (catalogObj != null)
                {
                    rootObjId = catalogObj.ObjectId;
                }
            }

            foreach (var obj in objects)
            {
                if (!TypePageRegex.IsMatch(obj.HeaderPart))
                {
                    continue;
                }

                result.TotalPages++;

                Match origMediaMatch = OrigMediaBoxRegex.Match(obj.HeaderPart);
                Match mediaMatch = origMediaMatch.Success ? origMediaMatch : MediaBoxRegex.Match(obj.HeaderPart);
                if (!mediaMatch.Success)
                {
                    result.SkippedStandardPages++;
                    continue;
                }

                if (!double.TryParse(mediaMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rawM0) ||
                    !double.TryParse(mediaMatch.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rawM1) ||
                    !double.TryParse(mediaMatch.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rawM2) ||
                    !double.TryParse(mediaMatch.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rawM3))
                {
                    result.SkippedStandardPages++;
                    continue;
                }

                double m0 = Math.Min(rawM0, rawM2);
                double m1 = Math.Min(rawM1, rawM3);
                double m2 = Math.Max(rawM0, rawM2);
                double m3 = Math.Max(rawM1, rawM3);

                double b0, b1, b2, b3;
                bool hasFrameBox = false;

                // 1. Check if /Page dictionary contains AutoCAD's viewport /BBox [llx lly urx ury]
                Match bboxMatch = ViewportBBoxRegex.Match(obj.HeaderPart);
                if (bboxMatch.Success &&
                    double.TryParse(bboxMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rawB0) &&
                    double.TryParse(bboxMatch.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rawB1) &&
                    double.TryParse(bboxMatch.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rawB2) &&
                    double.TryParse(bboxMatch.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rawB3))
                {
                    b0 = Math.Min(rawB0, rawB2);
                    b1 = Math.Min(rawB1, rawB3);
                    b2 = Math.Max(rawB0, rawB2);
                    b3 = Math.Max(rawB1, rawB3);
                    hasFrameBox = true;
                }
                else if (fallbackFrameWidthMm.HasValue && fallbackFrameHeightMm.HasValue &&
                         fallbackFrameWidthMm.Value > 0 && fallbackFrameHeightMm.Value > 0)
                {
                    // 2. Fallback to centering expected frame size (mm -> pt) inside MediaBox
                    double fwPt = fallbackFrameWidthMm.Value * 72.0 / 25.4;
                    double fhPt = fallbackFrameHeightMm.Value * 72.0 / 25.4;
                    double pageWPt = m2 - m0;
                    double pageHPt = m3 - m1;

                    bool pageLandscape = pageWPt >= pageHPt;
                    bool frameLandscape = fwPt >= fhPt;
                    if (pageLandscape != frameLandscape)
                    {
                        double tmp = fwPt;
                        fwPt = fhPt;
                        fhPt = tmp;
                    }

                    b0 = m0 + (pageWPt - fwPt) * 0.5;
                    b1 = m1 + (pageHPt - fhPt) * 0.5;
                    b2 = b0 + fwPt;
                    b3 = b1 + fhPt;
                    hasFrameBox = true;
                }
                else
                {
                    b0 = b1 = b2 = b3 = 0;
                }

                if (!hasFrameBox)
                {
                    result.SkippedStandardPages++;
                    continue;
                }

                double pageW_mm = (m2 - m0) * 25.4 / 72.0;
                double pageH_mm = (m3 - m1) * 25.4 / 72.0;
                double frameW_mm = (b2 - b0) * 25.4 / 72.0;
                double frameH_mm = (b3 - b1) * 25.4 / 72.0;

                // Sanity check
                if (frameW_mm <= 10.0 || frameH_mm <= 10.0 ||
                    frameW_mm > pageW_mm + 2.0 || frameH_mm > pageH_mm + 2.0)
                {
                    result.SkippedStandardPages++;
                    continue;
                }

                // Standard Frame check: if paper dimensions match frame dimensions within tolerance, do not crop
                if (Math.Abs(pageW_mm - frameW_mm) <= standardToleranceMm &&
                    Math.Abs(pageH_mm - frameH_mm) <= standardToleranceMm)
                {
                    result.SkippedStandardPages++;
                    continue;
                }

                double marginPt = Math.Max(0.0, marginMm) * 72.0 / 25.4;
                double c0 = Math.Max(m0, b0 - marginPt);
                double c1 = Math.Max(m1, b1 - marginPt);
                double c2 = Math.Min(m2, b2 + marginPt);
                double c3 = Math.Min(m3, b3 + marginPt);

                string origBoxStr = string.Format(
                    CultureInfo.InvariantCulture,
                    "[{0:F2} {1:F2} {2:F2} {3:F2}]",
                    m0, m1, m2, m3);

                string newBoxStr = string.Format(
                    CultureInfo.InvariantCulture,
                    "[{0:F2} {1:F2} {2:F2} {3:F2}]",
                    c0, c1, c2, c3);

                string cleanedHeader = CropBoxRegex.Replace(obj.HeaderPart, "");
                cleanedHeader = OrigMediaBoxRegex.Replace(cleanedHeader, "");
                obj.HeaderPart = MediaBoxRegex.Replace(
                    cleanedHeader,
                    "/OrigMediaBox " + origBoxStr + " /MediaBox " + newBoxStr + " /CropBox " + newBoxStr,
                    1);

                result.CroppedPages++;
                double newW_mm = (c2 - c0) * 25.4 / 72.0;
                double newH_mm = (c3 - c1) * 25.4 / 72.0;
                result.PageDetails.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "页面尺寸由 {0:F0}x{1:F0}mm 裁剪为 {2:F0}x{3:F0}mm (留白 {4:G}mm)",
                    pageW_mm,
                    pageH_mm,
                    newW_mm,
                    newH_mm,
                    Math.Max(0.0, marginMm)));
            }

            if (result.CroppedPages > 0 && rootObjId > 0)
            {
                RewritePdfWithUpdatedObjects(pdfPath, objects, rootObjId, infoObjId);
            }

            return result;
        }

        private static void RewritePdfWithUpdatedObjects(
            string pdfPath,
            List<PdfObjectRecord> objects,
            int rootObjId,
            int infoObjId)
        {
            int maxObjId = objects.Max(o => o.ObjectId);
            int totalXrefSize = maxObjId + 1;

            using (var ms = new MemoryStream())
            {
                void WriteLatin1(string s)
                {
                    byte[] b = Latin1.GetBytes(s);
                    ms.Write(b, 0, b.Length);
                }

                WriteLatin1("%PDF-1.7\r\n%\xE2\xE3\xCF\xD3\r\n");

                var offsetsById = new Dictionary<int, long>();
                foreach (var obj in objects.OrderBy(o => o.ObjectId))
                {
                    offsetsById[obj.ObjectId] = ms.Position;
                    WriteLatin1(string.Format(CultureInfo.InvariantCulture, "{0} 0 obj\r\n", obj.ObjectId));
                    WriteLatin1(obj.HeaderPart.Trim());
                    WriteLatin1("\r\n");
                    if (!string.IsNullOrEmpty(obj.RawStreamBlock))
                    {
                        WriteLatin1(obj.RawStreamBlock);
                        if (!obj.RawStreamBlock.EndsWith("\n"))
                        {
                            WriteLatin1("\r\n");
                        }
                    }
                    WriteLatin1("endobj\r\n");
                }

                long startXrefOffset = ms.Position;
                WriteLatin1(string.Format(CultureInfo.InvariantCulture, "xref\r\n0 {0}\r\n", totalXrefSize));
                WriteLatin1("0000000000 65535 f \r\n");

                for (int id = 1; id < totalXrefSize; id++)
                {
                    if (offsetsById.TryGetValue(id, out long pos))
                    {
                        WriteLatin1(string.Format(CultureInfo.InvariantCulture, "{0:D10} 00000 n \r\n", pos));
                    }
                    else
                    {
                        WriteLatin1("0000000000 65535 f \r\n");
                    }
                }

                if (infoObjId > 0)
                {
                    WriteLatin1(string.Format(
                        CultureInfo.InvariantCulture,
                        "trailer\r\n<< /Size {0} /Root {1} 0 R /Info {2} 0 R >>\r\nstartxref\r\n{3}\r\n%%EOF\r\n",
                        totalXrefSize,
                        rootObjId,
                        infoObjId,
                        startXrefOffset));
                }
                else
                {
                    WriteLatin1(string.Format(
                        CultureInfo.InvariantCulture,
                        "trailer\r\n<< /Size {0} /Root {1} 0 R >>\r\nstartxref\r\n{2}\r\n%%EOF\r\n",
                        totalXrefSize,
                        rootObjId,
                        startXrefOffset));
                }

                File.WriteAllBytes(pdfPath, ms.ToArray());
            }
        }

        private static List<PdfObjectRecord> ExtractObjectsFromPdf(string rawContent)
        {
            var results = new List<PdfObjectRecord>();

            int xrefPos = rawContent.LastIndexOf("\nxref", StringComparison.Ordinal);
            if (xrefPos < 0) xrefPos = rawContent.LastIndexOf("\rxref", StringComparison.Ordinal);
            if (xrefPos < 0) xrefPos = rawContent.Length;

            int searchPos = 0;
            while (searchPos < xrefPos)
            {
                Match m = ObjHeaderRegex.Match(rawContent, searchPos);
                if (!m.Success || m.Index >= xrefPos)
                {
                    break;
                }

                int objId = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int bodyStart = m.Index + m.Length;

                int nextEndObj = rawContent.IndexOf("endobj", bodyStart, StringComparison.Ordinal);
                if (nextEndObj < 0) break;

                int streamIdx = FindStreamKeywordOutsideInner(rawContent, bodyStart, nextEndObj);
                if (streamIdx >= 0)
                {
                    string headerPart = rawContent.Substring(bodyStart, streamIdx - bodyStart);

                    int streamDataStart = streamIdx + "stream".Length;
                    if (streamDataStart < rawContent.Length && rawContent[streamDataStart] == '\r') streamDataStart++;
                    if (streamDataStart < rawContent.Length && rawContent[streamDataStart] == '\n') streamDataStart++;

                    int endStreamSearchStart = streamDataStart;
                    Match lenMatch = Regex.Match(headerPart, @"/Length\s+(\d+)(?!\s+0\s+R)");
                    if (lenMatch.Success && int.TryParse(lenMatch.Groups[1].Value, out int declaredLen) && declaredLen > 0)
                    {
                        endStreamSearchStart = Math.Min(rawContent.Length, streamDataStart + declaredLen);
                    }

                    int endStreamIdx = rawContent.IndexOf("endstream", endStreamSearchStart, StringComparison.Ordinal);
                    if (endStreamIdx < 0)
                    {
                        endStreamIdx = rawContent.IndexOf("endstream", streamDataStart, StringComparison.Ordinal);
                    }
                    if (endStreamIdx < 0) break;

                    int endStreamEnd = endStreamIdx + "endstream".Length;
                    string rawStreamBlock = rawContent.Substring(streamIdx, endStreamEnd - streamIdx);

                    int realEndObj = rawContent.IndexOf("endobj", endStreamEnd, StringComparison.Ordinal);
                    if (realEndObj < 0) break;

                    results.Add(new PdfObjectRecord
                    {
                        ObjectId = objId,
                        HeaderPart = headerPart,
                        RawStreamBlock = rawStreamBlock
                    });
                    searchPos = realEndObj + "endobj".Length;
                }
                else
                {
                    string headerPart = rawContent.Substring(bodyStart, nextEndObj - bodyStart);
                    results.Add(new PdfObjectRecord
                    {
                        ObjectId = objId,
                        HeaderPart = headerPart,
                        RawStreamBlock = null
                    });
                    searchPos = nextEndObj + "endobj".Length;
                }
            }

            return results;
        }

        private static int FindStreamKeywordOutsideInner(string content, int start, int limit)
        {
            int idx = start;
            while (idx < limit)
            {
                int pos = content.IndexOf("stream", idx, limit - idx, StringComparison.Ordinal);
                if (pos < 0) return -1;

                bool precededByEnd = pos >= 3 && content.Substring(pos - 3, 3) == "end";
                int after = pos + 6;
                bool followedByNewline = after < content.Length && (content[after] == '\r' || content[after] == '\n');

                if (!precededByEnd && followedByNewline)
                {
                    return pos;
                }
                idx = pos + 6;
            }
            return -1;
        }
    }
}
