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
    internal sealed class HotkeyBinding
    {
        public HotKeyModifiers Modifiers { get; private set; }
        public Keys Key { get; private set; }

        /// <summary>解析失败/空字符串时返回 null。</summary>
        public static HotkeyBinding TryParse(string hotkeyText)
        {
            if (string.IsNullOrWhiteSpace(hotkeyText)) return null;
            string[] parts = hotkeyText.Trim().Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            HotKeyModifiers modifiers = HotKeyModifiers.None;
            Keys? key = null;
            foreach (string rawPart in parts)
            {
                string part = rawPart.Trim();
                if (part.Equals("ctrl", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("control", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= HotKeyModifiers.Control;
                }
                else if (part.Equals("shift", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= HotKeyModifiers.Shift;
                }
                else if (part.Equals("alt", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= HotKeyModifiers.Alt;
                }
                else if (part.Equals("win", StringComparison.OrdinalIgnoreCase) ||
                         part.Equals("windows", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= HotKeyModifiers.Win;
                }
                else
                {
                    Keys parsed;
                    if (TryParseKey(part, out parsed)) key = parsed;
                }
            }
            if (!key.HasValue) return null;
            return new HotkeyBinding { Modifiers = modifiers, Key = key.Value };
        }

        public static HotkeyBinding Parse(string hotkeyText)
        {
            HotkeyBinding b = TryParse(hotkeyText);
            if (b != null) return b;
            // 默认值：Ctrl+Shift+~
            return new HotkeyBinding
            {
                Modifiers = HotKeyModifiers.Control | HotKeyModifiers.Shift,
                Key = Keys.Oemtilde
            };
        }

        private static bool TryParseKey(string raw, out Keys result)
        {
            result = Keys.None;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            if (raw == "~" || raw.Equals("tilde", StringComparison.OrdinalIgnoreCase))
            {
                result = Keys.Oemtilde;
                return true;
            }
            Keys parsed;
            if (Enum.TryParse(raw, true, out parsed))
            {
                result = parsed;
                return true;
            }
            if (raw.Length == 1)
            {
                char character = char.ToUpperInvariant(raw[0]);
                if (character >= 'A' && character <= 'Z')
                {
                    result = (Keys)Enum.Parse(typeof(Keys), character.ToString());
                    return true;
                }
                if (character >= '0' && character <= '9')
                {
                    result = (Keys)Enum.Parse(typeof(Keys), "D" + character);
                    return true;
                }
            }
            return false;
        }
    }
}
