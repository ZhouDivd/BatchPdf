using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BatchPdf.Core.Configuration;
using BatchPdf.Core.Models;

namespace BatchPdf.Core.Services
{
    /// <summary>
    /// Intelligent titleblock text analyzer providing:
    /// 1. Label-Value spatial proximity pairing (e.g. "图名" -> "二层结构梁配筋图", "图号" -> "结-01", "比例" -> "1:100").
    /// 2. Heuristic extraction of drawing names in the bottom-right quadrant based on maximum font height.
    /// 3. Rich regular expressions supporting standard Chinese design institute drawing number patterns.
    /// </summary>
    public class TitleBlockTextAnalyzer
    {
        public static readonly HashSet<string> IgnoredTitleTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "图名", "图 名", "图  名", "图   名", "图纸名称", "项目图名",
            "图号", "图 号", "图  号", "图   号", "图纸编号", "工程编号",
            "比例", "比 例", "比  例", "出图比例", "图纸比例",
            "日期", "日 期", "设计", "设 计", "制图", "制 图",
            "校对", "校 对", "审核", "审 核", "审定", "审 定",
            "项目负责人", "专业负责人", "项目总监",
            "专业", "专 业", "阶段", "阶 段", "工程名称", "项目名称", "建设单位", "设计单位",
            "注册章", "出图章", "执业章", "会签", "会签栏",
            "建筑", "结构", "给排水", "电气", "暖通", "智能化", "施工图", "方案", "初设",
            "版本", "版次", "REVISION", "REV", "STATUS",
            "DWG NO", "DWG NO.", "DWG_NO", "DWGNO", "DRAWING NO", "DRAWING NO.", "SHEET NO", "NO", "NO.",
            "DWG NAME", "DRAWING NAME", "TITLE", "SCALE", "BL",
            // Common bilingual translations in parentheses or brackets
            "DRAWING TITLE", "(DRAWING TITLE)", "（DRAWING TITLE）",
            "(DRAWING NAME)", "（DRAWING NAME）",
            "(DRAWING NO)", "(DRAWING NO.)", "（DRAWING NO.）", "（DRAWING NO）",
            "(SHEET NO)", "(SHEET NO.)", "（SHEET NO.）",
            "(SCALE)", "（SCALE）",
            "(PROJECT NAME)", "（PROJECT NAME）",
            "(DATE)", "（DATE）",
            "(STATUS)", "（STATUS）",
            "(STAGE)", "（STAGE）",
            "(REV)", "（REV）", "(REVISION)", "（REVISION）"
        };

