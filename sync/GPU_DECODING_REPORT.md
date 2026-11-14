# GPU decoding — 14 November 2026

GPU decoding is integrated with the existing LibVLC player and native production/preview callbacks. The existing Windows hardware-decoding flag already requested D3D11VA; the change exposes a persisted decoder policy, explicit Auto/D3D11VA/DXVA2/CPU choices and native verification. It does not replace the playback engine.

In Output settings → Compatibility → GPU video decoding:

- Auto lets LibVLC choose an available hardware backend and fall back to software for unsupported codecs/devices.
- D3D11VA or DXVA2 selects a preferred Windows backend, retaining LibVLC's software decoder fallback.
- CPU explicitly disables hardware decoding.
- Apply decoder preference saves the setting and changes **future media loads**. It does not interrupt/reload an on-air clip. Program, source/colour previews and native live-preview decoders inherit the preference.
- Test selected decoder creates a short local H.264/HEVC fixture, opens a separate single-player LibVLC instance, uses the actual native production callback path and checks both frame delivery and LibVLC's hardware-decoder confirmation. Probe execution never changes the saved/current program preference.
- Refresh decoder observations reports historical engine-wide native confirmations. LibVLCSharp's event does not identify a player, so those observations are not presented as per-port activity. The isolated decoder test does provide per-test confirmation.
- The existing `NEWSPLAY_SOFTWARE_DECODE=1` override still forces CPU, including in tests.

Verification on this PC: NVIDIA GeForce RTX 5060, driver 32.0.15.9579.

| Test | Result |
|---|---|
| Auto, H.264, 1920×1080 | D3D11VA confirmed, native frames received |
| D3D11VA, H.264, 1280×720 | GPU confirmed, native frames received |
| D3D11VA, HEVC, 1280×720 | GPU confirmed, native frames received |
| D3D11VA, H.264, 3840×2160 | GPU confirmed, native frames received |
| D3D11VA, HEVC, 3840×2160 | GPU confirmed, native frames received |
| DXVA2, H.264, 1920×1080 | DXVA2 confirmed, native frames received |
| CPU, H.264, 1920×1080 | Frames received, no GPU decoder activated |
| Forced-software environment override with D3D11VA selected | Frames received, no GPU decoder activated |
| Existing regression/native decoder suite | 82 passed |
| Real engine API | Saved CPU preference remained intact while an isolated UHD HEVC probe confirmed D3D11VA |
| Settings UI | Existing Compatibility panel rendered and decoder preference save passed |

Tests receive at least two decoded native frames; these are short capability checks, not sustained-throughput or 4K60 certification. Codec/profile/bit depth, drivers and adapters on another PC can differ. A successful decode test does not remove the CPU scaling/admission limits documented in PROJECT_VIDEO_FORMAT_REPORT.md.

GPU decoding does not implement GPU raster scaling/effects. Production scaling and native pixel copy-back remain CPU/memory work. Browser webpage playback uses Chromium, and the Browser/NDI bridge supplies already-decoded JPEG/raw pictures; this setting does not accelerate those external players or eliminate their latency. GPU encoding and GPU decoding are independent.

The test fixture generator needs the existing bundled FFmpeg with libx264/libx265. Missing FFmpeg prevents the native test and produces an explicit error; it does not prevent saving decoder preferences or ordinary playback.

New source files: `NewsPlay.Core/HardwareDecodeSettings.cs`, `NewsPlay.Engine/HardwareDecodeMonitor.cs`, `NewsPlay.Engine/HardwareDecodeProbe.cs`, and `NewsPlay.ProjectVideoChecks/GpuDecodingChecks.cs`.

Integration touches existing state persistence, decoder/media options, live-preview decoder construction, engine/client API, the existing Compatibility panel and regression checks. The direct per-media option avoids the extra D3D11VA option that [LibVLCSharp's Windows hardware-decoding property adds](https://github.com/videolan/libvlcsharp/blob/3.x/src/LibVLCSharp/Shared/MediaConfiguration.cs).

Reproduce the native GPU/CPU checks:

```powershell
dotnet run --project desktop/NewsPlay.ProjectVideoChecks/NewsPlay.ProjectVideoChecks.csproj -- --gpu
dotnet run --project desktop/NewsPlay.Tests/NewsPlay.Tests.csproj -- --native
```

Native test results are written to the ignored check output folder as `project-video-save-check/gpu-decoding-results.json`. Reviewable builds are in `artifacts/PCR-GpuDecode` and `artifacts/PCR-GpuDecode-Engine`. Changes have not been committed/pushed.
