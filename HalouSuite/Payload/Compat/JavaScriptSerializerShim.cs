#if HALOU_NET8
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;

namespace System.Web.Script.Serialization
{
    /// <summary>
    /// Minimal JavaScriptSerializer compatibility shim for the AutoCAD 2025/.NET 8 build.
    /// Keeps the existing payload parsing code shared with the .NET Framework build.
    /// </summary>
    internal sealed class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; }

        public T Deserialize<T>(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return default(T);
            JsonSerializerOptions options = CreateOptions();
            if (typeof(T) == typeof(Dictionary<string, object>))
            {
                using (JsonDocument doc = JsonDocument.Parse(input))
                {
                    object converted = ConvertElement(doc.RootElement);
                    return (T)converted;
                }
            }
            return JsonSerializer.Deserialize<T>(input, options);
        }

        public string Serialize(object input)
        {
            return JsonSerializer.Serialize(input, CreateOptions());
        }

        private static JsonSerializerOptions CreateOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
#if NET8_0_OR_GREATER
                IncludeFields = true,
#endif
                WriteIndented = false
            };
        }

        private static object ConvertElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    Dictionary<string, object> obj = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (JsonProperty prop in element.EnumerateObject())
                    {
                        obj[prop.Name] = ConvertElement(prop.Value);
                    }
                    return obj;
                case JsonValueKind.Array:
                    ArrayList arr = new ArrayList();
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        arr.Add(ConvertElement(item));
                    }
                    return arr;
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    int i;
                    if (element.TryGetInt32(out i)) return i;
                    long l;
                    if (element.TryGetInt64(out l)) return l;
                    double d;
                    if (element.TryGetDouble(out d)) return d;
                    return element.GetRawText();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                default:
                    return null;
            }
        }
    }
}
#endif
