using System.Windows.Controls;
using System.Windows.Input;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;

namespace Pulse.App;

/// <summary>
/// RailWindow code-behind partial: mouse handlers referenced by RailWindow.xaml.
/// XAML currently wires no events (constructed programmatically); drag handlers are
/// attached in the constructor. Kept in a separate file so the XAML partial stays
/// focused on ring rendering.
/// </summary>
public static class RailWindowBehavior
{
    /// <summary>Attach drag-to-reposition handlers to the rail window.</summary>
    public static void AttachDrag(RailWindow window)
    {
        window.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { window.DragMove(); } catch (InvalidOperationException) { /* inactive window */ }
            }
        };
    }
}
