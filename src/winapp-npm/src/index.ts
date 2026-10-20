// Main entry point for the Windows SDK BuildTools package
import { execSyncWithBuildTools } from './buildtools-utils';
import { addMsixIdentityToExe, addElectronDebugIdentity, clearElectronDebugIdentity } from './msix-utils';
import { getGlobalWinappPath, getLocalWinappPath } from './winapp-path-utils';
import * as winappCommands from './winapp-commands';
import { uiRecord } from './ui-record-guard';
import { uiRecordActions } from './ui-record-actions-guard';

// Re-export types from child_process for convenience
export type { ExecSyncOptions } from 'child_process';

// Re-export types
export {
  MsixIdentityOptions,
  MsixIdentityResult,
  ElectronDebugIdentityResult,
  ClearElectronDebugIdentityResult,
} from './msix-utils';
export {
  CallWinappCliOptions,
  CallWinappCliResult,
  CallWinappCliCaptureOptions,
  CallWinappCliCaptureResult,
} from './winapp-cli-utils';
export { GenerateCppAddonOptions, GenerateCppAddonResult } from './cpp-addon-utils';
export { GenerateCsAddonOptions, GenerateCsAddonResult } from './cs-addon-utils';

// Re-export all command types and public functions automatically from the generated module.
// The generated _uiRecordGenerated function is module-internal (not exported) so it does
// not appear in the package surface — only the guarded uiRecord is public.
export * from './winapp-commands';

// Export the public, guarded uiRecord wrapper (overrides the internal _uiRecordGenerated).
// Importing from this module gives the safe version that enforces durationSec > 0.
// Also re-export the stricter UiRecordOptions type (durationSec: number, required),
// which shadows the generated optional durationSec version from winapp-commands.
export { uiRecord, type UiRecordOptions } from './ui-record-guard';
export { uiRecordActions, type UiRecordActionsOptions } from './ui-record-actions-guard';

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
  ...winappCommands,
  uiRecord, // guarded wrapper — overrides any uiRecord from the spread (none expected)
  uiRecordActions,
};
