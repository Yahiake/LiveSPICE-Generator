// File: Cli.cs
// Command line front end.
//
// Hand-rolled rather than built on System.CommandLine. The version LiveSPICE's own test
// harness uses is a 2021 beta of a package that has since changed shape twice; depending
// on it in something meant to be published and cloned is a bad trade for an argument
// parser that only needs to handle a dozen options and subcommands.

using System;
using System.Collections.Generic;
using System.IO;

namespace LiveSpiceGen
{
    /// <summary>Parsed command line: a verb, its options, and its positional arguments.</summary>
    public sealed class Args
    {
        public string Verb = "";
        public readonly Dictionary<string, string> Options =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<string>> MultiOptions =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Positional = new List<string>();

        public bool Flag(string name) => Has(name) && IsTrue(Options[name]);

        public bool Has(string name) => Options.ContainsKey(name);

        public string Get(string name, string fallback)
        {
            return Options.TryGetValue(name, out string? v) && v != null && v.Length > 0 ? v : fallback;
        }

        public List<string> GetAll(string name)
        {
            return MultiOptions.TryGetValue(name, out var list) ? list : new List<string>();
        }

        public int GetInt(string name, int fallback)
        {
            return int.TryParse(Get(name, ""), out int v) ? v : fallback;
        }

        public double GetDouble(string name, double fallback)
        {
            return double.TryParse(Get(name, ""), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out double v)
                ? v : fallback;
        }

        public string Require(int index, string what)
        {
            if (index >= Positional.Count)
                throw new ArgumentException($"missing argument: {what}");

            return Positional[index];
        }

        private static bool IsTrue(string v) =>
            v.Length == 0 ||
            v.Equals("1", StringComparison.Ordinal) ||
            v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    public static class Cli
    {
        private static readonly string[] Help =
        {
            "livespice-gen - render audio through analog circuit simulation to build training datasets",
            "",
            "usage: livespice-gen <command> [options]",
            "",
            "commands:",
            "  controls <circuit.schx>                  list pots, switches, inputs, and speakers in a circuit",
            "  knobs <circuit.schx>                     alias for 'controls'",
            "  spec <circuit.schx> [out.spec.json]      generate a customized spec template for a circuit",
            "  plan <spec.json | circuit.schx in.wav>   show the sampling plan and runtime estimate",
            "  process <circuit.schx> <in.wav> [out.wav] render full audio through circuit (NAM/reamp mode)",
            "  render <spec.json [outdir] | circuit.schx in.wav outdir> [options]",
            "                                           render audio dataset through analog simulation",
            "  verify <dataset_dir>                     verify dataset integrity, audio quality and files",
            "",
            "options for render and plan:",
            "  --budget <n>         total number of renders (e.g. 2500)",
            "  --threads, -j <n>    worker threads for parallel rendering (default: CPU count)",
            "  --duration <s>       duration per excerpt in seconds (default: 15.0)",
            "  --warmup <s>         pre-roll warmup seconds to settle DC biases (default: 0.1)",
            "  --rate <hz>          simulation sample rate (default: 48000)",
            "  --oversample <n>     oversampling factor (default: 2)",
            "  --iterations <n>     Newton-Raphson iterations per sample (default: 8)",
            "  --normalize <peak>   normalize input peak (default: 0.9, 0 to disable)",
            "  --gain <db>          fixed drive level in dB (sets both gain-low and gain-high)",
            "  --gain-low <db>      minimum random drive level in dB (default: -18.0)",
            "  --gain-high <db>     maximum random drive level in dB (default: 6.0)",
            "  --time-range <s>     excerpt time window inside input audio, e.g. 0..60",
            "  --speaker <name>     speaker/output component name in schematic",
            "  --seed <n>           random seed for reproducible dataset generation",
            "  --joint <mode>       joint mode: independent | grid | sweep | sweepRandom",
            "  --knob <spec>        custom knob bands, e.g. Volume=0.0..0.2:1000,0.2..1.0:1500 or Gain=0..0.25:70%",
            "  --focus <spec>       focus sampling on a knob range, e.g. Gain=0..0.25 (70% focus) or Gain=0..0.25:80%",
            "  --anchors            include extreme boundary corners (min, noon, max, alternating)",
            "  --switch <spec>      custom switch positions, e.g. Bright=0,1 or Mode=0:500,1:1500",
            "  --spans <mode>       input excerpt spans: signal (skip silence) | all",
            "  --resume <bool>      resume interrupted rendering (default: true)",
            "  --limit <n>          stop after rendering n items (useful for testing)",
            "  --dry-run            plan only, do not render audio",
            "",
            "Run 'livespice-gen <command> --help' for command-specific options.",
        };

        public static int Run(string[] argv, TextWriter stdout, TextWriter stderr)
        {
            var args = Parse(argv, stderr);

            if (args == null)
                return 2;

            if (args.Verb == "" || args.Verb == "help" || args.Flag("help"))
            {
                foreach (var l in Help) stdout.WriteLine(l);
                return (args.Verb == "" && !args.Flag("help")) ? 2 : 0;
            }

            switch (args.Verb.ToLowerInvariant())
            {
                case "knobs":
                case "controls": return Commands.Knobs(args, stdout, stderr);
                case "spec":
                case "template": return Commands.GenerateSpec(args, stdout, stderr);
                case "plan": return Commands.Plan(args, stdout, stderr);
                case "process":
                case "run":
                case "reamp": return Commands.Process(args, stdout, stderr);
                case "render": return Commands.Render(args, stdout, stderr);
                case "verify": return Commands.Verify(args, stdout, stderr);
                default:
                    stderr.WriteLine($"error: unknown command '{args.Verb}'");
                    foreach (var l in Help) stderr.WriteLine(l);
                    return 2;
            }
        }

        private static readonly HashSet<string> BooleanFlags =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "help", "resume", "quiet", "verbose", "dry-run", "json", "anchors" };

        private static bool IsOption(string s)
        {
            if (s.Length <= 1 || !s.StartsWith("-", StringComparison.Ordinal)) return false;
            if (s == "--") return true;
            // Negative numbers like -18 or -18.5 are values, not option names
            if (char.IsDigit(s[1])) return false;
            if (s.StartsWith("--", StringComparison.Ordinal) && s.Length > 2 && char.IsDigit(s[2])) return false;
            return true;
        }

        private static Args Parse(string[] argv, TextWriter stderr)
        {
            var a = new Args();
            var positionalOnly = false;

            for (int i = 0; i < argv.Length; ++i)
            {
                string s = argv[i];

                if (positionalOnly || s.Length == 0 || !IsOption(s))
                {
                    if (a.Verb.Length == 0) a.Verb = s;
                    else a.Positional.Add(s);

                    continue;
                }

                if (s == "--")
                {
                    positionalOnly = true;
                    continue;
                }

                string name = s.TrimStart('-');
                string? value = null;

                int eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    value = name.Substring(eq + 1);
                    name = name.Substring(0, eq);
                }
                else if (i + 1 < argv.Length
                         && !IsOption(argv[i + 1])
                         && !BooleanFlags.Contains(name))
                {
                    value = argv[++i];
                }

                string finalVal = value ?? "";
                a.Options[name] = finalVal;
                if (!a.MultiOptions.TryGetValue(name, out var list))
                {
                    list = new List<string>();
                    a.MultiOptions[name] = list;
                }
                list.Add(finalVal);
            }

            return a;
        }
    }
}