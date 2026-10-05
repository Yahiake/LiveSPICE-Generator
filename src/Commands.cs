// File: Commands.cs
// The verbs behind the command line.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Circuit;
using Util;

namespace LiveSpiceGen
{
    public static class Commands
    {
        private const string ToolName = "livespice-gen";

        // ---- knobs / controls ----------------------------------------------

        /// <summary>
        /// Lists the knobs, switches, inputs, and speakers a circuit exposes.
        /// </summary>
        public static int Knobs(Args args, TextWriter stdout, TextWriter stderr)
        {
            string file = args.Require(0, "circuit.schx");

            if (!File.Exists(file))
            {
                stderr.WriteLine($"error: no such circuit: {file}");
                return 1;
            }

            var log = new ConsoleLog { Verbosity = MessageType.Error };

            Circuit.Circuit circuit;
            try
            {
                circuit = Schematic.Load(file, log).Build();
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"error: could not load {file}");
                stderr.WriteLine("       " + ex.Message);
                return 1;
            }

            circuit.Name = Path.GetFileNameWithoutExtension(file);

            var controls = new KnobSet(circuit);

            stdout.WriteLine($"{circuit.Name}  ({file})");

            int pots = circuit.Components.OfType<Potentiometer>().Count() +
                       circuit.Components.OfType<VariableResistor>().Count();
#pragma warning disable CS0612
            int switches = circuit.Components.OfType<SinglePoleSwitch>().Count() +
                           circuit.Components.OfType<Switch>().Count();
#pragma warning restore CS0612

            stdout.WriteLine($"{pots} potentiometer(s), {switches} switch(es), {controls.Count} total interactive control(s)");

            var inputs = circuit.Components.OfType<Input>().ToList();
            if (inputs.Count > 0)
                stdout.WriteLine("inputs  : " + string.Join(", ", inputs.Select(i => $"{i.Name} (V0dBFS = {i.V0dBFS})")));

            var speakers = circuit.Components.OfType<Speaker>().ToList();
            if (speakers.Count > 0)
                stdout.WriteLine("speakers: " + string.Join(", ", speakers.Select(s => $"{s.Name} (V0dBFS = {s.V0dBFS}, {s.Impedance})")));

            if (controls.Count == 0)
            {
                stdout.WriteLine();
                stdout.WriteLine("Static circuit (no potentiometers or switches).");
                stdout.WriteLine("Audio can be rendered directly through it with 'process' or 'render'.");
                return 0;
            }

            stdout.WriteLine();

            foreach (string line in controls.Describe())
                stdout.WriteLine("  " + line);

            if (args.Flag("json"))
            {
                stdout.WriteLine();
                stdout.WriteLine(Json.Array(controls.Names));
            }

            return 0;
        }

        // ---- process (single-track / reamp / NAM target mode) --------------

        /// <summary>
        /// Renders full input audio through a circuit without slicing into excerpts.
        /// Perfect for generating a single training target for NAM or re-amping a track.
        /// </summary>
        public static int Process(Args args, TextWriter stdout, TextWriter stderr)
        {
            if (args.Positional.Count < 2)
            {
                stderr.WriteLine("usage: livespice-gen process <circuit.schx> <in.wav> [out.wav] [options]");
                stderr.WriteLine("       renders full audio through the circuit at specific settings (or default/static circuit)");
                return 1;
            }

            string circuitFile = args.Require(0, "circuit.schx");
            string inputFile = args.Require(1, "in.wav");

            circuitFile = ResolveCircuit(circuitFile, null!);
            inputFile = ResolveInput(inputFile, null!);

            if (!File.Exists(circuitFile))
            {
                stderr.WriteLine($"error: no such circuit: {circuitFile}");
                return 1;
            }

            if (!File.Exists(inputFile))
            {
                stderr.WriteLine($"error: no such input WAV: {inputFile}");
                return 1;
            }

            string outputFile = args.Positional.Count > 2
                ? args.Positional[2]
                : Path.Combine(
                    Path.GetDirectoryName(inputFile) ?? "",
                    $"{Path.GetFileNameWithoutExtension(inputFile)}_processed.wav");

            var log = new ConsoleLog { Verbosity = MessageType.Error };
            Circuit.Circuit circuit;
            try
            {
                circuit = Schematic.Load(circuitFile, log).Build();
                circuit.Name = Path.GetFileNameWithoutExtension(circuitFile);
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"error: could not load {circuitFile}: {ex.Message}");
                return 1;
            }

            var controls = new KnobSet(circuit);

            // Parse and apply knob settings
            var dialedValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            // 1. Defaults: 0.5 for pots, 0 for switches
            foreach (var ctrl in controls.Controls)
            {
                dialedValues[ctrl.Name] = ctrl.IsDiscrete ? 0.0 : 0.5;
            }

