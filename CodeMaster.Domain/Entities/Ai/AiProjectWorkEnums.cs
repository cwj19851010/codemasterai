namespace CodeMaster.Domain.Entities.Ai;

public enum AiProjectWorkPlanStatus
{
    Draft = 0,
    Active = 1,
    Completed = 2,
    Failed = 3,
    Blocked = 4,
    Cancelled = 5,
    Superseded = 6,
    AwaitingConfirmation = 7
}

public enum AiProjectWorkStageKind
{
    Foundation = 1,
    Relations = 2,
    DataControls = 3,
    Views = 4,
    ComplexPages = 5,
    EntityFields = 6,
    FieldControls = 7,
    ConsistencyCheck = 8,
    GenerateCode = 9,
    Custom = 100
}

public enum AiProjectWorkStageStatus
{
    Pending = 0,
    Ready = 1,
    InProgress = 2,
    Completed = 3,
    Failed = 4,
    Blocked = 5,
    Rejected = 6,
    Cancelled = 7
}

public enum AiProjectWorkTaskStatus
{
    Pending = 0,
    Ready = 1,
    WaitingApproval = 2,
    Applying = 3,
    Generating = 4,
    Migrating = 5,
    Building = 6,
    Verifying = 7,
    Completed = 8,
    Failed = 9,
    Blocked = 10,
    Rejected = 11,
    Cancelled = 12
}

public enum AiProjectWorkBatchStatus
{
    Draft = 0,
    PendingApproval = 1,
    Approved = 2,
    WaitingClientExecution = 3,
    Executing = 4,
    Completed = 5,
    Failed = 6,
    Rejected = 7,
    Stale = 8,
    Cancelled = 9
}

public enum AiProjectWorkAttemptStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}

public enum AiProjectWorkCheckpointKind
{
    Metadata = 1,
    IdentityResolution = 2,
    Generation = 3,
    Migration = 4,
    ViewDeployment = 5,
    Build = 6,
    Test = 7,
    Verification = 8,
    Repair = 9,
    Custom = 100
}

public enum AiProjectWorkCheckpointStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Invalidated = 4,
    Skipped = 5
}

public enum AiProjectWorkExecutionLocation
{
    Server = 1,
    Client = 2,
    LocalAgent = 3
}

public enum AiProjectWorkSafetyClass
{
    ReadOnly = 0,
    Metadata = 1,
    GeneratedArtifacts = 2,
    Database = 3,
    PlatformSource = 4
}

public enum AiProjectRepairMode
{
    Disabled = 0,
    MetadataOnly = 1,
    TestSourceRepair = 2
}

public enum AiProjectFailureCategory
{
    None = 0,
    PlanOrMetadata = 1,
    GeneratorOrTemplate = 2,
    GeneratedProjectIntegration = 3,
    ExternalEnvironment = 4,
    Unknown = 5
}
