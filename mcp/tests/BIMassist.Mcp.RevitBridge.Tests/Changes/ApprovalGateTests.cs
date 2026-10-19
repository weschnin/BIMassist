using BIMassist.Mcp.RevitBridge.Changes;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace BIMassist.Mcp.RevitBridge.Tests.Changes;

public sealed class ApprovalGateTests
{
    [Fact]
    public void Batch_modal_requires_Revit_UIApplication_and_exposes_no_HWND_overload()
    {
        // Inspect PE metadata without loading RevitAPIUI into the unit-test host.
        using var stream = File.OpenRead(typeof(ApprovalGate).Assembly.Location);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        MethodDefinitionHandle methodHandle = Assert.Single(metadata.MethodDefinitions,
            handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "ShowBatch");
        BlobReader signature = metadata.GetBlobReader(metadata.GetMethodDefinition(methodHandle).Signature);
        _ = signature.ReadSignatureHeader();
        Assert.Equal(4, signature.ReadCompressedInteger());
        Assert.Equal((byte)0x02, signature.ReadByte()); // bool return
        Assert.Equal("BIMassist.Mcp.Contracts.Changes.ChangePlan", ReadTypeName(ref signature, metadata));
        Assert.Equal((byte)0x0e, signature.ReadByte()); // string
        Assert.Equal("Autodesk.Revit.UI.UIApplication", ReadTypeName(ref signature, metadata));
        Assert.Equal("System.Threading.CancellationToken", ReadTypeName(ref signature, metadata));
        Assert.Equal(0, signature.RemainingBytes);
    }

    private static string ReadTypeName(ref BlobReader signature, MetadataReader metadata)
    {
        byte kind = signature.ReadByte();
        Assert.True(kind is 0x11 or 0x12); // value type or class
        int coded = signature.ReadCompressedInteger();
        Assert.Equal(1, coded & 3); // external TypeRef
        TypeReference reference = metadata.GetTypeReference(MetadataTokens.TypeReferenceHandle(coded >> 2));
        return metadata.GetString(reference.Namespace) + "." + metadata.GetString(reference.Name);
    }

    [Fact]
    public void Batch_owner_validation_rejects_zero_desktop_child_and_destroyed_handles()
    {
        Assert.False(ApprovalGate.IsValidBatchOwner(IntPtr.Zero));
        Assert.False(ApprovalGate.IsValidBatchOwner(GetDesktopWindow()));
        IntPtr parent = CreateWindowEx(0, "STATIC", "owner test", 0x80000000,
            0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, parent);
        try
        {
            IntPtr child = CreateWindowEx(0, "STATIC", "child", 0x40000000,
                0, 0, 50, 50, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, child);
            try
            {
                Assert.True(ApprovalGate.IsValidBatchOwner(parent));
                Assert.False(ApprovalGate.IsValidBatchOwner(child));
            }
            finally { Assert.True(DestroyWindow(child)); }
            Assert.False(ApprovalGate.IsValidBatchOwner(child));
        }
        finally { Assert.True(DestroyWindow(parent)); }
        Assert.False(ApprovalGate.IsValidBatchOwner(parent));
    }

