const config = {
  appId: "io.ridgetrail.editor",
  productName: "RidgeTrail Editor",
  directories: {
    output: "dist"
  },
  files: [
    "assets/win",
    "backend",
    "frontend/dist",
    "package.json",
    "package-lock.json"
  ],
  win: {
    icon: "assets/win/icon_win.ico",
    target: ["portable", "nsis"]
  },
  nsis: {
    oneClick: false,
    allowToChangeInstallationDirectory: true
  }
};

module.exports = config;
