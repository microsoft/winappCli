import type { Configuration } from "electron-builder";

const config: Configuration = {
  appId: "io.starlane.desktop",
  productName: "StarLane",
  directories: {
    output: "release"
  },
  files: ["dist/**/*", "package.json"],
  win: {
    target: "nsis",
    icon: "build/icon.ico"
  }
};

export default config;
