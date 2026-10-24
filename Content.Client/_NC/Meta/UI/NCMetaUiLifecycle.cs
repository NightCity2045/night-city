// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Meta.UI;

public static class NCMetaUiLifecycle
{
    /// <summary>Hide while the root is still attached, clearing active and background-window stored focus.</summary>
    public static void BeforeRemove(Control control)
    {
        control.Visible = false;
        control.ReleaseKeyboardFocus();
    }

    public static void RemoveChildren(Control parent)
    {
        foreach (var child in parent.Children.ToArray()) BeforeRemove(child);
        parent.DisposeAllChildren();
    }
}
