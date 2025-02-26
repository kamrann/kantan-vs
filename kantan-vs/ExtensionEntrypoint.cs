namespace Kantan;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;
using System.Resources;

/// <summary>
/// Extension entrypoint for the VisualStudio.Extensibility extension.
/// </summary>
[VisualStudioContribution]
internal class ExtensionEntrypoint : Extension
{
    /// <inheritdoc/>
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
                id: "Kantan.a0704f0e-bb28-4be9-9edd-458c7e6766ac",
                version: this.ExtensionAssemblyVersion,
                publisherName: "Tokamak",
                displayName: "Kantan VS",
                description: "VS extension for kantan development"),
    };

    protected override ResourceManager? ResourceManager => Strings.ResourceManager;

    /// <inheritdoc />
    protected override void InitializeServices(IServiceCollection serviceCollection)
    {
        base.InitializeServices(serviceCollection);

        // You can configure dependency injection here by adding services to the serviceCollection.
        serviceCollection.AddScoped<OutputUtilsService>();
        serviceCollection.AddScoped<DocumentTrackingService>();
    }
}
