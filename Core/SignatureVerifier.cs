using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UpdateHub.Core;

public enum SignatureStatus
{
    NotChecked = 0,
    /// <summary>Signed, chain verified, and the signature matches the file.</summary>
    Valid = 1,
    /// <summary>Signed, but the certificate chain could not be validated.</summary>
    UntrustedRoot = 2,
    /// <summary>Signature present but does not match the file contents.</summary>
    Invalid = 3,
    Unsigned = 4,
    Expired = 5,
    FileNotFound = 6,
    Error = 7,
}

public sealed record SignatureInfo(
    SignatureStatus Status,
    string? SubjectName,
    string? IssuerName,
    DateTime? NotAfter,
    string? Thumbprint)
{
    public bool IsTrusted => Status == SignatureStatus.Valid;

    public string StatusText => Status switch
    {
        SignatureStatus.Valid => "Signed and verified",
        SignatureStatus.UntrustedRoot => "Signed, but the issuer is not trusted",
        SignatureStatus.Invalid => "Signature does not match the file",
        SignatureStatus.Unsigned => "Not digitally signed",
        SignatureStatus.Expired => "Signing certificate has expired",
        SignatureStatus.FileNotFound => "File not found",
        SignatureStatus.NotChecked => "Not checked",
        _ => "Could not verify",
    };

    public string Publisher => SubjectName ?? "Unknown";

    public static SignatureInfo None(SignatureStatus status) => new(status, null, null, null, null);
}

/// <summary>
/// Authenticode verification through WinVerifyTrust — the same check Windows itself
/// performs. Used to confirm a downloaded installer really comes from its publisher before
/// it is executed, and to show who signed the software already installed.
/// </summary>
public static class SignatureVerifier
{
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_WHOLECHAIN = 1;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_SAFER_FLAG = 0x100;

    // Selected values from winerror.h that WinVerifyTrust returns.
    private const uint TRUST_E_NOSIGNATURE = 0x800B0100;
    private const uint TRUST_E_BAD_DIGEST = 0x80096010;
    private const uint TRUST_E_PROVIDER_UNKNOWN = 0x800B0001;
    private const uint TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003;
    private const uint TRUST_E_SUBJECT_NOT_TRUSTED = 0x800B0004;
    private const uint CERT_E_UNTRUSTEDROOT = 0x800B0109;
    private const uint CERT_E_CHAINING = 0x800B010A;
    private const uint CERT_E_EXPIRED = 0x800B0101;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID,
        ref WINTRUST_DATA pWVTData);

    public static SignatureInfo Verify(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return SignatureInfo.None(SignatureStatus.FileNotFound);

        uint result;
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_SAFER_FLAG,
            };

            try
            {
                result = WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, ref data);
            }
            finally
            {
                // The verify call allocates state that must be released with a second call.
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                try { WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, ref data); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }
        }
        catch (DllNotFoundException) { return SignatureInfo.None(SignatureStatus.Error); }
        catch (EntryPointNotFoundException) { return SignatureInfo.None(SignatureStatus.Error); }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }

        var status = result switch
        {
            0 => SignatureStatus.Valid,
            TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN
                => SignatureStatus.Unsigned,
            TRUST_E_BAD_DIGEST => SignatureStatus.Invalid,
            CERT_E_UNTRUSTEDROOT or CERT_E_CHAINING or TRUST_E_SUBJECT_NOT_TRUSTED
                => SignatureStatus.UntrustedRoot,
            CERT_E_EXPIRED => SignatureStatus.Expired,
            _ => SignatureStatus.Error,
        };

        var cert = ReadCertificate(filePath);
        return new SignatureInfo(
            status,
            cert?.GetNameInfo(X509NameType.SimpleName, false),
            cert?.GetNameInfo(X509NameType.SimpleName, true),
            cert?.NotAfter,
            cert?.Thumbprint);
    }

    private static X509Certificate2? ReadCertificate(string filePath)
    {
        try
        {
            // Present on signed files only; unsigned files throw rather than return null.
            return new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
        }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Confirms a downloaded installer is signed and trusted, and — when the expected
    /// publisher is known — that it was signed by them rather than merely by somebody.
    /// </summary>
    public static (bool Ok, string Reason) VerifyDownload(string filePath, string? expectedPublisher)
    {
        var info = Verify(filePath);

        if (info.Status == SignatureStatus.FileNotFound)
            return (false, "The downloaded file is missing.");
        if (info.Status is SignatureStatus.Invalid)
            return (false, "The installer's signature does not match its contents — it may be corrupt or tampered with.");
        if (info.Status is SignatureStatus.Unsigned)
            return (false, "The installer is not digitally signed.");
        if (info.Status is SignatureStatus.UntrustedRoot)
            return (false, "The installer is signed by an issuer this machine does not trust.");
        if (info.Status is SignatureStatus.Expired)
            return (false, "The installer's signing certificate has expired.");
        if (info.Status != SignatureStatus.Valid)
            return (false, "The installer's signature could not be verified.");

        if (!string.IsNullOrWhiteSpace(expectedPublisher) && info.SubjectName is not null)
        {
            var expected = NameNormalizer.Normalize(expectedPublisher);
            var actual = NameNormalizer.Normalize(info.SubjectName);
            // Publisher strings rarely match exactly ("Google LLC" vs "Google Inc"), so a
            // containment test in either direction is the practical check.
            if (expected.Length > 3 && actual.Length > 3 &&
                !actual.Contains(expected, StringComparison.Ordinal) &&
                !expected.Contains(actual, StringComparison.Ordinal))
                return (false, $"Signed by \"{info.SubjectName}\", but \"{expectedPublisher}\" was expected.");
        }

        return (true, $"Signed by {info.SubjectName}.");
    }
}
