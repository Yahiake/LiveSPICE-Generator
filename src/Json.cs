// File: Json.cs
// A tiny JSON writer that preserves field order.
//
// System.Text.Json can read a manifest, but it cannot be asked to emit object members in
// a chosen order without either an ordered dictionary plus a converter or a custom writer,
// and the manifest format is an interface: LiveSPICE's own renderer wrote it, the trainer
// reads it, and every dataset already on disk has it. Emitting keys in a fixed order is
// what makes two manifests diffable by eye, so it is worth thirty lines here.
// Reading uses System.Text.Json, in Spec.cs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LiveSpiceGen
{
    public static class Json
    {
        /// <summary>
        /// An object written as-is, so nested structures are not escaped into strings.
        /// </summary>
        public sealed class Raw
        {
            public readonly string Text;
            public Raw(string text) { Text = text; }
            public override string ToString() { return Text; }
        }

        public static KeyValuePair<string, object> F(string key, object value)
        {
            return new KeyValuePair<string, object>(key, value);
        }

        public static string Write(params KeyValuePair<string, object>[] fields)
        {
            var sb = new StringBuilder("{");
            for (int i = 0; i < fields.Length; ++i)
            {
                if (i > 0) sb.Append(',');
                sb.Append(String(fields[i].Key)).Append(':').Append(Value(fields[i].Value));
            }
            return sb.Append('}').ToString();
        }

        /// <summary>A string array, for example the circuit's knob names.</summary>
        public static string Array(IEnumerable<string> items)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (string s in items)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(String(s));
            }
            return sb.Append(']').ToString();
        }

        /// <summary>
        /// An object whose keys are knob names and whose values are positions.
        ///
        /// Keys come from the circuit, so they are escaped like any other string.
        /// </summary>
        public static string NumberMap(IReadOnlyList<string> order, IReadOnlyDictionary<string, double> values)
        {
            var sb = new StringBuilder("{");
            for (int i = 0; i < order.Count; ++i)
            {
                if (i > 0) sb.Append(',');
                sb.Append(String(order[i])).Append(':').Append(Number(values[order[i]]));
            }
            return sb.Append('}').ToString();
        }

        public static string Number(double v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        public static string Value(object v)
        {
            if (v == null) return "null";
            if (v is Raw) return ((Raw)v).Text;
            if (v is string) return String((string)v);
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is double) return Number((double)v);
            if (v is float) return Number((float)v);
            if (v is int) return ((int)v).ToString(CultureInfo.InvariantCulture);
            if (v is long) return ((long)v).ToString(CultureInfo.InvariantCulture);
            return String(v.ToString() ?? "");
        }

        public static string String(string? s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // Control characters must be escaped in JSON. Everything else,
                        // including non-ASCII, is emitted as UTF-8 by the writer.
                        sb.Append(c < ' ' ? string.Format("\\u{0:x4}", (int)c) : c.ToString());
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
