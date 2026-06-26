using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReplicationDemo.Messaging;
using ReplicationDemo.Messaging.Messages;

namespace ReplicationDemo.Worker.Consumers;

/// <summary>
/// Consumes messages from the <c>job-lifecycle</c> topic / <c>audit-log</c> subscription.
/// Demonstrates the fan-out pattern: every lifecycle event is received independently
/// by both the Orchestrator sync consumer and this audit consumer.
/// In production, write entries to an append-only audit store (e.g. Azure Table Storage, Cosmos DB).
/// </summary>
public sealed class AuditLogConsumer : IHostedService, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusOptions _options;
    private readonly ILogger<AuditLogConsumer> _logger;

    private ServiceBusProcessor? _processor;
    private bool _topicsSupported = false;

    public AuditLogConsumer(
        ServiceBusClient client,
        IOptions<ServiceBusOptions> options,
        ILogger<AuditLogConsumer> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _topicsSupported = await DetectTopicSupportAsync(ct);

        if (!_topicsSupported)
        {
            _logger.LogWarning(
                "AuditLogConsumer: Azure Service Bus Basic tier detected — topic subscriptions unavailable. Consumer will not start.");
            return;
        }

        _processor = _client.CreateProcessor(
            _options.Topics.JobLifecycle,
            _options.Subscriptions.AuditLog,
            new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentCalls = 4
            });

        _processor.ProcessMessageAsync += ProcessMessageAsync;
        _processor.ProcessErrorAsync += ProcessErrorAsync;

        await _processor.StartProcessingAsync(ct);
        _logger.LogInformation("AuditLogConsumer started \u2192 {Topic}/{Sub}",
            _options.Topics.JobLifecycle, _options.Subscriptions.AuditLog);
    }

    private async Task<bool> DetectTopicSupportAsync(CancellationToken ct)
    {
        try
        {
            var adminClient = new ServiceBusAdministrationClient(_options.ConnectionString);
            // Check topic existence — provisioner already ran before consumers start.
            return await adminClient.TopicExistsAsync(_options.Topics.JobLifecycle, ct);
        }
        catch (Exception ex) when (
            ex.Message.Contains("Basic") ||
            (ex.InnerException?.Message.Contains("Basic") == true))
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check topic availability; AuditLogConsumer will not start.");
            return false;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_processor is not null)
            await _processor.StopProcessingAsync(ct);
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var subject = args.Message.Subject ?? "unknown";
        var body = args.Message.Body.ToString();

        // Structured audit log entry (replace with durable audit store in production)
        _logger.LogInformation(
            "[AUDIT] Subject={Subject} MessageId={MessageId} CorrelationId={CorrelationId} Body={Body}",
            subject, args.Message.MessageId, args.Message.CorrelationId, body);

        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "AuditLogConsumer error: Source={Source} EntityPath={EntityPath}",
            args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_processor is not null)
            await _processor.DisposeAsync();
    }
}
