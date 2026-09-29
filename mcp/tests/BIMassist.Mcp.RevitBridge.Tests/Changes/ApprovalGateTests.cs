using BIMassist.Mcp.RevitBridge.Changes;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.RevitBridge.Tests.Changes;

public sealed class ApprovalGateTests
{
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
