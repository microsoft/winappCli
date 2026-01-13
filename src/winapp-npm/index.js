// Main entry point for the Windows SDK BuildTools package
const buildtoolsUtils = require('./buildtools-utils');
const msixUtils = require('./msix-utils');
const winappPathUtils = require('./winapp-path-utils');

module.exports = {
  // BuildTools utilities
  execWithBuildTools: buildtoolsUtils.execSyncWithBuildTools,

  // MSIX manifest utilities
  addMsixIdentityToExe: msixUtils.addMsixIdentityToExe,
  addElectronDebugIdentity: msixUtils.addElectronDebugIdentity,

  // winapp directory utilities
  getGlobalWinappPath: winappPathUtils.getGlobalWinappPath,
  getLocalWinappPath: winappPathUtils.getLocalWinappPath
};
