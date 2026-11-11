using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SpankyhoOptimizer.Services;

internal static class AuthenticodeVerifier
{
    // Keep signature verification native so Spotify install does not pay PowerShell startup overhead.
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint CertNameSimpleDisplayType = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint CbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint CbStruct;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertHead
    {
        public uint CbStruct;
        public IntPtr CertContext;
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(
        IntPtr hwnd,
        ref Guid actionId,
        ref WinTrustData trustData);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(
        IntPtr providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certIndex);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint CertGetNameStringW(
        IntPtr certContext,
        uint type,
        uint flags,
        IntPtr typeParameter,
        StringBuilder? name,
        uint nameCapacity);

    internal static bool TryVerify(string path, string expectedSubjectFragment, out string detail)
    {
        detail = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            detail = "Soubor neexistuje.";
            return false;
        }

        var fileInfo = new WinTrustFileInfo
        {
            CbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = Path.GetFullPath(path),
            FileHandle = IntPtr.Zero,
            KnownSubject = IntPtr.Zero
        };

        IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        Guid action = GenericVerifyV2;
        var trustData = new WinTrustData
        {
            CbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
            UiChoice = WtdUiNone,
            RevocationChecks = WtdRevokeWholeChain,
            UnionChoice = WtdChoiceFile,
            FileInfo = fileInfoPtr,
            StateAction = WtdStateActionVerify
        };

        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            uint trustStatus = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
            if (trustStatus != 0)
            {
                detail = $"WinVerifyTrust selhal: 0x{trustStatus:X8}.";
                return false;
            }

            string subject = ReadSignerSubject(trustData.StateData);
            if (string.IsNullOrWhiteSpace(subject))
            {
                detail = "Platn? podpis nem? ?iteln? signer subject.";
                return false;
            }
            if (!subject.Contains(expectedSubjectFragment, StringComparison.OrdinalIgnoreCase))
            {
                detail = $"Signer '{subject}' neodpov?d? o?ek?van?mu '{expectedSubjectFragment}'.";
                return false;
            }

            detail = subject;
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
        finally
        {
            if (trustData.StateData != IntPtr.Zero)
            {
                try
                {
                    trustData.StateAction = WtdStateActionClose;
                    _ = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
                }
                catch { }
            }
            try { Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPtr); } catch { }
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    private static string ReadSignerSubject(IntPtr stateData)
    {
        if (stateData == IntPtr.Zero) return string.Empty;
        IntPtr providerData = WTHelperProvDataFromStateData(stateData);
        if (providerData == IntPtr.Zero) return string.Empty;
        IntPtr signer = WTHelperGetProvSignerFromChain(providerData, 0, false, 0);
        if (signer == IntPtr.Zero) return string.Empty;
        IntPtr providerCert = WTHelperGetProvCertFromChain(signer, 0);
        if (providerCert == IntPtr.Zero) return string.Empty;

        var cert = Marshal.PtrToStructure<CryptProviderCertHead>(providerCert);
        if (cert.CertContext == IntPtr.Zero) return string.Empty;
        uint chars = CertGetNameStringW(cert.CertContext, CertNameSimpleDisplayType, 0, IntPtr.Zero, null, 0);
        if (chars <= 1) return string.Empty;
        var buffer = new StringBuilder((int)chars);
        uint written = CertGetNameStringW(cert.CertContext, CertNameSimpleDisplayType, 0, IntPtr.Zero, buffer, chars);
        return written > 1 ? buffer.ToString() : string.Empty;
    }

    internal static string RunSmoke()
    {
        string windowsRoot = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
        string file = Path.Combine(windowsRoot, "explorer.exe");
        if (!TryVerify(file, "Microsoft", out string validDetail))
            throw new InvalidOperationException("Native Authenticode valid-signature smoke failed: " + validDetail);
        if (TryVerify(file, "Spotify", out _))
            throw new InvalidOperationException("Native Authenticode signer mismatch smoke unexpectedly passed.");
        return "AUTHENTICODE_NATIVE_SMOKE_OK signer=" + validDetail;
    }
}
