// Main entry point for the Windows SDK BuildTools package
const buildtoolsUtils = require('./buildtools-utils');
const msixUtils = require('./msix-utils');

module.exports = {
  // BuildTools utilities
  execWithBuildTools: buildtoolsUtils.execSyncWithBuildTools,

  // MSIX manifest utilities
  addMsixIdentityToExe: msixUtils.addMsixIdentityToExe,
  addElectronDebugIdentity: msixUtils.addElectronDebugIdentity
};
