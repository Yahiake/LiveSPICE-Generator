// File: Manifest.cs
// The record of what was rendered.
//
// manifest.jsonl is the source of truth for a dataset. The WAVs are payloads; every row
// carries the circuit hash, the input hash, the knob values as exact doubles, the excerpt
// bounds and the measurements, so any single render can be reproduced or thrown out later
// without guessing. Line 1 is the dataset header.
//
// The field set here matches what LiveSPICE's renderer wrote, so datasets made by either
// program read the same way. New fields are only ever appended.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LiveSpiceGen
{
    /// <summary>One render of one excerpt at one knob setting.</summary>
    public sealed class RenderJob
    {
        /// <summary>Filename stem, without the extension.</summary>
        public string Id = "";

        public Dictionary<string, double> Knobs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Where in the shared input this excerpt starts, in seconds.</summary>
        public double Offset;

        public double Seconds;

        /// <summary>
        /// Linear gain applied to the input excerpt for this render only.
        ///
        /// A plugin is fed whatever level the player dials in, so a model trained at one
        /// fixed input level is wrong everywhere else. Recorded rather than baked into the
        /// file, so the trainer can apply it to the shared input and the audio does not
        /// have to exist twice on disk.
        /// </summary>
        public double InputGain = 1.0;

        /// <summary>
        /// Free text naming where in the sampling spec this job came from, e.g.
        /// "Volume[0.00,0.08)". Provenance: it is what makes a dataset auditable months
        /// later, when the question is "why are there 400 renders in this band and none
        /// in that one".
        /// </summary>
        public string Origin = "";

        /// <summary>Dataset split: "train", "val", "test", or "all".</summary>
        public string Split = "all";

        /// <summary>
        /// Filename stem plus extension. The span of the input is in the name for human
        /// convenience only; nothing downstream parses these names, the manifest is the
        /// record.
        /// </summary>
        public string FileName { get { return Id + ".wav"; } }
    }

    public static class Manifest
    {
        public const string HeaderType = "dataset";

        /// <summary>
        /// The header line.
        ///
        /// `duration_s` and the generator version were not in the original header. They are
        /// appended rather than inserted so that a line-1 diff against an older dataset
        /// shows the addition and nothing else.
        /// </summary>
        public static string Header(Renderer renderer, RenderSettings settings, double duration, string specHash)
        {
            return Json.Write(
                Json.F("type", HeaderType),
                Json.F("circuit", Path.GetFileName(renderer.CircuitFile)),
                Json.F("circuit_sha256", renderer.CircuitHash),
                Json.F("input", Path.GetFileName(renderer.InputFile)),
                Json.F("input_sha256", renderer.InputHash),
                Json.F("input_v0dBFS", renderer.InputV0dBFS),
                Json.F("sample_rate", (long)settings.SampleRate),
                Json.F("oversample", (long)settings.Oversample),
                Json.F("iterations", (long)settings.Iterations),
                Json.F("duration_s", duration),
                Json.F("generator", "livespice-gen"),
                Json.F("spec_sha256", specHash),
                new KeyValuePair<string, object>("knobs", new Json.Raw(renderer.KnobNamesJson())));
        }

        public static string Row(Renderer renderer, RenderJob job, bool ok, Wave.Measure m, string? error)
        {
            var fields = new List<KeyValuePair<string, object>>
            {
                Json.F("id", job.Id),
                Json.F("split", job.Split),
                Json.F("status", ok ? "ok" : "failed"),
                Json.F("file", job.FileName),
                new KeyValuePair<string, object>("knobs", new Json.Raw(renderer.KnobsJson(job.Knobs))),
                Json.F("offset_s", job.Offset),
                Json.F("duration_s", job.Seconds),

                // The trainer has to reproduce this render's exact input, and this gain
                // exists in no file on disk, so it has to travel with the row.
                Json.F("input_gain", job.InputGain),
            };

            if (!string.IsNullOrEmpty(job.Origin))
                fields.Add(Json.F("origin", job.Origin));

            if (!ok)
            {
                fields.Add(Json.F("error", error ?? "unknown"));
            }
            else
            {
                fields.Add(Json.F("mean", m.Mean));
                fields.Add(Json.F("peak", m.Peak));
                fields.Add(Json.F("rms", m.Rms));
                fields.Add(Json.F("clipped", (long)m.Clipped));
                fields.Add(Json.F("nonfinite", (long)m.NonFinite));
                fields.Add(Json.F("flat", m.Flat));
            }

            return Json.Write(fields.ToArray());
        }

        public static string CsvHeader(Renderer renderer)
        {
            var cols = new List<string> { "id", "split", "status", "file", "offset_s", "duration_s", "input_gain", "origin", "mean", "peak", "rms", "clipped", "nonfinite", "flat" };
            cols.AddRange(renderer.KnobOrder);
            return string.Join(",", cols);
        }

        public static string CsvRow(Renderer renderer, RenderJob job, bool ok, Wave.Measure m, string? error)
        {
            var cols = new List<string>
            {
                job.Id,
                job.Split,
                ok ? "ok" : "failed",
                job.FileName,
                job.Offset.ToString("F3", CultureInfo.InvariantCulture),
                job.Seconds.ToString("F3", CultureInfo.InvariantCulture),
                job.InputGain.ToString("F6", CultureInfo.InvariantCulture),
                job.Origin.Replace(',', ' '),
                (ok ? m.Mean : 0.0).ToString("E6", CultureInfo.InvariantCulture),
                (ok ? m.Peak : 0.0).ToString("F6", CultureInfo.InvariantCulture),
                (ok ? m.Rms : 0.0).ToString("F6", CultureInfo.InvariantCulture),
                (ok ? m.Clipped : 0).ToString(CultureInfo.InvariantCulture),
                (ok ? m.NonFinite : 0).ToString(CultureInfo.InvariantCulture),
                (ok ? m.Flat.ToString().ToLowerInvariant() : "false"),
            };
            foreach (string k in renderer.KnobOrder)
            {
                double val = job.Knobs.TryGetValue(k, out double v) ? v : 0.0;
                cols.Add(val.ToString("F4", CultureInfo.InvariantCulture));
            }
            return string.Join(",", cols);
        }

        /// <summary>
        /// Everything read back out of an existing manifest.
        ///
        /// Resume needs the set of ids already done, and refusing to resume needs the
        /// header. Both come from one pass.
        /// </summary>
        public sealed class Contents
        {
            public readonly HashSet<string> Done = new HashSet<string>(StringComparer.Ordinal);

            public int Rows;
            public int Ok;
            public int Failed;
            public JsonElement Header;
            public bool HasHeader;

            /// <summary>
            /// Ids in the manifest that no longer have a WAV beside it. A manifest that
            /// claims a render which is not on disk would otherwise be trusted by the
            /// trainer and turn into a confusing missing-file error much later.
            /// </summary>
            public List<string> MissingFiles(string renderDir)
            {
                var missing = new List<string>();
                string[] lines = File.Exists(renderDir) ? Directory.GetFiles(renderDir, "*.wav") : Array.Empty<string>();

                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string f in lines) present.Add(Path.GetFileName(f));

                foreach (string line in File.ReadLines(Path.Combine(renderDir, "..", "manifest.jsonl")))
                {
                    if (TryField(line, "file", out string? file) && file != null && !present.Contains(file))
                        missing.Add(file);
                }

                return missing;
            }
        }

        /// <summary>
        /// Read a manifest, and check it against what this run is about to do.
        ///
        /// Resuming is the single most dangerous thing this program does: it appends to a
        /// file that already describes hundreds of renders, and the failure mode of getting
        /// it wrong is a dataset that is silently a mixture of two experiments. So the
        /// circuit, the input and the simulation settings all have to match, or nothing is
        /// appended.
        /// </summary>
        public static Contents Read(string path, Renderer renderer, RenderSettings settings, TextWriter stderr)
        {
            var c = new Contents();

            using (var doc = JsonDocument.Parse("[" + string.Join(",", TrimBlank(File.ReadAllLines(path))) + "]"))
            {
                foreach (JsonElement e in doc.RootElement.EnumerateArray())
                {
                    if (!c.HasHeader && TryString(e, "type", out string? type) && type == HeaderType)
                    {
                        c.Header = e.Clone();
                        c.HasHeader = true;
                        continue;
                    }

                    ++c.Rows;

                    if (TryString(e, "status", out string? status))
                    {
                        if (status == "ok") ++c.Ok;
                        else ++c.Failed;
                    }

                    if (TryString(e, "id", out string? id) && id != null) c.Done.Add(id);
                }
            }

            if (!c.HasHeader)
            {
                stderr.WriteLine("warning: manifest has no dataset header; resuming anyway");
                return c;
            }

            void check(string field, string actual)
            {
                if (TryString(c.Header, field, out string? recorded) && recorded != actual)
                    throw new InvalidDataException(
                        $"this dataset was made with {field} {recorded}, but this run has {actual}.\n" +
                        "Resuming would append renders that do not belong to the same dataset.\n" +
                        "Render into a new directory, or delete the old manifest to start over.");
            }

            check("circuit_sha256", renderer.CircuitHash);
            check("input_sha256", renderer.InputHash);
            check("sample_rate", settings.SampleRate.ToString(CultureInfo.InvariantCulture));
            check("oversample", settings.Oversample.ToString(CultureInfo.InvariantCulture));
            check("iterations", settings.Iterations.ToString(CultureInfo.InvariantCulture));

            string[] oldKnobs = StringArray(c.Header, "knobs");
            string[] nowKnobs = renderer.Knobs.Names.ToArray();
            if (!Same(oldKnobs, nowKnobs))
                throw new InvalidDataException(
                    $"this dataset was made with knobs [{string.Join(", ", oldKnobs)}], " +
                    $"but this circuit has [{string.Join(", ", nowKnobs)}].");

            return c;

            static bool Same(string[] a, string[] b)
            {
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; ++i)
                    if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
                return true;
            }
        }

        private static IEnumerable<string> TrimBlank(IEnumerable<string> lines)
        {
            foreach (string l in lines)
                if (l.Trim().Length > 0) yield return l;
        }

        private static bool TryField(string jsonLine, string field, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
        {
            try
            {
                using (var doc = JsonDocument.Parse(jsonLine))
                    return TryString(doc.RootElement, field, out value);
            }
            catch (JsonException)
            {
                value = null;
                return false;
            }
        }

        private static bool TryString(JsonElement e, string field, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
        {
            if (e.TryGetProperty(field, out JsonElement v) && v.ValueKind == JsonValueKind.String)
            {
                value = v.GetString();
                return value != null;
            }

            // Numbers are emitted unquoted where a string would have been natural, so a
            // header written by either program still compares equal.
            if (e.TryGetProperty(field, out v) && v.ValueKind == JsonValueKind.Number)
            {
                value = v.GetRawText();
                return true;
            }

            value = null;
            return false;
        }

        private static string[] StringArray(JsonElement e, string field)
        {
            if (!e.TryGetProperty(field, out JsonElement v) || v.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var list = new List<string>();
            foreach (JsonElement item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is string s) list.Add(s);

            return list.ToArray();
        }
    }
}