        /// <summary>
        /// Matches common Chinese design institute drawing numbers such as:
        /// 结-01, J-02, A1-01, 2023-ZS-05, 结施-01, 2022-J-01, 结-01-1, S-01.1, 结施—02, J_03.
        /// </summary>
        public static readonly Regex EnhancedDrawingNumberRegex = new Regex(
            @"^[A-Za-z0-9\u4e00-\u9fa5]{1,16}(?:[-—_#/][A-Za-z0-9\u4e00-\u9fa5]{1,10})*[-—_#]\d{1,4}(?:[-—_\.][A-Za-z0-9]{1,4})?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Standard engineering discipline prefix regex without hyphen, e.g. 结01, 建施02.
        /// </summary>
        public static readonly Regex PrefixDrawingNumberNoHyphenRegex = new Regex(
            @"^(?:结施|建施|水施|电施|暖施|通施|总施|结|建|水|电|暖|G|S|J|A|DL|SL|NL|ZS)\s*[-—_\s]*\d{1,4}(?:[\-\.][A-Za-z0-9]{1,3})?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Standard scale pattern, e.g. 1:100, 1/50, 1:55, 1:1.
        /// </summary>
        public static readonly Regex DrawingScaleTextRegex = new Regex(
            @"^(?:1\s*[:/]\s*\d{1,4}|\d{1,3}\s*[:/]\s*\d{1,4})$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Cleans AutoCAD MText formatting codes, brackets, and common special symbols.
        /// </summary>
        public static string CleanCadFormatting(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string s = Regex.Replace(raw, @"\\A\d+;|\\f[^;]+;|\\H[^;]+;|\\W[^;]+;|\\C\d+;|\\T[^;]+;|\\Q[^;]+;|\\[LlOoKk]", "");
            s = s.Replace("{", "").Replace("}", "").Replace("%%c", "Φ").Replace("%%C", "Φ").Replace("%%d", "°").Replace("%%p", "±");
            return s.Trim();
        }

        /// <summary>
        /// Checks whether a text qualifies as a drawing number.
        /// </summary>
        public static bool IsDrawingNumberText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            if (s.Length < 2 || s.Length > 35) return false;
            if (IgnoredTitleTexts.Contains(s)) return false;

            // Reject pure date formats (e.g. 2023-10-25 or 2023.05)
            if (Regex.IsMatch(s, @"^\d{4}[-—_/\.]\d{1,2}(?:[-—_/\.]\d{1,2})?$")) return false;

            if (EnhancedDrawingNumberRegex.IsMatch(s)) return true;
            if (PrefixDrawingNumberNoHyphenRegex.IsMatch(s)) return true;
            return false;
        }

        /// <summary>
        /// Checks whether a text qualifies as a scale string (e.g. 1:100).
        /// </summary>
        public static bool IsScaleText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return DrawingScaleTextRegex.IsMatch(text.Trim());
        }

        /// <summary>
        /// Checks whether a text is purely an English translation of a titleblock label
        /// (e.g. "DRAWING TITLE", "(DRAWING TITLE)", "（DRAWING TITLE）", "DRAWING NO.", "SCALE", "PROJECT NAME").
        /// Such texts must NEVER be treated as values for drawing title, number, or scale.
        /// </summary>
        public static bool IsPureLabelEnglishTranslation(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            // Strip surrounding brackets: (), （）, [], 【】
            s = Regex.Replace(s, @"^[（\(\[【\s]+|[）\)\]】\s]+$", "");
            s = Regex.Replace(s, @"[\s:：_\-\./\\#]+", " ").Trim().ToUpperInvariant();

            return s == "DRAWING TITLE" ||
                   s == "DRAWING NAME" ||
                   s == "DWG TITLE" ||
                   s == "DWG NAME" ||
                   s == "TITLE" ||
                   s == "SHEET NAME" ||
                   s == "DRAWING NO" ||
                   s == "DRAWING NUMBER" ||
                   s == "DWG NO" ||
                   s == "SHEET NO" ||
                   s == "NO" ||
                   s == "SCALE" ||
                   s == "DRAWING SCALE" ||
                   s == "BL" ||
                   s == "PROJECT NAME" ||
                   s == "PROJ NAME" ||
                   s == "PROJECT NO" ||
                   s == "DATE" ||
                   s == "DESIGNED BY" ||
                   s == "DRAWN BY" ||
                   s == "CHECKED BY" ||
                   s == "APPROVED BY" ||
                   s == "REV" ||
                   s == "REVISION" ||
                   s == "STATUS" ||
                   s == "STAGE" ||
                   s == "DISCIPLINE";
        }

        /// <summary>
        /// Detects engineering annotations, structural rebar specifications, beam/column tags,
        /// dimension formulas, or construction notes that must NEVER be misidentified as a drawing title.
        /// Example: "8%%13222(实心)+6%%13220(空心)", "4Φ20", "%%1328@100/200", "KZ1 600x600".
        /// </summary>
        public static bool IsEngineeringDetailOrRebarAnnotation(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();

            // 1. Rebar symbol font codes (%%130..%%138, %%c, %%C, Φ, φ)
            if (s.Contains("%%") || s.Contains("Φ") || s.Contains("φ")) return true;

            // 2. Rebar specifications with count/diameter/spacing
            // e.g. 8%%132, 4Φ20, 2φ16, @100, @150, @200
            if (Regex.IsMatch(s, @"\d+\s*(?:%%|\u03a6|\u03c6|HRB|HPB|d=)", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(s, @"@\s*\d{2,4}")) return true;

            // 3. Structural rebar fill/type keywords
            if (s.Contains("实心") || s.Contains("空心") || s.Contains("通长") || s.Contains("架立") ||
                s.Contains("附加筋") || s.Contains("吊筋") || s.Contains("箍筋") || s.Contains("腰筋"))
            {
                return true;
            }

            // 4. Arithmetic or formula operators common in rebar schedules (e.g. +6, +2, /4, =100)
            if (s.Contains("+") || s.Contains("=") || s.Contains("*")) return true;

            // 5. Cross-section dimensions (e.g. 400x600, 400×600, 300*500)
            if (Regex.IsMatch(s, @"\b\d{2,4}\s*[x×*]\s*\d{2,4}\b", RegexOptions.IgnoreCase)) return true;

            // 6. Concrete or rebar grades (C30, C35, C40, HRB400, HRB500, HPB300, Q235, Q345, Q355)
            if (Regex.IsMatch(s, @"^(?:C\d{2}|HRB\d{3}|HPB\d{3}|Q\d{3})\b", RegexOptions.IgnoreCase)) return true;

            // 7. Structural member designations (KZ1, KL2, WKL3, XL1, GZ1, YBZ1, GBZ1, LL1)
            if (Regex.IsMatch(s, @"^(?:KZ|KL|WKL|XL|GZ|YBZ|GBZ|LL|L)\d+\b", RegexOptions.IgnoreCase)) return true;

            // 8. Elevation marks (▽-0.050, ▽+3.600)
            if (s.Contains("▽") || Regex.IsMatch(s, @"^[\+\-]\d+\.\d{2,3}$")) return true;

            return false;
        }

        /// <summary>
        /// Checks whether a text string looks like a valid drawing title.
        /// Requires structural engineering drawing terminology or standard drawing document suffix.
        /// </summary>
        public static bool IsLikelyDrawingTitleText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            if (s.Length < 2 || s.Length > 50) return false;
            if (IgnoredTitleTexts.Contains(s)) return false;

            // Reject engineering detail / rebar / section annotations
            if (IsEngineeringDetailOrRebarAnnotation(s)) return false;

            // Reject sentences with punctuation, colons, or notes
            if (s.Contains("：") || s.Contains(":") || s.Contains("。") || s.Contains("；") || s.Contains(";")) return false;
            if (s.StartsWith("注") || s.StartsWith("1.") || s.StartsWith("2.") || s.StartsWith("3.")) return false;
            if (s.StartsWith("（1）") || s.StartsWith("(1)") || s.StartsWith("（2）") || s.StartsWith("(2)")) return false;

            // Reject design institute company info, seals, and approvals
            if (s.IndexOf("有限公司", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("设计院", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("事务所", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("专用章", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("出图章", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("注册章", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("执业章", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("未盖", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("审定", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("项目负责", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            // Reject pure date strings or pure numbers
            if (Regex.IsMatch(s, @"^\d{4}[-—_/\.]\d{1,2}(?:[-—_/\.]\d{1,2})?$")) return false;
            if (Regex.IsMatch(s, @"^\d+$")) return false;

            string compacted = Regex.Replace(s, @"\s+", "");

            // 1. Strong match: ends with standard drawing document keyword
            if (EndsWithDrawingKeyword(compacted)) return true;

            // 2. Contains standard Chinese construction drawing terms
            return compacted.Contains("平面") ||
                   compacted.Contains("立面") ||
                   compacted.Contains("剖面") ||
                   compacted.Contains("配筋") ||
                   compacted.Contains("布置") ||
                   compacted.Contains("详图") ||
                   compacted.Contains("大样") ||
                   compacted.Contains("系统图") ||
                   compacted.Contains("说明") ||
                   compacted.Contains("目录") ||
                   compacted.Contains("定位") ||
                   compacted.Contains("身表") ||
                   compacted.Contains("截面") ||
                   compacted.Contains("结构") ||
                   compacted.Contains("节点") ||
                   compacted.Contains("基础") ||
                   compacted.Contains("构件");
        }

        public static bool EndsWithDrawingKeyword(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            string t = s.Trim();
            // Strip trailing parentheses like (一), （一）, (1), （1）, (二), (2), (A), (B), (大样), (详图)
            string norm = Regex.Replace(t, @"[\(（][一二三四五六七八九十\d\w\s]+[\)）]$", "").Trim();
            if (string.IsNullOrWhiteSpace(norm)) norm = t;

            return norm.EndsWith("图", StringComparison.Ordinal) ||
                   norm.EndsWith("表", StringComparison.Ordinal) ||
                   norm.EndsWith("说明", StringComparison.Ordinal) ||
                   norm.EndsWith("大样", StringComparison.Ordinal) ||
                   norm.EndsWith("详图", StringComparison.Ordinal) ||
                   norm.EndsWith("系统图", StringComparison.Ordinal) ||
                   norm.EndsWith("目录", StringComparison.Ordinal) ||
                   norm.EndsWith("总图", StringComparison.Ordinal);
        }

        public static bool IsTitleLayer(string layer)
        {
            if (string.IsNullOrWhiteSpace(layer)) return false;
            return layer.IndexOf("图框", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   layer.IndexOf("TITLE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   layer.IndexOf("TK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   layer.IndexOf("BORDER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   layer.IndexOf("PUB_TITLE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Analyzes texts inside a frame candidate and enriches missing drawing name,
        /// drawing number, and scale using spatial proximity and geometric heuristics.
        /// </summary>
        public void EnrichFrame(
            FrameCandidate frame,
            IList<TitleTextItem> inFrameTexts,
            IList<FrameRule> rules)
        {
            if (frame == null || inFrameTexts == null || inFrameTexts.Count == 0) return;

            Extents2D box = frame.WcsBoundingBox;
            double w = box.Width;
            double h = box.Height;
            if (w <= 0 || h <= 0) return;

            // 1. Identify label items vs value items inside the frame
            TitleTextItem nameLabelItem = null;
            TitleTextItem numberLabelItem = null;
            TitleTextItem scaleLabelItem = null;

            // Direct in-text extraction (e.g. "图名: 首层平面布置图", "图号: 结-01")
            foreach (var item in inFrameTexts)
            {
                if (string.IsNullOrWhiteSpace(item.Text)) continue;
                string clean = item.Text.Trim();

                // Check Drawing Name Label / In-Text
                if (string.IsNullOrWhiteSpace(frame.ExtractedDrawingName))
                {
                    var match = Regex.Match(clean, @"^(?:图\s*名|图\s*纸\s*名\s*称|DWG\s*NAME|DRAWING\s*NAME|SHEET\s*NAME|TITLE)\s*[:：\s/\\-]\s*(.+)$", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        string val = match.Groups[1].Value.Trim();
                        if (IsPureLabelEnglishTranslation(val) || IgnoredTitleTexts.Contains(val))
                        {
                            // Bilingual label like "图名：（DRAWING TITLE）", designate this item as the label item
                            if (nameLabelItem == null || item.Height > nameLabelItem.Height)
                            {
                                nameLabelItem = item;
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(val) && val.Length >= 2 && !IsEngineeringDetailOrRebarAnnotation(val))
                        {
                            frame.ExtractedDrawingName = val;
                        }
                    }
                    else if (IsDrawingNameLabel(clean))
                    {
                        if (nameLabelItem == null || item.Height > nameLabelItem.Height)
                        {
                            nameLabelItem = item;
                        }
                    }
                }

                // Check Drawing Number Label / In-Text
                if (string.IsNullOrWhiteSpace(frame.ExtractedDrawingNumber))
                {
                    var match = Regex.Match(clean, @"^(?:图\s*号|图\s*纸\s*编\s*号|工\s*程\s*编\s*号|DWG\s*NO\.?|DRAWING\s*NO\.?|SHEET\s*NO\.?|NO\.?)\s*[:：\s/\\-]\s*(.+)$", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        string val = match.Groups[1].Value.Trim();
                        if (IsPureLabelEnglishTranslation(val) || IgnoredTitleTexts.Contains(val))
                        {
                            if (numberLabelItem == null || item.Height > numberLabelItem.Height)
                            {
                                numberLabelItem = item;
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(val) && val.Length >= 1)
                        {
                            frame.ExtractedDrawingNumber = val;
                        }
                    }
                    else if (IsDrawingNumberLabel(clean))
                    {
                        if (numberLabelItem == null || item.Height > numberLabelItem.Height)
                        {
                            numberLabelItem = item;
                        }
                    }
                }

                // Check Scale Label / In-Text
                if (!frame.ExtractedScaleValue.HasValue)
                {
                    var match = Regex.Match(clean, @"^(?:比\s*例|出\s*图\s*比\s*例|图\s*纸\s*比\s*例|SCALE|BL)\s*[:：\s/\\-]\s*(.+)$", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        string val = match.Groups[1].Value.Trim();
                        if (IsPureLabelEnglishTranslation(val) || IgnoredTitleTexts.Contains(val))
                        {
                            if (scaleLabelItem == null || item.Height > scaleLabelItem.Height)
                            {
                                scaleLabelItem = item;
                            }
                        }
                        else
                        {
                            double? sVal = ScaleParser.TryParseScaleValue(val);
                            if (sVal.HasValue && sVal.Value > 0)
                            {
                                frame.ExtractedScaleValue = sVal.Value;
                                frame.ExtractedScaleText = val;
                            }
                        }
                    }
                    else if (IsScaleLabel(clean))
                    {
                        if (scaleLabelItem == null || item.Height > scaleLabelItem.Height)
                        {
                            scaleLabelItem = item;
                        }
                    }
                }
            }

            // 2. Spatial Proximity: Right-hand side or Below search for detected labels
            if (string.IsNullOrWhiteSpace(frame.ExtractedDrawingName) && nameLabelItem != null)
            {
                var valItem = FindNearestNeighborValue(
                    nameLabelItem,
                    inFrameTexts,
                    frame,
                    cand => !IgnoredTitleTexts.Contains(cand.Text) &&
                            cand.Text.Length >= 2 && cand.Text.Length <= 55 &&
                            !IsAnyLabel(cand.Text) &&
                            !IsPureLabelEnglishTranslation(cand.Text) &&
                            !IsEngineeringDetailOrRebarAnnotation(cand.Text) &&
                            !cand.Text.Contains("有限公司") &&
                            !cand.Text.Contains("设计院"),
                    isTitleSearch: true);

                if (valItem != null)
                {
                    frame.ExtractedDrawingName = valItem.Text;
                }
            }

            if (string.IsNullOrWhiteSpace(frame.ExtractedDrawingNumber) && numberLabelItem != null)
            {
                var valItem = FindNearestNeighborValue(
                    numberLabelItem,
                    inFrameTexts,
                    frame,
                    cand => !IgnoredTitleTexts.Contains(cand.Text) &&
                            cand.Text.Length >= 1 && cand.Text.Length <= 35 &&
                            !IsAnyLabel(cand.Text) &&
                            !IsPureLabelEnglishTranslation(cand.Text) &&
                            !IsLikelyDrawingTitleText(cand.Text) &&
                            (IsDrawingNumberText(cand.Text) || Regex.IsMatch(cand.Text, @"^[A-Za-z0-9\u4e00-\u9fa5\-_#/]{1,30}$")),
                    isTitleSearch: false);

                if (valItem != null)
                {
                    frame.ExtractedDrawingNumber = valItem.Text;
                }
            }

            if (!frame.ExtractedScaleValue.HasValue && scaleLabelItem != null)
            {
                var valItem = FindNearestNeighborValue(
                    scaleLabelItem,
                    inFrameTexts,
                    frame,
                    cand => !IsPureLabelEnglishTranslation(cand.Text) && IsScaleText(cand.Text),
                    isTitleSearch: false);

                if (valItem != null)
                {
                    double? parsed = ScaleParser.TryParseScaleValue(valItem.Text);
                    if (parsed.HasValue && parsed.Value > 0)
                    {
                        frame.ExtractedScaleValue = parsed.Value;
                        frame.ExtractedScaleText = valItem.Text;
                    }
                }
            }

            // 3. Intelligent Drawing Name Extraction when no explicit label was found
            if (string.IsNullOrWhiteSpace(frame.ExtractedDrawingName))
            {
                var candidates = inFrameTexts
                    .Where(t => IsLikelyDrawingTitleText(t.Text))
                    .ToList();

                if (candidates.Count > 0)
                {
                    double brCornerX = box.MaxX;
                    double brCornerY = box.MinY;

                    var scored = candidates.Select(t =>
                    {
                        double score = 0.0;
                        string clean = t.Text.Trim();
                        string compacted = Regex.Replace(clean, @"\s+", "");

                        // Title layer bonus (+1000)
                        if (IsTitleLayer(t.Layer))
                        {
                            score += 1000.0;
                        }

                        // Ends with drawing keyword (+500)
                        if (EndsWithDrawingKeyword(compacted))
                        {
                            score += 500.0;
                        }

                        // Contains domain keywords (+300)
                        if (compacted.Contains("平面") || compacted.Contains("配筋") ||
                            compacted.Contains("布置") || compacted.Contains("详图") ||
                            compacted.Contains("大样") || compacted.Contains("系统") ||
                            compacted.Contains("身表") || compacted.Contains("说明") ||
                            compacted.Contains("定位"))
                        {
                            score += 300.0;
                        }

                        // Titleblock Zone bonus
                        bool isBrCorner = (t.Position.X >= box.MinX + w * 0.58 && t.Position.Y <= box.MinY + h * 0.28);
                        bool isRightStrip = (t.Position.X >= box.MinX + w * 0.84);
                        bool isBottomStrip = (t.Position.Y <= box.MinY + h * 0.16);

                        if (isBrCorner)
                        {
                            score += 400.0;
                            double distToBr = Math.Sqrt(Math.Pow(t.Position.X - brCornerX, 2) + Math.Pow(t.Position.Y - brCornerY, 2));
                            double diag = Math.Sqrt(w * w + h * h);
                            if (diag > 0)
                            {
                                score += (1.0 - Math.Min(1.0, distToBr / diag)) * 100.0;
                            }
                        }
                        else if (isRightStrip || isBottomStrip)
                        {
                            score += 250.0;
                        }

                        // Height bonus: larger font in titleblock indicates title vs label
                        score += Math.Min(t.Height * 1.5, 300.0);

                        // Optimal length bonus (6-28 chars)
                        if (clean.Length >= 6 && clean.Length <= 28)
                        {
                            score += 50.0;
                        }

                        return new { Item = t, Score = score };
                    })
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => x.Item.Height)
                    .ToList();

                    if (scored.Count > 0)
                    {
                        frame.ExtractedDrawingName = scored[0].Item.Text.Trim();
                    }
                }
            }

            // 4. Drawing Number Standalone Regex Match inside title zone or entire frame
            if (string.IsNullOrWhiteSpace(frame.ExtractedDrawingNumber))
            {
                var titleZoneTexts = inFrameTexts
                    .Where(t => (t.Position.X >= box.MinX + w * 0.45 && t.Position.Y <= box.MinY + h * 0.50) ||
                                (t.Position.Y <= box.MinY + h * 0.20))
                    .ToList();

                var noCandidate = titleZoneTexts.FirstOrDefault(t => IsDrawingNumberText(t.Text)) ??
                                  inFrameTexts.FirstOrDefault(t => IsDrawingNumberText(t.Text));

                if (noCandidate != null)
                {
                    frame.ExtractedDrawingNumber = noCandidate.Text;
                }
            }

            // 5. Scale Validation against Outer Sheet Dimensions
            if (!frame.ExtractedScaleValue.HasValue)
            {
                var scaleCandidates = inFrameTexts.Where(t => IsScaleText(t.Text)).ToList();
                foreach (var sc in scaleCandidates)
                {
                    double? parsed = ScaleParser.TryParseScaleValue(sc.Text);
                    if (parsed.HasValue && parsed.Value > 0)
                    {
                        double wMm = frame.WidthDrawingUnits / parsed.Value;
                        double hMm = frame.HeightDrawingUnits / parsed.Value;
                        string gbMatch = ScaleParser.GetStandardPaperShortName(wMm, hMm, 8.0);
                        if (!gbMatch.Contains("x"))
                        {
                            frame.ExtractedScaleValue = parsed.Value;
                            frame.ExtractedScaleText = sc.Text;
                            break;
                        }
                    }
                }
            }
        }

        private static TitleTextItem FindNearestNeighborValue(
            TitleTextItem labelItem,
            IList<TitleTextItem> allItems,
            FrameCandidate frame,
            Func<TitleTextItem, bool> valueFilter,
            bool isTitleSearch = false)
        {
            if (labelItem == null || allItems == null) return null;

            double lx = labelItem.Position.X;
            double ly = labelItem.Position.Y;
            double refH = Math.Max(labelItem.Height, 2.5);
            double frameW = frame.WidthDrawingUnits > 0 ? frame.WidthDrawingUnits : 841.0;
            double frameH = frame.HeightDrawingUnits > 0 ? frame.HeightDrawingUnits : 594.0;

            TitleTextItem bestCandidate = null;
            double bestScore = double.MaxValue;

            foreach (var cand in allItems)
            {
                if (cand == labelItem) continue;
                if (string.IsNullOrWhiteSpace(cand.Text)) continue;
                if (!valueFilter(cand)) continue;

                double cx = cand.Position.X;
                double cy = cand.Position.Y;

                // Case 1: Value is to the Right of Label (horizontal row)
                if (cx > lx)
                {
                    double dx = cx - lx;
                    double dy = Math.Abs(cy - ly);

                    double maxDy = Math.Max(refH * 2.5, 15.0);
                    double maxDx = Math.Min(frameW * 0.35, Math.Max(refH * 35.0, 120.0));

                    if (dy <= maxDy && dx <= maxDx)
                    {
                        double score = Math.Sqrt(dx * dx + (dy * 3.5) * (dy * 3.5));
                        if (isTitleSearch && IsLikelyDrawingTitleText(cand.Text))
                        {
                            score *= 0.5;
                        }
                        else if (!isTitleSearch && IsDrawingNumberText(cand.Text))
                        {
                            score *= 0.5;
                        }
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestCandidate = cand;
                        }
                    }
                }

                // Case 2: Value is Below Label (vertical column)
                if (cy < ly)
                {
                    double dy = ly - cy;
                    double dx = Math.Abs(cx - lx);

                    double maxDx = Math.Min(frameW * 0.25, Math.Max(refH * 25.0, 100.0));
                    double maxDy = Math.Min(frameH * 0.18, Math.Max(refH * 15.0, 80.0));

                    if (dx <= maxDx && dy <= maxDy)
                    {
                        double score = Math.Sqrt((dx * 2.5) * (dx * 2.5) + dy * dy);
                        if (isTitleSearch && IsLikelyDrawingTitleText(cand.Text))
                        {
                            score *= 0.5;
                        }
                        else if (!isTitleSearch && IsDrawingNumberText(cand.Text))
                        {
                            score *= 0.5;
                        }
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestCandidate = cand;
                        }
                    }
                }
            }

            return bestCandidate;
        }

        public static bool IsDrawingNameLabel(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = Regex.Replace(text, @"[\(（\[【][^\)）\]】]*[\)）\]】]", "");
            string norm = Regex.Replace(s, @"[\s:：_\-/\\#]+", "").ToUpperInvariant();
            if (norm == "图名" || norm == "图纸名称" || norm == "项目图名" ||
                norm == "DWGNAME" || norm == "DRAWINGNAME" || norm == "TITLE" || norm == "SHEETNAME" || norm == "DRAWINGTITLE")
            {
                return true;
            }

            string compact = Regex.Replace(text, @"[\s:：_\-/\\#]+", "").ToUpperInvariant();
            return compact == "图名DRAWINGTITLE" || compact == "图名DRAWINGNAME" || compact == "图名TITLE";
        }

        public static bool IsDrawingNumberLabel(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = Regex.Replace(text, @"[\(（\[【][^\)）\]】]*[\)）\]】]", "");
            string norm = Regex.Replace(s, @"[\s:：_\-\./\\#]+", "").ToUpperInvariant();
            if (norm == "图号" || norm == "图纸编号" || norm == "工程编号" ||
                norm == "DWGNO" || norm == "DRAWINGNO" || norm == "SHEETNO" || norm == "NO")
            {
                return true;
            }

            string compact = Regex.Replace(text, @"[\s:：_\-\./\\#]+", "").ToUpperInvariant();
            return compact == "图号DRAWINGNO" || compact == "图号DWGNO" || compact == "图号SHEETNO" || compact == "图号NO";
        }

        public static bool IsScaleLabel(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = Regex.Replace(text, @"[\(（\[【][^\)）\]】]*[\)）\]】]", "");
            string norm = Regex.Replace(s, @"[\s:：_\-/\\#]+", "").ToUpperInvariant();
            if (norm == "比例" || norm == "出图比例" || norm == "图纸比例" || norm == "SCALE" || norm == "BL")
            {
                return true;
            }

            string compact = Regex.Replace(text, @"[\s:：_\-/\\#]+", "").ToUpperInvariant();
            return compact == "比例SCALE" || compact == "比例BL" || compact == "出图比例SCALE";
        }

        public static bool IsAnyLabel(string text)
        {
            return IsDrawingNameLabel(text) || IsDrawingNumberLabel(text) || IsScaleLabel(text);
        }
    }
}
