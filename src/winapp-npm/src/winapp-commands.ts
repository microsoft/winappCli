/**
 * AUTO-GENERATED — DO NOT EDIT
 *
 * Regenerate with:  npm run generate-commands
 * Source schema version: 0.6.3
 *
 * Programmatic wrappers for all winapp CLI commands.
 * Each function builds the CLI arguments, invokes the native CLI,
 * and returns a typed result with captured stdout/stderr.
 */
import {
  callWinappCliCapture,
  CallWinappCliCaptureOptions,
  CallWinappCliCaptureResult,
} from './winapp-cli-utils';

// ---------------------------------------------------------------------------
// Shared / common types
// ---------------------------------------------------------------------------

/** IfExists values. */
export type IfExists = 'error' | 'overwrite' | 'skip';

/** SdkInstallMode values. */
export type SdkInstallMode = 'stable' | 'preview' | 'experimental' | 'none';

/** ManifestTemplates values. */
export type ManifestTemplates = 'packaged' | 'sparse';

/** Base options shared by most commands. */
export interface CommonOptions {
  /** Suppress progress messages. */
  quiet?: boolean;
  /** Enable verbose output. */
  verbose?: boolean;
  /** Working directory for the CLI process (defaults to process.cwd()). */
  cwd?: string;
}

