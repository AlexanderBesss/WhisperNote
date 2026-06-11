# WhisperNote

Voice-to-text desktop app for Windows. Hold a key, speak, release — transcribed text is copied to clipboard instantly.

## Features

- **Hold-to-record** — Right Ctrl (default) to start/stop recording
- **Auto-start server** — llama.cpp server starts on demand, stops after each request
- **Multi-provider** — local GGUF models or cloud APIs (OpenAI, Azure)
- **Grammar correction** — LLM cleans up speech into proper English
- **VRAM offload** — stop server after each request to free GPU memory
- **Run on startup** — optional Windows auto-start

## Requirements

- Windows 10+
- .NET 8 Runtime
- llama.cpp server (`llama-server.exe`) for local models

## Configuration

Edit `whispernote.json` to add providers, change the hotkey, or toggle auto-start.
