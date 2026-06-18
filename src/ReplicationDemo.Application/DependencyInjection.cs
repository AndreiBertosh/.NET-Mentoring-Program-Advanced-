using Microsoft.Extensions.DependencyInjection;
using ReplicationDemo.Application.Consistency;
using ReplicationDemo.Application.Services;

namespace ReplicationDemo.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers all application-layer services: <see cref="ConsistencyManager"/>,
    /// <see cref="IJobManagerService"/>, and <see cref="IJobOrchestratorService"/>.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // IMemoryCache is required by ConsistencyManager.
        // AddMemoryCache is idempotent — safe to call even if already registered by the host.
        services.AddMemoryCache();

        // ConsistencySettings is bound from configuration in the host startup.
        // ConsistencyManager is singleton: the in-memory write-timestamp cache is shared across
        // all requests in the same process, which is the correct semantics for ReadAfterWrite.
        services.AddSingleton<ConsistencyManager>();

        services.AddScoped<IJobManagerService, JobManagerService>();
        services.AddScoped<IJobOrchestratorService, JobOrchestratorService>();

        return services;
    }
}
