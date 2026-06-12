using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CompressorPlugin;
using FluentAssertions;
using NAudio.Wave;
using SoundBoard.PluginApi;
using Xunit;

namespace CompressorPlugin.Tests;

public class CompressorTests
{
    // Host mixer contract: 48 kHz IEEE-float stereo.
    private static readonly WaveFormat Fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private static ISamplerInstance NewInstance() => new CompressorPlugin().CreateInstance();

    // ── Factory isolation ───────────────────────────────────────────

    [Fact]
    public void CreateInstance_returns_distinct_objects()
    {
        var plugin = new CompressorPlugin();
        var a = plugin.CreateInstance();
        var b = plugin.CreateInstance();
        a.Should().NotBeSameAs(b);
    }

    [Fact]
    public void Two_effects_do_not_share_envelope_state()
    {
        // Prime one effect with a loud tone so its envelope follower winds
        // up to a high gain-reduction state, then confirm a freshly-created
        // effect on a second instance still passes a quiet tone through at
        // full level (no cross-contamination via a stray static field).
        var loud = NewInstance();
        var loudFx = loud.CreateEffect(new SineProvider(Fmt, 1000, amplitude: 1.0f));
        Render(loudFx, frames: 48000); // wind up gain reduction, discard

        var fresh = NewInstance();
        // Quiet tone at -26 dBFS sits below the default -12 dB threshold,
        // so a correctly-isolated instance applies no reduction.
        var freshFx = fresh.CreateEffect(new SineProvider(Fmt, 1000, amplitude: 0.05f));
        var outRms = Rms(RenderSettled(freshFx));

        // Unit-amplitude sine has RMS ≈ 0.707, so 0.05 amplitude ≈ 0.0354.
        outRms.Should().BeApproximately(0.05f / MathF.Sqrt(2f), 5e-3f);
    }

    // ── Config round-trip ───────────────────────────────────────────

    [Fact]
    public void Config_round_trips_every_knob()
    {
        var inst = NewInstance();
        inst.DeserializeConfig(
            "{\"ThresholdDb\":-24,\"Ratio\":8,\"AttackMs\":5,\"ReleaseMs\":250,\"MakeupDb\":6}");
        var json = inst.SerializeConfig();

        ReadFloat(json, "ThresholdDb").Should().BeApproximately(-24f, 1e-4f);
        ReadFloat(json, "Ratio").Should().BeApproximately(8f, 1e-4f);
        ReadFloat(json, "AttackMs").Should().BeApproximately(5f, 1e-4f);
        ReadFloat(json, "ReleaseMs").Should().BeApproximately(250f, 1e-4f);
        ReadFloat(json, "MakeupDb").Should().BeApproximately(6f, 1e-4f);
    }

    [Fact]
    public void Defaults_match_spec()
    {
        var json = NewInstance().SerializeConfig();
        ReadFloat(json, "ThresholdDb").Should().BeApproximately(-12f, 1e-4f);
        ReadFloat(json, "Ratio").Should().BeApproximately(4f, 1e-4f);
        ReadFloat(json, "AttackMs").Should().BeApproximately(10f, 1e-4f);
        ReadFloat(json, "ReleaseMs").Should().BeApproximately(100f, 1e-4f);
        ReadFloat(json, "MakeupDb").Should().BeApproximately(0f, 1e-4f);
    }

