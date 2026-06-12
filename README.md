# gmsb-sampler-compressor

Compressor FX plugin for
[Game Master Sound Board](https://github.com/DevinSanders/game-master-soundboard).
A feedforward soft-knee dynamics compressor — stops loud SFX from blasting
the listener and evens out level differences across a chain.

## Install

**Paid plugin.** The source is open here for reference, but the pre-built
binary is distributed pay-what-you-want on itch.io:

**→ https://dsand64.itch.io/gmsb-sampler-compressor**

Download the `.zip` from that page and drop it onto **Settings → Plugin
Manager** in Game Master Sound Board. Restart when prompted, then enable it under **Settings → Plugins**.

## Parameters

| Knob       | Range            | Default | Notes                                  |
|------------|------------------|---------|----------------------------------------|
| Threshold  | −60 … 0 dB       | −12 dB  | Level above which gain reduction kicks in. |
| Ratio      | 1:1 … 100:1      | 4:1     | 100:1 acts as a limiter.               |
| Attack     | 0.1 … 100 ms     | 10 ms   | How fast it clamps a transient.        |
| Release    | 10 … 2000 ms     | 100 ms  | How fast it recovers after the peak.   |
| Makeup     | 0 … 24 dB        | 0 dB    | Output gain to compensate for reduction. |

A fixed 6 dB soft knee smooths the transition around the threshold.

## Manifest

| Field     | Value                       |
|-----------|-----------------------------|
| publisher | `github.DevinSanders`       |
| id        | `sampler.compressor`        |
| entryDll  | `CompressorPlugin.dll`      |

## Bypass behavior

This plugin holds DSP state (an envelope follower). The host's BypassableSamplerInstance wrapper toggles bypass by flipping a flag — it does NOT rebuild the chain. While bypassed the envelope isn't clocked, so on un-bypass the follower briefly resumes from its frozen value — a slight, short-lived gain-reduction artifact until it re-settles. For an interactive soundboard with on-demand FX this is negligible (the envelope re-converges within an attack/release time constant).

## License

Released under the [MIT License](LICENSE).

Third-party components used by this plugin:

- No third-party DSP — original implementation.