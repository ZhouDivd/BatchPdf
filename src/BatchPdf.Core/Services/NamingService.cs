using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BatchPdf.Core.Models;

namespace BatchPdf.Core.Services
{
    public class NamingService
    {
        private const int MaxFileNameLength = 120;
        private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

        /// <summary>
        /// Builds the target PDF file name for a single sheet according to Section 7.2:
        /// Default template: "{图号}_{图名}.pdf".
        /// Falls back to "{DWG名}_{序号}.pdf" when both drawing number and drawing name are missing.
        /// </summary>
        public string FormatSheetFileName(
            string template,
            string dwgPath,
            int orderIndex,
            string drawingNumber,
            string drawingName)
        {
            string dwgBaseName = GetDwgBaseName(dwgPath);
            string cleanNo = SanitizeComponent(drawingNumber);
            string cleanName = SanitizeComponent(drawingName);
            string indexStr = orderIndex.ToString("D2");

            string rawName;
            if (string.IsNullOrWhiteSpace(cleanNo) && string.IsNullOrWhiteSpace(cleanName))
            {
                rawName = string.Format("{0}_{1}.pdf", dwgBaseName, indexStr);
            }
            else if (string.IsNullOrWhiteSpace(template))
            {
                rawName = BuildFromNumberAndName(cleanNo, cleanName, dwgBaseName, indexStr);
            }
            else
            {
                rawName = template
                    .Replace("{图号}", cleanNo ?? "")
                    .Replace("{图名}", cleanName ?? "")
                    .Replace("{DWG名}", dwgBaseName)
                    .Replace("{序号}", indexStr);

                // Clean up leading/trailing/double underscores when one of {图号} or {图名} is empty
                string withoutExt = rawName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                    ? rawName.Substring(0, rawName.Length - 4)
                    : rawName;

                while (withoutExt.Contains("__"))
                {
                    withoutExt = withoutExt.Replace("__", "_");
                }
                withoutExt = withoutExt.Trim('_', ' ', '-');

                if (string.IsNullOrWhiteSpace(withoutExt))
                {
                    withoutExt = string.Format("{0}_{1}", dwgBaseName, indexStr);
                }

                rawName = withoutExt + ".pdf";
            }

            return EnsureValidPdfFileName(rawName);
        }

        public string FormatMergedFileName(string template, string dwgPath)
        {
            string dwgBaseName = GetDwgBaseName(dwgPath);
            string rawName = string.IsNullOrWhiteSpace(template)
                ? string.Format("{0}_合并.pdf", dwgBaseName)
                : template.Replace("{DWG名}", dwgBaseName);

            return EnsureValidPdfFileName(rawName);
        }

        public string EnsureValidPdfFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "Output.pdf";
            }

            string sanitized = SanitizeComponent(fileName);
            if (!sanitized.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                sanitized += ".pdf";
            }

            if (sanitized.Length > MaxFileNameLength)
            {
                string basePart = Path.GetFileNameWithoutExtension(sanitized);
                basePart = basePart.Substring(0, MaxFileNameLength - 4);
                sanitized = basePart + ".pdf";
            }

