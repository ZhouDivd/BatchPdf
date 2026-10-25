using System;
using System.Collections.Generic;

namespace BatchPdf.Core.Models
{
    public class SheetPlotResult
    {
        public string SheetId { get; set; }
        public int OrderIndex { get; set; }
        public string FrameHandle { get; set; }
        public string DrawingNumber { get; set; }
        public string DrawingName { get; set; }
        public string DeviceName { get; set; }
        public string CanonicalMediaName { get; set; }
        public string LocaleMediaName { get; set; }
        public double PaperWidthMm { get; set; }
        public double PaperHeightMm { get; set; }
        public double? ScaleValue { get; set; }
        public bool FitToPaper { get; set; }
        public string PlotStyle { get; set; }
        public string OutputFilePath { get; set; }
        public SheetStatus Status { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    }

    public class JobResult
    {
        public string JobId { get; set; } = Guid.NewGuid().ToString("N");
        public OutputMode OutputMode { get; set; }
        public string DeviceName { get; set; }
        public string OutputDirectory { get; set; }
        public string MergedOutputFilePath { get; set; }
        public bool MergedFileIsComplete { get; set; }
        public int TotalCount { get; set; }
        public int SucceededCount { get; set; }
        public int FailedCount { get; set; }
        public int SkippedCount { get; set; }
        public bool WasCancelled { get; set; }
        public string FatalError { get; set; }
        public string LogFilePath { get; set; }
        public List<SheetPlotResult> SheetResults { get; set; } = new List<SheetPlotResult>();
    }
}
