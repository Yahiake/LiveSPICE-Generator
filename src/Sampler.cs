// File: Sampler.cs
// Decides the knob and switch positions, and where in the input each render comes from.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace LiveSpiceGen
{
    /// <summary>What the sampler decided, for reporting and for the plan file.</summary>
    public sealed class SamplingPlan
    {
        public List<RenderJob> Jobs = new List<RenderJob>();

        /// <summary>Realised count per "knob[i]" where i is the 0-based band index.</summary>
        public readonly Dictionary<string, long> BandCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public long Budget;
        public JointMode Joint;
        public string[] KnobOrder = Array.Empty<string>();
        public List<string> Notes = new List<string>();

        /// <summary>Controls the circuit has that the spec never mentioned.</summary>
        public List<string> UnspecifiedKnobs = new List<string>();
    }

    public static class Sampler
    {
        public static SamplingPlan Build(Spec spec, KnobSet knobs, Wave input, TextWriter log, out List<double[]> spans)
        {
            var plan = new SamplingPlan { Joint = spec.Joint, KnobOrder = knobs.Names.ToArray() };

            foreach (string k in knobs.Names)
            {
                ControlInfo ctrl = knobs[k];

                if (!spec.Knobs.TryGetValue(k, out KnobSpec? ks))
                {
                    if (spec.RequireAllKnobs)
                        throw new SpecError(
                            $"the circuit has a control '{k}' that the spec does not mention.\n" +
                            $"Add it under \"knobs\": {{\"{k}\": ...}}, or set \"requireAllKnobs\": false " +
                            "to have it filled in automatically.");

                    plan.UnspecifiedKnobs.Add(k);
                    ks = new KnobSpec();

                    if (ctrl.IsDiscrete)
                    {
                        for (int p = 0; p < ctrl.NumPositions; ++p)
                            ks.Bands.Add(new Band { Position = p, From = p, To = p + 1, Count = 1, Note = $"pos {p}" });
                        plan.Notes.Add($"{k} [switch] was not in the spec; sampled across all {ctrl.NumPositions} positions [0..{ctrl.NumPositions - 1}]");
                    }
                    else
                    {
                        ks.Bands.Add(new Band { From = 0.0, To = 1.0, Count = 1 });
                        plan.Notes.Add($"{k} [knob] was not in the spec and was filled in uniformly");
                    }

                    spec.Knobs[k] = ks;
                }
                else
                {
                    // Control was specified. If it is a discrete switch and user specified continuous [0, 1] band,
                    // adapt it to discrete positions
                    if (ctrl.IsDiscrete && ks.Pin == null && ks.Bands.Count == 1 && ks.Bands[0].Position == null)
                    {
                        long count = ks.Bands[0].Count;
                        ks.Bands.Clear();
                        for (int p = 0; p < ctrl.NumPositions; ++p)
                            ks.Bands.Add(new Band { Position = p, From = p, To = p + 1, Count = count, Note = $"pos {p}" });
                    }
                }
            }

            foreach (string k in spec.Knobs.Keys)
            {
                if (!knobs.Contains(k))
                    throw new SpecError(
                        $"the spec has a control '{k}', but the circuit only has [{string.Join(", ", knobs.Names)}].\n" +
                        "A control named in a spec that the circuit does not have is never rendered.");
            }

            long budget = ResolveBudget(spec, knobs, plan);

            switch (spec.Joint)
            {
                case JointMode.Grid: BuildGrid(spec, knobs, budget, plan); break;
                case JointMode.Sweep: BuildSweep(spec, knobs, budget, plan, randomOthers: false); break;
                case JointMode.SweepRandom: BuildSweep(spec, knobs, budget, plan, randomOthers: true); break;
                default: BuildIndependent(spec, knobs, budget, plan); break;
            }

            AssignExcerpts(spec, plan, input, log, out spans);
            return plan;
        }

        private static long ResolveBudget(Spec spec, KnobSet knobs, SamplingPlan plan)
        {
            // If any bands have relative weights (e.g. 70%), scale them against the budget
            long budgetToScale = spec.Budget > 0 ? spec.Budget : 1000;
            foreach (var kvp in spec.Knobs)
            {
                var ks = kvp.Value;
                if (ks.Bands.Any(b => b.Weight != null))
                {
                    long allocated = 0;
                    for (int i = 0; i < ks.Bands.Count; ++i)
                    {
                        var b = ks.Bands[i];
                        if (i == ks.Bands.Count - 1)
                        {
                            b.Count = Math.Max(1, budgetToScale - allocated);
                        }
                        else
                        {
                            double w = b.Weight ?? (1.0 / ks.Bands.Count);
                            b.Count = Math.Max(1, (long)Math.Round(budgetToScale * w));
                            allocated += b.Count;
                        }
                    }
                }
            }

            var totals = new List<(string knob, long total)>();

            foreach (string k in knobs.Names)
            {
                var ks = spec.Knobs[k];
                if (ks.Pin != null) continue;

                long t = ks.TotalCount;
                if (t <= 0)
                    throw new SpecError($"control '{k}' has bands but no renders: give at least one band a count above zero");

                totals.Add((k, t));
            }

            if (spec.Budget > 0)
            {
                foreach (string k in plan.UnspecifiedKnobs)
                {
                    if (spec.Knobs.TryGetValue(k, out var ks) && ks.Bands.Count == 1)
                        ks.Bands[0].Count = spec.Budget;
                }
                return spec.Budget;
            }

            if (totals.Count == 0)
            {
                return 1;
            }

            var specifiedTotals = totals.Where(t => !plan.UnspecifiedKnobs.Contains(t.knob)).ToList();

            long budget;
            if (specifiedTotals.Count > 0)
            {
                long first = specifiedTotals[0].total;
                var disagreeing = specifiedTotals.Where(t => t.total != first).Select(t => $"{t.knob}={t.total}").ToList();

                if (disagreeing.Count > 0)
                {
                    throw new SpecError(
                        $"the specified controls do not agree on how many renders there are: " +
                        $"{specifiedTotals[0].knob}={first}, {string.Join(", ", disagreeing)}.\n" +
                        $"Either set \"budget\": {first} (the bands are then scaled to it exactly), " +
                        "or make the band counts of every control sum to the same number.\n" +
                        Spec.BudgetNote);
                }

                budget = first;
            }
            else
            {
                budget = 1000;
            }

            foreach (string k in plan.UnspecifiedKnobs)
            {
                if (spec.Knobs.TryGetValue(k, out var ks))
                {
                    if (ks.Bands.Count == 1)
                        ks.Bands[0].Count = budget;
                    else if (ks.Bands.Count > 1)
                    {
                        long perBand = Math.Max(1, budget / ks.Bands.Count);
                        foreach (var b in ks.Bands) b.Count = perBand;
                    }
                }
            }

            spec.Budget = budget;
            return budget;
        }

        private static List<double> DrawKnob(
            KnobSpec ks, string knob, long budget, Random rng, Dictionary<string, long> counts, bool report)
        {
            var values = new List<double>((int)Math.Min(budget, int.MaxValue));

            if (ks.Pin != null)
            {
                for (long i = 0; i < budget; ++i) values.Add(ks.Pin.Value);
                return values;
            }

            var bands = ks.Bands.ToArray();
            long total = bands.Sum(b => b.Count);
            var exact = new double[bands.Length];

            for (int i = 0; i < bands.Length; ++i)
                exact[i] = total == 0 ? 0.0 : (double)budget * bands[i].Count / total;

            var n = new long[bands.Length];
            for (int i = 0; i < bands.Length; ++i) n[i] = (long)Math.Floor(exact[i]);

            long assigned = n.Sum();
            var order = Enumerable.Range(0, bands.Length)
                .OrderByDescending(i => exact[i] - n[i])
                .ThenBy(i => i)
                .ToList();

            for (int i = 0; assigned < budget && order.Count > 0; ++i, ++assigned)
                n[order[i % order.Count]]++;

            var pick = new List<int>((int)budget);
            for (int b = 0; b < bands.Length; ++b)
                for (long i = 0; i < n[b]; ++i) pick.Add(b);

            Shuffle(pick, rng);

            var cursor = new long[bands.Length];

            foreach (int b in pick)
            {
                Band band = bands[b];
                long j = cursor[b]++;

                if (band.Position != null)
                {
                    values.Add((double)band.Position.Value);
                }
                else
                {
                    double width = band.To - band.From;
                    double frac = width * ((j + rng.NextDouble()) / Math.Max(1L, n[b]));
                    values.Add(band.From + frac);
                }

                string key = $"{knob}[{b}]";
                if (report)
                {
                    counts.TryGetValue(key, out long have);
                    counts[key] = have + 1;
                }
            }

            return values;
        }

        private static List<RenderJob> GenerateAnchors(Spec spec, KnobSet knobs)
        {
            var anchors = new List<RenderJob>();
            if (!spec.Anchors) return anchors;

            // 1. All Min
            var minJob = new RenderJob { Origin = "anchor_min" };
            foreach (string k in knobs.Names) minJob.Knobs[k] = 0.0;
            anchors.Add(minJob);

            // 2. All Max
            var maxJob = new RenderJob { Origin = "anchor_max" };
            foreach (string k in knobs.Names)
            {
                var c = knobs[k];
                maxJob.Knobs[k] = c.IsDiscrete ? Math.Max(0, c.NumPositions - 1) : 1.0;
            }
            anchors.Add(maxJob);

            // 3. All Center / Noon
            var noonJob = new RenderJob { Origin = "anchor_noon" };
            foreach (string k in knobs.Names)
            {
                var c = knobs[k];
                noonJob.Knobs[k] = c.IsDiscrete ? c.NumPositions / 2 : 0.5;
            }
            anchors.Add(noonJob);

            // 4. Alternating Min / Max
            if (knobs.Names.Count > 1)
            {
                var alt1 = new RenderJob { Origin = "anchor_alt1" };
                var alt2 = new RenderJob { Origin = "anchor_alt2" };
                for (int i = 0; i < knobs.Names.Count; ++i)
                {
                    string k = knobs.Names[i];
                    var c = knobs[k];
                    double minVal = 0.0;
                    double maxVal = c.IsDiscrete ? Math.Max(0, c.NumPositions - 1) : 1.0;
                    alt1.Knobs[k] = (i % 2 == 0) ? minVal : maxVal;
                    alt2.Knobs[k] = (i % 2 == 0) ? maxVal : minVal;
                }
                anchors.Add(alt1);
                anchors.Add(alt2);
            }

            return anchors;
        }

        private static void BuildIndependent(Spec spec, KnobSet knobs, long budget, SamplingPlan plan)
        {
            var anchors = GenerateAnchors(spec, knobs);
            long regularBudget = Math.Max(0, budget - anchors.Count);

            foreach (var a in anchors)
                plan.Jobs.Add(a);

            var rng = new Random(spec.Seed);
            var perKnob = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);

            foreach (string k in knobs.Names)
                perKnob[k] = DrawKnob(spec.Knobs[k], k, regularBudget, rng, plan.BandCounts, report: true);

            for (long i = 0; i < regularBudget; ++i)
            {
                var job = new RenderJob
                {
                    Knobs = knobs.Names.ToDictionary(k => k, k => perKnob[k][(int)i]),
                    Origin = "independent",
                };
                plan.Jobs.Add(job);
            }

            plan.Budget = budget;
        }

        private static void BuildGrid(Spec spec, KnobSet knobs, long budget, SamplingPlan plan)
        {
            var names = knobs.Names;
            var effective = new List<KnobSpec>();

            foreach (string k in names)
            {
                var ks = spec.Knobs[k];
                if (ks.Pin != null || ks.Bands.Count == 0)
                    throw new SpecError($"control '{k}' is pinned or empty, which cannot form a grid axis");

                effective.Add(ks);
            }

            double cells = 1.0;
            foreach (var ks in effective) cells *= ks.Bands.Count;

            if (cells > 4_000_000)
                throw new SpecError(
                    $"a grid over these controls is {cells:N0} combinations before any within-band detail.\n" +
                    "Use \"joint\": \"independent\", which honours the same distributions without the combinatorial explosion.");

            var rng = new Random(spec.Seed);
            var shape = effective.Select(ks => ks.Bands.Count).ToArray();

            long total = (long)Math.Min(cells, budget);
            if (total < cells)
                plan.Notes.Add($"budget {budget} is below the {cells:N0} grid combinations, " +
                               $"so only the first {total:N0} are rendered. Raise budget, or use \"joint\": \"independent\".");

            var perCell = new long[shape.Aggregate(1, (x, y) => x * y)];
            long baseCount = total / perCell.Length;
            long extra = total % perCell.Length;
            for (int i = 0; i < perCell.Length; ++i) perCell[i] = baseCount + (i < extra ? 1 : 0);

            var cursor = new long[perCell.Length];

            for (int cell = 0; cell < perCell.Length; ++cell)
            {
                long reps = perCell[cell];
                if (reps <= 0) continue;

                var index = new int[shape.Length];
                int rest = cell;
                for (int k = shape.Length - 1; k >= 0; --k)
                {
                    index[k] = rest % shape[k];
                    rest /= shape[k];
                }

                for (long r = 0; r < reps; ++r)
                {
                    var job = new RenderJob { Knobs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) };
                    var origin = new List<string>();

                    for (int k = 0; k < shape.Length; ++k)
                    {
                        string name = names[k];
                        Band band = effective[k].Bands[index[k]];
                        long j = cursor[cell]++;

                        if (band.Position != null)
                        {
                            job.Knobs[name] = (double)band.Position.Value;
                            origin.Add($"{name}[pos={band.Position.Value}]");
                        }
                        else
                        {
                            double width = band.To - band.From;
                            double frac = width * ((j + rng.NextDouble()) / reps);
                            job.Knobs[name] = band.From + frac;
                            origin.Add($"{name}{Band(band)}");
                        }

                        string key = $"{name}[{index[k]}]";
                        plan.BandCounts.TryGetValue(key, out long have);
                        plan.BandCounts[key] = have + 1;
                    }

                    job.Origin = "grid: " + string.Join(" ", origin);
                    plan.Jobs.Add(job);
                }
            }

            plan.Budget = plan.Jobs.Count;
        }

        private static void BuildSweep(Spec spec, KnobSet knobs, long budget, SamplingPlan plan, bool randomOthers)
        {
            var names = knobs.Names;
            var rng = new Random(spec.Seed);

            if (names.Count == 0)
            {
                plan.Jobs.Add(new RenderJob { Knobs = new Dictionary<string, double>(), Origin = "no controls" });
                plan.Budget = 1;
                return;
            }

            long perKnob = Math.Max(1, budget / names.Count);

            foreach (string swept in names)
            {
                ControlInfo sweptInfo = knobs[swept];
                var others = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                int steps = sweptInfo.IsDiscrete
                    ? sweptInfo.NumPositions
                    : (int)Math.Max(2, Math.Min(spec.SweepSteps, perKnob));

                if (randomOthers)
                {
                    foreach (string k in names)
                        if (k != swept) others[k] = DrawKnob(spec.Knobs[k], k, steps, rng, plan.BandCounts, report: false)[0];
                }
                else
                {
                    foreach (string k in names)
                    {
                        if (k != swept)
                        {
                            others[k] = spec.Knobs[k].Pin ?? (knobs[k].IsDiscrete ? 0.0 : spec.Pin);
                        }
                    }
                }

                for (int i = 0; i < steps; ++i)
                {
                    double frac = sweptInfo.IsDiscrete
                        ? (double)i
                        : (steps == 1 ? 0.5 : (double)i / (steps - 1));

                    var job = new RenderJob
                    {
                        Knobs = new Dictionary<string, double>(others, StringComparer.OrdinalIgnoreCase),
                        Origin = $"sweep {swept}" + (randomOthers ? " (others random)" : " (others pinned)"),
                    };

                    job.Knobs[swept] = frac;
                    plan.Jobs.Add(job);

                    string key = $"{swept}[sweep]";
                    plan.BandCounts.TryGetValue(key, out long have);
                    plan.BandCounts[key] = have + 1;
                }
            }

            plan.Budget = plan.Jobs.Count;
            plan.Notes.Add(
                "sweeps cover one control at a time. Multi-control interactions away from nominal points " +
                "are interpolated rather than rendered. Add \"joint\": \"independent\" passes to train cross-control interactions.");
        }

        private static void AssignExcerpts(Spec spec, SamplingPlan plan, Wave input, TextWriter log, out List<double[]> spans)
        {
            double duration = spec.ExcerptSeconds;
            double total = input.Seconds;
            double step = spec.Stride > 0.0 ? spec.Stride : duration;
            var starts = new List<double>();

            if (input.Length > 0)
            {
                spans = UsableSpans(input);

                if (spec.Spans == "signal")
                {
                    foreach (double[] span in spans)
                        for (double t = span[0]; t + duration <= span[1] + 1e-9; t += step)
                            starts.Add(t);

                    log.WriteLine(
                        "excerpts: {0} start points inside the signal ({1:F2}s of {2:F2}s carries signal, {3:F2}s quiet, stride {4:F1}s)",
                        starts.Count,
                        SignalSeconds(spans), input.Seconds, input.Seconds - SignalSeconds(spans), step);
                }
                else // spec.Spans == "all"
                {
                    for (double t = 0.0; t + duration <= total + 1e-9; t += step)
                        starts.Add(t);

                    log.WriteLine(
                        "excerpts: {0} start points across entire input ({1:F2}s total, duration {2:F1}s, stride {3:F1}s)",
                        starts.Count, input.Seconds, duration, step);
                }
            }
            else
            {
                spans = new List<double[]>();
            }

            // Apply optional timeRange crop
            if (spec.TimeRange != null && spec.TimeRange.Length == 2)
            {
                double rLo = spec.TimeRange[0];
                double rHi = spec.TimeRange[1];
                starts = starts.Where(t => t >= rLo && t + duration <= rHi + 1e-6).ToList();
                log.WriteLine("crop    : restricted to timeRange [{0:F1}s..{1:F1}s] ({2} start points available)",
                    rLo, rHi, starts.Count);
            }

            bool random = spec.Placement == "random";
            var rng = new Random(spec.Seed ^ 0x5f3759df);

            if (starts.Count == 0)
            {
                int segments = (int)Math.Floor(total / duration);
                if (segments < 1)
                    throw new SpecError(
                        $"the input is {total:F2}s, too short for even one {duration:F1}s excerpt.\n" +
                        "Use a longer input, or a shorter \"duration\".");

                double remainder = total - segments * duration;
                if (remainder > 1e-6)
                    log.WriteLine($"note: {remainder:F2}s of the input is left over and will not be rendered " +
                                  $"({segments} x {duration:F1}s = {segments * duration:F2}s)");

                for (int i = 0; i < plan.Jobs.Count; ++i)
                    plan.Jobs[i].Offset = random
                        ? Math.Floor(rng.NextDouble() * segments) * duration
                        : (double)(i % segments) * duration;
            }
            else
            {
                for (int i = 0; i < plan.Jobs.Count; ++i)
                    plan.Jobs[i].Offset = random
                        ? starts[rng.Next(starts.Count)]
                        : starts[i % starts.Count];
            }

            foreach (RenderJob job in plan.Jobs)
            {
                double db = spec.GainDbLow + rng.NextDouble() * (spec.GainDbHigh - spec.GainDbLow);
                job.InputGain = Math.Round(Math.Pow(10.0, db / 20.0), 6);
            }

            // Assign train / val / test splits
            if (!string.IsNullOrEmpty(spec.Split) && spec.Split != "none" && spec.Split.Contains("/"))
            {
                var parts = spec.Split.Split('/');
                if (parts.Length >= 2 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double pTrain) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double pVal))
                {
                    double pTest = parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double pt) ? pt : 0.0;
                    double sum = pTrain + pVal + pTest;
                    if (sum > 0)
                    {
                        double rTrain = pTrain / sum;
                        double rVal = (pTrain + pVal) / sum;
                        for (int i = 0; i < plan.Jobs.Count; ++i)
                        {
                            double roll = rng.NextDouble();
                            if (roll < rTrain) plan.Jobs[i].Split = "train";
                            else if (roll < rVal) plan.Jobs[i].Split = "val";
                            else plan.Jobs[i].Split = "test";
                        }
                    }
                }
            }
        }

        private static double SignalSeconds(List<double[]> spans)
        {
            double s = 0.0;
            foreach (double[] span in spans) s += span[1] - span[0];
            return s;
        }

        private static List<double[]> UsableSpans(Wave input)
        {
            var measure = input.Analyze();
            return FindUsableSpans(input.Samples, input.SampleRate, measure.Peak);
        }

        private static List<double[]> FindUsableSpans(double[] x, int sr, double peak)
        {
            const double MinGapSeconds = 0.15;

            int win = Math.Max(64, sr / 200);
            int n = x.Length / win;
            double thresh = Math.Max(peak * 1e-4, 1e-7);
            double thresh2 = thresh * thresh;

            var quiet = new bool[n];
            for (int w = 0; w < n; ++w)
            {
                double acc = 0.0;
                int o = w * win;
                for (int i = 0; i < win; ++i)
                {
                    double v = x[o + i];
                    acc += v * v;
                }
                quiet[w] = acc < thresh2 * win;
            }

            var gaps = new List<double[]>();
            double gapStart = -1.0;

            for (int w = 0; w <= n; ++w)
            {
                bool q = w < n && quiet[w];
                if (q)
                {
                    if (gapStart < 0.0) gapStart = w * win / (double)sr;
                }
                else if (gapStart >= 0.0)
                {
                    double gapEnd = w * win / (double)sr;
                    if (gapEnd - gapStart >= MinGapSeconds) gaps.Add(new double[] { gapStart, gapEnd });
                    gapStart = -1.0;
                }
            }

            var usable = new List<double[]>();
            double cursor = 0.0;
            foreach (double[] g in gaps)
            {
                if (g[0] > cursor) usable.Add(new double[] { cursor, g[0] });
                cursor = g[1];
            }

            double total = x.Length / (double)sr;
            if (cursor < total) usable.Add(new double[] { cursor, total });
            return usable;
        }

        private static string Band(Band b)
        {
            if (b.Position != null)
                return $"[pos={b.Position.Value}]";
            return string.Format(CultureInfo.InvariantCulture, "[{0:0.####},{1:0.####})", b.From, b.To);
        }

        private static void Shuffle<T>(IList<T> list, Random rng)
        {
            for (int i = list.Count - 1; i > 0; --i)
            {
                int j = rng.Next(i + 1);
                T t = list[i];
                list[i] = list[j];
                list[j] = t;
            }
        }
    }
}