            return sanitized;
        }

        /// <summary>
        /// Formats drawing number and drawing name into a clean bookmark title, e.g. "[建施-01] 一层平面图".
        /// Falls back to sheet output filename or order index if drawing number/name are missing.
        /// </summary>
        public string FormatSheetBookmarkTitle(PrintSheet sheet)
        {
            if (sheet == null)
            {
                return "未命名图纸";
            }

            string drawingNum = sheet.DrawingNumber?.Trim();
            string drawingName = sheet.DrawingName?.Trim();

            bool hasNum = !string.IsNullOrEmpty(drawingNum);
            bool hasName = !string.IsNullOrEmpty(drawingName);

            if (hasNum && hasName)
            {
                return string.Format("[{0}] {1}", drawingNum, drawingName);
            }
            if (hasNum)
            {
                return string.Format("[{0}]", drawingNum);
            }
            if (hasName)
            {
                return drawingName;
            }

            if (!string.IsNullOrWhiteSpace(sheet.OutputFileName))
            {
                return Path.GetFileNameWithoutExtension(sheet.OutputFileName);
            }

            return string.Format("图纸 #{0}", sheet.OrderIndex > 0 ? sheet.OrderIndex : 1);
        }

        /// <summary>
        /// Resolves duplicate file names both on disk and within the current batch.
        /// Returns null if the policy is Skip and a conflict exists.
        /// </summary>
        public string ResolveOutputFilePath(
            string outputDirectory,
            string desiredFileName,
            DuplicateFilePolicy policy,
            ISet<string> reservedPathsInBatch,
            Func<string, bool> confirmOverwriteCallback = null)
        {
            string cleanFileName = EnsureValidPdfFileName(desiredFileName);
            string candidatePath = Path.Combine(outputDirectory, cleanFileName);

            bool existsOnDisk = File.Exists(candidatePath);
            bool existsInBatch = reservedPathsInBatch != null && reservedPathsInBatch.Contains(candidatePath);

            if (!existsOnDisk && !existsInBatch)
            {
                reservedPathsInBatch?.Add(candidatePath);
                return candidatePath;
            }

            // Within the same batch, two sheets cannot overwrite each other silently; append index if needed
            if (policy == DuplicateFilePolicy.Skip && existsOnDisk)
            {
                return null;
            }

            if (policy == DuplicateFilePolicy.ConfirmOverwrite && existsOnDisk && !existsInBatch)
            {
                bool allowOverwrite = confirmOverwriteCallback != null && confirmOverwriteCallback(candidatePath);
                if (allowOverwrite)
                {
                    reservedPathsInBatch?.Add(candidatePath);
                    return candidatePath;
                }
                return null;
            }

            // Default or fallback: AutoAppendIndex
            string baseName = Path.GetFileNameWithoutExtension(cleanFileName);
            string ext = Path.GetExtension(cleanFileName);
            int counter = 1;
            while (true)
            {
                string numberedName = string.Format("{0}_{1}{2}", baseName, counter, ext);
                string numberedPath = Path.Combine(outputDirectory, numberedName);
                bool takenOnDisk = File.Exists(numberedPath);
                bool takenInBatch = reservedPathsInBatch != null && reservedPathsInBatch.Contains(numberedPath);
                if (!takenOnDisk && !takenInBatch)
                {
                    reservedPathsInBatch?.Add(numberedPath);
                    return numberedPath;
                }
                counter++;
            }
        }

        private static string BuildFromNumberAndName(string cleanNo, string cleanName, string dwgBaseName, string indexStr)
        {
            if (!string.IsNullOrWhiteSpace(cleanNo) && !string.IsNullOrWhiteSpace(cleanName))
            {
                return string.Format("{0}_{1}.pdf", cleanNo, cleanName);
            }
            if (!string.IsNullOrWhiteSpace(cleanNo))
            {
                return string.Format("{0}.pdf", cleanNo);
            }
            if (!string.IsNullOrWhiteSpace(cleanName))
            {
                return string.Format("{0}.pdf", cleanName);
            }
            return string.Format("{0}_{1}.pdf", dwgBaseName, indexStr);
        }

        private static string GetDwgBaseName(string dwgPath)
        {
            if (string.IsNullOrWhiteSpace(dwgPath))
            {
                return "Drawing";
            }
            try
            {
                string name = Path.GetFileNameWithoutExtension(dwgPath);
                return string.IsNullOrWhiteSpace(name) ? "Drawing" : SanitizeComponent(name);
            }
            catch
            {
                return "Drawing";
            }
        }

        public static string SanitizeComponent(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(input.Length);
            foreach (char c in input.Trim())
            {
                if (Array.IndexOf(InvalidFileNameChars, c) >= 0 || char.IsControl(c))
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString().Trim();
        }
    }
}
