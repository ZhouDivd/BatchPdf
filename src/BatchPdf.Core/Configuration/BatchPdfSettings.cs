using System;
using System.Collections.Generic;
using BatchPdf.Core.Models;

namespace BatchPdf.Core.Configuration
{
    [Serializable]
    public class FrameRule
    {
        public string RuleName { get; set; } = "DefaultFrameRule";
        public List<string> AllowedLayers { get; set; } = new List<string>();
        public List<string> BlockNames { get; set; } = new List<string>();
        public List<string> SheetNumberTags { get; set; } = new List<string>();
        public List<string> SheetNameTags { get; set; } = new List<string>();
        public List<string> ScaleTags { get; set; } = new List<string>();
        public double MinFrameWidth { get; set; } = 180.0;
        public double MinFrameHeight { get; set; } = 180.0;
    }

    [Serializable]
    public class BatchPdfSettings
    {
        // Frame Recognition Settings (matching TSSD layout)
        public bool EnableRuleSplit { get; set; } = true;
        public FrameRecognitionMode RecognitionMode { get; set; } = FrameRecognitionMode.ByLayer;
        public string ActiveFrameLayer { get; set; } = "TK,BORDER,PUB_TITLE,图框";
        public string ActiveFrameBlockName { get; set; } = "";
        public bool IncludeNonPlottableLayers { get; set; } = false;

        /// <summary>
        /// Whether to scan and include PaperSpace Layouts (布局空间) in addition to Model Space frames.
        /// Default is true.
        /// </summary>
        public bool IncludePaperSpaceLayouts { get; set; } = true;

        // Sort Order Settings
        public SheetSortOrderMode SortOrderMode { get; set; } = SheetSortOrderMode.ColumnFirst;
        public bool ReverseX { get; set; } = false;
        public bool ReverseY { get; set; } = true; // In CAD WCS, Y descending = Top-to-Bottom

        // Plotter / Paper / Scale / Style Settings
        public OutputMode OutputMode { get; set; } = OutputMode.SinglePdfPerSheet;
        public bool AutoMatchPaperSize { get; set; } = true;
        public string ManualPaperCanonicalName { get; set; } = "";
        public string PaperFilterKeyword { get; set; } = "";

        public bool UseFitToPaper { get; set; } = false;
        public bool UseCustomFixedScale { get; set; } = false;
        public double CustomScaleNumerator { get; set; } = 1.0;
        public double CustomScaleDenominator { get; set; } = 100.0;
        public double DefaultScaleValue { get; set; } = 100.0;
        public bool UseDefaultScaleWhenMissing { get; set; } = false;
        public bool InferScaleFromStandardPaper { get; set; } = true;
        public DrawingUnits DefaultDrawingUnits { get; set; } = DrawingUnits.Millimeters;

        public string DefaultPlotStyle { get; set; } = "monochrome.ctb";
        public bool UsePlotStyle { get; set; } = true;
        public ColorMode DefaultColorMode { get; set; } = ColorMode.Monochrome;
        public bool PrintLineweights { get; set; } = true;

        // Plot Offset & Orientation
        public bool CenterPlot { get; set; } = true;
        public double PlotOffsetX { get; set; } = 0.0;
        public double PlotOffsetY { get; set; } = 0.0;
        public PlotOrientationPolicy OrientationPolicy { get; set; } = PlotOrientationPolicy.Auto;
        public bool UpsideDown { get; set; } = false;

        // Output Options
        public bool SaveToDwgDirectory { get; set; } = true;
        public string OutputDirectory { get; set; } = "";
        public string FileNameTemplate { get; set; } = "{图号}_{图名}.pdf";
        public string MergedFileNameTemplate { get; set; } = "{DWG名}_合并.pdf";
        public bool AppendPaperSizeToFileName { get; set; } = false;
        public DuplicateFilePolicy DuplicatePolicy { get; set; } = DuplicateFilePolicy.AutoAppendIndex;
        public int PlotCopies { get; set; } = 1;
        public bool ConfirmPageCountBeforePlot { get; set; } = true;
        public bool OpenReportAfterPlot { get; set; } = false;

