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
    /// <summary>捕获键盘输入并格式化为 "Ctrl+Alt+Z" 样式的只读 TextBox。</summary>
    internal sealed class HotkeyCaptureTextBox : System.Windows.Forms.TextBox
    {
        /// <summary>
        /// true：允许用户纯文本输入（用于把功能绑定成 AutoCAD 命令别名，如 "MYZK"）。
        /// 按下带修饰键的组合键仍会被捕获为 "Ctrl+X" 格式。
        /// false：只接受组合键，禁止文字输入（用于全局弹窗热键）。
        /// </summary>
        public bool AllowPlainText { get; set; }

        public HotkeyCaptureTextBox()
        {
            BackColor = DrawingColor.White;
            Font = new DrawingFont("Microsoft YaHei UI", 9f);
            ShortcutsEnabled = false;
            Cursor = System.Windows.Forms.Cursors.IBeam;
        }

        protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
        {
            if (!Focused) return base.ProcessCmdKey(ref msg, keyData);

            Keys key = keyData & Keys.KeyCode;
            bool ctrl = (keyData & Keys.Control) == Keys.Control;
            bool shift = (keyData & Keys.Shift) == Keys.Shift;
            bool alt = (keyData & Keys.Alt) == Keys.Alt;

            // Esc 清空（两种模式都支持）
            if (key == Keys.Escape)
            {
                Text = string.Empty;
                return true;
            }

            // 只修饰键本身，忽略
            if (key == Keys.None || key == Keys.ControlKey || key == Keys.ShiftKey
                || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin)
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }

            // 带修饰键 → 捕获为组合热键（覆盖已有内容）
            if (ctrl || alt || (shift && !AllowPlainText))
            {
                // AllowPlainText 场景下 Shift+字母应该只是大写字母，不算组合
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                if (ctrl) sb.Append("Ctrl+");
                if (shift) sb.Append("Shift+");
                if (alt) sb.Append("Alt+");
                sb.Append(FormatKey(key));
                Text = sb.ToString();
                SelectionStart = Text.Length;
                return true;
            }

            // 非组合键
            if (AllowPlainText)
            {
                // 允许正常输入（由 TextBox 默认处理）
                return base.ProcessCmdKey(ref msg, keyData);
            }

            // 纯热键模式：无修饰键时不予接受（避免误录入）
            if (key == Keys.Back || key == Keys.Delete)
            {
                Text = string.Empty;
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private static string FormatKey(Keys key)
        {
            if (key >= Keys.D0 && key <= Keys.D9) return ((char)('0' + (key - Keys.D0))).ToString();
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return "Num" + (int)(key - Keys.NumPad0);
            if (key == Keys.Oemtilde) return "~";
            return key.ToString();
        }
    }
}
