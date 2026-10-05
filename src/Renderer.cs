// File: Renderer.cs
// Runs the circuit.
//
// The simulation core here is ported from LiveSPICE's test harness (Tests/Render.cs, MIT,
// Copyright (c) 2013 Dillon Sharlet - see LICENSE) and is deliberately close to the
// original line for line. What was changed is the shape around it: knobs arrive as a
// KnobSet instead of a private list, render settings are an object rather than eight
// constructor arguments, and where knob positions come from is no longer decided here.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Circuit;
using ComputerAlgebra;
using Util;

namespace LiveSpiceGen
{
    /// <summary>Simulation settings that affect the numbers, not the sampling.</summary>
    public sealed class RenderSettings
    {
        public int SampleRate = 48000;
        public int Oversample = 2;
        public int Iterations = 8;

        /// <summary>Peak the driving input is scaled to before use, or 0 to leave it alone.</summary>
        public double Normalize = 0.9;

        /// <summary>Seconds of audio fed through simulation before recording each excerpt, to settle DC transients.</summary>
        public double WarmupSeconds = 0.05;

        /// <summary>Optional specific speaker name to probe. When null, all speakers in the circuit are summed.</summary>
        public string? Speaker = null;

        public void Validate()
        {
            if (SampleRate < 8000 || SampleRate > 384000)
                throw new InvalidDataException($"sample rate {SampleRate} Hz is out of range");

            if (Oversample < 1 || Oversample > 64)
                throw new InvalidDataException($"oversample {Oversample} is out of range (1..64)");

            if (Iterations < 1 || Iterations > 64)
                throw new InvalidDataException($"iterations {Iterations} is out of range (1..64)");

            if (Normalize < 0.0 || Normalize > 1.0)
                throw new InvalidDataException($"normalize {Normalize} is out of range (0..1, or 0 to disable)");

            if (WarmupSeconds < 0.0 || WarmupSeconds > 5.0)
                throw new InvalidDataException($"warmup {WarmupSeconds}s is out of range (0..5s)");
        }

        public override string ToString()
        {
            return string.Format("{0} Hz, oversample {1} ({2} Hz internal), iterations {3}{4}{5}",
                SampleRate, Oversample, SampleRate * Oversample, Iterations,
                WarmupSeconds > 0 ? $", warmup {WarmupSeconds:F3}s" : "",
                !string.IsNullOrEmpty(Speaker) ? $", speaker {Speaker}" : "");
        }
    }

    public sealed class Renderer
    {
        private readonly Circuit.Circuit circuit;
        private readonly Wave input;
        private readonly RenderSettings settings;
        private readonly Expression inputExpression;
        private readonly Expression outputExpression;

        public KnobSet Knobs { get; }

        /// <summary>Knob names in circuit order, for filename construction.</summary>
        public IReadOnlyList<string> KnobOrder { get { return Knobs.Names; } }

        public RenderSettings Settings { get { return settings; } }

        /// <summary>
        /// The driving samples, already resampled and normalised.
        ///
        /// Exposed so the input written beside a dataset is the exact array that was
        /// simulated, rather than a second pass over the file that might differ.
        /// </summary>
        public double[] InputSamples { get { return input.Samples; } }

        public string CircuitFile { get; }
        public string CircuitHash { get; }
        public string InputFile { get; }
        public string InputHash { get; }

        /// <summary>Circuit name with the spaces taken out, "Big Muff Pi" -> "BigMuffPi".</summary>
        public string CircuitName { get; }

        /// <summary>
        /// Spans of the input that carry signal, as [start,end] seconds. Sampler places
        /// excerpts inside these unless told otherwise, which is most of the difference
        /// between rendering music and rendering the runways either side of it.
        /// </summary>
        public List<double[]> UsableSpans { get; }

        /// <summary>Seconds of the input that are too quiet to place an excerpt in.</summary>
        public double SilentSeconds { get; }

        public double InputSeconds { get { return input.Seconds; } }

        /// <summary>
        /// The level the circuit's input source is scaled by, which is what decides what
        /// one volt means. Recorded in the manifest because it is the difference between
        /// "the model matches the recording" and "the model matches the circuit at the
        /// voltage the recording was made at".
        /// </summary>
        public double InputV0dBFS
        {
            get { return (double)((Input)circuit.Components.OfType<Input>().Single()).V0dBFS; }
        }