            // 2. Parse --knobs "Gain=0.35,Tone=0.7,Pull Bright=1"
            void ParseKnobPair(string pair)
            {
                int eq = pair.IndexOf('=');
                if (eq > 0)
                {
                    string name = pair.Substring(0, eq).Trim();
                    string valStr = pair.Substring(eq + 1).Trim();
                    if (double.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    {
                        dialedValues[name] = v;
                    }
                }
            }

            if (args.Has("knobs"))
            {
                string raw = args.Get("knobs", "");
                foreach (string part in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    ParseKnobPair(part);
            }

            foreach (string opt in args.GetAll("knob")) ParseKnobPair(opt);
            foreach (string opt in args.GetAll("switch")) ParseKnobPair(opt);

            controls.Apply(dialedValues);

            Wave inputWave;
            try
            {
                inputWave = Wave.Read(inputFile);
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"error: could not read {inputFile}: {ex.Message}");
                return 1;
            }

            var settings = new RenderSettings
            {
                SampleRate = args.GetInt("rate", inputWave.SampleRate),
                Oversample = args.GetInt("oversample", 2),
                Iterations = args.GetInt("iterations", 8),
                WarmupSeconds = args.GetDouble("warmup", 0.05),
                Speaker = args.Has("speaker") ? args.Get("speaker", "") : null,
                Normalize = args.GetDouble("normalize", 0.0) // 0 = raw analog output
            };

            settings.Validate();

            if (inputWave.SampleRate != settings.SampleRate)
            {
                stdout.WriteLine("resampling input: {0} Hz -> {1} Hz", inputWave.SampleRate, settings.SampleRate);
                inputWave = inputWave.Resample(settings.SampleRate);
            }

            double driveDb = args.GetDouble("gain", 0.0);
            double driveGain = Math.Pow(10.0, driveDb / 20.0);

            stdout.WriteLine("circuit : {0}", circuit.Name);
            if (controls.Count > 0)
            {
                stdout.WriteLine("settings: {0}", string.Join(", ", controls.Names.Select(k =>
                {
                    var c = controls[k];
                    return c.IsDiscrete ? $"{k}={dialedValues[k]:F0}" : $"{k}={dialedValues[k]:F2}";
                })));
            }
            else
            {
                stdout.WriteLine("settings: (static circuit, no controls)");
            }
            stdout.WriteLine("input   : {0} ({1:F2}s, {2} Hz)", Path.GetFileName(inputFile), inputWave.Seconds, inputWave.SampleRate);
            if (driveDb != 0.0) stdout.WriteLine("drive   : {0:F1} dB (gain {1:F3})", driveDb, driveGain);
            stdout.WriteLine("rate    : {0}", settings);

            Renderer renderer;
            try
            {
                renderer = new Renderer(circuitFile, inputWave, inputFile, settings);
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"error: could not initialize simulation: {ex.Message}");
                return 1;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            int lastPercent = -1;

            double[]? outputSamples = renderer.Process(
                inputWave.Samples,
                driveGain,
                progress =>
                {
                    int p = (int)(progress * 100);
                    if (p != lastPercent && p % 5 == 0 && !args.Flag("quiet"))
                    {
                        lastPercent = p;
                        double elapsed = clock.Elapsed.TotalSeconds;
                        double speed = elapsed > 0 ? (inputWave.Seconds * progress) / elapsed : 0;
                        stdout.Write($"\rsimulating: [{new string('#', p / 5)}{new string('-', 20 - p / 5)}] {p,3}% ({speed:F1}x realtime)   ");
                        stdout.Flush();
                    }
                },
                out Wave.Measure measure,
                out string? err);

            if (!args.Flag("quiet") && lastPercent >= 0) stdout.WriteLine();

            if (outputSamples == null || err != null)
            {
                stderr.WriteLine($"error: simulation failed: {err}");
                return 1;
            }

            if (settings.Normalize > 0.0 && measure.Peak > 0.0)
            {
                double g = settings.Normalize / measure.Peak;
                for (int i = 0; i < outputSamples.Length; ++i) outputSamples[i] *= g;
                measure = Wave.FromSamples(settings.SampleRate, outputSamples).Analyze();
                stdout.WriteLine("normalize: scaled to peak {0:F3} (gain {1:F3})", settings.Normalize, g);
            }

            string? outDir = Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            Wave.WriteMono(outputFile, outputSamples, settings.SampleRate, 32);

            clock.Stop();
            double wallClock = clock.Elapsed.TotalSeconds;
            double realtimeFactor = wallClock > 0 ? inputWave.Seconds / wallClock : 0;

            stdout.WriteLine();
            stdout.WriteLine("rendered: {0:F2}s in {1:F1}s ({2:F1}x realtime)", inputWave.Seconds, wallClock, realtimeFactor);
            stdout.WriteLine("stats   : peak={0:F4} rms={1:F4} mean={2:E2} clip={3}", measure.Peak, measure.Rms, measure.Mean, measure.Clipped);
            stdout.WriteLine("output  : {0}", Path.GetFullPath(outputFile));

            return 0;
        }

        // ---- spec / template ----------------------------------------------

        /// <summary>
        /// Generates a customized spec JSON template for a given circuit schematic.
        /// Discovers all controls (pots + switches) and populates sample bands so users can
        /// immediately edit and run.
        /// </summary>
        public static int GenerateSpec(Args args, TextWriter stdout, TextWriter stderr)
        {
            string circuitPath = args.Require(0, "circuit.schx");
            if (!File.Exists(circuitPath))
            {
                stderr.WriteLine($"error: no such circuit: {circuitPath}");
                return 1;
            }

            var log = new ConsoleLog { Verbosity = MessageType.Error };
            Circuit.Circuit circuit;
            try
            {
                circuit = Schematic.Load(circuitPath, log).Build();
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"error: could not load {circuitPath}: {ex.Message}");
                return 1;
            }

            circuit.Name = Path.GetFileNameWithoutExtension(circuitPath);
            var knobSet = new KnobSet(circuit);

