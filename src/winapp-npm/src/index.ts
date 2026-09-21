// Main entry point for the Windows SDK BuildTools package
import { execSyncWithBuildTools } from './buildtools-utils';
import { addMsixIdentityToExe, addElectronDebugIdentity, clearElectronDebugIdentity } from './msix-utils';
import { getGlobalWinappPath, getLocalWinappPath } from './winapp-path-utils';
import * as winappCommands from './winapp-commands';
import { uiRecord } from './ui-record-guard';
import { targetRecord } from './target-record-guard';

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
// The generated _uiRecordGenerated and _targetRecordGenerated functions are module-internal
// (not exported) so they do not appear in the package surface — only the guarded uiRecord and
// targetRecord are public.
export * from './winapp-commands';

// Export the public, guarded uiRecord wrapper (overrides the internal _uiRecordGenerated).
// Importing from this module gives the safe version that enforces durationSec > 0.
// Also re-export the stricter UiRecordOptions type (durationSec: number, required),
// which shadows the generated optional durationSec version from winapp-commands.
export { uiRecord, type UiRecordOptions } from './ui-record-guard';

// Same arrangement for `target record`: the stricter TargetRecordOptions (durationSec required)
// shadows the generated one, so a caller who omits a duration fails to compile as well as at
// runtime.
export { targetRecord, type TargetRecordOptions } from './target-record-guard';

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
  targetRecord, // guarded wrapper — overrides any targetRecord from the spread (none expected)
};
