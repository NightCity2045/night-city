// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using Content.Client.UserInterface.Controls;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Meta.UI;

/// <summary>Secondary toolbars stay collapsed and the MUI preview lives in its own window.</summary>
public sealed partial class NCMetaWorkspaceWindow
{
    private FancyWindow? _previewWindow;

    private void InitializeLayout()
    {
        MoreToggle.OnToggled += args => MoreRows.Visible = args.Pressed;
        SearchToggle.OnToggled += args => SearchRow.Visible = args.Pressed;
        FileToolsToggle.OnToggled += args => FileTools.Visible = args.Pressed;
        PreviewOpen.OnPressed += _ =>
        {
            OpenPreviewWindow();
            TryPreview();
        };
        PreviewWindowButton.OnPressed += _ => OpenPreviewWindow();
    }

    private void OpenPreviewWindow()
    {
        if (_previewWindow == null)
        {
            // The same named controls keep working after they are moved: the code-behind only needs the fields.
            PreviewPanel.Orphan();
            PreviewPanel.Margin = new Thickness(8);
            _previewWindow = new FancyWindow
            {
                Title = Loc.GetString("nc-meta-preview-window-title"),
                MinSize = new Vector2(420, 320),
                SetSize = new Vector2(760, 560),
            };
            _previewWindow.ContentsContainer.AddChild(PreviewPanel);
        }

        if (!_previewWindow.IsOpen)
            _previewWindow.OpenCentered();
    }

    private void ClosePreviewWindow()
    {
        if (_previewWindow is { IsOpen: true })
            _previewWindow.Close();
    }

    private void DisposePreviewWindow()
    {
        _previewWindow?.Dispose();
        _previewWindow = null;
    }
}
