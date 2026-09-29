using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using BIMassist.Mcp.Contracts.Changes;

namespace BIMassist.Mcp.RevitBridge.Changes;

internal static class ApprovalGate
{
    internal static bool CanConfirm(CancellationToken cancellationToken, DateTimeOffset now, DateTimeOffset expiresAtUtc) =>
        !cancellationToken.IsCancellationRequested && now < expiresAtUtc;

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
}
