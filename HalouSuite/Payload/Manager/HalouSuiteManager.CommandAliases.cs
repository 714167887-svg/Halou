using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

namespace HalouSuite.Payload
{
    // 动态命令别名：优先 Autodesk.AutoCAD.Internal.Utils.AddCommand（反射）；失败时通过 LISP defun 兜底。
    // 字段 _registeredAliases / 常量 DynamicCommandGroup 见主文件。
    internal sealed partial class HalouSuiteManager
    {
        /// <summary>
        /// 根据 configuration.FeatureCommands 动态注册 AutoCAD 命令别名。
        /// 用户在命令行敲该别名即触发对应功能。通过反射调用 Autodesk.AutoCAD.Internal.Utils
        /// 以避免硬依赖内部类型；若当前 AutoCAD 版本不支持则静默忽略并写状态。
        /// </summary>
        private void ApplyCommandAliases()
        {
            // 先注销之前注册的
            foreach (string alias in _registeredAliases)
            {
                try { TryRemoveAcadCommand(DynamicCommandGroup, alias); } catch { }
            }
            _registeredAliases.Clear();

            if (_configuration == null || _configuration.FeatureCommands == null) return;
            if (_manifest == null || _manifest.Features == null) return;

            HashSet<string> validFeatureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CadPluginFeature f in _manifest.Features)
            {
                if (f != null && !string.IsNullOrWhiteSpace(f.Id)) validFeatureIds.Add(f.Id);
            }

            List<string> conflicts = new List<string>();
            HashSet<string> usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // featureId -> 其 manifest 里的原生 Command（如 ZK14），用于识别与别名同名的冗余项
            Dictionary<string, string> featureNativeCommand = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (CadPluginFeature f in _manifest.Features)
            {
                if (f != null && !string.IsNullOrWhiteSpace(f.Id) && !string.IsNullOrWhiteSpace(f.Command))
                {
                    featureNativeCommand[f.Id] = f.Command.Trim().ToUpperInvariant();
                }
            }

            foreach (KeyValuePair<string, string> kv in _configuration.FeatureCommands)
            {
                string featureId = kv.Key;
                string alias = (kv.Value ?? string.Empty).Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(featureId) || string.IsNullOrWhiteSpace(alias)) continue;
                if (!validFeatureIds.Contains(featureId)) continue;
                if (!IsValidAcadCommandName(alias)) { conflicts.Add(alias + "(命名非法)"); continue; }
                if (usedAliases.Contains(alias)) { conflicts.Add(alias + "(重复)"); continue; }

                // 如果别名与 LSP 已定义的原生命令同名（例如 OLE 功能的 Command=OLE），说明冗余且会和 LISP 的 c:OLE 冲突，直接跳过
                string nativeCmd;
                if (featureNativeCommand.TryGetValue(featureId, out nativeCmd)
                    && string.Equals(nativeCmd, alias, StringComparison.OrdinalIgnoreCase))
                {
                    usedAliases.Add(alias);
                    continue;
                }

