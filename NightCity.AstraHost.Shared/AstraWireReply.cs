namespace Content.Shared._NC.AstraGraph;

/// <summary>
/// Authoring payload after the host has spoken to AstraGraph.
/// Content assemblies pass only this value.
/// </summary>
public readonly record struct AstraWireReply(string Kind, byte[] Payload);
