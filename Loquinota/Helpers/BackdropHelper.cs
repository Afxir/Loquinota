using Loquinota.Helpers;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Loquinota.Helpers;

/// <summary>
/// Applies a Mica (Windows 11) or Acrylic (Windows 10) backdrop when the OS supports it,
/// and reports whether the window background should stay transparent.
/// </summary>
public static class BackdropHelper
{
    public static bool TryApply(Window window)
    {
        try
        {
            if (MicaController.IsSupported())
            {
                window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                Log.Info("Applied Mica backdrop.");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Mica backdrop unavailable.", ex);
        }

        try
        {
            if (DesktopAcrylicController.IsSupported())
            {
                window.SystemBackdrop = new DesktopAcrylicBackdrop();
                Log.Info("Applied Acrylic backdrop (Mica unsupported on this OS).");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Acrylic backdrop unavailable.", ex);
        }

        Log.Info("No system backdrop available; using solid Material surface.");
        return false;
    }
}
