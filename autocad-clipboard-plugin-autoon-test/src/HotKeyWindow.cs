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

namespace JsqClipboardCadPlugin
{
    internal sealed class HotKeyWindow : System.Windows.Forms.NativeWindow, IDisposable
    {
        public const int PaletteHotKeyId = 4096;
        public const int FeatureHotKeyStart = 4097;
        private const int WmHotKey = 0x0312;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly HashSet<int> _registeredIds = new HashSet<int>();

        public event EventHandler<HotKeyPressedEventArgs> HotKeyPressed;

        public HotKeyWindow()
        {
            CreateHandle(new System.Windows.Forms.CreateParams());
        }

        public bool Register(int id, HotKeyModifiers modifiers, Keys key)
        {
            Unregister(id);
            bool ok = RegisterHotKey(Handle, id, (uint)modifiers, (uint)key);
            if (ok) _registeredIds.Add(id);
            return ok;
        }

        public void Unregister(int id)
        {
            if (_registeredIds.Remove(id))
            {
                UnregisterHotKey(Handle, id);
            }
        }

        public void UnregisterAll()
        {
            foreach (int id in _registeredIds.ToArray())
            {
                UnregisterHotKey(Handle, id);
            }
            _registeredIds.Clear();
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WmHotKey && HotKeyPressed != null)
            {
                HotKeyPressed(this, new HotKeyPressedEventArgs((int)m.WParam));
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            UnregisterAll();
            DestroyHandle();
        }
    }
}
