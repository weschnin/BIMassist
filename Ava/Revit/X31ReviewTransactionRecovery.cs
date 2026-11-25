using System;
using System.Collections.Generic;

namespace BIMassist.Ava.Revit;

/// <summary>Resolves a retained Revit transaction without disposing it before a terminal status.</summary>
public static class X31ReviewTransactionRecovery
{
    public readonly record struct Recovery<TStatus>(TStatus Status, bool CanDispose, Exception? Error);

    public static Recovery<TStatus> Resolve<TStatus>(Func<TStatus> getStatus, Func<TStatus> rollback,
        TStatus started, TStatus pending, Func<TStatus, bool> isTerminal)
    {
        ArgumentNullException.ThrowIfNull(getStatus);
        ArgumentNullException.ThrowIfNull(rollback);
        ArgumentNullException.ThrowIfNull(isTerminal);
        var status = default(TStatus)!;
        try
        {
            status = getStatus();
            if (EqualityComparer<TStatus>.Default.Equals(status, pending))
                return new Recovery<TStatus>(status, false, null);
            if (EqualityComparer<TStatus>.Default.Equals(status, started))
            {
                status = rollback();
                if (EqualityComparer<TStatus>.Default.Equals(status, started)
                    || EqualityComparer<TStatus>.Default.Equals(status, pending))
                    return new Recovery<TStatus>(status, false, null);
            }
            return new Recovery<TStatus>(status, isTerminal(status), null);
        }
        catch (Exception ex)
        {
            return new Recovery<TStatus>(status, false, ex);
        }
    }
}
