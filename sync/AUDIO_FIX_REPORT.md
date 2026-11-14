# Audio stability regression fix — 14 November 2026

User reported distorted/crackling speech across outputs on another PC after installing the update ZIP. Actual sound quality on that PC remains unverified.

Changes:

- Restore the previously stable 80 ms shared-mode WASAPI buffer for native PCM and NDI. The recent 20 ms setting could underrun on slower/USB devices. Measured render-queue compensation remains active; no fixed 80 ms A/V offset was added.
- Increase the timestamp reconciliation deadband from 1 ms to 5 ms, avoiding sample cuts and inserted silence for small device-clock/dispatch jitter. Larger late/future timestamps still reconcile against the clock, preserving the bounded timing behavior.
- NDI normal packet bursts no longer clear the entire buffer at 100 ms. Queue capacity remains bounded; only excess oldest audio is discarded above the 200 ms catch-up threshold.
- Add source-starvation/callback-error counters and Output settings → Audio / PCM → Save audio diagnostics. Export contains audio routing and timing, not source URLs or stream credentials.

Evidence:

- 82 existing/native playback regression checks passed.
- 26 audio timing checks passed, including continuous stereo PCM through ±2.5 ms clock jitter.
- Compared identical tone packets against the original 1 ms timestamp implementation: 100/100 output waveform blocks altered before the fix; 0/100 after the fix.
- Native/WASAPI diagnostics distinguish source starvation from intentional future-timestamp holds. Starvation counters are not hardware-underrun measurements and can include the tail of a finished source.

Update artifact: `artifacts/updates/NewsPlay-PCR-AudioFix-2026-11-14.zip`. This full runtime-included update preserves the existing updater's data/template protection and binary backup. Install it instead of the earlier update ZIP, then test the same voice/byte clip on the affected PC. If distortion remains, collect the audio diagnostics and a short output recording; the physical driver/source/SDI path has not been certified by these software checks.
