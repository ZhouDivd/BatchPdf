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
            "图名", "图 名", "图  名", "图   名", "图纸名称", "项目图名", "工程图名",
            "图号", "图 号", "图  号", "图   号", "图纸编号", "工程编号",
            "比例", "比 例", "比  例", "出图比例", "图纸比例",
            "日期", "日 期", "出图日期", "设计", "设 计", "制图", "制 图", "描图", "描 图",
            "校对", "校 对", "审核", "审 核", "审定", "审 定", "批准", "批 准", "核定", "核 定", "审查", "审 查",
            "项目负责人", "专业负责人", "项目总监", "注册建筑师", "注册结构师", "注册工程师",
            "专业", "专 业", "阶段", "阶 段", "设计阶段", "出图阶段", "工程阶段", "施工图阶段", "方案阶段", "初设阶段", "初步设计",
            "工程名称", "项目名称", "子项名称", "子项编号", "子项", "分项", "分项名称", "建设单位", "设计单位", "施工图审查",
            "注册章", "出图章", "执业章", "会签", "会签栏", "图别", "图幅", "工种", "设计号", "工程号", "合同号",
            "建筑", "结构", "给排水", "电气", "暖通", "智能化", "总图", "施工图", "方案", "初设",
            "版本", "版次", "备注", "签字", "签名", "实名", "职责", "图纸目录",
            "REVISION", "REV", "REV.", "STATUS", "STAGE", "PHASE", "DISCIPLINE", "ISSUE", "SUBMISSION",
            "KEY PLAN", "KEYPLAN", "LEGEND", "LOCATION PLAN", "REMARKS", "REVISION DESCRIPTION", "LEAD CONSULTANT",
            "JOB", "JOB NO", "JOB NO.", "DRAWING STATUS", "BY",
            "DWG NO", "DWG NO.", "DWG_NO", "DWGNO", "DRAWING NO", "DRAWING NO.", "DRAWING NUMBER", "DWG NUMBER",
            "SHEET NO", "SHEET NO.", "SHEET NUMBER", "NO", "NO.", "NUMBER",
            "DWG NAME", "DRAWING NAME", "DRAWING TITLE", "DWG TITLE", "SHEET NAME", "SHEET TITLE",
            "TITLE", "TITLE:", "TITLE :", "NAME", "NAME:", "NAME :", "DRAWING", "DWG", "SHEET",
            "SCALE", "SCALE:", "SCALE :", "BL", "DATE", "PROJECT", "CLIENT", "OWNER",
            "DESIGN", "DESIGNED", "DESIGNED BY", "DRAWN", "DRAWN BY", "CHECKED", "CHECKED BY", "APPROVED", "APPROVED BY",
            "CAD", "FILE", "FILENAME", "DESCRIPTION", "TBD", "N/A", "NA",
            "DD", "SD", "CD", "IFC", "WIP",
            "TITRE", "DESSIN", "NOM DU DESSIN", "TITRE DU PLAN", "ECHELLE", "ÉCHELLE", "PROJET",
            // Common bilingual translations in parentheses or brackets
            "(DRAWING TITLE)", "（DRAWING TITLE）",
            "(DRAWING NAME)", "（DRAWING NAME）",
            "(DWG TITLE)", "（DWG TITLE）", "(DWG NAME)", "（DWG NAME）",
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
            string s = Regex.Replace(
                raw,
                @"\\A\d+;|\\f[^;]*;|\\H[^;]*;|\\W[^;]*;|\\C\d+;|\\T[^;]*;|\\Q[^;]*;|\\p[^;]*;|\\[LlOoKk]",
                "",
                RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\\S([^;]*);", "$1", RegexOptions.IgnoreCase);
            s = s.Replace("\\~", " ");
            s = Regex.Replace(s, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
            s = s.Replace("{", "").Replace("}", "").Replace("%%c", "Φ").Replace("%%C", "Φ").Replace("%%d", "°").Replace("%%p", "±");
            s = Regex.Replace(s, @"\s+", " ");
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
            if (IgnoredTitleTexts.Contains(s) || IsPureLabelEnglishTranslation(s)) return false;

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
        /// Checks whether a text is ignored label text, punctuation-only, or a template placeholder (e.g. "XXXXX", "XX", "Date:", "Key plan").
        /// </summary>
        public static bool IsIgnoredOrPlaceholderTitleText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            string s = text.Trim();
            if (IgnoredTitleTexts.Contains(s)) return true;

            string strippedTrailing = Regex.Replace(s, @"[\s:：_\-\./\\]+$", "").Trim();
            if (string.IsNullOrEmpty(strippedTrailing)) return true;
            if (IgnoredTitleTexts.Contains(strippedTrailing)) return true;

            // Template placeholders such as "XXXXX", "xxx", "XX", "---", "???"
            string compact = Regex.Replace(s, @"\s+", "");
            if (Regex.IsMatch(compact, @"^[X_\-\.\?\*:：/\\]+$", RegexOptions.IgnoreCase)) return true;

            return false;
        }

        /// <summary>
        /// Checks whether a text is purely an English/French translation or fragment of a titleblock label
        /// (e.g. "DRAWING TITLE", "(DRAWING TITLE)", "（DRAWING TITLE）", "DRAWING", "TITLE :", "DRAWING NO.", "SCALE").
        /// Such texts must NEVER be treated as values for drawing title, number, or scale.
        /// </summary>
        public static bool IsPureLabelEnglishTranslation(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            // Strip surrounding brackets: (), （）, [], 【】
            s = Regex.Replace(s, @"^[（\(\[【\s]+|[）\)\]】\s]+$", "");
            s = Regex.Replace(s, @"[\s:：_\-\./\\#]+", " ").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(s)) return true;

            return s == "DRAWING TITLE" ||
                   s == "DRAWING NAME" ||
                   s == "DWG TITLE" ||
                   s == "DWG NAME" ||
                   s == "SHEET NAME" ||
                   s == "SHEET TITLE" ||
                   s == "TITLE" ||
                   s == "NAME" ||
                   s == "DRAWING" ||
                   s == "DWG" ||
                   s == "SHEET" ||
                   s == "DRAWING NO" ||
                   s == "DRAWING NUMBER" ||
                   s == "DWG NO" ||
                   s == "DWG NUMBER" ||
                   s == "SHEET NO" ||
                   s == "SHEET NUMBER" ||
                   s == "NO" ||
                   s == "NUMBER" ||
                   s == "SCALE" ||
                   s == "DRAWING SCALE" ||
                   s == "BL" ||
                   s == "PROJECT NAME" ||
                   s == "PROJ NAME" ||
                   s == "PROJECT TITLE" ||
                   s == "PROJECT NO" ||
                   s == "PROJECT" ||
                   s == "CLIENT" ||
                   s == "DATE" ||
                   s == "DESIGNED BY" ||
                   s == "DRAWN BY" ||
                   s == "CHECKED BY" ||
                   s == "APPROVED BY" ||
                   s == "DESIGN" ||
                   s == "DRAWN" ||
                   s == "CHECKED" ||
                   s == "APPROVED" ||
                   s == "BY" ||
                   s == "JOB" ||
                   s == "JOB NO" ||
                   s == "DRAWING STATUS" ||
                   s == "KEY PLAN" ||
                   s == "KEYPLAN" ||
                   s == "LEGEND" ||
                   s == "LOCATION PLAN" ||
                   s == "REMARKS" ||
                   s == "REVISION DESCRIPTION" ||
                   s == "LEAD CONSULTANT" ||
                   s == "REV" ||
                   s == "REVISION" ||
                   s == "STATUS" ||
                   s == "STAGE" ||
                   s == "PHASE" ||
                   s == "DISCIPLINE" ||
                   s == "TITRE" ||
                   s == "DESSIN" ||
                   s == "NOM DU DESSIN" ||
                   s == "TITRE DU PLAN" ||
                   s == "ECHELLE" ||
                   s == "ÉCHELLE" ||
                   s == "PROJET";
        }

        /// <summary>
        /// Detects engineering annotations, structural rebar specifications, beam/column tags,
        /// figure/table numbers in drawing notes, cross-reference callouts, dimension formulas,
        /// or construction notes that must NEVER be misidentified as a drawing title.
        /// Example: "8%%13222(实心)+6%%13220(空心)", "4Φ20", "图10.4.3-1 现浇板留洞构造一", "配筋详平面".
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

            // 4. Arithmetic, inequality, or formula operators common in rebar schedules and notes
            if (s.Contains("+") || s.Contains("=") || s.Contains("*") ||
                s.Contains("<") || s.Contains(">") || s.Contains("^") ||
                s.Contains("≤") || s.Contains("≥") || s.Contains("±"))
            {
                return true;
            }

            // 5. Cross-section dimensions (e.g. 400x600, 400×600, 300*500)
            if (Regex.IsMatch(s, @"\b\d{2,4}\s*[x×*]\s*\d{2,4}\b", RegexOptions.IgnoreCase)) return true;

            // 6. Concrete or rebar grades (C30, C35, C40, HRB400, HRB500, HPB300, Q235, Q345, Q355)
            if (Regex.IsMatch(s, @"^(?:C\d{2}|HRB\d{3}|HPB\d{3}|Q\d{3})\b", RegexOptions.IgnoreCase)) return true;

            // 7. Structural member designations (KZ1, KL2, WKL3, XL1, GZ1, YBZ1, GBZ1, LL1)
            if (Regex.IsMatch(s, @"^(?:KZ|KL|WKL|XL|GZ|YBZ|GBZ|LL|L)\d+\b", RegexOptions.IgnoreCase)) return true;

            // 8. Elevation marks (▽-0.050, ▽+3.600)
            if (s.Contains("▽") || Regex.IsMatch(s, @"^[\+\-]\d+\.\d{2,3}$")) return true;

            // 9. Figure or Table captions inside general notes (e.g. "图8.1.8 ...", "图10.4.3-1 ...", "表10.1.1 ...")
            if (Regex.IsMatch(s, @"^(?:图|表|附图|附表|Figure|Fig\.?|Table|Tableau)\s*\d", RegexOptions.IgnoreCase)) return true;

            // 10. Cross-reference callouts inside drawings (e.g. "详梁配筋图", "配筋详平面", "详见...", "参见...")
            if (Regex.IsMatch(s, @"(?:详梁|详墙|详柱|详板|详平面|详见|参见|见图|详图集|配筋详)")) return true;

            return false;
        }

        /// <summary>
        /// Checks whether a text string looks like a valid drawing title (Chinese, English, or French).
        /// Requires structural/architectural engineering drawing terminology or standard drawing document suffix.
        /// </summary>
        public static bool IsLikelyDrawingTitleText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            if (s.Length < 2 || s.Length > 65) return false;
            if (IsIgnoredOrPlaceholderTitleText(s) || IsPureLabelEnglishTranslation(s)) return false;

            // Reject engineering detail / rebar / section annotations / figure captions
            if (IsEngineeringDetailOrRebarAnnotation(s)) return false;

            // Reject sentences with punctuation, colons, commas, or notes
            if (s.Contains("：") || s.Contains(":") || s.Contains("。") || s.Contains("；") || s.Contains(";") || s.Contains("，")) return false;
            if (s.StartsWith("注") || s.StartsWith("1.") || s.StartsWith("2.") || s.StartsWith("3.") ||
                s.StartsWith("4.") || s.StartsWith("5.")) return false;
            if (s.StartsWith("（1）") || s.StartsWith("(1)") || s.StartsWith("（2）") || s.StartsWith("(2)")) return false;

            // Reject design institute company info, seals, approvals, and prose clauses
            if (s.IndexOf("有限公司", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("设计院", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("事务所", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("专用章", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("出图章", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("注册章", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("执业章", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("未盖", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("审定", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("项目负责", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("的所有", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("不应小于", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("不小于", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("应符合", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            // Reject pure date strings or pure numbers
            if (Regex.IsMatch(s, @"^\d{4}[-—_/\.]\d{1,2}(?:[-—_/\.]\d{1,2})?$")) return false;
            if (Regex.IsMatch(s, @"^\d+$")) return false;

            string compacted = Regex.Replace(s, @"\s+", "");

            // 1. Strong match: ends with standard Chinese drawing document keyword
            if (EndsWithDrawingKeyword(compacted)) return true;

            // 2. Contains specific Chinese construction drawing compound terms
            if (compacted.Contains("平面图") ||
                compacted.Contains("立面图") ||
                compacted.Contains("剖面图") ||
                compacted.Contains("配筋图") ||
                compacted.Contains("布置图") ||
                compacted.Contains("模板图") ||
                compacted.Contains("定位图") ||
                compacted.Contains("详图") ||
                compacted.Contains("大样") ||
                compacted.Contains("系统图") ||
                compacted.Contains("总说明") ||
                compacted.Contains("设计说明") ||
                compacted.Contains("结构说明") ||
                compacted.Contains("建筑说明") ||
                compacted.Contains("施工说明") ||
                compacted.Contains("目录") ||
                compacted.Contains("身表") ||
                compacted.Contains("构件表") ||
                compacted.Contains("一览表") ||
                compacted.Contains("材料表"))
            {
                return true;
            }

            // Short clean Chinese title phrases containing core layout words (e.g. "首层平面布置", "二层梁配筋")
            if (compacted.Length <= 24 &&
                (compacted.Contains("平面") || compacted.Contains("立面") || compacted.Contains("剖面") ||
                 compacted.Contains("配筋") || compacted.Contains("布置") || compacted.Contains("定位")))
            {
                return true;
            }

            // 3. English / French drawing title keywords (e.g. "GROUND FLOOR LAYOUT PLAN", "FOUNDATION FRAMING PLAN", "BEAM SCHEDULE")
            if (Regex.IsMatch(s, @"[A-Za-z]{2,}\s+[A-Za-z]{2,}") &&
                !Regex.IsMatch(s, @"^(?:NOTE|NOTES|SEE|REFER|ALL|UNLESS|FOR\s+DETAIL)\b", RegexOptions.IgnoreCase))
            {
                if (Regex.IsMatch(
                    s,
                    @"\b(?:PLAN|PLANS|LAYOUT|ELEVATION|ELEVATIONS|SECTION|SECTIONS|DETAIL|DETAILS|SCHEDULE|SCHEDULES|DIAGRAM|DIAGRAMS|GENERAL\s+NOTES|STRUCTURAL\s+NOTES|DESIGN\s+NOTES|CONSTRUCTION\s+NOTES|DRAWING\s+LIST|DRAWING\s+INDEX|SHEET\s+INDEX|FOUNDATION|FRAMING|REINFORCEMENT|ARRANGEMENT|PROFILE|ISOMETRIC|ROOF|FLOOR|BASEMENT|COLUMN|BEAM|SLAB|WALL|STAIR|STAIRCASE|FOOTING|PILE|COFFRAGE|FERRAILLAGE|COUPE|COUPES|FAÇADE|FACADE|NOTES\s+GÉNÉRALES|SOMMAIRE|NOMENCLATURE)\b",
                    RegexOptions.IgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool EndsWithDrawingKeyword(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            string t = s.Trim();
            if (Regex.IsMatch(t, @"^(?:图|表|附图|附表)\s*\d")) return false;

            // Strip trailing parentheses like (一), （一）, (1), （1）, (二), (2), (A), (B), (大样), (详图)
            string norm = Regex.Replace(t, @"[\(（][一二三四五六七八九十\d\w\s\-—~～/]+[\)）]$", "").Trim();
            if (string.IsNullOrWhiteSpace(norm)) norm = t;

            return norm.EndsWith("图", StringComparison.Ordinal) ||
                   norm.EndsWith("表", StringComparison.Ordinal) ||
                   norm.EndsWith("总说明", StringComparison.Ordinal) ||
                   norm.EndsWith("设计说明", StringComparison.Ordinal) ||
                   norm.EndsWith("施工说明", StringComparison.Ordinal) ||
                   norm.EndsWith("结构说明", StringComparison.Ordinal) ||
                   norm.EndsWith("建筑说明", StringComparison.Ordinal) ||
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
                   layer.IndexOf("TTLB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   layer.IndexOf("TK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   layer.IndexOf("BORDER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   layer.IndexOf("PUB_TITLE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Checks whether a point lies inside the standard titleblock regions of a sheet frame
        /// (bottom-right quadrant, right-hand vertical title strip, or bottom horizontal title strip),
        /// excluding the interior drawing content canvas.
        /// </summary>
        public static bool IsInTitleblockZone(Point2D pos, Extents2D box)
        {
            double w = box.Width;
            double h = box.Height;
            if (w <= 0 || h <= 0) return true;

            bool isBrCorner = (pos.X >= box.MinX + w * 0.48 && pos.Y <= box.MinY + h * 0.35);
            bool isRightStrip = (pos.X >= box.MinX + w * 0.75);
            bool isBottomStrip = (pos.Y <= box.MinY + h * 0.18);
            return isBrCorner || isRightStrip || isBottomStrip;
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

            // Direct in-text extraction (e.g. "图名: 首层平面布置图", "Drawing Title: GROUND FLOOR LAYOUT PLAN", "图号: 结-01")
            foreach (var item in inFrameTexts)
            {
                if (string.IsNullOrWhiteSpace(item.Text)) continue;
                string clean = item.Text.Trim();

                // Check Drawing Name Label / In-Text
                if (string.IsNullOrWhiteSpace(frame.ExtractedDrawingName))
                {
                    var match = Regex.Match(
                        clean,
                        @"^(?:图\s*名|图\s*纸\s*名\s*称|项\s*目\s*图\s*名|工\s*程\s*图\s*名|DWG\s*NAME|DRAWING\s*NAME|DRAWING\s*TITLE|DWG\s*TITLE|SHEET\s*NAME|SHEET\s*TITLE|TITRE(?:\s*DU\s*PLAN)?|NOM\s*DU\s*DESSIN|TITLE)\s*[:：\s/\\-]+\s*(.+)$",
                        RegexOptions.IgnoreCase);
                    if (!match.Success && rules != null)
                    {
                        match = TryMatchCustomTagPrefix(clean, rules.SelectMany(r => r.SheetNameTags ?? Enumerable.Empty<string>()));
                    }

                    if (match != null && match.Success)
                    {
                        string val = match.Groups[1].Value.Trim();
                        if (IsPureLabelEnglishTranslation(val) || IsIgnoredOrPlaceholderTitleText(val) || IsAnyLabel(val, rules))
                        {
                            // Bilingual label like "图名：（DRAWING TITLE）" or label with punctuation, designate this item as the label item
                            if (IsBetterLabelCandidate(item, nameLabelItem, box))
                            {
                                nameLabelItem = item;
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(val) && val.Length >= 2 && !IsEngineeringDetailOrRebarAnnotation(val))
                        {
                            frame.ExtractedDrawingName = val;
                        }
                    }
                    else if (IsDrawingNameLabel(clean, rules))
                    {
                        if (IsBetterLabelCandidate(item, nameLabelItem, box))
                        {
                            nameLabelItem = item;
                        }
                    }
                }

                // Check Drawing Number Label / In-Text
                if (string.IsNullOrWhiteSpace(frame.ExtractedDrawingNumber))
                {
                    var match = Regex.Match(
                        clean,
                        @"^(?:图\s*号|图\s*纸\s*编\s*号|工\s*程\s*编\s*号|DWG\s*NO\.?|DRAWING\s*NO\.?|DRAWING\s*NUMBER|DWG\s*NUMBER|SHEET\s*NO\.?|SHEET\s*NUMBER|NO\.?)\s*[:：\s/\\-]+\s*(.+)$",
                        RegexOptions.IgnoreCase);
                    if (!match.Success && rules != null)
                    {
                        match = TryMatchCustomTagPrefix(clean, rules.SelectMany(r => r.SheetNumberTags ?? Enumerable.Empty<string>()));
                    }

                    if (match != null && match.Success)
                    {
                        string val = match.Groups[1].Value.Trim();
                        if (IsPureLabelEnglishTranslation(val) || IsIgnoredOrPlaceholderTitleText(val) || IsAnyLabel(val, rules))
                        {
                            if (IsBetterLabelCandidate(item, numberLabelItem, box))
                            {
                                numberLabelItem = item;
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(val) && val.Length >= 1 &&
                                 (IsDrawingNumberText(val) || Regex.IsMatch(val, @"\d")))
                        {
                            frame.ExtractedDrawingNumber = val;
                        }
                    }
                    else if (IsDrawingNumberLabel(clean, rules))
                    {
                        if (IsBetterLabelCandidate(item, numberLabelItem, box))
                        {
                            numberLabelItem = item;
                        }
                    }
                }

                // Check Scale Label / In-Text
                if (!frame.ExtractedScaleValue.HasValue)
                {
                    var match = Regex.Match(
                        clean,
                        @"^(?:比\s*例|出\s*图\s*比\s*例|图\s*纸\s*比\s*例|SCALE|BL|ECHELLE|ÉCHELLE)\s*[:：\s/\\-]+\s*(.+)$",
                        RegexOptions.IgnoreCase);
                    if (!match.Success && rules != null)
                    {
                        match = TryMatchCustomTagPrefix(clean, rules.SelectMany(r => r.ScaleTags ?? Enumerable.Empty<string>()));
                    }

                    if (match != null && match.Success)
                    {
                        string val = match.Groups[1].Value.Trim();
                        if (IsPureLabelEnglishTranslation(val) || IsIgnoredOrPlaceholderTitleText(val) || IsAnyLabel(val, rules))
                        {
                            if (IsBetterLabelCandidate(item, scaleLabelItem, box))
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
                    else if (IsScaleLabel(clean, rules))
                    {
                        if (IsBetterLabelCandidate(item, scaleLabelItem, box))
                        {
                            scaleLabelItem = item;
                        }
                    }
                }
            }

            // 2. Spatial Proximity: Right-hand side, Below, or Cell-Centered search for detected labels
            if (string.IsNullOrWhiteSpace(frame.ExtractedDrawingName) && nameLabelItem != null)
            {
                var valItem = FindNearestNeighborValue(
                    nameLabelItem,
                    inFrameTexts,
                    frame,
                    cand => !IsIgnoredOrPlaceholderTitleText(cand.Text) &&
                            cand.Text.Length >= 2 && cand.Text.Length <= 65 &&
                            !IsAnyLabel(cand.Text, rules) &&
                            !IsPureLabelEnglishTranslation(cand.Text) &&
                            !IsEngineeringDetailOrRebarAnnotation(cand.Text) &&
                            !IsScaleText(cand.Text) &&
                            !cand.Text.Contains("有限公司") &&
                            !cand.Text.Contains("设计院"),
                    isTitleSearch: true);

                if (valItem != null)
                {
                    frame.ExtractedDrawingName = valItem.Text.Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(frame.ExtractedDrawingNumber) && numberLabelItem != null)
            {
                var valItem = FindNearestNeighborValue(
                    numberLabelItem,
                    inFrameTexts,
                    frame,
                    cand => !IsIgnoredOrPlaceholderTitleText(cand.Text) &&
                            cand.Text.Length >= 1 && cand.Text.Length <= 35 &&
                            !IsAnyLabel(cand.Text, rules) &&
                            !IsPureLabelEnglishTranslation(cand.Text) &&
                            !IsLikelyDrawingTitleText(cand.Text) &&
                            !IsScaleText(cand.Text) &&
                            (IsDrawingNumberText(cand.Text) ||
                             (Regex.IsMatch(cand.Text, @"\d") && Regex.IsMatch(cand.Text, @"^[A-Za-z0-9\u4e00-\u9fa5\-_#/\.\(\)]{1,30}$"))),
                    isTitleSearch: false);

                if (valItem != null)
                {
                    frame.ExtractedDrawingNumber = valItem.Text.Trim();
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
                        frame.ExtractedScaleText = valItem.Text.Trim();
                    }
                }
            }

            // 3. Intelligent Drawing Name Extraction when no explicit label was found or matched
            //    Strictly restrict to the Titleblock Zone (or top-center General Notes / Directory header)
            //    so texts from the interior drawing content canvas are NEVER mistakenly picked!
            if (string.IsNullOrWhiteSpace(frame.ExtractedDrawingName))
            {
                var candidates = inFrameTexts
                    .Where(t => IsLikelyDrawingTitleText(t.Text) &&
                                (IsInTitleblockZone(t.Position, box) ||
                                 (IsTitleLayer(t.Layer) && (t.Position.X >= box.MinX + w * 0.45 || t.Position.Y <= box.MinY + h * 0.25)) ||
                                 IsTopHeaderGeneralNotesOrIndex(t, box)))
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
                            compacted.Contains("身表") || compacted.Contains("总说明") ||
                            compacted.Contains("设计说明") || compacted.Contains("定位") ||
                            Regex.IsMatch(clean, @"\b(?:PLAN|LAYOUT|ELEVATION|SECTION|DETAIL|SCHEDULE|NOTES)\b", RegexOptions.IgnoreCase))
                        {
                            score += 300.0;
                        }

                        // Titleblock Zone bonus
                        bool isBrCorner = (t.Position.X >= box.MinX + w * 0.55 && t.Position.Y <= box.MinY + h * 0.28);
                        bool isRightStrip = (t.Position.X >= box.MinX + w * 0.80);
                        bool isBottomStrip = (t.Position.Y <= box.MinY + h * 0.16);

                        if (isBrCorner)
                        {
                            score += 500.0;
                            double distToBr = Math.Sqrt(Math.Pow(t.Position.X - brCornerX, 2) + Math.Pow(t.Position.Y - brCornerY, 2));
                            double diag = Math.Sqrt(w * w + h * h);
                            if (diag > 0)
                            {
                                score += (1.0 - Math.Min(1.0, distToBr / diag)) * 120.0;
                            }
                        }
                        else if (isRightStrip || isBottomStrip)
                        {
                            score += 250.0;
                        }

                        // Height bonus: larger font in titleblock indicates title vs label
                        score += Math.Min(t.Height * 2.0, 350.0);

                        // Optimal length bonus (4-40 chars)
                        if (clean.Length >= 4 && clean.Length <= 40)
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

            // 4. Drawing Number Standalone Regex Match inside titleblock zone
            if (string.IsNullOrWhiteSpace(frame.ExtractedDrawingNumber))
            {
                var titleZoneTexts = inFrameTexts
                    .Where(t => IsInTitleblockZone(t.Position, box))
                    .ToList();

                var noCandidate = titleZoneTexts.FirstOrDefault(t => IsDrawingNumberText(t.Text)) ??
                                  inFrameTexts.FirstOrDefault(t => IsTitleLayer(t.Layer) && IsDrawingNumberText(t.Text));

                if (noCandidate != null)
                {
                    frame.ExtractedDrawingNumber = noCandidate.Text.Trim();
                }
            }

            // 5. Scale Validation against Outer Sheet Dimensions
            if (!frame.ExtractedScaleValue.HasValue)
            {
                var scaleCandidates = inFrameTexts
                    .OrderByDescending(t => IsInTitleblockZone(t.Position, box) ? 1 : 0)
                    .Where(t => IsScaleText(t.Text))
                    .ToList();
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
                            frame.ExtractedScaleText = sc.Text.Trim();
                            break;
                        }
                    }
                }
            }
        }

        private static bool IsBetterLabelCandidate(TitleTextItem candidate, TitleTextItem currentBest, Extents2D box)
        {
            if (currentBest == null) return true;
            bool candInZone = IsInTitleblockZone(candidate.Position, box);
            bool currInZone = IsInTitleblockZone(currentBest.Position, box);
            if (candInZone && !currInZone) return true;
            if (!candInZone && currInZone) return false;

            bool candTitleLayer = IsTitleLayer(candidate.Layer);
            bool currTitleLayer = IsTitleLayer(currentBest.Layer);
            if (candTitleLayer && !currTitleLayer) return true;
            if (!candTitleLayer && currTitleLayer) return false;

            return candidate.Height > currentBest.Height;
        }

        private static bool IsTopHeaderGeneralNotesOrIndex(TitleTextItem t, Extents2D box)
        {
            if (t == null || string.IsNullOrWhiteSpace(t.Text)) return false;
            double w = box.Width;
            double h = box.Height;
            if (t.Position.Y < box.MinY + h * 0.80) return false;
            if (t.Position.X < box.MinX + w * 0.15 || t.Position.X > box.MinX + w * 0.85) return false;

            string s = t.Text.Trim();
            return s.EndsWith("总说明", StringComparison.Ordinal) ||
                   s.EndsWith("设计说明", StringComparison.Ordinal) ||
                   s.EndsWith("目录", StringComparison.Ordinal) ||
                   Regex.IsMatch(s, @"\b(?:GENERAL\s+NOTES|DRAWING\s+LIST|DRAWING\s+INDEX)\b", RegexOptions.IgnoreCase);
        }

        private static Match TryMatchCustomTagPrefix(string text, IEnumerable<string> customTags)
        {
            if (string.IsNullOrWhiteSpace(text) || customTags == null) return null;
            foreach (string rawTag in customTags)
            {
                if (string.IsNullOrWhiteSpace(rawTag)) continue;
                string tag = rawTag.Trim();
                var m = Regex.Match(text, @"^" + Regex.Escape(tag) + @"\s*[:：\s/\\-]+\s*(.+)$", RegexOptions.IgnoreCase);
                if (m.Success) return m;
            }
            return null;
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

                double candidateScore = double.MaxValue;

                // Case 1: Value is to the Right of Label (same horizontal row)
                if (cx > lx)
                {
                    double dx = cx - lx;
                    double dy = Math.Abs(cy - ly);

                    double maxDy = Math.Max(refH * 3.0, 18.0);
                    double maxDx = Math.Min(frameW * 0.40, Math.Max(refH * 45.0, 150.0));

                    if (dy <= maxDy && dx <= maxDx)
                    {
                        double s1 = Math.Sqrt(dx * dx + (dy * 3.0) * (dy * 3.0));
                        if (s1 < candidateScore) candidateScore = s1;
                    }
                }

                // Case 2: Value is Below Label (same vertical column)
                if (cy < ly)
                {
                    double dy = ly - cy;
                    double dx = Math.Abs(cx - lx);

                    double maxDx = Math.Min(frameW * 0.25, Math.Max(refH * 25.0, 100.0));
                    double maxDy = Math.Min(frameH * 0.22, Math.Max(refH * 22.0, 100.0));

                    if (dx <= maxDx && dy <= maxDy)
                    {
                        double s2 = Math.Sqrt((dx * 2.5) * (dx * 2.5) + dy * dy);
                        if (s2 < candidateScore) candidateScore = s2;
                    }
                }

                // Case 3: Cell-Centered Value inside a Titleblock Box where the Label is at Top-Left of the Cell
                // (e.g. "Drawing\nTitle :" at top-left and "GROUND FLOOR LAYOUT PLAN" centered in the cell)
                if (cx >= lx - refH * 2.0 && cy <= ly + refH * 1.5)
                {
                    double dx = Math.Max(0.0, cx - lx);
                    double dy = Math.Max(0.0, ly - cy);

                    double maxDx = Math.Min(frameW * 0.45, Math.Max(refH * 55.0, 220.0));
                    double maxDy = Math.Min(frameH * 0.28, Math.Max(refH * 35.0, 140.0));

                    if (dx <= maxDx && dy <= maxDy)
                    {
                        double s3 = Math.Sqrt(dx * dx + (dy * 1.4) * (dy * 1.4)) * 1.05;
                        if (s3 < candidateScore) candidateScore = s3;
                    }
                }

                if (candidateScore < double.MaxValue)
                {
                    if (isTitleSearch)
                    {
                        // Strong bonus when the title value has a larger font height than the small cell label
                        if (cand.Height >= labelItem.Height * 1.2)
                        {
                            candidateScore *= 0.45;
                        }
                        if (IsLikelyDrawingTitleText(cand.Text))
                        {
                            candidateScore *= 0.40;
                        }
                        if (IsTitleLayer(cand.Layer))
                        {
                            candidateScore *= 0.80;
                        }
                        // Penalize drawing numbers if they happen to sit near the title label
                        if (IsDrawingNumberText(cand.Text))
                        {
                            candidateScore *= 4.0;
                        }
                    }
                    else
                    {
                        if (IsDrawingNumberText(cand.Text) || IsScaleText(cand.Text))
                        {
                            candidateScore *= 0.45;
                        }
                    }

                    if (candidateScore < bestScore)
                    {
                        bestScore = candidateScore;
                        bestCandidate = cand;
                    }
                }
            }

            return bestCandidate;
        }

        public static bool IsDrawingNameLabel(string text, IList<FrameRule> rules = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = Regex.Replace(text, @"[\(（\[【][^\)）\]】]*[\)）\]】]", "");
            string norm = Regex.Replace(s, @"[\s:：_\-\./\\#]+", "").ToUpperInvariant();
            if (norm == "图名" || norm == "图纸名称" || norm == "项目图名" || norm == "工程图名" ||
                norm == "DWGNAME" || norm == "DRAWINGNAME" || norm == "TITLE" || norm == "SHEETNAME" ||
                norm == "DRAWINGTITLE" || norm == "DWGTITLE" || norm == "SHEETTITLE" ||
                norm == "TITRE" || norm == "TITREDUPLAN" || norm == "NOMDUDESSIN")
            {
                return true;
            }

            string compact = Regex.Replace(text, @"[\s:：_\-\./\\#]+", "").ToUpperInvariant();
            if (compact == "图名DRAWINGTITLE" || compact == "图名DRAWINGNAME" || compact == "图名TITLE" ||
                compact == "图纸名称DRAWINGTITLE" || compact == "图纸名称DRAWINGNAME")
            {
                return true;
            }

            if (rules != null)
            {
                foreach (var r in rules)
                {
                    if (r?.SheetNameTags == null) continue;
                    foreach (var tag in r.SheetNameTags)
                    {
                        if (string.IsNullOrWhiteSpace(tag)) continue;
                        string normTag = Regex.Replace(tag, @"[\s:：_\-\./\\#]+", "").ToUpperInvariant();
                        if (!string.IsNullOrEmpty(normTag) && (norm == normTag || compact == normTag))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        public static bool IsDrawingNumberLabel(string text, IList<FrameRule> rules = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = Regex.Replace(text, @"[\(（\[【][^\)）\]】]*[\)）\]】]", "");
            string norm = Regex.Replace(s, @"[\s:：_\-\./\\#]+", "").ToUpperInvariant();
            if (norm == "图号" || norm == "图纸编号" || norm == "工程编号" ||
                norm == "DWGNO" || norm == "DRAWINGNO" || norm == "DRAWINGNUMBER" || norm == "DWGNUMBER" ||
                norm == "SHEETNO" || norm == "SHEETNUMBER" || norm == "NO")
            {
                return true;
            }

            string compact = Regex.Replace(text, @"[\s:：_\-\./\\#]+", "").ToUpperInvariant();
            if (compact == "图号DRAWINGNO" || compact == "图号DWGNO" || compact == "图号SHEETNO" || compact == "图号NO")
            {
                return true;
            }

            if (rules != null)
            {
                foreach (var r in rules)
                {
                    if (r?.SheetNumberTags == null) continue;
                    foreach (var tag in r.SheetNumberTags)
                    {
                        if (string.IsNullOrWhiteSpace(tag)) continue;
                        string normTag = Regex.Replace(tag, @"[\s:：_\-\./\\#]+", "").ToUpperInvariant();
                        if (!string.IsNullOrEmpty(normTag) && (norm == normTag || compact == normTag))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        public static bool IsScaleLabel(string text, IList<FrameRule> rules = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = Regex.Replace(text, @"[\(（\[【][^\)）\]】]*[\)）\]】]", "");
            string norm = Regex.Replace(s, @"[\s:：_\-\./\\#]+", "").ToUpperInvariant();
            if (norm == "比例" || norm == "出图比例" || norm == "图纸比例" ||
                norm == "SCALE" || norm == "BL" || norm == "ECHELLE" || norm == "ÉCHELLE")
            {
                return true;
            }

            string compact = Regex.Replace(text, @"[\s:：_\-\./\\#]+", "").ToUpperInvariant();
            if (compact == "比例SCALE" || compact == "比例BL" || compact == "出图比例SCALE")
            {
                return true;
            }

            if (rules != null)
            {
                foreach (var r in rules)
                {
                    if (r?.ScaleTags == null) continue;
                    foreach (var tag in r.ScaleTags)
                    {
                        if (string.IsNullOrWhiteSpace(tag)) continue;
                        string normTag = Regex.Replace(tag, @"[\s:：_\-\./\\#]+", "").ToUpperInvariant();
                        if (!string.IsNullOrEmpty(normTag) && (norm == normTag || compact == normTag))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        public static bool IsAnyLabel(string text, IList<FrameRule> rules = null)
        {
            return IsDrawingNameLabel(text, rules) || IsDrawingNumberLabel(text, rules) || IsScaleLabel(text, rules);
        }
    }
}
