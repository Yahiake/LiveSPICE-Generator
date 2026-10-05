// File: KnobSet.cs
// Interactive controls discovery and parameter application.
//
// Discovers both continuous controls (Potentiometer, VariableResistor) and discrete
// controls (SinglePoleSwitch, SPDT, SP3T, SP4T, SP5T, Switch), groups multi-gang
// sections, and applies values to the circuit simulation.

using System;
using System.Collections.Generic;
using System.Linq;
using Circuit;

namespace LiveSpiceGen
{
    public enum ControlKind
    {
        Potentiometer,
        Switch,
    }

    /// <summary>
    /// Metadata about an interactive control in a circuit.
    /// </summary>
    public sealed class ControlInfo
    {
        public string Name { get; }
        public ControlKind Kind { get; }
        public int NumPositions { get; } // 0 for pot, >= 2 for switch
        public List<Component> Components { get; } = new List<Component>();

        public bool IsDiscrete => Kind == ControlKind.Switch;
        public bool IsContinuous => Kind == ControlKind.Potentiometer;

        public string Resistance { get; set; } = "";
        public string Sweep { get; set; } = "";

        public ControlInfo(string name, ControlKind kind, int numPositions = 0)
        {
            Name = name;
            Kind = kind;
            NumPositions = numPositions;
        }

        public string Sections => string.Join("+", Components.Select(p => p.Name.Trim()));
    }

    /// <summary>
    /// The logical interactive controls of a circuit: knobs and switches.
    ///
    /// Controls sharing a Group name are multi-gang sections (e.g. dual-gang pots or
    /// 4PDT pull switches) that move together physically. They count as one control.
    /// </summary>
    public class ControlSet
    {
        protected readonly List<string> order = new List<string>();
        protected readonly Dictionary<string, ControlInfo> controls =
            new Dictionary<string, ControlInfo>(StringComparer.OrdinalIgnoreCase);

        public ControlSet(Circuit.Circuit circuit)
        {
            foreach (Component c in circuit.Components)
            {
                if (c is IPotControl pot)
                {
                    string groupAttr = ((IGroupableComponent)pot).Group;
                    string key = string.IsNullOrWhiteSpace(groupAttr) ? c.Name.Trim() : groupAttr.Trim();

                    if (!controls.TryGetValue(key, out ControlInfo? info))
                    {
                        info = new ControlInfo(key, ControlKind.Potentiometer);
                        controls[key] = info;
                        order.Add(key);
                    }

                    info.Components.Add(c);

                    if (c is Potentiometer p)
                    {
                        info.Resistance = p.Resistance.ToString();
                        info.Sweep = p.Sweep.ToString();
                    }
                    else if (c is VariableResistor vr)
                    {
                        info.Resistance = vr.Resistance.ToString();
                        info.Sweep = vr.Sweep.ToString();
                    }
                }
                else if (c is IButtonControl button)
                {
                    string groupAttr = ((IGroupableComponent)button).Group;
                    string key = string.IsNullOrWhiteSpace(groupAttr) ? c.Name.Trim() : groupAttr.Trim();
                    int numPositions = Math.Max(2, button.NumPositions);

                    if (!controls.TryGetValue(key, out ControlInfo? info))
                    {
                        info = new ControlInfo(key, ControlKind.Switch, numPositions);
                        controls[key] = info;
                        order.Add(key);
                    }

                    info.Components.Add(c);
                }
#pragma warning disable CS0612 // Circuit.Switch is obsolete
                else if (c is Switch sw)
                {
                    string key = c.Name.Trim();
                    if (!controls.TryGetValue(key, out ControlInfo? info))
                    {
                        info = new ControlInfo(key, ControlKind.Switch, 2);
                        controls[key] = info;
                        order.Add(key);
                    }

                    info.Components.Add(c);
                }
#pragma warning restore CS0612
            }
        }

        /// <summary>All control names in circuit order.</summary>
        public IReadOnlyList<string> Names => order;

        public int Count => order.Count;

        public IReadOnlyList<ControlInfo> Controls => order.Select(k => controls[k]).ToList();

        public ControlInfo this[string name] => controls[name];

        public bool Contains(string name) => controls.ContainsKey(name);

        public string Sections(string controlName)
        {
            return controls.TryGetValue(controlName, out var info) ? info.Sections : controlName;
        }

        /// <summary>
        /// Writes control positions to the circuit components.
        /// Continuous knobs expect 0..1; discrete switches expect position indices 0..N-1.
        /// </summary>
        public void Apply(IReadOnlyDictionary<string, double> values)
        {
            foreach (string k in order)
            {
                if (!values.TryGetValue(k, out double v))
                    continue;

                ControlInfo info = controls[k];
                if (info.IsContinuous)
                {
                    foreach (Component comp in info.Components)
                    {
                        if (comp is IPotControl pot)
                            pot.PotValue = Math.Clamp(v, 0.0, 1.0);
                    }
                }
                else
                {
                    int pos = (int)Math.Round(v);
                    if (pos < 0) pos = 0;
                    if (pos >= info.NumPositions && info.NumPositions > 0)
                        pos = info.NumPositions - 1;

                    foreach (Component comp in info.Components)
                    {
                        if (comp is IButtonControl btn)
                            btn.Position = pos;
#pragma warning disable CS0612
                        else if (comp is Switch sw)
                            sw.Closed = (pos != 0);
#pragma warning restore CS0612
                    }
                }
            }
        }

        /// <summary>Every control at nominal position (0.5 for pots, 0 for switches).</summary>
        public Dictionary<string, double> Uniform(double potValue = 0.5)
        {
            var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in order)
            {
                dict[k] = controls[k].IsContinuous ? potValue : 0.0;
            }
            return dict;
        }

        /// <summary>Descriptive lines for CLI and logs.</summary>
        public IEnumerable<string> Describe()
        {
            foreach (string k in order)
            {
                ControlInfo info = controls[k];
                int sections = info.Components.Count;
                string secStr = sections > 1 ? $" x{sections}" : "";

                if (info.IsContinuous)
                {
                    yield return string.Format(
                        "{0} [knob] ({1}{2}, {3} {4}, 0 = min of travel)",
                        k,
                        info.Sections,
                        secStr,
                        info.Resistance,
                        info.Sweep);
                }
                else
                {
                    yield return string.Format(
                        "{0} [switch] ({1}{2}, {3} positions: [{4}])",
                        k,
                        info.Sections,
                        secStr,
                        info.NumPositions,
                        string.Join(", ", Enumerable.Range(0, info.NumPositions)));
                }
            }
        }

        public string DescribeOneLine() => string.Join(", ", Describe());
    }

    /// <summary>
    /// Alias for backwards compatibility with existing codebase.
    /// </summary>
    public sealed class KnobSet : ControlSet
    {
        public KnobSet(Circuit.Circuit circuit) : base(circuit) { }
    }
}
