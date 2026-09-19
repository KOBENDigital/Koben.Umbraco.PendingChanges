namespace Koben.Umbraco.PendingChanges.Services.Models;

/// <summary>How a block in a saved value relates to the same block in the published value.</summary>
public enum BlockChangeStatus
{
    /// <summary>The block exists in the saved value only: publishing would add it.</summary>
    Added,

    /// <summary>The block exists in both, but some of its values differ.</summary>
    Changed,

    /// <summary>The block exists in the published value only: publishing would take it away.</summary>
    Removed
}