                string idCopy = featureId;
                Action handler = delegate { RunFeatureById(idCopy); };
                try
                {
                    if (TryAddAcadCommand(DynamicCommandGroup, alias, handler))
                    {
                        _registeredAliases.Add(alias);
                        usedAliases.Add(alias);
                    }
                    else if (TryAddLispCommand(alias, idCopy))
                    {
                        _registeredAliases.Add(alias);
                        usedAliases.Add(alias);
                    }
                    else
                    {
                        conflicts.Add(alias + "(API 不可用)");
                    }
                }
                catch (System.Exception ex)
                {
                    System.Exception real = ex;
                    while (real.InnerException != null) real = real.InnerException;
                    // Utils.AddCommand 失败时，尝试 LISP 兜底
                    if (TryAddLispCommand(alias, idCopy))
                    {
                        _registeredAliases.Add(alias);
                        usedAliases.Add(alias);
                    }
                    else
                    {
                        conflicts.Add(string.Format("{0}({1})", alias, real.Message));
                    }
                }
            }
            if (conflicts.Count > 0)
            {
                string extra = "命令别名注册失败：" + string.Join("，", conflicts.ToArray());
                _statusMessage = string.IsNullOrEmpty(_statusMessage) ? extra : (_statusMessage + "；" + extra);
            }
        }

        private static bool IsValidAcadCommandName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (s.Length < 1 || s.Length > 32) return false;
            if (!char.IsLetter(s[0])) return false;
            foreach (char c in s)
            {
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            }
            return true;
        }

        private static Type ResolveAcadType(string fullName)
        {
            // 优先在已加载的 acmgd / acdbmgd 程序集里找
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName = asm.GetName().Name;
                if (!string.Equals(asmName, "acmgd", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(asmName, "acdbmgd", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(asmName, "accoremgd", StringComparison.OrdinalIgnoreCase)) continue;
                Type t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        private static bool TryAddAcadCommand(string group, string globalName, Action callback)
        {
            Type utilsType = ResolveAcadType("Autodesk.AutoCAD.Internal.Utils");
            Type callbackType = ResolveAcadType("Autodesk.AutoCAD.Runtime.CommandCallback");
            if (utilsType == null || callbackType == null) return false;

            Delegate del = Delegate.CreateDelegate(callbackType, callback.Target, callback.Method);
            MethodInfo addCommand = utilsType.GetMethod(
                "AddCommand",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(string), typeof(string), typeof(CommandFlags), callbackType },
                null);
            if (addCommand == null) return false;
            addCommand.Invoke(null, new object[] { group, globalName, globalName, CommandFlags.Modal, del });
            return true;
        }

        private static void TryRemoveAcadCommand(string group, string globalName)
        {
            Type utilsType = ResolveAcadType("Autodesk.AutoCAD.Internal.Utils");
            if (utilsType == null) return;
            MethodInfo rm = utilsType.GetMethod(
                "RemoveCommand",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(string) },
                null);
            if (rm == null) return;
            rm.Invoke(null, new object[] { group, globalName });
        }

        /// <summary>
        /// 用 LISP defun 在当前文档里动态定义一个 c:ALIAS 命令，作为 Internal.Utils.AddCommand 失败时的兜底方案。
        /// 如果 feature 自带原生 Command（如 ZK14），则 (defun c:ZK () (c:ZK14))；
        /// 否则只打印一行说明（占位 feature 的场景）。
        /// </summary>
        private bool TryAddLispCommand(string alias, string featureId)
        {
            try
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return false;

                string nativeCmd = null;
                if (_manifest != null && _manifest.Features != null)
                {
                    foreach (CadPluginFeature f in _manifest.Features)
                    {
                        if (f != null && string.Equals(f.Id, featureId, StringComparison.OrdinalIgnoreCase))
                        {
                            nativeCmd = (f.Command ?? string.Empty).Trim();
                            break;
                        }
                    }
                }

                // 取该 feature 的 LSP 路径（已解压到 EmbeddedRoot），用于运行时兜底 (load "...")
                string lispLoadExpr = null;
                if (_manifest != null && _manifest.Features != null)
                {
                    foreach (CadPluginFeature f in _manifest.Features)
                    {
                        if (f != null && string.Equals(f.Id, featureId, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(f.LoadPath))
                        {
                            string lp = ResolveManifestPath(f.LoadPath);
                            if (!string.IsNullOrWhiteSpace(lp) && File.Exists(lp))
                            {
                                lispLoadExpr = "(load \"" + lp.Replace('\\', '/').Replace("\"", "\\\"") + "\")";
                            }
                            break;
                        }
                    }
                }

                string body;
                if (!string.IsNullOrWhiteSpace(nativeCmd) && IsValidAcadCommandName(nativeCmd))
                {
                    // 别名调用：c:XXXX 已定义直接调；未定义则尝试加载对应 LSP 后再调；仍失败才报错。
                    if (!string.IsNullOrWhiteSpace(lispLoadExpr))
                    {
                        body = string.Format(
                            "(if c:{0} (c:{0}) (progn {1} (if c:{0} (c:{0}) (princ \"\\n[HALOU] 功能未加载\"))))",
                            nativeCmd, lispLoadExpr);
                    }
                    else
                    {
                        body = string.Format("(if c:{0} (c:{0}) (princ \"\\n[HALOU] 功能未加载\"))", nativeCmd);
                    }
                }
                else
                {
                    body = string.Format("(princ \"\\n[HALOU] 占位功能 {0}：暂无 LSP 实现\")", featureId.Replace("\"", "\\\""));
                }

                string defun = string.Format("(defun c:{0} () {1} (princ))", alias, body);
                doc.SendStringToExecute(defun + "\n", false, false, false);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
