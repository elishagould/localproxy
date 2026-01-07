using System;
using System.Runtime.InteropServices;

namespace localproxy;

public class SspiHelper : IDisposable
{
    private IntPtr _credHandle;
    private IntPtr _contextHandle;
    private bool _contextInitialized;
    private readonly string _package;
    
    public string Scheme => _package;

    public SspiHelper(string package)
    {
        _package = package;
        _credHandle = IntPtr.Zero;
        _contextHandle = IntPtr.Zero;
        _contextInitialized = false;
    }

    public byte[] GetClientToken(byte[] serverToken)
    {
        const int SECPKG_CRED_OUTBOUND = 2;
        const int SECURITY_NATIVE_DREP = 0x00000010;
        const int SEC_E_OK = 0;
        const int SEC_I_CONTINUE_NEEDED = 0x00090312;
        const int ISC_REQ_CONFIDENTIALITY = 0x00000010;

        if (_credHandle == IntPtr.Zero)
        {
            var lifetimeStruct = new SECURITY_INTEGER();
            var credHandleStruct = new SecHandle();
            
            int result = AcquireCredentialsHandle(
                null,
                _package,
                SECPKG_CRED_OUTBOUND,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                ref credHandleStruct,
                ref lifetimeStruct);

            if (result != SEC_E_OK)
            {
                throw new Exception($"AcquireCredentialsHandle failed with error: 0x{result:X8}");
            }

            _credHandle = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SecHandle)));
            Marshal.StructureToPtr(credHandleStruct, _credHandle, false);
        }

        SecBufferDesc inputBuffer = default;
        SecBuffer inputSecBuffer = default;
        IntPtr inputBufferPtr = IntPtr.Zero;

        if (serverToken != null && serverToken.Length > 0)
        {
            inputSecBuffer = new SecBuffer
            {
                cbBuffer = serverToken.Length,
                BufferType = 2,
                pvBuffer = Marshal.AllocHGlobal(serverToken.Length)
            };
            Marshal.Copy(serverToken, 0, inputSecBuffer.pvBuffer, serverToken.Length);

            inputBuffer = new SecBufferDesc
            {
                ulVersion = 0,
                cBuffers = 1,
                pBuffers = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SecBuffer)))
            };
            Marshal.StructureToPtr(inputSecBuffer, inputBuffer.pBuffers, false);
            inputBufferPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SecBufferDesc)));
            Marshal.StructureToPtr(inputBuffer, inputBufferPtr, false);
        }

        var outputSecBuffer = new SecBuffer
        {
            cbBuffer = 12288,
            BufferType = 2,
            pvBuffer = Marshal.AllocHGlobal(12288)
        };

        var outputBuffer = new SecBufferDesc
        {
            ulVersion = 0,
            cBuffers = 1,
            pBuffers = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SecBuffer)))
        };
        Marshal.StructureToPtr(outputSecBuffer, outputBuffer.pBuffers, false);

        IntPtr outputBufferPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SecBufferDesc)));
        Marshal.StructureToPtr(outputBuffer, outputBufferPtr, false);

        var newContext = new SecHandle();
        IntPtr newContextPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SecHandle)));
        Marshal.StructureToPtr(newContext, newContextPtr, false);

        var credHandleValue = Marshal.PtrToStructure<SecHandle>(_credHandle);
        IntPtr contextPtr = _contextInitialized ? _contextHandle : IntPtr.Zero;

        uint contextAttr = 0;
        var lifetimeValue = new SECURITY_INTEGER();

        int status = InitializeSecurityContext(
            ref credHandleValue,
            contextPtr,
            null,
            ISC_REQ_CONFIDENTIALITY,
            0,
            SECURITY_NATIVE_DREP,
            inputBufferPtr,
            0,
            newContextPtr,
            outputBufferPtr,
            out contextAttr,
            ref lifetimeValue);

        outputBuffer = Marshal.PtrToStructure<SecBufferDesc>(outputBufferPtr);
        outputSecBuffer = Marshal.PtrToStructure<SecBuffer>(outputBuffer.pBuffers);

        byte[] token = new byte[outputSecBuffer.cbBuffer];
        Marshal.Copy(outputSecBuffer.pvBuffer, token, 0, outputSecBuffer.cbBuffer);

        if (!_contextInitialized)
        {
            _contextHandle = newContextPtr;
            _contextInitialized = true;
        }

        if (inputBufferPtr != IntPtr.Zero)
        {
            if (inputSecBuffer.pvBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(inputSecBuffer.pvBuffer);
            Marshal.FreeHGlobal(inputBuffer.pBuffers);
            Marshal.FreeHGlobal(inputBufferPtr);
        }
        Marshal.FreeHGlobal(outputSecBuffer.pvBuffer);
        Marshal.FreeHGlobal(outputBuffer.pBuffers);
        Marshal.FreeHGlobal(outputBufferPtr);

        if (status != SEC_E_OK && status != SEC_I_CONTINUE_NEEDED)
        {
            throw new Exception($"InitializeSecurityContext failed with error: 0x{status:X8}");
        }

        return token;
    }

    public void Dispose()
    {
        if (_contextHandle != IntPtr.Zero)
        {
            try
            {
                var context = Marshal.PtrToStructure<SecHandle>(_contextHandle);
                DeleteSecurityContext(ref context);
                Marshal.FreeHGlobal(_contextHandle);
            }
            catch { }
            _contextHandle = IntPtr.Zero;
        }

        if (_credHandle != IntPtr.Zero)
        {
            try
            {
                var cred = Marshal.PtrToStructure<SecHandle>(_credHandle);
                FreeCredentialsHandle(ref cred);
                Marshal.FreeHGlobal(_credHandle);
            }
            catch { }
            _credHandle = IntPtr.Zero;
        }
    }

    [DllImport("secur32.dll", SetLastError = true)]
    private static extern int AcquireCredentialsHandle(
        string pszPrincipal,
        string pszPackage,
        int fCredentialUse,
        IntPtr pvLogonId,
        IntPtr pAuthData,
        IntPtr pGetKeyFn,
        IntPtr pvGetKeyArgument,
        ref SecHandle phCredential,
        ref SECURITY_INTEGER ptsExpiry);

    [DllImport("secur32.dll", SetLastError = true)]
    private static extern int InitializeSecurityContext(
        ref SecHandle phCredential,
        IntPtr phContext,
        string pszTargetName,
        int fContextReq,
        int Reserved1,
        int TargetDataRep,
        IntPtr pInput,
        int Reserved2,
        IntPtr phNewContext,
        IntPtr pOutput,
        out uint pfContextAttr,
        ref SECURITY_INTEGER ptsExpiry);

    [DllImport("secur32.dll", SetLastError = true)]
    private static extern int DeleteSecurityContext(ref SecHandle phContext);

    [DllImport("secur32.dll", SetLastError = true)]
    private static extern int FreeCredentialsHandle(ref SecHandle phCredential);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecHandle
    {
        public IntPtr dwLower;
        public IntPtr dwUpper;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_INTEGER
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecBuffer
    {
        public int cbBuffer;
        public int BufferType;
        public IntPtr pvBuffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecBufferDesc
    {
        public int ulVersion;
        public int cBuffers;
        public IntPtr pBuffers;
    }
}
