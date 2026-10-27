# WhisperNote

Voice-to-text desktop app for Windows. Hold a key, speak, release — transcribed text is copied to clipboard instantly.

## Features

- **Hold-to-record** — Right Ctrl (default) to start/stop recording
- **Auto-start server** — llama.cpp server starts on demand, stops after each request
- **Local GGUF models** — llama.cpp runs the selected model entirely on this machine
- **Grammar correction** — the transcription prompt already turns speech into proper English
- **Grammar check (optional)** — an extra pass over the transcript through your own LLM endpoint
- **VRAM offload** — stop server after each request to free GPU memory
- **Run on startup** — optional Windows auto-start
- **System tray** — minimize to the notification area and keep the hotkey active

## Build

For a manual build, right-click `build.cmd` and choose **Run**. It launches
`build.ps1` with the required PowerShell execution-policy bypass and keeps the
window open so errors remain visible.

`build.ps1` never downloads or copies any runtime payload: the llama.cpp
backends and the model files are fetched on first use by the app itself
(`BackendDownloader` / `ModelDownloader`) into `publish/`, according to the
machine's hardware or the CPU-only setting.

Publishing is non-destructive: the app is published into `obj/publish-staging`
and merged into `publish/`, so rebuilding keeps `publish/models/*.gguf`,
`whispernote.json` and `logs.log`. Only files the build itself produces are
replaced, and files a previous build produced that the current build no longer
produces are removed.

| Switch | Effect |
| --- | --- |
| `-Kill` | Force-close the running app (and its `llama-server`) before publishing. |

## GPU Backends

At startup WhisperNote detects the GPU and picks the matching llama.cpp binary
from `publish/`:

| Detected hardware | Backend folder | Notes |
| --- | --- | --- |
| NVIDIA Turing (sm_75) or newer | `llama/` | CUDA 13 build. |
| NVIDIA Maxwell, Pascal, Volta (pre-Turing, e.g. GTX 1080 Ti) | `cuda12/` | CUDA 12.4 build; the CUDA 13 build ships no kernels for these GPUs. |
| AMD or Intel GPU | `vulkan/` | Also covers NVIDIA Pascal as a fallback. |
| Intel Core Ultra (NPU) | `NPU/llama-ov/` | OpenVINO build; optional payload. |
| No supported GPU | `llama/` with `--device none` | CPU inference. |

If the selected backend aborts on the device (for example `no kernel image is
available`), the server automatically retries the next backend in the chain
(CUDA 13 → CUDA 12.4 → Vulkan → CPU, shortened to what was detected), so a
wrong guess degrades instead of failing. The status chip in the main window
shows which backend is running. **CPU only mode** in Settings skips every GPU
backend.

The backend payloads are downloaded into `publish/` by the app on first use,
and the staging merge in `build.ps1` keeps those folders through every rebuild.

## Requirements

- Windows 10+
- .NET 10 Desktop Runtime
- llama.cpp server (`llama-server.exe`) for local models

## Configuration

Edit `whispernote.json` in the application folder to add providers, change the hotkey, or toggle auto-start.

### Grammar check

The **Grammar** tab adds an optional second pass: recording → transcript → your LLM rewrites it →
the **corrected** text is what gets copied and pasted. Fill in the **API host** of any OpenAI-compatible
server (`https://api.openai.com/v1`, `http://localhost:8082`, an Ollama or vLLM address) and, when the
server needs one, the **API key**; there is no model field because WhisperNote reads the model the server
reports for itself from `/v1/models` and shows it under the fields — that same request is how it knows
whether the endpoint is reachable at all.

The pass is attempted **once** per recording. A host that does not answer, a rejected key or a model that
answers instead of correcting leaves the original transcript untouched and says so in the main window
("Copied and pasted · grammar API unavailable"). In the Grammar tab the dot next to the switch carries the
same state — grey idle, amber checking, green reachable, red skipped — and its tooltip spells out the
reason. A failed probe is not retried for a minute, so an offline endpoint never stalls your dictation.
The API key is stored in plain text in `whispernote.json`.

