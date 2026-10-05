# LiveSPICE-Generator Example Library

This directory contains a battle-tested, verified collection of 12 production-ready specification files (`.spec.json`), corresponding circuit schematics (`circuits/*.schx`), and reference DI guitar audio (`audio/sample_di.wav`).

Every example demonstrates specific solver configurations, stratified sampling strategies, discrete switch permutations, and downstream neural modeling techniques.

---

## Example Catalog

| # | Specification File | Circuit Name | Circuit Topology | Special Technique / Feature |
|---|---|---|---|---|
| **01** | [`01_bd2_breakup_stratified.spec.json`](01_bd2_breakup_stratified.spec.json) | BOSS BD-2 Blues Driver | JFET + Discrete Op-Amp Clipper | Dual-gang pot, 65% breakup focus, volume floor protection |
| **02** | [`02_ts9_classic_drive.spec.json`](02_ts9_classic_drive.spec.json) | Ibanez Tube Screamer TS-9 | Symmetrical Diode Soft-Clipper | Active RC mid-hump tone stack, clean boost into diode saturation |
| **03** | [`03_bigmuff_fuzz_saturation.spec.json`](03_bigmuff_fuzz_saturation.spec.json) | Electro-Harmonix Big Muff Pi | 4-Stage Cascaded BJT Fuzz | Mid-scoop tone filter sweep, extreme fuzz saturation, 32 NR iterations |
| **04** | [`04_rat_opamp_clipper.spec.json`](04_rat_opamp_clipper.spec.json) | Pro Co Rat | Symmetrical Hard Diode Clipper | Reverse-logarithmic Filter tone control, LM308 op-amp slew |
| **05** | [`05_jcm800_modded_switches.spec.json`](05_jcm800_modded_switches.spec.json) | Marshall JCM800 2203 Modded | High-Gain Cascaded 12AX7 Preamp | **Dual 3-Position (SP3T) Voicing Switches** (`C1`, `C2` throws [0, 1, 2]) |
| **06** | [`06_jcm800_classic_5knob.spec.json`](06_jcm800_classic_5knob.spec.json) | Marshall JCM800 2203 Preamp | Standard British 5-Knob Tube Preamp | Classic FMV passive tone stack interaction, broad dynamic pickup drive |
| **07** | [`07_mark2c_multigang_pushpull.spec.json`](07_mark2c_multigang_pushpull.spec.json) | Mesa Boogie Mark IIC+ (NOGEQ) | Multi-Channel High-Voltage Tube Amp | **4-Gang Switch** (`Pull Shift x4`), 3-gang switch, 240V probe tap (`S9`) |
| **08** | [`08_mark2c_geq_flagship.spec.json`](08_mark2c_geq_flagship.spec.json) | Mesa Boogie Mark IIC+ (GEQ) | Flagship Tube Preamp + Graphic EQ | **20 Interactive Controls** (12 pots, 8 switches incl. 5-band Graphic EQ) |
| **09** | [`09_fender_5e3_tweed.spec.json`](09_fender_5e3_tweed.spec.json) | Fender 5e3 Deluxe | Cathode-Biased Vintage Tweed Tube Amp | Dual-channel interactive volume loading (`Unused` load) & tube sag |
| **10** | [`10_crybaby_wah_sweep.spec.json`](10_crybaby_wah_sweep.spec.json) | Dunlop Cry Baby GCB-95 | Resonant LC Inductor Filter | **Continuous `sweep` Joint Mode** along foot treadle travel |
| **11** | [`11_rockerverb_highgain.spec.json`](11_rockerverb_highgain.spec.json) | Orange Rockerverb 50 Preamp | Modern 4-Stage Tube High-Gain Preamp | Dual-gang Gain pot, thick harmonic lead saturation |
| **12** | [`12_sd1_asymmetrical_drive.spec.json`](12_sd1_asymmetrical_drive.spec.json) | Boss Super Overdrive SD-1 | Asymmetrical Diode Clipper | Even-order harmonic generation, dynamic overdrive sweet spot |

---

## Quick-Start: Inspect, Plan & Render

All examples use self-contained relative paths to `circuits/` and `audio/`. You can invoke them directly from the repository root:

### 1. Inspect Circuit Controls
```bash
livespice-gen controls "examples/circuits/Marshall JCM800 2203 preamp modded.schx"
```

### 2. Dry-Run Plan
Verify sample distributions and estimated rendering time:
```bash
livespice-gen plan examples/01_bd2_breakup_stratified.spec.json
```

### 3. Parallel Render
Render 500 clips across 8 worker threads:
```bash
livespice-gen render examples/01_bd2_breakup_stratified.spec.json ./dataset_bd2 --budget 500 -j 8
```

### 4. Audit Dataset Integrity
```bash
livespice-gen verify ./dataset_bd2
```

---

## Detailed Circuit Deep-Dives

### 01: BOSS BD-2 Blues Driver (`01_bd2_breakup_stratified.spec.json`)
The Blues Driver uses discrete JFET stages and a dual-gang potentiometer (`G-A+G-B x2`) to simultaneously alter gain and low-frequency shelving. 
- **Sampling Strategy**: 65% of the 2,000-render budget is allocated to $w \in [0.0, 0.25)$ where diodes first conduct.
- **Volume Protection**: `Level` is restricted to $[0.15, 1.0)$ to prevent un-trainable quiet audio renders.

### 05: Marshall JCM800 Modded (`05_jcm800_modded_switches.spec.json`)
Modded high-gain Marshall featuring dual 3-throw switches (`C1` and `C2` with positions `0`, `1`, `2`) changing cathode bypass capacitors.
- **Switch Handling**: The engine visits all 9 discrete permutations ($3 \times 3$) with balanced sample allocation while stratifying the continuous tone and gain potentiometers.

### 08: Mesa Boogie Mark IIC+ Graphic EQ (`08_mark2c_geq_flagship.spec.json`)
The ultimate test of simulation complexity: 20 total interactive controls.
- **Graphic EQ**: 5 sliders (`80hz`, `240hz`, `750hz`, `2200hz`, `6600hz`) each mapped with full slider travel.
- **Multi-Gang Switches**: Pull Shift (++ mod) ties 4 switch stages together, and Pull Lead ties 5 switch contacts together.

### 10: Dunlop Cry Baby Wah (`10_crybaby_wah_sweep.spec.json`)
Demonstrates continuous `sweep` joint mode. Rather than independent random points, successive excerpts follow smooth parameter sweeps across the sweep range. This is specifically designed for recurrent neural networks (GRU / LSTM) to learn filter hysteresis and state persistence.
