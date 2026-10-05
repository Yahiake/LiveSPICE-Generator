# LiveSPICE-Generator (`livespice-gen`)

A batch simulator and dataset generator for LiveSPICE circuit schematics (`.schx`).

Point it at a schematic, a dry input WAV, and a knob/switch budget. It enumerates the
circuit's controls itself, draws a stratified sample of control settings, renders each
setting through the real LiveSPICE solver, and writes 32-bit float WAVs plus a
`manifest.jsonl` index describing exactly how each one was made.

It is built for making training data for neural audio models (RTNeural, NAM, PyTorch
GRU/LSTM) out of analog circuits: overdrive pedals, fuzz boxes, tube preamps, active
tone stacks.

---

## Table of contents

1. [What it does](#what-it-does)
2. [Install](#install)
3. [Quick start](#quick-start)
4. [CLI reference](#cli-reference)
5. [Spec file schema](#spec-file-schema)
6. [Joint sampling modes](#joint-sampling-modes)
7. [Dataset layout and manifest](#dataset-layout-and-manifest)
8. [What was verified](#what-was-verified)
9. [Known limitations](#known-limitations)
10. [Measured performance](#measured-performance)
11. [Bundled examples](#bundled-examples)
12. [Attribution](#attribution)

---

## What it does

```
circuit.schx  +  input.wav  +  spec.json
        |
        v
  control discovery      pots, tapers, switches, multi-gangs, speakers, inputs
        |
        v
  plan                  stratified knob/switch sample + energy-gated excerpt placement
        |
        v
  render                N worker threads, each with its own circuit graph + JIT solver
        |
        v
  dataset/
    manifest.jsonl      every row: knobs, gains, offset, and measured audio stats
    input/normalized.wav
    renders/*.wav
```

You can skip the spec file entirely and drive it from the command line, which is what
most of the examples below do.

---

## Install

### Pre-built (what was tested here)

```
livespice-gen.exe     self-contained single-file executable, no .NET needed
Components/
        Tubes.xml           <- this is the ONLY file actually shipped
```


### From source

Needs the .NET 10.0 SDK. The solution references LiveSPICE as a sibling checkout
(`../LiveSPICE`), so clone LiveSPICE next to this repo:

```bash
git clone --recurse-submodules <this-repo>
dotnet build LiveSPICE-Generator.sln -c Release
```

Verified: builds clean, 0 warnings, 0 errors.

```bash
# or publish your own single-file binary
dotnet publish src/LiveSPICE-Generator.csproj -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -o publish/win-x64
```

---

## Quick start

```bash
./livespice-gen.exe

# 1. What can this circuit do?
$GEN controls "examples/circuits/Big Muff Pi.schx"
#   3 potentiometer(s), 0 switch(es), 3 total interactive control(s)
#   inputs  : Input (V0dBFS = 1 V)

# 2. See the plan without rendering anything
$GEN plan "examples/circuits/Big Muff Pi.schx" input.wav \
    --budget 100 --duration 2.0 --oversample 16 --iterations 32

# 3. Render it
$GEN render "examples/circuits/Big Muff Pi.schx" input.wav ./dataset \
    --budget 100 --duration 2.0 --oversample 16 --iterations 32 -j 8

# 4. Index check
$GEN verify ./dataset
```

### Working knobs directly, without a spec

```bash
# put 70% of the budget in the bottom 25% of the drive knob (edge of breakup)
$GEN render fuzz.schx input.wav ./ds --budget 2000 --duration 2.0 \
    --knob "Sustain=0.0..0.25:1400,0.25..1.0:600" \
    --oversample 16 --iterations 32 -j 8

# pin the knobs, sweep only input drive (NAM-style re-amp)
$GEN render fuzz.schx input.wav ./ds --budget 1 --duration 180 --spans all \
    --knob "Sustain=0.45" --knob "Tone=0.5" --knob "Volume=0.75"

# switches that the spec forgot get enumerated automatically, evenly
$GEN render jcm800.schx input.wav ./ds --budget 1800 --duration 2.0
#   note: C1 [switch] was not in the spec; sampled across all 3 positions [0..2]
#   C1  pos=0  6 renders (33.3%)   ... etc.
```

### `process` — one fixed setting over a whole file

```bash
$GEN process circuit.schx full_di.wav out.wav \
    --knob "Sustain=0.45" --warmup 0.5 --oversample 16
```

This is the fastest path to NAM data. Measured: 10.00 s of Big Muff audio in 5.9 s
(1.7x realtime).

---

## CLI reference

### Commands

| Command | Aliases | What it does |
|---|---|---|
| `controls` | `knobs` | Lists every interactive control, plus inputs and speaker probes. |
| `spec` | `template` | Writes a ready-to-edit JSON spec template for a schematic. |
| `plan` | — | Full dry run: knob bands, switch states, excerpt positions, ETA. Renders nothing. |
| `render` | — | Renders. Either `render spec.json outdir` or `render circuit.schx in.wav outdir`. |
| `process` | — | Renders one fixed setting across an entire input file. Writes one WAV. |
| `verify` | — | Checks the manifest against disk. **Does not re-analyse audio.** |

### Options

| Option | Argument | Real default | Notes |
|---|---|---|---|
| `-j`, `--threads`, `--j` | `<int>` | all cores | Worker count. See [thread scaling](#measured-performance). |
| `--budget` | `<int>` | `0` → **1000** | See the trap below. |
| `--duration` | `<float>` | `15.0` | Seconds of audio per render. |
| `--warmup` | `<float>` | `0.05` | **Inadequate for tube stages — see limitations.** |
| `--rate` | `<int>` | `48000` | Output rate. Input is resampled if needed. |
| `--oversample` | `<int>` | **`2`** | Range 1..64. Docs previously said 16. |
| `--iterations` | `<int>` | **`8`** | Newton-Raphson iteration cap. Docs previously said 32. |
| `--normalize` | `<float>` | `0.9` | Target peak of the master reference input. `0` disables. |
| `--gain` | `<float>` | none | Fixed drive in dB; sets both bounds. |
| `--gain-low` | `<float>` | `-18.0` | Lower bound of the log-uniform drive distribution. |
| `--gain-high` | `<float>` | `6.0` | Upper bound. |
| `--time-range` | `<t0..t1>` or `<t0,t1>` | full file | Crops the excerpt window. Works; logs a `crop:` line. |
| `--spans` | `signal` \| `all` | `signal` | `signal` uses an energy gate to skip silence. |
| `--joint` | `independent` \| `grid` \| `sweep` \| `sweepRandom` | `independent` | See [joint modes](#joint-sampling-modes). |
| `--knob` | `<name>=<spec>` | from spec | e.g. `--knob "Gain=0..0.2:1000,0.2..1.0:1500"`. |
| `--switch` | `<name>=<pos,...>` | from spec | e.g. `--switch "C1=0,2"`. |
| `--speaker` | `<name>` | sum all | e.g. `--speaker S9`. Errors on an unknown name. |
| `--focus` | `<name>=<r0>..<r1>:<pct>` | none | e.g. `--focus "Distortion=0..0.3:70%"`; remainder is labelled. |
| `--anchors` | flag | off | Adds 5 renders: all-min, all-max, all-noon, and two alternating corners. |
| `--seed` | `<int>` | `0` | Fully deterministic. Verified. |
| `--resume` | `<bool>` | `true` | Skips IDs already in the manifest. |
| `--limit` | `<int>` | none | Stop after N jobs. Good for smoke tests. |
| `--dry-run` | flag | `false` | Equivalent to `plan`. Writes nothing. |
| `--quiet` | flag | `false` | Suppresses per-job progress. |
| `--stride` | `<float>` | — | **Accepted and silently ignored.** See limitations. |
| `--split` | `<a/b/c>` | — | **Accepted and silently ignored.** See limitations. |

#### The `--budget` trap

`--budget` defaults to `0`, not `2500`. If you give a spec weighted bands but no
`--budget`, the sampler silently substitutes **1000** (`src/Sampler.cs:178`). You get
1000 renders and no warning that your number was ignored.

If you set explicit counts on some knobs' bands, they must agree, or you get a hard error
telling you to either set `budget` or make the counts sum the same way.

---

## Spec file schema

A spec makes an experiment reproducible. Generate a correct skeleton with `spec`:

```bash
$GEN spec "circuits/Mark IIC+(++) Preamp_NOGEQ.schx" my.spec.json
```

### Where each key actually has to live

**This is the single biggest source of silent misconfiguration.** The parser reads some
keys from the top level and some from nested objects, and it does not warn about the wrong
location.

| What you want | Key that **works** | Key that is **silently ignored** |
|---|---|---|
| Joint sampling mode | `sampling.joint` | `joint` |
| Excerpt placement | `sampling.input.spans` | `spans` |
| Time window crop | `sampling.timeRange` | `timeRange` |
| Drive range in dB | `sampling.input.gainDb` | `sampling.gainDb` |
| Sample rate / oversample / iterations | `render.sampleRate`, `render.oversample`, `render.iterations` | — |
| Normalize / warmup / speaker | `render.normalize`, `render.warmup`, `render.speaker` | — |
| Band counts | `knobs.<Name>.bands[].count` | `knobs.<Name>.bands[].weight` relative form is supported but needs a budget |

Proof, measured:

```
top-level "joint":"sweep"        ->  plan: ... Independent joint     (ignored)
"sampling"."joint":"sweep"       ->  plan: ... Sweep joint          (honoured)

"sampling"."gainDb":[-3,3]       ->  drew -14.20 / -15.49 / -16.54 dB   (ignored)
"sampling"."input"."gainDb":..   ->  drew exactly -3.00 dB and +3.00 dB (honoured)
```

### Example

```json5
{
  "name": "MarkIIC-lead",
  "circuit": "circuits/Mark IIC+(++) Preamp_NOGEQ.schx",
  "input": "audio/di.wav",
  "budget": 2400,
  "duration": 2.5,

  "render": {
    "sampleRate": 48000,
    "oversample": 16,          // remember: default is 2
    "iterations": 32,          // remember: default is 8
    "normalize": 0.9,
    "warmup": 0.5,             // 0.5 for tube stages, not 0.05
    "speaker": "S9"
  },

  "sampling": {
    "joint": "independent",                       // <- under sampling
    "timeRange": [0.0, 180.0],                    // <- under sampling
    "seed": 42,
    "input": {
      "spans": "signal",                          // <- under sampling.input
      "gainDb": [-18.0, 6.0]                      // <- under sampling.input
    }
  },

  "knobs": {
    "Lead Drive": {
      "bands": [
        { "from": 0.0, "to": 0.3, "count": 1500, "note": "crunch boundary" },
        { "from": 0.3, "to": 1.0, "count": 900,  "note": "lead saturation" }
      ]
    },
    "Pull Lead": { "positions": [0, 1] },
    "Pull Shift (++ mod)": { "pin": 1 },
    "Master 1": { "bands": [ { "from": 0.2, "to": 1.0, "count": 2400 } ] }
  }
}
```

Multi-gang switches collapse into one logical control (`Pull Lead` drives
`Pull Lead + Pull Lead1 + Pull Lead2 x3`). Pinning one with `"pin": 1` pins all gangs.

### Sampling is genuinely stratified

Within each band the sampler splits the band into `n` equal sub-intervals and draws one
jittered sample from each (`src/Sampler.cs:248-250`). So continuous knobs really do get
uniform coverage, with band counts honoured exactly — not clustered pseudo-random. Band
counts in `plan` output are exact, not approximate:

```
Distortion  [knob]  [0,0.3)    7   70.0%  focus 70%
Distortion  [knob]  [0.3,1)    3   30.0%  remainder 30%
```

---

## Joint sampling modes

| Mode | Behaviour | Use for |
|---|---|---|
| `independent` | Each knob drawn independently from its stratified bands. Default. | Maximum space filling. |
| `grid` | Cartesian product of the bands. | 1–3 control circuits where you want every combination. |
| `sweep` | One knob ramps 0→1 at a time, others pinned. | Training recurrent models on continuous gestures. |
| `sweepRandom` | Same ramp structure, other knobs redrawn randomly per step. | Gesture + random backdrop. |

### `sweep` and `sweepRandom` will not fill your budget

They produce `numKnobs × min(sweepSteps, budget/numKnobs)` renders, with `sweepSteps`
defaulting to **24** (`src/Spec.cs:108`). Measured on a 3-knob circuit:

| `--budget` | `independent` | `sweep` | `sweepRandom` |
|---:|---:|---:|---:|
| 50 | 50 | 48 | 48 |
| 100 | 100 | 72 | 72 |
| 300 | 300 | 72 | 72 |
| 1000 | 1000 | 72 | 72 |
| 2500 | 2500 | **72** | **72** |

Asking for 2500 renders gets you 72, silently. There is no `--sweep-steps` CLI flag; you
have to set `sweepSteps` in the spec.

At low budgets the ramps degenerate further. At `--budget 8` you get 6 renders: each knob
at min and max, nothing in between.

Use the `origin` field in the manifest to find the ramp runs — they are tagged
`sweep <Knob> (others pinned)`.

---

## Dataset layout and manifest

```
dataset/
  manifest.jsonl
  input/
    normalized.wav
  renders/
    BigMuffPi_S28_T60_V178_t0.20-0.40s.wav
    ...
```

`manifest.jsonl` is JSON Lines. Line 1 is a header, every subsequent line is one render.

### Header

```json
{
  "type": "dataset",
  "circuit": "Big Muff Pi.schx",
  "circuit_sha256": "c5e2b9f9...",
  "input": "sample_di.wav",
  "input_sha256": "09c64142...",
  "input_v0dBFS": 1.0,
  "sample_rate": 48000,
  "oversample": 2,
  "iterations": 8,
  "duration_s": 0.5,
  "generator": "livespice-gen",
  "spec_sha256": "1c0ff198...",
  "knobs": ["Sustain", "Tone", "Volume"],
  "split": "all"
}
```

Two corrections to the old docs: `input` is the **original source filename**, not
`normalized.wav`; and every row carries `split`.

### Row

```json
{
  "id": "BigMuffPi_S28_T60_V178_t0.20-0.40s",
  "status": "ok",
  "file": "BigMuffPi_S28_T60_V178_t0.20-0.40s.wav",
  "knobs": { "Sustain": 0.28, "Tone": 0.6, "Volume": 0.178 },
  "offset_s": 0.2,
  "duration_s": 0.2,
  "input_gain": 0.12401,
  "origin": "Sustain[0,0.5) Tone[0,0.5) Volume[0.5,1)",
  "split": "all",
  "mean": -0.0016,
  "peak": 0.5854,
  "rms": 0.2444,
  "clipped": 0,
  "nonfinite": 0,
  "flat": false
}
```

`origin` tells you which band each knob came from, which makes the manifest enough to
re-derive the sampling design. `status: "failed"` rows carry an `error` string and no
`file`.

IDs are built by rounding knobs to 2 decimals. Circuits with very few knobs can collide;
`bigmuff-v1` was checked clean (1631 rows, 1631 unique IDs).

---

## What was verified

Everything in this section was executed against the shipped binary and passed.

**Numerical parity.** Four Big Muff renders at 48 kHz / oversample 16 / 32 iterations were
compared sample-for-sample against LiveSPICE's own `Tests/Render.cs` harness with matched
knobs, gain and offset:

```
max abs sample difference : 0.000e+00
ESR                       : 0.000e+00
4 of 4 renders bit-identical
```

**Determinism.** Same seed twice → identical manifests. Different seed → different knobs.
Thread count does not change results: `-j 1` and `-j 8` produce the same 8 jobs.

**Parallelism is safe.** Each worker builds its own circuit graph, analysis and JIT
solver. No shared mutable state.

**Resume.** Render 4 of 8, re-run the same command → `4 ok, 4 skipped`, 8 rows, 8 files,
`verify` clean. Resume correctly refuses to mix incompatible runs:

```
error: this dataset was made with oversample 2, but this run has 16.
error: this dataset was made with circuit_sha256 c5e2b9f9..., but this run has 875737b5...
```

**Failure isolation.** `Triode stage` at 12 dB drive failed all 12 renders with
`Failed to eliminate differentials from system of equations`. Every failure was recorded
per-row, no partial WAVs were left behind, and the run exited cleanly. One bad knob
setting does not take down the batch.

**Control discovery.**

| Circuit | Reported |
|---|---|
| `Big Muff Pi.schx` | 3 pots, 0 switches, 3 controls, input `Input`, speaker `S1` |
| `Marshall JCM800 2203 preamp modded.schx` | 6 pots, 2 SP3T switches (C1, C2), 8 controls |
| `Mark IIC+(++) Preamp_NOGEQ.schx` | 7 pots, 12 switches, **14** logical controls |
| `Mark IIC+(++) Preamp_GEQ.schx` | 12 pots, 15 switches, **20** logical controls |

Tapers, multi-gang linkage and switch throw counts are all resolved correctly.

**Switch auto-enumeration.** A spec that omits `C1`/`C2` gets an even split across all
three positions, with an explicit note and a warning:

```
note : C1 [switch] was not in the spec; sampled across all 3 positions [0..2]
C1  pos=0  6 renders  33.3%
C1  pos=1  6 renders  33.3%
C1  pos=2  6 renders  33.3%
```

**Error handling is good.** All of these fail loudly with a useful message and exit 1:
a spec naming a control the circuit does not have; a missing input WAV; a duration longer
than the input; an unknown `--speaker`; a missing circuit file.

**Excerpt placement.** `--time-range 4..6` crops correctly (logs
`crop : restricted to timeRange [4.0s..6.0s] (4 start points available)`).
`--spans all` switches off energy gating as documented.

**Warmup removes the DC thump.** Mark IIC+ tube preamp, `Vol1=0.5`, first 50 ms vs full
file:

| `--warmup` | DC mean | first-50 ms RMS | first/full RMS |
|---:|---:|---:|---:|
| 0 | -1.13e-03 | 1.25e-01 | **8.89** |
| 0.05 (default) | -5.11e-04 | 6.16e-02 | **5.60** |
| 0.5 | -3.73e-06 | 1.46e-02 | **1.47** |
| 2.0 | +1.08e-06 | 1.46e-02 | **1.47** |

---

## Known limitations

These are the things that will bite you. Each one was measured.

### `--oversample` does not give you the anti-aliasing the docs promised

The previous README claimed a proper anti-aliasing filter and ">90 dB" of aliasing
rejection at oversample 32. Neither is true. LiveSPICE decimates by **summing the internal
samples and dividing by the oversample factor** (`Simulation.cs:385` and `:398`) — that is
a boxcar average, a first-order low-pass rolling off at **6 dB per octave** above 12 kHz.

Measured on a hard-clipped Big Muff (`Sustain=0.85`), each setting compared against
oversample 32:

| `--oversample` | residual vs. oversample 32 |
|---:|---:|
| 1 | **-23.5 dB** |
| 2 | **-29.9 dB** |
| 4 | -36.4 dB |
| 8 | -43.6 dB |
| 16 | **-51.3 dB** |

Clean aliasing rolls off at 48 dB per octave. This rolls off at 6. Reaching -90 dB would
take roughly 10,000x oversampling, which is not available.

So: raising `--oversample` does help, monotonically, about 6 dB per doubling. It just does
not reach the advertised figure, and the **default of 2 leaves about 30 dB of aliasing
error** on a clipping circuit. If aliasing matters to your model, either push
`--oversample` as high as you can afford or anti-alias downstream.

### The default `--warmup` is not enough for tube circuits

`0.05 s` cuts the startup transient by about a third (table above). A tube stage needs
roughly `0.5 s` to reach a clean DC operating point. Use `--warmup 0.5` for anything with
valves.

### `verify` trusts the manifest

`verify` reads `flat`, `nonfinite` and `clipped` **from the manifest row**
(`src/Commands.cs:635-637`). It only checks that the referenced file **exists**. It never
opens the WAV.

Injected `NaN`, `+Inf`, `-Inf`, `NaN` into the first four samples of a rendered file:

```
rows  : 4 ok, 0 failed, 0 missing files
audio : 0 flat, 0 with non-finite samples, 2 clipped at least once

clean. 4 renders agree with their files.
exit=0
```

It also printed `clean.` and exited **0** on a dataset where all 12 renders had failed,
and it reported `clean` on a dataset containing clipped renders. Only genuinely missing
files make it exit non-zero.

Treat `verify` as a completeness check, not an integrity check. If you care, audit the
audio yourself.

### `--stride` and `--split` are accepted and ignored

Both parse cleanly and neither does anything.

```
$ ... --stride 0.25 --duration 1.0
excerpts: 10 start points ... stride 1.0s        <- still 1.0
```

`--stride` is read from a spec file (`spec.stride`) and honoured by the sampler, but
`ApplyOverrides` never reads it off the command line. `--split "80/10/10"` renders
normally and every row comes out `"split": "all"`. **The generator never produces a
train/val/test split.** Do that downstream.

### `--joint sweep` / `sweepRandom` cap out at 72 renders

See the table in [joint modes](#joint-sampling-modes). Budget 2500 in, 72 out, no warning.

### Dead and inert settings

* `silenceBelowDb` is declared, parsed and range-checked in `src/Spec.cs` — and then
  never read by any sampler or renderer. Setting it does nothing.
* `ExplicitSettings`, `CsvHeader`, `CsvRow` and `MissingFiles` are defined and never called.
* `include/CircuitDspNode.hpp` cannot run inference — see
  [its own section](#includecircuitdspnodehpp).

### Wrong key locations in the shipped examples

See [the table in the schema section](#where-each-key-actually-has-to-live). Three of the
twelve bundled specs use keys the parser ignores. Always read `plan` output before
committing to a long render.

### Repo hygiene

* `.github/workflows/ci.yml` runs `plan examples/bd2-breakup-focus.spec.json`. **That file
  does not exist.** CI fails on every push.
* `.gitignore` contains `*.wav`, so `examples/audio/sample_di.wav` — which every bundled
  spec depends on — is not tracked. A fresh clone cannot run any example until you supply
  your own DI audio.

### Integration method

LiveSPICE hard-codes `IntegrationMethod.BackwardDifferenceFormula2`
(`TransientSolution.cs:133`). The previous README said "Trapezoidal or Backward Euler",
which is neither.

### There is no `training/` directory

The previous README documented a full `training/train.py prepare | train | export`
PyTorch pipeline. **None of it is in this repo.** This tool produces the dataset and stops.
You write the trainer.

---

## Measured performance

### Thread scaling — not near-linear

32 renders, Big Muff, 1.0 s each, oversample 4, 32 iterations, on 12 logical cores:

| `-j` | wall time | jobs/s | speedup |
|---:|---:|---:|---:|
| 1 | 13.54 s | 2.36 | 1.00x |
| 2 | 9.11 s | 3.51 | 1.49x |
| 4 | 6.53 s | 4.90 | 2.07x |
| 8 | 5.85 s | 5.47 | **2.32x** |
| 12 | 6.11 s | 5.24 | 2.22x |

2.3x on 12 cores, and going past 8 workers makes it slower. The previous README's claim of
"near-linear speedup across all CPU cores" is not achievable. Plan for ~2-2.5x, and do not
expect `-j 12` to help.

### Throughput

`process`, 10.00 s of Big Muff, oversample 2 / 8 iterations: **5.9 s** (1.7x realtime).

`render`, Big Muff, 2.0 s clips, oversample 16 / 32 iterations, single thread:
**0.38 jobs/s**.

| Circuit | Settings | Measured |
|---|---|---|
| Big Muff Pi | os2, iter8, 1.0 s, `-j 8` | 5.5 jobs/s |
| Big Muff Pi | os16, iter32, 2.0 s, `-j 1` | 0.38 jobs/s |

The old README's "Verified Speed" column listed Big Muff at 1.8 jobs/s with no stated
settings; measured at its own stated settings it is 0.38 jobs/s, about 5x optimistic.
Those numbers were not reproducible and have been removed rather than restated.

Budget accordingly: a 2400-clip, 2.5 s, oversample-16 dataset of a tube preamp is a
multi-hour job. Use `plan` for an ETA and `--limit` to smoke-test.

---

## Bundled examples

Twelve spec files ship in `examples/`.

| Spec | Circuit |
|---|---|
| `01_bd2_breakup_stratified.spec.json` | BOSS BD-2 Blues Driver |
| `02_ts9_classic_drive.spec.json` | Ibanez TS-9 |
| `03_bigmuff_fuzz_saturation.spec.json` | Big Muff Pi |
| `04_rat_opamp_clipper.spec.json` | Pro Co Rat |
| `05_jcm800_modded_switches.spec.json` | Marshall JCM800 2203 modded |
| `06_jcm800_classic_5knob.spec.json` | Marshall JCM800 2203 classic |
| `07_mark2c_multigang_pushpull.spec.json` | Mark IIC+(++) NOGEQ |
| `08_mark2c_geq_flagship.spec.json` | Mark IIC+(++) GEQ |
| `09_fender_5e3_tweed.spec.json` | Fender 5E3 Tweed |
| `10_crybaby_wah_sweep.spec.json` | Dunlop Cry Baby GCB-95 |
| `11_rockerverb_highgain.spec.json` | Rockerverb high gain |
| `12_sd1_asymmetrical_drive.spec.json` | Superfuzz asymmetric drive |

Circuit control counts, as actually reported:

| Circuit | Pots | Switches | Logical controls |
|---|---:|---:|---:|
| Big Muff Pi | 3 | 0 | 3 |
| Marshall JCM800 modded | 6 | 2 | 8 |
| Mark IIC+ NOGEQ | 7 | **12** | 14 |
| Mark IIC+ GEQ | 12 | **15** | 20 |

The old table said 7 and 8 switches for the two Mark circuits. The logical totals were
right; the switch counts were not.

The specs themselves need review before use — several use the ignored top-level `joint` /
`spans` / `timeRange` keys. Run `plan` on each and check the output.

---

## Attribution

* **LiveSPICE-Generator** — MIT, see `LICENSE`.
* **LiveSPICE core** — <https://github.com/dsharlet/LiveSPICE>, by **DSharlet**
  (Dillon Sharlet), MIT. `livespice-gen` links directly against `Circuit.dll` and
  `ComputerAlgebra.dll` and contains no circuit mathematics of its own.

Third-party circuit models are the property of their respective authors and are
distributed under whatever terms LiveSPICE ships them under.
