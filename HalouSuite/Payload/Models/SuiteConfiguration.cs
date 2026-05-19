using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using Clipboard = System.Windows.Forms.Clipboard;
using DrawingColor = System.Drawing.Color;
using DrawingFont = System.Drawing.Font;
using Keys = System.Windows.Forms.Keys;

namespace HalouSuite.Payload
{
    internal sealed class SuiteConfiguration
    {
        public string ManifestSource { get; set; }
        public string CredentialHeader { get; set; }
        public string CredentialValue { get; set; }
        public string Hotkey { get; set; }
        public int AutoRefreshSeconds { get; set; }
        public string AccountName { get; set; }
        public string AccountToken { get; set; }
        public string AccountEndpoint { get; set; }
        public string LicenseEndpoint { get; set; }
        /// <summary>功能级快捷键：featureId → hotkey 字符串（如 "Ctrl+Alt+Z"）。空 = 未设置。</summary>
        public Dictionary<string, string> FeatureHotkeys { get; set; }
        /// <summary>功能级 AutoCAD 命令别名：featureId → 命令名（如 "MYZK"）。空 = 未设置。</summary>
        public Dictionary<string, string> FeatureCommands { get; set; }
        /// <summary>功能级自动加载开关：featureId → bool。开 = 每次新建/打开 CAD 文档时自动 (load) 对应 LSP，无需用户先点功能。</summary>
        public Dictionary<string, bool> AutoLoadFeatures { get; set; }

        public static SuiteConfiguration Load(string path, string localManifestPath)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try
                {
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    SuiteConfiguration configuration = serializer.Deserialize<SuiteConfiguration>(File.ReadAllText(path));
                    if (configuration != null)
                    {
                        configuration.Normalize(localManifestPath);
                        return configuration;
                    }
                }
                catch
                {
                }
            }

            SuiteConfiguration fallback = CreateDefault(localManifestPath);
            fallback.Save(path);
            return fallback;
        }

        public void Save(string path)
        {
            Normalize(null);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            File.WriteAllText(path, serializer.Serialize(this));
        }

        public static SuiteConfiguration CreateDefault(string localManifestPath)
        {
            return new SuiteConfiguration
            {
                ManifestSource = localManifestPath,
                CredentialHeader = "Authorization",
                CredentialValue = string.Empty,
                Hotkey = "Ctrl+Shift+~",
                AutoRefreshSeconds = 300,
                AccountName = string.Empty,
                AccountToken = string.Empty,
                AccountEndpoint = string.Empty,
                LicenseEndpoint = PayloadConstants.DefaultLicenseEndpoint,
                FeatureHotkeys = new Dictionary<string, string>(),
                FeatureCommands = new Dictionary<string, string>(),
                AutoLoadFeatures = new Dictionary<string, bool>()
            };
        }

        private void Normalize(string localManifestPath)
        {
            if (string.IsNullOrWhiteSpace(ManifestSource) && !string.IsNullOrWhiteSpace(localManifestPath))
            {
                ManifestSource = localManifestPath;
            }

            if (string.IsNullOrWhiteSpace(CredentialHeader))
            {
                CredentialHeader = "Authorization";
            }

            if (string.IsNullOrWhiteSpace(Hotkey))
            {
                Hotkey = "Ctrl+Shift+~";
            }

            AutoRefreshSeconds = Math.Max(AutoRefreshSeconds, 60);

            if (AccountName == null) AccountName = string.Empty;
            if (AccountToken == null) AccountToken = string.Empty;
            if (AccountEndpoint == null) AccountEndpoint = string.Empty;
            if (FeatureHotkeys == null) FeatureHotkeys = new Dictionary<string, string>();
            if (FeatureCommands == null) FeatureCommands = new Dictionary<string, string>();
            if (AutoLoadFeatures == null) AutoLoadFeatures = new Dictionary<string, bool>();
            if (string.IsNullOrWhiteSpace(LicenseEndpoint))
            {
                LicenseEndpoint = PayloadConstants.DefaultLicenseEndpoint;
            }
            // 迁移：已保存了旧 W 仓库 URL 的客户端自动切到新发布仓库
            else if (LicenseEndpoint.IndexOf("/714167887-svg/W/", StringComparison.OrdinalIgnoreCase) >= 0
                  && LicenseEndpoint.IndexOf("license.json", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                LicenseEndpoint = PayloadConstants.DefaultLicenseEndpoint;
            }
            // v2.0.51: 迁移 jsDelivr URL → raw（jsDelivr CDN purge throttle 死锁问题）
            // 凡是指向 halou-release/license.json 的 jsDelivr URL，无论 @main 还是 @<sha>，
            // 一律强制切到 raw.githubusercontent.com（v2.0.44 起的官方主路径）。
            // 触发场景：客户在 v2.0.40~v2.0.43 期间装的 Payload 把 jsDelivr URL 写进了
            // 本地 SuiteConfiguration.json；升级到 v2.0.44+ 后该字段优先于 DefaultLicenseEndpoint，
            // 导致 license 检查仍走被 CDN 缓存锁死的 jsDelivr，面板版本号永远停在旧值。
            else if (LicenseEndpoint.IndexOf("cdn.jsdelivr.net", StringComparison.OrdinalIgnoreCase) >= 0
                  && LicenseEndpoint.IndexOf("halou-release", StringComparison.OrdinalIgnoreCase) >= 0
                  && LicenseEndpoint.IndexOf("license.json", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                LicenseEndpoint = PayloadConstants.DefaultLicenseEndpoint;
            }
        }
    }
}
