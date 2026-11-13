using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using BatchPdf.Core.Models;

namespace BatchPdf.Infrastructure.Services
{
    /// <summary>
    /// Zero-dependency OpenXML (.xlsx) workbook writer for drawing index schedules.
    /// Produces standard UTF-8 OOXML packages compatible with Microsoft Excel, WPS Office,
    /// Google Sheets, and Chinese online spreadsheet tools.
    /// </summary>
    public static class DrawingIndexExcelExporter
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void ExportToXlsx(
            string outputPath,
            IList<DrawingIndexExportRow> rows,
            string worksheetName = "图纸目录")
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("导出文件路径不能为空。", nameof(outputPath));
            }
            if (rows == null || rows.Count == 0)
            {
                throw new ArgumentException("图纸目录行不能为空。", nameof(rows));
            }

            string fullPath = Path.GetFullPath(outputPath);
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string safeSheetName = SanitizeWorksheetName(worksheetName);

            // Write atomically via temp file in same directory so failed writes never corrupt target.
            string tempPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
                {
                    WriteZipEntry(archive, "[Content_Types].xml", BuildContentTypesXml());
                    WriteZipEntry(archive, "_rels/.rels", BuildRootRelsXml());
                    WriteZipEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelsXml());
                    WriteZipEntry(archive, "xl/workbook.xml", BuildWorkbookXml(safeSheetName));
                    WriteZipEntry(archive, "xl/styles.xml", BuildStylesXml());
                    WriteZipEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(rows));
                }

                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
                File.Move(tempPath, fullPath);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch { }
            }
        }

        private static void WriteZipEntry(ZipArchive archive, string entryPath, string xmlContent)
        {
            var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
            using (var stream = entry.Open())
            using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                writer.Write(xmlContent);
            }
        }

        private static string BuildContentTypesXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                   "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">\r\n" +
                   "  <Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>\r\n" +
                   "  <Default Extension=\"xml\" ContentType=\"application/xml\"/>\r\n" +
                   "  <Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>\r\n" +
                   "  <Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>\r\n" +
                   "  <Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>\r\n" +
                   "</Types>";
        }

        private static string BuildRootRelsXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                   "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\r\n" +
                   "  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>\r\n" +
                   "</Relationships>";
        }

        private static string BuildWorkbookRelsXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                   "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\r\n" +
                   "  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>\r\n" +
                   "  <Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>\r\n" +
                   "</Relationships>";
        }

        private static string BuildWorkbookXml(string safeSheetName)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                   "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">\r\n" +
                   "  <sheets>\r\n" +
                   "    <sheet name=\"" + EscapeXmlAttribute(safeSheetName) + "\" sheetId=\"1\" r:id=\"rId1\"/>\r\n" +
                   "  </sheets>\r\n" +
                   "</workbook>";
        }

        private static string BuildStylesXml()
        {
            // Strict OOXML styleSheet element ordering: fonts -> fills -> borders -> cellStyleXfs -> cellXfs -> cellStyles
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                   "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">\r\n" +
                   "  <fonts count=\"2\">\r\n" +
                   "    <font><sz val=\"11\"/><name val=\"Microsoft YaHei\"/></font>\r\n" +
                   "    <font><b/><sz val=\"11\"/><color rgb=\"FF1F2937\"/><name val=\"Microsoft YaHei\"/></font>\r\n" +
                   "  </fonts>\r\n" +
                   "  <fills count=\"3\">\r\n" +
                   "    <fill><patternFill patternType=\"none\"/></fill>\r\n" +
                   "    <fill><patternFill patternType=\"gray125\"/></fill>\r\n" +
                   "    <fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9E1F2\"/><bgColor indexed=\"64\"/></patternFill></fill>\r\n" +
                   "  </fills>\r\n" +
                   "  <borders count=\"2\">\r\n" +
                   "    <border><left/><right/><top/><bottom/><diagonal/></border>\r\n" +
                   "    <border>\r\n" +
                   "      <left style=\"thin\"><color rgb=\"FFBFBFBF\"/></left>\r\n" +
                   "      <right style=\"thin\"><color rgb=\"FFBFBFBF\"/></right>\r\n" +
                   "      <top style=\"thin\"><color rgb=\"FFBFBFBF\"/></top>\r\n" +
                   "      <bottom style=\"thin\"><color rgb=\"FFBFBFBF\"/></bottom>\r\n" +
                   "      <diagonal/>\r\n" +
                   "    </border>\r\n" +
                   "  </borders>\r\n" +
                   "  <cellStyleXfs count=\"1\">\r\n" +
                   "    <xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/>\r\n" +
                   "  </cellStyleXfs>\r\n" +
                   "  <cellXfs count=\"4\">\r\n" +
                   "    <xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>\r\n" +
                   "    <xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>\r\n" +
                   "    <xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>\r\n" +
                   "    <xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" wrapText=\"1\"/></xf>\r\n" +
                   "  </cellXfs>\r\n" +
                   "  <cellStyles count=\"1\">\r\n" +
                   "    <cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/>\r\n" +
                   "  </cellStyles>\r\n" +
                   "</styleSheet>";
        }

        private static string BuildWorksheetXml(IList<DrawingIndexExportRow> rows)
        {
            string[] headers = DrawingIndexRowBuilder.Headers;
            int colCount = headers.Length;
            double[] colWidths = ComputeColumnWidths(headers, rows, colCount);

            var sb = new StringBuilder(4096 + rows.Count * 512);
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.AppendLine("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

            // Freeze header row (row 1) for convenient scrolling and filtering
            sb.AppendLine("  <sheetViews>");
            sb.AppendLine("    <sheetView tabSelected=\"1\" workbookViewId=\"0\">");
            sb.AppendLine("      <pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>");
            sb.AppendLine("    </sheetView>");
            sb.AppendLine("  </sheetViews>");
            sb.AppendLine("  <sheetFormatPr defaultRowHeight=\"22\"/>");

            // Auto-sized column widths
            sb.AppendLine("  <cols>");
            for (int c = 0; c < colCount; c++)
            {
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "    <col min=\"{0}\" max=\"{0}\" width=\"{1:0.##}\" customWidth=\"1\"/>\r\n",
                    c + 1,
                    colWidths[c]);
            }
            sb.AppendLine("  </cols>");

            sb.AppendLine("  <sheetData>");

            // Header row (Row 1)
            sb.AppendLine("    <row r=\"1\" ht=\"26\" customHeight=\"1\">");
            for (int c = 0; c < colCount; c++)
            {
                AppendInlineStringCell(sb, c, 1, styleId: 1, headers[c]);
            }
            sb.AppendLine("    </row>");

            // Data rows (Row 2+)
            for (int i = 0; i < rows.Count; i++)
            {
                DrawingIndexExportRow item = rows[i] ?? new DrawingIndexExportRow { Index = i + 1 };
                int excelRow = i + 2;
                int displayIndex = item.Index > 0 ? item.Index : (i + 1);
                string[] cells = item.ToCellArray();
                int maxLines = EstimateRowLineCount(cells, colWidths);
                double rowHeight = Math.Max(22.0, 8.0 + maxLines * 16.0);

                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "    <row r=\"{0}\" ht=\"{1:0.##}\" customHeight=\"1\">\r\n",
                    excelRow,
                    rowHeight);

                // Col A (0): numeric Index (style 2 = centered)
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "      <c r=\"{0}\" s=\"2\"><v>{1}</v></c>\r\n",
                    GetCellReference(0, excelRow),
                    displayIndex);

                // Cols B..G (1..6)
                for (int c = 1; c < colCount; c++)
                {
                    // Left-align DrawingNumber (1), DrawingName (2), Remarks (6); center PaperSize (3), Scale (4), IssueDate (5)
                    int styleId = (c == 1 || c == 2 || c == 6) ? 3 : 2;
                    string value = c < cells.Length ? cells[c] : "";
                    AppendInlineStringCell(sb, c, excelRow, styleId, value);
                }

                sb.AppendLine("    </row>");
            }

            sb.AppendLine("  </sheetData>");
            sb.AppendLine("  <pageMargins left=\"0.5\" right=\"0.5\" top=\"0.6\" bottom=\"0.6\" header=\"0.3\" footer=\"0.3\"/>");
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        private static void AppendInlineStringCell(StringBuilder sb, int colIndex0, int rowIndex1, int styleId, string value)
        {
            string cellRef = GetCellReference(colIndex0, rowIndex1);
            string sanitized = NormalizeExcelText(value);
            if (string.IsNullOrEmpty(sanitized))
            {
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "      <c r=\"{0}\" t=\"inlineStr\" s=\"{1}\"><is><t/></is></c>\r\n",
                    cellRef,
                    styleId);
                return;
            }

            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "      <c r=\"{0}\" t=\"inlineStr\" s=\"{1}\"><is><t xml:space=\"preserve\">{2}</t></is></c>\r\n",
                cellRef,
                styleId,
                EscapeXmlText(sanitized));
        }

        private static double[] ComputeColumnWidths(string[] headers, IList<DrawingIndexExportRow> rows, int colCount)
        {
            double[] minWidths = { 8.0, 18.0, 32.0, 12.0, 11.0, 13.0, 14.0 };
            double[] maxWidths = { 12.0, 38.0, 72.0, 22.0, 18.0, 20.0, 40.0 };
            double[] result = new double[colCount];

            for (int c = 0; c < colCount; c++)
            {
                double minW = c < minWidths.Length ? minWidths[c] : 12.0;
                double maxW = c < maxWidths.Length ? maxWidths[c] : 40.0;
                double required = EstimateExcelDisplayUnits(headers[c]) + 4.0;

                foreach (var row in rows)
                {
                    if (row == null) continue;
                    string[] cells = row.ToCellArray();
                    if (c < cells.Length)
                    {
                        double cellW = EstimateExcelDisplayUnits(cells[c]) + 3.5;
                        if (cellW > required) required = cellW;
                    }
                }

                result[c] = Math.Max(minW, Math.Min(maxW, Math.Ceiling(required)));
            }

            return result;
        }

        private static double EstimateExcelDisplayUnits(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0.0;
            string normalized = NormalizeExcelText(text);
            string[] lines = normalized.Split('\n');
            double maxLine = 0.0;

            foreach (string line in lines)
            {
                double width = 0.0;
                foreach (char ch in line)
                {
                    width += ch > 0x7F ? 2.15 : 1.12;
                }
                if (width > maxLine) maxLine = width;
            }

            return maxLine;
        }

        private static int EstimateRowLineCount(string[] cells, double[] colWidths)
        {
            int maxLines = 1;
            for (int c = 0; c < cells.Length && c < colWidths.Length; c++)
            {
                string text = NormalizeExcelText(cells[c]);
                if (string.IsNullOrEmpty(text)) continue;

                double avail = Math.Max(4.0, colWidths[c] - 2.0);
                string[] explicitLines = text.Split('\n');
                int total = 0;
                foreach (string line in explicitLines)
                {
                    if (line.Length == 0)
                    {
                        total++;
                        continue;
                    }
                    double units = 0.0;
                    foreach (char ch in line)
                    {
                        units += ch > 0x7F ? 2.15 : 1.12;
                    }
                    total += Math.Max(1, (int)Math.Ceiling(units / avail));
                }
                if (total > maxLines) maxLines = total;
            }
            return maxLines;
        }

        private static string GetCellReference(int colIndex0, int rowIndex1)
        {
            int col = colIndex0;
            var colName = new StringBuilder(3);
            do
            {
                int rem = col % 26;
                colName.Insert(0, (char)('A' + rem));
                col = (col / 26) - 1;
            }
            while (col >= 0);

            return colName.ToString() + rowIndex1.ToString(CultureInfo.InvariantCulture);
        }

        private static string SanitizeWorksheetName(string name)
        {
            string candidate = string.IsNullOrWhiteSpace(name) ? "图纸目录" : name.Trim();
            char[] invalid = { '\\', '/', '?', '*', '[', ']', ':' };
            var sb = new StringBuilder(candidate.Length);
            foreach (char ch in candidate)
            {
                if (Array.IndexOf(invalid, ch) >= 0) continue;
                if (!IsValidXmlChar(ch)) continue;
                sb.Append(ch);
            }
            string cleaned = sb.ToString().Trim();
            if (cleaned.Length == 0) cleaned = "图纸目录";
            if (cleaned.Length > 31) cleaned = cleaned.Substring(0, 31);
            return cleaned;
        }

        private static string NormalizeExcelText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string unified = raw
                .Replace("\\P", "\n")
                .Replace("\\p", "\n")
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');

            var sb = new StringBuilder(unified.Length);
            for (int i = 0; i < unified.Length; i++)
            {
                char ch = unified[i];
                if (char.IsHighSurrogate(ch))
                {
                    if (i + 1 < unified.Length && char.IsLowSurrogate(unified[i + 1]))
                    {
                        sb.Append(ch);
                        sb.Append(unified[i + 1]);
                        i++;
                    }
                    continue;
                }
                if (char.IsLowSurrogate(ch)) continue;
                if (IsValidXmlChar(ch)) sb.Append(ch);
            }
            return sb.ToString();
        }

        private static bool IsValidXmlChar(char ch)
        {
            return ch == '\t' || ch == '\n' || ch == '\r' ||
                   (ch >= 0x20 && ch <= 0xD7FF) ||
                   (ch >= 0xE000 && ch <= 0xFFFD);
        }

        private static string EscapeXmlText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length + 8);
            foreach (char ch in text)
            {
                switch (ch)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }

        private static string EscapeXmlAttribute(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length + 8);
            foreach (char ch in text)
            {
                switch (ch)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }
    }
}
