using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdf.Core.Services
{
    public sealed class DrawingIndexTableLayout
    {
        public double UnitBase { get; set; }
        public double DataTextHeight { get; set; }
        public double HeaderTextHeight { get; set; }
        public double HorzMargin { get; set; }
        public double VertMargin { get; set; }
        public double[] ColumnWidths { get; set; }
        public double[] RowHeights { get; set; }
        public int[] RowMaxLineCounts { get; set; }
    }

    /// <summary>
    /// Computes adaptive column widths and row heights for AutoCAD drawing index tables
    /// so long Chinese/English drawing numbers and titles fit on a single row where reasonable
    /// and expand row height automatically whenever text wraps to multiple lines.
    /// </summary>
    public static class DrawingIndexTableLayoutService
    {
        // [序号, 图号, 图纸名称, 图幅, 比例, 出图日期, 备注] in mm on plotted paper
        private static readonly double[] MinPaperColumnWidthsMm = { 14.0, 32.0, 65.0, 22.0, 20.0, 24.0, 24.0 };
        private static readonly double[] MaxPaperColumnWidthsMm = { 26.0, 95.0, 185.0, 55.0, 42.0, 45.0, 90.0 };

        public static DrawingIndexTableLayout ComputeLayout(
            IList<string> headers,
            IList<string[]> dataRows,
            double scaleFactor)
        {
            if (headers == null) throw new ArgumentNullException(nameof(headers));
            int colCount = headers.Count;
            if (colCount == 0) throw new ArgumentException("Headers must not be empty.", nameof(headers));

            double unitBase = scaleFactor > 0.0 ? scaleFactor : 100.0;
            double dataTextHeight = Math.Max(2.5, 3.5 * unitBase);
            double headerTextHeight = dataTextHeight * 1.2;
            double horzMargin = dataTextHeight * 0.65;
            double vertMargin = dataTextHeight * 0.50;

            double[] colWidths = new double[colCount];
            for (int c = 0; c < colCount; c++)
            {
                double minPaperW = c < MinPaperColumnWidthsMm.Length ? MinPaperColumnWidthsMm[c] : 24.0;
                double maxPaperW = c < MaxPaperColumnWidthsMm.Length ? MaxPaperColumnWidthsMm[c] : 90.0;
                double minW = minPaperW * unitBase;
                double maxW = Math.Max(minW, maxPaperW * unitBase);

                // Measure header width with conservative CAD font buffer
                double requiredContentW = EstimateMaxLineWidth(headers[c], headerTextHeight) * 1.15;

                if (dataRows != null)
                {
                    foreach (var row in dataRows)
                    {
                        if (row == null || c >= row.Length) continue;
                        double cellW = EstimateMaxLineWidth(row[c], dataTextHeight) * 1.15;
                        if (cellW > requiredContentW)
                        {
                            requiredContentW = cellW;
                        }
                    }
                }

                double idealWidth = requiredContentW + 2.0 * horzMargin;
                colWidths[c] = Math.Max(minW, Math.Min(maxW, idealWidth));
            }

            int totalRows = 1 + (dataRows != null ? dataRows.Count : 0);
            double[] rowHeights = new double[totalRows];
            int[] rowLineCounts = new int[totalRows];

            // Header row height
            int headerLines = 1;
            for (int c = 0; c < colCount; c++)
            {
                double availW = Math.Max(headerTextHeight, colWidths[c] - 2.0 * horzMargin);
                int lines = EstimateWrappedLineCount(headers[c], headerTextHeight, availW);
                if (lines > headerLines) headerLines = lines;
            }
            rowLineCounts[0] = headerLines;
            rowHeights[0] = ComputeRowHeight(headerLines, headerTextHeight, vertMargin);

            // Data rows height (per row, adaptive to wrapped lines across all cells)
            if (dataRows != null)
            {
                for (int r = 0; r < dataRows.Count; r++)
                {
                    var row = dataRows[r];
                    int maxLines = 1;
                    if (row != null)
                    {
                        for (int c = 0; c < Math.Min(colCount, row.Length); c++)
                        {
                            double availW = Math.Max(dataTextHeight, colWidths[c] - 2.0 * horzMargin);
                            int lines = EstimateWrappedLineCount(row[c], dataTextHeight, availW);
                            if (lines > maxLines) maxLines = lines;
                        }
                    }
                    rowLineCounts[r + 1] = maxLines;
                    rowHeights[r + 1] = ComputeRowHeight(maxLines, dataTextHeight, vertMargin);
                }
            }

            return new DrawingIndexTableLayout
            {
                UnitBase = unitBase,
                DataTextHeight = dataTextHeight,
                HeaderTextHeight = headerTextHeight,
                HorzMargin = horzMargin,
                VertMargin = vertMargin,
                ColumnWidths = colWidths,
                RowHeights = rowHeights,
                RowMaxLineCounts = rowLineCounts
            };
        }

        public static double ComputeRowHeight(int lineCount, double textHeight, double vertMargin)
        {
            int lines = Math.Max(1, lineCount);
            double vertPad = Math.Max(vertMargin, textHeight * 0.85);
            // AutoCAD MText default line baseline spacing inside AcDbTable is ~1.67 * textHeight
            double textBlockHeight = lines == 1
                ? textHeight
                : (1.72 * lines - 0.72) * textHeight;
            return textBlockHeight + 2.0 * vertPad;
        }

        public static double EstimateMaxLineWidth(string text, double textHeight)
        {
            if (string.IsNullOrEmpty(text) || textHeight <= 0) return 0.0;
            string[] lines = SplitExplicitLines(text);
            double maxW = 0.0;
            foreach (string line in lines)
            {
                double w = EstimateSingleLineWidth(line, textHeight);
                if (w > maxW) maxW = w;
            }
            return maxW;
        }

        public static int EstimateWrappedLineCount(string text, double textHeight, double availableWidth)
        {
            if (string.IsNullOrWhiteSpace(text)) return 1;
            if (availableWidth <= 0 || textHeight <= 0) return 1;

            // Use slightly tighter available width (93%) to account for AutoCAD SHX/TTF font metrics variance
            double effectiveWidth = Math.Max(textHeight, availableWidth * 0.93);
            string[] explicitLines = SplitExplicitLines(text);
            int totalLines = 0;

            foreach (string rawLine in explicitLines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    totalLines++;
                    continue;
                }

                int wrappedForThisLine = 1;
                double currentWidth = 0.0;

                // Tokenize so ASCII words wrap at spaces like AutoCAD MText does, while CJK chars wrap per glyph
                foreach (string token in TokenizeForWrap(line))
                {
                    double tokenWidth = EstimateSingleLineWidth(token, textHeight);
                    if (token == " ")
                    {
                        if (currentWidth > 0 && currentWidth + tokenWidth <= effectiveWidth)
                        {
                            currentWidth += tokenWidth;
                        }
                        else if (currentWidth > effectiveWidth)
                        {
                            wrappedForThisLine++;
                            currentWidth = 0.0;
                        }
                        continue;
                    }

                    if (currentWidth > 0 && currentWidth + tokenWidth <= effectiveWidth)
                    {
                        currentWidth += tokenWidth;
                    }
                    else if (currentWidth == 0 && tokenWidth <= effectiveWidth)
                    {
                        currentWidth = tokenWidth;
                    }
                    else if (tokenWidth > effectiveWidth)
                    {
                        // Very long unbroken token (e.g. a long code without spaces): wrap character by character
                        if (currentWidth > 0)
                        {
                            wrappedForThisLine++;
                            currentWidth = 0.0;
                        }
                        foreach (char ch in token)
                        {
                            double cw = EstimateCharWidth(ch, textHeight);
                            if (currentWidth > 0 && currentWidth + cw > effectiveWidth)
                            {
                                wrappedForThisLine++;
                                currentWidth = cw;
                            }
                            else
                            {
                                currentWidth += cw;
                            }
                        }
                    }
                    else
                    {
                        wrappedForThisLine++;
                        currentWidth = tokenWidth;
                    }
                }

                totalLines += wrappedForThisLine;
            }

            return Math.Max(1, totalLines);
        }

        private static string[] SplitExplicitLines(string text)
        {
            return (text ?? "")
                .Replace("\\P", "\n")
                .Replace("\\p", "\n")
                .Replace("\r\n", "\n")
                .Split('\n');
        }

        private static IEnumerable<string> TokenizeForWrap(string line)
        {
            int i = 0;
            while (i < line.Length)
            {
                char ch = line[i];
                if (ch == ' ')
                {
                    yield return " ";
                    while (i < line.Length && line[i] == ' ') i++;
                }
                else if (IsWideCharacter(ch))
                {
                    yield return ch.ToString();
                    i++;
                }
                else
                {
                    int start = i;
                    while (i < line.Length && line[i] != ' ' && !IsWideCharacter(line[i]))
                    {
                        i++;
                    }
                    yield return line.Substring(start, i - start);
                }
            }
        }

        private static double EstimateSingleLineWidth(string text, double textHeight)
        {
            if (string.IsNullOrEmpty(text)) return 0.0;
            double total = 0.0;
            for (int i = 0; i < text.Length; i++)
            {
                total += EstimateCharWidth(text[i], textHeight);
            }
            return total;
        }

        private static double EstimateCharWidth(char ch, double textHeight)
        {
            if (IsWideCharacter(ch)) return textHeight * 1.05;
            if (ch == ' ') return textHeight * 0.38;
            if (ch == 'I' || ch == 'i' || ch == 'l' || ch == '1' || ch == '.' || ch == ',' ||
                ch == ':' || ch == ';' || ch == '\'' || ch == '|' || ch == '!')
            {
                return textHeight * 0.45;
            }
            if (ch == 'W' || ch == 'M' || ch == 'm' || ch == 'w' || ch == 'G' || ch == 'Q' ||
                ch == 'O' || ch == 'D' || ch == 'R' || ch == 'N' || ch == 'H' || ch == 'K' ||
                ch == 'U' || ch == '%' || ch == '&' || ch == '@')
            {
                return textHeight * 0.85;
            }
            if (char.IsUpper(ch) || char.IsDigit(ch))
            {
                return textHeight * 0.76;
            }
            if (ch == '-' || ch == '/' || ch == '\\' || ch == '(' || ch == ')')
            {
                return textHeight * 0.50;
            }
            return textHeight * 0.62;
        }

        private static bool IsWideCharacter(char ch)
        {
            return ch > 0x7F;
        }
    }
}