            string pedalName = Renderer.Slug(circuit.Name);
            string outPath = args.Positional.Count > 1
                ? args.Positional[1]
                : args.Get("out", $"{pedalName}.spec.json");

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"name\": \"{pedalName}-dataset\",");
            sb.AppendLine($"  \"circuit\": \"{circuitPath.Replace("\\", "/")}\",");
            sb.AppendLine("  \"input\": \"input.wav\",");
            sb.AppendLine("  \"duration\": 15.0,");
            sb.AppendLine("  \"render\": {");
            sb.AppendLine("    \"sampleRate\": 48000,");
            sb.AppendLine("    \"oversample\": 2,");
            sb.AppendLine("    \"iterations\": 8,");
            sb.AppendLine("    \"normalize\": 0.9,");
            sb.AppendLine("    \"warmup\": 0.05");
            sb.AppendLine("  },");
            sb.AppendLine("  \"sampling\": {");
            sb.AppendLine("    \"joint\": \"independent\",");
            sb.AppendLine("    \"spans\": \"signal\",");
            sb.AppendLine("    \"gainDb\": [-18.0, 6.0],");
            sb.AppendLine("    \"threads\": 0");
            sb.AppendLine("  },");
            sb.AppendLine("  \"knobs\": {");

            for (int i = 0; i < knobSet.Names.Count; ++i)
            {
                string k = knobSet.Names[i];
                ControlInfo ctrl = knobSet[k];
                string comma = (i < knobSet.Names.Count - 1) ? "," : "";

                sb.AppendLine($"    \"{k}\": {{");
                if (ctrl.IsDiscrete)
                {
                    sb.AppendLine($"      \"positions\": [{string.Join(", ", Enumerable.Range(0, ctrl.NumPositions))}]");
                }
                else
                {
                    bool isDriveOrVol = k.IndexOf("gain", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        k.IndexOf("drive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        k.IndexOf("volume", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        k.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (isDriveOrVol)
                    {
                        sb.AppendLine("      \"bands\": [");
                        sb.AppendLine("        { \"from\": 0.0, \"to\": 0.2, \"count\": 1000, \"note\": \"low range detail\" },");
                        sb.AppendLine("        { \"from\": 0.2, \"to\": 1.0, \"count\": 1500, \"note\": \"high range\" }");
                        sb.AppendLine("      ]");
                    }
                    else
                    {
                        sb.AppendLine("      \"bands\": [");
                        sb.AppendLine("        { \"from\": 0.0, \"to\": 1.0, \"count\": 2500 }");
                        sb.AppendLine("      ]");
                    }
                }
                sb.AppendLine($"    }}{comma}");
            }

            sb.AppendLine("  }");
            sb.AppendLine("}");

            File.WriteAllText(outPath, sb.ToString());
            stdout.WriteLine($"Generated spec template for {circuit.Name} with {knobSet.Count} control(s):");
            stdout.WriteLine($"  {outPath}");
            stdout.WriteLine();
            stdout.WriteLine("Inspect or edit the bands in the file, then test with:");
            stdout.WriteLine($"  livespice-gen plan \"{outPath}\"");
            stdout.WriteLine($"  livespice-gen render \"{outPath}\" ./dataset");
            return 0;
        }

        // ---- plan ----------------------------------------------------------

        /// <summary>
        /// Works out what a spec would render, and renders nothing.
        /// </summary>
        public static int Plan(Args args, TextWriter stdout, TextWriter stderr)
        {
            string firstArg = args.Require(0, "spec.json or circuit.schx");
            Spec spec;

            if (firstArg.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    spec = Spec.Load(firstArg);
                }
                catch (SpecError ex)
                {
                    stderr.WriteLine("error: " + ex.Message);
                    return 1;
                }
                ApplyOverrides(spec, args);
            }
            else
            {
                string circuitFile = firstArg;
                string inputFile = args.Positional.Count > 1 ? args.Positional[1] : args.Get("input", "");
                spec = new Spec
                {
                    Circuit = circuitFile,
                    Input = inputFile,
                };
                ApplyOverrides(spec, args);
            }

            string circuit = args.Has("circuit")
                ? Path.GetFullPath(args.Get("circuit", ""))
                : ResolveCircuit(spec.Circuit, spec.SourcePath);

            string input = args.Has("input")
                ? Path.GetFullPath(args.Get("input", ""))
                : ResolveInput(spec.Input, spec.SourcePath);

            if (string.IsNullOrEmpty(circuit)) { stderr.WriteLine("error: no circuit; set \"circuit\" in the spec or pass --circuit"); return 1; }
            if (string.IsNullOrEmpty(input)) { stderr.WriteLine("error: no input; set \"input\" in the spec or pass --input"); return 1; }

            return WithCircuit(args, stdout, stderr, circuit, input, spec.Render, (renderer, knobs, wave, path) =>
            {
                var plan = Sampler.Build(spec, knobs, wave, stdout, out List<double[]> spans);
                int rate = spec.Render.SampleRate;

                ReportPlan(spec, plan, renderer, wave, rate, stdout, args.Flag("verbose"));
                return 0;
            });
        }

        // ---- render --------------------------------------------------------

        public static int Render(Args args, TextWriter stdout, TextWriter stderr)
        {
            string firstArg = args.Require(0, "spec.json or circuit.schx");
            Spec spec;
            string outDir;

            if (firstArg.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    spec = Spec.Load(firstArg);
                }
                catch (SpecError ex)
                {
                    stderr.WriteLine("error: " + ex.Message);
                    return 1;
                }

                outDir = args.Positional.Count > 1 ? args.Positional[1] : args.Get("output", "dataset");
                ApplyOverrides(spec, args);
            }
            else
            {
                string circuitFile = firstArg;
                string inputFile = args.Positional.Count > 1 ? args.Positional[1] : args.Get("input", "");
                outDir = args.Positional.Count > 2 ? args.Positional[2] : args.Get("output", "dataset");

                if (string.IsNullOrEmpty(inputFile))
                {
                    stderr.WriteLine("error: missing input WAV argument\nusage: livespice-gen render <circuit.schx> <in.wav> <outdir> [options]");
                    return 1;
                }

                spec = new Spec
                {
                    Circuit = circuitFile,
                    Input = inputFile,
                };
                ApplyOverrides(spec, args);
            }

            string circuit = args.Has("circuit")
                ? Path.GetFullPath(args.Get("circuit", ""))
                : ResolveCircuit(spec.Circuit, spec.SourcePath);

            string input = args.Has("input")
                ? Path.GetFullPath(args.Get("input", ""))
                : ResolveInput(spec.Input, spec.SourcePath);

            if (string.IsNullOrEmpty(circuit)) { stderr.WriteLine("error: no circuit; set \"circuit\" in the spec or pass --circuit"); return 1; }
            if (string.IsNullOrEmpty(input)) { stderr.WriteLine("error: no input; set \"input\" in the spec or pass --input"); return 1; }

            if (args.Flag("dry-run"))
                return Plan(args, stdout, stderr);

            return WithCircuit(args, stdout, stderr, circuit, input, spec.Render, (renderer, knobs, wave, normalised) =>
            {
                SamplingPlan plan;
                try
                {
                    plan = Sampler.Build(spec, knobs, wave, stdout, out List<double[]> spans);
                }
                catch (SpecError ex)
                {
                    stderr.WriteLine("error: " + ex.Message);
                    return 1;
                }

                int rate = spec.Render.SampleRate;
                ReportPlan(spec, plan, renderer, wave, rate, stdout, verbose: false);

                // Estimate single render time
                double perRender = renderer.SecondsPerSecondOfAudio(spec.Render, spec.ExcerptSeconds, out string probeNote);
                int numThreads = spec.Threads > 0 ? spec.Threads : Math.Clamp(Environment.ProcessorCount, 1, 8);

                stdout.WriteLine("speed   : {0:F2}s of wall clock per second of audio{1}",
                    perRender, probeNote.Length > 0 ? " (" + probeNote + ")" : "");
                double hours = (plan.Jobs.Count * perRender * spec.ExcerptSeconds / numThreads) / 3600.0;
                stdout.WriteLine("estimate: {0:F2} h for {1} renders on {2} thread(s)",
                    hours, plan.Jobs.Count, numThreads);
                stdout.WriteLine();

                int limit = args.GetInt("limit", 0);
                if (limit > 0 && limit < plan.Jobs.Count)
                    stdout.WriteLine("note    : --limit {0}, so {1} renders will be skipped",
                        limit, plan.Jobs.Count - limit);

                return RunJobs(args, stdout, stderr, renderer, plan, outDir, wave, spec, perRender, numThreads);
            });
        }

        // ---- verify --------------------------------------------------------

        /// <summary>
        /// Re-checks a dataset against itself: manifest rows against files on disk, knobs
        /// against the manifest header, durations against the audio.
        /// </summary>
        public static int Verify(Args args, TextWriter stdout, TextWriter stderr)
        {
            string dir = args.Get("dataset", args.Require(0, "dataset"));
            string manifestPath = Path.Combine(dir, "manifest.jsonl");
            string renderDir = Path.Combine(dir, "renders");

            if (!File.Exists(manifestPath))
            {
                stderr.WriteLine($"error: no manifest at {manifestPath}");
                return 1;
            }

            var rows = new List<JsonElement>();
            string? header = null;
            var problems = new List<string>();

            foreach (string line in File.ReadLines(manifestPath))
            {
                if (line.Trim().Length == 0) continue;

                try
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(line))
                    {
                        if (header == null && doc.RootElement.TryGetProperty("type", out var t) &&
                            t.ValueKind == System.Text.Json.JsonValueKind.String && t.GetString() == "dataset")
                        {
                            header = line;
                            continue;
                        }
                        rows.Add(doc.RootElement.Clone());
                    }
                }
                catch (System.Text.Json.JsonException ex)
                {
                    problems.Add($"a manifest line is not valid JSON ({ex.Message})");
                }
            }

            if (header == null)
                problems.Add("the manifest has no dataset header line");

            string[] knobs = header != null ? KnobsFromHeader(header) : Array.Empty<string>();

            int ok = 0, failed = 0, missing = 0, flat = 0, nonfinite = 0, clipped = 0;
            var ids = new HashSet<string>(StringComparer.Ordinal);

            foreach (JsonElement row in rows)
            {
                string id = Str(row, "id") ?? "";
                string file = Str(row, "file") ?? (id + ".wav");
                string status = Str(row, "status") ?? "";

                if (!ids.Add(id)) problems.Add($"id {id} appears more than once");
                if (knobs.Length > 0 && !row.TryGetProperty("knobs", out _))
                    problems.Add($"row {id} has no knobs");

                if (status == "failed")
                {
                    ++failed;
                    string why = Str(row, "error") ?? "unknown";
                    stdout.WriteLine($"  failed   {id}  {why}");
                    continue;
                }

                string path = Path.Combine(renderDir, file);
                if (!File.Exists(path))
                {
                    ++missing;
                    problems.Add($"{id} is in the manifest but {file} is not on disk");
                    continue;
                }

                if (Num(row, "nonfinite") > 0) { ++nonfinite; problems.Add($"{id} contains NaN or infinity"); }
                if (Num(row, "flat") > 0) { ++flat; problems.Add($"{id} is flat: the circuit produced nothing at this setting"); }
                if (Num(row, "clipped") > 0) ++clipped;

                double seconds = Num(row, "duration_s");
                double offset = Num(row, "offset_s");
                if (seconds <= 0) problems.Add($"{id} has a non-positive duration");

                ++ok;
            }

            stdout.WriteLine("dataset : {0}", dir);
            stdout.WriteLine("controls: {0}", knobs.Length == 0 ? "(unknown)" : string.Join(", ", knobs));
            stdout.WriteLine("rows    : {0} ok, {1} failed, {2} missing files", ok, failed, missing);
            stdout.WriteLine("audio   : {0} flat, {1} with non-finite samples, {2} clipped at least once",
                flat, nonfinite, clipped);

            if (problems.Count == 0)
            {
                stdout.WriteLine();
                stdout.WriteLine("clean. {0} renders agree with their files.", ok);
                return 0;
            }

            stdout.WriteLine();
            stdout.WriteLine("{0} problem(s):", problems.Count);
            foreach (string p in problems.Take(200)) stdout.WriteLine("  " + p);
            if (problems.Count > 200) stdout.WriteLine("  ... and {0} more", problems.Count - 200);

            return 1;
        }

        // ---- shared machinery ----------------------------------------------

        private static void ApplyOverrides(Spec spec, Args args)
        {
            spec.Render.SampleRate = args.GetInt("rate", spec.Render.SampleRate);
            spec.Render.Oversample = args.GetInt("oversample", spec.Render.Oversample);
            spec.Render.Iterations = args.GetInt("iterations", spec.Render.Iterations);
            spec.Render.Normalize = args.GetDouble("normalize", spec.Render.Normalize);
            spec.Render.WarmupSeconds = args.GetDouble("warmup", spec.Render.WarmupSeconds);

            if (args.Has("speaker"))
                spec.Render.Speaker = args.Get("speaker", "");

            spec.ExcerptSeconds = args.GetDouble("duration", spec.ExcerptSeconds);
            spec.Budget = args.GetInt("budget", (int)spec.Budget);
            spec.Seed = args.GetInt("seed", spec.Seed);

            int threads = args.GetInt("threads", spec.Threads);
            if (args.Has("j")) threads = args.GetInt("j", threads);
            spec.Threads = threads;

            if (args.Has("gain"))
            {
                double g = args.GetDouble("gain", 0.0);
                spec.GainDbLow = g;
                spec.GainDbHigh = g;
            }
            if (args.Has("gain-low")) spec.GainDbLow = args.GetDouble("gain-low", spec.GainDbLow);
            if (args.Has("gain-high")) spec.GainDbHigh = args.GetDouble("gain-high", spec.GainDbHigh);

            if (args.Has("joint"))
            {
                if (Enum.TryParse<JointMode>(args.Get("joint", ""), ignoreCase: true, out var jm))
                    spec.Joint = jm;
            }

            if (args.Has("steps")) spec.SweepSteps = args.GetInt("steps", spec.SweepSteps);
            if (args.Has("pin")) spec.Pin = args.GetDouble("pin", spec.Pin);

            if (args.Has("spans")) spec.Spans = args.Get("spans", spec.Spans);
            if (args.Has("placement")) spec.Placement = args.Get("placement", spec.Placement);

            if (args.Has("time-range"))
            {
                string tr = args.Get("time-range", "");
                if (tr.Contains(".."))
                {
                    var parts = tr.Split(new[] { ".." }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 &&
                        double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t0) &&
                        double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double t1))
                    {
                        spec.TimeRange = new[] { t0, t1 };
                    }
                }
                else if (tr.Contains(","))
                {
                    var parts = tr.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 &&
                        double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t0) &&
                        double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double t1))
                    {
                        spec.TimeRange = new[] { t0, t1 };
                    }
                }
            }

            if (args.Flag("anchors")) spec.Anchors = true;

            foreach (string focusArg in args.GetAll("focus"))
                ParseFocusCli(spec, focusArg);

            foreach (string knobArg in args.GetAll("knob"))
                ParseKnobCli(spec, knobArg);

            foreach (string switchArg in args.GetAll("switch"))
                ParseSwitchCli(spec, switchArg);

            spec.Render.Validate();
        }

        private static void ParseFocusCli(Spec spec, string focusArg)
        {
            int eq = focusArg.IndexOf('=');
            if (eq <= 0) return;
            string knobName = focusArg.Substring(0, eq).Trim();
            string val = focusArg.Substring(eq + 1).Trim();

            // Default to 70% if no count/percentage was specified
            if (!val.Contains(":"))
                val = val + ":70%";

            ParseKnobCli(spec, $"{knobName}={val}");
        }

        private static void ParseSwitchCli(Spec spec, string switchArg)
        {
            int eq = switchArg.IndexOf('=');
            if (eq <= 0) return;
            string name = switchArg.Substring(0, eq).Trim();
            string val = switchArg.Substring(eq + 1).Trim();

            var ks = new KnobSpec();
            if (val.Contains(","))
            {
                var parts = val.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    if (int.TryParse(p.Trim(), out int pos))
                        ks.Bands.Add(new Band { Position = pos, From = pos, To = pos + 1, Count = 1, Note = $"pos {pos}" });
                }
            }
            else if (int.TryParse(val, out int singlePos))
            {
                ks.Pin = singlePos;
            }

            if (ks.Bands.Count > 0 || ks.Pin != null)
                spec.Knobs[name] = ks;
        }

        private static void ParseKnobCli(Spec spec, string knobArg)
        {
            int eq = knobArg.IndexOf('=');
            if (eq <= 0) return;
            string knobName = knobArg.Substring(0, eq).Trim();
            string val = knobArg.Substring(eq + 1).Trim();

            var ks = new KnobSpec();
            if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double pinVal))
            {
                ks.Pin = pinVal;
                spec.Knobs[knobName] = ks;
                return;
            }

            string[] bands = val.Split(',');
            foreach (string b in bands)
            {
                string bandStr = b.Trim();
                if (bandStr.Length == 0) continue;

                long count = 1000;
                double? weight = null;
                string rangeStr = bandStr;

                int colon = bandStr.LastIndexOf(':');
                if (colon >= 0)
                {
                    string countOrWeight = bandStr.Substring(colon + 1).Trim();
                    if (countOrWeight.EndsWith("%") && double.TryParse(countOrWeight.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                    {
                        weight = Math.Clamp(pct / 100.0, 0.0, 1.0);
                    }
                    else if (long.TryParse(countOrWeight, out long parsedCount))
                    {
                        count = parsedCount;
                    }
                    rangeStr = bandStr.Substring(0, colon);
                }

                double lo = 0.0, hi = 1.0;
                if (rangeStr.Contains(".."))
                {
                    var parts = rangeStr.Split(new[] { ".." }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lo);
                        double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out hi);
                    }
                }
                else if (rangeStr.Contains("-"))
                {
                    var parts = rangeStr.Split('-');
                    if (parts.Length >= 2)
                    {
                        double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lo);
                        double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out hi);
                    }
                }

                ks.Bands.Add(new Band { From = lo, To = hi, Count = count, Weight = weight, Note = weight != null ? $"focus {weight:P0}" : "" });
            }

            // If a single percentage focus band was specified (e.g. Gain=0..0.25:70%), generate the remainder band automatically
            if (ks.Bands.Count == 1 && ks.Bands[0].Weight is double w && w < 1.0)
            {
                double hi = ks.Bands[0].To;
                if (hi < 1.0)
                {
                    double remWeight = 1.0 - w;
                    ks.Bands.Add(new Band { From = hi, To = 1.0, Weight = remWeight, Note = $"remainder {remWeight:P0}" });
                }
            }

            if (ks.Bands.Count > 0)
                spec.Knobs[knobName] = ks;
        }

        private static int WithCircuit(
            Args args, TextWriter stdout, TextWriter stderr, string circuit, string input,
            RenderSettings? initialSettings,
            Func<Renderer, KnobSet, Wave, string, int> body)
        {
            if (!File.Exists(circuit))
            {
                stderr.WriteLine($"error: no such circuit: {circuit}");
                return 1;
            }

            var renderSettings = initialSettings != null ? new RenderSettings
            {
                SampleRate = initialSettings.SampleRate,
                Oversample = initialSettings.Oversample,
                Iterations = initialSettings.Iterations,
                Normalize = initialSettings.Normalize,
                WarmupSeconds = initialSettings.WarmupSeconds,
                Speaker = initialSettings.Speaker
            } : new RenderSettings();

            try
            {
                if (args.Has("rate")) renderSettings.SampleRate = args.GetInt("rate", renderSettings.SampleRate);
                if (args.Has("oversample")) renderSettings.Oversample = args.GetInt("oversample", renderSettings.Oversample);
                if (args.Has("iterations")) renderSettings.Iterations = args.GetInt("iterations", renderSettings.Iterations);
                if (args.Has("normalize")) renderSettings.Normalize = args.GetDouble("normalize", renderSettings.Normalize);
                if (args.Has("warmup")) renderSettings.WarmupSeconds = args.GetDouble("warmup", renderSettings.WarmupSeconds);
                if (args.Has("speaker")) renderSettings.Speaker = args.Get("speaker", renderSettings.Speaker ?? "");
                renderSettings.Validate();
            }
            catch (InvalidDataException ex)
            {
                stderr.WriteLine("error: " + ex.Message);
                return 1;
            }

            KnobSet knobs;
            try
            {
                var log = new ConsoleLog { Verbosity = MessageType.Error };
                var built = Schematic.Load(circuit, log).Build();
                built.Name = Path.GetFileNameWithoutExtension(circuit);
                knobs = new KnobSet(built);
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"error: could not load {circuit}");
                stderr.WriteLine("       " + ex.Message);
                return 1;
            }

            stdout.WriteLine("circuit : {0}", Path.GetFileName(circuit));
            stdout.WriteLine("controls: {0}", knobs.Count == 0 ? "(none)" : knobs.DescribeOneLine());

            string inputFile = !string.IsNullOrEmpty(input) ? input : args.Get("input", "");
            if (string.IsNullOrEmpty(inputFile))
            {
                stderr.WriteLine("error: no input; set \"input\" in the spec or pass --input");
                return 1;
            }

            if (!File.Exists(inputFile))
            {
                stderr.WriteLine($"error: input WAV file not found: {inputFile}");
                return 1;
            }

            Wave wave;
            try
            {
                wave = Wave.Read(inputFile);
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"error: could not read {inputFile}");
                stderr.WriteLine("       " + ex.Message);
                return 1;
            }

            stdout.WriteLine("input   : {0}", wave.Describe());

            if (wave.SampleRate != renderSettings.SampleRate)
            {
                stdout.WriteLine("resample: {0} Hz -> {1} Hz", wave.SampleRate, renderSettings.SampleRate);
                wave = wave.Resample(renderSettings.SampleRate);
                stdout.WriteLine("resampled: {0}", wave.Describe());
            }

            if (renderSettings.Normalize > 0.0)
            {
                double peak = wave.Analyze().Peak;
                if (peak > 0.0)
                {
                    double gain = renderSettings.Normalize / peak;
                    for (int i = 0; i < wave.Length; ++i) wave.Samples[i] *= gain;
                    stdout.WriteLine("normalize: peak {0:F5} -> {1:F5} (gain {2:F4})", peak, renderSettings.Normalize, gain);
                }
            }

            Renderer renderer;
            try
            {
                renderer = new Renderer(circuit, wave, inputFile, renderSettings);
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"error: could not prepare {circuit}");
                stderr.WriteLine("       " + ex.Message);
                return 1;
            }

            stdout.WriteLine("rate    : {0}", renderSettings);
            stdout.WriteLine();

            try
            {
                return body(renderer, knobs, wave, inputFile);
            }
            catch (SpecError ex)
            {
                stderr.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

        private static void ReportPlan(
            Spec spec, SamplingPlan plan, Renderer renderer, Wave wave, int rate,
            TextWriter stdout, bool verbose)
        {
            stdout.WriteLine("plan    : {0} renders, {1} joint, seed {2}",
                plan.Jobs.Count, plan.Joint, spec.Seed);
            stdout.WriteLine("render  : {0:F1}s each, {1:F1} min of audio total",
                spec.ExcerptSeconds, plan.Jobs.Count * spec.ExcerptSeconds / 60.0);

            foreach (string note in plan.Notes) stdout.WriteLine("note    : {0}", note);

            foreach (string k in plan.UnspecifiedKnobs)
                stdout.WriteLine("warn    : {0} was not in the spec; automatically sampled", k);

            stdout.WriteLine();

            foreach (string control in plan.KnobOrder)
            {
                var got = new List<(int band, long count)>();

                foreach (var kv in plan.BandCounts)
                {
                    if (kv.Key.StartsWith(control + "[", StringComparison.OrdinalIgnoreCase))
                    {
                        string inside = kv.Key.Substring(control.Length + 1).TrimEnd(']');
                        int index = inside == "sweep" ? -1 : int.Parse(inside, CultureInfo.InvariantCulture);
                        got.Add((index, kv.Value));
                    }
                }

                if (got.Count == 0) continue;

                got.Sort((a, b) => a.band.CompareTo(b.band));
                var ks = spec.Knobs.TryGetValue(control, out var found) ? found : null;
                var ctrlInfo = renderer.Knobs[control];

                foreach (var (index, count) in got)
                {
                    string range = "?";
                    string note = "";

                    if (index < 0)
                    {
                        range = "sweep";
                    }
                    else if (ks != null && index < ks.Bands.Count)
                    {
                        var b = ks.Bands[index];
                        if (b.Position != null)
                            range = $"pos={b.Position.Value}";
                        else
                            range = string.Format(CultureInfo.InvariantCulture, "[{0:0.###},{1:0.###})", b.From, b.To);

                        note = !string.IsNullOrEmpty(b.Note) ? "  " + b.Note : "";
                    }

                    double pct = plan.Jobs.Count > 0 ? 100.0 * count / plan.Jobs.Count : 0.0;
                    string kindTag = ctrlInfo.IsDiscrete ? "[switch]" : "[knob]";

                    stdout.WriteLine("  {0,-16} {1,-8} {2,-16} {3,6}  {4,5:F1}%{5}",
                        control, kindTag, range, count, pct, note);
                }
            }

            if (verbose)
            {
                stdout.WriteLine();
                stdout.WriteLine("first 12 renders:");
                for (int i = 0; i < Math.Min(12, plan.Jobs.Count); ++i)
                {
                    RenderJob j = plan.Jobs[i];
                    string knobs = string.Join(" ", renderer.KnobOrder.Select(k =>
                        string.Format(CultureInfo.InvariantCulture, "{0}={1:0.###}", k, j.Knobs[k])));
                    stdout.WriteLine("  {0,2}  t{1,7:F2}s  gain {2,6:F3}  {3}  {4}",
                        i + 1, j.Offset, j.InputGain, j.Origin, knobs);
                }
            }

            stdout.WriteLine();
        }

        private static int RunJobs(
            Args args, TextWriter stdout, TextWriter stderr, Renderer renderer,
            SamplingPlan plan, string outDir, Wave wave, Spec spec, double perRender, int numThreads)
        {
            string renderDir = Path.Combine(outDir, "renders");
            string inputDir = Path.Combine(outDir, "input");
            string manifestPath = Path.Combine(outDir, "manifest.jsonl");

            Directory.CreateDirectory(renderDir);
            Directory.CreateDirectory(inputDir);

            string inputCopy = Path.Combine(inputDir, "normalized.wav");
            Wave.WriteMono(inputCopy, renderer.InputSamples, renderer.Settings.SampleRate, 32);
            stdout.WriteLine("input   : written to {0}", Path.GetRelativePath(outDir, inputCopy));
            stdout.WriteLine("dataset : {0}", Path.GetFullPath(outDir));
            stdout.WriteLine("threads : {0} worker(s)", numThreads);
            int rate = renderer.Settings.SampleRate;

            try
            {
                string fullOut = Path.GetFullPath(outDir);
                string? root = Path.GetPathRoot(fullOut);
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    long bytesPerRender = (long)(spec.ExcerptSeconds * rate * 4);
                    long estBytes = (long)plan.Jobs.Count * bytesPerRender;
                    double estGb = estBytes / 1_000_000_000.0;
                    double freeGb = drive.AvailableFreeSpace / 1_000_000_000.0;
                    stdout.WriteLine("storage : {0:F2} GB estimated for {1} renders, {2:F2} GB free on {3}",
                        estGb, plan.Jobs.Count, freeGb, drive.Name);
                    if (drive.AvailableFreeSpace < estBytes)
                    {
                        stdout.WriteLine("warn    : WARNING: Insufficient free space! Only {0:F2} GB free, need ~{1:F2} GB.", freeGb, estGb);
                    }
                }
            }
            catch { }
            stdout.WriteLine();

            bool resume = !args.Has("resume") || args.Flag("resume");
            Manifest.Contents? done = null;

            if (File.Exists(manifestPath))
            {
                if (!resume)
                {
                    stdout.WriteLine("note    : --resume false, so an existing manifest is left alone");
                }
                else
                {
                    try
                    {
                        done = Manifest.Read(manifestPath, renderer, renderer.Settings, stderr);
                    }
                    catch (InvalidDataException ex)
                    {
                        stderr.WriteLine("error: " + ex.Message);
                        return 1;
                    }

                    stdout.WriteLine("resume  : {0} renders already recorded ({1} ok, {2} failed)",
                        done.Rows, done.Ok, done.Failed);
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (done != null) seen.UnionWith(done.Done);

            int limit = args.GetInt("limit", 0);

            var jobsToRun = new List<RenderJob>();
            int skipped = 0;

            foreach (RenderJob job in plan.Jobs)
            {
                job.Seconds = spec.ExcerptSeconds;
                job.Id = MakeId(renderer, job);

                if (seen.Contains(job.Id))
                {
                    ++skipped;
                    continue;
                }

                if (limit > 0 && jobsToRun.Count + skipped >= limit)
                    break;

                jobsToRun.Add(job);
            }

            int totalToRun = jobsToRun.Count;
            int rendered = 0, failed = 0;
            long totalAudioFrames = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            object manifestLock = new object();
            object consoleLock = new object();

            using (var manifest = new StreamWriter(manifestPath, append: true))
            {
                if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length == 0)
                    manifest.WriteLine(Manifest.Header(renderer, renderer.Settings, spec.ExcerptSeconds, spec.Hash));
                manifest.Flush();

                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, numThreads)
                };

                Parallel.ForEach(
                    jobsToRun,
                    parallelOptions,
                    () => new Renderer(renderer.CircuitFile, wave, inputCopy, spec.Render),
                    (job, loopState, threadRenderer) =>
                    {
                        Wave.Measure measure;
                        string? error;
                        double[]? samples = threadRenderer.RenderOne(job, out measure, out error);
                        bool ok = samples != null;

                        string path = Path.Combine(renderDir, job.FileName);

                        if (ok && samples != null)
                        {
                            Wave.WriteMono(path, samples, rate, 32);
                            Interlocked.Add(ref totalAudioFrames, samples.Length);
                        }
                        else
                        {
                            Interlocked.Increment(ref failed);
                            if (File.Exists(path)) File.Delete(path);
                        }

                        string rowJson = Manifest.Row(threadRenderer, job, ok, measure, error);
                        lock (manifestLock)
                        {
                            manifest.WriteLine(rowJson);
                            manifest.Flush();
                            seen.Add(job.Id);
                        }

                        int currentRendered = Interlocked.Increment(ref rendered);

                        if (!args.Flag("quiet"))
                        {
                            double elapsedSec = clock.Elapsed.TotalSeconds;
                            double jobsPerSec = currentRendered / Math.Max(0.001, elapsedSec);
                            double left = totalToRun - currentRendered;
                            double etaSec = left / Math.Max(0.001, jobsPerSec);
                            double rtFactor = (currentRendered * spec.ExcerptSeconds) / Math.Max(0.001, elapsedSec);

                            string etaFormatted;
                            if (etaSec >= 3600)
                                etaFormatted = $"{(int)(etaSec / 3600)}h {(int)((etaSec % 3600) / 60):D2}m";
                            else if (etaSec >= 60)
                                etaFormatted = $"{(int)(etaSec / 60)}m {(int)(etaSec % 60):D2}s";
                            else
                                etaFormatted = $"{etaSec:F0}s";

                            lock (consoleLock)
                            {
                                stdout.WriteLine("[{0,5}/{1}] {2,-32} {3,-12} {4,7} left ({5:F1}x RT)  {6}",
                                    currentRendered + skipped, plan.Jobs.Count, job.Id, job.Origin, etaFormatted,
                                    rtFactor,
                                    ok ? measure.ToString() : error);
                                stdout.Flush();
                            }
                        }

                        return threadRenderer;
                    },
                    threadRenderer => { }
                );
            }

            double totalMinutes = clock.Elapsed.TotalMinutes;
            double totalAudioMinutes = (totalAudioFrames / (double)rate) / 60.0;
            double avgJobsPerSec = rendered / Math.Max(0.001, clock.Elapsed.TotalSeconds);
            double avgRt = (totalAudioFrames / (double)rate) / Math.Max(0.001, clock.Elapsed.TotalSeconds);

            stdout.WriteLine();
            stdout.WriteLine("=== Render Complete ===");
            stdout.WriteLine("rendered: {0} ok, {1} skipped, {2} failed in {3:F2} min", rendered - failed, skipped, failed, totalMinutes);
            stdout.WriteLine("audio   : {0:F2} hours total ({1:F1} min of output)", totalAudioMinutes / 60.0, totalAudioMinutes);
            stdout.WriteLine("speed   : {0:F1} jobs/sec average ({1:F1}x real-time)", avgJobsPerSec, avgRt);
            stdout.WriteLine("dataset : {0}", Path.GetFullPath(outDir));

            if (failed > 0)
                stdout.WriteLine("note    : {0} render(s) failed. `livespice-gen verify {1}` lists them.", failed, outDir);

            return failed == 0 ? 0 : 1;
        }

        public static string MakeId(Renderer renderer, RenderJob job)
        {
            var sb = new StringBuilder();
            sb.Append(renderer.CircuitName);

            foreach (string k in renderer.KnobOrder)
            {
                char c = char.IsLetter(k[0]) ? k[0] : 'X';
                double val = job.Knobs[k];
                var ctrl = renderer.Knobs[k];

                if (ctrl.IsDiscrete)
                {
                    sb.Append('_').Append(char.ToUpperInvariant(c))
                      .Append(((int)Math.Round(val)).ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append('_').Append(char.ToUpperInvariant(c))
                      .Append(((int)Math.Round(val * 100)).ToString("D2", CultureInfo.InvariantCulture));
                }
            }

            sb.Append("_t")
              .Append(job.Offset.ToString("F2", CultureInfo.InvariantCulture))
              .Append('-')
              .Append((job.Offset + job.Seconds).ToString("F2", CultureInfo.InvariantCulture))
              .Append('s');

            return sb.ToString();
        }

        private static string ResolveCircuit(string path, string specPath)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (Path.IsPathRooted(path)) return path;

            if (!string.IsNullOrEmpty(specPath))
            {
                string specDir = Path.GetDirectoryName(Path.GetFullPath(specPath))!;
                string candidate = Path.Combine(specDir, path);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }

            if (File.Exists(path)) return Path.GetFullPath(path);

            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string fileName = Path.GetFileName(path);
            string[] searchRoots = new[]
            {
                Path.Combine(appDir, "..", "..", "..", "..", "third_party", "LiveSPICE"),
                Path.Combine(appDir, "..", "..", "..", "..", "..", "LiveSPICE"),
                Path.Combine(Environment.CurrentDirectory, "third_party", "LiveSPICE"),
                Path.Combine(Environment.CurrentDirectory, "..", "LiveSPICE"),
                Path.Combine(Environment.CurrentDirectory, "..", "..", "LiveSPICE")
            };

            foreach (string root in searchRoots)
            {
                if (Directory.Exists(root))
                {
                    string candidate = Path.Combine(root, fileName);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);

                    candidate = Path.Combine(root, "Tests", "Examples", fileName);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }

            if (!string.IsNullOrEmpty(specPath))
            {
                string specDir = Path.GetDirectoryName(Path.GetFullPath(specPath))!;
                return Path.Combine(specDir, path);
            }

            return path;
        }

        private static string ResolveInput(string path, string specPath)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (Path.IsPathRooted(path)) return path;

            if (File.Exists(path)) return Path.GetFullPath(path);

            if (!string.IsNullOrEmpty(specPath))
            {
                string specDir = Path.GetDirectoryName(Path.GetFullPath(specPath))!;
                string candidate = Path.Combine(specDir, path);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }

            return path;
        }

        private static string[] KnobsFromHeader(string header)
        {
            if (header == null) return Array.Empty<string>();

            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(header))
                {
                    if (!doc.RootElement.TryGetProperty("knobs", out var k) ||
                        k.ValueKind != System.Text.Json.JsonValueKind.Array)
                        return Array.Empty<string>();

                    return k.EnumerateArray()
                        .Where(x => x.ValueKind == System.Text.Json.JsonValueKind.String)
                        .Select(x => x.GetString()!)
                        .Where(x => x != null)
                        .ToArray();
                }
            }
            catch (System.Text.Json.JsonException)
            {
                return Array.Empty<string>();
            }
        }

        private static string? Str(JsonElement e, string field)
        {
            return e.TryGetProperty(field, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;
        }

        private static double Num(JsonElement e, string field)
        {
            if (!e.TryGetProperty(field, out var v)) return 0.0;

            return v.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number => v.GetDouble(),
                System.Text.Json.JsonValueKind.True => 1.0,
                _ => 0.0,
            };
        }
    }
}
