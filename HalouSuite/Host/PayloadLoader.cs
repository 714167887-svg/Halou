using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using HalouSuite.Contract;

namespace HalouSuite.Host
{
    /// <summary>
    /// Payload 发现 / 加载 / 热重载 控制器。所有外部调用必须在 acad 主线程里。
    /// </summary>
    internal sealed class PayloadLoader : IDisposable, IHostServices
    {
        private const string PayloadFileGlob = "HalouPayload.*.dll";
        private const string PayloadEntryTypeName = "HalouSuite.Payload.PayloadEntry";

        /// <summary>当前 Host 提供的 API 级别。</summary>
        public const int CurrentHostApiLevel = 2;

        private readonly string _hostVersion;
        private readonly string _hostDir;
        private readonly string _payloadDir;
        private readonly string _configDir;
        private readonly string _stateDir;
        private readonly string _lkgFile;
        private readonly string _disabledFlagFile;
        private readonly object _swapLock = new object();

        private IPayload _current;
        private string _currentPath;
        private string _pendingReloadPath;

        public PayloadLoader(string hostVersion)
        {
            _hostVersion = hostVersion;
            _hostDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(_hostDir)) _hostDir = AppDomain.CurrentDomain.BaseDirectory;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _payloadDir = Path.Combine(localAppData, "HalouSuite", "payloads");
            _configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HalouCadSuite");
            _stateDir = Path.Combine(localAppData, "HalouSuite", "state");
            _lkgFile = Path.Combine(_stateDir, "lkg.txt");
            _disabledFlagFile = Path.Combine(_stateDir, "disabled.flag");

            Directory.CreateDirectory(_payloadDir);
            Directory.CreateDirectory(_configDir);
            Directory.CreateDirectory(_stateDir);

            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        public IPayload Current { get { return _current; } }
        public string CurrentPath { get { return _currentPath; } }
        public string LkgFile { get { return _lkgFile; } }
        public string DisabledFlagFile { get { return _disabledFlagFile; } }
        public bool IsDisabled { get { return File.Exists(_disabledFlagFile); } }

        // ===== IHostServices =====
        public int HostApiLevel { get { return CurrentHostApiLevel; } }
        public string HostVersion { get { return _hostVersion; } }
        public string HostDirectory { get { return _hostDir; } }
        public string PayloadDirectory { get { return _payloadDir; } }
        public string ConfigDirectory { get { return _configDir; } }

        public void RequestReload(string newPayloadDllPath, string reasonForBubble)
        {
            _pendingReloadPath = newPayloadDllPath;
            try { Application.Idle += OnIdleSwap; } catch { }
            WriteLine("\n[HalouHost] 已排队热重载: " + Path.GetFileName(newPayloadDllPath) + " (" + reasonForBubble + ")");
        }

