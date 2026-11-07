using GenesisRecomp.Machine;

namespace GenesisRecomp.Tools;

/// <summary>
/// Sound self-test, no ROM needed: programs the sound chips directly, renders a few seconds and writes a WAV file.
///   0.0-1.0 s  YM2612 channel 1: a pure sine at A4 (440 Hz)
///   1.0-2.0 s  YM2612 channels 1-3: a C major chord with a decaying envelope
///   2.0-3.0 s  PSG: square wave at 440 Hz, then noise
///   3.0-4.0 s  YM2612 DAC: a 1 kHz square wave streamed as 8-bit samples
/// Usage: GenesisRecomp --audio-test out.wav
/// </summary>
public static class AudioTest
{
    private const int Rate = 48000;

    public static int Run(string path)
    {
        var snd = new SoundSystem { Capture = true, OutputRate = Rate };
        long master = 0;
        var pcm = new List<short>();

        void Advance(double seconds)
        {
            long target = master + (long)(seconds * 53693175.0);
            // Advance in small steps and collect, as the real machine does each frame.
            while (master < target)
            {
                master = Math.Min(target, master + 53693175 / 60);
                snd.SyncTo(master);
                var (buf, frames) = snd.Collect();
                for (int i = 0; i < frames * 2; i++) pcm.Add(buf[i]);
            }
        }

        var ym = snd.Ym;
        void Reg(int part, int reg, int val) { ym.Write(part * 2, (byte)reg); ym.Write(part * 2 + 1, (byte)val); }

        // A simple "organ" patch on channel c: algorithm 7 (all four operators are carriers) would be louder,
        // so use algorithm 4 with only the carriers audible. For a pure tone: operator 4 only, algorithm 7.
        void PureTone(int ch, int fnum, int block)
        {
            int part = ch / 3, c = ch % 3;
            Reg(part, 0xB0 + c, 0x07);                       // algorithm 7, no feedback
            Reg(part, 0xB4 + c, 0xC0);                       // both speakers
            for (int op = 0; op < 4; op++)
            {
                int off = c + op * 4;                        // register slot order S1,S3,S2,S4
                bool isS4 = op == 3;
                Reg(part, 0x30 + off, 0x01);                 // detune 0, multiple 1
                Reg(part, 0x40 + off, isS4 ? 0x10 : 0x7F);   // total level: S4 audible, others silent
                Reg(part, 0x50 + off, 0x1F);                 // attack rate max
                Reg(part, 0x60 + off, 0x00);                 // no decay
                Reg(part, 0x70 + off, 0x00);
                Reg(part, 0x80 + off, 0x0F);                 // release rate max
            }
            Reg(part, 0xA4 + c, (block << 3) | (fnum >> 8));
            Reg(part, 0xA0 + c, fnum & 0xFF);
        }

        void KeyOn(int ch) => Reg(0, 0x28, 0xF0 | (ch < 3 ? ch : ch + 1));
        void KeyOff(int ch) => Reg(0, 0x28, ch < 3 ? ch : ch + 1);

        // ---- 1. A4 sine: fnum for 440 Hz at block 4 -> fnum = f * 2^(21-block) / (clock/144) ... = 440 * 2^17 / 53267 ~ 1083 -> use block 5: 541
        int FnumFor(double hz, int block) => (int)Math.Round(hz * (1 << (21 - block)) / Ym2612.SampleRate);
        PureTone(0, FnumFor(440, 5), 5);
        KeyOn(0);
        Advance(0.9);
        KeyOff(0);
        Advance(0.1);

        // ---- 2. chord with a decay
        double[] chord = { 261.63, 329.63, 392.00 };
        for (int i = 0; i < 3; i++)
        {
            PureTone(i, FnumFor(chord[i], 4), 4);
            int part = 0, c = i;
            Reg(part, 0x50 + c + 12, 0x1F);                   // S4 attack
            Reg(part, 0x60 + c + 12, 0x0A);                   // S4 decay 1 rate
            Reg(part, 0x80 + c + 12, 0x6F);                   // sustain level 6, release 15
            KeyOn(i);
        }
        Advance(0.9);
        for (int i = 0; i < 3; i++) KeyOff(i);
        Advance(0.1);

        // ---- 3. PSG: tone on channel 0 at 440 Hz: period = clock / (32 * f)
        int period = (int)Math.Round(Sn76489.ClockHz / (32.0 * 440.0));
        snd.Psg.Write((byte)(0x80 | (period & 0x0F)));
        snd.Psg.Write((byte)(period >> 4));
        snd.Psg.Write(0x90);                                  // channel 0 volume 0 (loudest)
        Advance(0.5);
        snd.Psg.Write(0x9F);                                  // silence tone
        snd.Psg.Write(0xE4);                                  // noise, white, medium rate
        snd.Psg.Write(0xF0);                                  // noise volume 0
        Advance(0.4);
        snd.Psg.Write(0xFF);
        Advance(0.1);

        // ---- 4. DAC streaming a 1 kHz square wave at ~16 kHz sample rate
        Reg(0, 0x2B, 0x80);                                   // DAC on
        Reg(1, 0xB4 + 2, 0xC0);                               // channel 6 (second register bank, slot 2): both speakers
        double dacPeriod = 1.0 / 16000.0;
        for (int n = 0; n < 16000; n++)
        {
            int v = ((n / 8) & 1) == 0 ? 0xC0 : 0x40;       // 16000/16 = 1000 Hz
            Reg(0, 0x2A, v);
            Advance(dacPeriod);
        }
        Reg(0, 0x2B, 0x00);

        WriteWav(path, pcm.ToArray());
        Console.WriteLine($"Wrote {path}: {pcm.Count / 2} frames at {Rate} Hz ({pcm.Count / 2.0 / Rate:F2} s)");
        return 0;
    }

    private static void WriteWav(string path, short[] interleaved)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = interleaved.Length * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + bytes); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(bytes);
        foreach (short s in interleaved) w.Write(s);
    }
}
