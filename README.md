# WhisperNote

Voice-to-text desktop app for Windows. Hold a key, speak, release — transcribed text is copied to clipboard instantly.

## Features

- **Hold-to-record** — Right Ctrl (default) to start/stop recording
- **Auto-start server** — llama.cpp server starts on demand, stops after each request
- **Local GGUF models** — llama.cpp runs the selected model entirely on this machine
- **Grammar correction** — LLM cleans up speech into proper English
- **VRAM offload** — stop server after each request to free GPU memory
- **Run on startup** — optional Windows auto-start
- **System tray** — minimize to the notification area and keep the hotkey active

## Build

For a manual build, right-click `build.cmd` and choose **Run**. It launches
`build.ps1` with the required PowerShell execution-policy bypass and keeps the
window open so errors remain visible.

`build.ps1` checks the runtime payload before publishing. If the llama.cpp CUDA
binaries are missing from `llm-servers/llama/windows/llama`, it runs
`update-llama.ps1` to download the latest preview, so a fresh clone builds
without a manual download step. Missing optional backends (Vulkan, NPU) and
missing model files are reported, not fatal.

Publishing is non-destructive: the app is published into `obj/publish-staging`
and merged into `publish/`, so rebuilding keeps `publish/models/*.gguf`,
`whispernote.json` and `logs.log`. Only files the build itself produces are
replaced, and files a previous build produced that the current build no longer
produces are removed.

| Switch | Effect |
| --- | --- |
| `-Kill` | Force-close the running app (and its `llama-server`) before publishing. |
| `-NoUpdate` | Never download; fail if a required backend is missing. |
| `-UpdateBackends` | Also fetch the CUDA 12.4 (legacy NVIDIA), Vulkan (iGPU) and NPU backends when missing. |
| `-ForceUpdate` | Run the update scripts even when binaries are already present. |
| `-RefreshModels` | Overwrite `publish/models` from the source `models/` folder. |

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

The backend payloads live in `llm-servers/llama/windows/` in this repository
and are synced into `publish/` by `build.ps1`; `cuda12/update-cuda12.ps1` and
`vulkan/update-vulkan.ps1` refresh them from llama.cpp preview releases.

## Requirements

- Windows 10+
- .NET 8 Runtime
- llama.cpp server (`llama-server.exe`) for local models

## Configuration

Edit `whispernote.json` in the application folder to add providers, change the hotkey, or toggle auto-start.

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

