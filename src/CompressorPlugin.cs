using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Layout;
using NAudio.Wave;
using SoundBoard.PluginApi;

namespace CompressorPlugin;

/// <summary>
/// <see cref="IAudioSamplerPlugin"/> implementing a feedforward, soft-knee
/// dynamic-range compressor. Stops loud SFX from blasting the listener by
/// attenuating signal above a configurable threshold. Knobs: threshold (dB),
/// ratio, attack (ms), release (ms), makeup gain (dB).
///
/// <para>Per-instance envelope state — multiple attachments on different
/// shortcuts do NOT cross-modulate each other's gain reduction.</para>
/// </summary>
public sealed class CompressorPlugin : IAudioSamplerPlugin
{
    public string Id => "sampler.compressor";
    public string Name => "Compressor";
    public string Description => "Dynamic-range compressor. Stops loud SFX from blasting; tames volume spikes.";
    public string Version => PluginVersion.OfAssembly(typeof(CompressorPlugin));
    public string Author => "Devin Sanders";

    public SamplerAttachmentPoints SupportedAttachments => SamplerAttachmentPoints.All;

    public void Initialize(IPluginContext context) { }
    public void Shutdown() { }

    public ISamplerInstance CreateInstance() => new CompressorInstance();
}

/// <summary>
/// One configured compressor node. Owns the envelope-follower state plus
/// the five knob values. The DSP work happens in the nested
/// <see cref="CompressorEffect"/> sample-provider.
/// </summary>
internal sealed class CompressorInstance : ISamplerInstance
{
    // ── Defaults ──────────────────────────────────────────────────────────
    private const float DefaultThresholdDb = -12f;
    private const float DefaultRatio       = 4f;
    private const float DefaultAttackMs    = 10f;
    private const float DefaultReleaseMs   = 100f;
    private const float DefaultMakeupDb    = 0f;

    // ── Documented ranges (used for both clamp + UI slider extents) ──────
    private const float MinThresholdDb = -60f, MaxThresholdDb = 0f;
    private const float MinRatio       =   1f, MaxRatio       = 100f;  // 100:1 ≈ limiter
    private const float MinAttackMs    = 0.1f, MaxAttackMs    = 100f;
    private const float MinReleaseMs   =  10f, MaxReleaseMs   = 2000f;
    private const float MinMakeupDb    =   0f, MaxMakeupDb    = 24f;

    // Soft-knee width in dB. Not user-exposed; 6 dB is the conventional
    // sweet spot (audibly smooth, narrow enough that the threshold knob
    // still feels like a hard target).
    private const float KneeDb = 6f;

    // Tiny epsilon for log10 to avoid -∞ when signal is exactly 0.
    private const float LevelEpsilon = 1e-12f;

    // ── Parameter state ──────────────────────────────────────────────────
    // Float values stored as the int bit-pattern so reads/writes across
    // threads are atomic via Volatile.Read/Write. UI thread writes via the
    // properties below; audio thread reads them at the top of each buffer.
    // No locking on the Read path.
    private int _thresholdDbBits = BitConverter.SingleToInt32Bits(DefaultThresholdDb);
    private int _ratioBits       = BitConverter.SingleToInt32Bits(DefaultRatio);
    private int _attackMsBits    = BitConverter.SingleToInt32Bits(DefaultAttackMs);
    private int _releaseMsBits   = BitConverter.SingleToInt32Bits(DefaultReleaseMs);
    private int _makeupDbBits    = BitConverter.SingleToInt32Bits(DefaultMakeupDb);

    // ── Envelope state ──────────────────────────────────────────────────
    // Audio thread only — no synchronisation needed. Lives on the
    // instance (NOT static) so concurrent attachments don't cross-talk.
    private float _env;

