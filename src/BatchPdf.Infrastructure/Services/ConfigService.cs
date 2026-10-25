using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using BatchPdf.Core.Configuration;

namespace BatchPdf.Infrastructure.Services
{
    public class ConfigService
    {
        private readonly string _settingsFilePath;

        public ConfigService()
            : this(GetDefaultSettingsFilePath())
        {
        }

        public ConfigService(string settingsFilePath)
        {
            _settingsFilePath = settingsFilePath;
        }

        public string SettingsFilePath => _settingsFilePath;

        public static string GetDefaultSettingsFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "BatchPdf", "settings.json");
        }

        public BatchPdfSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    var defaults = BatchPdfSettings.CreateDefault();
                    SaveSettings(defaults);
                    return defaults;
                }

                string json = File.ReadAllText(_settingsFilePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return BatchPdfSettings.CreateDefault();
                }

                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var loaded = serializer.Deserialize<BatchPdfSettings>(json);
                if (loaded == null)
                {
                    return BatchPdfSettings.CreateDefault();
                }

                if (loaded.FrameRules == null || loaded.FrameRules.Count == 0)
                {
                    loaded.FrameRules = BatchPdfSettings.CreateDefault().FrameRules;
                }
                if (string.IsNullOrWhiteSpace(loaded.OutputDirectory))
                {
                    loaded.OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                }
                if (string.IsNullOrWhiteSpace(loaded.FileNameTemplate))
                {
                    loaded.FileNameTemplate = "{图号}_{图名}.pdf";
                }
                if (string.IsNullOrWhiteSpace(loaded.MergedFileNameTemplate))
                {
                    loaded.MergedFileNameTemplate = "{DWG名}_合并.pdf";
                }

                return loaded;
            }
            catch
            {
                return BatchPdfSettings.CreateDefault();
            }
        }

        public void SaveSettings(BatchPdfSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            try
            {
                string dir = Path.GetDirectoryName(_settingsFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                string json = FormatJsonPretty(serializer.Serialize(settings));
                File.WriteAllText(_settingsFilePath, json, Encoding.UTF8);
            }
            catch
            {
                // Non-fatal if user profile folder is temporarily locked
            }
        }

        private static string FormatJsonPretty(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            var sb = new StringBuilder();
            int indent = 0;
            bool inQuotes = false;
            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];
                if (ch == '"' && (i == 0 || json[i - 1] != '\\'))
                {
                    inQuotes = !inQuotes;
                    sb.Append(ch);
                }
                else if (!inQuotes)
                {
                    if (ch == '{' || ch == '[')
                    {
                        sb.Append(ch);
                        sb.AppendLine();
                        indent++;
                        sb.Append(new string(' ', indent * 2));
                    }
                    else if (ch == '}' || ch == ']')
                    {
                        sb.AppendLine();
                        indent = Math.Max(0, indent - 1);
                        sb.Append(new string(' ', indent * 2));
                        sb.Append(ch);
                    }
                    else if (ch == ',')
                    {
                        sb.Append(ch);
                        sb.AppendLine();
                        sb.Append(new string(' ', indent * 2));
                    }
                    else if (ch == ':')
                    {
                        sb.Append(": ");
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                }
                else
                {
                    sb.Append(ch);
                }
            }
            return sb.ToString();
        }
    }
}
