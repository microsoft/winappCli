// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class AuthenticodeVerifierTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"Authenticode_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public void IsMicrosoftSubject_MicrosoftOrganization_ReturnsTrue()
    {
        Assert.IsTrue(AuthenticodeVerifier.IsMicrosoftSubject(
            "CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_MicrosoftOrganizationWithProductCommonName_ReturnsTrue()
    {
        // The arm64 SDK build tools ship under this subject, so the gate must accept a common name
        // that names a team or product rather than the company.
        Assert.IsTrue(AuthenticodeVerifier.IsMicrosoftSubject(
            "CN=Microsoft Windows Kits Publisher, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_CaseInsensitive_ReturnsTrue()
    {
        Assert.IsTrue(AuthenticodeVerifier.IsMicrosoftSubject("cn=microsoft corporation, o=microsoft corporation"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_ThirdParty_ReturnsFalse()
    {
        Assert.IsFalse(AuthenticodeVerifier.IsMicrosoftSubject(
            "CN=Contoso Ltd, O=Contoso Corporation, C=US"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_MicrosoftCommonNameUnderAnotherOrganization_ReturnsFalse()
    {
        // The whole point of the check: this signer is Contoso, however its common name reads.
        Assert.IsFalse(AuthenticodeVerifier.IsMicrosoftSubject("CN=Microsoft Tools, O=Contoso Ltd"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_NoOrganizationAttribute_ReturnsFalse()
    {
        // A common name alone proves nothing about who owns the certificate, and every real
        // Microsoft code-signing certificate carries O=Microsoft Corporation.
        Assert.IsFalse(AuthenticodeVerifier.IsMicrosoftSubject("CN=Microsoft Corporation"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_OrganizationThatMerelyStartsWithMicrosoft_ReturnsFalse()
    {
        Assert.IsFalse(AuthenticodeVerifier.IsMicrosoftSubject("CN=Acme, O=Microsoft Corporation Ltd"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_MicrosoftNameInANonOrganizationAttribute_ReturnsFalse()
    {
        // Organizational unit is chosen by the requester, not asserted by the CA about the owner.
        Assert.IsFalse(AuthenticodeVerifier.IsMicrosoftSubject("CN=Acme, OU=Microsoft Corporation"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_OrganizationSmuggledIntoACommonNameValue_ReturnsFalse()
    {
        // An escaped '=' keeps this all one common name value; there is no organization attribute.
        Assert.IsFalse(AuthenticodeVerifier.IsMicrosoftSubject(@"CN=O\=Microsoft Corporation, O=Contoso"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_OrganizationSmuggledIntoAQuotedCommonName_ReturnsFalse()
    {
        // Quoting is how a common name containing a comma is written, so the text after it is part
        // of the common name and not a second attribute.
        Assert.IsFalse(AuthenticodeVerifier.IsMicrosoftSubject(@"CN=""Acme, O=Microsoft Corporation"", O=Contoso"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_LookalikeWithoutMicrosoftMarkers_ReturnsFalse()
    {
        Assert.IsFalse(AuthenticodeVerifier.IsMicrosoftSubject("O=Not Microsoft-Affiliated Vendor, CN=Acme"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_SecondOrganizationAlongsideMicrosoft_ReturnsFalse()
    {
        // Two organizations name two owners. Accepting this because one of them happens to be
        // Microsoft is the same mistake as the substring test this check replaced.
        Assert.IsFalse(AuthenticodeVerifier.IsMicrosoftSubject("CN=Acme, O=Contoso Ltd, O=Microsoft Corporation"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_MicrosoftOrganizationListedFirst_StillReturnsFalse()
    {
        // Order must not decide the verdict, or the check becomes "is Microsoft in here somewhere".
        Assert.IsFalse(AuthenticodeVerifier.IsMicrosoftSubject("CN=Acme, O=Microsoft Corporation, O=Contoso Ltd"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_RepeatedMicrosoftOrganization_ReturnsFalse()
    {
        // A subject is expected to name its organization once; anything else is malformed enough
        // that the safe reading is to refuse it.
        Assert.IsFalse(AuthenticodeVerifier.IsMicrosoftSubject("O=Microsoft Corporation, O=Microsoft Corporation"));
    }

    [TestMethod]
    public void IsMicrosoftSubject_NonMicrosoftOrganizationHiddenInAMultiValuedAttribute_ReturnsFalse()
    {
        // The dangerous shape: a second organization tucked into a multi-valued attribute, next to
        // a well-formed O=Microsoft Corporation. Skipping the attribute we cannot read plainly
        // would let the Microsoft one answer for a subject that also names Contoso.
        var subject = EncodeSubject(
        [
            [("2.5.4.10", "Contoso Ltd"), ("2.5.4.3", "Acme")],
            [("2.5.4.10", "Microsoft Corporation")],
        ]);

        Assert.IsFalse(AuthenticodeVerifier.IsMicrosoftSubject(subject));
    }

    [TestMethod]
    public void IsMicrosoftSubject_EncodedMicrosoftOrganization_ReturnsTrue()
    {
        // Control for the test above: the same encoding path, with the one organization a real
        // Microsoft certificate carries, must still pass.
        var subject = EncodeSubject([[("2.5.4.10", "Microsoft Corporation")]]);

        Assert.IsTrue(AuthenticodeVerifier.IsMicrosoftSubject(subject));
    }

    /// <summary>
    /// Builds an encoded subject directly, so a relative distinguished name can hold several
    /// attributes at once. That form is legal in a certificate but cannot be expressed through the
    /// distinguished-name string, whose parser rejects it outright.
    /// </summary>
    private static X500DistinguishedName EncodeSubject((string Oid, string Value)[][] relativeNames)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);

        using (writer.PushSequence())
        {
            foreach (var attributes in relativeNames)
            {
                using (writer.PushSetOf())
                {
                    foreach (var (oid, value) in attributes)
                    {
                        using (writer.PushSequence())
                        {
                            writer.WriteObjectIdentifier(oid);
                            writer.WriteCharacterString(UniversalTagNumber.UTF8String, value);
                        }
                    }
                }
            }
        }

        return new X500DistinguishedName(writer.Encode());
    }

    [TestMethod]
    public void IsMicrosoftSubject_MalformedSubject_ReturnsFalse()
    {
        Assert.IsFalse(AuthenticodeVerifier.IsMicrosoftSubject("not a distinguished name at all"),
            "A subject that cannot be parsed must fail closed.");
    }

    [TestMethod]
    public void IsMicrosoftSubject_EmptySubject_ReturnsFalse()
    {
        Assert.IsFalse(AuthenticodeVerifier.IsMicrosoftSubject(string.Empty));
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_NonexistentFile_ReturnsFalse()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist.dll");

        Assert.IsFalse(AuthenticodeVerifier.IsTrustedMicrosoftSigned(missing, NullLogger.Instance),
            "A missing file must fail the fail-closed trust gate.");
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_UnsignedFile_ReturnsFalse()
    {
        var unsigned = Path.Combine(_tempDir, "unsigned.dll");
        File.WriteAllBytes(unsigned, [0x4D, 0x5A, 0x90, 0x00, 0x01, 0x02, 0x03, 0x04]);

        Assert.IsFalse(AuthenticodeVerifier.IsTrustedMicrosoftSigned(unsigned, NullLogger.Instance),
            "An unsigned file must not pass the Authenticode trust gate.");
    }

    // HRESULTs mirrored from the production constants (which are private).
    private const int CERT_E_REVOKED = unchecked((int)0x800B010C);
    private const int CERT_E_REVOCATION_FAILURE = unchecked((int)0x800B010E);
    private const int CRYPT_E_REVOCATION_OFFLINE = unchecked((int)0x80092013);

    // WTD_* policy flags mirrored from the production constants (which are private), so the two-pass
    // policy can be asserted precisely: whole-chain revocation via locally cached CRLs first, then a
    // signature-only fallback with revocation checking disabled.
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_REVOKE_WHOLECHAIN = 1;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    [TestMethod]
    public void IsTrustedMicrosoftSigned_TrustOkAndMicrosoftSigner_ReturnsTrue()
    {
        (uint RevocationChecks, uint ProvFlags)? firstPass = null;
        var result = AuthenticodeVerifier.IsTrustedMicrosoftSigned(
            "any.dll", NullLogger.Instance,
            verifyTrustCore: (_, revocationChecks, provFlags) =>
            {
                firstPass ??= (revocationChecks, provFlags);
                return 0;
            },
            isMicrosoftSigner: _ => true);

        Assert.IsTrue(result);

        // The single successful pass must use the strict policy: whole-chain revocation, cache-only.
        Assert.IsNotNull(firstPass, "WinVerifyTrust must be invoked.");
        Assert.AreEqual(WTD_REVOKE_WHOLECHAIN, firstPass.Value.RevocationChecks,
            "The initial pass must request whole-chain revocation checking.");
        Assert.AreEqual(WTD_CACHE_ONLY_URL_RETRIEVAL, firstPass.Value.ProvFlags,
            "The initial pass must retrieve revocation data from the local cache only.");
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_TrustOkButNonMicrosoftSigner_ReturnsFalse()
    {
        var result = AuthenticodeVerifier.IsTrustedMicrosoftSigned(
            "any.dll", NullLogger.Instance,
            verifyTrustCore: (_, _, _) => 0,
            isMicrosoftSigner: _ => false);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_CertificateRevoked_ReturnsFalse()
    {
        var signerCalled = false;
        var result = AuthenticodeVerifier.IsTrustedMicrosoftSigned(
            "any.dll", NullLogger.Instance,
            verifyTrustCore: (_, _, _) => CERT_E_REVOKED,
            isMicrosoftSigner: _ => { signerCalled = true; return true; });

        Assert.IsFalse(result, "A revoked certificate is a hard failure.");
        Assert.IsFalse(signerCalled, "Signer check must be skipped once trust verification fails.");
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_RevocationOffline_FallsBackToSignatureOnly_AndPasses()
    {
        // First call (whole-chain, cache-only) reports revocation data unavailable; the fallback
        // signature-only call (revocation checking disabled) succeeds, so trust verification passes
        // and the Microsoft signer gate then applies.
        var calls = new List<(uint RevocationChecks, uint ProvFlags)>();
        var result = AuthenticodeVerifier.IsTrustedMicrosoftSigned(
            "any.dll", NullLogger.Instance,
            verifyTrustCore: (_, revocationChecks, provFlags) =>
            {
                calls.Add((revocationChecks, provFlags));
                return calls.Count == 1 ? CERT_E_REVOCATION_FAILURE : 0;
            },
            isMicrosoftSigner: _ => true);

        Assert.IsTrue(result);
        Assert.AreEqual(2, calls.Count, "The fallback signature-only verification must run.");

        // First pass: WITH revocation checking — full chain, but using locally cached CRLs only.
        Assert.AreEqual(WTD_REVOKE_WHOLECHAIN, calls[0].RevocationChecks,
            "First pass must request whole-chain revocation checking.");
        Assert.AreEqual(WTD_CACHE_ONLY_URL_RETRIEVAL, calls[0].ProvFlags,
            "First pass must retrieve revocation data from the local cache only.");

        // Fallback pass: WITHOUT revocation checking — signature-only once revocation data is offline.
        Assert.AreEqual(WTD_REVOKE_NONE, calls[1].RevocationChecks,
            "Fallback pass must disable revocation checking.");
        Assert.AreEqual(WTD_REVOCATION_CHECK_NONE, calls[1].ProvFlags,
            "Fallback pass must set the no-revocation-check provider flag.");
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_RevocationOffline_FallbackAlsoFails_ReturnsFalse()
    {
        var call = 0;
        var result = AuthenticodeVerifier.IsTrustedMicrosoftSigned(
            "any.dll", NullLogger.Instance,
            verifyTrustCore: (_, _, _) => ++call == 1 ? CRYPT_E_REVOCATION_OFFLINE : unchecked((int)0x80070005),
            isMicrosoftSigner: _ => true);

        Assert.IsFalse(result);
        Assert.AreEqual(2, call);
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_UnrecognizedTrustError_ReturnsFalse()
    {
        // Any other WinVerifyTrust HRESULT (e.g. TRUST_E_NOSIGNATURE) is an untrusted result.
        var result = AuthenticodeVerifier.IsTrustedMicrosoftSigned(
            "any.dll", NullLogger.Instance,
            verifyTrustCore: (_, _, _) => unchecked((int)0x800B0100),
            isMicrosoftSigner: _ => true);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_VerifierThrows_IsCaught_ReturnsFalse()
    {
        var result = AuthenticodeVerifier.IsTrustedMicrosoftSigned(
            "any.dll", NullLogger.Instance,
            verifyTrustCore: (_, _, _) => throw new InvalidOperationException("boom"),
            isMicrosoftSigner: _ => true);

        Assert.IsFalse(result, "An exception during verification must be caught and fail closed.");
    }

    [TestMethod]
    public void IsTrustedMicrosoftSigned_RealMicrosoftSignedBinary_ReturnsTrue()
    {
        // Exercises the real native trust + signer extraction path against known embedded-signed
        // Microsoft OS binaries. At least one of these must validate on a healthy Windows install.
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] candidates =
        [
            Path.Combine(system32, "dllhost.exe"),
            Path.Combine(system32, "taskhostw.exe"),
            Path.Combine(windows, "explorer.exe"),
        ];

        var anyTrusted = candidates
            .Where(File.Exists)
            .Any(f => AuthenticodeVerifier.IsTrustedMicrosoftSigned(f, NullLogger.Instance));

        Assert.IsTrue(anyTrusted,
            "A trusted, embedded-signed Microsoft binary must pass the Authenticode gate.");
    }
}