        public Renderer(string circuitFile, Wave input, string inputFile, RenderSettings settings)
        {
            CircuitFile = circuitFile;
            this.input = input;
            InputFile = inputFile;
            this.settings = settings;

            CircuitHash = Sha256(circuitFile);
            InputHash = Sha256(inputFile);
            CircuitName = Slug(Path.GetFileNameWithoutExtension(circuitFile));

            var log = new ConsoleLog { Verbosity = MessageType.Error };
            circuit = Schematic.Load(circuitFile, log).Build();
            circuit.Name = Path.GetFileNameWithoutExtension(circuitFile);

            Knobs = new KnobSet(circuit);

            var m = input.Analyze();
            UsableSpans = FindUsableSpans(input.Samples, settings.SampleRate, m.Peak);
            SilentSeconds = input.Seconds;
            foreach (double[] s in UsableSpans)
                SilentSeconds -= s[1] - s[0];

            var source = circuit.Components.OfType<Input>().SingleOrDefault();
            if (source == null)
                throw new InvalidDataException("circuit has no Input component");
            inputExpression = source.In;

            var allSpeakers = circuit.Components.OfType<Speaker>().ToList();
            if (allSpeakers.Count == 0)
                throw new InvalidDataException("circuit has no Speaker component");

            if (!string.IsNullOrWhiteSpace(settings.Speaker))
            {
                var target = allSpeakers.FirstOrDefault(s => string.Equals(s.Name.Trim(), settings.Speaker.Trim(), StringComparison.OrdinalIgnoreCase));
                if (target == null)
                    throw new InvalidDataException($"speaker '{settings.Speaker}' not found. Available speakers: [{string.Join(", ", allSpeakers.Select(s => s.Name.Trim()))}]");
                outputExpression = target.Out;
            }
            else
            {
                Expression sum = 0;
                foreach (Speaker s in allSpeakers) sum += s.Out;
                outputExpression = sum;
            }
        }

