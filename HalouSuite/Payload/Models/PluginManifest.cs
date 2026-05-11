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
    internal sealed class PluginManifest
    {
        public string Version { get; set; }
        public string UpdatedAt { get; set; }
        public List<CadPluginFeature> Features { get; set; }
        public string BaseDirectory { get; set; }

        public static PluginManifest Load(
            SuiteConfiguration configuration,
            string localManifestPath,
            string manifestCachePath,
            out string statusMessage)
        {
            PluginManifest manifest;
            string sourceDescription;

            if (TryLoadFromSource(configuration.ManifestSource, configuration, out manifest, out sourceDescription))
            {
                manifest.BaseDirectory = InferBaseDirectory(configuration.ManifestSource, localManifestPath);
                PersistCache(manifestCachePath, manifest);
                statusMessage = string.Format("已从 {0} 更新到 {1}", sourceDescription, manifest.Version ?? "未标注版本");
                return Normalize(manifest, manifest.BaseDirectory);
            }

            if (TryLoadFromFile(localManifestPath, out manifest))
            {
                manifest.BaseDirectory = Path.GetDirectoryName(localManifestPath);
                statusMessage = string.Format("使用本地清单 {0}", localManifestPath);
                return Normalize(manifest, manifest.BaseDirectory);
            }

            if (TryLoadFromFile(manifestCachePath, out manifest))
            {
                manifest.BaseDirectory = Path.GetDirectoryName(manifestCachePath);
                statusMessage = "远端不可用，已回退到本地缓存清单";
                return Normalize(manifest, manifest.BaseDirectory);
            }

            manifest = CreateDefault();
            manifest.BaseDirectory = Path.GetDirectoryName(localManifestPath);
            statusMessage = "未找到可用清单，已启用内置默认功能";
            return Normalize(manifest, manifest.BaseDirectory);
        }

        private static bool TryLoadFromSource(string source, SuiteConfiguration configuration, out PluginManifest manifest, out string sourceDescription)
        {
            manifest = null;
            sourceDescription = null;
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            string normalized = source.Trim();
            try
            {
                string json;
                if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    Dictionary<string, string> headers = null;
                    if (!string.IsNullOrWhiteSpace(configuration.CredentialHeader) &&
                        !string.IsNullOrWhiteSpace(configuration.CredentialValue))
                    {
                        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        headers[configuration.CredentialHeader.Trim()] = configuration.CredentialValue.Trim();
                    }
                    json = RobustHttp.DownloadString(normalized, headers, s =>
                    {
                        string t = s == null ? "" : s.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                        return t.Length > 0 && t[0] == '{';
                    });
                }
                else
                {
                    string filePath = Path.IsPathRooted(normalized)
                        ? normalized
                        : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(localManifestPathPlaceholder), normalized));
                    json = File.ReadAllText(filePath);
                }

                manifest = Deserialize(json);
                sourceDescription = normalized;
                return manifest != null;
            }
            catch
            {
                manifest = null;
                sourceDescription = null;
                return false;
            }
        }

        private static readonly string localManifestPathPlaceholder = Path.Combine(
            SafeAssemblyDirectory(),
            "halou-plugin-manifest.json");

        private static string SafeAssemblyDirectory()
        {
            try
            {
                var loc = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    var d = Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(d)) return d;
                }
            }
            catch { }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private static bool TryLoadFromFile(string path, out PluginManifest manifest)
        {
            manifest = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                manifest = Deserialize(File.ReadAllText(path));
                return manifest != null;
            }
            catch
            {
                manifest = null;
                return false;
            }
        }

        private static PluginManifest Deserialize(string json)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue
            };
            return serializer.Deserialize<PluginManifest>(json);
        }

        // 供 HalouSuiteManager.TrySyncManifestFromCdn 校验下载内容用：解析失败返回 null，
        // 不抛异常，便于调用方丢掉脏数据。
        internal static PluginManifest TryDeserialize(string json)
        {
            try { return Deserialize(json); } catch { return null; }
        }

        private static PluginManifest Normalize(PluginManifest manifest, string baseDirectory)
        {
            if (manifest == null)
            {
                manifest = CreateDefault();
            }

            if (manifest.Features == null)
            {
                manifest.Features = new List<CadPluginFeature>();
            }

            manifest.BaseDirectory = baseDirectory;
            return manifest;
        }

        private static void PersistCache(string manifestCachePath, PluginManifest manifest)
        {
            if (manifest == null || string.IsNullOrWhiteSpace(manifestCachePath))
            {
                return;
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            File.WriteAllText(manifestCachePath, serializer.Serialize(manifest));
        }

        private static string InferBaseDirectory(string source, string localManifestPath)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return Path.GetDirectoryName(localManifestPath);
            }

            string normalized = source.Trim();
            if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(localManifestPath);
            }

            string filePath = Path.IsPathRooted(normalized)
                ? normalized
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(localManifestPath), normalized));
            return Path.GetDirectoryName(filePath);
        }

        private static PluginManifest CreateDefault()
        {
            return new PluginManifest
            {
                Version = "built-in",
                UpdatedAt = DateTime.Now.ToString("s"),
                Features = new List<CadPluginFeature>
                {
                    new CadPluginFeature
                    {
                        Id = "zk",
                        Title = "展开功能(开缺待完善)",
                        Description = "加载并执行 ZKK 的 AutoLISP 入口。",
                        Kind = "lisp",
                        LoadPath = "ZK/ZKK_Unfold_V13.lsp",
                        Command = "ZKK",
                        Enabled = true
                    },
                    new CadPluginFeature
                    {
                        Id = "kb",
                        Title = "开板耗材计算",
                        Description = "选闭合钣金截面多段线，按 ZKK 算法展开并计算可开支数（默认 1210mm），在截面下方红色标注。",
                        Kind = "lisp",
                        LoadPath = "KB/KB_Yield.lsp",
                        Command = "KB",
                        Enabled = true
                    },
                    new CadPluginFeature
                    {
                        Id = "jt",
                        Title = "方框截图",
                        Description = "选一个方框，自动拷到剪贴板并导出 PNG 到 E:/halou wode/W/JT 目录。",
                        Kind = "lisp",
                        LoadPath = "JT/JT_Snapshot.lsp",
                        Command = "JT",
                        Enabled = true
                    },
                    new CadPluginFeature
                    {
                        Id = "ole",
                        Title = "OLE 批量导入图片",
                        Description = "扫描当前图纸所在目录，逐张以 OLE 方式插入当前 CAD 图纸；单张最大 2500×2500mm，横向间距 150mm。",
                        Kind = "lisp",
                        LoadPath = "OLE/oleimgdir.lsp",
                        Command = "OLE",
                        Enabled = true
                    }
                }
            };
        }
    }
}
