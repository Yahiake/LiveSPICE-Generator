// File: WaveIo.cs
//
// Provenance: copied verbatim from LiveSPICE's test harness, Tests/Wave.cs. LiveSPICE is
// MIT licensed, Copyright (c) 2013 Dillon Sharlet, so this file carries that licence:
// see LICENSE at the root of this repository. Only the namespace was changed.
//
// Why it is copied rather than referenced: in LiveSPICE this file belongs to the Tests
// project, which is an executable that also drags in a 2021 beta of System.CommandLine.
// A published generator should not have to take a dependency on somebody else's test
// runner to read a WAV file.
//
// The code is left byte-identical to the original on purpose. This is the component that
// decides what ends up on disk, and a WAV that the previous, working renderer wrote must
// stay writable by this one. Changes here are changes to the dataset format.

using System;
using System.IO;
using System.Text;

namespace LiveSpiceGen
{
    /// <summary>
    /// Minimal RIFF/WAVE reader and writer.
    ///
    /// Everything is normalized to mono doubles, nominally in the range -1..1, because
    /// that is what the simulation wants to be fed and what we want to store. Reading
    /// downmixes multi-channel input; writing always produces mono.
    /// </summary>
    public class Wave
    {
        public int SampleRate { get; private set; }
        public double[] Samples { get; private set; }

        public int Length { get { return Samples.Length; } }
        public double Seconds { get { return (double)Length / SampleRate; } }

        private Wave() { Samples = Array.Empty<double>(); }

        /// <summary>
        /// Wrap an existing sample array, for measuring rendered output without
        /// committing it to disk first.
        /// </summary>
        public static Wave FromSamples(int sampleRate, double[] samples)
        {
            var w = new Wave();
            w.SampleRate = sampleRate;
            w.Samples = samples;
            return w;
        }

        /// <summary>
        /// Measurements we record for every render, both as a sanity check on the
        /// output and as a cheap fingerprint of what the circuit actually did.
        /// </summary>
        public struct Measure
        {
            public double Mean;
            public double Peak;
            public double Rms;
            public int Clipped;       // samples at or beyond full scale
            public int NonFinite;     // NaN or infinity, i.e. numerical garbage
            public bool Flat;         // every sample identical, i.e. the circuit produced nothing

            public override string ToString()
            {
                return string.Format("mean={0:G4} peak={1:G4} rms={2:G4} clip={3} nonfinite={4}{5}",
                    Mean, Peak, Rms, Clipped, NonFinite, Flat ? " FLAT" : "");
            }
        }

        public Measure Analyze()
        {
            Measure m = new Measure();
            double sum = 0.0, sumsq = 0.0;
            double first = Samples.Length > 0 ? Samples[0] : 0.0;
            m.Flat = true;
            for (int i = 0; i < Samples.Length; ++i)
            {
                double x = Samples[i];
                if (double.IsNaN(x) || double.IsInfinity(x)) { m.NonFinite++; continue; }
                if (x != first) m.Flat = false;
                double a = Math.Abs(x);
                if (a > m.Peak) m.Peak = a;
                if (a >= 1.0) m.Clipped++;
                sum += x;
                sumsq += x * x;
            }
            int n = Samples.Length > 0 ? Samples.Length : 1;
            m.Mean = sum / n;
            m.Rms = Math.Sqrt(sumsq / n);
            return m;
        }

        public static Wave Read(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) throw new ArgumentException("filename required");
            byte[] bytes = File.ReadAllBytes(filename);
            if (bytes.Length < 12 ||
                Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
                Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
                throw new InvalidDataException(filename + ": not a RIFF/WAVE file.");

            int format = 0, channels = 0, sampleRate = 0, bits = 0;
            int dataOffset = -1, dataLength = 0;

            // Walk the chunk list. The order of chunks is not guaranteed, so we look for
            // both 'fmt ' and 'data' rather than assuming they come first.
            for (int p = 12; p + 8 <= bytes.Length;)
            {
                string id = Encoding.ASCII.GetString(bytes, p, 4);
                int size = BitConverter.ToInt32(bytes, p + 4);
                int body = p + 8;
                if (size < 0 || body > bytes.Length)
                    break;
                // Tolerate a truncated or lying data size rather than throwing.
                if (body + size > bytes.Length)
                    size = bytes.Length - body;

                if (id == "fmt ")
                {
                    if (size < 16)
                        throw new InvalidDataException(filename + ": malformed fmt chunk.");
                    format = BitConverter.ToUInt16(bytes, body + 0);
                    channels = BitConverter.ToUInt16(bytes, body + 2);
                    sampleRate = (int)BitConverter.ToUInt32(bytes, body + 4);
                    bits = BitConverter.ToUInt16(bytes, body + 14);
                    // WAVE_FORMAT_EXTENSIBLE hides the real format in the first two
                    // bytes of the sub-format GUID.
                    if (format == 0xFFFE && size >= 26)
                        format = BitConverter.ToUInt16(bytes, body + 24);
                }
                else if (id == "data")
                {
                    dataOffset = body;
                    dataLength = size;
                }

                p = body + size + (size & 1);
            }

            if (dataOffset < 0)
                throw new InvalidDataException(filename + ": no data chunk.");
            if (channels < 1)
                throw new InvalidDataException(filename + ": zero channels.");
            if (sampleRate <= 0)
                throw new InvalidDataException(filename + ": zero sample rate.");

            int bytesPerSample = bits / 8;
            int frameBytes = bytesPerSample * channels;
            int frames = frameBytes > 0 ? dataLength / frameBytes : 0;

            double[] interleaved = new double[frames * channels];
            int o = dataOffset;
            for (int i = 0; i < interleaved.Length; ++i, o += bytesPerSample)
            {
                if (format == 3 && bits == 32)
                    interleaved[i] = BitConverter.ToSingle(bytes, o);
                else if (format == 3 && bits == 64)
                    interleaved[i] = BitConverter.ToDouble(bytes, o);
                else if (format == 1 || format == 0xFFFE)
                {
                    switch (bits)
                    {
                        case 8:
                            interleaved[i] = bytes[o] / 128.0 - 1.0;
                            break;
                        case 16:
                            interleaved[i] = BitConverter.ToInt16(bytes, o) / 32768.0;
                            break;
                        case 24:
                            // Shift up then arithmetic-shift back down to sign extend.
                            interleaved[i] = ((bytes[o] | (bytes[o + 1] << 8) | (bytes[o + 2] << 16)) << 8 >> 8) / 8388608.0;
                            break;
                        case 32:
                            interleaved[i] = BitConverter.ToInt32(bytes, o) / 2147483648.0;
                            break;
                        default:
                            throw new InvalidDataException(filename + ": unsupported bit depth " + bits + ".");
                    }
                }
                else
                {
                    throw new InvalidDataException(filename + ": unsupported WAVE format 0x" + format.ToString("X4") + ".");
                }
            }

            double[] mono = new double[frames];
            if (channels == 1)
            {
                Array.Copy(interleaved, mono, frames);
            }
            else
            {
                for (int i = 0; i < frames; ++i)
                {
                    double s = 0.0;
                    for (int c = 0; c < channels; ++c)
                        s += interleaved[i * channels + c];
                    mono[i] = s / channels;
                }
            }

            return new Wave() { SampleRate = sampleRate, Samples = mono };
        }