/** Result returned by every command wrapper. */
export interface WinappResult {
  /** Process exit code (always 0 on success – non-zero throws). */
  exitCode: number;
  /** Captured standard output. */
  stdout: string;
  /** Captured standard error. */
  stderr: string;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function pushCommon(args: string[], opts: CommonOptions): void {
  // Insert global flags before any "--" passthrough separator so winapp consumes them rather
  // than forwarding them to the launched app/tool (e.g. run ... -- appArgs).
  const flags: string[] = [];
  if (opts.quiet) flags.push('--quiet');
  if (opts.verbose) flags.push('--verbose');
  if (flags.length === 0) return;
  const sep = args.indexOf('--');
  if (sep === -1) args.push(...flags);
  else args.splice(sep, 0, ...flags);
}

function captureOpts(opts: CommonOptions): CallWinappCliCaptureOptions {
  return opts.cwd ? { cwd: opts.cwd } : {};
}

async function execCommand(args: string[], opts: CommonOptions): Promise<WinappResult> {
  pushCommon(args, opts);
  const result: CallWinappCliCaptureResult = await callWinappCliCapture(args, captureOpts(opts));
  return { exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr };
}

// ---------------------------------------------------------------------------
// az-sign
// ---------------------------------------------------------------------------

export interface AzSignOptions extends CommonOptions {
  /** Path to the file to sign (exe, msix, or msixbundle) */
  filePath: string;
  /** Signing account name. Must be used with --resource-group */
  account?: string;
  /** Path to an existing metadata.json file. Skips resource discovery and account/profile selection prompts and signs using this file directly. A non-interactive Azure credential should already be available; the CLI can otherwise fall back to an interactive tenant prompt or 'az login', but the npm programmatic API is always non-interactive and fails instead of prompting. */
  metadataFile?: string;
  /** Certificate profile name. Must be used with --account */
  profile?: string;
  /** Resource group to narrow down signing accounts */
  resourceGroup?: string;
  /** Azure subscription ID to use. If not provided and multiple subscriptions exist, you will be prompted. */
  subscription?: string;
}

/**
 * Code-sign a file using Azure Trusted Signing. Signs executables, MSIX packages, or MSIX bundles using a cloud-managed signing identity. Example: winapp az-sign ./app.msix
 */
export async function azSign(options: AzSignOptions): Promise<WinappResult> {
  const args: string[] = ['az-sign'];
  args.push(options.filePath);
  if (options.account) args.push('--account', options.account);
  if (options.metadataFile) args.push('--metadata-file', options.metadataFile);
  if (options.profile) args.push('--profile', options.profile);
  if (options.resourceGroup) args.push('--resource-group', options.resourceGroup);
  if (options.subscription) args.push('--subscription', options.subscription);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// cert generate
// ---------------------------------------------------------------------------

export interface CertGenerateOptions extends CommonOptions {
  /** Export a .cer file (public key only) alongside the .pfx */
  exportCer?: boolean;
  /** Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) */
  ifExists?: IfExists;
  /** Install the certificate to the local machine store after generation */
  install?: boolean;
  /** Format output as JSON */
  json?: boolean;
  /** Path to Package.appxmanifest or appxmanifest.xml file to extract publisher information from */
  manifest?: string;
  /** Output path for the generated PFX file */
  output?: string;
  /** Password for the generated PFX file */
  password?: string;
  /** Publisher distinguished name (DN) for the generated certificate (e.g., CN=MyCompany or OU=Team, O=Corp, C=US). If not specified, will be inferred from manifest. Bare names are auto-wrapped as CN=<name>. */
  publisher?: string;
  /** Number of days the certificate is valid */
  validDays?: number;
}

/**
 * Create a self-signed certificate for local testing only. Publisher must match the manifest (auto-inferred if --manifest provided or Package.appxmanifest is in working directory). Output: devcert.pfx (default password: 'password'). For production, obtain a certificate from a trusted CA. Use 'cert install' to trust on this machine.
 */
export async function certGenerate(options: CertGenerateOptions = {}): Promise<WinappResult> {
  const args: string[] = ['cert', 'generate'];
  if (options.exportCer) args.push('--export-cer');
  if (options.ifExists) args.push('--if-exists', options.ifExists);
  if (options.install) args.push('--install');
  if (options.json) args.push('--json');
  if (options.manifest) args.push('--manifest', options.manifest);
  if (options.output) args.push('--output', options.output);
  if (options.password) args.push('--password', options.password);
  if (options.publisher) args.push('--publisher', options.publisher);
  if (options.validDays !== undefined) args.push('--valid-days', options.validDays.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// cert info
// ---------------------------------------------------------------------------

export interface CertInfoOptions extends CommonOptions {
  /** Path to the certificate file (PFX) */
  certPath: string;
  /** Format output as JSON */
  json?: boolean;
  /** Password for the PFX file */
  password?: string;
}

/**
 * Display certificate details (subject, thumbprint, expiry). Useful for verifying a certificate matches your manifest before signing.
 */
export async function certInfo(options: CertInfoOptions): Promise<WinappResult> {
  const args: string[] = ['cert', 'info'];
  args.push(options.certPath);
  if (options.json) args.push('--json');
  if (options.password) args.push('--password', options.password);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// cert install
// ---------------------------------------------------------------------------

export interface CertInstallOptions extends CommonOptions {
  /** Path to the certificate file (PFX or CER) */
  certPath: string;
  /** Force installation even if the certificate already exists */
  force?: boolean;
  /** Password for the PFX file */
  password?: string;
}

/**
 * Trust a certificate on this machine (requires admin). Run before installing MSIX packages signed with dev certificates. Example: winapp cert install ./devcert.pfx. Only needed once per certificate.
 */
export async function certInstall(options: CertInstallOptions): Promise<WinappResult> {
  const args: string[] = ['cert', 'install'];
  args.push(options.certPath);
  if (options.force) args.push('--force');
  if (options.password) args.push('--password', options.password);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// create-debug-identity
// ---------------------------------------------------------------------------

export interface CreateDebugIdentityOptions extends CommonOptions {
  /** Path to the .exe that will need to run with identity, or entrypoint script. */
  entrypoint?: string;
  /** Keep the package identity from the manifest as-is, without appending '.debug' to the package name and application ID. */
  keepIdentity?: boolean;
  /** Path to the Package.appxmanifest or appxmanifest.xml */
  manifest?: string;
  /** Do not install the package after creation. */
  noInstall?: boolean;
}

/**
 * Enable package identity for debugging without creating full MSIX. Required for testing Windows APIs (push notifications, share target, etc.) during development. Example: winapp create-debug-identity ./myapp.exe. Requires Package.appxmanifest or appxmanifest.xml in current directory or passed via --manifest. Re-run after changing the manifest or Assets/.
 */
export async function createDebugIdentity(options: CreateDebugIdentityOptions = {}): Promise<WinappResult> {
  const args: string[] = ['create-debug-identity'];
  if (options.entrypoint) args.push(options.entrypoint);
  if (options.keepIdentity) args.push('--keep-identity');
  if (options.manifest) args.push('--manifest', options.manifest);
  if (options.noInstall) args.push('--no-install');
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// create-external-catalog
// ---------------------------------------------------------------------------

export interface CreateExternalCatalogOptions extends CommonOptions {
  /** List of input folders with executable files to process (separated by semicolons) */
  inputFolder: string;
  /** Include flat hashes when generating the catalog */
  computeFlatHashes?: boolean;
  /** Behavior when output file already exists */
  ifExists?: IfExists;
  /** Output catalog file path. If not specified, the default CodeIntegrityExternal.cat name is used. */
  output?: string;
  /** Include files from subdirectories */
  recursive?: boolean;
  /** Include page hashes when generating the catalog */
  usePageHashes?: boolean;
}

/**
 * Generates a CodeIntegrityExternal.cat catalog file with hashes of executable files from specified directories. Used with the TrustedLaunch flag in MSIX sparse package manifests (AllowExternalContent) to allow execution of external files not included in the package.
 */
export async function createExternalCatalog(options: CreateExternalCatalogOptions): Promise<WinappResult> {
  const args: string[] = ['create-external-catalog'];
  args.push(options.inputFolder);
  if (options.computeFlatHashes) args.push('--compute-flat-hashes');
  if (options.ifExists) args.push('--if-exists', options.ifExists);
  if (options.output) args.push('--output', options.output);
  if (options.recursive) args.push('--recursive');
  if (options.usePageHashes) args.push('--use-page-hashes');
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// embed-identity
// ---------------------------------------------------------------------------

export interface EmbedIdentityOptions extends CommonOptions {
  /** Path to the .exe (embeds identity into its side-by-side manifest via mt.exe) or an .xml/.manifest side-by-side manifest file (inserts/replaces the <msix> element; created if it doesn't exist). */
  target: string;
  /** Path to the sparse appxmanifest.xml to read identity from. When omitted, searched in a 'sparse/' folder (where 'winapp init --exe --sparse' writes it by default) beside the target first, then in the current directory, then beside the target and in the current directory. */
  manifest?: string;
}

/**
 * Connect a desktop exe to its sparse identity package by embedding the <msix> element. Reads identity (packageName, publisher, applicationId) from a sparse appxmanifest.xml and writes it into the target's side-by-side (fusion) manifest. EXE targets are updated with mt.exe; .xml/.manifest targets are edited directly. Example: winapp embed-identity ./bin/MyApp.exe. This is step 3 of the sparse packaging workflow (after 'winapp init --exe --sparse' and 'winapp pack').
 */
export async function embedIdentity(options: EmbedIdentityOptions): Promise<WinappResult> {
  const args: string[] = ['embed-identity'];
  args.push(options.target);
  if (options.manifest) args.push('--manifest', options.manifest);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// find-ui
// ---------------------------------------------------------------------------

export interface FindUiOptions extends CommonOptions {
  /** What you're looking for, e.g. "tabbed layout" or "color picker". Matched lexically against WinUI control names, sample headers, and tags. */
  query?: string;
  /** Fetch the code (Gallery/Toolkit return XAML and/or C#; Reactor is C#-only) plus prerequisite notes for one or more scenario ids from a prior search (e.g. gallery-tabview-1). */
  id?: string | string[];
  /** Format output as JSON */
  json?: boolean;
  /** List every discoverable control/sample id instead of searching. Covers Gallery, Toolkit, and core; the opt-in Reactor source is excluded (search it with --source reactor). */
  list?: boolean;
  /** Maximum number of matched controls to return. Applies to search only; ignored with --list and --id. */
  max?: number;
  /** Bypass the local cache and re-fetch the WinUI corpus from GitHub. */
  refresh?: boolean;
  /** Restrict results to a single source: gallery (WinUI 3 Gallery), toolkit (Windows Community Toolkit), reactor (microsoft-ui-reactor, C#-only declarative WinUI), or core (curated patterns). Reactor is opt-in — it is excluded from a normal search, so pass --source reactor to search it (only do this for a Reactor/MVU project; its C#-only samples don't paste into a standard XAML app). */
  source?: string;
}

/**
 * Search WinUI controls and samples for a working code example. WinUI-only: covers the WinUI 3 Gallery and the Windows Community Toolkit by default (plus the microsoft-ui-reactor ReactorGallery as an opt-in source via --source reactor); not WPF/WinForms. A corpus is baked into the CLI, so this works offline and behind proxies; when GitHub is reachable it refreshes to the latest samples and caches them per-user.
 */
export async function findUi(options: FindUiOptions = {}): Promise<WinappResult> {
  const args: string[] = ['find-ui'];
  if (options.query) args.push(options.query);
  if (options.id) {
    const idArr = Array.isArray(options.id) ? options.id : [options.id];
    for (const v of idArr) args.push('--id', v);
  }
  if (options.json) args.push('--json');
  if (options.list) args.push('--list');
  if (options.max !== undefined) args.push('--max', options.max.toString());
  if (options.refresh) args.push('--refresh');
  if (options.source) args.push('--source', options.source);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// get-winapp-path
// ---------------------------------------------------------------------------

export interface GetWinappPathOptions extends CommonOptions {
  /** Get the global .winapp directory instead of local */
  global?: boolean;
}

/**
 * Print the path to the .winapp directory. Use --global for the shared cache location, or omit for the project-local .winapp folder. Useful for build scripts that need to reference installed packages.
 */
export async function getWinappPath(options: GetWinappPathOptions = {}): Promise<WinappResult> {
  const args: string[] = ['get-winapp-path'];
  if (options.global) args.push('--global');
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// init
// ---------------------------------------------------------------------------

export interface InitOptions extends CommonOptions {
  /** Base/root directory for the winapp workspace, for consumption or installation. */
  baseDirectory?: string;
  /** Directory to read/store configuration (default: the selected project directory, or current directory if no project is detected) */
  configDir?: string;
  /** Only handle configuration file operations (create if missing, validate if exists). Skip package installation and other workspace setup steps. */
  configOnly?: boolean;
  /** Path to the application executable. Requires --sparse. Generates an identity-only sparse manifest for the exe instead of a full package/SDK setup. */
  exe?: string;
  /** Overwrite an existing appxmanifest.xml in the target directory (sparse only). Without this, init fails instead of replacing existing manifest/asset files. */
  force?: boolean;
  /** Don't use configuration file for version management */
  ignoreConfig?: boolean;
  /** Override the package name (sparse only; default: inferred from the exe) */
  name?: string;
  /** Don't update .gitignore file */
  noGitignore?: boolean;
  /** Directory to write the sparse manifest and Assets/ (sparse only; default: a 'sparse/' folder in the current directory) */
  outputDir?: string;
  /** Override the publisher CN (sparse only; default: inferred from the exe's company name). Bare names are auto-wrapped as CN=<name>. */
  publisher?: string;
  /** SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation) */
  setupSdks?: SdkInstallMode;
  /** Generate a sparse identity manifest (appxmanifest.xml) for an existing desktop exe instead of a full package manifest. Use with --exe. Skips SDK/package installation. */
  sparse?: boolean;
  /** Skip interactive prompts and use default answers. Normal init targets the positional project directory if given, otherwise the current directory (e.g., winapp init . --use-defaults). Sparse init (--exe --sparse) ignores the positional directory and writes to --output-dir instead. */
  useDefaults?: boolean;
}

/**
 * Start here for initializing a Windows app with required setup. Sets up everything needed for Windows app development: creates Package.appxmanifest with default assets, downloads Windows SDK and Windows App SDK packages, and generates projections. When SDK packages are managed (--setup-sdks stable/preview/experimental), also creates winapp.yaml to pin versions for 'restore'/'update'; with --setup-sdks none (e.g., for Rust/Tauri projects that bring their own SDK bindings), no winapp.yaml is created. Interactive by default; automatically uses defaults in non-interactive environments (use --use-defaults to skip prompts explicitly). Use 'restore' instead if you cloned a repo that already has winapp.yaml. Use 'manifest generate' if you only need a manifest, or 'cert generate' if you need a development certificate for code signing.
 */
export async function init(options: InitOptions = {}): Promise<WinappResult> {
  const args: string[] = ['init'];
  if (options.baseDirectory) args.push(options.baseDirectory);
  if (options.configDir) args.push('--config-dir', options.configDir);
  if (options.configOnly) args.push('--config-only');
  if (options.exe) args.push('--exe', options.exe);
  if (options.force) args.push('--force');
  if (options.ignoreConfig) args.push('--ignore-config');
  if (options.name) args.push('--name', options.name);
  if (options.noGitignore) args.push('--no-gitignore');
  if (options.outputDir) args.push('--output-dir', options.outputDir);
  if (options.publisher) args.push('--publisher', options.publisher);
  if (options.setupSdks) args.push('--setup-sdks', options.setupSdks);
  if (options.sparse) args.push('--sparse');
  if (options.useDefaults) args.push('--use-defaults');
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// manifest add-alias
// ---------------------------------------------------------------------------

export interface ManifestAddAliasOptions extends CommonOptions {
  /** Application Id to add the alias to (default: first Application element) */
  appId?: string;
  /** Path to Package.appxmanifest or appxmanifest.xml file (default: search current directory) */
  manifest?: string;
  /** Alias name (e.g. 'myapp.exe'). Default: inferred from the Executable attribute in the manifest. */
  name?: string;
}

/**
 * Add an execution alias (uap5:AppExecutionAlias) to a Package.appxmanifest. This allows launching the packaged app from the command line by typing the alias name. By default, the alias is inferred from the Executable attribute (e.g. $targetnametoken$.exe becomes $targetnametoken$.exe alias).
 */
export async function manifestAddAlias(options: ManifestAddAliasOptions = {}): Promise<WinappResult> {
  const args: string[] = ['manifest', 'add-alias'];
  if (options.appId) args.push('--app-id', options.appId);
  if (options.manifest) args.push('--manifest', options.manifest);
  if (options.name) args.push('--name', options.name);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// manifest generate
// ---------------------------------------------------------------------------

export interface ManifestGenerateOptions extends CommonOptions {
  /** Directory to generate manifest in */
  directory?: string;
  /** Human-readable app description shown during installation and in Windows Settings */
  description?: string;
  /** Path to the application's executable. Default: <package-name>.exe */
  executable?: string;
  /** Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) */
  ifExists?: IfExists;
  /** Path to logo image file */
  logoPath?: string;
  /** Package name (default: folder name) */
  packageName?: string;
  /** Publisher distinguished name (DN) (default: CN=<current user>). Accepts any valid X.500 DN; bare names are auto-wrapped as CN=<name>. */
  publisherName?: string;
  /** Manifest template type: 'packaged' (full MSIX app, default) or 'sparse' (desktop app with package identity for Windows APIs) */
  template?: ManifestTemplates;
  /** App version in Major.Minor.Build.Revision format (e.g., 1.0.0.0). */
  version?: string;
}

/**
 * Create Package.appxmanifest without full project setup. Use when you only need a manifest and image assets (no SDKs, no certificate). For full setup, use 'init' instead. Templates: 'packaged' (full MSIX), 'sparse' (desktop app needing Windows APIs).
 */
export async function manifestGenerate(options: ManifestGenerateOptions = {}): Promise<WinappResult> {
  const args: string[] = ['manifest', 'generate'];
  if (options.directory) args.push(options.directory);
  if (options.description) args.push('--description', options.description);
  if (options.executable) args.push('--executable', options.executable);
  if (options.ifExists) args.push('--if-exists', options.ifExists);
  if (options.logoPath) args.push('--logo-path', options.logoPath);
  if (options.packageName) args.push('--package-name', options.packageName);
  if (options.publisherName) args.push('--publisher-name', options.publisherName);
  if (options.template) args.push('--template', options.template);
  if (options.version) args.push('--version', options.version);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// manifest update-assets
// ---------------------------------------------------------------------------

export interface ManifestUpdateAssetsOptions extends CommonOptions {
  /** Path to source image file (SVG, PNG, ICO, JPG, BMP, GIF) */
  imagePath: string;
  /** Path to source image for light theme variants (SVG, PNG, ICO, JPG, BMP, GIF) */
  lightImage?: string;
  /** Path to Package.appxmanifest or appxmanifest.xml file (default: search current directory) */
  manifest?: string;
}

/**
 * Generate new assets for images referenced in a Package.appxmanifest from a single source image. Source image should be at least 400x400 pixels.
 */
export async function manifestUpdateAssets(options: ManifestUpdateAssetsOptions): Promise<WinappResult> {
  const args: string[] = ['manifest', 'update-assets'];
  args.push(options.imagePath);
  if (options.lightImage) args.push('--light-image', options.lightImage);
  if (options.manifest) args.push('--manifest', options.manifest);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// new
// ---------------------------------------------------------------------------

export interface NewOptions extends CommonOptions {
  /** Scaffold even if the output directory already contains files. */
  force?: boolean;
  /** Format output as JSON */
  json?: boolean;
  /** List the available WinUI templates and exit (installs the latest template pack if none is installed). */
  list?: boolean;
  /** Name for the new app/project (default: derived from --output, else 'WinUIApp'). */
  name?: string;
  /** Directory to create the app in (default: ./<name>). Created if it doesn't exist. */
  output?: string;
  /** Template short name. XAML templates: winui, winui-navview, winui-tabview, winui-mvvm, winui-lib, winui-unittest. Experimental Reactor (C#-only, MVU) templates: reactor, reactor-mvu, reactor-navview, reactor-tabview. Run 'winapp new --list' to see all. */
  template?: string;
  /** WinUI template pack version: 'latest' (install newest), 'installed' (keep what's installed), or an explicit version. Default: install latest if none, else prompt to update a stale pack. */
  templateVersion?: string;
  /** Do not prompt; use defaults (blank template, name from --output/--name, keep installed templates). */
  useDefaults?: boolean;
}

/**
 * Create a new WinUI app from an official Windows App SDK template. Templates cover both markup-based XAML apps (blank, NavigationView, TabView, MVVM) and the experimental Reactor apps (C#-only, MVU) — pick one interactively, then a name (the output directory defaults to ./<name>). Automatically uses defaults in non-interactive environments (use --use-defaults to skip prompts explicitly). Requires the .NET SDK; installs the WinUI template pack on demand (grabbing the latest, or offering to update a stale one) and delegates scaffolding to 'dotnet new'. Use --list to see the available templates. Scaffolds against the installed SDK's target framework and prints a template-specific next step when done (e.g. 'dotnet run' for app templates).
 */
export async function newCommand(options: NewOptions = {}): Promise<WinappResult> {
  const args: string[] = ['new'];
  if (options.force) args.push('--force');
  if (options.json) args.push('--json');
  if (options.list) args.push('--list');
  if (options.name) args.push('--name', options.name);
  if (options.output) args.push('--output', options.output);
  if (options.template) args.push('--template', options.template);
  if (options.templateVersion) args.push('--template-version', options.templateVersion);
  if (options.useDefaults) args.push('--use-defaults');
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// package
// ---------------------------------------------------------------------------

export interface PackageOptions extends CommonOptions {
  /** One or more input folders with package layout, or a single sparse appxmanifest.xml file (an identity-only package with AllowExternalContent). Pass multiple folders to create an MSIX bundle (e.g., winapp pack ./publish/x64 ./publish/arm64). */
  inputFolder: string | string[];
  /** Path to signing certificate (will auto-sign if provided) */
  cert?: string;
  /** Certificate password (default: password) */
  certPassword?: string;
  /** Path to the executable relative to the input folder. */
  executable?: string;
  /** Generate a new development certificate */
  generateCert?: boolean;
  /** Install certificate to machine */
  installCert?: boolean;
  /** Path to AppX manifest file (default: auto-detect from input folder or current directory) */
  manifest?: string;
  /** Package name (default: from manifest) */
  name?: string;
  /** Output file name for the generated package (.msix) or bundle (.msixbundle). Defaults to <name>_<version>_<arch>.msix for single packages, or <name>_<version>_<arch1>_<arch2>.msixbundle for bundles. */
  output?: string;
  /** Publisher distinguished name (DN) for certificate generation (e.g., CN=MyCompany). Bare names are auto-wrapped as CN=<name>. */
  publisher?: string;
  /** Bundle Windows App SDK runtime for self-contained deployment */
  selfContained?: boolean;
  /** Skip PRI file generation */
  skipPri?: boolean;
}

/**
 * Create MSIX installer from your built app. Run after building your app. A manifest (Package.appxmanifest or appxmanifest.xml) is required for packaging - it must be in current working directory, passed as --manifest or be in the input folder. Use --cert devcert.pfx to sign for testing. Example: winapp package ./dist --manifest Package.appxmanifest --cert ./devcert.pfx
 */
export async function packageApp(options: PackageOptions): Promise<WinappResult> {
  const args: string[] = ['package'];
  const inputFolderArr = Array.isArray(options.inputFolder) ? options.inputFolder : [options.inputFolder];
  args.push(...inputFolderArr);
  if (options.cert) args.push('--cert', options.cert);
  if (options.certPassword) args.push('--cert-password', options.certPassword);
  if (options.executable) args.push('--executable', options.executable);
  if (options.generateCert) args.push('--generate-cert');
  if (options.installCert) args.push('--install-cert');
  if (options.manifest) args.push('--manifest', options.manifest);
  if (options.name) args.push('--name', options.name);
  if (options.output) args.push('--output', options.output);
  if (options.publisher) args.push('--publisher', options.publisher);
  if (options.selfContained) args.push('--self-contained');
  if (options.skipPri) args.push('--skip-pri');
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// restore
// ---------------------------------------------------------------------------

export interface RestoreOptions extends CommonOptions {
  /** Base/root directory for the winapp workspace */
  baseDirectory?: string;
  /** Directory to read configuration from (default: base-directory) */
  configDir?: string;
}

/**
 * Use after cloning a repo or when .winapp/ folder is missing. Reinstalls SDK packages without changing versions, reading them from winapp.yaml or, for a .NET project initialized by 'init', from the .csproj via 'dotnet restore'. Requires a project already initialized by 'init'. To check for newer SDK versions, use 'update' instead.
 */
export async function restore(options: RestoreOptions = {}): Promise<WinappResult> {
  const args: string[] = ['restore'];
  if (options.baseDirectory) args.push(options.baseDirectory);
  if (options.configDir) args.push('--config-dir', options.configDir);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// run
// ---------------------------------------------------------------------------

export interface RunOptions extends CommonOptions {
  /** Path to the app to run: a build-output folder, a .csproj project, a .sln/.slnx solution, or a directory containing one of those at its top level (default: current directory). */
  input?: string;
  /** @deprecated Use `input` instead. Retained for backward compatibility. */
  inputFolder?: string;
  /** Project mode: target architecture (x64, arm64, or x86). Ignored in folder mode. Default: the current process architecture. */
  arch?: string;
  /** Command-line arguments to pass to the application. Alternatively, use -- followed by arguments to avoid escaping (e.g., winapp run . -- --flag value). */
  args?: string;
  /** Remove the existing package's application data (LocalState, settings, etc.) before re-deploying. By default, application data is preserved across re-deployments. */
  clean?: boolean;
  /** Project mode: build configuration (e.g., Debug, Release). Ignored in folder mode. Default: Debug. */
  configuration?: string;
  /** Capture OutputDebugString messages and first-chance exceptions from the launched application. Only one debugger can attach to a process at a time, so other debuggers (Visual Studio, VS Code) cannot be used simultaneously. Use --no-launch instead if you need to attach a different debugger. For WinUI apps, a crash also triggers a stowed-exception triage pass; the first run downloads debugger components (cached under the winapp global directory) and can be pointed at an existing debugger install via the WINAPP_DBGTOOLS_DIR environment variable. Cannot be combined with --no-launch or --json. */
  debugOutput?: boolean;
  /** Launch the application and return immediately without waiting for it to exit. Useful for CI/automation where you need to interact with the app after launch. Prints the PID to stdout (or in JSON with --json). */
  detach?: boolean;
  /** Path to the executable relative to the input folder. Use to disambiguate when the manifest contains a $targetnametoken$ placeholder and multiple .exe files are present in the input folder. */
  executable?: string;
  /** Project mode: target framework moniker for multi-targeted projects (e.g. net10.0-windows10.0.26100.0). Ignored in folder mode. */
  framework?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Path to the Package.appxmanifest (default: auto-detect from input folder or current directory) */
  manifest?: string;
  /** Project mode: skip building and run the existing build output (still evaluates output properties). Ignored in folder mode. */
  noBuild?: boolean;
  /** Only create the debug identity and register the package without launching the application */
  noLaunch?: boolean;
  /** Project mode: skip restoring the project before building. Ignored in folder mode. */
  noRestore?: boolean;
  /** Output directory for the loose layout package. If not specified, a directory named AppX inside the input directory will be used. */
  outputAppxDirectory?: string;
  /** Project mode: when the input is a solution (.sln/.slnx) or a directory with multiple runnable app projects, selects which project to launch (by name or path). Ignored in folder mode. */
  project?: string;
  /** Project mode: MSBuild property as Name=Value, forwarded to both build and evaluation. Repeatable (e.g. -p WindowsPackageType=None). Ignored in folder mode. */
  property?: string | string[];
  /** Project mode: target .NET runtime identifier (RID), e.g. win-x64. Project mode uses only the RID's architecture, always builds the canonical win-<arch>, and rejects non-Windows RIDs (e.g. linux-x64); it overrides --arch. Ignored in folder mode. */
  runtime?: string;
  /** Download symbols from Microsoft Symbol Server for richer native crash analysis, including the WinUI stowed-exception dispatch stack. Only used with --debug-output. First run downloads symbols and caches them locally; subsequent runs use the cache. */
  symbols?: boolean;
  /** Unregister the development package after the application exits. Only removes packages registered in development mode. */
  unregisterOnExit?: boolean;
  /** Launch the app using its execution alias instead of AUMID activation. The app runs in the current terminal with inherited stdin/stdout/stderr. Requires a uap5:ExecutionAlias in the manifest. Use "winapp manifest add-alias" to add an execution alias to the manifest. */
  withAlias?: boolean;
  /** Arguments to pass to the launched application (forwarded after --). */
  appArgs?: string | string[];
}

/**
 * Builds and runs a Windows app from a .csproj/.sln or a build-output folder. In project mode, invokes dotnet build then launches the app (packaged or unpackaged); in folder mode, creates a debug-signed layout, registers the package, and launches it.
 */
export async function run(options: RunOptions = {}): Promise<WinappResult> {
  const args: string[] = ['run'];
  const inputValue = options.input ?? options.inputFolder;
  if (inputValue) args.push(inputValue);
  if (options.arch) args.push('--arch', options.arch);
  if (options.args) args.push('--args', options.args);
  if (options.clean) args.push('--clean');
  if (options.configuration) args.push('--configuration', options.configuration);
  if (options.debugOutput) args.push('--debug-output');
  if (options.detach) args.push('--detach');
  if (options.executable) args.push('--executable', options.executable);
  if (options.framework) args.push('--framework', options.framework);
  if (options.json) args.push('--json');
  if (options.manifest) args.push('--manifest', options.manifest);
  if (options.noBuild) args.push('--no-build');
  if (options.noLaunch) args.push('--no-launch');
  if (options.noRestore) args.push('--no-restore');
  if (options.outputAppxDirectory) args.push('--output-appx-directory', options.outputAppxDirectory);
  if (options.project) args.push('--project', options.project);
  if (options.property) {
    const propertyArr = Array.isArray(options.property) ? options.property : [options.property];
    for (const v of propertyArr) args.push('--property', v);
  }
  if (options.runtime) args.push('--runtime', options.runtime);
  if (options.symbols) args.push('--symbols');
  if (options.unregisterOnExit) args.push('--unregister-on-exit');
  if (options.withAlias) args.push('--with-alias');
  if (options.appArgs !== undefined) {
    const appArgsArr = Array.isArray(options.appArgs) ? options.appArgs : [options.appArgs];
    if (appArgsArr.length > 0) {
      args.push('--', ...appArgsArr);
    }
  }
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// sign
// ---------------------------------------------------------------------------

export interface SignOptions extends CommonOptions {
  /** Path to the file/package to sign */
  filePath: string;
  /** Path to the certificate file (PFX format) */
  certPath: string;
  /** Certificate password */
  password?: string;
  /** Timestamp server URL */
  timestamp?: string;
}

/**
 * Code-sign an MSIX package or executable. Example: winapp sign ./app.msix ./devcert.pfx. Use --timestamp for production builds to remain valid after cert expires. The 'package' command can sign automatically with --cert.
 */
export async function sign(options: SignOptions): Promise<WinappResult> {
  const args: string[] = ['sign'];
  args.push(options.filePath);
  args.push(options.certPath);
  if (options.password) args.push('--password', options.password);
  if (options.timestamp) args.push('--timestamp', options.timestamp);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// store
// ---------------------------------------------------------------------------

export interface StoreOptions extends CommonOptions {
  /** Arguments to pass through to the Microsoft Store Developer CLI. */
  storeArgs?: string | string[];
}

/**
 * Run a Microsoft Store Developer CLI command. This command will download the Microsoft Store Developer CLI if not already downloaded. Learn more about the Microsoft Store Developer CLI here: https://aka.ms/msstoredevcli
 */
export async function store(options: StoreOptions = {}): Promise<WinappResult> {
  const args: string[] = ['store'];
  if (options.storeArgs !== undefined) {
    const storeArgsArr = Array.isArray(options.storeArgs) ? options.storeArgs : [options.storeArgs];
    args.push(...storeArgsArr);
  }
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// tool
// ---------------------------------------------------------------------------

export interface ToolOptions extends CommonOptions {
  /** Arguments to pass to the SDK tool, e.g. ['makeappx', 'pack', '/d', './folder', '/p', './out.msix']. */
  toolArgs?: string | string[];
}

/**
 * Run Windows SDK tools directly (makeappx, signtool, makepri, etc.). Auto-downloads Build Tools if needed. For most tasks, prefer higher-level commands like 'package' or 'sign'. Example: winapp tool makeappx pack /d ./folder /p ./out.msix
 */
export async function tool(options: ToolOptions = {}): Promise<WinappResult> {
  const args: string[] = ['tool'];
  if (options.toolArgs !== undefined) {
    const toolArgsArr = Array.isArray(options.toolArgs) ? options.toolArgs : [options.toolArgs];
    if (toolArgsArr.length > 0) {
      args.push('--', ...toolArgsArr);
    }
  }
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui click
// ---------------------------------------------------------------------------

export interface UiClickOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Perform a double-click instead of a single click */
  double?: boolean;
  /** Format output as JSON */
  json?: boolean;
  /** Perform a right-click instead of a left click */
  right?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Click an element by slug or text search using mouse simulation. Works on elements that don't support InvokePattern (e.g., column headers, list items). Use --double for double-click, --right for right-click.
 */
export async function uiClick(options: UiClickOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'click'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.double) args.push('--double');
  if (options.json) args.push('--json');
  if (options.right) args.push('--right');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui drag
// ---------------------------------------------------------------------------

export interface UiDragOptions extends CommonOptions {
  /** Start point — an element selector (drags from its center) or screen coordinates x,y as reported by 'ui inspect' (e.g. pn-list-d736 or 100,200). */
  from?: string;
  /** End point — an element selector (drops at its center) or screen coordinates x,y as reported by 'ui inspect' (e.g. pn-target-d746 or 300,400). */
  to?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Milliseconds to dwell at the destination after moving, before releasing (default: 0). Lets drop targets / merge overlays that arm from a sustained hover latch before release. */
  dwellMs?: number;
  /** Milliseconds to hold the button down at the start before moving (default: 0). With <from> == <to> (no movement) this performs a press-and-hold / long-press gesture. */
  holdMs?: number;
  /** Format output as JSON */
  json?: boolean;
  /** Drag with the right mouse button instead of the left button */
  right?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Press the mouse button at one point, move to another, then release. 'drag <from> <to>', where <from>/<to> are each an element selector (uses the element's center) or screen x,y coordinates as reported by 'ui inspect'. Useful for reorder/resize/slider gestures and drag-and-drop. Use --right for a right-button drag, --hold-ms for press-and-hold/long-press, and --dwell-ms to settle on a drop target before releasing.
 */
export async function uiDrag(options: UiDragOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'drag'];
  if (options.from) args.push(options.from);
  if (options.to) args.push(options.to);
  if (options.app) args.push('--app', options.app);
  if (options.dwellMs !== undefined) args.push('--dwell-ms', options.dwellMs.toString());
  if (options.holdMs !== undefined) args.push('--hold-ms', options.holdMs.toString());
  if (options.json) args.push('--json');
  if (options.right) args.push('--right');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui focus
// ---------------------------------------------------------------------------

export interface UiFocusOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Move keyboard focus to the specified element using UIA SetFocus.
 */
export async function uiFocus(options: UiFocusOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'focus'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui get-focused
// ---------------------------------------------------------------------------

export interface UiGetFocusedOptions extends CommonOptions {
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Show the element that currently has keyboard focus in the target app.
 */
export async function uiGetFocused(options: UiGetFocusedOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'get-focused'];
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui get-property
// ---------------------------------------------------------------------------

export interface UiGetPropertyOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Property name to read or filter on */
  property?: string;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Read UIA property values from an element. Specify --property for a single property or omit for all.
 */
export async function uiGetProperty(options: UiGetPropertyOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'get-property'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.property) args.push('--property', options.property);
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui get-value
// ---------------------------------------------------------------------------

export interface UiGetValueOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Read the current value from an element. Tries TextPattern (RichEditBox, Document), ValuePattern (TextBox, ComboBox, Slider), then Name (labels). Usage: winapp ui get-value <selector> -a <app>
 */
export async function uiGetValue(options: UiGetValueOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'get-value'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui hover
// ---------------------------------------------------------------------------

export interface UiHoverOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Time in milliseconds to wait after hovering for hover effects to appear (default: 800) */
  dwellTime?: number;
  /** Format output as JSON */
  json?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Move the mouse to an element's center to trigger hover effects (tooltips, flyouts, visual states). Uses SendInput for realistic mouse movement and waits for a configurable dwell time.
 */
export async function uiHover(options: UiHoverOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'hover'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.dwellTime !== undefined) args.push('--dwell-time', options.dwellTime.toString());
  if (options.json) args.push('--json');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui inspect
// ---------------------------------------------------------------------------

export interface UiInspectOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Walk up the tree from the specified element to the root */
  ancestors?: boolean;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Tree inspection depth */
  depth?: number;
  /** Hide disabled elements from output */
  hideDisabled?: boolean;
  /** Hide offscreen elements from output */
  hideOffscreen?: boolean;
  /** Show only interactive/invokable elements (buttons, links, inputs, list items). Increases default depth to 8. */
  interactive?: boolean;
  /** Format output as JSON */
  json?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * View the UI element tree with semantic slugs, element types, names, and bounds.
 */
export async function uiInspect(options: UiInspectOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'inspect'];
  if (options.selector) args.push(options.selector);
  if (options.ancestors) args.push('--ancestors');
  if (options.app) args.push('--app', options.app);
  if (options.depth !== undefined) args.push('--depth', options.depth.toString());
  if (options.hideDisabled) args.push('--hide-disabled');
  if (options.hideOffscreen) args.push('--hide-offscreen');
  if (options.interactive) args.push('--interactive');
  if (options.json) args.push('--json');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui invoke
// ---------------------------------------------------------------------------

export interface UiInvokeOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Activate an element by slug or text search. Tries InvokePattern, TogglePattern, SelectionItemPattern, and ExpandCollapsePattern in order.
 */
export async function uiInvoke(options: UiInvokeOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'invoke'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui list-windows
// ---------------------------------------------------------------------------

export interface UiListWindowsOptions extends CommonOptions {
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Include untitled zero-size windows that are hidden by default */
  showHidden?: boolean;
}

/**
 * List all visible windows with their HWND, title, process, and size. Use -a to filter by app name. Use the HWND with -w to target a specific window.
 */
export async function uiListWindows(options: UiListWindowsOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'list-windows'];
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.showHidden) args.push('--show-hidden');
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui pen
// ---------------------------------------------------------------------------

export interface UiPenOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Pen contact point as screen coordinates x,y (as reported by 'ui inspect'). Defaults to the selector's element center. Ignored when --path is given. */
  at?: string;
  /** Total glide time in milliseconds distributed across the stroke path segments (default: ~10 ms per segment). */
  durationMs?: number;
  /** Use the eraser end of the pen instead of the tip. */
  eraser?: boolean;
  /** Format output as JSON */
  json?: boolean;
  /** Ink stroke path as a whitespace-separated list of x,y pairs, e.g. "10,10 20,30 40,50". */
  path?: string;
  /** Pen pressure from 0.0 to 1.0 (default: 0.5). */
  pressure?: number;
  /** Pen tilt along the x-axis in degrees (-90 to 90, default: 0). */
  tiltX?: number;
  /** Pen tilt along the y-axis in degrees (-90 to 90, default: 0). */
  tiltY?: number;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Inject synthetic pen/stylus input using the Windows synthetic-pointer API. Taps or draws ink strokes with configurable pressure, tilt and eraser mode, at an element's center or explicit screen x,y coordinates. Requires an unlocked, interactive desktop with the target window foregroundable (Windows 10 1809+).
 */
export async function uiPen(options: UiPenOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'pen'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.at) args.push('--at', options.at);
  if (options.durationMs !== undefined) args.push('--duration-ms', options.durationMs.toString());
  if (options.eraser) args.push('--eraser');
  if (options.json) args.push('--json');
  if (options.path) args.push('--path', options.path);
  if (options.pressure !== undefined) args.push('--pressure', options.pressure.toString());
  if (options.tiltX !== undefined) args.push('--tilt-x', options.tiltX.toString());
  if (options.tiltY !== undefined) args.push('--tilt-y', options.tiltY.toString());
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui record
// ---------------------------------------------------------------------------

export interface UiRecordOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Capture from screen DC via BitBlt (includes popups/overlays not owned by the target). */
  captureScreen?: boolean;
  /** Recording duration in seconds. 0 records until Ctrl+C or redirected-stdin newline/EOF. */
  durationSec?: number;
  /** Frames per second to capture */
  fps?: number;
  /** Write timestamped JPEGs, frames.ndjson, and manifest.json to <output-name>.frames. Supports 1-30 fps and max-edge 64-4096 (default 1280), with a 1 GiB frame-data cap. */
  frames?: boolean;
  /** Format output as JSON */
  json?: boolean;
  /** Downscale so the longest edge is at most this many pixels (0 = no downscale) */
  maxEdge?: number;
  /** Save output to this file path. */
  output?: string;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

// _uiRecordGenerated: options interface exported above; function body omitted — use the
//   public guarded wrapper (e.g. uiRecord from ui-record-guard.ts) instead.

// ---------------------------------------------------------------------------
// ui screenshot
// ---------------------------------------------------------------------------

export interface UiScreenshotOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Capture from screen DC via BitBlt (includes popups/overlays not owned by the target). */
  captureScreen?: boolean;
  /** Bring the target window to the foreground before capture. Already implied by --capture-screen. */
  focus?: boolean;
  /** Format output as JSON */
  json?: boolean;
  /** Save output to this file path. */
  output?: string;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Capture the target window or element as a PNG image. When multiple windows exist (e.g., dialogs), captures each to a separate file. With --json, returns file path and dimensions. Use --capture-screen for popup overlays.
 */
export async function uiScreenshot(options: UiScreenshotOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'screenshot'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.captureScreen) args.push('--capture-screen');
  if (options.focus) args.push('--focus');
  if (options.json) args.push('--json');
  if (options.output) args.push('--output', options.output);
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui scroll
// ---------------------------------------------------------------------------

export interface UiScrollOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Scroll direction: up, down, left, right */
  direction?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Scroll to position: top, bottom */
  to?: string;
  /** Rotate the mouse wheel over the element by this many notches (1 = one notch up, -1 = one notch down). Synthesizes real wheel input instead of using ScrollPattern. */
  wheel?: number;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Scroll a container element using ScrollPattern. Use --direction to scroll incrementally, --to to jump to top/bottom, or --wheel to synthesize mouse-wheel input.
 */
export async function uiScroll(options: UiScrollOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'scroll'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.direction) args.push('--direction', options.direction);
  if (options.json) args.push('--json');
  if (options.to) args.push('--to', options.to);
  if (options.wheel !== undefined) args.push('--wheel', options.wheel.toString());
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui scroll-into-view
// ---------------------------------------------------------------------------

export interface UiScrollIntoViewOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Scroll the specified element into the visible area using UIA ScrollItemPattern.
 */
export async function uiScrollIntoView(options: UiScrollIntoViewOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'scroll-into-view'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui search
// ---------------------------------------------------------------------------

export interface UiSearchOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Maximum search results */
  max?: number;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Search the element tree for elements matching a text query. Returns all matches with semantic slugs.
 */
export async function uiSearch(options: UiSearchOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'search'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.max !== undefined) args.push('--max', options.max.toString());
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui send-keys
// ---------------------------------------------------------------------------

export interface UiSendKeysOptions extends CommonOptions {
  /** Keys to send. Whitespace-separated tokens: named keys (down, enter, tab, esc, f5), modifier combos (ctrl+shift+t, alt+f4), raw virtual keys (vk=0x42), or literal text (hello). Use text=<literal> to type a single value verbatim when it would otherwise be read as a key name or combo (text=enter types "enter"; text=ctrl+a types "ctrl+a"); backslash escapes \s \t \n \r \\ are supported (text=a\s\sb types "a b"). To type the whole argument literally without escaping each token, pass --verbatim instead. Quote multi-token strings, e.g. "ctrl+a delete". */
  keys?: string;
  /** Allow synthesizing system-/shell-reserved combos (win+<key>, alt+f4, alt+tab, ctrl+esc, …) via --via send-input, which are refused by default because they act on the OS/shell beyond the target app. Opt in to drive global hotkeys (e.g. PowerToys' win+shift+v, win+r). No effect on --via post-message (already window-scoped; a warning is emitted if set without send-input). Note: win+l and ctrl+alt+del stay blocked even with this flag — win+l locks the workstation (LockWorkStation() via the shell hook), which is unrecoverable from automation, and ctrl+alt+del is a Secure Attention Sequence (SAS) that Windows drops from injected input regardless of this flag, so it can never take effect. */
  allowSystemKeys?: boolean;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Optional selector (slug or text) to focus before sending keys. */
  target?: string;
  /** Type the entire keys argument as literal text — no named-key, combo, or vk= interpretation, and exact whitespace preserved. The whole-argument form of the per-token text= escape: --verbatim "down down enter" types the words instead of pressing Down, Down, Enter. */
  verbatim?: boolean;
  /** Transport: post-message (default, HWND-targeted, bypasses UIPI; typed text raises TextChanged but not a per-character KeyDown) or send-input (OS-wide; typed text raises a real per-character KeyDown + TextChanged). Named keys and combos raise KeyDown on both, but keyboard accelerators/shortcuts (KeyboardAccelerator, e.g. ctrl+t) only fire via send-input. post-message targets the focused child control and works for classic Win32/WinForms controls, but WinUI 3 / UWP / XAML controls are windowless and ignore posted messages — use send-input for those (a warning is emitted when the target looks like a XAML app). */
  via?: string;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Send synthetic keyboard input to a window. Supports named keys (down, enter, tab), modifier combos (ctrl+shift+t), raw virtual keys (vk=0xNN), and literal text. Use --verbatim to type the whole argument literally, or --target to focus an element first. Two transports via --via: post-message (default, HWND-targeted, bypasses UIPI) or send-input (OS-wide). For per-keystroke KeyDown on typed text (e.g. a WinUI 3/WPF TextBox), use --via send-input.
 */
export async function uiSendKeys(options: UiSendKeysOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'send-keys'];
  if (options.keys) args.push(options.keys);
  if (options.allowSystemKeys) args.push('--allow-system-keys');
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.target) args.push('--target', options.target);
  if (options.verbatim) args.push('--verbatim');
  if (options.via) args.push('--via', options.via);
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui set-value
// ---------------------------------------------------------------------------

export interface UiSetValueOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Value to set (text for TextBox/ComboBox, number for Slider) */
  value?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Set a value on an element programmatically. Works for TextBox, ComboBox, Slider, and other editable controls via UIA ValuePattern/RangeValuePattern, with a LegacyIAccessible (put_accValue) fallback for TextPattern-only edit controls — no app foreground required. Some rich text controls (e.g. WinUI 3 RichEditBox and WPF RichTextBox) don't support setting their value programmatically — use the 'send-keys' command with '--via send-input' to type into them instead. Usage: winapp ui set-value <selector> <value> -a <app>
 */
export async function uiSetValue(options: UiSetValueOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'set-value'];
  if (options.selector) args.push(options.selector);
  if (options.value) args.push(options.value);
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui status
// ---------------------------------------------------------------------------

export interface UiStatusOptions extends CommonOptions {
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Connect to a target app and display connection info.
 */
export async function uiStatus(options: UiStatusOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'status'];
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui touch
// ---------------------------------------------------------------------------

export interface UiTouchOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Explicit start point as screen coordinates x,y (as reported by 'ui inspect'). Defaults to the selector's element center. */
  at?: string;
  /** Swipe direction: right (default), left, up, or down. Combined with --distance to compute the end point when --to-point is not given. */
  direction?: string;
  /** Distance in pixels for pinch/stretch (finger spread) or swipe. */
  distance?: number;
  /** Glide time in milliseconds for moving gestures (swipe/pinch/stretch). */
  durationMs?: number;
  /** Number of touch contacts (default: 1). Pinch/stretch always use 2. */
  fingers?: number;
  /** Gesture to perform: tap, double-tap, long-press, swipe, pinch, stretch (default: tap). */
  gesture?: string;
  /** Milliseconds to hold contacts down before lifting (long-press hold time). Defaults to 500 ms when --gesture long-press is used and this option is not set. */
  holdMs?: number;
  /** Format output as JSON */
  json?: boolean;
  /** End point x,y for a swipe (screen coordinates). Takes precedence over --direction. */
  toPoint?: string;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Inject synthetic touch input using the Windows touch-injection API. Supports tap, double-tap, long-press, swipe, pinch and stretch gestures at an element's center or explicit screen x,y coordinates. Requires an unlocked, interactive desktop with the target window foregroundable.
 */
export async function uiTouch(options: UiTouchOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'touch'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.at) args.push('--at', options.at);
  if (options.direction) args.push('--direction', options.direction);
  if (options.distance !== undefined) args.push('--distance', options.distance.toString());
  if (options.durationMs !== undefined) args.push('--duration-ms', options.durationMs.toString());
  if (options.fingers !== undefined) args.push('--fingers', options.fingers.toString());
  if (options.gesture) args.push('--gesture', options.gesture);
  if (options.holdMs !== undefined) args.push('--hold-ms', options.holdMs.toString());
  if (options.json) args.push('--json');
  if (options.toPoint) args.push('--to-point', options.toPoint);
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui wait-for
// ---------------------------------------------------------------------------

export interface UiWaitForOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Use substring matching for --value instead of exact match */
  contains?: boolean;
  /** Wait for element to disappear instead of appear */
  gone?: boolean;
  /** Format output as JSON */
  json?: boolean;
  /** Property name to read or filter on */
  property?: string;
  /** Timeout in milliseconds */
  timeout?: number;
  /** Wait for element value to equal this string. Uses smart fallback (TextPattern -> ValuePattern -> Name). Combine with --property to check a specific property instead. */
  value?: string;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Wait for an element to appear, disappear, or have a property reach a target value. Polls at 100ms intervals until condition met or timeout.
 */
export async function uiWaitFor(options: UiWaitForOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'wait-for'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.contains) args.push('--contains');
  if (options.gone) args.push('--gone');
  if (options.json) args.push('--json');
  if (options.property) args.push('--property', options.property);
  if (options.timeout !== undefined) args.push('--timeout', options.timeout.toString());
  if (options.value) args.push('--value', options.value);
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// unregister
// ---------------------------------------------------------------------------

export interface UnregisterOptions extends CommonOptions {
  /** Skip the install-location directory check and unregister even if the package was registered from a different project tree */
  force?: boolean;
  /** Format output as JSON */
  json?: boolean;
  /** Path to the Package.appxmanifest (default: auto-detect from current directory) */
  manifest?: string;
}

/**
 * Unregisters a sideloaded development package. Only removes packages registered in development mode (e.g., via 'winapp run' or 'create-debug-identity').
 */
export async function unregister(options: UnregisterOptions = {}): Promise<WinappResult> {
  const args: string[] = ['unregister'];
  if (options.force) args.push('--force');
  if (options.json) args.push('--json');
  if (options.manifest) args.push('--manifest', options.manifest);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// update
// ---------------------------------------------------------------------------

export interface UpdateOptions extends CommonOptions {
  /** SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation) */
  setupSdks?: SdkInstallMode;
}

/**
 * Check for and install newer SDK versions. Updates winapp.yaml with latest versions and reinstalls packages. Requires existing winapp.yaml (created by 'init'). Use --setup-sdks preview for preview SDKs. To reinstall current versions without updating, use 'restore' instead.
 */
export async function update(options: UpdateOptions = {}): Promise<WinappResult> {
  const args: string[] = ['update'];
  if (options.setupSdks) args.push('--setup-sdks', options.setupSdks);
  return execCommand(args, options);
}
