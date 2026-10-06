# WhisperNote UI Guidelines

- Keep WPF controls aligned with the app's dark visual design. Use the shared styles in `Styles/` and do not rely on default WPF button colors or templates for new settings actions.
- Use `./build.ps1` to build and publish the app; pass `-Kill` when a running WhisperNote instance must be closed forcefully before publishing.
- After every code change, rebuild with `./build.ps1` and restart the app (`publish\WhisperNote.exe`) so the user can verify the result immediately.
- `build.ps1` publishes into `obj/publish-staging` and merges into `publish/`. Do not point `dotnet publish` straight at `publish/`: the SDK's incremental publish-clean deletes payload files it no longer produces, which wipes the multi-gigabyte `publish/models` files.
- The build never downloads or copies any runtime payload. The llama.cpp backends (`llama/`, `cuda12/`, `vulkan/`, `NPU/llama-ov/`, `cpu/`) and the model files are downloaded on first use by `BackendDownloader`/`ModelDownloader` into `publish/`, according to the machine's hardware or the CPU-only setting. Keep the merge in `build.ps1` manifest-scoped so those folders survive every rebuild.
