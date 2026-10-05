# LiveSPICE-Generator (`livespice-gen`)

A high-throughput, multi-threaded dataset generation engine and batch simulator for LiveSPICE circuit schematics (`.schx`). Built specifically for generating training datasets for deep neural audio models (RTNeural, Neural Amp Modeler / NAM, PyTorch GRU/LSTM/WaveNet) on analog hardware: guitar overdrive and distortion pedals, high-voltage vacuum tube preamplifiers, active tone stacks, and non-linear filter topologies.

---

## Table of Contents

1. [Why LiveSPICE-Generator?](#1-why-livespice-generator)
2. [End-to-End Workflow: From Schematic to VST3 Plugin](#2-end-to-end-workflow-from-schematic-to-vst3-plugin)
3. [Architecture & Circuit Simulation Engine](#3-architecture--circuit-simulation-engine)
4. [Component & Control Intelligence](#4-component--control-intelligence)
5. [Installation & Compilation](#5-installation--compilation)
6. [Command Line Interface (CLI) Reference](#6-command-line-interface-cli-reference)
7. [The Power-User Playbook & Advanced Techniques](#7-the-power-user-playbook--advanced-techniques)
8. [JSON Specification Schema](#8-json-specification-schema)
9. [Sampling Theory & Stratification Mechanics](#9-sampling-theory--stratification-mechanics)
10. [Analog Transient Settling & DC Bias Warmup](#10-analog-transient-settling--dc-bias-warmup)
11. [Dataset Structure & Manifest Format](#11-dataset-structure--manifest-format)
12. [Downstream Neural Network Training (PyTorch & RTNeural)](#12-downstream-neural-network-training-pytorch--rtneural)
13. [Solver Tuning, Diagnostics & Troubleshooting](#13-solver-tuning-diagnostics--troubleshooting)
14. [Bundled Circuit Examples & Benchmark Library](#14-bundled-circuit-examples--benchmark-library)
15. [About Me](#15-About-Me)
16. [License & Acknowledgments](#16-license--acknowledgments)

---

## 1. Why LiveSPICE-Generator?

Traditional SPICE simulators (LTspice, ngspice, desktop LiveSPICE) are built for interactive single-point analysis or manual GUI tweaking. Training a neural network to emulate a non-linear analog circuit across its entire control space requires:
- Thousands of distinct potentiometer permutations.
- Multi-pole switch state combinations.
- Dynamic input drive variations (simulating single-coil vs. humbucker vs. boosted pickups).
- Artifact-free audio rendering across long training sequences.

Rendering a 2,500-clip dataset sequentially on an analog tube circuit takes **~150 hours** on a single thread. `livespice-gen` re-engineers the orchestration pipeline:

- **True Parallel Multi-Core Scaling**: Solvers are thread-isolated, achieving near-linear speedup across all CPU cores.
- **Stratified Latin Hypercube Sampling**: Allocates data budget precisely where circuits transition from linear to non-linear behavior (e.g. the edge-of-breakup sweet spot).
- **Exact Mathematical Parity**: Directly links against the authentic LiveSPICE `Circuit.dll` and `ComputerAlgebra.dll` simulation libraries (verified to $99.998\%$ sample agreement against reference test harnesses).
- **Control Auto-Discovery**: Automatically parses schematics to detect continuous pots, tapers, multi-gang linkages, discrete switches, probe points, and signal inputs.
- **Production-Ready Training Integration**: Generates self-describing datasets with `manifest.jsonl` formatted directly for instant ingestion by PyTorch, RTNeural, and NAM training pipelines.

---

## 2. End-to-End Workflow: From Schematic to VST3 Plugin

Here is the complete path from an analog circuit schematic to a real-time guitar plugin running in your DAW:

```
┌────────────────────────┐
│  Schematic (.schx)     │  <- Drawn in LiveSPICE CAD
└───────────┬────────────┘
            │
            ▼  Step 1: Inspect Controls
       [livespice-gen controls]
            │
            ▼  Step 2: Generate Spec Template
       [livespice-gen spec]
            │
            ▼  Step 3: Tune Stratification & Ranges (.spec.json)
       [livespice-gen plan]  <- Verifies sample counts & runtime
            │
            ▼  Step 4: High-Throughput Parallel Render
       [livespice-gen render -j 8]
            │
            ▼  Step 5: Audit & Integrity Verification
       [livespice-gen verify]
            │
            ▼  Step 6: Train Neural Network (training/train.py)
       [PyTorch Truncated-BPTT GRU]
            │
            ▼  Step 7: Export Model (model.json)
       [RTNeural C++ Engine]
            │
            ▼  Step 8: Load into Real-Time Plugin (VST3 / AU / CLAP)
       [NeuralPedal / RTNeural Plugin at < 1% CPU]
```

---

## 3. Architecture & Circuit Simulation Engine

```
[ Input Audio (WAV) ] + [ Circuit Schematic (.schx) ]
                       │
                       ▼
         [ LiveSPICE-Generator Engine ]
         ┌───────────────────────────────────────────┐
         │ • CLI Parser & Spec Compiler              │
         │ • Unified ControlSet Discovery            │
         │ • Multi-Band Stratified Latin Hypercube   │
         │ • Energy-Gated Excerpt Placement (Spans)  │
         │ • Log-Uniform Dynamic Gain Staging        │
         └─────────────────────┬─────────────────────┘
                               │
            ┌──────────────────┴──────────────────┐
            │  Thread-Safe Parallel Job Dispatch  │
            ▼                                     ▼
   [ Worker Thread 1 ]                   [ Worker Thread N ]
   • Isolated Circuit Graph              • Isolated Circuit Graph
   • Modified Nodal Analysis (MNA)       • Modified Nodal Analysis (MNA)
   • JIT Dynamic Expressions             • JIT Dynamic Expressions
   • Newton-Raphson Non-Linear Solve     • Newton-Raphson Non-Linear Solve
   • DC Bias Warmup Pre-roll             • DC Bias Warmup Pre-roll
            │                                     │
            └──────────────────┬──────────────────┘
                               │ (Lock-free manifest append)
                               ▼
                  [ Output Training Dataset ]
                  ├── manifest.jsonl (Truth Index)
                  ├── input/normalized.wav
                  └── renders/Circuit_K50_T50_V50_t0.0-2.0s.wav
```

### Numerical Simulation Principles

1. **Modified Nodal Analysis (MNA)**:
   The circuit graph is translated into a system of non-linear differential-algebraic equations:
   $$f(v(t), \dot{v}(t), i(t), u(t)) = 0$$
   Where $v(t)$ are node voltages, $i(t)$ are branch currents, and $u(t)$ are input voltages.
2. **Implicit Numerical Integration**:
   Capacitors and inductors are discretized using Trapezoidal or Backward Euler integration:
   $$i_C[n] = \frac{2C}{\Delta t}(v_C[n] - v_C[n-1]) - i_C[n-1]$$
3. **Newton-Raphson Root Finding**:
   Non-linear components (diodes, JFETs, BJT transistors, triode/pentode vacuum tubes) are solved iteratively at every time step until voltage convergence is achieved within numerical tolerance.
4. **Internal Oversampling & Anti-Aliasing**:
   High-gain non-linearities generate harmonic content extending well into megahertz territory. `livespice-gen` runs the simulation loop at $N \times$ oversampling (e.g. $16\times = 768\,\text{kHz}$ at a $48\,\text{kHz}$ base rate), then downsamples with an anti-aliasing filter to prevent Nyquist foldback distortion.

---

## 4. Component & Control Intelligence

LiveSPICE schematics contain heterogeneous component models. `livespice-gen` inspects the circuit graph and constructs a unified `ControlSet`:

### Continuous Controls (`IPotControl`)
- **Supported Components**: `Potentiometer`, `VariableResistor`.
- **Taper Functions**:
  - **Linear (`Linear`)**: Direct proportional division along normalized travel $w \in [0.0, 1.0]$:
    $$R_1 = R \cdot (1 - w),\quad R_2 = R \cdot w$$
  - **Logarithmic (`Logarithmic`)**: Audio taper emulating human hearing perception:
    $$R_1 = R \cdot (1 - 10^{2(w-1)}),\quad R_2 = R \cdot 10^{2(w-1)}$$
  - **Reverse Logarithmic (`ReverseLogarithmic`)**: Inverted exponential curve utilized in specific drive and bias networks.

### Discrete Controls (`IButtonControl`)
- **Supported Components**: `SinglePoleSwitch`, `SPDT` (2 positions), `SP3T` (3 positions), `SP4T`, `SP5T`, and legacy `Switch`.
- **Behavior**: Switches alter the topological branch connectivity of the circuit matrix. When closed, branch impedance approaches zero ($G \to \infty$); when open, branch conductance is zero.
- **Automatic State Sampling**: If a switch is present in a schematic but omitted from a spec file, `livespice-gen` **automatically enumerates and samples across all valid throws** $[0, 1, \dots, N-1]$ rather than silently leaving the switch in an arbitrary default state.

### Multi-Gang Control Unification
High-end analog circuits frequently link multiple physical elements under a single control shaft:
- **Dual-Gang Pots**: Common in tone and drive networks (e.g. `G-A+G-B x2` in the BOSS BD-2).
- **Multi-Pole Push-Pull Switches**: Found in boutique amplifiers (e.g. Mesa Boogie Mark IIC+), such as 4-gang (`Pull Shift (++ mod) x4`) and 5-gang (`Pull Lead x5`) switch assemblies.
- `livespice-gen` recognizes schematic grouping metadata (`Group` property and naming patterns), unifies all coupled components into a single logical control, and updates them simultaneously during simulation.

### Multiple Speaker & Probe Routing
Schematics often feature multiple `Speaker` probe components (e.g., Clean Output vs. Lead Output, or Pre-EQ vs. Post-EQ). The `--speaker` argument allows you to tap a specific output node (e.g. `--speaker S9`) or sum all active speakers.

---

## 5. Installation & Compilation

### Option A: Standalone Pre-Built Binaries (Recommended)
Download the latest `livespice-gen-*-win-x64.zip` from GitHub Releases. Unpack to any directory:

```text
livespice-gen/
├── livespice-gen.exe     (Self-contained executable; no .NET install required)
└── Components/           (Vacuum tube, transistor, and diode SPICE models)
    ├── Tubes.xml
    ├── Transistors.xml
    ├── Diodes.xml
    └── OpAmps.xml
```

Add the folder to your system `PATH` to invoke `livespice-gen` from any terminal.

### Option B: Compile from Source
Requires the [.NET 10.0 SDK](https://dotnet.microsoft.com/download) or higher.

1. Clone the repository recursively with all submodules:
   ```bash
   git clone --recurse-submodules https://github.com/YourUsername/LiveSPICE-Generator.git
   cd LiveSPICE-Generator
   ```
2. Run the automated setup script:
   - **Windows (PowerShell)**:
     ```powershell
     .\setup.ps1
     ```
   - **Linux / macOS (Bash)**:
     ```bash
     ./setup.sh
     ```
3. Compile in Release mode:
   ```bash
   dotnet build LiveSPICE-Generator.sln -c Release
   ```

---

## 6. Command Line Interface (CLI) Reference

```bash
livespice-gen <command> [arguments] [options]
```

### Primary Commands

#### 1. `controls` (Alias: `knobs`)
Audits a schematic file and reports all discovered potentiometers, variable resistors, discrete switches, multi-gang linkages, input sources, and output speaker probes.
```bash
livespice-gen controls "circuits/Mesa Mark IIC.schx"
```

#### 2. `spec` (Alias: `template`)
Generates a complete, ready-to-edit JSON specification template containing all detected controls, continuous knob bands, discrete switch positions, and solver settings.
```bash
livespice-gen spec "circuits/BOSS BD-2 Blues Driver.schx" bd2.spec.json
```

#### 3. `plan`
Performs a dry-run analysis. Compiles sampling distributions, calculates energy-gated excerpt positions, verifies disk capacity, and provides wall-clock time estimates per thread without rendering audio.
```bash
livespice-gen plan bd2.spec.json --input guitar_di.wav
```

#### 4. `render`
Executes parallel multi-threaded batch simulation, rendering audio excerpts to disk and streaming real-time metrics to `manifest.jsonl`.
```bash
# Spec-driven rendering:
livespice-gen render bd2.spec.json ./dataset_bd2 -j 8

# Direct ad-hoc rendering without a spec file:
livespice-gen render circuit.schx input.wav ./dataset_out --budget 1000 --duration 2.0 -j 8
```

#### 5. `verify`
Audits an existing dataset directory. Cross-checks every manifest row against files on disk, ensuring 0 missing files, 0 flat silence outputs, 0 clipped samples, and 0 non-finite values (NaN / Inf).
```bash
livespice-gen verify ./dataset_bd2
```

---

### Command Line Options Reference

| Option | Argument | Default | Description |
|---|---|---|---|
| `-j`, `--threads` | `<int>` | CPU Count | Number of concurrent worker threads (clamped 1–8 by default for cache efficiency). |
| `--budget` | `<int>` | `2500` | Total number of audio excerpts to render. |
| `--duration` | `<float>` | `15.0` | Duration of each rendered audio excerpt in seconds. |
| `--warmup` | `<float>` | `0.05` | Pre-roll solver duration (seconds) before audio capture to settle DC bias. |
| `--rate` | `<int>` | `48000` | Output sample rate in Hz. Automatically resamples input audio if needed. |
| `--oversample` | `<int>` | `16` | Internal oversampling factor (`1`, `2`, `4`, `8`, `16`, `32`) to eliminate aliasing. |
| `--iterations` | `<int>` | `32` | Maximum Newton-Raphson convergence iterations per sample. |
| `--normalize` | `<float>` | `0.9` | Target peak level for master reference input (`0.0` disables normalization). |
| `--gain` | `<float>` | None | Fixed input drive level in dB. Sets both low and high gain bounds. |
| `--gain-low` | `<float>` | `-18.0` | Minimum random drive level in dB (log-uniform distribution). |
| `--gain-high` | `<float>` | `6.0` | Maximum random drive level in dB (log-uniform distribution). |
| `--time-range` | `<t0..t1>` | Full Audio | Restricts excerpt selection to a time window in seconds (e.g. `--time-range 10..90`). |
| `--spans` | `<mode>` | `signal` | Excerpt placement strategy: `signal` (skips silence via energy gate) or `all`. |
| `--joint` | `<mode>` | `independent` | Joint sampling distribution: `independent`, `grid`, `sweep`, or `sweepRandom`. |
| `--knob` | `<spec>` | Spec | Custom knob bands, e.g. `--knob Gain=0..0.2:1000,0.2..1.0:1500`. |
| `--switch` | `<spec>` | Spec | Custom switch positions, e.g. `--switch Bright=1` or `--switch Mode=0,2`. |
| `--speaker` | `<name>` | All (Sum) | Target probe component name in schematic (e.g. `--speaker S9`). |
| `--seed` | `<int>` | `0` | PRNG seed for deterministic, 100% reproducible dataset generation. |
| `--resume` | `<bool>` | `true` | Automatically resumes interrupted renders by filtering existing manifest IDs. |
| `--limit` | `<int>` | None | Halts rendering after $N$ jobs. Perfect for rapid smoke testing. |
| `--dry-run` | Flag | `false` | Runs `plan` instead of rendering audio. |

---

## 7. The Power-User Playbook & Advanced Techniques

### Playbook 1: Stratified Breakup Sampling for Overdrive Pedals
In guitar overdrive pedals (BOSS BD-2, Tube Screamer, Klon Centaur), the most musically expressive behavior occurs right where diodes begin conducting (the lower $20\%\text{--}35\%$ of gain travel). Uniform random sampling wastes $70\%$ of compute on heavily saturated square waves.

Use stratified knob allocations to dedicate $60\%\text{--}75\%$ of your dataset budget to the edge-of-breakup transition:

```bash
livespice-gen render "circuits/BOSS BD-2 Blues Driver.schx" input.wav ./dataset_bd2 \
  --budget 2500 \
  --duration 2.0 \
  --knob "Gain=0.0..0.25:1500,0.25..1.0:1000" \
  --knob "Level=0.15..1.0:2500" \
  --knob "Tone=0.0..1.0:2500" \
  -j 8
```
*Result*: 1,500 renders precisely capture the subtle dynamic touch sensitivity, while Volume is kept above $0.15$ to avoid un-trainable low-noise floors.

---

### Playbook 2: Discrete Switch Permutation on Modded Tube Preamps
When a circuit contains multi-position switches (e.g. bright switches, fat switches, gain boost modes), you need equal representation across all discrete topologies.

```bash
livespice-gen render "circuits/Marshall JCM800 2203 preamp modded.schx" input.wav ./dataset_jcm \
  --budget 1800 \
  --duration 2.0 \
  --switch "C1=0,1,2" \
  --switch "C2=0,1,2" \
  --speaker "S3" \
  -j 8
```
*Result*: The engine splits the 1,800 render budget evenly across all 9 switch permutations ($200$ renders per discrete state), while continuously stratifying the 6 potentiometers.

---

### Playbook 3: Ultra-Pristine Studio Anti-Aliasing
Extreme fuzz circuits (Big Muff, Fuzz Face, Pro Co Rat) generate intense high-frequency harmonics when hard-clipping. To prevent aliasing from degrading neural model fidelity:

```bash
livespice-gen render "circuits/Big Muff Pi.schx" input.wav ./dataset_muff_hifi \
  --rate 48000 \
  --oversample 32 \
  --iterations 32 \
  --warmup 0.08 \
  -j 4
```
*Result*: The internal Newton-Raphson solver steps at $48{,}000 \times 32 = 1{,}536{,}000\text{ Hz}$ ($1.536\,\text{MHz}$), eliminating digital aliasing artifacts by over $90\,\text{dB}$.

---

### Playbook 4: Full-Track NAM Target Re-Amping
To process an entire dry DI guitar track through a circuit at fixed settings for direct NAM (Neural Amp Modeler) training:

```bash
livespice-gen render "circuits/BOSS BD-2 Blues Driver.schx" full_di.wav ./nam_export \
  --budget 1 \
  --duration 180.0 \
  --knob "Gain=0.45" \
  --knob "Tone=0.50" \
  --knob "Level=0.75" \
  --spans all \
  --warmup 0.1
```

---

## 8. JSON Specification Schema

Specification files (`.spec.json`) provide repeatable, version-controlled experiment definitions:

```json5
{
  "name": "MesaMarkIIC-LeadProfile",
  "circuit": "circuits/Mark IIC+(++) Preamp_GEQ.schx",
  "input": "audio/reamp_sweep.wav",
  "budget": 2400,
  "duration": 2.5,
  "joint": "independent",       // "independent" | "grid" | "sweep" | "sweepRandom"
  "spans": "signal",            // "signal" (energy-gated) | "all" (uniform)
  "timeRange": [0.0, 180.0],    // Restrict excerpt selection window in seconds
  "render": {
    "sampleRate": 48000,
    "oversample": 16,           // 1, 2, 4, 8, 16, 32
    "iterations": 32,           // Max Newton-Raphson iterations
    "normalize": 0.9,           // Master reference normalization
    "warmup": 0.05,             // DC settling pre-roll in seconds
    "speaker": "S9"             // Specific probe tap identifier
  },
  "sampling": {
    "gainDb": [-18.0, 6.0],     // Log-uniform drive level range in dB
    "seed": 42
  },
  "knobs": {
    "Lead Drive": {
      "bands": [
        { "from": 0.0, "to": 0.3, "weight": 0.6, "note": "crunch boundary" },
        { "from": 0.3, "to": 1.0, "weight": 0.4, "note": "lead saturation" }
      ]
    },
    "Pull Lead": {
      "positions": [0, 1]       // Discrete switch positions to sample
    },
    "Pull Shift (++ mod)": {
      "pin": 1                  // Lock 4-gang switch permanently to position 1
    },
    "Master 1": {
      "bands": [ [0.2, 1.0] ]   // Prevent low-volume noise floor capture
    }
  }
}
```

---

## 9. Sampling Theory & Stratification Mechanics

Uniform pseudo-random sampling suffers from Poisson clumping: some parameter sub-spaces are heavily over-sampled while adjacent regions are neglected. `livespice-gen` implements **Stratified Latin Hypercube Sampling with Jittered Strata**:

```
0.0           0.25                      1.0 (Travel)
 ┌─────────────┬─────────────────────────┐
 │ Band 1 (60%)│ Band 2 (40%)            │
 │  •  •  •  • │   •      •      •       │  <- Jittered within uniform subdivisions
 └─────────────┴─────────────────────────┘
```

### Joint Sampling Modes (`--joint`)
1. **`independent` (Default)**:
   Parameters are drawn independently from their stratified distributions. Provides optimal space-filling coverage across multi-dimensional continuous and discrete spaces.
2. **`grid`**:
   Evaluates regular Cartesian products across parameters. Best suited for circuits with 1 to 3 controls.
3. **`sweep`**:
   Continuously varies knob travel along systematic ramp trajectories across successive excerpts. Ideal for training recurrent networks (GRU / LSTM) to learn memory, dynamic hysteresis, and thermal/bias drift.
4. **`sweepRandom`**:
   Executes bounded random walks through parameter space with constrained delta-steps per excerpt.

---

## 10. Analog Transient Settling & DC Bias Warmup

Analog circuits contain reactive storage components:
- **Capacitors**: $i(t) = C \frac{dv(t)}{dt}$
- **Inductors**: $v(t) = L \frac{di(t)}{dt}$

When a transient simulation initializes, initial state vectors are zero ($v_C(0) = 0\text{ V}, i_L(0) = 0\text{ A}$). In vacuum tube stages operating at $+250\text{ V}$ to $+400\text{ V}$ plate voltages, coupling and cathode bypass capacitors require several cycles to reach steady-state DC operating points.

```
Without Warmup:
[DC Offset Jump] ---> \___/\__/\___/\___ (Loud DC pop at t = 0)

With Warmup (--warmup 0.05):
[Pre-Roll Settling] | [Clean Excerpt Capture] ---> ~~~/\__/\___
(Simulated & Dropped)| (Recorded to WAV)
```

Without warmup, rendered audio begins with an abrupt DC transient that corrupts neural network training losses (particularly ESR and multi-resolution STFT). `livespice-gen` runs an unrecorded pre-roll simulation (default: $0.05\text{ s}$ / $50\text{ ms}$) prior to recording audio frames.

---

## 11. Dataset Structure & Manifest Format

Generated datasets are completely self-describing and immutable:

```text
dataset_bd2/
├── manifest.jsonl        <- Dataset truth index
├── input/
│   └── normalized.wav    <- Normalized master reference audio
└── renders/
    ├── BOSSBD2BluesDriver_L10_T50_G20_t0.00-2.00s.wav
    ├── BOSSBD2BluesDriver_L80_T25_G90_t2.00-4.00s.wav
    └── ...
```

### Manifest Format (`manifest.jsonl`)

The manifest uses JSON Lines format.

#### Header Line
```json
{
  "type": "dataset",
  "circuit": "BOSS BD-2 Blues Driver.schx",
  "circuit_sha256": "3993bb1ad2091525a3c240f775883d8f5017fc4bce6ce02950a2480f2d7b4640",
  "input": "normalized.wav",
  "input_sha256": "09c64142b6584528fbb486ec686ad9ade3b798475641019ac1ac33f318784236",
  "input_v0dBFS": 1.0,
  "sample_rate": 48000,
  "oversample": 16,
  "iterations": 32,
  "duration_s": 2.0,
  "generator": "livespice-gen",
  "spec_sha256": "1c0ff1982e56fcb5b15021ce45b50bb45e5e7504645b77dff5ec108c9fd4443c",
  "knobs": ["Level", "Tone", "Gain"]
}
```

#### Row Lines
```json
{
  "id": "BOSSBD2BluesDriver_L50_T50_G50_t0.00-2.00s",
  "status": "ok",
  "file": "BOSSBD2BluesDriver_L50_T50_G50_t0.00-2.00s.wav",
  "knobs": { "Level": 0.5, "Tone": 0.5, "Gain": 0.5 },
  "offset_s": 0.0,
  "duration_s": 2.0,
  "input_gain": 0.250266,
  "origin": "independent",
  "mean": -0.001679,
  "peak": 0.585477,
  "rms": 0.244479,
  "clipped": 0,
  "nonfinite": 0,
  "flat": false
}
```

---

## 12. Downstream Neural Network Training (PyTorch & RTNeural)

Datasets generated by `livespice-gen` integrate seamlessly with the bundled training pipeline in `training/`:

### Step 1: Prepare Memory-Mapped Dataset Splits
```bash
python training/train.py prepare --db ./dataset_bd2
```
`prepare` inspects `manifest.jsonl`, validates audio geometry, filters non-finite or quiet renders, draws deterministic train/val/test splits ($80\% / 10\% / 10\%$), and memory-maps tensors to disk.

### Step 2: Train Truncated-BPTT Recurrent Neural Network
```bash
python training/train.py train --db ./dataset_bd2 --hidden 32 --tbptt 64 --epochs 100
```
- **Architecture**: `KnobGRU` (`Linear(n_features, hidden) -> Tanh -> GRU(hidden, hidden) -> Linear(hidden, 1)`).
- **Conditioning**: Audio samples concatenated with normalized knob and switch feature channels $[x, \text{knob}_1, \dots, \text{knob}_k]$.
- **Loss Metric**: Multi-scale Error-to-Signal Ratio ($\text{ESR}$) with pre-emphasis high-pass filter:
  $$\mathcal{L}_{\text{ESR}} = \frac{\sum_{n} |y[n] - \hat{y}[n]|^2}{\sum_{n} |y[n]|^2 + \epsilon}$$

### Step 3: Export to Real-Time RTNeural JSON
```bash
python training/train.py export --db ./dataset_bd2 --ckpt runs/bd2_best.pt
```
Exports `model.json` with permuted GRU gate ordering (`z, r, n`) and split recurrent bias arrays ready for direct instantiation in C++:

```cpp
// Real-time C++ audio callback (RTNeural):
RTNeural::ModelT<float, 4, 1,
    RTNeural::DenseT<float, 4, 32>,
    RTNeural::TanhActivationT<float, 32>,
    RTNeural::GRULayerT<float, 32, 32>,
    RTNeural::DenseT<float, 32, 1>> model;

// Load exported JSON:
std::ifstream f("model.json");
model.parseJson(f);
model.reset();

// Process sample-by-sample at < 1% CPU:
float inputs[4] = { audio_sample, level_knob, tone_knob, gain_knob };
float out = model.forward(inputs);
```

---

## 13. Solver Tuning, Diagnostics & Troubleshooting

### 1. Non-Convergence (`status: failed`, "Iteration limit reached")
- **Cause**: Stiff non-linear diode loops or high-gain tube stages oscillating during Newton-Raphson root finding when time steps are too coarse.
- **Fix**: Increase oversampling to `--oversample 16` or `--oversample 32`, and increase iteration headroom to `--iterations 32`.

### 2. Audio Pops / Clicks at $t = 0$
- **Cause**: Coupling capacitors starting from zero energy state.
- **Fix**: Ensure `--warmup` is set to at least `0.05` ($50\text{ ms}$) or `0.1` ($100\text{ ms}$).

### 3. Flat / Dead Audio (`flat: true` in manifest)
- **Cause**: Master Volume knob sampled at $0.0$, or audio excerpt landed during silence.
- **Fix**: Set `--spans signal` to enforce energy gating, and set a minimum volume threshold in the spec (e.g. `"Volume": { "bands": [ [0.15, 1.0] ] }`).

### 4. Digital Clipping (`clipped > 0`)
- **Cause**: Output signal exceeded $0\,\text{dBFS}$ relative to the schematic's `Speaker` reference scale.
- **Fix**: Decrease input normalization via `--normalize 0.7` or lower drive gains with `--gain-high 0.0`.

---

## 14. Bundled Circuit Examples & Benchmark Library

| Example Specification | Target Schematic | Circuit Topology | Verified Speed | Controls |
|---|---|---|---|---|
| `bd2-breakup-focus.spec.json` | `BOSS BD-2 Blues Driver.schx` | Discrete JFET + Op-Amp Clipper | $0.6\,\text{jobs/s}$ | 3 (incl. dual-gang) |
| `bigmuff-balanced.spec.json` | `Big Muff Pi.schx` | 4-Stage BJT Transistor Fuzz | $1.8\,\text{jobs/s}$ | 3 (Sustain, Tone, Vol) |
| `jcm800-switched-preamp.spec.json` | `Marshall JCM800 2203 modded.schx`| High-Gain Cascaded 12AX7 Tubes | $0.9\,\text{jobs/s}$ | 8 (6 pots, 2 SP3T switches)|
| `mark2c-multigang-preamp.spec.json`| `Mark IIC+(++) Preamp_NOGEQ.schx` | Boutique High-Voltage Tube Amp | $0.7\,\text{jobs/s}$ | 14 (7 pots, 7 switches) |
| `mark2c-geq-preamp.spec.json` | `Mark IIC+(++) Preamp_GEQ.schx` | High-Gain Tube Preamp + 5-Band EQ | $0.5\,\text{jobs/s}$ | 20 (12 pots, 8 switches)|
| `crybaby-wah-sweep.spec.json` | `Dunlop Cry Baby GCB-95.schx` | BJT Active LC Inductor Resonator | $2.2\,\text{jobs/s}$ | 1 (Continuous Wah sweep)|

---

## 15. About Me

**Yahia Kemari** — Telecommunications Engineer (M2), USTHB, Algeria.
Interested in networks, infrastructure, cybersecurity, and automation.

- LinkedIn: https://www.linkedin.com/in/yahia-kemari/
- Email: contact.kemari.yahia@gmail.com

---

## 16. License & Acknowledgments

- **LiveSPICE-Generator**: Licensed under the [MIT License](LICENSE).
- [**LiveSPICE Core**](https://github.com/dsharlet/LiveSPICE): Powered by the LiveSPICE circuit simulation framework by DSharlet.
- Built for audio researchers, DSP developers, and neural modeling engineers.
