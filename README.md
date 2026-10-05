<div align="center">

# ⚡ LiveSPICE-Generator (`livespice-gen`)

**The ultimate high-performance dataset generator for analog circuit simulation.**  
*Turn any LiveSPICE schematic (`.schx`) into clean, stratified datasets for training real-time neural audio models (RTNeural, NAM, PyTorch GRU/LSTM).*

[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-blue.svg)](https://github.com/)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Engine](https://img.shields.io/badge/Engine-LiveSPICE%20Core-orange.svg)](https://github.com/dsharlet/LiveSPICE)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

</div>

---

## 🎯 Why This Exists

Training neural network models on analog gear (guitar pedals, tube preamps, equalizers) requires **massive, balanced audio datasets**. If you only test random knob positions, you will miss the circuit's most critical non-linear behaviors: **the edge-of-breakup transition, switch toggles, diode knee compression, and extreme boundary settings**.

Standard LiveSPICE is an interactive graphical simulator or a sequential single-threaded test harness. It cannot:
- Dynamically discover complex multi-gang switches (e.g. 4-gang and 5-gang push-pull switches on Mesa preamps).
- Sample stratified non-linear knob regions with custom density.
- Parallelize simulations across modern multi-core processors.
- Eliminate analog DC settling pops on excerpt boundaries.

**`livespice-gen` solves all of this in a single portable, standalone CLI.**

---

## 🚀 Key Features

```
  ┌─────────────────────────────────────────────────────────────────────────────┐
  │                           LiveSPICE-Generator                               │
  ├─────────────────────────────────────────────────────────────────────────────┤
  │  • Multi-Threaded Engine : 8-16x speedup across CPU cores                   │
  │  • Full Control Intel    : Pots (Lin/Log), Switches (SPDT, SP3T), Gangs     │
  │  • Stratified Sampling   : Multi-band focus on edge-of-breakup sweet spots  │
  │  • Dynamic Gain Staging  : Log-uniform input drive variation (-18 to +6 dB) │
  │  • DC Bias Warmup        : Eliminates analog startup transients & pops      │
  │  • Anchor Corner Sweeps  : Forces model to learn min/max boundaries         │
  │  • Re-Amping / NAM Mode  : Process entire full-length tracks at fixed knobs │
  │  • Dataset Verification  : Audits audio for NaNs, clipping, and flat silence│
  └─────────────────────────────────────────────────────────────────────────────┘
```

---

## 📦 Installation

### Option 1: Standalone Download (No .NET Required)
1. Download `livespice-gen-v1.0.0-win-x64.zip` from **Releases**.
2. Unzip it to any folder.
3. Open PowerShell in that folder and run:
   ```powershell
   .\livespice-gen.exe --help
   ```

*(Optional)* To run `livespice-gen` from anywhere without typing `.\`:
```powershell
[Environment]::SetEnvironmentVariable("Path", $env:Path + ";$PWD", "User")
```

---

### Option 2: Build from Source

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download) and Git.

```bash
git clone --recurse-submodules https://github.com/YourUsername/LiveSPICE-Generator.git
cd LiveSPICE-Generator
```

Run the automated setup script:
* **Windows (PowerShell):**
  ```powershell
  .\setup.ps1
  ```
* **Linux / macOS (Bash):**
  ```bash
  ./setup.sh
  ```

---

## ⚡ 5-Minute Quickstart

Here is the complete workflow using any schematic (e.g. Boss Blues Driver or Tube Screamer):

```powershell
# 1. Discover all controls, inputs, and probe taps
livespice-gen controls "BOSS BD-2 Blues Driver.schx"

# 2. Generate a customized JSON spec template
livespice-gen spec "BOSS BD-2 Blues Driver.schx" bd2.spec.json

# 3. Preview the plan & runtime estimate before rendering
livespice-gen plan bd2.spec.json --input guitar_di.wav --budget 500

# 4. Render the dataset using 8 CPU threads in parallel
livespice-gen render bd2.spec.json ./dataset --input guitar_di.wav -j 8

# 5. Verify dataset integrity (NaNs, clipping, missing files)
livespice-gen verify ./dataset
```

---

## 🧠 Power-User Playbook: Exploiting Maximum Potential

### 1. The "Edge-of-Breakup" Focus (The Secret to Great Drive Models)
In analog overdrive pedals, 80% of the interesting tone happens in the first 20–30% of the Gain knob. If you sample uniformly, your dataset will be mostly oversaturated fuzz.

Tell the generator to focus **70% of its budget** on the breakup region:
```powershell
livespice-gen render bd2.spec.json ./dataset --input guitar.wav --focus Gain=0..0.25:70%
```

Or write custom stratified bands directly:
```powershell
livespice-gen render bd2.spec.json ./dataset --input guitar.wav --knob "Gain=0.0..0.2:1500,0.2..1.0:1000"
```

---

### 2. Taming Complex Tube Amps & Multi-Throw Switches
Vintage and high-gain preamps (e.g. Marshall JCM800, Mesa Mark IIC+) have discrete toggle switches (`SPDT`, `SP3T`, `SP5T`) and multi-gang push-pull pots.

* **Pin a switch to a specific mode**:
  ```powershell
  livespice-gen render amp.spec.json ./dataset --input guitar.wav --switch "Bright=1"
  ```
* **Sample only specific positions on a 3-way switch (positions 0 and 2, skipping 1)**:
  ```powershell
  livespice-gen render amp.spec.json ./dataset --input guitar.wav --switch "Mode=0,2"
  ```
* **Automatic Multi-Gang Unification**: Schematics with 4-gang and 5-gang linked switches (like `Pull Shift (++ mod) x4` or `Pull Lead x5`) are automatically grouped into single logical controls with synchronized states.

---

### 3. Dynamic Gain Staging (Simulating Pickup Variances)
Different guitars have vastly different pickup outputs (vintage single coils vs active humbuckers). Use random log-uniform gain staging to force the model to respond naturally to soft playing and hard picking:

```powershell
livespice-gen render circuit.spec.json ./dataset --input guitar.wav --gain-low -18.0 --gain-high 6.0
```

---

### 4. Extreme Boundary Anchors (`--anchors`)
Neural networks often fail at the exact extremes of travel (0% and 100%). Use `--anchors` to automatically inject extreme corner cases (all min, all noon, all max, alternating min/max):

```powershell
livespice-gen plan circuit.spec.json --input guitar.wav --budget 500 --anchors
```

---

### 5. Multi-Speaker & Probe Tap Routing (`--speaker`)
Schematics often contain multiple probe taps or speaker loads (e.g. Clean Out vs Lead Out, or Master Out vs Graphic EQ Out):

```powershell
# Target a specific probe component by name
livespice-gen render amp.spec.json ./dataset --input guitar.wav --speaker "S9"
```

---

### 6. Full-Track Re-Amping / NAM Mode (`process`)
Want to run a full 3-minute song or guitar solo through the schematic at fixed settings? Use `process`:

```powershell
livespice-gen process "Marshall JCM800 2203 Preamp.schx" solo_di.wav solo_amped.wav
```

---

## 🎛️ CLI Reference

### Commands
| Command | Description |
|---|---|
| `controls <circuit.schx>` | Discovers all pots, switches, multi-gangs, inputs, and speakers |
| `spec <circuit.schx> [out.json]` | Emits a customizable JSON specification template |
| `plan <spec.json \| circuit.schx in.wav>` | Analyzes excerpt distribution, bands, and gives time estimate |
| `render <spec.json [outdir] \| circuit.schx in.wav outdir>` | Runs multi-threaded simulation and writes dataset |
| `process <circuit.schx> <in.wav> [out.wav]` | Renders an entire audio track at fixed settings (NAM mode) |
| `verify <dataset_dir>` | Audits dataset metadata, audio files, NaNs, clipping, and silence |

### Options
| Flag | Description | Default |
|---|---|---|
| `-j, --threads <n>` | Number of parallel worker solvers | All CPU Cores |
| `--budget <n>` | Total number of render excerpts to create | Spec default |
| `--duration <s>` | Excerpt length in seconds | `15.0` |
| `--warmup <s>` | Pre-roll solver duration to settle DC bias pops | `0.05` |
| `--rate <hz>` | Output audio sample rate | `48000` |
| `--oversample <n>` | Oversampling multiplier (1, 2, 4, 8, 16, 32) | `2` |
| `--iterations <n>` | Newton-Raphson iteration limit per sample | `8` |
| `--normalize <peak>` | Master reference input peak normalization | `0.9` |
| `--gain <db>` | Fixed input drive level in dB | (Uses range) |
| `--gain-low <db>` | Minimum random drive level in dB | `-18.0` |
| `--gain-high <db>` | Maximum random drive level in dB | `6.0` |
| `--focus <spec>` | Quick area focus, e.g. `Gain=0..0.25:80%` | (None) |
| `--knob <spec>` | Explicit knob strata, e.g. `Volume=0..0.2:500,0.2..1:1000` | Spec default |
| `--switch <spec>` | Explicit switch positions, e.g. `Bright=1` or `Mode=0,2` | Spec default |
| `--anchors` | Inject extreme boundary test corners | `false` |
| `--joint <mode>` | `independent` \| `grid` \| `sweep` \| `sweepRandom` | `independent` |
| `--time-range <s..s>`| Restrict audio excerpting to a time slice (e.g. `10..90`) | Full audio |
| `--spans <mode>` | `signal` (energy-gated, skips silence) or `all` | `signal` |
| `--speaker <name>` | Target speaker component name in schematic | (Sums all) |
| `--resume <bool>` | Safely resume interrupted renders | `true` |
| `--limit <n>` | Render only the first N jobs (for fast testing) | All |

---

## 📋 JSON Specification Schema (`spec.json`)

```json5
{
  "circuit": "BOSS BD-2 Blues Driver.schx",
  "budget": 2500,
  "duration": 15.0,
  "joint": "independent",       // "independent" | "grid" | "sweep" | "sweepRandom"
  "spans": "signal",            // "signal" (skip silence) | "all"
  "timeRange": [10.0, 120.0],   // Optional: restrict excerpt window in seconds
  "render": {
    "sampleRate": 48000,
    "oversample": 16,           // 1 to 32x oversampling
    "iterations": 32,           // Newton-Raphson max iterations
    "normalize": 0.9,           // Master reference normalization
    "warmup": 0.05,             // 50ms DC transient settling
    "speaker": "S1"             // Target probe tap (optional)
  },
  "sampling": {
    "gainDb": [-18.0, 6.0],     // Log-uniform drive level range
    "seed": 42
  },
  "knobs": {
    "Level": { "bands": [ [0.0, 1.0] ] },
    "Tone":  { "bands": [ [0.0, 1.0] ] },
    "Gain": {
      "bands": [
        { "from": 0.0, "to": 0.2, "count": 1500, "note": "edge-of-breakup focus" },
        { "from": 0.2, "to": 1.0, "count": 1000, "note": "high saturation" }
      ]
    },
    "BrightSwitch": {
      "positions": [0, 1]       // Discrete switch sampling
    }
  }
}
```

---

## 📂 Output Dataset Structure

Generated datasets are 100% compliant with standard neural network loaders:

```text
my_dataset/
├── manifest.jsonl            # Master metadata & ground-truth table
├── input/
│   └── normalized.wav        # Standardized reference audio
└── renders/
    ├── Circuit_L10_T50_G20_t0.00-15.00s.wav
    ├── Circuit_L80_T25_G90_t15.00-30.00s.wav
    └── ...
```

### Manifest Format (`manifest.jsonl`)
* **Header line**: Records global simulation settings, hashes of input audio and circuit schematic, oversample rates, and solver parameters.
* **Row lines**:
  ```json
  {
    "id": "BOSSBD2BluesDriver_L50_T50_G50_t0.00-15.00s",
    "status": "ok",
    "file": "BOSSBD2BluesDriver_L50_T50_G50_t0.00-15.00s.wav",
    "knobs": { "Level": 0.5, "Tone": 0.5, "Gain": 0.5 },
    "offset_s": 0.0,
    "duration_s": 15.0,
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

## 🔬 Downstream Neural Training

To train directly on this dataset using PyTorch / RTNeural:
1. Load `input/normalized.wav` and scale it by each row's `input_gain`.
2. Feed the audio into your model conditioned on the normalized `knobs` dictionary values (both continuous floats and discrete switch integers).
3. Compute loss (e.g. Multi-Resolution STFT + ESR) against the target WAV in `renders/`.

---

## 🛠️ Publishing Releases

To compile standalone single-file binaries:

* **Windows:** `dotnet publish src/LiveSPICE-Generator.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64`
* **Linux:** `dotnet publish src/LiveSPICE-Generator.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish/linux-x64`
* **macOS:** `dotnet publish src/LiveSPICE-Generator.csproj -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o publish/osx-arm64`

Zip the output folder (including `Components/`) and attach it to your GitHub Release!

---

## 📜 License & Credits

* Licensed under the [MIT License](LICENSE).
* Core SPICE simulation engine powered by [LiveSPICE](https://github.com/dsharlet/LiveSPICE) by Dmitry Sharlet.