    public float ThresholdDb
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _thresholdDbBits));
        set => Volatile.Write(ref _thresholdDbBits, BitConverter.SingleToInt32Bits(value));
    }
    public float Ratio
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _ratioBits));
        set => Volatile.Write(ref _ratioBits, BitConverter.SingleToInt32Bits(value));
    }
    public float AttackMs
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _attackMsBits));
        set => Volatile.Write(ref _attackMsBits, BitConverter.SingleToInt32Bits(value));
    }
    public float ReleaseMs
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _releaseMsBits));
        set => Volatile.Write(ref _releaseMsBits, BitConverter.SingleToInt32Bits(value));
    }
    public float MakeupDb
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _makeupDbBits));
        set => Volatile.Write(ref _makeupDbBits, BitConverter.SingleToInt32Bits(value));
    }

    public ISampleProvider CreateEffect(ISampleProvider source) => new CompressorEffect(source, this);

    // ── Persistence ──────────────────────────────────────────────────────
    public string SerializeConfig()
    {
        return JsonSerializer.Serialize(new ConfigDto
        {
            ThresholdDb = ThresholdDb,
            Ratio       = Ratio,
            AttackMs    = AttackMs,
            ReleaseMs   = ReleaseMs,
            MakeupDb    = MakeupDb,
        });
    }

    public void DeserializeConfig(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        ConfigDto? c;
        try { c = JsonSerializer.Deserialize<ConfigDto>(json); }
        catch (JsonException) { return; }
        if (c is null) return;

        // Publish each scalar atomically. Clamp here so a corrupted blob
        // or older-version config can't push values out of range and
        // blow up the DSP (e.g. AttackMs <= 0 → divide by zero in alpha).
        ThresholdDb = Clamp(c.ThresholdDb, MinThresholdDb, MaxThresholdDb);
        Ratio       = Clamp(c.Ratio,       MinRatio,       MaxRatio);
        AttackMs    = Clamp(c.AttackMs,    MinAttackMs,    MaxAttackMs);
        ReleaseMs   = Clamp(c.ReleaseMs,   MinReleaseMs,   MaxReleaseMs);
        MakeupDb    = Clamp(c.MakeupDb,    MinMakeupDb,    MaxMakeupDb);
    }

    // ── Editor UI ────────────────────────────────────────────────────────
    public object? CreateControl()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };
        panel.Children.Add(BuildSlider("Threshold (dB)", MinThresholdDb, MaxThresholdDb, ThresholdDb, "F1", v => ThresholdDb = v));
        panel.Children.Add(BuildSlider("Ratio (x:1)",    MinRatio,       MaxRatio,       Ratio,       "F1", v => Ratio       = v));
        panel.Children.Add(BuildSlider("Attack (ms)",    MinAttackMs,    MaxAttackMs,    AttackMs,    "F1", v => AttackMs    = v));
        panel.Children.Add(BuildSlider("Release (ms)",   MinReleaseMs,   MaxReleaseMs,   ReleaseMs,   "F0", v => ReleaseMs   = v));
        panel.Children.Add(BuildSlider("Makeup (dB)",    MinMakeupDb,    MaxMakeupDb,    MakeupDb,    "F1", v => MakeupDb    = v));
        return panel;
    }

    private static Control BuildSlider(string label, double min, double max, double initial, string fmt, Action<float> setter)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2 };
        var caption = new TextBlock
        {
            Text = $"{label}: {initial.ToString(fmt, CultureInfo.InvariantCulture)}"
        };
        var slider = new Slider { Minimum = min, Maximum = max, Value = initial };
        // Slider.ValueChanged fires on every drag tick — the host's
        // ~100 ms live-edit cadence is finer than human knob-twiddling,
        // so writes here are effectively instant from the user's POV.
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                var v = (float)slider.Value;
                setter(v);
                caption.Text = $"{label}: {v.ToString(fmt, CultureInfo.InvariantCulture)}";
            }
        };
        stack.Children.Add(caption);
        stack.Children.Add(slider);
        return stack;
    }

    public void Dispose() { }

    private static float Clamp(float v, float min, float max)
        => v < min ? min : (v > max ? max : v);

    private sealed class ConfigDto
    {
        public float ThresholdDb { get; set; } = DefaultThresholdDb;
        public float Ratio       { get; set; } = DefaultRatio;
        public float AttackMs    { get; set; } = DefaultAttackMs;
        public float ReleaseMs   { get; set; } = DefaultReleaseMs;
        public float MakeupDb    { get; set; } = DefaultMakeupDb;
    }

    /// <summary>
    /// Per-sample feedforward compressor. Topology:
    ///   1. Peak detector across channels → smoothed by one-pole IIR
    ///      with separate attack/release time constants.
    ///   2. Gain-reduction curve in the dB domain, with a 6 dB quadratic
    ///      soft knee centred on the threshold.
    ///   3. Same gain applied to every channel — preserves stereo image
    ///      (one channel transient won't pan the program material).
    /// </summary>
    private sealed class CompressorEffect : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly CompressorInstance _owner;

        public CompressorEffect(ISampleProvider source, CompressorInstance owner)
        {
            _source = source;
            _owner = owner;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = _source.Read(buffer, offset, count);
            if (n <= 0) return n;

            // Snapshot parameters once per buffer. Volatile reads, so a
            // mid-buffer UI write just lands in the NEXT buffer — no torn
            // float reads, no audible discontinuity. Buffers are ~10 ms
            // at typical host sizes; well below the perceptual threshold
            // for the live-edit "feels instant" requirement (~100 ms).
            float thresholdDb = _owner.ThresholdDb;
            float ratio       = MathF.Max(1f, _owner.Ratio);
            float attackMs    = MathF.Max(0.01f, _owner.AttackMs);
            float releaseMs   = MathF.Max(0.01f, _owner.ReleaseMs);
            float makeupDb    = _owner.MakeupDb;

            // Per-sample one-pole IIR coefficient:
            //   alpha = exp(-1 / (tau_seconds * Fs))
            // attack/release time constants in milliseconds → seconds.
            // Fs comes from the source (48 kHz by host contract, but derived
            // here so the time constants stay correct at any rate).
            float sampleRate   = WaveFormat.SampleRate;
            float attackAlpha  = MathF.Exp(-1f / (attackMs  * 0.001f * sampleRate));
            float releaseAlpha = MathF.Exp(-1f / (releaseMs * 0.001f * sampleRate));

            float invRatio        = 1f / ratio;
            float oneMinusInvRat  = 1f - invRatio;
            float lowerKnee       = thresholdDb - KneeDb * 0.5f;
            float upperKnee       = thresholdDb + KneeDb * 0.5f;
            float makeupLin       = MathF.Pow(10f, makeupDb / 20f);

            // Host mixer is 48 kHz IEEE-float stereo, interleaved.
            // Read generally as channels though — defensive against
            // future mono attachment edge cases.
            int channels = WaveFormat.Channels;
            float env = _owner._env;

            for (int i = 0; i < n; i += channels)
            {
                // ── Step 1: peak detection across channels ───────────────
                float level = 0f;
                for (int c = 0; c < channels; c++)
                {
                    float s = buffer[offset + i + c];
                    float abs = s < 0f ? -s : s;
                    if (abs > level) level = abs;
                }

                // One-pole IIR with attack/release branching:
                // attack alpha when the signal is rising above the envelope,
                // release alpha when it's decaying back below.
                float alpha = (level > env) ? attackAlpha : releaseAlpha;
                env = alpha * env + (1f - alpha) * level;

                // ── Step 2: gain-reduction curve in dB ───────────────────
                float levelDb = 20f * MathF.Log10(env + LevelEpsilon);
                float reductionDb;
                if (levelDb <= lowerKnee)
                {
                    reductionDb = 0f;
                }
                else if (levelDb >= upperKnee)
                {
                    // Full above-threshold compression slope:
                    //   GR = (level - threshold) * (1 - 1/ratio)
                    reductionDb = (levelDb - thresholdDb) * oneMinusInvRat;
                }
                else
                {
                    // Quadratic soft-knee bridge from 0 at the lower knee
                    // to the full slope at the upper knee. Continuous in
                    // value AND first derivative at both endpoints.
                    float x = levelDb - lowerKnee;
                    reductionDb = oneMinusInvRat * (x * x) / (2f * KneeDb);
                }

                // ── Step 3: apply the same linear gain to every channel ─
                // (one detector → one gain → stereo image stays intact)
                float gainLin = MathF.Pow(10f, -reductionDb / 20f) * makeupLin;
                for (int c = 0; c < channels; c++)
                {
                    buffer[offset + i + c] *= gainLin;
                }
            }

            _owner._env = env;
            return n;
        }
    }
}
