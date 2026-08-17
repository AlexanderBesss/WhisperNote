# WhisperNote

Voice-to-text desktop app for Windows. Hold a key, speak, release — transcribed text is copied to clipboard instantly.

## Features

- **Hold-to-record** — Right Ctrl (default) to start/stop recording
- **Auto-start server** — llama.cpp server starts on demand, stops after each request
- **Multi-provider** — local GGUF models or cloud APIs (OpenAI, Azure)
- **Remote execution** — send recordings to another WhisperNote instance that runs its local model
- **Grammar correction** — LLM cleans up speech into proper English
- **VRAM offload** — stop server after each request to free GPU memory
- **Run on startup** — optional Windows auto-start

## Build

For a manual build, right-click `build.cmd` and choose **Run**. It launches
`build.ps1` with the required PowerShell execution-policy bypass and keeps the
window open so errors remain visible. Pass `-Kill` when the running app must be
force-closed before publishing.

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
configure the client with the server's reachable LAN hostname or address rather than `0.0.0.0`. A LAN
binding may require a Windows URL ACL and firewall rule, and should only be exposed on a trusted network. The protocol intentionally does
not add authentication or TLS; cloud orchestration, streaming, and request queuing are not supported.
