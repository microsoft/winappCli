// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;
using static WinApp.Cli.Services.BuildToolsService;

namespace WinApp.Cli.Commands;

internal partial class ManifestValidateCommand : Command
{
    public static Argument<FileInfo> ManifestArgument { get; }

    static ManifestValidateCommand()
    {
        ManifestArgument = new Argument<FileInfo>("manifest-path")
        {
            Description = "Path to AppxManifest.xml or Package.appxmanifest file to validate"
        };
        ManifestArgument.AcceptExistingOnly();
    }

    public ManifestValidateCommand() : base("validate", "Validate an AppxManifest.xml file against the schema")
    {
        Arguments.Add(ManifestArgument);
    }

    /// <summary>
    /// Represents a validation error with line number and optional suggestion.
    /// </summary>
    private sealed record ValidationError(int LineNumber, string Message, string? Suggestion = null)
    {
        public string Format()
        {
            var result = LineNumber > 0
                ? $"Error at Line {LineNumber}:\n  {Message}"
                : $"Error:\n  {Message}";
            if (!string.IsNullOrEmpty(Suggestion))
            {
                result += $"\n  Suggestion: {Suggestion}";
            }
            return result;
        }
    }

    public partial class Handler(
        IStatusService statusService,
        IBuildToolsService buildToolsService,
        ILogger<ManifestValidateCommand> logger) : AsynchronousCommandLineAction
    {
        // AppxManifest namespaces
        private static readonly XNamespace FoundationNs = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        private static readonly XNamespace UapNs = "http://schemas.microsoft.com/appx/manifest/uap/windows10";

        // Version pattern: Major.Minor.Build.Revision (all non-negative integers)
        [GeneratedRegex(@"^\d+\.\d+\.\d+\.\d+$")]
        private static partial Regex VersionPattern();
        
        // Publisher pattern: Must start with CN= or other X.500 distinguished name attributes
        [GeneratedRegex(@"^(CN|O|OU|E|C|S|L|STREET|T|G|I|SN|DC|SERIALNUMBER|Description|PostalCode|POBox|Phone|X21Address|dnQualifier|)=.+", RegexOptions.IgnoreCase)]
        private static partial Regex PublisherPattern();

        // Package name pattern: letters, numbers, dots, and hyphens
        [GeneratedRegex(@"^[A-Za-z0-9\.\-]+$")]
        private static partial Regex PackageNamePattern();

        // Application ID pattern: starts with letter, contains only letters and numbers
        [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*$")]
        private static partial Regex ApplicationIdPattern();

        // MakeAppx line number pattern: "Line X, Column Y" or "Line X,"
        [GeneratedRegex(@"Line\s+(\d+)", RegexOptions.IgnoreCase)]
        private static partial Regex MakeAppxLinePattern();

        // Error code pattern for MakeAppx output
        [GeneratedRegex(@"^error [A-Z0-9]+:\s*")]
        private static partial Regex ErrorCodePattern();

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var manifestPath = parseResult.GetRequiredValue(ManifestArgument);

            logger.LogDebug("Validating manifest at: {ManifestPath}", manifestPath.FullName);

            return await statusService.ExecuteWithStatusAsync("Validating AppxManifest", async (taskContext, cancellationToken) =>
            {
                try
                {
                    // Run MakeAppx validation (source of truth)
                    var makeAppxErrors = await ValidateWithMakeAppxAsync(manifestPath, taskContext, cancellationToken);

                    if (makeAppxErrors.Count == 0)
                    {
                        // MakeAppx says valid - we're done!
                        return (0, $"{UiSymbols.Check} Manifest is valid: {manifestPath.Name}");
                    }

                    // MakeAppx found errors - run structural validation to get friendly messages
                    var structuralErrors = await ValidateStructuralAsync(manifestPath, cancellationToken);

                    // Merge errors: use our friendly message if line numbers match, otherwise use MakeAppx message
                    var mergedErrors = MergeErrors(makeAppxErrors, structuralErrors);

                    // Build error output
                    var errorLines = new List<string>();
                    foreach (var error in mergedErrors)
                    {
                        var lineInfo = error.LineNumber > 0 ? $" at line {error.LineNumber}" : "";
                        errorLines.Add($"{UiSymbols.Error} Error{lineInfo}: {error.Message}");
                        
                        if (!string.IsNullOrEmpty(error.Suggestion))
                        {
                            errorLines.Add($"  Tip: {error.Suggestion}");
                        }
                    }
                    errorLines.Add($"{UiSymbols.Error} Manifest validation failed with {mergedErrors.Count} error(s).");

                    // Return the error output as the completed message (starts with [ so no prefix added)
                    return (1, string.Join("\n", errorLines));
                }
                catch (XmlException ex)
                {
                    var errorMessage = FormatXmlError(ex);
                    var output = $"{errorMessage}\n{UiSymbols.Error} Manifest is not valid XML.";
                    return (1, output);
                }
                catch (Exception ex)
                {
                    return (1, $"{UiSymbols.Error} Error validating manifest: {ex.Message}");
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Merges MakeAppx errors with structural validation errors.
        /// If a structural error has the same line number as a MakeAppx error, use the structural error (friendlier message).
        /// Otherwise, use the MakeAppx error.
        /// </summary>
        private static List<ValidationError> MergeErrors(List<ValidationError> makeAppxErrors, List<ValidationError> structuralErrors)
        {
            var result = new List<ValidationError>();
            var structuralByLine = structuralErrors
                .Where(e => e.LineNumber > 0)
                .GroupBy(e => e.LineNumber)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var makeAppxError in makeAppxErrors)
            {
                if (makeAppxError.LineNumber > 0 && structuralByLine.TryGetValue(makeAppxError.LineNumber, out var structuralError))
                {
                    // We have a matching structural error - use it for the friendly message
                    result.Add(structuralError);
                }
                else
                {
                    // No matching structural error - use MakeAppx error as-is
                    result.Add(makeAppxError);
                }
            }

            return result;
        }

        /// <summary>
        /// Validates the manifest using MakeAppx.exe with /nv flag (skips file validation but keeps schema validation).
        /// </summary>
        private async Task<List<ValidationError>> ValidateWithMakeAppxAsync(
            FileInfo manifestPath,
            ConsoleTasks.TaskContext taskContext,
            CancellationToken cancellationToken)
        {
            var errors = new List<ValidationError>();

            // Create a temporary directory for validation
            var tempDir = Directory.CreateTempSubdirectory("winapp-manifest-validate-");
            var tempMsix = Path.Combine(tempDir.FullName, "validate.msix");

            try
            {
                // Copy the manifest to the temp directory as AppxManifest.xml
                var destManifest = Path.Combine(tempDir.FullName, "AppxManifest.xml");
                File.Copy(manifestPath.FullName, destManifest, overwrite: true);

                // Run MakeAppx.exe with /nv to skip file existence validation but keep schema validation
                // The /nv flag skips: file existence checks, ContentGroupMap validation, Protocol/FileTypeAssociation semantic checks
                // But it STILL validates the manifest schema!
                var arguments = $@"pack /nv /o /d ""{tempDir.FullName}"" /p ""{tempMsix}""";

                logger.LogDebug("Running MakeAppx validation: {Arguments}", arguments);

                var (stdout, stderr) = await buildToolsService.RunBuildToolAsync(
                    new MakeAppxTool(),
                    arguments,
                    taskContext,
                    printErrors: false, // We'll parse and format errors ourselves
                    cancellationToken: cancellationToken);

                // Parse MakeAppx output for validation errors
                var combinedOutput = stdout + "\n" + stderr;
                errors.AddRange(ParseMakeAppxErrors(combinedOutput));
            }
            catch (InvalidBuildToolException ex)
            {
                // MakeAppx returned non-zero exit code - parse errors from output
                var combinedOutput = ex.Stdout + "\n" + ex.Stderr;
                var parsedErrors = ParseMakeAppxErrors(combinedOutput);
                
                if (parsedErrors.Count > 0)
                {
                    errors.AddRange(parsedErrors);
                }
                else
                {
                    // Couldn't parse specific errors, show generic message
                    errors.Add(new ValidationError(0, $"MakeAppx validation failed: {ex.Message}"));
                    if (!string.IsNullOrWhiteSpace(ex.Stderr))
                    {
                        errors.Add(new ValidationError(0, ex.Stderr.Trim()));
                    }
                }
            }
            finally
            {
                // Clean up temp directory
                try
                {
                    tempDir.Delete(recursive: true);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            return errors;
        }

        /// <summary>
        /// Parses MakeAppx.exe output for validation error messages.
        /// </summary>
        private static List<ValidationError> ParseMakeAppxErrors(string output)
        {
            var errors = new List<ValidationError>();
            
            // MakeAppx error patterns:
            // "MakeAppx : error: Manifest validation error: Line X, Column Y, Reason: ..."
            // "MakeAppx : error: Error info: error CXXXXXX: App manifest validation error: ..."
            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (line.Contains("MakeAppx : error:", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract the error message after "MakeAppx : error:"
                    var errorIndex = line.IndexOf("MakeAppx : error:", StringComparison.OrdinalIgnoreCase);
                    var errorMessage = line[(errorIndex + "MakeAppx : error:".Length)..].Trim();

                    // Skip generic "Package creation failed" messages
                    if (errorMessage.StartsWith("Package creation failed", StringComparison.OrdinalIgnoreCase) ||
                        errorMessage.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Extract line number if present
                    int lineNumber = 0;
                    var lineMatch = MakeAppxLinePattern().Match(errorMessage);
                    if (lineMatch.Success && int.TryParse(lineMatch.Groups[1].Value, out var parsedLine))
                    {
                        lineNumber = parsedLine;
                    }

                    // Format the error nicely
                    if (errorMessage.StartsWith("Manifest validation error:", StringComparison.OrdinalIgnoreCase))
                    {
                        errorMessage = errorMessage["Manifest validation error:".Length..].Trim();
                    }
                    else if (errorMessage.StartsWith("Error info:", StringComparison.OrdinalIgnoreCase))
                    {
                        errorMessage = errorMessage["Error info:".Length..].Trim();
                        // Remove error code prefix like "error C00CE169:"
                        var codeMatch = ErrorCodePattern().Match(errorMessage);
                        if (codeMatch.Success)
                        {
                            errorMessage = errorMessage[codeMatch.Length..];
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(errorMessage))
                    {
                        errors.Add(new ValidationError(lineNumber, errorMessage));
                    }
                }
            }

            return errors;
        }

        private static async Task<List<ValidationError>> ValidateStructuralAsync(
            FileInfo manifestPath,
            CancellationToken cancellationToken)
        {
            var errors = new List<ValidationError>();
            
            // Load the XML with line info for better error reporting
            XDocument doc;
            using (var stream = manifestPath.OpenRead())
            {
                var settings = new XmlReaderSettings
                {
                    Async = true,
                    IgnoreWhitespace = false,
                    IgnoreComments = true
                };
                
                using var reader = XmlReader.Create(stream, settings);
                doc = await XDocument.LoadAsync(reader, LoadOptions.SetLineInfo, cancellationToken);
            }

            var root = doc.Root;
            if (root == null)
            {
                errors.Add(new ValidationError(1, "Empty XML document"));
                return errors;
            }

            // Validate root element is Package
            if (root.Name.LocalName != "Package")
            {
                AddError(errors, root, $"Root element must be 'Package', found '{root.Name.LocalName}'");
                return errors;
            }

            // Validate required elements
            ValidateIdentity(root, errors);
            ValidateProperties(root, errors);
            ValidateDependencies(root, errors);
            ValidateResources(root, errors);
            ValidateApplications(root, errors);

            return errors;
        }

        private static void ValidateIdentity(XElement root, List<ValidationError> errors)
        {
            var identity = root.Element(FoundationNs + "Identity");
            if (identity == null)
            {
                AddError(errors, root, "Missing required element: Identity",
                    "Add an <Identity> element with Name, Publisher, and Version attributes.");
                return;
            }

            // Validate Name attribute
            var name = identity.Attribute("Name")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                AddError(errors, identity, "Identity element missing required 'Name' attribute",
                    "Add a Name attribute (e.g., Name=\"MyApp\").");
            }
            else if (name.Length > 50 || !PackageNamePattern().IsMatch(name))
            {
                AddError(errors, identity, $"Invalid package name: '{name}'",
                    "Package name must be 1-50 characters and contain only letters, numbers, dots, and hyphens.");
            }

            // Validate Publisher attribute
            var publisher = identity.Attribute("Publisher")?.Value;
            if (string.IsNullOrWhiteSpace(publisher))
            {
                AddError(errors, identity, "Identity element missing required 'Publisher' attribute",
                    "Add a Publisher attribute (e.g., Publisher=\"CN=YourName\").");
            }
            else if (!PublisherPattern().IsMatch(publisher))
            {
                AddError(errors, identity, $"Invalid publisher format: '{publisher}'",
                    "Publisher must be a valid X.500 distinguished name starting with CN=, O=, etc. (e.g., CN=Contoso).");
            }

            // Validate Version attribute
            var version = identity.Attribute("Version")?.Value;
            if (string.IsNullOrWhiteSpace(version))
            {
                AddError(errors, identity, "Identity element missing required 'Version' attribute",
                    "Add a Version attribute (e.g., Version=\"1.0.0.0\").");
            }
            else if (!VersionPattern().IsMatch(version))
            {
                AddError(errors, identity, $"Invalid version format: '{version}'",
                    "Version must be in format 'Major.Minor.Build.Revision' (e.g., 1.0.0.0).");
            }
        }

        private static void ValidateProperties(XElement root, List<ValidationError> errors)
        {
            var properties = root.Element(FoundationNs + "Properties");
            if (properties == null)
            {
                AddError(errors, root, "Missing required element: Properties",
                    "Add a <Properties> element with DisplayName, PublisherDisplayName, and Logo.");
                return;
            }

            // Validate DisplayName
            var displayName = properties.Element(FoundationNs + "DisplayName");
            if (displayName == null || string.IsNullOrWhiteSpace(displayName.Value))
            {
                AddError(errors, properties, "Properties element missing required 'DisplayName' element",
                    "Add a <DisplayName> element with your app's display name.");
            }
            else if (displayName.Value.Length > 256)
            {
                AddError(errors, displayName, "DisplayName exceeds maximum length of 256 characters");
            }

            // Validate PublisherDisplayName
            var publisherDisplayName = properties.Element(FoundationNs + "PublisherDisplayName");
            if (publisherDisplayName == null || string.IsNullOrWhiteSpace(publisherDisplayName.Value))
            {
                AddError(errors, properties, "Properties element missing required 'PublisherDisplayName' element",
                    "Add a <PublisherDisplayName> element with your publisher's display name.");
            }

            // Validate Logo
            var logo = properties.Element(FoundationNs + "Logo");
            if (logo == null || string.IsNullOrWhiteSpace(logo.Value))
            {
                AddError(errors, properties, "Properties element missing required 'Logo' element",
                    "Add a <Logo> element with path to your store logo (e.g., Assets\\StoreLogo.png).");
            }
        }

        private static void ValidateDependencies(XElement root, List<ValidationError> errors)
        {
            var dependencies = root.Element(FoundationNs + "Dependencies");
            if (dependencies == null)
            {
                AddError(errors, root, "Missing required element: Dependencies",
                    "Add a <Dependencies> element with TargetDeviceFamily.");
                return;
            }

            // Validate TargetDeviceFamily
            var targetDeviceFamily = dependencies.Element(FoundationNs + "TargetDeviceFamily");
            if (targetDeviceFamily == null)
            {
                AddError(errors, dependencies, "Dependencies element missing required 'TargetDeviceFamily' element",
                    "Add a <TargetDeviceFamily> element specifying the target Windows version.");
                return;
            }

            var familyName = targetDeviceFamily.Attribute("Name")?.Value;
            if (string.IsNullOrWhiteSpace(familyName))
            {
                AddError(errors, targetDeviceFamily, "TargetDeviceFamily missing required 'Name' attribute",
                    "Add Name attribute (e.g., Name=\"Windows.Desktop\" or Name=\"Windows.Universal\").");
            }

            var minVersion = targetDeviceFamily.Attribute("MinVersion")?.Value;
            if (string.IsNullOrWhiteSpace(minVersion))
            {
                AddError(errors, targetDeviceFamily, "TargetDeviceFamily missing required 'MinVersion' attribute",
                    "Add MinVersion attribute (e.g., MinVersion=\"10.0.18362.0\").");
            }
            else if (!VersionPattern().IsMatch(minVersion))
            {
                AddError(errors, targetDeviceFamily, $"Invalid MinVersion format: '{minVersion}'",
                    "MinVersion must be in format 'Major.Minor.Build.Revision' (e.g., 10.0.18362.0).");
            }

            var maxVersionTested = targetDeviceFamily.Attribute("MaxVersionTested")?.Value;
            if (string.IsNullOrWhiteSpace(maxVersionTested))
            {
                AddError(errors, targetDeviceFamily, "TargetDeviceFamily missing required 'MaxVersionTested' attribute",
                    "Add MaxVersionTested attribute (e.g., MaxVersionTested=\"10.0.26100.0\").");
            }
            else if (!VersionPattern().IsMatch(maxVersionTested))
            {
                AddError(errors, targetDeviceFamily, $"Invalid MaxVersionTested format: '{maxVersionTested}'",
                    "MaxVersionTested must be in format 'Major.Minor.Build.Revision' (e.g., 10.0.26100.0).");
            }
        }

        private static void ValidateResources(XElement root, List<ValidationError> errors)
        {
            var resources = root.Element(FoundationNs + "Resources");
            if (resources == null)
            {
                AddError(errors, root, "Missing required element: Resources",
                    "Add a <Resources> element with at least one Resource element.");
                return;
            }

            var resourceElements = resources.Elements(FoundationNs + "Resource").ToList();
            if (resourceElements.Count == 0)
            {
                AddError(errors, resources, "Resources element must contain at least one Resource element",
                    "Add a <Resource Language=\"en-us\"/> element.");
            }
        }

        private static void ValidateApplications(XElement root, List<ValidationError> errors)
        {
            var applications = root.Element(FoundationNs + "Applications");
            if (applications == null)
            {
                AddError(errors, root, "Missing required element: Applications",
                    "Add an <Applications> element with at least one Application.");
                return;
            }

            var appElements = applications.Elements(FoundationNs + "Application").ToList();
            if (appElements.Count == 0)
            {
                AddError(errors, applications, "Applications element must contain at least one Application element",
                    "Add an <Application> element defining your app's entry point.");
                return;
            }

            foreach (var app in appElements)
            {
                ValidateApplication(app, errors);
            }
        }

        private static void ValidateApplication(XElement app, List<ValidationError> errors)
        {
            // Validate Id
            var id = app.Attribute("Id")?.Value;
            if (string.IsNullOrWhiteSpace(id))
            {
                AddError(errors, app, "Application element missing required 'Id' attribute",
                    "Add an Id attribute (e.g., Id=\"App\").");
            }
            else if (!ApplicationIdPattern().IsMatch(id))
            {
                AddError(errors, app, $"Invalid Application Id: '{id}'",
                    "Application Id must start with a letter and contain only letters and numbers.");
            }

            // Validate Executable
            var executable = app.Attribute("Executable")?.Value;
            if (string.IsNullOrWhiteSpace(executable))
            {
                AddError(errors, app, "Application element missing required 'Executable' attribute",
                    "Add an Executable attribute (e.g., Executable=\"MyApp.exe\").");
            }

            // Validate EntryPoint
            var entryPoint = app.Attribute("EntryPoint")?.Value;
            if (string.IsNullOrWhiteSpace(entryPoint))
            {
                AddError(errors, app, "Application element missing required 'EntryPoint' attribute",
                    "Add an EntryPoint attribute (e.g., EntryPoint=\"Windows.FullTrustApplication\").");
            }

            // Validate VisualElements
            var visualElements = app.Element(UapNs + "VisualElements");
            if (visualElements == null)
            {
                AddError(errors, app, "Application element missing required 'uap:VisualElements' element",
                    "Add a <uap:VisualElements> element with DisplayName, Description, and logo attributes.");
                return;
            }

            var veDisplayName = visualElements.Attribute("DisplayName")?.Value;
            if (string.IsNullOrWhiteSpace(veDisplayName))
            {
                AddError(errors, visualElements, "VisualElements missing required 'DisplayName' attribute",
                    "Add a DisplayName attribute to VisualElements.");
            }

            var description = visualElements.Attribute("Description")?.Value;
            if (string.IsNullOrWhiteSpace(description))
            {
                AddError(errors, visualElements, "VisualElements missing required 'Description' attribute",
                    "Add a Description attribute to VisualElements.");
            }

            var square150 = visualElements.Attribute("Square150x150Logo")?.Value;
            if (string.IsNullOrWhiteSpace(square150))
            {
                AddError(errors, visualElements, "VisualElements missing required 'Square150x150Logo' attribute",
                    "Add a Square150x150Logo attribute (e.g., Square150x150Logo=\"Assets\\Square150x150Logo.png\").");
            }

            var square44 = visualElements.Attribute("Square44x44Logo")?.Value;
            if (string.IsNullOrWhiteSpace(square44))
            {
                AddError(errors, visualElements, "VisualElements missing required 'Square44x44Logo' attribute",
                    "Add a Square44x44Logo attribute (e.g., Square44x44Logo=\"Assets\\Square44x44Logo.png\").");
            }
        }

        private static void AddError(List<ValidationError> errors, XElement element, string message, string? suggestion = null)
        {
            var lineInfo = (IXmlLineInfo)element;
            var lineNumber = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0;
            errors.Add(new ValidationError(lineNumber, message, suggestion));
        }

        private static string FormatXmlError(XmlException ex)
        {
            var suggestion = GetXmlFixSuggestion(ex.Message);
            if (ex.LineNumber > 0)
            {
                return $"XML Error at line {ex.LineNumber}, position {ex.LinePosition}:\n" +
                       $"  {ex.Message}\n" +
                       (string.IsNullOrEmpty(suggestion) ? "" : $"  Suggestion: {suggestion}");
            }

            return $"XML Error: {ex.Message}";
        }

        private static string GetXmlFixSuggestion(string errorMessage)
        {
            if (errorMessage.Contains("unexpected end", StringComparison.OrdinalIgnoreCase))
            {
                return "The XML file appears to be truncated. Ensure all elements are properly closed.";
            }

            if (errorMessage.Contains("encoding", StringComparison.OrdinalIgnoreCase))
            {
                return "The XML file should use UTF-8 encoding. Check the XML declaration at the top of the file.";
            }

            if (errorMessage.Contains("invalid character", StringComparison.OrdinalIgnoreCase))
            {
                return "Remove or encode invalid characters. Special characters should use XML entities (e.g., &amp; for &).";
            }

            if (errorMessage.Contains("not closed", StringComparison.OrdinalIgnoreCase) ||
                errorMessage.Contains("end tag", StringComparison.OrdinalIgnoreCase))
            {
                return "Check that all XML elements have matching opening and closing tags.";
            }

            return "Check the XML syntax. Ensure proper tag formatting, attribute quoting, and character encoding.";
        }
    }
}
