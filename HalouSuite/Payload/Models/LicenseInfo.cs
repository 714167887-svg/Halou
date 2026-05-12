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
    internal sealed class LicenseInfo
    {
        // 老字段：服务于 1.x 宿主 JsqClipboardCadPlugin.dll 自更新
        public string LatestVersion { get; set; }
        public string DownloadUrl { get; set; }
        // 新字段：Phase 2 Payload 热更新
        public string LatestPayloadVersion { get; set; }
        public string PayloadDownloadUrl { get; set; }
        // 多 ARX SDK 分发（如 payload_download_url_arx25）：tag → url
        // 客户端按本进程 acmgd 大版本（24=arx24/25=arx25）优先匹配，未命中回退到 PayloadDownloadUrl
        public Dictionary<string, string> PayloadDownloadUrls { get; set; }
        public string ReleaseNotes { get; set; }
        public bool DefaultAllowed { get; set; }
        public bool KillSwitch { get; set; }
        public string KillReason { get; set; }
        public Dictionary<string, LicenseAccountInfo> Accounts { get; set; }

        public static LicenseInfo Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            // 剥 UTF-8 BOM：JavaScriptSerializer 不会自动处理，遇到“\uFEFF{...}”会报「无效的 JSON 基元」。
            if (json[0] == '\uFEFF') json = json.Substring(1);
            json = json.TrimStart(' ', '\t', '\r', '\n');

            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> raw = serializer.Deserialize<Dictionary<string, object>>(json);
            if (raw == null)
            {
                return null;
            }

            LicenseInfo info = new LicenseInfo
            {
                LatestVersion = GetString(raw, "latest_version"),
                DownloadUrl = GetString(raw, "download_url"),
                LatestPayloadVersion = GetString(raw, "latest_payload_version"),
                PayloadDownloadUrl = GetString(raw, "payload_download_url"),
                PayloadDownloadUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ReleaseNotes = GetString(raw, "release_notes"),
                DefaultAllowed = GetBool(raw, "default_allowed", false),
                KillSwitch = GetBool(raw, "kill_switch", false),
                KillReason = GetString(raw, "kill_reason"),
                Accounts = new Dictionary<string, LicenseAccountInfo>(StringComparer.OrdinalIgnoreCase)
            };

            // 扫描所有 payload_download_url_<tag> 字段
            const string prefix = "payload_download_url_";
            foreach (KeyValuePair<string, object> kv in raw)
            {
                if (kv.Key == null) continue;
                if (kv.Key.Length <= prefix.Length) continue;
                if (!kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string tag = kv.Key.Substring(prefix.Length).Trim();
                if (string.IsNullOrWhiteSpace(tag)) continue;
                string url = kv.Value == null ? null : kv.Value.ToString();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    info.PayloadDownloadUrls[tag] = url.Trim();
                }
            }

            object accountsObj;
            if (raw.TryGetValue("accounts", out accountsObj) && accountsObj is Dictionary<string, object>)
            {
                Dictionary<string, object> accountMap = (Dictionary<string, object>)accountsObj;
                foreach (KeyValuePair<string, object> kv in accountMap)
                {
                    Dictionary<string, object> sub = kv.Value as Dictionary<string, object>;
                    if (sub == null) continue;
                    info.Accounts[kv.Key] = new LicenseAccountInfo
                    {
                        Allowed = GetBool(sub, "allowed", true),
                        Reason = GetString(sub, "reason"),
                        Note = GetString(sub, "note"),
                        ExpiresAt = GetString(sub, "expires_at"),
                        Features = GetStringList(sub, "features"),
                        Password = GetString(sub, "password")
                    };
                }
            }

            return info;
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            object v;
            return (d != null && d.TryGetValue(key, out v) && v != null) ? v.ToString() : null;
        }

        private static bool GetBool(Dictionary<string, object> d, string key, bool def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            bool parsed;
            return bool.TryParse(v.ToString(), out parsed) ? parsed : def;
        }

        private static List<string> GetStringList(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            System.Collections.ArrayList arr = v as System.Collections.ArrayList;
            if (arr == null)
            {
                object[] objArr = v as object[];
                if (objArr != null)
                {
                    arr = new System.Collections.ArrayList(objArr);
                }
            }
            if (arr == null) return null;
            List<string> result = new List<string>();
            foreach (object item in arr)
            {
                if (item == null) continue;
                string s = item.ToString();
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s.Trim());
            }
            return result;
        }
    }
}
