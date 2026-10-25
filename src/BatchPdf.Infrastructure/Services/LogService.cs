using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using BatchPdf.Core.Models;

namespace BatchPdf.Infrastructure.Services
{
    public class LogService
    {
        private readonly string _logDirectory;
        private string _currentLogFilePath;
        private readonly object _syncRoot = new object();

        public LogService()
            : this(GetDefaultLogDirectory())
        {
        }

        public LogService(string logDirectory)
        {
            _logDirectory = logDirectory;
            EnsureDirectory();
        }

        public string LogDirectory => _logDirectory;

        public string CurrentLogFilePath
        {
            get
            {
                if (string.IsNullOrEmpty(_currentLogFilePath))
                {
                    string fileName = string.Format("BatchPdf_{0:yyyyMMdd}.jsonl", DateTime.Now);
                    _currentLogFilePath = Path.Combine(_logDirectory, fileName);
                }
                return _currentLogFilePath;
            }
        }

        public static string GetDefaultLogDirectory()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "BatchPdf", "Logs");
        }

        public void LogInfo(string eventType, string message, object data = null)
        {
            WriteLogEntry("INFO", eventType, message, data);
        }

        public void LogWarning(string eventType, string message, object data = null)
        {
            WriteLogEntry("WARN", eventType, message, data);
        }

        public void LogError(string eventType, string message, Exception ex = null, object data = null)
        {
            WriteLogEntry("ERROR", eventType, message, new
            {
                Error = ex != null ? ex.ToString() : null,
                Data = data
            });
        }

        public void LogSheetResult(string jobId, SheetPlotResult sheetResult)
        {
            WriteLogEntry(
                sheetResult.Status == SheetStatus.Succeeded ? "INFO" : "ERROR",
                "SheetPlot",
                string.Format("Sheet #{0} [{1}] -> {2}", sheetResult.OrderIndex, sheetResult.FrameHandle, sheetResult.Status),
                new
                {
                    JobId = jobId,
                    sheetResult.SheetId,
                    sheetResult.OrderIndex,
                    sheetResult.FrameHandle,
                    sheetResult.DrawingNumber,
                    sheetResult.DrawingName,
                    sheetResult.DeviceName,
                    sheetResult.CanonicalMediaName,
                    sheetResult.LocaleMediaName,
                    sheetResult.PaperWidthMm,
                    sheetResult.PaperHeightMm,
                    sheetResult.ScaleValue,
                    sheetResult.FitToPaper,
                    sheetResult.PlotStyle,
                    sheetResult.OutputFilePath,
                    Status = sheetResult.Status.ToString(),
                    sheetResult.ErrorMessage
                });
        }

        public void LogJobSummary(JobResult jobResult)
        {
            if (jobResult == null) return;
            jobResult.LogFilePath = CurrentLogFilePath;

            WriteLogEntry(
                jobResult.FailedCount == 0 && string.IsNullOrEmpty(jobResult.FatalError) ? "INFO" : "WARN",
                "JobSummary",
                string.Format("Job {0} finished: Total={1}, Succeeded={2}, Failed={3}, Skipped={4}",
                    jobResult.JobId, jobResult.TotalCount, jobResult.SucceededCount, jobResult.FailedCount, jobResult.SkippedCount),
                new
                {
                    jobResult.JobId,
                    OutputMode = jobResult.OutputMode.ToString(),
                    jobResult.DeviceName,
                    jobResult.OutputDirectory,
                    jobResult.MergedOutputFilePath,
                    jobResult.MergedFileIsComplete,
                    jobResult.TotalCount,
                    jobResult.SucceededCount,
                    jobResult.FailedCount,
                    jobResult.SkippedCount,
                    jobResult.WasCancelled,
                    jobResult.FatalError
                });
        }

        private void WriteLogEntry(string level, string eventType, string message, object payload)
        {
            try
            {
                EnsureDirectory();
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var entry = new Dictionary<string, object>
                {
                    ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    ["level"] = level,
                    ["event"] = eventType,
                    ["message"] = message
                };
                if (payload != null)
                {
                    entry["data"] = payload;
                }

                string line = serializer.Serialize(entry) + Environment.NewLine;
                lock (_syncRoot)
                {
                    File.AppendAllText(CurrentLogFilePath, line, Encoding.UTF8);
                }
            }
            catch
            {
                // Ignore logging I/O exceptions
            }
        }

        private void EnsureDirectory()
        {
            try
            {
                if (!string.IsNullOrEmpty(_logDirectory) && !Directory.Exists(_logDirectory))
                {
                    Directory.CreateDirectory(_logDirectory);
                }
            }
            catch
            {
            }
        }
    }
}
