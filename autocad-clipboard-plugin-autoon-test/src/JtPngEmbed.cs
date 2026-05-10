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
    internal static class JtPngEmbed
    {
        public const string DefaultKeyword = "JSQCAD";

        private static readonly uint[] _crcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                t[n] = c;
            }
            return t;
        }

        private static uint Crc32(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < length; i++)
                crc = _crcTable[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        private static uint ReadUInt32BE(byte[] buf, int offset)
        {
            return ((uint)buf[offset] << 24) | ((uint)buf[offset + 1] << 16) |
                   ((uint)buf[offset + 2] << 8) | buf[offset + 3];
        }

        private static void WriteUInt32BE(MemoryStream ms, uint value)
        {
            ms.WriteByte((byte)(value >> 24));
            ms.WriteByte((byte)(value >> 16));
            ms.WriteByte((byte)(value >> 8));
            ms.WriteByte((byte)value);
        }

        private static int FindChunk(byte[] png, string type)
        {
            int i = 8; // skip 8-byte signature
            while (i + 8 <= png.Length)
            {
                uint len = ReadUInt32BE(png, i);
                string t = Encoding.ASCII.GetString(png, i + 4, 4);
                if (t == type) return i;
                long next = (long)i + 12L + len;
                if (next > int.MaxValue || next > png.Length) return -1;
                i = (int)next;
            }
            return -1;
        }

        /// <summary>
        /// 在 PNG 的 IEND 之前插入一个 tEXt chunk（key + 0 + text）。text 仅支持 Latin-1（base64 安全）。
        /// </summary>
        public static void WriteTextChunk(string pngPath, string keyword, string text)
        {
            if (string.IsNullOrEmpty(keyword) || keyword.Length > 79)
                throw new ArgumentException("keyword length must be 1..79");
            byte[] png = File.ReadAllBytes(pngPath);
            int iendPos = FindChunk(png, "IEND");
            if (iendPos < 0) throw new InvalidDataException("PNG IEND chunk not found");

            var latin1 = Encoding.GetEncoding("ISO-8859-1");
            byte[] kw = latin1.GetBytes(keyword);
            byte[] tx = latin1.GetBytes(text);
            byte[] data = new byte[kw.Length + 1 + tx.Length];
            Buffer.BlockCopy(kw, 0, data, 0, kw.Length);
            data[kw.Length] = 0;
            Buffer.BlockCopy(tx, 0, data, kw.Length + 1, tx.Length);

            byte[] type = Encoding.ASCII.GetBytes("tEXt");
            byte[] crcInput = new byte[type.Length + data.Length];
            Buffer.BlockCopy(type, 0, crcInput, 0, type.Length);
            Buffer.BlockCopy(data, 0, crcInput, type.Length, data.Length);
            uint crc = Crc32(crcInput, 0, crcInput.Length);

            using (var ms = new MemoryStream(png.Length + data.Length + 12))
            {
                ms.Write(png, 0, iendPos);
                WriteUInt32BE(ms, (uint)data.Length);
                ms.Write(type, 0, 4);
                ms.Write(data, 0, data.Length);
                WriteUInt32BE(ms, crc);
                ms.Write(png, iendPos, png.Length - iendPos);
                File.WriteAllBytes(pngPath, ms.ToArray());
            }
        }

        /// <summary>
        /// 读取 PNG 中第一个匹配 keyword 的 tEXt chunk text；找不到返回 null。
        /// </summary>
        public static string ReadTextChunk(string pngPath, string keyword)
        {
            byte[] png = File.ReadAllBytes(pngPath);
            var latin1 = Encoding.GetEncoding("ISO-8859-1");
            int i = 8;
            while (i + 8 <= png.Length)
            {
                uint len = ReadUInt32BE(png, i);
                string t = Encoding.ASCII.GetString(png, i + 4, 4);
                if (t == "tEXt")
                {
                    int dataStart = i + 8;
                    int dataEnd = dataStart + (int)len;
                    if (dataEnd > png.Length) return null;
                    int sep = Array.IndexOf(png, (byte)0, dataStart, (int)len);
                    if (sep > dataStart)
                    {
                        string kw = latin1.GetString(png, dataStart, sep - dataStart);
                        if (kw == keyword)
                            return latin1.GetString(png, sep + 1, dataEnd - sep - 1);
                    }
                }
                long next = (long)i + 12L + len;
                if (next > int.MaxValue || next > png.Length) return null;
                i = (int)next;
            }
            return null;
        }
    }

    /// <summary>
}
