namespace Koben.Umbraco.PendingChanges.Services.Models;

/// <summary>Which half of a block an element holds.</summary>
public enum BlockElementScope
{
    /// <summary>The block's content: the properties an editor fills in.</summary>
    Content,

    /// <summary>The block's settings: the properties configured behind the block.</summary>
    Settings
}
