// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml;
using System.Xml.Linq;
using System.Text;
using WinApp.Cli.Models;

namespace WinApp.Cli.Commands;

internal partial class MigrateCommand
{
    public partial class Handler
    {
        private const string FoundationManifestNamespace =
            "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        private const string UapManifestNamespace =
            "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        private const string Uap3ManifestNamespace =
            "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
        private const string RestrictedCapabilitiesNamespace =
            "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
        private const string Desktop11ManifestNamespace =
            "http://schemas.microsoft.com/appx/manifest/desktop/windows10/11";

        private static readonly XNamespace FoundationManifest =
            FoundationManifestNamespace;
        private static readonly XNamespace UapManifest =
            UapManifestNamespace;
        private static readonly XNamespace Uap3Manifest =
            Uap3ManifestNamespace;
        private static readonly XNamespace RestrictedCapabilitiesManifest =
            RestrictedCapabilitiesNamespace;
        private static readonly XNamespace Desktop11Manifest =
            Desktop11ManifestNamespace;

        private static readonly XName FoundationApplications =
            FoundationManifest + "Applications";
        private static readonly XName FoundationApplication =
            FoundationManifest + "Application";
        private static readonly XName FoundationExtensions =
            FoundationManifest + "Extensions";
        private static readonly XName FoundationTargetDeviceFamily =
            FoundationManifest + "TargetDeviceFamily";
        private static readonly XName RestrictedCapability =
            RestrictedCapabilitiesManifest + "Capability";
        private static readonly XName UapExtension =
            UapManifest + "Extension";
        private static readonly XName UapProtocol =
            UapManifest + "Protocol";
        private static readonly XName UapFileTypeAssociation =
            UapManifest + "FileTypeAssociation";
        private static readonly XName UapDisplayName =
            UapManifest + "DisplayName";
        private static readonly XName UapLogo =
            UapManifest + "Logo";
        private static readonly XName UapSupportedFileTypes =
            UapManifest + "SupportedFileTypes";
        private static readonly XName UapFileType =
            UapManifest + "FileType";
        private static readonly XName Uap3Extension =
            Uap3Manifest + "Extension";
        private static readonly XName Uap3Protocol =
            Uap3Manifest + "Protocol";
        private static readonly XName Uap3FileTypeAssociation =
            Uap3Manifest + "FileTypeAssociation";
        private static readonly XName Desktop11AppLifecycleBehavior =
            Desktop11Manifest + "AppLifecycleBehavior";

        private static readonly HashSet<string> SupportedActivationCategories =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "windows.protocol",
                "windows.fileTypeAssociation"
            };

        private static readonly HashSet<XName> SafeProtocolAttributes =
            new()
            {
                XName.Get("Name"),
                XName.Get("DesiredView"),
                XName.Get("ReturnResults")
            };

        private static readonly HashSet<XName> CategoryOnlyAttributes =
            new()
            {
                XName.Get("Category")
            };

        private static readonly HashSet<XName> SafeProtocolChildren =
            new()
            {
                UapDisplayName,
                UapLogo
            };

        private static readonly HashSet<XName> SafeFileAssociationAttributes =
            new()
            {
                XName.Get("Name"),
                XName.Get("DesiredView"),
                XName.Get("MultiSelectModel")
            };

        private static readonly HashSet<XName> SafeFileAssociationChildren =
            new()
            {
                UapDisplayName,
                UapLogo,
                UapSupportedFileTypes
            };

        private sealed record SourceActivationSchema(
            XName Protocol,
            XName FileTypeAssociation,
            string ReportName);

        internal sealed record ActivationMigrationResult(
            MigrationActivationAnalysis Analysis,
            int ChangedFiles);