    [Fact]
    public void Batch_owner_validation_rejects_window_on_another_UI_thread()
    {
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        IntPtr owner = IntPtr.Zero;
        var thread = new Thread(() =>
        {
            owner = CreateWindowEx(0, "STATIC", "other UI thread", 0x80000000,
                0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            ready.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            if (owner != IntPtr.Zero) DestroyWindow(owner);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotEqual(IntPtr.Zero, owner);
            Assert.False(ApprovalGate.IsValidBatchOwner(owner));
        }
        finally
        {
            release.Set();
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [Theory]
    [InlineData(0, 500, 500, true)] // Entire prompt fits without scrolling.
    [InlineData(0, 500, 400, true)]
    [InlineData(0, 500, 1000, false)] // Not yet at the bottom.
    [InlineData(499.49, 500, 1000, false)] // More than half a DIP remains.
    [InlineData(499.5, 500, 1000, true)] // Tolerate subpixel WPF rounding.
    [InlineData(500, 500, 1000, true)]
    public void Batch_scroll_confirmation_requires_the_current_bottom(
        double verticalOffset, double viewportHeight, double extentHeight, bool expected)
    {
        Assert.Equal(expected, ApprovalGate.IsAtBottom(verticalOffset, viewportHeight, extentHeight));
    }

    [Theory]
    [InlineData(0, 0, 1000)]
    [InlineData(0, -1, 1000)]
    [InlineData(0, 500, -1)]
    [InlineData(-1, 500, 1000)]
    [InlineData(501, 500, 1000)] // Stale/out-of-range offset must not bypass the gate.
    [InlineData(double.NaN, 500, 1000)]
    [InlineData(0, double.NaN, 1000)]
    [InlineData(0, 500, double.NaN)]
    [InlineData(double.PositiveInfinity, 500, 1000)]
    [InlineData(0, double.PositiveInfinity, 1000)]
    [InlineData(0, 500, double.PositiveInfinity)]
    public void Batch_scroll_rejects_invalid_or_unmeasured_geometry(
        double verticalOffset, double viewportHeight, double extentHeight)
    {
        Assert.False(ApprovalGate.IsAtBottom(verticalOffset, viewportHeight, extentHeight));
    }

    [Fact]
    public void Expanded_content_or_smaller_viewport_relocks_confirmation()
    {
        Assert.True(ApprovalGate.IsAtBottom(500, 500, 1000));
        Assert.False(ApprovalGate.IsAtBottom(500, 500, 1100));
        Assert.False(ApprovalGate.IsAtBottom(0, 500, 800));
        Assert.True(ApprovalGate.IsAtBottom(0, 800, 800));
        Assert.False(ApprovalGate.IsAtBottom(0, 500, 800));
    }

    [Fact]
    public void Modal_approval_closes_without_a_click_when_request_is_cancelled()
    {
        bool? result = null;
        Exception? failure = null;
        using var cancellation = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            try
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                var plan = new ChangePlan
                {
                    PlanId = "plan-1", SessionId = "session-1", DocumentKey = "document-1",
                    ExpectedRevision = "session-1:0", CreatedAtUtc = now,
                    ExpiresAtUtc = now.AddMinutes(1), PlanHash = new string('a', 64), Warnings = [],
                    Operations = [new ChangeOperation
                    {
                        OperationId = "operation-1", Kind = ChangeOperationKind.SetParameterValue,
                        Target = new ParameterTarget { Kind = ParameterTargetKind.Document },
                        Parameter = new ParameterIdentity
                        {
                            Kind = ParameterIdentityKind.BuiltIn, BuiltInId = -1001203,
                            StableId = "built-in:-1001203", Name = "Projektstatus"
                        },
                        Before = new ParameterValue
                        {
                            Kind = ParameterValueKind.String, HasValue = true, IsReadOnly = false, StringValue = "vorher"
                        },
                        After = new ParameterValue
                        {
                            Kind = ParameterValueKind.String, HasValue = true, IsReadOnly = false, StringValue = "nachher"
                        },
                        Options = new Dictionary<string, string>()
                    }]
                };
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));
                result = ApprovalGate.Show(plan, "Testprojekt", IntPtr.Zero, cancellation.Token);
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The modal dialog did not close after cancellation.");
        Assert.Null(failure);
        Assert.False(result);
    }

    [Fact]
    public void Cancelled_or_expired_confirmation_cannot_authorize_write()
    {
        using var cancellation = new CancellationTokenSource();
        DateTimeOffset expires = DateTimeOffset.UtcNow.AddMinutes(1);
        Assert.True(ApprovalGate.CanConfirm(cancellation.Token, DateTimeOffset.UtcNow, expires));
        cancellation.Cancel();
        Assert.False(ApprovalGate.CanConfirm(cancellation.Token, DateTimeOffset.UtcNow, expires));
        Assert.False(ApprovalGate.CanConfirm(CancellationToken.None, expires, expires));
    }
}
