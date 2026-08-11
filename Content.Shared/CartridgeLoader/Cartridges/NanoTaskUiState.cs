using Robust.Shared.Serialization;

namespace Content.Shared.CartridgeLoader.Cartridges;

/// <summary>
///     The priority assigned to a NanoTask item
/// </summary>
[Serializable, NetSerializable]
public enum NanoTaskPriority : byte
{
    High,
    Medium,
    Low,
};

// NC start - Agent TASKBOARD workflow states.
/// <summary>
///     Persistent workflow state used by the Agent TASKBOARD interface.
/// </summary>
[Serializable, NetSerializable]
public enum NanoTaskStatus : byte
{
    Open,
    Active,
    Done,
};
// NC end

/// <summary>
///     The data relating to a single NanoTask item, but not its identifier
/// </summary>
[Serializable, NetSerializable, DataRecord]
public sealed partial class NanoTaskItem : IRobustCloneable<NanoTaskItem>
{
    /// <summary>
    ///     The maximum length of the Description and TaskIsFor fields
    /// </summary>
    public static int MaximumStringLength = 30;

    /// <summary>
    ///     The task description, i.e. "Bake a cake"
    /// </summary>
    public readonly string Description;

    /// <summary>
    ///     Who the task is for, i.e. "Cargo"
    /// </summary>
    public readonly string TaskIsFor;

    /// <summary>
    ///     If the task is marked as done or not
    /// </summary>
    public readonly bool IsTaskDone;

    /// <summary>
    ///     The task's marked priority
    /// </summary>
    public readonly NanoTaskPriority Priority;

    // NC start - Agent TASKBOARD extended task data.
    /// <summary>
    ///     Workflow state. IsTaskDone remains synchronized for legacy NanoTask controls.
    /// </summary>
    public readonly NanoTaskStatus Status;

    /// <summary>
    ///     Optional free-form deadline kept deliberately short for the compact Agent display.
    /// </summary>
    public readonly string Deadline;
    // NC end

    public NanoTaskItem(
        string description,
        string taskIsFor,
        bool isTaskDone,
        NanoTaskPriority priority,
        NanoTaskStatus status = NanoTaskStatus.Open,
        string deadline = "")
    {
        Description = description;
        TaskIsFor = taskIsFor;
        Priority = priority;
        // NC start - Old callers only set IsTaskDone, so completed legacy tasks remain completed.
        Status = isTaskDone ? NanoTaskStatus.Done : status;
        IsTaskDone = Status == NanoTaskStatus.Done;
        Deadline = deadline;
        // NC end
    }

    public bool Validate()
    {
        // NC - Deadline uses the same compact field limit as the original NanoTask strings.
        return Description.Length <= MaximumStringLength &&
               TaskIsFor.Length <= MaximumStringLength &&
               Deadline.Length <= MaximumStringLength &&
               Enum.IsDefined(Status);
    }

    public NanoTaskItem Clone()
    {
        // NC - Preserve extended TASKBOARD data when cloning a vanilla task record.
        return new(Description, TaskIsFor, IsTaskDone, Priority, Status, Deadline);
    }
};

/// <summary>
///     Pairs a NanoTask item and its identifier
/// </summary>
[Serializable, NetSerializable, DataRecord]
public sealed partial class NanoTaskItemAndId
{
    public readonly int Id;
    public readonly NanoTaskItem Data;

    public NanoTaskItemAndId(int id, NanoTaskItem data)
    {
        Id = id;
        Data = data;
    }
};

/// <summary>
///     The UI state of the NanoTask
/// </summary>
[Serializable, NetSerializable]
public sealed class NanoTaskUiState : BoundUserInterfaceState
{
    public List<NanoTaskItemAndId> Tasks;

    public NanoTaskUiState(List<NanoTaskItemAndId> tasks)
    {
        Tasks = tasks;
    }
}