        internal static ActivationMigrationResult AnalyzeActivationContracts(
            string sourceRoot,
            string targetRoot,
            bool applyChanges)
        {
            var analysis = new MigrationActivationAnalysis();
            var sourceManifests = Directory.Exists(sourceRoot)
                ? Directory.EnumerateFiles(sourceRoot, "*.appxmanifest", SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
            if (sourceManifests.Count == 0)
            {
                analysis.Status = "not-available";
                analysis.Issues.Add(new MigrationActivationIssue
                {
                    Kind = "source-manifest-missing",
                    Severity = "review-required",
                    Reason = "No top-level UWP Package.appxmanifest was available for activation analysis."
                });
                return new ActivationMigrationResult(analysis, 0);
            }
            if (sourceManifests.Count > 1)
            {
                analysis.Status = "incomplete";
                analysis.Issues.Add(new MigrationActivationIssue
                {
                    Kind = "ambiguous-source-manifest",
                    Severity = "error",
                    Reason = $"Multiple top-level .appxmanifest files were found: {string.Join(", ", sourceManifests.Select(Path.GetFileName))}."
                });
                return new ActivationMigrationResult(analysis, 0);
            }

            var sourceManifest = sourceManifests[0];
            analysis.SourceManifest = NormalizePath(Path.GetRelativePath(sourceRoot, sourceManifest));
            XDocument sourceDocument;
            try
            {
                sourceDocument = XDocument.Load(
                    sourceManifest,
                    LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            }
            catch (Exception exception) when (exception is XmlException or IOException)
            {
                analysis.Status = "incomplete";
                analysis.Issues.Add(new MigrationActivationIssue
                {
                    Kind = "source-manifest-inspection-failed",
                    Severity = "error",
                    Reason = exception.Message,
                    Location = new MigrationLocation
                    {
                        Path = analysis.SourceManifest
                    }
                });
                return new ActivationMigrationResult(analysis, 0);
            }

            var sourceApplications = FindApplications(sourceDocument).ToList();
            if (sourceApplications.Count != 1)
            {
                analysis.Status = "incomplete";
                analysis.Issues.Add(new MigrationActivationIssue
                {
                    Kind = "ambiguous-source-application",
                    Severity = "error",
                    Reason = $"Expected exactly one source Application declaration; found {sourceApplications.Count}.",
                    Location = new MigrationLocation
                    {
                        Path = analysis.SourceManifest
                    }
                });
                return new ActivationMigrationResult(analysis, 0);
            }

            RecordMismatchedSourceExtensionContainers(
                sourceRoot,
                sourceManifest,
                sourceApplications[0],
                analysis.Issues);
            foreach (var extension in FindApplicationExtensionCandidates(sourceApplications[0]))
            {
                var categoryAttribute = extension.Attribute("Category");
                var category = categoryAttribute?.Value.Trim();
                if (categoryAttribute is null)
                {
                    var categoryLookalike = extension.Attributes().FirstOrDefault(
                        attribute =>
                            !attribute.IsNamespaceDeclaration
                            && attribute.Name.LocalName == "Category"
                            && SupportedActivationCategories.Contains(
                                attribute.Value.Trim()));
                    if (categoryLookalike is not null)
                    {
                        analysis.Issues.Add(new MigrationActivationIssue
                        {
                            Kind = "source-activation-category-namespace-unsupported",
                            Reason =
                                $"Activation Category attribute '{categoryLookalike.Name}' must be unqualified and was not inspected as a contract.",
                            Location = ManifestLocation(
                                sourceRoot,
                                sourceManifest,
                                extension)
                        });
                    }
                }
                if (category is null || !SupportedActivationCategories.Contains(category))
                {
                    continue;
                }

                if (!TryGetSourceActivationSchema(extension, out var sourceSchema))
                {
                    analysis.Issues.Add(new MigrationActivationIssue
                    {
                        Kind = "source-activation-namespace-unsupported",
                        Reason =
                            $"Activation extension '{extension.Name}' uses an unsupported namespace and was not migrated.",
                        Location = ManifestLocation(
                            sourceRoot,
                            sourceManifest,
                            extension)
                    });
                    continue;
                }

                var contract = category.Equals("windows.protocol", StringComparison.OrdinalIgnoreCase)
                    ? ReadProtocolContract(
                        sourceRoot,
                        sourceManifest,
                        extension,
                        sourceSchema,
                        analysis.Issues)
                    : ReadFileAssociationContract(
                        sourceRoot,
                        sourceManifest,
                        extension,
                        sourceSchema,
                        analysis.Issues);
                if (contract is not null)
                {
                    analysis.Contracts.Add(contract);
                }
            }

            MarkDuplicateSourceContracts(analysis);
            if (analysis.Contracts.Count == 0)
            {
                analysis.Status = analysis.Issues.Any(issue => issue.Severity == "error")
                    ? "incomplete"
                    : analysis.Issues.Count > 0
                        ? "review-required"
                        : "not-required";
                return new ActivationMigrationResult(analysis, 0);
            }

            var targetManifests = Directory.Exists(targetRoot)
                ? Directory.EnumerateFiles(targetRoot, "*.appxmanifest", SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
            if (targetManifests.Count != 1)
            {
                analysis.Status = "failed";
                analysis.Issues.Add(new MigrationActivationIssue
                {
                    Kind = targetManifests.Count == 0
                        ? "target-manifest-missing"
                        : "ambiguous-target-manifest",
                    Severity = "error",
                    Reason = targetManifests.Count == 0
                        ? "The packaged WinUI target manifest was not found."
                        : $"Multiple top-level target manifests were found: {string.Join(", ", targetManifests.Select(Path.GetFileName))}."
                });
                MarkEligibleContractsMissing(analysis, "The target manifest could not be resolved.");
                return new ActivationMigrationResult(analysis, 0);
            }

            var targetManifest = targetManifests[0];
            analysis.TargetManifest = NormalizePath(Path.GetRelativePath(targetRoot, targetManifest));
            EncodedTextFile targetFile;
            XDocument targetDocument;
            try
            {
                targetFile = ReadTextFile(targetManifest);
                targetDocument = XDocument.Parse(
                    targetFile.Content,
                    LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            }
            catch (Exception exception) when (
                exception is XmlException or IOException or DecoderFallbackException)
            {
                analysis.Status = "failed";
                analysis.Issues.Add(new MigrationActivationIssue
                {
                    Kind = "target-manifest-inspection-failed",
                    Severity = "error",
                    Reason = exception.Message,
                    Location = new MigrationLocation
                    {
                        Path = analysis.TargetManifest
                    }
                });
                MarkEligibleContractsMissing(analysis, "The target manifest could not be inspected.");
                return new ActivationMigrationResult(analysis, 0);
            }

            var targetApplications = FindApplications(targetDocument).ToList();
            if (targetApplications.Count != 1)
            {
                analysis.Issues.Add(new MigrationActivationIssue
                {
                    Kind = "ambiguous-target-application",
                    Severity = "error",
                    Reason = $"Expected exactly one target Application declaration; found {targetApplications.Count}.",
                    Location = new MigrationLocation
                    {
                        Path = analysis.TargetManifest
                    }
                });
                MarkEligibleContractsMissing(analysis, "The target Application declaration is ambiguous.");
                analysis.Status = "failed";
                return new ActivationMigrationResult(analysis, 0);
            }

            RecordMismatchedTargetActivationNamespaces(
                targetApplications[0],
                analysis);
            ValidateTargetActivationPrerequisites(targetDocument, analysis);
            var changedFiles = 0;
            if (applyChanges && !analysis.Issues.Any(issue => issue.Severity == "error"))
            {
                var changed = MergeSafeActivationContracts(
                    targetDocument,
                    targetApplications[0],
                    analysis);
                if (changed)
                {
                    WriteTextFile(
                        targetManifest,
                        targetDocument.ToString(SaveOptions.DisableFormatting),
                        targetFile.Encoding);
                    changedFiles = 1;
                    targetDocument = XDocument.Load(
                        targetManifest,
                        LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
                    targetApplications = FindApplications(targetDocument).ToList();
                }
            }

            VerifyTargetActivationContracts(
                analysis,
                targetApplications.Single(),
                analysis.TargetManifest);
            analysis.Status = GetActivationAnalysisStatus(analysis);
            return new ActivationMigrationResult(analysis, changedFiles);
        }

        internal static MigrationActivationVerification CreateActivationVerification(
            MigrationActivationAnalysis analysis) =>
            new()
            {
                SourceContracts = analysis.Contracts.Count,
                MigratedContracts = analysis.Contracts.Count(contract =>
                    contract.MigrationStatus == "migrated"),
                VerifiedContracts = analysis.Contracts.Count(contract =>
                    contract.VerificationStatus == "verified"),
                ReviewRequiredContracts = analysis.Contracts.Count(contract =>
                    contract.MigrationStatus == "review-required"),
                MissingContracts = analysis.Contracts.Count(contract =>
                    contract.VerificationStatus == "missing"),
                DriftedContracts = analysis.Contracts.Count(contract =>
                    contract.VerificationStatus == "drifted"),
                Issues = analysis.Issues.Count
            };

        internal static bool HasActivationMechanicalFailure(
            MigrationActivationAnalysis analysis) =>
            analysis.Status is "failed" or "incomplete";

        private static MigrationActivationContract? ReadProtocolContract(
            string sourceRoot,
            string sourceManifest,
            XElement extension,
            SourceActivationSchema sourceSchema,
            List<MigrationActivationIssue> issues)
        {
            var location = ManifestLocation(sourceRoot, sourceManifest, extension);
            var protocolElements = extension.Elements()
                .Where(element => element.Name == sourceSchema.Protocol)
                .ToList();
            if (protocolElements.Count != 1)
            {
                var namespaceLookalikes = extension.Elements()
                    .Where(element => element.Name.LocalName == "Protocol")
                    .Select(element => element.Name.ToString())
                    .ToList();
                issues.Add(new MigrationActivationIssue
                {
                    Kind = namespaceLookalikes.Count > 0
                        ? "protocol-namespace-unsupported"
                        : "ambiguous-protocol-declaration",
                    Reason = namespaceLookalikes.Count > 0
                        ? $"Protocol child namespace must be '{sourceSchema.Protocol.NamespaceName}'; found {string.Join(", ", namespaceLookalikes)}."
                        : $"Expected one Protocol child; found {protocolElements.Count}.",
                    Location = location
                });
                return null;
            }

            var protocol = protocolElements[0];
            var name = protocol.Attribute("Name")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                issues.Add(new MigrationActivationIssue
                {
                    Kind = "protocol-name-missing",
                    Reason = "The protocol declaration has no Name.",
                    Location = location
                });
                return null;
            }

            var contract = new MigrationActivationContract
            {
                Id = ActivationContractId("windows.protocol", name),
                Category = "windows.protocol",
                SourceLocation = location,
                SourceSchema = sourceSchema.ReportName,
                ProtocolName = name,
                DisplayName = ReadSingleChildValue(protocol, UapDisplayName),
                Logo = ReadSingleChildValue(protocol, UapLogo),
                DesiredView = protocol.Attribute("DesiredView")?.Value,
                ReturnResults = protocol.Attribute("ReturnResults")?.Value,
                MigrationStatus = "migrated",
                TargetSchema = "uap3"
            };
            ValidateSafeAttributes(
                extension,
                CategoryOnlyAttributes,
                contract,
                location,
                issues,
                "protocol-extension-attribute-review-required");
            ValidateSafeAttributes(
                protocol,
                SafeProtocolAttributes,
                contract,
                location,
                issues,
                "protocol-attribute-review-required");
            ValidateSafeChildren(
                protocol,
                SafeProtocolChildren,
                contract,
                location,
                issues,
                "protocol-child-review-required");
            ValidateUniqueChildren(
                protocol,
                SafeProtocolChildren,
                contract,
                location,
                issues);
            return contract;
        }

        private static MigrationActivationContract? ReadFileAssociationContract(
            string sourceRoot,
            string sourceManifest,
            XElement extension,
            SourceActivationSchema sourceSchema,
            List<MigrationActivationIssue> issues)
        {
            var location = ManifestLocation(sourceRoot, sourceManifest, extension);
            var associationElements = extension.Elements()
                .Where(element => element.Name == sourceSchema.FileTypeAssociation)
                .ToList();
            if (associationElements.Count != 1)
            {
                var namespaceLookalikes = extension.Elements()
                    .Where(element => element.Name.LocalName == "FileTypeAssociation")
                    .Select(element => element.Name.ToString())
                    .ToList();
                issues.Add(new MigrationActivationIssue
                {
                    Kind = namespaceLookalikes.Count > 0
                        ? "file-association-namespace-unsupported"
                        : "ambiguous-file-association-declaration",
                    Reason = namespaceLookalikes.Count > 0
                        ? $"FileTypeAssociation child namespace must be '{sourceSchema.FileTypeAssociation.NamespaceName}'; found {string.Join(", ", namespaceLookalikes)}."
                        : $"Expected one FileTypeAssociation child; found {associationElements.Count}.",
                    Location = location
                });
                return null;
            }

            var association = associationElements[0];
            var name = association.Attribute("Name")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                issues.Add(new MigrationActivationIssue
                {
                    Kind = "file-association-name-missing",
                    Reason = "The file type association has no Name.",
                    Location = location
                });
                return null;
            }

            var contract = new MigrationActivationContract
            {
                Id = ActivationContractId("windows.fileTypeAssociation", name),
                Category = "windows.fileTypeAssociation",
                SourceLocation = location,
                SourceSchema = sourceSchema.ReportName,
                AssociationName = name,
                DisplayName = ReadSingleChildValue(association, UapDisplayName),
                Logo = ReadSingleChildValue(association, UapLogo),
                DesiredView = association.Attribute("DesiredView")?.Value,
                MultiSelectModel = association.Attribute("MultiSelectModel")?.Value,
                MigrationStatus = "migrated",
                TargetSchema = "uap3"
            };
            ValidateSafeAttributes(
                extension,
                CategoryOnlyAttributes,
                contract,
                location,
                issues,
                "file-association-extension-attribute-review-required");
            ValidateSafeAttributes(
                association,
                SafeFileAssociationAttributes,
                contract,
                location,
                issues,
                "file-association-attribute-review-required");
            ValidateSafeChildren(
                association,
                SafeFileAssociationChildren,
                contract,
                location,
                issues,
                "file-association-child-review-required");
            ValidateUniqueChildren(
                association,
                SafeFileAssociationChildren,
                contract,
                location,
                issues);

            var supportedGroups = association.Elements()
                .Where(element => element.Name == UapSupportedFileTypes)
                .ToList();
            if (supportedGroups.Count != 1)
            {
                MarkContractReviewRequired(
                    contract,
                    location,
                    issues,
                    "file-types-ambiguous",
                    $"Expected one SupportedFileTypes element; found {supportedGroups.Count}.");
                return contract;
            }

            foreach (var fileType in supportedGroups[0].Elements())
            {
                if (fileType.Name != UapFileType
                    || fileType.HasElements
                    || fileType.Attributes().Any(attribute =>
                        !attribute.IsNamespaceDeclaration
                        && attribute.Name != XName.Get("ContentType")))
                {
                    MarkContractReviewRequired(
                        contract,
                        location,
                        issues,
                        "file-type-declaration-review-required",
                        "The file type association contains a FileType declaration that cannot be translated deterministically.");
                    continue;
                }

                var extensionValue = fileType.Value.Trim();
                if (string.IsNullOrWhiteSpace(extensionValue)
                    || !extensionValue.StartsWith('.')
                    || extensionValue.IndexOfAny(['*', '?']) >= 0
                    || extensionValue.Contains("$(", StringComparison.Ordinal))
                {
                    MarkContractReviewRequired(
                        contract,
                        location,
                        issues,
                        "file-type-value-review-required",
                        $"File type '{extensionValue}' is not a literal extension.");
                    continue;
                }

                contract.SupportedFileTypes.Add(new MigrationActivationFileType
                {
                    Extension = extensionValue,
                    ContentType = fileType.Attribute("ContentType")?.Value
                });
            }
            if (contract.SupportedFileTypes.Count == 0)
            {
                MarkContractReviewRequired(
                    contract,
                    location,
                    issues,
                    "supported-file-types-missing",
                    "No literal supported file types were available for migration.");
            }
            return contract;
        }

        private static void ValidateSafeAttributes(
            XElement element,
            HashSet<XName> allowedAttributes,
            MigrationActivationContract contract,
            MigrationLocation location,
            List<MigrationActivationIssue> issues,
            string issueKind)
        {
            foreach (var attribute in element.Attributes().Where(attribute =>
                !attribute.IsNamespaceDeclaration
                && !allowedAttributes.Contains(attribute.Name)))
            {
                MarkContractReviewRequired(
                    contract,
                    location,
                    issues,
                    issueKind,
                    $"Attribute '{attribute.Name}' requires semantic review.");
            }
        }

        private static void ValidateSafeChildren(
            XElement element,
            HashSet<XName> allowedChildren,
            MigrationActivationContract contract,
            MigrationLocation location,
            List<MigrationActivationIssue> issues,
            string issueKind)
        {
            foreach (var child in element.Elements().Where(child =>
                !allowedChildren.Contains(child.Name)))
            {
                MarkContractReviewRequired(
                    contract,
                    location,
                    issues,
                    issueKind,
                    $"Child element '{child.Name}' requires semantic review.");
            }
        }

        private static void ValidateUniqueChildren(
            XElement element,
            HashSet<XName> uniqueChildren,
            MigrationActivationContract contract,
            MigrationLocation location,
            List<MigrationActivationIssue> issues)
        {
            foreach (var duplicate in element.Elements()
                .Where(child => uniqueChildren.Contains(child.Name))
                .GroupBy(child => child.Name)
                .Where(group => group.Count() > 1))
            {
                MarkContractReviewRequired(
                    contract,
                    location,
                    issues,
                    "duplicate-activation-fact",
                    $"Multiple {duplicate.Key} elements were found.");
            }
        }

        private static string? ReadSingleChildValue(
            XElement parent,
            XName name)
        {
            var elements = parent.Elements()
                .Where(element => element.Name == name)
                .ToList();
            return elements.Count == 0 ? null : elements[0].Value.Trim();
        }

        private static void MarkContractReviewRequired(
            MigrationActivationContract contract,
            MigrationLocation location,
            List<MigrationActivationIssue> issues,
            string kind,
            string reason)
        {
            contract.MigrationStatus = "review-required";
            contract.TargetSchema = null;
            issues.Add(new MigrationActivationIssue
            {
                Kind = kind,
                Reason = reason,
                Location = location,
                ContractId = contract.Id
            });
        }

        private static void MarkDuplicateSourceContracts(
            MigrationActivationAnalysis analysis)
        {
            foreach (var duplicate in analysis.Contracts
                .GroupBy(contract => contract.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                foreach (var contract in duplicate)
                {
                    contract.MigrationStatus = "review-required";
                    contract.TargetSchema = null;
                }
                analysis.Issues.Add(new MigrationActivationIssue
                {
                    Kind = "duplicate-source-activation-contract",
                    Reason = $"Source manifest contains multiple activation contracts with identity '{duplicate.Key}'.",
                    ContractId = duplicate.Key,
                    Location = duplicate.First().SourceLocation
                });
            }
        }

        private static void ValidateTargetActivationPrerequisites(
            XDocument targetDocument,
            MigrationActivationAnalysis analysis)
        {
            if (!analysis.Contracts.Any(contract => contract.MigrationStatus == "migrated"))
            {
                return;
            }

            var root = targetDocument.Root!;
            ValidateNamespacePrefix(root, "uap", UapManifestNamespace, analysis);
            ValidateNamespacePrefix(root, "uap3", Uap3ManifestNamespace, analysis);

            var extensionGroups = FindApplications(targetDocument)
                .SelectMany(application => application.Elements())
                .Where(element => element.Name == FoundationExtensions)
                .ToList();
            if (extensionGroups.Count > 1)
            {
                analysis.Issues.Add(new MigrationActivationIssue
                {
                    Kind = "duplicate-target-extension-groups",
                    Severity = "error",
                    Reason = "The target Application contains multiple Extensions elements.",
                    Location = analysis.TargetManifest is null
                        ? null
                        : new MigrationLocation { Path = analysis.TargetManifest }
                });
            }

            var hasDesktopFamily = targetDocument.Descendants().Any(element =>
                element.Name == FoundationTargetDeviceFamily
                && string.Equals(
                    element.Attribute("Name")?.Value,
                    "Windows.Desktop",
                    StringComparison.OrdinalIgnoreCase));
            if (!hasDesktopFamily)
            {
                analysis.Issues.Add(new MigrationActivationIssue
                {
                    Kind = "desktop-target-family-missing",
                    Severity = "error",
                    Reason = "Migrated desktop activation contracts require a Windows.Desktop target device family.",
                    Location = analysis.TargetManifest is null
                        ? null
                        : new MigrationLocation { Path = analysis.TargetManifest }
                });
            }

            var hasRunFullTrust = targetDocument.Descendants().Any(element =>
                element.Name == RestrictedCapability
                && string.Equals(
                    element.Attribute("Name")?.Value,
                    "runFullTrust",
                    StringComparison.OrdinalIgnoreCase));
            if (!hasRunFullTrust)
            {
                analysis.Issues.Add(new MigrationActivationIssue
                {
                    Kind = "run-full-trust-capability-missing",
                    Severity = "error",
                    Reason = "The CLI-created WinUI desktop target must retain rescap:Capability Name=\"runFullTrust\" for packaged classic activation.",
                    Location = analysis.TargetManifest is null
                        ? null
                        : new MigrationLocation { Path = analysis.TargetManifest }
                });
            }

            var usesSystemManagedLifecycle = targetDocument.Descendants().Any(element =>
                element.Name == Uap3Extension
                && element.Attributes().Any(attribute =>
                    attribute.Name == Desktop11AppLifecycleBehavior
                    && string.Equals(
                        attribute.Value,
                        "systemManaged",
                        StringComparison.OrdinalIgnoreCase)));
            var hasShellExperience = targetDocument.Descendants().Any(element =>
                element.Name == RestrictedCapability
                && string.Equals(
                    element.Attribute("Name")?.Value,
                    "shellExperience",
                    StringComparison.OrdinalIgnoreCase));
            if (usesSystemManagedLifecycle && !hasShellExperience)
            {
                analysis.Issues.Add(new MigrationActivationIssue
                {
                    Kind = "shell-experience-capability-required",
                    Severity = "error",
                    Reason = "AppLifecycleBehavior=\"systemManaged\" requires the restricted shellExperience capability and cannot be added safely.",
                    Location = analysis.TargetManifest is null
                        ? null
                        : new MigrationLocation { Path = analysis.TargetManifest }
                });
            }
        }

        private static void RecordMismatchedTargetActivationNamespaces(
            XElement targetApplication,
            MigrationActivationAnalysis analysis)
        {
            foreach (var container in targetApplication.Elements().Where(element =>
                element.Name.LocalName == "Extensions"
                && element.Name != FoundationExtensions))
            {
                if (container.DescendantsAndSelf().Any(element =>
                    SupportedActivationCategories.Contains(
                        element.Attribute("Category")?.Value ?? string.Empty)))
                {
                    AddTargetNamespaceIssue(
                        analysis,
                        "target-extension-container-namespace-unsupported",
                        $"Target Extensions container '{container.Name}' must use the foundation manifest namespace.",
                        container);
                }
            }

            foreach (var extension in targetApplication
                .Elements(FoundationExtensions)
                .SelectMany(container => container.Elements()))
            {
                var category = extension.Attribute("Category")?.Value;
                if (category is null
                    || !SupportedActivationCategories.Contains(category))
                {
                    continue;
                }
                if (extension.Name != Uap3Extension)
                {
                    AddTargetNamespaceIssue(
                        analysis,
                        "target-activation-extension-namespace-unsupported",
                        $"Target activation extension '{extension.Name}' must use the uap3 namespace.",
                        extension);
                    continue;
                }

                var expectedDeclaration = category.Equals(
                    "windows.protocol",
                    StringComparison.OrdinalIgnoreCase)
                    ? Uap3Protocol
                    : Uap3FileTypeAssociation;
                foreach (var declaration in extension.Elements().Where(element =>
                    element.Name.LocalName == expectedDeclaration.LocalName
                    && element.Name != expectedDeclaration))
                {
                    AddTargetNamespaceIssue(
                        analysis,
                        "target-activation-declaration-namespace-unsupported",
                        $"Target declaration '{declaration.Name}' must use the uap3 namespace.",
                        declaration);
                }

                foreach (var declaration in extension.Elements(expectedDeclaration))
                {
                    var allowedFacts = category.Equals(
                        "windows.protocol",
                        StringComparison.OrdinalIgnoreCase)
                        ? SafeProtocolChildren
                        : SafeFileAssociationChildren;
                    foreach (var fact in declaration.Elements().Where(element =>
                        allowedFacts.Any(allowed =>
                            allowed.LocalName == element.Name.LocalName)
                        && !allowedFacts.Contains(element.Name)))
                    {
                        AddTargetNamespaceIssue(
                            analysis,
                            "target-activation-fact-namespace-unsupported",
                            $"Target activation fact '{fact.Name}' must use the base uap namespace.",
                            fact);
                    }
                    foreach (var fileType in declaration
                        .Elements(UapSupportedFileTypes)
                        .SelectMany(group => group.Elements())
                        .Where(element =>
                            element.Name.LocalName == UapFileType.LocalName
                            && element.Name != UapFileType))
                    {
                        AddTargetNamespaceIssue(
                            analysis,
                            "target-file-type-namespace-unsupported",
                            $"Target FileType '{fileType.Name}' must use the base uap namespace.",
                            fileType);
                    }
                }
            }
        }

        private static void AddTargetNamespaceIssue(
            MigrationActivationAnalysis analysis,
            string kind,
            string reason,
            XObject location)
        {
            analysis.Issues.Add(new MigrationActivationIssue
            {
                Kind = kind,
                Severity = "error",
                Reason = reason,
                Location = analysis.TargetManifest is null
                    ? null
                    : ManifestLocation(analysis.TargetManifest, location)
            });
        }

        private static bool MergeSafeActivationContracts(
            XDocument targetDocument,
            XElement targetApplication,
            MigrationActivationAnalysis analysis)
        {
            var safeContracts = analysis.Contracts
                .Where(contract => contract.MigrationStatus == "migrated")
                .ToList();
            if (safeContracts.Count == 0)
            {
                return false;
            }

            var changed = EnsureManifestNamespace(
                targetDocument,
                "uap",
                UapManifestNamespace);
            changed |= EnsureManifestNamespace(
                targetDocument,
                "uap3",
                Uap3ManifestNamespace);
            changed |= EnsureIgnorableNamespace(targetDocument, "uap3");

            var extensions = targetApplication.Elements()
                .FirstOrDefault(element => element.Name == FoundationExtensions);
            if (extensions is null)
            {
                extensions = new XElement(
                    FoundationExtensions);
                targetApplication.Add(extensions);
                changed = true;
            }

            foreach (var contract in safeContracts)
            {
                var matches = FindTargetContractExtensions(extensions, contract).ToList();
                if (matches.Count == 0)
                {
                    extensions.Add(CreateTargetContract(contract));
                    changed = true;
                }
                else if (matches.Count > 1 || !TargetContractMatches(matches[0], contract))
                {
                    analysis.Issues.Add(new MigrationActivationIssue
                    {
                        Kind = matches.Count > 1
                            ? "duplicate-target-activation-contract"
                            : "target-activation-contract-conflict",
                        Severity = "error",
                        Reason = matches.Count > 1
                            ? $"Target manifest contains multiple declarations for '{contract.Id}'."
                            : $"Target manifest already contains a conflicting declaration for '{contract.Id}'.",
                        ContractId = contract.Id,
                        Location = analysis.TargetManifest is null
                            ? null
                            : ManifestLocation(analysis.TargetManifest, matches[0])
                    });
                }
            }
            return changed;
        }

        private static XElement CreateTargetContract(
            MigrationActivationContract contract)
        {
            XNamespace uap = UapManifestNamespace;
            var extension = new XElement(
                Uap3Extension,
                new XAttribute("Category", contract.Category));
            if (contract.Category == "windows.protocol")
            {
                var protocol = new XElement(
                    Uap3Protocol,
                    new XAttribute("Name", contract.ProtocolName!));
                AddOptionalAttribute(protocol, "DesiredView", contract.DesiredView);
                AddOptionalAttribute(protocol, "ReturnResults", contract.ReturnResults);
                AddOptionalElement(protocol, uap + "Logo", contract.Logo);
                AddOptionalElement(protocol, uap + "DisplayName", contract.DisplayName);
                extension.Add(protocol);
            }
            else
            {
                var association = new XElement(
                    Uap3FileTypeAssociation,
                    new XAttribute("Name", contract.AssociationName!));
                AddOptionalAttribute(association, "DesiredView", contract.DesiredView);
                AddOptionalAttribute(association, "MultiSelectModel", contract.MultiSelectModel);
                AddOptionalElement(association, uap + "DisplayName", contract.DisplayName);
                AddOptionalElement(association, uap + "Logo", contract.Logo);
                var supported = new XElement(uap + "SupportedFileTypes");
                foreach (var fileType in contract.SupportedFileTypes)
                {
                    var fileTypeElement = new XElement(
                        uap + "FileType",
                        fileType.Extension);
                    AddOptionalAttribute(
                        fileTypeElement,
                        "ContentType",
                        fileType.ContentType);
                    supported.Add(fileTypeElement);
                }
                association.Add(supported);
                extension.Add(association);
            }
            return extension;
        }

        private static void VerifyTargetActivationContracts(
            MigrationActivationAnalysis analysis,
            XElement targetApplication,
            string targetManifest)
        {
            var extensions = targetApplication.Elements()
                .FirstOrDefault(element => element.Name == FoundationExtensions);
            foreach (var contract in analysis.Contracts)
            {
                if (contract.MigrationStatus != "migrated")
                {
                    contract.VerificationStatus = "not-applicable";
                    contract.VerificationReason =
                        "The source declaration requires semantic review and was not transformed.";
                    continue;
                }

                var matches = extensions is null
                    ? []
                    : FindTargetContractExtensions(extensions, contract).ToList();
                if (matches.Count == 0)
                {
                    contract.VerificationStatus = "missing";
                    contract.VerificationReason =
                        "The migrated target activation declaration is missing.";
                    continue;
                }
                if (matches.Count > 1)
                {
                    contract.VerificationStatus = "drifted";
                    contract.VerificationReason =
                        "The target contains duplicate declarations for this activation identity.";
                    continue;
                }

                contract.TargetLocation = ManifestLocation(targetManifest, matches[0]);
                if (!TargetContractMatches(matches[0], contract))
                {
                    contract.VerificationStatus = "drifted";
                    contract.VerificationReason =
                        "The target declaration no longer matches the mechanically migrated source facts.";
                    continue;
                }

                contract.VerificationStatus = "verified";
                contract.VerificationReason = null;
            }
        }

        private static IEnumerable<XElement> FindTargetContractExtensions(
            XElement extensions,
            MigrationActivationContract contract) =>
            extensions.Elements().Where(extension =>
                extension.Name == Uap3Extension
                && string.Equals(
                    extension.Attribute("Category")?.Value,
                    contract.Category,
                    StringComparison.OrdinalIgnoreCase)
                && extension.Elements().Any(child =>
                    contract.Category == "windows.protocol"
                        ? child.Name == Uap3Protocol
                            && string.Equals(
                                child.Attribute("Name")?.Value,
                                contract.ProtocolName,
                                StringComparison.OrdinalIgnoreCase)
                        : child.Name == Uap3FileTypeAssociation
                            && string.Equals(
                                child.Attribute("Name")?.Value,
                                contract.AssociationName,
                                StringComparison.OrdinalIgnoreCase)));

        private static bool TargetContractMatches(
            XElement targetExtension,
            MigrationActivationContract contract)
        {
            if (targetExtension.Name != Uap3Extension)
            {
                return false;
            }

            var declarations = targetExtension.Elements().ToList();
            if (declarations.Count != 1
                || declarations[0] is not { } declaration)
            {
                return false;
            }

            if (contract.Category == "windows.protocol")
            {
                return declaration.Name == Uap3Protocol
                    && declaration.Elements().All(element =>
                        SafeProtocolChildren.Contains(element.Name))
                    && HasNoDuplicateChildren(declaration, SafeProtocolChildren)
                    && string.Equals(
                        declaration.Attribute("Name")?.Value,
                        contract.ProtocolName,
                        StringComparison.OrdinalIgnoreCase)
                    && SameOptionalFact(
                        declaration.Attribute("DesiredView")?.Value,
                        contract.DesiredView)
                    && SameOptionalFact(
                        declaration.Attribute("ReturnResults")?.Value,
                        contract.ReturnResults)
                    && SameOptionalFact(
                        ReadTargetChildValue(declaration, UapDisplayName),
                        contract.DisplayName)
                    && SameOptionalFact(
                        ReadTargetChildValue(declaration, UapLogo),
                        contract.Logo);
            }

            if (declaration.Name != Uap3FileTypeAssociation
                || declaration.Elements().Any(element =>
                    !SafeFileAssociationChildren.Contains(element.Name))
                || !HasNoDuplicateChildren(
                    declaration,
                    SafeFileAssociationChildren)
                || !string.Equals(
                    declaration.Attribute("Name")?.Value,
                    contract.AssociationName,
                    StringComparison.OrdinalIgnoreCase)
                || !SameOptionalFact(
                    declaration.Attribute("DesiredView")?.Value,
                    contract.DesiredView)
                || !SameOptionalFact(
                    declaration.Attribute("MultiSelectModel")?.Value,
                    contract.MultiSelectModel)
                || !SameOptionalFact(
                    ReadTargetChildValue(declaration, UapDisplayName),
                    contract.DisplayName)
                || !SameOptionalFact(
                    ReadTargetChildValue(declaration, UapLogo),
                    contract.Logo))
            {
                return false;
            }

            var supportedGroups = declaration.Elements(UapSupportedFileTypes).ToList();
            if (supportedGroups.Count != 1
                || supportedGroups[0].Elements().Any(element =>
                    element.Name != UapFileType
                    || element.HasElements
                    || element.Attributes().Any(attribute =>
                        !attribute.IsNamespaceDeclaration
                        && attribute.Name != XName.Get("ContentType"))))
            {
                return false;
            }

            var targetFileTypes = supportedGroups[0].Elements(UapFileType)
                .Select(element => (
                    Extension: element.Value.Trim(),
                    ContentType: element.Attribute("ContentType")?.Value))
                .OrderBy(value => value.Extension, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.ContentType, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var sourceFileTypes = contract.SupportedFileTypes
                .Select(fileType => (
                    fileType.Extension,
                    fileType.ContentType))
                .OrderBy(value => value.Extension, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.ContentType, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return targetFileTypes.SequenceEqual(
                sourceFileTypes,
                ActivationFileTypeComparer.OrdinalIgnoreCase);
        }

        private static string? ReadTargetChildValue(
            XElement parent,
            XName name)
        {
            var matches = parent.Elements()
                .Where(element => element.Name == name)
                .ToList();
            return matches.Count == 1 ? matches[0].Value.Trim() : null;
        }

        private static bool HasNoDuplicateChildren(
            XElement parent,
            HashSet<XName> allowedChildren) =>
            parent.Elements()
                .Where(element => allowedChildren.Contains(element.Name))
                .GroupBy(element => element.Name)
                .All(group => group.Count() == 1);

        private static string GetActivationAnalysisStatus(
            MigrationActivationAnalysis analysis)
        {
            if (analysis.Issues.Any(issue => issue.Severity == "error")
                || analysis.Contracts.Any(contract =>
                    contract.MigrationStatus == "migrated"
                    && contract.VerificationStatus is "missing" or "drifted"))
            {
                return "failed";
            }
            if (analysis.Contracts.Any(contract =>
                    contract.MigrationStatus == "review-required")
                || analysis.Issues.Count > 0)
            {
                return "review-required";
            }
            return analysis.Contracts.Count == 0 ? "not-required" : "passed";
        }

        private static void MarkEligibleContractsMissing(
            MigrationActivationAnalysis analysis,
            string reason)
        {
            foreach (var contract in analysis.Contracts.Where(contract =>
                contract.MigrationStatus == "migrated"))
            {
                contract.VerificationStatus = "missing";
                contract.VerificationReason = reason;
            }
        }

        private static IEnumerable<XElement> FindApplications(XDocument document)
        {
            var root = document.Root;
            if (root is null)
            {
                return [];
            }
            return root.Elements()
                .Where(element => element.Name == FoundationApplications)
                .SelectMany(element => element.Elements())
                .Where(element => element.Name == FoundationApplication);
        }

        private static IEnumerable<XElement> FindApplicationExtensionCandidates(
            XElement application) =>
            application.Elements()
                .Where(element => element.Name == FoundationExtensions)
                .SelectMany(element => element.Elements());

        private static bool TryGetSourceActivationSchema(
            XElement extension,
            out SourceActivationSchema schema)
        {
            if (extension.Name == UapExtension)
            {
                schema = new SourceActivationSchema(
                    UapProtocol,
                    UapFileTypeAssociation,
                    "uap");
                return true;
            }
            if (extension.Name == Uap3Extension)
            {
                schema = new SourceActivationSchema(
                    Uap3Protocol,
                    Uap3FileTypeAssociation,
                    "uap3");
                return true;
            }

            schema = null!;
            return false;
        }

        private static void RecordMismatchedSourceExtensionContainers(
            string sourceRoot,
            string sourceManifest,
            XElement application,
            List<MigrationActivationIssue> issues)
        {
            foreach (var container in application.Elements().Where(element =>
                element.Name.LocalName == "Extensions"
                && element.Name != FoundationExtensions))
            {
                if (!container.Elements().Any(element =>
                    SupportedActivationCategories.Contains(
                        element.Attribute("Category")?.Value ?? string.Empty)))
                {
                    continue;
                }

                issues.Add(new MigrationActivationIssue
                {
                    Kind = "source-extension-container-namespace-unsupported",
                    Reason =
                        $"Activation Extensions container '{container.Name}' must use the foundation manifest namespace.",
                    Location = ManifestLocation(
                        sourceRoot,
                        sourceManifest,
                        container)
                });
            }
        }

        private static MigrationLocation ManifestLocation(
            string sourceRoot,
            string manifestPath,
            XObject item) =>
            new()
            {
                Path = NormalizePath(Path.GetRelativePath(sourceRoot, manifestPath)),
                Line = (item as IXmlLineInfo)?.HasLineInfo() == true
                    ? ((IXmlLineInfo)item).LineNumber
                    : null
            };

        private static MigrationLocation ManifestLocation(
            string manifestPath,
            XObject item) =>
            new()
            {
                Path = NormalizePath(manifestPath),
                Line = (item as IXmlLineInfo)?.HasLineInfo() == true
                    ? ((IXmlLineInfo)item).LineNumber
                    : null
            };

        private static string ActivationContractId(
            string category,
            string name) =>
            $"{category.Trim().ToLowerInvariant()}:{name.Trim().ToLowerInvariant()}";

        private static void ValidateNamespacePrefix(
            XElement root,
            string prefix,
            string expectedNamespace,
            MigrationActivationAnalysis analysis)
        {
            var actual = root.GetNamespaceOfPrefix(prefix);
            if (actual is not null
                && actual.NamespaceName != expectedNamespace)
            {
                analysis.Issues.Add(new MigrationActivationIssue
                {
                    Kind = "target-manifest-namespace-conflict",
                    Severity = "error",
                    Reason = $"Manifest prefix '{prefix}' maps to '{actual.NamespaceName}' instead of '{expectedNamespace}'.",
                    Location = analysis.TargetManifest is null
                        ? null
                        : new MigrationLocation { Path = analysis.TargetManifest }
                });
            }
        }

        private static bool EnsureManifestNamespace(
            XDocument document,
            string prefix,
            string namespaceName)
        {
            var root = document.Root!;
            var attribute = root.Attribute(XNamespace.Xmlns + prefix);
            if (attribute is not null)
            {
                return false;
            }
            root.Add(new XAttribute(XNamespace.Xmlns + prefix, namespaceName));
            return true;
        }

        private static bool EnsureIgnorableNamespace(
            XDocument document,
            string prefix)
        {
            var root = document.Root!;
            var attribute = root.Attribute("IgnorableNamespaces");
            var values = (attribute?.Value ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            if (values.Contains(prefix, StringComparer.Ordinal))
            {
                return false;
            }
            values.Add(prefix);
            root.SetAttributeValue("IgnorableNamespaces", string.Join(' ', values));
            return true;
        }

        private static void AddOptionalAttribute(
            XElement element,
            string name,
            string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                element.Add(new XAttribute(name, value));
            }
        }

        private static void AddOptionalElement(
            XElement parent,
            XName name,
            string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parent.Add(new XElement(name, value));
            }
        }

        private static bool SameOptionalFact(
            string? target,
            string? source) =>
            string.Equals(
                string.IsNullOrWhiteSpace(target) ? null : target.Trim(),
                string.IsNullOrWhiteSpace(source) ? null : source.Trim(),
                StringComparison.Ordinal);

        private sealed class ActivationFileTypeComparer :
            IEqualityComparer<(string Extension, string? ContentType)>
        {
            internal static readonly ActivationFileTypeComparer OrdinalIgnoreCase = new();

            public bool Equals(
                (string Extension, string? ContentType) x,
                (string Extension, string? ContentType) y) =>
                string.Equals(
                    x.Extension,
                    y.Extension,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    x.ContentType,
                    y.ContentType,
                    StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(
                (string Extension, string? ContentType) value) =>
                HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(value.Extension),
                    value.ContentType is null
                        ? 0
                        : StringComparer.OrdinalIgnoreCase.GetHashCode(value.ContentType));
        }
    }
}
