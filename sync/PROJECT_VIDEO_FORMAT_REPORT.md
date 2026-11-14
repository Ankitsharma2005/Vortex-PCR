# Project Video Format System — implementation and validation

Date: 14 November 2026.

Subsequent GPU decoder preferences and actual RTX decoding verification are documented in [GPU_DECODING_REPORT.md](GPU_DECODING_REPORT.md). The production CPU scaling and real-time admission limitations below still apply.

The software implementation adds a separate production raster to the existing LibVLC engine. The existing settings design, 960×540 operator preview, rundown/automation and CG design are preserved. Older projects keep the legacy path until a project format is explicitly applied. Previous uncommitted UI/audio work in this workspace was preserved; it is not newly introduced by this feature.

Full acceptance is **not yet achieved**: physical DeckLink modes/field order/reconnection and long-duration split-device lip-sync need destination-hardware testing. GPU production scaling and GPU utilization telemetry are not implemented; production conversion currently uses the CPU. This PC did not pass the conservative admission check for UHD production. Native UHD raster correctness and short UHD encodes passing do not establish real-time UHD operation.

## Implemented

- Global master project format: 576i50, 720p25/50, 1080i50, 1080p25/30/50/60 and 2160p25/30/50/60.
- Native production storage up to 3840×2160, independent of the legacy preview. Actual LibVLC UHD decoding was checked for preservation of one-pixel detail. Source dimensions and upscale status are reported separately.
- Paced production frames; field-time sampling/weaving, upper/lower field-order buffers, field-aware scaling, progressive output images, aspect-preserving letterbox conversion and repeated/missed-deadline counters. LibVLC auto-deinterlace requests the existing YADIF2x filter when processing a production format.
- Explicit CPU/memory admission before applying a format. Apply requires both programs and external outputs stopped. Changes roll back if configuration/persistence fails. Inconsistent legacy timecode changes are rejected while a master standard is active.
- Existing DeckLink SDK adapter reused. Enumerates only driver-supported modes, up to UHD60; validates the chosen connector/mode again at Start. UHD additionally requires a project format and CPU conversion admission. Routes stereo PCM to independently selected channels in a 2/8/16-channel output; the actual driver must accept that channel count.
- Failed/disconnected DeckLink sessions release resources and can be started again after device rediscovery. Saved routes are restored as configuration, never automatically started.
- Independent stream raster/FPS, H.264 or HEVC, bitrate, keyframe interval, profile/level, AAC/AC3, output audio bitrate/rate and mono/stereo settings. HEVC is intentionally limited to SRT/UDP; this adapter does not implement enhanced HEVC RTMP.
- Selected CPU/GPU encoder is tested with FFmpeg before Start. Invalid codec/profile/level combinations are rejected. Stream scaling preserves aspect ratio. Status shows source/production/output raster and skipped-pump frames.
- RTMP, RTMPS, SRT and UDP are supported. Optional custom RTMPS CA certificate permits private-server certificates without disabling verification.
- Project format and independent route/profile settings persist in the existing state store. Stream options can be saved/loaded; destination URLs and keys are excluded and must be re-entered. Missing saved hardware never auto-starts an output.
- Existing settings receive Project format and Compatibility tabs plus additional fields inside the existing DeckLink/Streaming sections. There is no further application-wide UI redesign.
- Compatibility view reports CPU usage, RAM, original source size, conversion cost, repeated frames and missed deadlines. GPU utilization is explicitly unavailable rather than fabricated.

## Validation performed

| Check | Result and scope |
|---|---|
| Solution and separate desktop/engine builds | Passed, 0 warnings/errors |
| Existing regression suite with native decode enabled | 81 passed, including real dual-channel decode/stop/load |
| Audio timing/meter suite | 24 passed; includes simulated two-hour timing, not a hardware soak |
| Production-format suite | 37 checks passed across all 12 rasters, field separation, aspect ratio, legacy defaults, JSON/SQLite save/reopen and actual native UHD LibVLC decode |
| Actual UHD encoding | H.264 and HEVC at 25/30/50/60 fps: two-frame encode, ffprobe inspection and actual decode passed; not real-time/sustained performance certification |
| Actual local protocol loopback | 1080i50 project → independent 720p25 stream, explicit video and audio receive: UDP, SRT caller, RTMP and certificate-verified RTMPS passed |
| Combined format/encoder/protocol suite | 49 passed |
| Isolated engine API | Catalog, compatibility, apply, restart restoration, independent UHD profile persistence, URL/secret exclusion and unavailable-device retry/cleanup passed |
| Settings UI | Rendered; protocol/codec control behavior and compatibility-gated Apply checked with an isolated mock engine |
| Physical DeckLink, GPU encoders, remote platform ingest, hardware lip-sync | Not verified |

Final local admission measurements (two-port CPU workload; load-dependent):

| Format | CPU conversion | Tick budget | Admission |
|---|---:|---:|---|
| 576i50 | 12.16 ms | 20.00 ms | Pass |
| 720p25 | 18.82 ms | 40.00 ms | Pass |
| 720p50 | 17.97 ms | 20.00 ms | Reject with headroom requirement |
| 1080i50 | 21.00 ms | 20.00 ms | Reject |
| 1080p25 | 22.29 ms | 40.00 ms | Pass |
| 1080p30 | 20.37 ms | 33.33 ms | Pass |
| 1080p50 | 21.15 ms | 20.00 ms | Reject |
| 1080p60 | 21.47 ms | 16.67 ms | Reject |
| 2160p25 | 39.78 ms | 40.00 ms | Reject with headroom requirement |
| 2160p30 | 37.07 ms | 33.33 ms | Reject |
| 2160p50 | 48.94 ms | 20.00 ms | Reject |
| 2160p60 | 44.56 ms | 16.67 ms | Reject |

