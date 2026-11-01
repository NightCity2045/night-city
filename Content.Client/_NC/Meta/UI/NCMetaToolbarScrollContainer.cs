// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._NC.Meta.UI;

/// <summary>Keeps a horizontal toolbar's natural height while its viewport owns the available width.</summary>
public sealed class NCMetaToolbarScrollContainer : ScrollContainer
{
    public NCMetaToolbarScrollContainer()
    {
        VScrollEnabled = false;
        ReturnMeasure = true;
        HorizontalExpand = true;
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var measured = base.MeasureOverride(availableSize);
        return new Vector2(0, measured.Y);
    }
}
