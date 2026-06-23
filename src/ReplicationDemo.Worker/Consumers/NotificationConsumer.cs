using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReplicationDemo.Messaging;
using ReplicationDemo.Messaging.Messages;

namespace ReplicationDemo.Worker.Consumers;

/// <summary>
/// Consumes <see cref="JobExecutionNotificationMessage"/> from the <c>notifications</c> queue.
/// Simulates delivery to user channels (email, webhook, in-app alert).
/// In production, replace the log statement with calls to your notification service.
/// </summary>
public sealed class NotificationConsumer : IHostedService, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusOptions _options;
    private readonly ILogger<NotificationConsumer> _logger;

    private ServiceBusProcessor? _processor;

    public NotificationConsumer(
        ServiceBusClient client,
        IOptions<ServiceBusOptions> options,
        ILogger<NotificationConsumer> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _processor = _client.CreateProcessor(
            _options.Queues.Notifications,
            new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentCalls = 8
            });

        _processor.ProcessMessageAsync += ProcessMessageAsync;
        _processor.ProcessErrorAsync += ProcessErrorAsync;

        await _processor.StartProcessingAsync(ct);
        _logger.LogInformation("NotificationConsumer started → {Queue}", _options.Queues.Notifications);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_processor is not null)
            await _processor.StopProcessingAsync(ct);
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var msg = args.Message.Body.ToObjectFromJson<JobExecutionNotificationMessage>()!;

        // Simulate notification delivery (replace with real email/webhook/SignalR push)
        _logger.LogInformation(
            "[NOTIFY] JobId={JobId} JobName=\"{JobName}\" EventType={EventType} " +
            "Status={Status} Started={StartedAt:u} Completed={CompletedAt:u} Error={Error}",
            msg.Payload.JobId,
            msg.Payload.JobName,
            msg.Payload.EventType,
            msg.Payload.Status,
            msg.Payload.StartedAt,
            msg.Payload.CompletedAt,
            msg.Payload.ErrorMessage ?? "none");

        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "NotificationConsumer error: Source={Source} EntityPath={EntityPath}",
            args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_processor is not null)
            await _processor.DisposeAsync();
    }
}