        /// <summary>
        /// Render one job: set the controls, simulate the excerpt, return the samples.
        ///
        /// Returns null and sets <paramref name="error"/> if the circuit could not be
        /// solved at this control position, which for a nonlinear circuit is a normal
        /// occurrence at the extremes of travel rather than a bug.
        /// </summary>
        public double[]? RenderOne(RenderJob job, out Wave.Measure measure, out string? error)
        {
            Knobs.Apply(job.Knobs);

            measure = new Wave.Measure();
            error = null;

            int sampleRate = settings.SampleRate;
            int start = (int)Math.Round(job.Offset * sampleRate);
            if (start < 0) start = 0;
            if (start > input.Length) start = input.Length;

            int count = (int)Math.Round(job.Seconds * sampleRate);
            if (count < 0) count = 0;
            if (start + count > input.Length) count = input.Length - start;

            if (count <= 0)
            {
                error = job.Offset + job.Seconds > input.Seconds + 1e-9
                    ? $"excerpt {job.Offset:F2}-{job.Offset + job.Seconds:F2}s runs past the end of the input ({input.Seconds:F2}s)"
                    : "empty excerpt";
                return null;
            }

            try
            {
                var analysis = circuit.Analyze();
                var solution = TransientSolution.Solve(analysis, (Real)1 / (sampleRate * settings.Oversample));

                var sim = new Simulation(solution)
                {
                    Oversample = settings.Oversample,
                    Iterations = settings.Iterations,
                    Input = new[] { inputExpression },
                    Output = new[] { outputExpression },
                };

                // Warmup pre-roll to settle DC biases and eliminate cold-start transient clicks
                int warmupCount = (int)Math.Round(settings.WarmupSeconds * sampleRate);
                if (warmupCount > 0)
                {
                    var warmupIn = new double[warmupCount];
                    var warmupOut = new double[warmupCount];
                    int preStart = start - warmupCount;
                    if (preStart >= 0)
                    {
                        Array.Copy(input.Samples, preStart, warmupIn, 0, warmupCount);
                    }
                    else
                    {
                        int available = Math.Max(0, start);
                        int pad = warmupCount - available;
                        if (available > 0)
                            Array.Copy(input.Samples, 0, warmupIn, pad, available);
                    }

                    if (job.InputGain != 1.0)
                        for (int i = 0; i < warmupCount; ++i)
                            warmupIn[i] *= job.InputGain;

                    sim.Run(warmupIn, warmupOut);
                }

                var output = new double[count];

                // In blocks, so a long excerpt does not need two more full-length buffers
                // beside the output one.
                const int Block = 1 << 18;
                var inBuf = new double[Math.Min(Block, count)];
                var outBuf = new double[inBuf.Length];
                int n = 0;

                while (n < count)
                {
                    int m = Math.Min(inBuf.Length, count - n);
                    if (m != inBuf.Length)
                    {
                        inBuf = new double[m];
                        outBuf = new double[m];
                    }

                    Array.Copy(input.Samples, start + n, inBuf, 0, m);

                    if (job.InputGain != 1.0)
                        for (int i = 0; i < m; ++i)
                            inBuf[i] *= job.InputGain;

                    sim.Run(inBuf, outBuf);
                    Array.Copy(outBuf, 0, output, n, m);
                    n += m;
                }

                measure = Wave.FromSamples(sampleRate, output).Analyze();
                return output;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Renders an entire audio waveform through the circuit at the currently applied control settings.
        /// Ideal for processing a complete track, re-amping, or creating a single NAM target WAV.
        /// </summary>
        public double[]? Process(
            double[] inputSamples,
            double inputGain,
            Action<double>? progress,
            out Wave.Measure measure,
            out string? error)
        {
            measure = new Wave.Measure();
            error = null;

            int sampleRate = settings.SampleRate;
            int count = inputSamples.Length;
            if (count == 0)
            {
                error = "input has 0 samples";
                return null;
            }

            try
            {
                var analysis = circuit.Analyze();
                var solution = TransientSolution.Solve(analysis, (Real)1 / (sampleRate * settings.Oversample));

                var sim = new Simulation(solution)
                {
                    Oversample = settings.Oversample,
                    Iterations = settings.Iterations,
                    Input = new[] { inputExpression },
                    Output = new[] { outputExpression },
                };

                // Warmup pre-roll to settle DC biases
                int warmupCount = (int)Math.Round(settings.WarmupSeconds * sampleRate);
                if (warmupCount > 0)
                {
                    var warmupIn = new double[warmupCount];
                    var warmupOut = new double[warmupCount];
                    int take = Math.Min(warmupCount, count);
                    Array.Copy(inputSamples, 0, warmupIn, warmupCount - take, take);

                    if (inputGain != 1.0)
                        for (int i = 0; i < warmupCount; ++i)
                            warmupIn[i] *= inputGain;

                    sim.Run(warmupIn, warmupOut);
                }

                var output = new double[count];
                const int Block = 1 << 16;
                var inBuf = new double[Math.Min(Block, count)];
                var outBuf = new double[inBuf.Length];
                int n = 0;

                while (n < count)
                {
                    int m = Math.Min(inBuf.Length, count - n);
                    if (m != inBuf.Length)
                    {
                        inBuf = new double[m];
                        outBuf = new double[m];
                    }

                    Array.Copy(inputSamples, n, inBuf, 0, m);

                    if (inputGain != 1.0)
                        for (int i = 0; i < m; ++i)
                            inBuf[i] *= inputGain;

                    sim.Run(inBuf, outBuf);
                    Array.Copy(outBuf, 0, output, n, m);
                    n += m;

                    progress?.Invoke((double)n / count);
                }

                measure = Wave.FromSamples(sampleRate, output).Analyze();
                return output;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Find the spans of the input that carry signal.
        ///
        /// Reamp files open and close with long digital-silence runways and carry
        /// calibrated silent windows for measuring latency and the noise floor. Rendering
        /// those costs exactly as much wall clock as rendering music and teaches the model
        /// nothing, so they are found once and offered to the sampler as a placement
        /// constraint.
        ///
        /// Windows are judged on short-term RMS, not sample peak: one stray non-zero sample
        /// in a runway of zeros would otherwise mark the whole runway usable. A dip shorter
        /// than MinGapSeconds is treated as part of the music, not as a hole in it.
        /// </summary>
        private static List<double[]> FindUsableSpans(double[] x, int sr, double peak)
        {
            const double MinGapSeconds = 0.15;

            int win = Math.Max(64, sr / 200);      // 5 ms
            int n = x.Length / win;

            // -80 dB below this file's own peak, with an absolute floor so a genuinely
            // quiet recording is not declared entirely silent.
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

        public static string Slug(string stem)
        {
            var sb = new StringBuilder();
            foreach (char c in stem ?? "")
                if (char.IsLetterOrDigit(c)) sb.Append(c);

            return sb.Length == 0 ? "circuit" : sb.ToString();
        }

        public static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// How long one second of output audio costs, in wall clock.
        ///
        /// Measured by rendering, not estimated from a constant. A circuit's cost depends
        /// on its nonlinearity and on how hard it is being driven, so a figure carried over
        /// from another circuit or another machine is worth very little: the estimate in
        /// the plan decides whether a six-hour job gets started, and being wrong there is
        /// expensive.
        ///
        /// One render is done to get the number. It is discarded, and it is not written to
        /// the dataset, because the sampler does not know about this probe.
        /// </summary>
        public double SecondsPerSecondOfAudio(RenderSettings s, double seconds, out string note)
        {
            note = "";

            var job = new RenderJob
            {
                Id = "probe",
                Knobs = Knobs.Uniform(0.5),
                Offset = 0.0,
                Seconds = seconds,
            };

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var measure = new Wave.Measure();
            double[]? samples = RenderOne(job, out measure, out string? error);
            clock.Stop();

            if (samples == null)
            {
                note = "probe render failed, so this estimate is a guess";
                return 0.05;
            }

            double audio = samples.Length / (double)s.SampleRate;
            return audio > 0.0 ? clock.Elapsed.TotalSeconds / audio : 0.05;
        }

        /// <summary>Knob positions as exact doubles, in circuit order.</summary>
        public string KnobsJson(IReadOnlyDictionary<string, double> knobs)
        {
            return Json.NumberMap(Knobs.Names, knobs);
        }

        public string KnobNamesJson()
        {
            return Json.Array(Knobs.Names);
        }
    }
}