**Thinking effort** decides how much the model may think before answering, forwarded as the standard
`reasoning_effort` chat parameter: **Server default** sends no parameter at all (what an endpoint without
that support expects), or **Off**, **Low**, **Medium**, **High** — `Off` is what `whispernote.json` stores
and it goes over the wire as `minimal`, the lowest value servers in this family accept. On a reasoning
model this is the difference between roughly half a second and several seconds per correction, and
proofreading rarely needs the extra thinking. An unrecognised value in `whispernote.json` is treated as
"Server default".

### When a recording produces nothing

A dedicated ASR model decides for itself whether the audio contains speech, and for a hum, a click or a
half-word it does not stay silent: probed on this machine, a low-level vocalisation came back as
`language Chinese<asr_text>啊哈哈！`. Transcription requests therefore pin `language=en`, and an answer
built of non-Latin letters is dropped rather than pasted into your document.

The three ways a recording can still produce nothing each get their own message in the window — **"Did not
hear speech through “Mic name” — speak up or move closer"** (the mic heard a hum, room tone or nothing),
**"The model answered in another language — nothing was pasted"** and **"Model returned no transcription —
try again"** — instead of the pill quietly returning to idle. A very short tap on the hotkey is answered
immediately ("hold the key while speaking"), and an answer of "no speech" is not re-sent three times:
decoding is greedy, so repeating the same audio only repeats the same answer.

Audio is only rejected before the model when the capture is genuinely dead (peak under 80). Quiet speech
peaks at 250–400 and transcribes word for word, so the older 500-peak gate that discarded usable
recordings is gone.

Every recording logs **which device it came from** and how long that capture had been alive, and when the
Windows default mic has moved elsewhere while WhisperNote kept recording the device it had opened, the log
says so on the recording it affected — that drift is the usual reason a recording arrives full of audio the
model hears nothing in. A capture whose audio engine dies mid-recording (`0x88890004`, a device invalidated
under us) logs the bytes it discards and is rebuilt before the next press. A server build that does not
understand the `language` field drops it after the first refusal instead of failing every recording.

**Numbers** become digits in two stages. A dedicated ASR model transcribes verbatim, so WhisperNote's own
number pass converts what it can parse with certainty (`twenty five dollars` → `$25`, `three point five` → `3.5`)
and deliberately leaves idioms in words (`one of them`, `a hundred times`). When the grammar check is on, the
proofreader then handles the ambiguous cases a rule cannot judge — `five thirty tomorrow` → `5:30 tomorrow`,
`one thousand dollars` → `$1,000` — while keeping everyday counts and set phrases in words (`two bananas`,
`one more day`, `Chapter One`) and leaving digits that are already there exactly as they are (`$1,000`,
`12/05/2026`, `+1 555 0100`). The transcript is sent to it between `---` markers, labelled as data, because a
bare message let the model delete a dictated sentence it mistook for an instruction. Measured on a local
reasoning model at effort Off: 0.7–1.3 s per recording.

By default WhisperNote **starts hidden in the notification area** (toggle **Start in tray** in Settings).
While a recording is active — hotkey-held or button-started — a small dark pill appears at the bottom of the
screen with a pulsing red dot and "Recording…"; it hides automatically when the recording stops. The pill
never steals focus and clicks pass through it, so the app you are dictating into keeps the caret.

The main window's close button hides WhisperNote in the notification area, where the recording hotkey keeps
working. Click the tray icon, or choose **Open WhisperNote** in its menu, to bring the window back;
**Exit** in that menu quits the app. The tray icon's ring is red while nothing is being said and green while
the mic is listening or a request is being processed, matching the status dot in the window. **Minimize to
tray** in Settings controls what the close button does: when it is on, closing hides the window to the tray,
and when it is off, closing exits the app.