    [Theory]
    // Each knob driven past both ends of its documented range.
    [InlineData("ThresholdDb", 50, 0)]      [InlineData("ThresholdDb", -200, -60)]
    [InlineData("Ratio", 9999, 100)]        [InlineData("Ratio", 0.1, 1)]
    [InlineData("AttackMs", 9999, 100)]     [InlineData("AttackMs", 0.001, 0.1)]
    [InlineData("ReleaseMs", 99999, 2000)]  [InlineData("ReleaseMs", 1, 10)]
    [InlineData("MakeupDb", 999, 24)]       [InlineData("MakeupDb", -10, 0)]
    public void Out_of_range_values_clamp(string knob, double input, float expected)
    {
        var inst = NewInstance();
        inst.DeserializeConfig($"{{\"{knob}\":{input.ToString(CultureInfo.InvariantCulture)}}}");
        ReadFloat(inst.SerializeConfig(), knob).Should().BeApproximately(expected, 1e-3f);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"Ratio\":\"oops\"}")]
    [InlineData("42")]
    public void Malformed_config_never_throws(string json)
    {
        var inst = NewInstance();
        var act = () => inst.DeserializeConfig(json);
        act.Should().NotThrow();
        // And the audio path still works afterwards.
        var fx = inst.CreateEffect(new SilenceProvider(Fmt));
        var read = () => Render(fx, 256);
        read.Should().NotThrow();
    }

    // ── Format preserved ────────────────────────────────────────────

    [Fact]
    public void CreateEffect_preserves_waveformat()
    {
        var fx = NewInstance().CreateEffect(new SineProvider(Fmt, 1000, 1.0f));
        fx.WaveFormat.SampleRate.Should().Be(Fmt.SampleRate);
        fx.WaveFormat.Channels.Should().Be(Fmt.Channels);
        fx.WaveFormat.Encoding.Should().Be(Fmt.Encoding);
    }

    [Fact]
    public void Works_for_mono_sources_too()
    {
        var mono = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var fx = NewInstance().CreateEffect(new SineProvider(mono, 1000, 1.0f));
        var act = () => Render(fx, 4096);
        act.Should().NotThrow();
    }

    // ── The compressor actually compresses ──────────────────────────

    [Fact]
    public void Above_threshold_signal_is_attenuated()
    {
        var inst = NewInstance(); // defaults: -12 dB threshold, 4:1
        var fx = inst.CreateEffect(new SineProvider(Fmt, 1000, amplitude: 1.0f));

        // Unit-amplitude tone is ~0 dBFS peak — well above -12 dB — so it
        // gets pulled down. Bare input RMS is ≈ 0.707.
        Rms(RenderSettled(fx)).Should().BeLessThan(0.5f);
    }

    [Fact]
    public void Below_threshold_signal_passes_unchanged()
    {
        var inst = NewInstance(); // default -12 dB threshold, 0 dB makeup
        var fx = inst.CreateEffect(new SineProvider(Fmt, 1000, amplitude: 0.05f));

        // -26 dBFS is below the threshold (and below the knee's lower edge
        // at -15 dB) ⇒ unity gain.
        Rms(RenderSettled(fx)).Should().BeApproximately(0.05f / MathF.Sqrt(2f), 2e-3f);
    }

    [Fact]
    public void Makeup_gain_raises_level()
    {
        // Same below-threshold tone, one with +12 dB makeup. No reduction
        // applies to either (below threshold), so makeup is the only factor:
        // +12 dB ≈ ×3.98.
        var plain = NewInstance();
        var plainRms = Rms(RenderSettled(
            plain.CreateEffect(new SineProvider(Fmt, 1000, 0.05f))));

        var boosted = NewInstance();
        boosted.DeserializeConfig("{\"MakeupDb\":12}");
        var boostedRms = Rms(RenderSettled(
            boosted.CreateEffect(new SineProvider(Fmt, 1000, 0.05f))));

        (boostedRms / plainRms).Should().BeApproximately(MathF.Pow(10f, 12f / 20f), 0.05f);
    }

    [Fact]
    public void Higher_ratio_attenuates_more()
    {
        // Identical loud tone through a gentle 2:1 and an aggressive 8:1.
        var gentle = NewInstance();
        gentle.DeserializeConfig("{\"ThresholdDb\":-12,\"Ratio\":2}");
        var gentleRms = Rms(RenderSettled(
            gentle.CreateEffect(new SineProvider(Fmt, 1000, 1.0f))));

        var hard = NewInstance();
        hard.DeserializeConfig("{\"ThresholdDb\":-12,\"Ratio\":8}");
        var hardRms = Rms(RenderSettled(
            hard.CreateEffect(new SineProvider(Fmt, 1000, 1.0f))));

        hardRms.Should().BeLessThan(gentleRms);
    }

    [Fact]
    public void Stereo_image_is_preserved()
    {
        // Different amplitudes per channel; the louder channel drives
        // detection, but both channels get the SAME gain, so their ratio
        // is unchanged after compression.
        var fx = NewInstance().CreateEffect(new StereoProvider(Fmt, 1000, 1.0f, 0.5f));
        Render(fx, frames: 48000); // settle
        var buf = Render(fx, frames: 24000);

        float lRms = ChannelRms(buf, 0), rRms = ChannelRms(buf, 1);
        // Left was twice the right going in; that 2:1 ratio must survive.
        (lRms / rRms).Should().BeApproximately(2f, 0.05f);
    }

    // ── Live-config smoke ───────────────────────────────────────────

    [Fact]
    public async Task Hammering_DeserializeConfig_while_reading_never_throws()
    {
        var inst = NewInstance();
        var fx = inst.CreateEffect(new SineProvider(Fmt, 1000, 1.0f));

        using var cts = new CancellationTokenSource();
        Exception? failure = null;

        var writer = Task.Run(() =>
        {
            var rng = new Random(1234);
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    inst.DeserializeConfig(string.Format(CultureInfo.InvariantCulture,
                        "{{\"ThresholdDb\":{0},\"Ratio\":{1},\"AttackMs\":{2},\"ReleaseMs\":{3},\"MakeupDb\":{4}}}",
                        -60 + rng.NextDouble() * 60, 1 + rng.NextDouble() * 99,
                        0.1 + rng.NextDouble() * 99.9, 10 + rng.NextDouble() * 1990,
                        rng.NextDouble() * 24));
                }
            }
            catch (Exception ex) { failure = ex; }
        }, cts.Token);

        var buf = new float[2048];
        for (int i = 0; i < 5000; i++)
        {
            fx.Read(buf, 0, buf.Length);
            foreach (var s in buf)
                float.IsNaN(s).Should().BeFalse("audio must stay finite during live config swaps");
        }

        cts.Cancel();
        await writer;
        failure.Should().BeNull();
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static float ReadFloat(string json, string prop)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(prop).GetSingle();
    }

    /// <summary>Render <paramref name="frames"/> frames through the effect
    /// and return the interleaved buffer.</summary>
    private static float[] Render(ISampleProvider fx, int frames)
    {
        var buf = new float[frames * fx.WaveFormat.Channels];
        int total = 0;
        while (total < buf.Length)
            total += fx.Read(buf, total, buf.Length - total);
        return buf;
    }

    /// <summary>Render through the effect, discarding a settling prefix so
    /// the envelope follower's attack transient doesn't skew the RMS, and
    /// return the steady tail.</summary>
    private static float[] RenderSettled(ISampleProvider fx)
    {
        Render(fx, frames: 24000); // 0.5 s settle, discarded
        return Render(fx, frames: 24000);
    }

    private static float Rms(float[] buf)
    {
        double acc = 0;
        for (int i = 0; i < buf.Length; i++) acc += (double)buf[i] * buf[i];
        return (float)Math.Sqrt(acc / buf.Length);
    }

    /// <summary>RMS of one interleaved channel (assumes 2-channel buffer).</summary>
    private static float ChannelRms(float[] buf, int channel)
    {
        double acc = 0;
        int n = 0;
        for (int i = channel; i < buf.Length; i += 2) { acc += (double)buf[i] * buf[i]; n++; }
        return (float)Math.Sqrt(acc / n);
    }
}

