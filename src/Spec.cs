// File: Spec.cs
// What to render.
//
// A spec is a JSON file describing one experiment: which circuit, which input, how the
// audio is driven, and how many renders to draw from each region of each knob or switch.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LiveSpiceGen
{
    public enum JointMode
    {
        /// <summary>Every control's band counts are met exactly; which bands combine is random.</summary>
        Independent,

        /// <summary>Every combination of bands and switch states is visited evenly.</summary>
        Grid,

        /// <summary>One control at a time sweeps its travel/positions; the others sit at a fixed point.</summary>
        Sweep,

        /// <summary>As Sweep, but the other controls are drawn from their own distributions each time.</summary>
        SweepRandom,
    }

    /// <summary>One region of a knob's travel or a discrete switch state, and its render allocation.</summary>
    public sealed class Band
    {
        public double From = 0.0;
        public double To = 1.0;

        /// <summary>For discrete switches: exact switch position index (0, 1, ...).</summary>
        public int? Position = null;

        public long Count = 1;

        /// <summary>Optional percentage/fraction of total budget (e.g. 0.70 for 70%).</summary>
        public double? Weight = null;

        public string Note = "";

        public override string ToString()
        {
            if (Position != null)
            {
                return string.Format(CultureInfo.InvariantCulture, "pos={0}x{1}{2}",
                    Position.Value, Count, string.IsNullOrEmpty(Note) ? "" : "  " + Note);
            }

            return string.Format(CultureInfo.InvariantCulture, "[{0:0.####},{1:0.####})x{2}{3}",
                From, To, Count, string.IsNullOrEmpty(Note) ? "" : "  " + Note);
        }
    }

    /// <summary>How one knob or switch should be sampled.</summary>
    public sealed class KnobSpec
    {
        public List<Band> Bands = new List<Band>();

        /// <summary>
        /// When set, every render uses this fixed value for this control and its bands are ignored.
        /// </summary>
        public double? Pin;

        public long TotalCount { get { return Bands.Sum(b => b.Count); } }
    }

    public sealed class Spec
    {
        public int Version = 1;
        public string Name = "";

        public string Circuit = "";
        public string Input = "";

        public RenderSettings Render = new RenderSettings();

        /// <summary>Drive level in dB, drawn per render. Log-uniform, because loudness is log.</summary>
        public double GainDbLow = -18.0;
        public double GainDbHigh = 6.0;

        /// <summary>Where in the input an excerpt may start: "signal" or "all".</summary>
        public string Spans = "signal";

        /// <summary>Optional time window [start_s, end_s] restricting where excerpts can be drawn.</summary>
        public double[]? TimeRange = null;

        /// <summary>Level in dB below input peak at which a stretch counts as silence.</summary>
        public double SilenceBelowDb = -80.0;

        /// <summary>How excerpts are handed out: "sequential" or "random".</summary>
        public string Placement = "sequential";

        public JointMode Joint = JointMode.Independent;

        /// <summary>Length of each render, in seconds.</summary>
        public double ExcerptSeconds = 15.0;

        /// <summary>Stride in seconds between excerpt start points (0 = non-overlapping tiles).</summary>
        public double Stride = 0.0;

        /// <summary>Positions in a continuous Sweep, per knob.</summary>
        public int SweepSteps = 24;

        /// <summary>Where non-swept knobs sit in Sweep mode.</summary>
        public double Pin = 0.5;

        /// <summary>Total renders. If 0, resolved from band counts.</summary>
        public long Budget = 0;

        /// <summary>When true, ensures extreme boundary corners (min, noon, max, alternating) are included.</summary>
        public bool Anchors = false;

        public int Seed;

        /// <summary>Automated dataset train/val/test split ratio, e.g. "80/10/10" or "none".</summary>
        public string Split = "none";

        /// <summary>Specific dialed knob settings to render, bypassing random generation.</summary>
        public List<Dictionary<string, double>>? ExplicitSettings = null;

        /// <summary>Number of worker threads to use for rendering (0 = auto-detect CPU cores).</summary>
        public int Threads = 0;

        /// <summary>Stop if the circuit has a control the spec does not mention.</summary>
        public bool RequireAllKnobs = false;

        public readonly Dictionary<string, KnobSpec> Knobs =
            new Dictionary<string, KnobSpec>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Where this spec was read from, for error messages and the manifest.</summary>
        public string SourcePath = "";

        /// <summary>SHA-256 of the spec file, recorded in the manifest header.</summary>
        public string Hash = "";

        public const string BudgetNote =
            "`budget` is the number of renders. A band's `count` is a share of the knob's " +
            "renders, so bands of one knob should sum to the same total as bands of another; " +
            "they are scaled to `budget` exactly, and that is how a count stays an integer.";

        public static Spec Load(string path)
        {
            string text = File.ReadAllText(path);
            var spec = Parse(text, Path.GetFileName(path));
            spec.SourcePath = Path.GetFullPath(path);
            spec.Hash = Renderer.Sha256(path);
            return spec;
        }

        public static Spec Parse(string text, string origin)
        {
            using (var doc = JsonDocument.Parse(text))
            {
                var spec = new Spec();
                JsonElement e = doc.RootElement;

                if (e.ValueKind != JsonValueKind.Object)
                    throw new SpecError($"{origin}: the spec must be a JSON object");

                spec.Version = OptInt(e, "version", 1, origin);
                if (spec.Version != 1)
                    throw new SpecError($"{origin}: spec version {spec.Version} is not supported by this build");

                spec.Name = OptString(e, "name", "");
                spec.Circuit = OptString(e, "circuit", "");
                spec.Input = OptString(e, "input", "");
                spec.Budget = OptInt(e, "budget", 0, origin);
                spec.Seed = OptInt(e, "seed", 0, origin);
                spec.ExcerptSeconds = OptDouble(e, "duration", spec.ExcerptSeconds, origin);

                if (spec.ExcerptSeconds <= 0.0)
                    throw new SpecError($"{origin}: \"duration\" must be a positive number of seconds, " +
                                        $"got {Json.Number(spec.ExcerptSeconds)}");

                if (e.TryGetProperty("render", out JsonElement r)) spec.Render = ParseRender(r, origin);
                if (e.TryGetProperty("sampling", out JsonElement s)) ParseSampling(spec, s, origin);

                if (e.TryGetProperty("split", out JsonElement sp))
                {
                    if (sp.ValueKind == JsonValueKind.String) spec.Split = sp.GetString() ?? "none";
                }

                if (e.TryGetProperty("settings", out JsonElement settingsArr) && settingsArr.ValueKind == JsonValueKind.Array)
                {
                    spec.ExplicitSettings = new List<Dictionary<string, double>>();
                    foreach (var item in settingsArr.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                            foreach (var prop in item.EnumerateObject())
                            {
                                if (prop.Value.ValueKind == JsonValueKind.Number)
                                    dict[prop.Name] = prop.Value.GetDouble();
                            }
                            if (dict.Count > 0) spec.ExplicitSettings.Add(dict);
                        }
                    }
                }

                if (e.TryGetProperty("knobs", out JsonElement k))
                    ParseKnobs(spec, k, origin);
                else if (e.TryGetProperty("controls", out JsonElement c))
                    ParseKnobs(spec, c, origin);

                return spec;
            }
        }

        private static RenderSettings ParseRender(JsonElement e, string origin)
        {
            var rs = new RenderSettings();

            rs.SampleRate = OptInt(e, "sampleRate", rs.SampleRate, origin);
            rs.Oversample = OptInt(e, "oversample", rs.Oversample, origin);
            rs.Iterations = OptInt(e, "iterations", rs.Iterations, origin);
            rs.Normalize = OptDouble(e, "normalize", rs.Normalize, origin);
            rs.WarmupSeconds = OptDouble(e, "warmup", rs.WarmupSeconds, origin);

            string spk = OptString(e, "speaker", "", origin);
            rs.Speaker = string.IsNullOrWhiteSpace(spk) ? null : spk;

            return rs;
        }

        private static void ParseSampling(Spec spec, JsonElement e, string origin)
        {
            spec.Joint = OptEnum(e, "joint", JointMode.Independent, origin);
            spec.SweepSteps = OptInt(e, "sweepSteps", spec.SweepSteps, origin);
            spec.Pin = OptDouble(e, "pin", spec.Pin, origin);
            spec.RequireAllKnobs = OptBool(e, "requireAllKnobs", spec.RequireAllKnobs, origin);
            spec.Threads = OptInt(e, "threads", spec.Threads, origin);
            spec.Anchors = OptBool(e, "anchors", spec.Anchors, origin);
            spec.Split = OptString(e, "split", spec.Split, origin);

            if (e.TryGetProperty("budget", out JsonElement b))
            {
                if (b.ValueKind == JsonValueKind.Number && b.TryGetInt64(out long budget))
                    spec.Budget = budget;
            }
            if (e.TryGetProperty("seed", out JsonElement sd) && sd.ValueKind == JsonValueKind.Number)
                spec.Seed = sd.GetInt32();

            if (e.TryGetProperty("timeRange", out JsonElement tr))
                spec.TimeRange = Range(tr, "sampling.timeRange", 0.0, 360000.0);

            if (e.TryGetProperty("input", out JsonElement i)) ParseInput(spec, i, origin);
        }

        private static void ParseInput(Spec spec, JsonElement e, string origin)
        {
            spec.Spans = OptString(e, "spans", spec.Spans, origin);
            spec.Placement = OptString(e, "placement", spec.Placement, origin);
            spec.SilenceBelowDb = OptDouble(e, "silenceBelowDb", spec.SilenceBelowDb, origin);
            spec.Stride = OptDouble(e, "stride", spec.Stride, origin);

            if (e.TryGetProperty("gainDb", out JsonElement g))
            {
                double[] range = Range(g, "input.gainDb", -60.0, 60.0);
                spec.GainDbLow = range[0];
                spec.GainDbHigh = range[1];
            }

            if (spec.Spans != "signal" && spec.Spans != "all")
                throw new SpecError($"{origin}: input.spans must be \"signal\" or \"all\", not \"{spec.Spans}\"");

            if (spec.Placement != "sequential" && spec.Placement != "random")
                throw new SpecError($"{origin}: input.placement must be \"sequential\" or \"random\", not \"{spec.Placement}\"");

            if (spec.SilenceBelowDb < -200.0 || spec.SilenceBelowDb > 0.0)
                throw new SpecError($"{origin}: input.silenceBelowDb must be a negative dB level, " +
                                    $"got {Json.Number(spec.SilenceBelowDb)}");
        }

        private static void ParseKnobs(Spec spec, JsonElement e, string origin)
        {
            foreach (JsonProperty prop in e.EnumerateObject())
            {
                string knob = prop.Name;
                var ks = new KnobSpec();

                ParseKnobValue(ks, prop.Value, $"{origin}: knobs.{knob}");

                if (ks.Bands.Count == 0 && ks.Pin == null)
                    throw new SpecError($"{origin}: knobs.{knob} has no bands or pin value");

                spec.Knobs[knob] = ks;
            }
        }

        private static void ParseKnobValue(KnobSpec ks, JsonElement e, string at)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Number:
                    if (e.TryGetInt64(out long countVal) && countVal > 1)
                    {
                        // E.g. "Volume": 400 -> uniform count
                        ks.Bands.Add(new Band { From = 0.0, To = 1.0, Count = countVal });
                    }
                    else
                    {
                        // E.g. "Pull Bright": 0 or "Volume": 0.5 -> pinned value
                        ks.Pin = e.GetDouble();
                    }
                    return;

                case JsonValueKind.Array:
                    // Check if array is discrete positions [0, 1, 2]
                    bool allInts = true;
                    foreach (var item in e.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Number || item.GetInt64() != item.GetDouble())
                        {
                            allInts = false;
                            break;
                        }
                    }

                    if (allInts && e.GetArrayLength() > 2)
                    {
                        foreach (var item in e.EnumerateArray())
                        {
                            int p = item.GetInt32();
                            ks.Bands.Add(new Band { Position = p, From = p, To = p + 1, Count = 1, Note = $"pos {p}" });
                        }
                        return;
                    }

                    ks.Bands.Add(BandFrom(e, 1L, at));
                    return;

                case JsonValueKind.Object:
                    if (e.TryGetProperty("pin", out JsonElement pin))
                    {
                        if (pin.ValueKind != JsonValueKind.Number)
                            throw new SpecError($"{at}.pin must be a number");
                        ks.Pin = pin.GetDouble();
                        return;
                    }

                    if (e.TryGetProperty("positions", out JsonElement posArr) && posArr.ValueKind == JsonValueKind.Array)
                    {
                        long defaultCount = OptLong(e, "count", 1L, at);
                        foreach (JsonElement p in posArr.EnumerateArray())
                        {
                            int pos = p.GetInt32();
                            ks.Bands.Add(new Band { Position = pos, From = pos, To = pos + 1, Count = defaultCount, Note = $"pos {pos}" });
                        }
                        return;
                    }

                    if (e.TryGetProperty("position", out JsonElement singlePos) && singlePos.ValueKind == JsonValueKind.Number)
                    {
                        int pos = singlePos.GetInt32();
                        ks.Bands.Add(new Band { Position = pos, From = pos, To = pos + 1, Count = OptLong(e, "count", 1L, at), Note = $"pos {pos}" });
                        return;
                    }

                    if (e.TryGetProperty("bands", out JsonElement bands))
                    {
                        int i = 0;
                        foreach (JsonElement b in bands.EnumerateArray())
                        {
                            if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("position", out JsonElement bPos))
                            {
                                int pos = bPos.GetInt32();
                                ks.Bands.Add(new Band
                                {
                                    Position = pos,
                                    From = pos,
                                    To = pos + 1,
                                    Count = OptLong(b, "count", 1L, at),
                                    Note = OptString(b, "note", $"pos {pos}")
                                });
                            }
                            else
                            {
                                ks.Bands.Add(BandFrom(b, 1L, $"{at}.bands[{i++}]"));
                            }
                        }
                        return;
                    }

                    if (e.TryGetProperty("band", out JsonElement band))
                    {
                        ks.Bands.Add(BandFrom(band, OptLong(e, "count", 1L, at), at));
                        return;
                    }

                    // An object carrying from/to is one band.
                    if (e.TryGetProperty("from", out _) || e.TryGetProperty("to", out _))
                    {
                        ks.Bands.Add(BandFrom(e, OptLong(e, "count", 1L, at), at));
                        return;
                    }

                    throw new SpecError($"{at} has none of \"bands\", \"band\", \"pin\", \"positions\", \"from\" or \"to\"");
            }

            throw new SpecError($"{at} must be a number, an array, or an object");
        }

        private static Band BandFrom(JsonElement e, long defaultCount, string at)
        {
            var band = new Band { Count = defaultCount };

            if (e.ValueKind == JsonValueKind.Array)
            {
                double[] range = Range(e, at, 0.0, 1.0);
                band.From = range[0];
                band.To = range[1];
                band.Count = e.GetArrayLength() > 2 && e[2].ValueKind == JsonValueKind.Number
                    ? e[2].GetInt64()
                    : defaultCount;
                Validate(band, at);
                return band;
            }

            if (e.ValueKind != JsonValueKind.Object)
                throw new SpecError($"{at} must be [from, to, count?] or an object with from/to");

            if (e.TryGetProperty("position", out JsonElement posElem))
            {
                band.Position = posElem.GetInt32();
                band.From = band.Position.Value;
                band.To = band.Position.Value + 1;
                band.Count = OptLong(e, "count", defaultCount, at);
                band.Note = OptString(e, "note", $"pos {band.Position.Value}");
                Validate(band, at);
                return band;
            }

            band.From = OptDouble(e, "from", 0.0, at);
            band.To = OptDouble(e, "to", 1.0, at);
            band.Count = OptLong(e, "count", defaultCount, at);
            if (e.TryGetProperty("weight", out JsonElement wElem) && wElem.ValueKind == JsonValueKind.Number)
                band.Weight = wElem.GetDouble();
            band.Note = OptString(e, "note", "");
            Validate(band, at);
            return band;
        }

        private static void Validate(Band b, string at)
        {
            if (b.Position != null)
            {
                if (b.Position.Value < 0)
                    throw new SpecError($"{at}: switch position must be a non-negative integer, got {b.Position.Value}");
                if (b.Count < 0)
                    throw new SpecError($"{at}: count must not be negative, got {b.Count}");
                return;
            }

            if (double.IsNaN(b.From) || double.IsNaN(b.To))
                throw new SpecError($"{at}: from and to must be numbers");

            if (b.From < 0.0 || b.From > 1.0 || b.To < 0.0 || b.To > 1.0)
                throw new SpecError($"{at}: from and to are positions in 0..1, " +
                                    $"got from={Json.Number(b.From)} to={Json.Number(b.To)}");

            if (b.To <= b.From)
                throw new SpecError($"{at}: to ({Json.Number(b.To)}) must be greater than from ({Json.Number(b.From)}). " +
                                    "A band is a half-open interval [from, to), so a band from 0.8 to 0.8 covers nothing.");

            if (b.Count < 0)
                throw new SpecError($"{at}: count must not be negative, got {b.Count}");
        }

        // ---- reading helpers ----------------------------------------------------

        private static bool TryPath(JsonElement e, string name, out JsonElement v)
        {
            return e.TryGetProperty(name, out v);
        }

        private static string OptString(JsonElement e, string name, string fallback, string origin = "")
        {
            return TryPath(e, name, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? fallback : fallback;
        }

        private static bool OptBool(JsonElement e, string name, bool fallback, string origin = "")
        {
            if (!TryPath(e, name, out JsonElement v)) return fallback;

            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new SpecError($"{(origin.Length > 0 ? origin + ": " : "")}\"{name}\" must be true or false, not {v.ValueKind}"),
            };
        }

        private static int OptInt(JsonElement e, string name, int fallback, string origin)
        {
            if (!TryPath(e, name, out JsonElement v)) return fallback;

            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out i)) return i;

            throw new SpecError($"{origin}: \"{name}\" must be a whole number, got {v.GetRawText()}");
        }

        private static long OptLong(JsonElement e, string name, long fallback, string origin)
        {
            if (!TryPath(e, name, out JsonElement v)) return fallback;

            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l)) return l;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out l)) return l;

            throw new SpecError($"{origin}: \"{name}\" must be a whole number, got {v.GetRawText()}");
        }

        private static double OptDouble(JsonElement e, string name, double fallback, string origin)
        {
            if (!TryPath(e, name, out JsonElement v)) return fallback;

            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d;

            throw new SpecError($"{origin}: \"{name}\" must be a number, got {v.GetRawText()}");
        }

        private static T OptEnum<T>(JsonElement e, string name, T fallback, string origin) where T : struct, Enum
        {
            string s = OptString(e, name, null!);

            if (s == null) return fallback;
            if (!Enum.TryParse<T>(s, ignoreCase: true, out T parsed) || !Enum.IsDefined(typeof(T), parsed))
                throw new SpecError($"{origin}: \"{name}\" must be one of {string.Join(", ", Enum.GetNames(typeof(T)))}" +
                                    $", got \"{s}\"");

            return parsed;
        }

        private static double[] Range(JsonElement e, string at, double lo, double hi)
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 2)
                throw new SpecError($"{at} must be [{Json.Number(lo)}, {Json.Number(hi)}], got {e.GetRawText()}");

            if (e[0].ValueKind != JsonValueKind.Number || e[1].ValueKind != JsonValueKind.Number)
                throw new SpecError($"{at} must be two numbers, got {e.GetRawText()}");

            double a = e[0].GetDouble();
            double b = e[1].GetDouble();

            if (double.IsNaN(a) || double.IsNaN(b) || double.IsInfinity(a) || double.IsInfinity(b))
                throw new SpecError($"{at} must be finite numbers, got {e.GetRawText()}");

            if (a > b)
                throw new SpecError($"{at} is [{Json.Number(a)}, {Json.Number(b)}], which runs backwards");

            if (a < lo || a > hi || b < lo || b > hi)
                throw new SpecError($"{at} is out of range: values must be {Json.Number(lo)}..{Json.Number(hi)}, " +
                                    $"got [{Json.Number(a)}, {Json.Number(b)}]");

            return new[] { a, b };
        }
    }

    public sealed class SpecError : Exception
    {
        public SpecError(string message) : base(message) { }
    }
}
