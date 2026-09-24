using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace BIMassist.Mcp.RevitBridge.Transport;

public sealed record PipeServerConfiguration(
    string Name,
    PipeDirection Direction,
    int MaximumServerInstances,
    PipeTransmissionMode TransmissionMode,
    PipeOptions Options,
    bool RejectRemoteClients);

public static class CurrentUserPipeSecurity
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint SddlRevision1 = 1;
    private const uint BufferSize = 64 * 1024;

    public static PipeServerConfiguration CreateConfiguration(string endpointName)
    {
        if (string.IsNullOrWhiteSpace(endpointName) ||
            endpointName.Length > 200 ||
            endpointName.IndexOfAny(['\\', '/', '\0']) >= 0)
        {
            throw new ArgumentException("A valid local pipe endpoint name is required.", nameof(endpointName));
        }

        return new PipeServerConfiguration(
            endpointName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            RejectRemoteClients: true);
    }

    public static NamedPipeServerStream CreateServer(string endpointName)
    {
        PipeServerConfiguration configuration = CreateConfiguration(endpointName);
        string userSid = GetCurrentUserSid();
        string securityDescriptorDefinition =
            $"O:{userSid}G:{userSid}D:P(A;;GA;;;{userSid})(A;;GA;;;SY)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                securityDescriptorDefinition,
                SddlRevision1,
                out IntPtr securityDescriptor,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var securityAttributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = securityDescriptor,
                InheritHandle = 0
            };
            string fullPipeName = $@"\\.\pipe\{configuration.Name}";
            SafePipeHandle handle = CreateNamedPipe(
                fullPipeName,
                PipeAccessDuplex | FileFlagOverlapped | FileFlagFirstPipeInstance,
                PipeRejectRemoteClients,
                (uint)configuration.MaximumServerInstances,
                BufferSize,
                BufferSize,
                0,
                ref securityAttributes);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }

            try
            {
                return new NamedPipeServerStream(
                    configuration.Direction,
                    isAsync: true,
                    isConnected: false,
                    handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            LocalFree(securityDescriptor);
        }
    }

    private static string GetCurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user has no SID.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maximumInstances,
        uint outputBufferSize,
        uint inputBufferSize,
        uint defaultTimeout,
        ref SecurityAttributes securityAttributes);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSecurityDescriptorRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
