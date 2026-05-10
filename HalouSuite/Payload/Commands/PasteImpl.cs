using System;
using System.IO;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Clipboard = System.Windows.Forms.Clipboard;

namespace HalouSuite.Payload.Commands
{
    /// <summary>
    /// 从 1.1.74 ClipboardCommands.cs 移植过来的剪贴板命令实现。
    /// 由 PayloadEntry.HookPasteClip / UnhookPasteClip / PasteFromClipboard /
    /// PasteFromFile / PasteClipOverrideHandled 调用。
    /// 业务逻辑（InsertPayload + 解析 + Build*）与 1.1.74 完全一致。
    /// </summary>
    internal static class PasteImpl
    {
        private const string FormatPrefix = "JSQCAD:";

        // -------- 命令入口 --------
        public static void HookPasteClip(bool silent)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            if (!silent)
            {
                doc.Editor.WriteMessage(
                    "\nEnabling Ctrl+V hook for this AutoCAD session. Clipboard text with JSQCAD: will be drawn as CAD geometry.");
            }
            doc.SendStringToExecute("._UNDEFINE PASTECLIP ", true, false, false);
        }

        public static void UnhookPasteClip()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            doc.Editor.WriteMessage("\nRestoring native PASTECLIP for this AutoCAD session.");
            doc.SendStringToExecute("._REDEFINE PASTECLIP ", true, false, false);
        }

        public static void PasteFromClipboard()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            string raw = ReadClipboardText();
            if (string.IsNullOrWhiteSpace(raw))
            {
                ed.WriteMessage("\nClipboard is empty or does not contain text.");
                return;
            }
            try
            {
                int count = InsertPayload(raw, doc, true);
                ed.WriteMessage("\nJSQPASTE created {0} entit{1}.", count, count == 1 ? "y" : "ies");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nJSQPASTE failed: {0}", ex.Message);
            }
        }

        public static void PasteFromFile()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            PromptOpenFileOptions options = new PromptOpenFileOptions("\nSelect a JSQCAD JSON file");
            options.Filter = "JSON Files (*.json)|*.json|Text Files (*.txt)|*.txt|All Files (*.*)|*.*";
            PromptFileNameResult result = ed.GetFileNameForOpen(options);
            if (result.Status != PromptStatus.OK) return;
            try
            {
                string raw = File.ReadAllText(result.StringResult);
                int count = InsertPayload(raw, doc, true);
                ed.WriteMessage("\nJSQPASTEFILE created {0} entit{1}.", count, count == 1 ? "y" : "ies");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nJSQPASTEFILE failed: {0}", ex.Message);
            }
        }

        /// <summary>
        /// 返回 true = 已自行处理 JSQCAD 数据；返回 false = 不是 JSQ 格式，Host 回落原生 PASTECLIP。
        /// </summary>
        public static bool PasteClipOverrideHandled()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return false;
            Editor ed = doc.Editor;
            string raw = ReadClipboardText();
            if (!LooksLikeJsqPayload(raw)) return false;
            try
            {
                int count = InsertPayload(raw, doc, true);
                ed.WriteMessage("\nPASTECLIP routed to JSQCAD and created {0} entit{1}.", count, count == 1 ? "y" : "ies");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nJSQCAD clipboard payload is invalid: {0}", ex.Message);
            }
            return true;
        }

        // -------- 业务核心：InsertPayload + helpers（与 1.1.74 一致） --------
        private static int InsertPayload(string raw, Document doc, bool promptForInsertionPoint)
        {
            CadClipboardPayload payload = ParsePayload(raw);
            if (payload == null) throw new InvalidOperationException("Could not parse payload.");
            if (payload.entities == null || payload.entities.Count == 0)
                throw new InvalidOperationException("Payload does not contain any entities.");

            Editor ed = doc.Editor;
            Database db = doc.Database;
            double unitScale = ResolveUnitScale(payload.units);
            Point2d basePoint = ToPoint2d(payload.basePoint, unitScale, 0.0, 0.0);
            Point3d insertionPoint = Point3d.Origin;

            if (promptForInsertionPoint)
            {
                PromptPointOptions pointOptions = new PromptPointOptions("\nSpecify insertion point <0,0>: ");
                pointOptions.AllowNone = true;
                PromptPointResult pointResult = ed.GetPoint(pointOptions);
                if (pointResult.Status == PromptStatus.Cancel)
                    throw new InvalidOperationException("Command canceled.");
                if (pointResult.Status == PromptStatus.OK)
                    insertionPoint = pointResult.Value;
            }

            double offsetX = insertionPoint.X - basePoint.X;
            double offsetY = insertionPoint.Y - basePoint.Y;

            int created = 0;
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord currentSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                EnsureLayers(payload, db, tr);

                foreach (CadEntityDefinition entityDef in payload.entities)
                {
                    Entity entity = BuildEntity(entityDef, unitScale, offsetX, offsetY);
                    if (entity == null) continue;
                    ApplyEntityProperties(entity, entityDef, db, tr);
                    currentSpace.AppendEntity(entity);
                    tr.AddNewlyCreatedDBObject(entity, true);
                    created++;
                }
                tr.Commit();
            }
            return created;
        }

        private static CadClipboardPayload ParsePayload(string raw)
        {
            string normalized = NormalizePayload(raw);
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            CadClipboardPayload payload = serializer.Deserialize<CadClipboardPayload>(normalized);
            if (payload == null) return null;
            if (!string.IsNullOrWhiteSpace(payload.format) &&
                !payload.format.Equals("JSQCAD/1.0", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsupported format: " + payload.format);
            return payload;
        }

        private static string NormalizePayload(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string normalized = raw.Trim();
            if (normalized.StartsWith(FormatPrefix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(FormatPrefix.Length).Trim();
            return normalized;
        }

        private static bool LooksLikeJsqPayload(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string normalized = raw.TrimStart();
            if (normalized.StartsWith(FormatPrefix, StringComparison.OrdinalIgnoreCase)) return true;
            return normalized.StartsWith("{", StringComparison.Ordinal) &&
                   normalized.IndexOf("\"format\"", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   normalized.IndexOf("JSQCAD/1.0", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ReadClipboardText()
        {
            if (Clipboard.ContainsText(System.Windows.Forms.TextDataFormat.UnicodeText))
                return Clipboard.GetText(System.Windows.Forms.TextDataFormat.UnicodeText);
            if (Clipboard.ContainsText())
                return Clipboard.GetText();
            return string.Empty;
        }

        private static void EnsureLayers(CadClipboardPayload payload, Database db, Transaction tr)
        {
            if (payload.layers == null || payload.layers.Count == 0) return;
            foreach (CadLayerDefinition layer in payload.layers)
            {
                if (layer == null || string.IsNullOrWhiteSpace(layer.name)) continue;
                EnsureLayer(layer.name, layer.colorIndex, db, tr);
            }
        }

        private static ObjectId EnsureLayer(string layerName, short? colorIndex, Database db, Transaction tr)
        {
            LayerTable layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (layerTable.Has(layerName)) return layerTable[layerName];
            layerTable.UpgradeOpen();
            LayerTableRecord record = new LayerTableRecord { Name = layerName };
            if (colorIndex.HasValue)
                record.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex.Value);
            ObjectId layerId = layerTable.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
            return layerId;
        }

        private static void ApplyEntityProperties(Entity entity, CadEntityDefinition entityDef, Database db, Transaction tr)
        {
            if (!string.IsNullOrWhiteSpace(entityDef.layer))
            {
                EnsureLayer(entityDef.layer, entityDef.colorIndex, db, tr);
                entity.Layer = entityDef.layer;
            }
            if (entityDef.colorIndex.HasValue)
                entity.Color = Color.FromColorIndex(ColorMethod.ByAci, entityDef.colorIndex.Value);
        }

        private static Entity BuildEntity(CadEntityDefinition d, double unitScale, double offsetX, double offsetY)
        {
            if (d == null || string.IsNullOrWhiteSpace(d.type)) return null;
            string type = d.type.Trim().ToLowerInvariant();
            switch (type)
            {
                case "polyline": return BuildPolyline(d, unitScale, offsetX, offsetY);
                case "line": return BuildLine(d, unitScale, offsetX, offsetY);
                case "circle": return BuildCircle(d, unitScale, offsetX, offsetY);
                case "arc": return BuildArc(d, unitScale, offsetX, offsetY);
                case "text": return BuildText(d, unitScale, offsetX, offsetY);
                default: throw new InvalidOperationException("Unsupported entity type: " + d.type);
            }
        }

        private static Entity BuildPolyline(CadEntityDefinition d, double s, double ox, double oy)
        {
            if (d.vertices == null || d.vertices.Length < 2)
                throw new InvalidOperationException("Polyline must contain at least two vertices.");
            Polyline polyline = new Polyline();
            for (int i = 0; i < d.vertices.Length; i++)
            {
                double[] vertex = d.vertices[i];
                Point2d point = ToPoint2d(vertex, s, ox, oy);
                double bulge = (d.bulges != null && i < d.bulges.Length) ? d.bulges[i] : 0.0;
                polyline.AddVertexAt(i, point, bulge, 0.0, 0.0);
            }
            polyline.Closed = d.closed;
            return polyline;
        }

        private static Entity BuildLine(CadEntityDefinition d, double s, double ox, double oy)
        {
            return new Line(ToPoint3d(d.start, s, ox, oy), ToPoint3d(d.end, s, ox, oy));
        }

        private static Entity BuildCircle(CadEntityDefinition d, double s, double ox, double oy)
        {
            if (!d.radius.HasValue) throw new InvalidOperationException("Circle requires radius.");
            return new Circle(ToPoint3d(d.center, s, ox, oy), Vector3d.ZAxis, d.radius.Value * s);
        }

        private static Entity BuildArc(CadEntityDefinition d, double s, double ox, double oy)
        {
            if (!d.radius.HasValue || !d.startAngle.HasValue || !d.endAngle.HasValue)
                throw new InvalidOperationException("Arc requires center, radius, startAngle and endAngle.");
            return new Arc(
                ToPoint3d(d.center, s, ox, oy), d.radius.Value * s,
                DegreesToRadians(d.startAngle.Value), DegreesToRadians(d.endAngle.Value));
        }

        private static Entity BuildText(CadEntityDefinition d, double s, double ox, double oy)
        {
            if (string.IsNullOrWhiteSpace(d.value))
                throw new InvalidOperationException("Text requires value.");
            DBText text = new DBText
            {
                Position = ToPoint3d(d.position, s, ox, oy),
                Height = (d.height ?? 5.0) * s,
                TextString = d.value,
                Rotation = DegreesToRadians(d.rotation ?? 0.0)
            };
            return text;
        }

        private static Point2d ToPoint2d(double[] c, double s, double ox, double oy)
        {
            if (c == null || c.Length < 2) return new Point2d(ox, oy);
            return new Point2d(c[0] * s + ox, c[1] * s + oy);
        }

        private static Point3d ToPoint3d(double[] c, double s, double ox, double oy)
        {
            Point2d p = ToPoint2d(c, s, ox, oy);
            double z = (c != null && c.Length > 2) ? c[2] * s : 0.0;
            return new Point3d(p.X, p.Y, z);
        }

        private static double DegreesToRadians(double degrees) { return degrees * Math.PI / 180.0; }

        private static double ResolveUnitScale(string units)
        {
            string n = string.IsNullOrWhiteSpace(units) ? "mm" : units.Trim().ToLowerInvariant();
            switch (n)
            {
                case "mm": case "millimeter": case "millimeters": return 1.0;
                case "cm": case "centimeter": case "centimeters": return 10.0;
                case "m": case "meter": case "meters": return 1000.0;
                case "in": case "inch": case "inches": return 25.4;
                default: throw new InvalidOperationException("Unsupported units: " + units);
            }
        }
    }
}
