# LiveSPICE-Generator (`livespice-gen`)

A high-throughput, multi-threaded dataset generation engine and batch simulator for LiveSPICE circuit schematics (`.schx`). Designed for training neural audio models (NAM, RTNeural, PyTorch GRU/LSTM/WaveNet) on analog hardware: guitar distortion/overdrive pedals, vacuum tube preamplifiers, active tone stacks, and non-linear filter topologies.

---

## Table of Contents

1. [Architecture & Design Principles](#architecture--design-principles)
2. [Component & Control Intelligence](#component--control-intelligence)
3. [Installation & Compilation](#installation--compilation)
4. [Command Line Interface](#command-line-interface)
5. [The Power-User Playbook](#the-power-user-playbook)
6. [JSON Specification Schema](#json-specification-schema)
7. [Sampling Theory & Stratification](#sampling-theory--stratification)
8. [Analog Transient Settling & Warmup](#analog-transient-settling--warmup)
9. [Dataset Structure & Manifest Format](#dataset-structure--manifest-format)
10. [Downstream Neural Network Training](#downstream-neural-network-training)
11. [Troubleshooting & Numerical Solver Diagnostics](#troubleshooting--numerical-solver-diagnostics)
12. [Bundled Examples](#bundled-examples)
13. [Continuous Integration & Release Publishing](#continuous-integration--release-publishing)
14. [License & Acknowledgments](#license--acknowledgments)

---

## 1. Architecture & Design Principles

Standard circuit simulators are designed for interactive graphical analysis or single-point transient inspection. Generating supervised learning datasets for deep neural networks requires simulating thousands of parameter combinations across hundreds of audio excerpts.

`livespice-gen` decouples the core numerical simulation engine of LiveSPICE from its desktop GUI and re-engineers the orchestration pipeline:

```
[ Input Audio (WAV) ] + [ Circuit Schematic (.schx) ]
                       |
                       v
         [ LiveSPICE-Generator Engine ]
         +-------------------------------------------+
         | - Argument Parser & Spec Compiler         |
         | - Control Discovery & Gang Unification     |
         | - Multi-Band Stratified Latin Hypercube    |
         | - Energy-Gated Signal Excerpt Placement   |
         | - Dynamic Gain Staging (Log-Uniform)      |
         +---------------------+---------------------+
                               |
            +------------------+------------------+
            | (Worker Solvers across CPU Cores)   |
            v                                     v
   [ Thread 1: Solver ]                 [ Thread N: Solver ]
   - Modified Nodal Analysis (MNA)      - MNA Equations
   - JIT Dynamic Expressions            - JIT Dynamic Expressions
   - Implicit Trapezoidal Step          - Implicit Trapezoidal Step
   - Newton-Raphson Non-Linear Solve    - Newton-Raphson Solve
   - DC Bias Warmup Pre-roll            - DC Bias Warmup Pre-roll
            |                                     |
            +------------------+------------------+
                               |
                               v
                  [ Output Training Dataset ]
                  |-- manifest.jsonl (Truth Index)
                  |-- input/normalized.wav
                  \-- renders/*.wav (24-bit / 32-bit float)
```

### Core Architecture Highlights

- **Thread-Isolated Solvers**: LiveSPICE simulation instances maintain state vectors for reactive components (capacitors and inductors). Concurrency cannot be achieved by sharing a simulation object. `livespice-gen` allocates an independent, thread-local `Circuit` graph and `Simulation` instance per worker thread, achieving lock-free linear scaling across CPU cores.
- **Exact Mathematical Parity**: Audio generated through `livespice-gen` has been verified against native LiveSPICE test harnesses with 99.9982% sample-level agreement across 48,000 raw floating-point samples (max absolute difference of 2.21e-5, attributable solely to IEEE 32-bit float quantization).
- **Zero Heavy Runtime Dependencies**: The CLI argument parser, JSON serializer, and WAV I/O subsystems are hand-rolled to eliminate dependency bloat and guarantee deterministic execution across Windows, Linux, and macOS.

---

## 2. Component & Control Intelligence

LiveSPICE schematics contain heterogeneous component models. `livespice-gen` scans the circuit graph and constructs a unified `ControlSet` abstraction:

### Continuous Controls (`IPotControl`)
- **Components**: `Potentiometer`, `VariableResistor`.
- **Taper Functions**:
  - **Linear**: Resistance divides proportionally to normalized wipe $w \in [0.0, 1.0]$:
    $$R_1 = R \cdot (1 - w),\quad R_2 = R \cdot w$$
  - **Logarithmic (Audio Taper)**: Models human hearing response using exponential resistance distribution:
    $$R_1 = R \cdot (1 - 10^{2(w-1)}),\quad R_2 = R \cdot 10^{2(w-1)}$$
  - **Reverse Logarithmic**: Inverted exponential curve used in specialized tone and gain stages.

### Discrete Controls (`IButtonControl`)
- **Components**: `SinglePoleSwitch`, `SPDT` (2 positions), `SP3T` (3 positions), `SP4T`, `SP5T`, and legacy `Switch`.
- **Behavior**: Switches alter the topological connectivity of the circuit branch matrix. When closed, branch impedance approaches zero ($G \to \infty$); when open, branch conductance is zero.
- **Automated State Sampling**: If a switch is omitted from a specification file, `livespice-gen` automatically samples across all available discrete positions $[0, 1, \dots, N-1]$ rather than silently leaving it in an arbitrary default position.

### Multi-Gang Control Unification
High-end analog hardware frequently couples multiple circuit elements under a single physical control shaft:
- **Dual-Gang Potentiometers**: Common in tone controls and drive loops (e.g. `G-A+G-B x2` in the BOSS BD-2).
- **Multi-Pole Push-Pull Switches**: Found in boutique preamplifiers (e.g. Mesa Boogie Mark IIC+), including 4-gang (`Pull Shift (++ mod) x4`) and 5-gang (`Pull Lead x5`) switch banks.
- `livespice-gen` inspects schematic grouping identifiers (`Group` property and naming heuristics), collapses associated elements into a single logical control, and updates all sub-elements simultaneously during simulation.

### Multiple Speaker & Probe Routing
Complex schematics may contain multiple `Speaker` load components representing distinct circuit taps (e.g., Clean Output vs. Lead Output, or Pre-EQ vs. Post-EQ). The `--speaker` parameter directs the simulation to tap a specific component probe or sum all active speakers.

---

## 3. Installation & Compilation

### Option A: Standalone Binary (Recommended for End Users)
Standalone builds include the .NET runtime and required component libraries. No external SDK is required.

1. Download the latest `livespice-gen-*-<platform>.zip` from the Releases tab.
2. Unpack the archive to your target directory:
   ```text
   livespice-gen/
   |-- livespice-gen.exe     (Executable)
   \-- Components/           (Required vacuum tube, diode & transistor XML libraries)
       |-- Tubes.xml
       |-- Transistors.xml
       |-- Diodes.xml
       \-- OpAmps.xml
   ```
3. Add the unpacked directory to your system `PATH` to invoke `livespice-gen` globally.

### Option B: Build from Source
Requires the [.NET 10.0 SDK](https://dotnet.microsoft.com/download) or higher.

1. Clone the repository with recursive submodules to pull the LiveSPICE engine:
   ```bash
   git clone --recurse-submodules https://github.com/YourUsername/LiveSPICE-Generator.git
   cd LiveSPICE-Generator
   ```
2. Run the automated initialization script:
   - **Windows (PowerShell)**:
     ```powershell
     .\setup.ps1
     ```
   - **Linux / macOS (Bash)**:
     ```bash
     ./setup.sh
     ```
3. Alternatively, compile directly using the .NET CLI:
   ```bash
   dotnet build LiveSPICE-Generator.sln -c Release
   ```

---

## 4. Command Line Interface

```bash
livespice-gen <command> [arguments] [options]
```

### Primary Commands

#### `controls` (Alias: `knobs`)
Inspects a `.schx` schematic file and prints all discovered potentiometers, variable resistors, discrete switches, multi-gang groupings, audio input sources, and output speaker probes.
```bash
livespice-gen controls "path/to/circuit.schx"
```

#### `spec` (Alias: `template`)
Generates a structured JSON specification template pre-populated with every control, default sampling ranges, and simulation parameters.
```bash
livespice-gen spec "path/to/circuit.schx" [output_spec.json]
```

#### `plan`
Parses a specification file (or circuit + WAV pair), calculates the multi-dimensional sampling grid, evaluates signal excerpt coverage, checks available disk space, and reports estimated wall-clock rendering time. No audio is generated.
```bash
livespice-gen plan spec.json --input audio.wav [options]
```

#### `render`
Executes the simulation pipeline across parallel worker threads, writing audio files to disk and appending real-time progress and audio metrics to `manifest.jsonl`.
```bash
livespice-gen render spec.json [output_directory] [options]
# Or direct invocation without a spec file:
livespice-gen render circuit.schx input.wav output_directory [options]
```

#### `process`
Processes an entire audio file continuously through the circuit at fixed knob and switch positions. Useful for full-track re-amping, generating NAM test sweeps, or listening to custom circuit voicings.
```bash
livespice-gen process circuit.schx input.wav output.wav --knob Gain=0.35,Tone=0.7 --switch Bright=1
```

#### `verify`
Audits an existing dataset directory. Parses `manifest.jsonl`, verifies that every audio file exists on disk, and analyzes audio metrics for non-finite values (NaN / Inf), digital clipping (exceeding 0 dBFS), or silence / failed convergence.
```bash
livespice-gen verify ./dataset_directory
```

---

### Command Line Options Reference

| Flag | Argument | Default | Description |
|---|---|---|---|
| `-j`, `--threads` | `<int>` | CPU Count | Number of concurrent worker threads. Clamped between 1 and 8 by default to prevent thread thrashing. |
| `--budget` | `<int>` | `2500` | Total number of audio excerpts to render. |
| `--duration` | `<float>` | `15.0` | Length of each rendered excerpt in seconds. |
| `--warmup` | `<float>` | `0.05` | Pre-roll solver duration in seconds before recording audio. Settles analog DC bias and capacitor states. |
| `--rate` | `<int>` | `48000` | Simulation sample rate in Hz. If input audio differs, it is resampled automatically. |
| `--oversample` | `<int>` | `2` | Internal oversampling factor (`1`, `2`, `4`, `8`, `16`, `32`). Eliminates high-frequency aliasing in non-linear stages. |
| `--iterations` | `<int>` | `8` | Maximum Newton-Raphson numerical convergence iterations per sample step. |
| `--normalize` | `<float>` | `0.9` | Target peak level for master reference input (`0.0` disables normalization). |
| `--gain` | `<float>` | None | Fixed input drive level in dB. Sets both low and high gain bounds. |
| `--gain-low` | `<float>` | `-18.0` | Minimum random drive level in dB (log-uniform distribution). |
| `--gain-high` | `<float>` | `6.0` | Maximum random drive level in dB (log-uniform distribution). |
| `--time-range` | `<t0..t1>` | Full Audio | Restricts excerpt selection to a time window (seconds), e.g. `--time-range 10.0..60.0`. |
| `--spans` | `<mode>` | `signal` | Excerpt placement strategy: `signal` (skips silence via energy gate) or `all` (uniform across file). |
| `--joint` | `<mode>` | `independent` | Joint sampling distribution: `independent`, `grid`, `sweep`, or `sweepRandom`. |
| `--knob` | `<spec>` | Spec | Override knob bands, e.g. `--knob Gain=0..0.2:1000,0.2..1.0:1500` or `--knob Gain=0..0.25:70%`. |
| `--focus` | `<spec>` | None | Quick focus override for non-linear regions, e.g. `--focus Gain=0..0.3:75%`. |
| `--switch` | `<spec>` | Spec | Restrict or pin switch positions, e.g. `--switch Bright=1` or `--switch Mode=0,2`. |
| `--anchors` | Flag | `false` | Injects deterministic boundary corners (all min, all noon, all max, alternating min/max). |
| `--speaker` | `<name>` | All (Sum) | Target probe component name in schematic (e.g. `--speaker S9`). |
| `--seed` | `<int>` | `0` | PRNG seed for fully reproducible dataset generation. |
| `--resume` | `<bool>` | `true` | Resumes interrupted rendering by cross-checking completed job IDs in `manifest.jsonl`. |
| `--limit` | `<int>` | None | Aborts after rendering $N$ jobs. Essential for quick smoke testing. |
| `--dry-run` | Flag | `false` | Runs `plan` instead of executing simulation. |

---

## 5. The Power-User Playbook

### Playbook 1: Stratified Breakup Sampling for Overdrive Pedals
Overdrive circuits (e.g. Tube Screamer, Blues Driver) exhibit pronounced non-linearities in the lower 20% to 35% of knob travel. Uniform random sampling allocates too many resources to saturated square-wave distortion.

```bash
livespice-gen render "circuits/BOSS BD-2 Blues Driver.schx" input.wav ./bd2_dataset \
  --budget 2000 \
  --duration 2.0 \
  --focus "Gain=0.0..0.25:75%" \
  --knob "Level=0.0..0.2:500,0.2..1.0:1500" \
  -j 8
```
*Result*: 75% of renders explore the edge-of-breakup transition, 25% cover saturation, and output volume is partitioned to avoid un-trainable low-noise floors.

### Playbook 2: Discrete Switch Enumeration on Tube Preamps
A high-gain amplifier may have multiple discrete voicing switches. To ensure every switch configuration is sufficiently represented:

```bash
livespice-gen render "circuits/Marshall JCM800 2203 preamp modded.schx" input.wav ./jcm_dataset \
  --budget 1800 \
  --duration 2.0 \
  --switch "C1=0,1,2" \
  --switch "C2=0,1,2" \
  --speaker "S3" \
  -j 8
```
*Result*: The engine splits the 1,800 render budget evenly across all 9 switch permutations (200 renders per discrete state), while continuously stratifying the tone and gain potentiometers.

### Playbook 3: High-Fidelity Audio Setup (Anti-Aliasing)
Severe diode clipping and pentode saturation introduce high-order harmonics that fold over the Nyquist frequency. For maximum numerical precision:

```bash
livespice-gen render spec.json ./dataset_hi_fi \
  --rate 48000 \
  --oversample 16 \
  --iterations 32 \
  --warmup 0.1 \
  -j 4
```
*Result*: The internal Newton-Raphson solver steps at $48{,}000 \times 16 = 768{,}000\text{ Hz}$, suppressing aliasing artifacts by over 80 dB before decimation.

### Playbook 4: Full-Track NAM Target Generation
To re-amp an un-processed DI track through a circuit at fixed settings for direct NAM (Neural Amp Modeler) training:

```bash
livespice-gen process "circuits/Ibanez Tube Screamer TS-9.schx" v1_di.wav v1_target.wav \
  --knob "Drive=0.6,Tone=0.5,Level=0.8" \
  --oversample 8 \
  --warmup 0.1
```

---

## 6. JSON Specification Schema

Specification files (`.spec.json`) define repeatable experimental protocols. Relative paths are resolved relative to the directory containing the JSON file.

```json5
{
  "name": "MesaMarkIIC-LeadProfile",
  "circuit": "circuits/Mark IIC+(++) Preamp_NOGEQ.schx",
  "input": "audio/sample_di.wav",
  "budget": 2400,
  "duration": 2.5,
  "joint": "independent",       // "independent" | "grid" | "sweep" | "sweepRandom"
  "spans": "signal",            // "signal" (energy-gated) | "all"
  "timeRange": [0.0, 180.0],    // Restrict excerpt selection window in seconds
  "render": {
    "sampleRate": 48000,
    "oversample": 8,            // 1, 2, 4, 8, 16, 32
    "iterations": 16,           // Max Newton-Raphson iterations
    "normalize": 0.9,           // Master reference normalization
    "warmup": 0.05,             // DC settling pre-roll in seconds
    "speaker": "S9"             // Specific probe tap identifier
  },
  "sampling": {
    "gainDb": [-15.0, 6.0],     // Log-uniform drive level range in dB
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

## 7. Sampling Theory & Stratification

Uniform pseudo-random sampling exhibits Poisson clumping: regions of parameter space are over-sampled while other regions are omitted. `livespice-gen` implements **Stratified Latin Hypercube Sampling with Jittered Strata**:

```
0.0           0.2                       1.0 (Travel)
 +-------------+-------------------------+
 | Band 1 (60%)| Band 2 (40%)            |
 |  x  x  x  x |   x      x      x       |  <- Jittered within uniform subdivisions
 +-------------+-------------------------+
```

### Joint Sampling Modes (`--joint`)
1. `independent` (Default): Knobs are sampled from their respective stratified bands independently. Provides optimal space-filling coverage across high-dimensional parameter spaces.
2. `grid`: Evaluates Cartesian products across discrete points. Best suited for circuits with 1 to 3 controls.
3. `sweep`: Varies parameters along continuous trajectories across successive excerpts. Ideal for training recurrent networks (GRU / LSTM) to learn memory and hysteresis effects.
4. `sweepRandom`: Random walks through parameter space with bounded delta-steps per excerpt.

### Boundary Anchor Sampling (`--anchors`)
Neural networks often extrapolate poorly outside their convex training hull. Enabling `--anchors` injects deterministic boundary combinations:
- All controls set to minimum ($0.0$).
- All controls set to noon ($0.5$).
- All controls set to maximum ($1.0$).
- Alternating corner permutations ($0.0$ / $1.0$).

---

## 8. Analog Transient Settling & Warmup

Analog circuits contain reactive storage components:
- **Capacitors**: $i(t) = C \frac{dv(t)}{dt}$
- **Inductors**: $v(t) = L \frac{di(t)}{dt}$

When a transient simulation initializes, all initial conditions are assumed to be zero ($v_C(0) = 0\text{ V}, i_L(0) = 0\text{ A}$). In vacuum tube circuits operating with high plate voltages (e.g. $+250\text{ V}$ to $+400\text{ V}$), coupling capacitors require several cycles to charge to their steady-state DC operating point.

```
Without Warmup:
[DC Offset Jump] ---> \___/\__/\___/\___ (Massive pop at t = 0)

With Warmup (--warmup 0.05):
[Pre-Roll Settling] | [Clean Excerpt Capture] ---> ~~~/\__/\___
(Discarded)         | (Recorded to WAV)
```

Without warmup, rendered audio begins with a low-frequency thump or DC offset that corrupts neural network training losses (particularly Mean Squared Error and STFT magnitude). `livespice-gen` executes an unrecorded pre-roll step (default: $0.05\text{ s}$ / $50\text{ ms}$) prior to recording audio frames, ensuring clean DC baselines.

---

## 9. Dataset Structure & Manifest Format

Datasets are generated with self-describing directory hierarchies:

```text
dataset_output/
|-- manifest.jsonl
|-- input/
|   \-- normalized.wav
\-- renders/
    |-- BOSSBD2BluesDriver_L10_T50_G20_t0.00-2.00s.wav
    |-- BOSSBD2BluesDriver_L80_T25_G90_t2.00-4.00s.wav
    \-- ...
```

### The Manifest (`manifest.jsonl`)
The manifest utilizes JSON Lines (newline-delimited JSON).

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

#### Row Line
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

## 10. Downstream Neural Network Training

### Input Reconstruction
Audio written to `renders/*.wav` reflects the input signal scaled by `input_gain`. To reconstruct the exact input tensor in PyTorch:

```python
import torch
import soundfile as sf
import json

# 1. Load reference input audio
ref_audio, sr = sf.read("dataset/input/normalized.wav", dtype="float32")

# 2. Iterate manifest rows
with open("dataset/manifest.jsonl", "r") as f:
    header = json.loads(f.readline())
    for line in f:
        row = json.loads(line)
        if row.get("status") != "ok":
            continue
            
        start_idx = int(round(row["offset_s"] * sr))
        length = int(round(row["duration_s"] * sr))
        
        # Exact input slice with dynamic drive applied
        x = ref_audio[start_idx : start_idx + length] * row["input_gain"]
        
        # Target simulated audio
        y, _ = sf.read(f"dataset/renders/{row['file']}", dtype="float32")
        
        # Conditioning vector (floats for pots, integers for switches)
        cond = torch.tensor([row["knobs"][k] for k in header["knobs"]], dtype=torch.float32)
```

### Recommended Loss Functions
When training recurrent (GRU/LSTM) or convolutional (TCN/WaveNet) audio models on circuit datasets, combine time-domain error with Multi-Resolution STFT spectral distance:

$$\mathcal{L} = \mathcal{L}_{\text{ESR}} + \lambda \mathcal{L}_{\text{MR-STFT}}$$

Where Error-to-Signal Ratio ($\text{ESR}$) is defined as:
$$\mathcal{L}_{\text{ESR}} = \frac{\sum_{n} |y[n] - \hat{y}[n]|^2}{\sum_{n} |y[n]|^2 + \epsilon}$$

---

## 11. Troubleshooting & Numerical Solver Diagnostics

### Circuit Convergence Failures (`status: failed`)
- **Symptom**: Manifest rows show `"status": "failed"` and `"error": "Iteration limit reached"`.
- **Cause**: High-gain nonlinear feedback loops (e.g. clipping diodes in high-gain op-amp loops) can oscillate during Newton-Raphson root finding if the time step is too large.
- **Remedy**: Increase oversampling via `--oversample 16` or `--oversample 32` and increase iteration limits via `--iterations 32`.

### DC Baseline Shift / Output Clicks
- **Symptom**: Audio files contain loud clicks at sample index $0$.
- **Cause**: Reactive components initializing from zero energy states.
- **Remedy**: Ensure `--warmup` is set to at least `0.05` ($50\text{ ms}$) or `0.1` ($100\text{ ms}$).

### Flat Silence Output (`flat: true`)
- **Symptom**: `verify` flags flat audio lines.
- **Cause**: Input signal placed in silent pause of guitar recording, or Master Volume / Level knob sampled at $0.0$.
- **Remedy**: Set `--spans signal` to skip silent passages, and define a lower bound in your knob bands (e.g. `"Volume": { "bands": [ [0.15, 1.0] ] }`).

---

## 12. Bundled Examples

The `examples/` directory contains self-contained configurations and schematics:

| Specification | Target Schematic | Circuit Topology | Highlight Features |
|---|---|---|---|
| `bd2-breakup-focus.spec.json` | `BOSS BD-2 Blues Driver.schx` | Discrete JFET + Op-Amp Clipping | Stratified edge-of-breakup focus; dual-gang pot |
| `ts9-classic-drive.spec.json` | `Ibanez Tube Screamer TS-9.schx` | Symmetrical Silicon Diode Clipper | Soft-clipping curve; active RC tone stack |
| `bigmuff-balanced.spec.json` | `Big Muff Pi.schx` | 4-Stage BJT Transistor Fuzz | Mid-scoop tone filter sweep; extreme fuzz saturation |
| `jcm800-switched-preamp.spec.json`| `Marshall JCM800 2203 modded.schx`| High-Gain Cascaded 12AX7 Tubes | Dual `SP3T` (3-throw) switches (`C1`, `C2`); probe tap `S3` |
| `mark2c-multigang-preamp.spec.json`| `Mark IIC+(++) Preamp_NOGEQ.schx` | Boutique High-Voltage Tube Amp | 4-gang switch (`Pull Shift x4`); 3-gang switch; 240V tap `S9` |
| `crybaby-wah-sweep.spec.json` | `Dunlop Cry Baby GCB-95.schx` | BJT Active LC Inductor Resonator | Continuous sweep mode along treadle travel |

Test any example immediately:
```bash
livespice-gen plan examples/bd2-breakup-focus.spec.json
```

---

## 13. Continuous Integration & Release Publishing

The repository includes a GitHub Actions CI/CD configuration (`.github/workflows/ci.yml`).

### Automated Matrix Testing
On every commit or pull request, the workflow:
1. Checks out the repository and initializes git submodules recursively.
2. Builds the solution on `windows-latest`, `ubuntu-latest`, and `macos-latest`.
3. Executes CLI plan and validation checks.

### Automated Release Builds
Pushing a semver release tag (e.g. `git tag v1.0.0 && git push origin v1.0.0`) triggers the automated release builder. The workflow:
1. Compiles self-contained, single-file executables for Windows x64.
2. Bundles the required `Components/` XML libraries.
3. Compresses the release archive into `livespice-gen-v1.0.0-win-x64.zip`.
4. Automatically attaches the binary package to the GitHub Release.

---
## About Me

**Yahia Kemari** — Telecommunications Engineer (M2), USTHB, Algeria.
Interested in networks, infrastructure, cybersecurity, and automation.

- LinkedIn: https://www.linkedin.com/in/yahia-kemari/
- Email: contact.kemari.yahia@gmail.com

Open to opportunities

## 14. License & Acknowledgments

- **LiveSPICE-Generator**: Licensed under the [MIT License](LICENSE).
- **LiveSPICE Core**: Powered by the LiveSPICE circuit simulation framework by Dmitry Sharlet.
- Designed for audio researchers, DSP engineers, and machine learning practitioners creating real-time virtual analog instruments.
