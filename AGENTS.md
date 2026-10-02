# WhisperNote UI Guidelines

- Keep WPF controls aligned with the app's dark visual design. Use the shared styles in `Styles/` and do not rely on default WPF button colors or templates for new settings actions.
- Use `./build.ps1` to build and publish the app; pass `-Kill` when a running WhisperNote instance must be closed forcefully before publishing.
- `build.ps1` publishes into `obj/publish-staging` and merges into `publish/`. Do not point `dotnet publish` straight at `publish/`: the SDK's incremental publish-clean deletes payload files it no longer produces, which wipes the multi-gigabyte `publish/models` files.
- Runtime payload (models, `llama/`, `vulkan/`, `NPU/llama-ov/`) uses `CopyToPublishDirectory="Never"` in `WhisperNote.csproj` and is synced by `build.ps1`. Keep that metadata so the SDK never owns those folders.
