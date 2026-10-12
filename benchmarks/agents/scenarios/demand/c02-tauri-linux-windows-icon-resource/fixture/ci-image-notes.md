# CI image notes

- Base image: Debian 12.
- Rust targets installed: `x86_64-unknown-linux-gnu`, `x86_64-pc-windows-msvc`.
- Node 20 and pnpm 9 are installed.
- NSIS is installed from the distribution package.
- No mingw binutils, LLVM resource tools, or cargo-xwin setup is recorded in the image.
