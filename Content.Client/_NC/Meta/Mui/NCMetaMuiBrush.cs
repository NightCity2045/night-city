// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Client.Graphics;

namespace Content.Client._NC.Meta.Mui;

/// <summary>Fixed-cost vertical two-stop gradient, independent of panel pixel area.</summary>
public sealed class NCMetaMuiBrush(Color first, Color last, Color border = default, float borderWidth = 0, float radius = 0) : StyleBox
{
    protected override void DoDraw(DrawingHandleScreen handle, UIBox2 box, float uiScale)
    {
        const int strips = 32;
        if (borderWidth > 0)
            Fill(box, border, border, radius * uiScale);
        var inset = borderWidth * uiScale;
        Fill(new UIBox2(box.Left + inset, box.Top + inset, Math.Max(box.Left + inset, box.Right - inset), Math.Max(box.Top + inset, box.Bottom - inset)),
            first, last, Math.Max(0, radius - borderWidth) * uiScale);

        void Fill(UIBox2 area, Color from, Color to, float corner)
        {
        corner = Math.Min(corner, Math.Min(area.Width, area.Height) / 2);
        for (var i = 0; i < strips; i++)
        {
            var top = area.Top + area.Height * i / strips;
            var bottom = area.Top + area.Height * (i + 1) / strips;
            var middle = (top + bottom) / 2;
            var distance = Math.Max(0, Math.Max(area.Top + corner - middle, middle - (area.Bottom - corner)));
            var trim = corner > 0 ? corner - MathF.Sqrt(Math.Max(0, corner * corner - distance * distance)) : 0;
            handle.DrawRect(new UIBox2(area.Left + trim, top, area.Right - trim, bottom), Color.InterpolateBetween(from, to, (float) i / (strips - 1)));
        }
        }
    }
}