/// <summary>Sine of a given amplitude, identical on every channel.</summary>
internal sealed class SineProvider(WaveFormat fmt, double freq, float amplitude) : ISampleProvider
{
    private long _frame;
    public WaveFormat WaveFormat => fmt;

    public int Read(float[] buffer, int offset, int count)
    {
        int ch = fmt.Channels;
        for (int i = 0; i < count; i += ch)
        {
            var s = amplitude * (float)Math.Sin(2.0 * Math.PI * freq * _frame / fmt.SampleRate);
            for (int k = 0; k < ch; k++) buffer[offset + i + k] = s;
            _frame++;
        }
        return count;
    }
}

/// <summary>Stereo sine with independent per-channel amplitudes — used to
/// verify the compressor applies one shared gain (stereo image intact).</summary>
internal sealed class StereoProvider(WaveFormat fmt, double freq, float ampL, float ampR) : ISampleProvider
{
    private long _frame;
    public WaveFormat WaveFormat => fmt;

    public int Read(float[] buffer, int offset, int count)
    {
        for (int i = 0; i + 1 < count; i += 2)
        {
            var phase = 2.0 * Math.PI * freq * _frame / fmt.SampleRate;
            buffer[offset + i]     = ampL * (float)Math.Sin(phase);
            buffer[offset + i + 1] = ampR * (float)Math.Sin(phase);
            _frame++;
        }
        return count;
    }
}

internal sealed class SilenceProvider(WaveFormat fmt) : ISampleProvider
{
    public WaveFormat WaveFormat => fmt;
    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        return count;
    }
}
