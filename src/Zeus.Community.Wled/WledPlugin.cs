// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.AspNetCore.Routing;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Community.Wled;

/// <summary>
/// Thin shell between Zeus and <see cref="WledService"/>: lifecycle and route registration only.
/// Everything else is SDK-free and unit tested.
/// </summary>
public sealed class WledPlugin : IZeusPlugin, IBackendPlugin
{
    private WledService? _service;

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        var service = new WledService(
            context.Logger,
            HostBridge.CreateSettingsStore(context),
            HostBridge.CreateRadioFeed(context));
        await service.StartAsync(ct);
        _service = service;
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        var service = _service;
        _service = null;
        if (service is not null) await service.DisposeAsync();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        WledEndpoints.Map(endpoints, () => _service);
}