Admission is recalculated on the destination PC. These values are neither hardware capability claims nor guarantees under effects/output load.

## Hardware/dependency requirements

- Windows x64, .NET 8 SDK/runtime and the existing LibVLC package.
- Appropriate Blackmagic Desktop Video driver and output-capable DeckLink hardware for the requested mode/channel count. A card reporting HD-only modes is never offered UHD modes.
- FFmpeg with the chosen encoder/protocol. A checksum-verified Gyan FFmpeg 9.0.2 essentials build was downloaded to the ignored local `tools/ffmpeg` directory for these tests. The executable is not committed; the existing build script copies `tools/ffmpeg/ffmpeg.exe` to the engine output. The [FFmpeg download page](https://ffmpeg.org/download.html) lists this Windows build provider.
- NVENC/QSV/AMF need matching hardware/driver and encoder support. Only CPU encoders were verified here.
- CPU/RAM and decode/output capacity sufficient for the selected project. UHD encoding availability alone is insufficient to enable UHD production.

## Remaining limits

- No GPU raster/scaling backend or GPU utilization measurement. CPU fallback is implemented; GPU decoder acceleration remains a request, not a guarantee from LibVLC memory callbacks.
- No certified sustained UHD50/60 performance. The admission guard rejects combinations this machine cannot safely budget.
- Hardware field cadence, actual SDI channel mapping, disconnection/reconnection and audio/video offset require physical receiver tests. Interlaced conversion is software tested, not broadcast-certified.
- Existing display windows remain preview based. Existing desktop CG overlays and Browser/NDI audio embedding limitations are unchanged.
- Progressive-to-interlaced conversion cannot invent motion absent from a lower-frame-rate source. Sources below the chosen raster remain upscaled and are labeled accordingly.
- Stream pacing/drop counters and existing timestamped audio tests do not certify end-to-end lipsync on a remote ingest platform. SRT listener parameters are validated, but the live SRT loopback test used caller output.
- Streaming destination credentials are intentionally not persisted. Custom CA file paths can be unavailable on another PC and must be selected again.
- SDI/stream outputs never auto-start just because a saved project is opened. An unavailable format raises a startup alarm and retains the saved setting while leaving its production path inactive.

## Files changed for this feature

Existing files:

- `.gitignore`, `desktop/NewsPlay.PCR.sln`, `sync/Finish-Work.ps1` — include the new format checks in source/build workflow.
- `desktop/NewsPlay.Core/Models.cs`, `DeckLinkModels.cs`, `StreamModels.cs`, `PlayoutController.cs` — format/route settings, validation and persistence.
- `desktop/NewsPlay.Engine/VlcDecoder.cs`, `Program.cs`, `DeckLinkOutputService.cs`, `StreamingService.cs`, `HardwareEncoderProbe.cs` — narrowly integrate production buffers and independent outputs using the existing engines.
- `desktop/NewsPlay.Desktop/EngineClient.cs`, `OutputSettingsWindow.cs`, `OutputSettingsWindow.DeckLink.cs`, `StreamingWindow.cs` — API/settings integration in the existing layout.
- `desktop/NewsPlay.Tests/Program.cs` — master consistency and embedded-channel regression checks.

New files:

- `desktop/NewsPlay.Core/ProjectVideoFormat.cs`
- `desktop/NewsPlay.Engine/ProductionVideoPipeline.cs`
- `desktop/NewsPlay.Engine/StreamingArguments.cs`
- `desktop/NewsPlay.Engine/VideoFormatCompatibility.cs`
- `desktop/NewsPlay.Engine/VideoResourceMonitor.cs`
- `desktop/NewsPlay.Desktop/OutputSettingsWindow.ProjectVideo.cs`
- `desktop/NewsPlay.ProjectVideoChecks/NewsPlay.ProjectVideoChecks.csproj`
- `desktop/NewsPlay.ProjectVideoChecks/Program.cs`
- `desktop/NewsPlay.ProjectVideoChecks/EncodingChecks.cs`
- `sync/PROJECT_VIDEO_FORMAT_REPORT.md`

## Reproduce

```powershell
dotnet build desktop/NewsPlay.PCR.sln -c Debug
dotnet run --project desktop/NewsPlay.Tests/NewsPlay.Tests.csproj -- --native
dotnet run --project desktop/NewsPlay.AudioTimingChecks/NewsPlay.AudioTimingChecks.csproj
dotnet run --project desktop/NewsPlay.ProjectVideoChecks/NewsPlay.ProjectVideoChecks.csproj -- --native --encoding --capabilities
```

The last command needs `tools/ffmpeg/ffmpeg.exe` and `ffprobe.exe`; it creates temporary media and localhost listeners. Production files and the running operator project are not used. Feature builds are in `artifacts/PCR-ProjectFormat` and `artifacts/PCR-ProjectFormat-Engine`. The existing operator session has not been switched to this feature build or changed to a different master format. Changes have not been committed/pushed.
