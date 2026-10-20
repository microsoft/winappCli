# Build change under test

- Previous successful build: electron-builder 26.x
- Current failing build: electron-builder 27.0.0-alpha.9
- The app uses a patched NSISBI archive because the generated installer is larger than the stock NSIS limit.
- Signing succeeds before the NSIS compiler step.
