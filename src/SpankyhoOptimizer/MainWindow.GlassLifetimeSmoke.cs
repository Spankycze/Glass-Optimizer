using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    // Explicit regression only: detached cached cards must release thread-affine native input
    // ownership on the UI thread, before the CLR finalizer is allowed to reclaim wrappers.
    private async Task VerifyGlassCardDetachReloadAsync()
    {
        var card = CreateNativeGlassElement(new CornerRadius(22));
        card.Width = 160;
        card.Height = 100;
        try
        {
            for (int cycle = 0; cycle < 3; cycle++)
            {
                RootGrid.Children.Add(card);
                await Task.Delay(150);
                var backdrop = card.SystemBackdrop ?? throw new InvalidOperationException("Reload lost card material.");
                await VerifyElementActiveMaterialAsync(card);
                RootGrid.Children.Remove(card);
                await Task.Delay(150);
                if (card.IsLoaded) throw new InvalidOperationException("Card did not unload during lifetime test.");
                var controller = backdrop.GetType().GetField("_controller", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(backdrop) as DesktopAcrylicController;
                if (card.SystemBackdrop != null || controller != null)
                    throw new InvalidOperationException("Detached card still owns its native backdrop; release can escape to the CLR finalizer thread.");
            }

        }
        finally
        {
            RootGrid.Children.Remove(card);
            card.SystemBackdrop = null;
        }
    }
    private static async Task VerifyPopupNativeDetachAsync()
    {
        var popup = MakeContinuationPopup((_, _) => Task.FromResult(new DesktopUpdateCompletion("Ready", "Close")));
        var elements = new System.Collections.Generic.List<SystemBackdropElement>();
        try
        {
            popup.ShowWithoutActivation();
            await Task.Delay(100);
            CollectBackdropElements((DependencyObject)popup.Content, elements);
            if (elements.Count != 1) throw new InvalidOperationException("Popup native material was not present.");
            popup.Close();
            await Task.Delay(100);
            if (elements[0].SystemBackdrop != null)
                throw new InvalidOperationException("Closed popup still retains its inner native backdrop instead of releasing it on the UI thread.");
        }
        finally { popup.Close(); }
    }
}