        public void WriteLine(string message)
        {
            try
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null) doc.Editor.WriteMessage(message);
                else System.Diagnostics.Trace.WriteLine(message);
            }
            catch
            {
                System.Diagnostics.Trace.WriteLine(message);
            }
        }

        public void LoadInitial()
        {
            DiagLog.Write("Loader", "LoadInitial: payloadDir=" + _payloadDir + ", apiLevel=" + CurrentHostApiLevel);

            if (IsDisabled)
            {
                DiagLog.Write("Loader", "LoadInitial: DISABLED flag set at " + _disabledFlagFile);
                WriteLine("\n[HalouHost] Payload 已禁用（disabled.flag 存在），命令将不可用。HALOUENABLE 可解除。");
                return;
            }

            string dll = FindLatestPayloadDll();
            if (dll == null)
            {
                DiagLog.Write("Loader", "LoadInitial: NO payload dll found");
                WriteLine("\n[HalouHost] 未找到任何 HalouPayload.<ver>.dll，请重新安装。");
                return;
            }
            DiagLog.Write("Loader", "LoadInitial: trying " + dll);
            if (!TryLoadFromFile(dll, "initial"))
            {
                // 尝试 LKG 回退
                string lkg = ReadLkgPath();
                if (!string.IsNullOrEmpty(lkg) && !PathEquals(lkg, dll) && File.Exists(lkg))
                {
                    DiagLog.Write("Loader", "LoadInitial: falling back to LKG " + lkg);
                    WriteLine("\n[HalouHost] 新 Payload 加载失败，回退到上次正常版本: " + Path.GetFileName(lkg));
                    TryLoadFromFile(lkg, "lkg-fallback");
                }
            }
        }

        private void OnIdleSwap(object sender, EventArgs e)
        {
            try { Application.Idle -= OnIdleSwap; } catch { }
            string target = Interlocked.Exchange(ref _pendingReloadPath, null);
            if (string.IsNullOrEmpty(target)) return;
            ReloadFromFileSafe(target);
        }

        public void ReloadFromFileSafe(string dllPath)
        {
            if (IsDisabled)
            {
                WriteLine("\n[HalouHost] 当前处于 disabled 状态，已忽略热重载请求。HALOUENABLE 可解除。");
                DiagLog.Write("Loader", "Reload skipped: disabled");
                return;
            }
            string previousPath = _currentPath;
            try
            {
                ReloadFromFile(dllPath);
            }
            catch (System.Exception ex)
            {
                DiagLog.Write("Loader", "Reload FAIL: " + ex);
                WriteLine("\n[HalouHost] 热重载失败: " + ex.Message);
                // 回退：优先 LKG，其次旧路径
                string fallback = ReadLkgPath();
                if (string.IsNullOrEmpty(fallback) || PathEquals(fallback, dllPath) || !File.Exists(fallback))
                    fallback = previousPath;
                if (!string.IsNullOrEmpty(fallback) && File.Exists(fallback) && !PathEquals(fallback, dllPath))
                {
                    DiagLog.Write("Loader", "Reload fallback to " + fallback);
                    try { ReloadFromFile(fallback); }
                    catch (System.Exception ex2)
                    {
                        DiagLog.Write("Loader", "Fallback reload FAIL: " + ex2);
                        WriteLine("\n[HalouHost] 回退也失败: " + ex2.Message);
                    }
                }
            }
        }

        /// <summary>
        /// 尝试加载并 Activate Payload。失败时不抛异常，只写日志返回 false，并保留旧 _current。
        /// </summary>
        private bool TryLoadFromFile(string dllPath, string source)
        {
            lock (_swapLock)
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(dllPath);
                    DiagLog.Write("Loader", "Assembly.Load " + bytes.Length + " bytes from " + dllPath + " (" + source + ")");
                    Assembly asm = Assembly.Load(bytes);
                    Type entry = asm.GetType(PayloadEntryTypeName, true);
                    IPayload payload = (IPayload)Activator.CreateInstance(entry);

                    int required;
                    try { required = payload.RequiredHostApiLevel; }
                    catch (System.Exception exApi)
                    {
                        DiagLog.Write("Loader", "Read RequiredHostApiLevel FAIL: " + exApi.Message);
                        try { payload.Dispose(); } catch { }
                        WriteLine("\n[HalouHost] Payload 不兼容（无法读取 API 级别）。");
                        return false;
                    }
                    if (required > CurrentHostApiLevel)
                    {
                        DiagLog.Write("Loader", "API mismatch: payload requires " + required + ", host has " + CurrentHostApiLevel);
                        try { payload.Dispose(); } catch { }
                        WriteLine("\n[HalouHost] Payload v" + payload.Version + " 需要 Host API >= " + required +
                                  "，当前 Host API = " + CurrentHostApiLevel + "。请先升级 Host。");
                        return false;
                    }

                    payload.Activate(this);
                    _current = payload;
                    _currentPath = dllPath;
                    DiagLog.Write("Loader", "TryLoadFromFile OK, version=" + payload.Version + " api>=" + required);
                    WriteLkgPath(dllPath);
                    return true;
                }
                catch (System.Exception ex)
                {
                    DiagLog.Write("Loader", "TryLoadFromFile FAIL: " + ex);
                    return false;
                }
            }
        }

        private void ReloadFromFile(string dllPath)
        {
            lock (_swapLock)
            {
                DiagLog.Write("Loader", "Reload begin, target=" + dllPath);
                // 1. 先创建并校验新 Payload，再 Dispose 旧的，保证失败时旧的还能用
                byte[] bytes = File.ReadAllBytes(dllPath);
                Assembly asm = Assembly.Load(bytes);
                Type entry = asm.GetType(PayloadEntryTypeName, true);
                IPayload candidate = (IPayload)Activator.CreateInstance(entry);

                int required = candidate.RequiredHostApiLevel;
                if (required > CurrentHostApiLevel)
                {
                    try { candidate.Dispose(); } catch { }
                    string msg = "Payload v" + candidate.Version + " 需要 Host API >= " + required +
                                 "，当前 = " + CurrentHostApiLevel + "，已拒绝。旧 Payload 继续运行。";
                    DiagLog.Write("Loader", "Reload rejected (API): " + msg);
                    WriteLine("\n[HalouHost] " + msg);
                    return;
                }

                IPayload old = _current;
                string oldVer = old == null ? "(none)" : old.Version;
                _current = null;
                try { if (old != null) old.Dispose(); }
                catch (System.Exception ex) { WriteLine("\n[HalouHost] 旧 Payload Dispose 异常: " + ex.Message); DiagLog.Write("Loader", "old.Dispose FAIL: " + ex); }

                candidate.Activate(this);
                _current = candidate;
                _currentPath = dllPath;
                WriteLkgPath(dllPath);
                DiagLog.Write("Loader", "Reload done, " + oldVer + " -> " + candidate.Version);
                WriteLine("\n[HalouHost] Payload 已切换到 v" + candidate.Version + "（无需重启 CAD）。");
            }
        }

        // ===== LKG / disabled 操作 =====
        private void WriteLkgPath(string dllPath)
        {
            try { File.WriteAllText(_lkgFile, dllPath); }
            catch (System.Exception ex) { DiagLog.Write("Loader", "WriteLkgPath FAIL: " + ex.Message); }
        }

        public string ReadLkgPath()
        {
            try
            {
                if (!File.Exists(_lkgFile)) return null;
                string txt = File.ReadAllText(_lkgFile).Trim();
                return string.IsNullOrEmpty(txt) ? null : txt;
            }
            catch { return null; }
        }

        public void SetDisabled(bool disabled)
        {
            try
            {
                if (disabled) File.WriteAllText(_disabledFlagFile, DateTime.Now.ToString("o"));
                else if (File.Exists(_disabledFlagFile)) File.Delete(_disabledFlagFile);
                DiagLog.Write("Loader", "SetDisabled=" + disabled);
            }
            catch (System.Exception ex) { DiagLog.Write("Loader", "SetDisabled FAIL: " + ex.Message); }
        }

        private static bool PathEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        private string FindLatestPayloadDll()
        {
            string[] candidates = new string[0];
            try
            {
                if (Directory.Exists(_payloadDir))
                {
                    candidates = Directory.GetFiles(_payloadDir, PayloadFileGlob);
                }
            }
            catch { }

            if (candidates.Length == 0)
            {
                try { candidates = Directory.GetFiles(_hostDir, PayloadFileGlob); } catch { }
            }

            string bestPath = null;
            Version bestVer = null;
            for (int i = 0; i < candidates.Length; i++)
            {
                Version v = ParseVersionFromName(Path.GetFileName(candidates[i]));
                if (v == null) continue;
                if (bestVer == null || v > bestVer)
                {
                    bestVer = v;
                    bestPath = candidates[i];
                }
            }
            return bestPath;
        }

        private static Version ParseVersionFromName(string fileName)
        {
            const string prefix = "HalouPayload.";
            const string suffix = ".dll";
            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
            string mid = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
            Version v;
            return Version.TryParse(mid, out v) ? v : null;
        }

        private Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string simpleName = new AssemblyName(args.Name).Name;
            string p1 = Path.Combine(_hostDir, simpleName + ".dll");
            if (File.Exists(p1)) return Assembly.LoadFrom(p1);
            string p2 = Path.Combine(_payloadDir, simpleName + ".dll");
            if (File.Exists(p2)) return Assembly.LoadFrom(p2);
            return null;
        }

        public void Dispose()
        {
            try { Application.Idle -= OnIdleSwap; } catch { }
            try { if (_current != null) _current.Dispose(); } catch { }
            _current = null;
            try { AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve; } catch { }
        }
    }
}
