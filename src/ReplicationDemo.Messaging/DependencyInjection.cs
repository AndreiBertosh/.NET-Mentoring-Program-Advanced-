using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReplicationDemo.Messaging.Publishing;

namespace ReplicationDemo.Messaging;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the Azure Service Bus client, publisher, and startup provisioner.
    /// The provisioner runs once at startup and creates all required entities,
    /// so both the API and Worker self-heal on a fresh namespace.
    /// </summary>
    public static IServiceCollection AddMessaging(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.Configure<ServiceBusOptions>(configuration.GetSection(ServiceBusOptions.SectionName));

        // ServiceBusClient is thread-safe and designed to be a long-lived singleton.
        services.AddSingleton(sp =>
        {
            var connectionString = configuration
                .GetSection(ServiceBusOptions.SectionName)["ConnectionString"]
                ?? throw new InvalidOperationException(
                    "ServiceBus:ConnectionString is not configured.");

            return new ServiceBusClient(connectionString, new ServiceBusClientOptions
            {
                RetryOptions = new ServiceBusRetryOptions
                {
                    Mode = ServiceBusRetryMode.Exponential,
                    MaxRetries = 5,
                    Delay = TimeSpan.FromSeconds(1),
                    MaxDelay = TimeSpan.FromSeconds(30)
                }
            });
        });

        // Singleton publisher: senders are thread-safe and reused across requests.
        services.AddSingleton<IMessagePublisher, ServiceBusMessagePublisher>();

        // Provisioner: creates all SB entities at startup (idempotent, handles both tiers).
        services.AddHostedService<ServiceBusProvisionerService>();

        return services;
    }
}