        /// <summary>
        /// Maximum allowed edge difference (in mm) when matching frame target paper size against PC3 media.
        /// </summary>
        public double PaperMatchToleranceMm { get; set; } = 10.0;

        /// <summary>
        /// Whether to automatically crop excess white borders on PDFs plotted from non-standard / lengthened frames.
        /// Standard frames that already match the paper size are not cropped.
        /// </summary>
        public bool AutoCropNonStandardPdf { get; set; } = true;

        /// <summary>
        /// White margin (in millimeters) to preserve around the frame when cropping non-standard PDFs (default 0.0 mm).
        /// </summary>
        public double PdfCropMarginMm { get; set; } = 0.0;

        public List<FrameRule> FrameRules { get; set; } = new List<FrameRule>();

        public static BatchPdfSettings CreateDefault()
        {
            var settings = new BatchPdfSettings
            {
                EnableRuleSplit = true,
                RecognitionMode = FrameRecognitionMode.ByLayer,
                ActiveFrameLayer = "TK,BORDER,PUB_TITLE,图框",
                ActiveFrameBlockName = "",
                IncludeNonPlottableLayers = false,
                IncludePaperSpaceLayouts = true,
                SortOrderMode = SheetSortOrderMode.RowFirst,
                ReverseX = false,
                ReverseY = true,
                OutputMode = OutputMode.SinglePdfPerSheet,
                AutoMatchPaperSize = true,
                ManualPaperCanonicalName = "",
                UseFitToPaper = false,
                UseCustomFixedScale = false,
                CustomScaleNumerator = 1.0,
                CustomScaleDenominator = 100.0,
                DefaultScaleValue = 100.0,
                UseDefaultScaleWhenMissing = false,
                InferScaleFromStandardPaper = true,
                DefaultDrawingUnits = DrawingUnits.Millimeters,
                DefaultPlotStyle = "monochrome.ctb",
                UsePlotStyle = true,
                DefaultColorMode = ColorMode.Monochrome,
                PrintLineweights = true,
                CenterPlot = true,
                PlotOffsetX = 0.0,
                PlotOffsetY = 0.0,
                OrientationPolicy = PlotOrientationPolicy.Auto,
                UpsideDown = false,
                SaveToDwgDirectory = true,
                OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                FileNameTemplate = "{图号}_{图名}.pdf",
                MergedFileNameTemplate = "{DWG名}_合并.pdf",
                AppendPaperSizeToFileName = false,
                DuplicatePolicy = DuplicateFilePolicy.AutoAppendIndex,
                PlotCopies = 1,
                ConfirmPageCountBeforePlot = true,
                OpenReportAfterPlot = false,
                PaperMatchToleranceMm = 10.0,
                AutoCropNonStandardPdf = true,
                PdfCropMarginMm = 0.0,
                FrameRules = new List<FrameRule>
                {
                    new FrameRule
                    {
                        RuleName = "通用工程图框规则",
                        AllowedLayers = new List<string> { "TK", "BORDER", "PUB_TITLE", "图框", "S-BDER", "FRAME" },
                        BlockNames = new List<string>
                        {
                            "*FRAME*", "*TITLE*", "*BORDER*", "*TK*", "*TUKUANG*",
                            "*图框*", "*图签*", "*A0*", "*A1*", "*A2*", "*A3*", "*A4*"
                        },
                        SheetNumberTags = new List<string>
                        {
                            "图号", "图纸编号", "图名图号", "DRAWING_NO", "DWG_NO", "DWGNO", "SHEET_NO", "NO"
                        },
                        SheetNameTags = new List<string>
                        {
                            "图名", "图纸名称", "工程图名", "DRAWING_NAME", "DWG_NAME", "DWGNAME", "SHEET_NAME", "TITLE"
                        },
                        ScaleTags = new List<string>
                        {
                            "比例", "出图比例", "图纸比例", "SCALE", "BL"
                        },
                        MinFrameWidth = 180.0,
                        MinFrameHeight = 180.0
                    }
                }
            };
            return settings;
        }
    }
}
