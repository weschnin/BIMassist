using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using Autodesk.Revit.UI;
using BIMassist.Mcp.Contracts.Changes;

namespace BIMassist.Mcp.RevitBridge.Changes;

internal static class ApprovalGate
{
    internal static bool CanConfirm(CancellationToken cancellationToken, DateTimeOffset now, DateTimeOffset expiresAtUtc) =>
        !cancellationToken.IsCancellationRequested && now < expiresAtUtc;

    // WPF scroll metrics are DIPs; permit only subpixel rounding at the end.
    internal static bool IsAtBottom(double verticalOffset, double viewportHeight, double extentHeight)
    {
        const double epsilon = 0.5;
        if (!double.IsFinite(verticalOffset) || !double.IsFinite(viewportHeight) ||
            !double.IsFinite(extentHeight) || verticalOffset < 0 || viewportHeight <= 0 || extentHeight < 0)
            return false;
        double maximumOffset = Math.Max(0, extentHeight - viewportHeight);
        return verticalOffset <= maximumOffset + epsilon &&
            maximumOffset - verticalOffset <= epsilon;
    }

    // Unlike Autodesk TaskDialog this dialog is not affected by Revit's
    // DialogBoxShowing.OverrideResult. Same-integrity local UI automation is
    // outside this security boundary; it must not be described as human-proof.
    internal static bool Show(ChangePlan plan, string documentTitle, IntPtr owner, CancellationToken cancellationToken)
    {
        string details = ApplyApprovalPrompt.Format(plan, documentTitle);
        if (!CanConfirm(cancellationToken, DateTimeOffset.UtcNow, plan.ExpiresAtUtc))
            return false;

        var window = new Window
        {
            Title = "BIMassist MCP: Änderung freigeben",
            Width = 700,
            Height = 480,
            MinWidth = 500,
            MinHeight = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        window.Content = BuildContent(plan, details, cancellationToken);
        new WindowInteropHelper(window).Owner = owner;
        var timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        timer.Tick += (_, _) =>
        {
            if (!CanConfirm(cancellationToken, DateTimeOffset.UtcNow, plan.ExpiresAtUtc))
            {
                timer.Stop();
                window.DialogResult = false;
            }
        };
        timer.Start();
        try
        {
            return window.ShowDialog() == true &&
                CanConfirm(cancellationToken, DateTimeOffset.UtcNow, plan.ExpiresAtUtc);
        }
        finally
        {
            timer.Stop();
        }

        UIElement BuildContent(ChangePlan currentPlan, string description, CancellationToken token)
        {
            var root = new DockPanel { Margin = new Thickness(16) };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var no = new Button { Content = "Nein", Width = 90, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            var yes = new Button { Content = "Ja, genau diesen Plan anwenden", Width = 225, IsDefault = false };
            no.Click += (_, _) => window.DialogResult = false;
            yes.Click += (_, _) =>
            {
                if (CanConfirm(token, DateTimeOffset.UtcNow, currentPlan.ExpiresAtUtc))
                    window.DialogResult = true;
                else
                    window.DialogResult = false;
            };
            buttons.Children.Add(no);
            buttons.Children.Add(yes);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            var hash = new TextBlock
            {
                Text = "Plan-Hash (SHA-256): " + currentPlan.PlanHash,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            DockPanel.SetDock(hash, Dock.Bottom);
            root.Children.Add(hash);
            root.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = "Genau diese Parameteränderung erlauben?\n\n" + description,
                    TextWrapping = TextWrapping.Wrap
                }
            });
            return root;
        }
    }

    // Only a caller executing in Revit's ExternalEvent may supply this UIApplication.
    // The HWND is obtained here, never accepted from a batch caller.
    internal static bool ShowBatch(ChangePlan plan, string documentTitle, UIApplication application, CancellationToken cancellationToken)
    {
        string details = BatchApplyApprovalPrompt.Format(plan, documentTitle);
        if (!CanConfirm(cancellationToken, DateTimeOffset.UtcNow, plan.ExpiresAtUtc))
            return false;
        if (application is null)
            throw new ChangePlanFailure(BIMassist.Mcp.Contracts.Protocol.BridgeErrorCodes.InvalidRequest);
        IntPtr owner = application.MainWindowHandle;
        if (!IsValidBatchOwner(owner))
            throw new ChangePlanFailure(BIMassist.Mcp.Contracts.Protocol.BridgeErrorCodes.InvalidRequest);

        var window = new Window
        {
            Title = "BIMassist MCP: Batch-Änderungen freigeben",
            Width = 850,
            Height = 600,
            MinWidth = 500,
            MinHeight = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var no = new Button { Content = "Nein", Width = 90, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var yes = new Button
        {
            Content = "Ja, genau diesen Batch-Plan anwenden", Width = 260,
            IsDefault = false, IsEnabled = false
        };
        no.Click += (_, _) => window.DialogResult = false;
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var hash = new TextBlock
        {
            Text = BatchApplyApprovalPrompt.FixedHashText(plan.PlanHash),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        DockPanel.SetDock(hash, Dock.Bottom);
        root.Children.Add(hash);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // The wrapped prompt must be readable without a second, horizontal axis.
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = details,
                TextWrapping = TextWrapping.Wrap
            }
        };
        void RefreshYes() => yes.IsEnabled =
            IsAtBottom(scroll.VerticalOffset, scroll.ViewportHeight, scroll.ExtentHeight) &&
            CanConfirm(cancellationToken, DateTimeOffset.UtcNow, plan.ExpiresAtUtc);
        scroll.ScrollChanged += (_, _) => RefreshYes(); // Includes programmatic offsets and extent/viewport changes.
        scroll.Loaded += (_, _) => RefreshYes();
        // Recheck on the click too: a queued click or layout change must not approve stale geometry.
        yes.Click += (_, _) => window.DialogResult =
            IsAtBottom(scroll.VerticalOffset, scroll.ViewportHeight, scroll.ExtentHeight) &&
            CanConfirm(cancellationToken, DateTimeOffset.UtcNow, plan.ExpiresAtUtc);
        root.Children.Add(scroll);
        window.Content = root;
        new WindowInteropHelper(window).Owner = owner;
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            if (!CanConfirm(cancellationToken, DateTimeOffset.UtcNow, plan.ExpiresAtUtc))
            {
                timer.Stop();
                window.DialogResult = false;
            }
            else
                RefreshYes();
        };
        timer.Start();
        try
        {
            return window.ShowDialog() == true &&
                IsAtBottom(scroll.VerticalOffset, scroll.ViewportHeight, scroll.ExtentHeight) &&
                CanConfirm(cancellationToken, DateTimeOffset.UtcNow, plan.ExpiresAtUtc);
        }
        finally
        {
            timer.Stop();
        }
    }

    // Intrinsic sanity check, not proof of Revit identity: only UIApplication can
    // provide the HWND to the modal entry point. Reject cross-thread use too.
    internal static bool IsValidBatchOwner(IntPtr owner) =>
        owner != IntPtr.Zero && IsWindow(owner) && GetAncestor(owner, 3) == owner &&
        GetWindowThreadProcessId(owner, out uint processId) == GetCurrentThreadId() &&
        processId == (uint)Environment.ProcessId;

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
