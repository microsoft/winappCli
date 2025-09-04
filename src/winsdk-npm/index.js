// Main entry point for the Windows SDK BuildTools package
const setupSdks = require('./setup-sdks');
const nugetUtils = require('./nuget-utils');
const buildtoolsUtils = require('./buildtools-utils');
const msixUtils = require('./msix-utils');

module.exports = {
  // Setup SDKs functions
  setupSDKs: setupSdks.setupSDKs,
  downloadAllSDKPackages: setupSdks.downloadAllSDKPackages,
  runCppWinRT: setupSdks.runCppWinRT,
  SDK_PACKAGES: setupSdks.SDK_PACKAGES,

  // NuGet utilities
  downloadAndExtractNuGetPackage: nugetUtils.downloadAndExtractNuGetPackage,
  getNuGetPackageVersions: nugetUtils.getNuGetPackageVersions,
  getLatestNugetVersion: nugetUtils.getLatestVersion,
  getNugetPackagePath: nugetUtils.getPackagePath,

  // BuildTools utilities
  execWithBuildTools: buildtoolsUtils.execSyncWithBuildTools,
  getBuildToolPath: buildtoolsUtils.getBuildToolPath,
  findBuildToolsBinPath: buildtoolsUtils.findBuildToolsBinPath,
  getCurrentArchitecture: buildtoolsUtils.getCurrentArchitecture,
  ensureBuildTools: buildtoolsUtils.ensureBuildTools,
  downloadAndExtractBuildTools: buildtoolsUtils.downloadAndExtractBuildTools,

  // MSIX manifest utilities
  addMsixIdentityToExe: msixUtils.addMsixIdentityToExe,
  createPriConfig: msixUtils.createPriConfig,
  generatePriFile: msixUtils.generatePriFile,
  generateDevCertificate: msixUtils.generateDevCertificate,
  installDevCertificate: msixUtils.installDevCertificate,
  signMsixPackage: msixUtils.signMsixPackage,
  createMsixPackage: msixUtils.createMsixPackage,
  generateMsixAssets: msixUtils.generateMsixAssets
};
