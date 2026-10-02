# WhisperNote

Voice-to-text desktop app for Windows. Hold a key, speak, release — transcribed text is copied to clipboard instantly.

## Features

- **Hold-to-record** — Right Ctrl (default) to start/stop recording
- **Auto-start server** — llama.cpp server starts on demand, stops after each request
- **Multi-provider** — local GGUF models or cloud APIs (OpenAI, Azure)
- **Remote execution** — warm and send recordings to another WhisperNote instance that runs its local model
- **Grammar correction** — LLM cleans up speech into proper English
- **VRAM offload** — stop server after each request to free GPU memory
- **Run on startup** — optional Windows auto-start

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
| `-UpdateBackends` | Also fetch the Vulkan (iGPU) and NPU backends when missing. |
| `-ForceUpdate` | Run the update scripts even when binaries are already present. |
| `-RefreshModels` | Overwrite `publish/models` from the source `models/` folder. |

## Requirements

- Windows 10+
- .NET 8 Runtime
- llama.cpp server (`llama-server.exe`) for local models

## Configuration

Edit `whispernote.json` in the application folder to add providers, change the hotkey, or toggle auto-start.

Remote providers have two independent modes in Settings:

- **DirectApi** keeps using the configured provider endpoints, credentials, and ordered failover.
- **RemoteExecution** sends PCM audio over HTTP to the configured WhisperNote server endpoint.

On the server instance, enable **Accept remote execution**, choose an HTTP listen endpoint, and keep that
instance in **Local LLM** mode. The default listener is `http://0.0.0.0:8090`, which binds all interfaces;
configure the client with the server's reachable LAN hostname or address rather than `0.0.0.0`. The app
opens the TCP listener directly, so no Windows URL ACL setup is required. Windows Firewall may still need
an inbound rule for the selected port, and the listener should only be exposed on a trusted network. The
protocol intentionally does not add authentication or TLS; cloud orchestration, streaming, and request
queuing are not supported.

When recording begins in RemoteExecution mode, the client sends a best-effort warm-up request so the
server can load its model while the user is speaking. Audio is still sent only when recording ends. With
auto-offload enabled, the warmed model remains loaded until that transcription finishes and is then
offloaded as before. If no transcription follows, the server releases the warm-up after five minutes.

To let a client control server-side model behavior, enable **Allow remote settings control** on the server.
The client then synchronizes **Auto-offload VRAM** and **Thinking mode** when those settings are saved. This is
disabled by default because the protocol has no authentication or TLS; any
trusted-network client that can reach the listener may otherwise change those two settings.
