# WhisperNote UI Guidelines

- Keep WPF controls aligned with the app's dark visual design. Use the shared styles in `Styles/` and do not rely on default WPF button colors or templates for new settings actions.
- Use `./build.ps1` to build and publish the app; pass `-Kill` when a running WhisperNote instance must be closed forcefully before publishing.
