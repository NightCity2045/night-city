// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Meta.UI;

// Transient editor buffers. These messages never save sources or grant execution authority.
[Serializable, NetSerializable]
public sealed record NCMetaDraftFile(Guid Id, string Path, long Revision, int Bytes, string Hash);

[Serializable, NetSerializable]
public sealed class NCMetaAuthoringBeginMessage(Guid id, Guid project, long revision, Guid selected, int kind,
    int caret, List<NCMetaDraftFile> files, List<NCMetaFileSummary> inventory) : BoundUserInterfaceMessage
{
    public readonly Guid Id = id, Project = project, Selected = selected;
    public readonly long Revision = revision;
    public readonly int Kind = kind, Caret = caret;
    public readonly List<NCMetaDraftFile> Files = files;
    public readonly List<NCMetaFileSummary> Inventory = inventory;
}

[Serializable, NetSerializable]
public sealed class NCMetaAuthoringChunkMessage(Guid id, int file, int offset, byte[] bytes) : BoundUserInterfaceMessage
{
    public readonly Guid Id = id;
    public readonly int File = file, Offset = offset;
    public readonly byte[] Bytes = bytes;
}

[Serializable, NetSerializable]
public sealed class NCMetaAuthoringRunMessage(Guid id, bool cancel = false) : BoundUserInterfaceMessage
{
    public readonly Guid Id = id;
    public readonly bool Cancel = cancel;
}

[Serializable, NetSerializable]
public sealed record NCMetaAuthoringItem(string Name, string Signature, string Path, string Hash, int Offset);

[Serializable, NetSerializable]
public sealed class NCMetaAuthoringReplyMessage(Guid id) : BoundUserInterfaceMessage
{
    public readonly Guid Id = id;
    public bool Ready, Complete, Rejected;
    public int File = -1, Offset;
    public string Signature = "", Code = "", ViewId = "";
    public List<NCMetaAuthoringItem> Items = [];
    public List<NCMetaDiagnostic> Diagnostics = [];
    public NCMetaMuiPresentation? Presentation;
    public NCMetaUiValue? Values;
}
