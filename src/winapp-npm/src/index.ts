// Main entry point for the Windows SDK BuildTools package
import { execSyncWithBuildTools } from './buildtools-utils';
import { addMsixIdentityToExe, addElectronDebugIdentity, clearElectronDebugIdentity } from './msix-utils';
import { getGlobalWinappPath, getLocalWinappPath } from './winapp-path-utils';

// Re-export types from child_process for convenience
export type { ExecSyncOptions } from 'child_process';

// Re-export types
export {
  MsixIdentityOptions,
  MsixIdentityResult,
  ElectronDebugIdentityResult,
  ClearElectronDebugIdentityResult,
} from './msix-utils';
export { CallWinappCliOptions, CallWinappCliResult } from './winapp-cli-utils';
export { GenerateCppAddonOptions, GenerateCppAddonResult } from './cpp-addon-utils';
export { GenerateCsAddonOptions, GenerateCsAddonResult } from './cs-addon-utils';

// Re-export functions
export {
  // BuildTools utilities
  execSyncWithBuildTools as execWithBuildTools,

  // MSIX manifest utilities
  addMsixIdentityToExe,
  addElectronDebugIdentity,
  clearElectronDebugIdentity,

  // winapp directory utilities
  getGlobalWinappPath,
  getLocalWinappPath,
};

// Default export for CommonJS compatibility
export default {
  execWithBuildTools: execSyncWithBuildTools,
  addMsixIdentityToExe,
  addElectronDebugIdentity,
  clearElectronDebugIdentity,
  getGlobalWinappPath,
  getLocalWinappPath,
};
