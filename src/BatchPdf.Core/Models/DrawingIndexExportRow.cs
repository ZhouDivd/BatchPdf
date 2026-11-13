using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdf.Core.Models
{
    /// <summary>
    /// Editable row representation of a drawing index entry shared across UI preview/confirmation,
    /// Excel (.xlsx) export, and AutoCAD native table generation.
    /// </summary>
    public sealed class DrawingIndexExportRow
    {
        public int Index { get; set; }
        public PrintSheet SourceSheet { get; set; }
        public string DrawingNumber { get; set; }
        public string DrawingName { get; set; }
        public string PaperSize { get; set; }
        public string Scale { get; set; }
        public string IssueDate { get; set; }
        public string Remarks { get; set; }

        public string[] ToCellArray()
        {
            return new[]
            {
                Index.ToString(),
                (DrawingNumber ?? "").Trim(),
                (DrawingName ?? "").Trim(),
                (PaperSize ?? "").Trim(),
                (Scale ?? "").Trim(),
                (IssueDate ?? "").Trim(),
                (Remarks ?? "").Trim()
            };
        }
    }

    public static class DrawingIndexRowBuilder
    {
        public static readonly string[] Headers =
        {
            "序号", "图号", "图纸名称", "图幅", "比例", "出图日期", "备注"
        };

        public static List<PrintSheet> SelectActiveSheets(IList<PrintSheet> sheets)
        {
            if (sheets == null || sheets.Count == 0)
            {
                return new List<PrintSheet>();
            }

            var selected = sheets
                .Where(s => s != null && s.IsSelected)
                .OrderBy(s => s.OrderIndex)
                .ToList();

            if (selected.Count > 0)
            {
                return selected;
            }

            return sheets
                .Where(s => s != null)
                .OrderBy(s => s.OrderIndex)
                .ToList();
        }

        public static List<DrawingIndexExportRow> BuildFromSheets(IList<PrintSheet> sheets, string defaultIssueDate = null)
        {
            var activeSheets = SelectActiveSheets(sheets);
            string issueDate = string.IsNullOrWhiteSpace(defaultIssueDate)
                ? DateTime.Now.ToString("yyyy.MM")
                : defaultIssueDate.Trim();

            var rows = new List<DrawingIndexExportRow>(activeSheets.Count);
            for (int i = 0; i < activeSheets.Count; i++)
            {
                PrintSheet s = activeSheets[i];
                string paperStr = !string.IsNullOrWhiteSpace(s.TargetPaperLocaleName)
                    ? ExtractCleanPaperName(s.TargetPaperLocaleName)
                    : (s.FormattedGeometryInfo ?? "");

                string scaleStr = s.ScaleValue.HasValue && s.ScaleValue.Value > 0
                    ? string.Format("1:{0:G}", s.ScaleValue.Value)
                    : (s.FitToPaper ? "Fit" : "-");

                rows.Add(new DrawingIndexExportRow
                {
                    Index = i + 1,
                    SourceSheet = s,
                    DrawingNumber = NormalizeInlineText(s.DrawingNumber),
                    DrawingName = NormalizeInlineText(s.DrawingName),
                    PaperSize = (paperStr ?? "").Trim(),
                    Scale = scaleStr,
                    IssueDate = issueDate,
                    Remarks = ""
                });
            }

            return rows;
        }

        public static string ExtractCleanPaperName(string localeName)
        {
            if (string.IsNullOrWhiteSpace(localeName)) return "";
            string suffix = localeName.IndexOf("加长", StringComparison.OrdinalIgnoreCase) >= 0 ? "加长" : "";
            string[] bases = { "A0", "A1", "A2", "A3", "A4" };
            foreach (string b in bases)
            {
                if (localeName.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return b + suffix;
                }
            }

            return localeName.Split('(')[0].Trim();
        }

        private static string NormalizeInlineText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return value
                .Replace("\\P", "\n")
                .Replace("\\p", "\n")
                .Replace("\r\n", "\n")
                .Trim();
        }
    }
}
