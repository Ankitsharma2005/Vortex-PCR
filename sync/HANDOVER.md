# Handover

Current project: NewsPlay PCR, independent from MCR.

Recent work: encoder selection (NVENC/QSV/AMF), automatic hardware decoding
request, Configuration menu, visible clip search, timestamped sound-card buffer.

Outstanding validation: real long-duration DeckLink/sound-card lip-sync test;
WASAPI queued latency now uses device playback position instead of a fixed 80 ms estimate. Sound-card alignment follows the smoothed DeckLink timeline. Audio timing checks and engine build passed; actual split-device hardware soak testing remains pending. True 4K/8K and higher-bit-depth processing
remain incomplete; timecode presets do not enable those output pipelines.

Before switching computers, replace this note with the current task, files
changed, checks run, and the next concrete step. Do not include credentials.