        /// <summary>
        /// Write mono samples. 32 bits means IEEE float, which is lossless and what we
        /// want for training data; 24 or 16 bits means integer PCM.
        /// </summary>
        public static void WriteMono(string filename, double[] samples, int sampleRate, int bits)
        {
            if (bits != 32 && bits != 24 && bits != 16)
                throw new ArgumentException("bits must be 16, 24 or 32, not " + bits);
            bool isFloat = bits == 32;

            int bytesPerSample = bits / 8;
            int dataBytes = samples.Length * bytesPerSample;
            using (FileStream f = new FileStream(filename, FileMode.Create, FileAccess.Write))
            using (BinaryWriter w = new BinaryWriter(f))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + dataBytes);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));

                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);
                w.Write((short)(isFloat ? 3 : 1));         // format tag
                w.Write((short)1);                         // channels
                w.Write((int)sampleRate);
                w.Write(sampleRate * bytesPerSample);      // byte rate
                w.Write((short)bytesPerSample);            // block align
                w.Write((short)bits);

                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataBytes);

                byte[] buf = new byte[bytesPerSample];
                for (int i = 0; i < samples.Length; ++i)
                {
                    double x = samples[i];
                    if (isFloat)
                    {
                        byte[] b = BitConverter.GetBytes((float)x);
                        w.Write(b, 0, 4);
                    }
                    else
                    {
                        double scaled = x * (bits == 16 ? 32768.0 : 8388608.0);
                        int v = (int)Math.Round(scaled);
                        int lo = v & 0xFF, mid = (v >> 8) & 0xFF, hi = (v >> 16) & 0xFF;
                        buf[0] = (byte)lo; buf[1] = (byte)mid;
                        if (bits == 24) buf[2] = (byte)hi;
                        w.Write(buf, 0, bytesPerSample);
                    }
                }
            }
        }

        /// <summary>
        /// Linear-interpolate to a new sample rate. Adequate for driving a nonlinear
        /// circuit and far simpler than a proper filter; the dataset records the
        /// resulting rate so nothing downstream has to guess.
        /// </summary>
        public Wave Resample(int newSampleRate)
        {
            if (newSampleRate == SampleRate || Length == 0)
                return this;

            double ratio = (double)SampleRate / newSampleRate;
            int count = (int)Math.Floor(Length / ratio);
            double[] s = new double[count];
            for (int i = 0; i < count; ++i)
            {
                double pos = i * ratio;
                int j = (int)pos;
                if (j >= Length - 1)
                {
                    s[i] = Samples[Length - 1];
                    continue;
                }
                double frac = pos - j;
                s[i] = Samples[j] * (1.0 - frac) + Samples[j + 1] * frac;
            }
            return new Wave() { SampleRate = newSampleRate, Samples = s };
        }

        public Wave Excerpt(double offsetSeconds, double seconds)
        {
            int start = (int)Math.Round(offsetSeconds * SampleRate);
            if (start < 0) start = 0;
            if (start > Length) start = Length;
            int count = (int)Math.Round(seconds * SampleRate);
            if (count < 0) count = 0;
            if (start + count > Length) count = Length - start;

            double[] s = new double[count];
            Array.Copy(Samples, start, s, 0, count);
            return new Wave() { SampleRate = SampleRate, Samples = s };
        }

        public string Describe()
        {
            Measure m = Analyze();
            return string.Format("{0} frames, {1} Hz, {2:F2} s | {3}",
                Length, SampleRate, Seconds, m);
        }
    }
}