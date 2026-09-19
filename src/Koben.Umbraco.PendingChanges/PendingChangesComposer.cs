using Koben.Umbraco.PendingChanges.Services;
using Koben.Umbraco.PendingChanges.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace Koben.Umbraco.PendingChanges;

/// <summary>
/// Registers the comparison service. The controller and the backoffice extension are discovered by
/// Umbraco's own type scanning and static web asset scanning, so installing the package is enough.
/// </summary>
public sealed class PendingChangesComposer : IComposer
{
    /// <inheritdoc />
    public void Compose(IUmbracoBuilder builder)
    {
        // Stateless, over Umbraco's own singleton services.
        builder.Services.AddSingleton<IPendingChangesService, PendingChangesService>();
    }
}
