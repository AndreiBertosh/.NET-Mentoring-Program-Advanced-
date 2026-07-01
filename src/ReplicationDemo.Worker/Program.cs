using ReplicationDemo.Application;
using ReplicationDemo.DAL;
using ReplicationDemo.Messaging;
using ReplicationDemo.Worker.Consumers;
using ReplicationDemo.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

// ------------------------------------------------------------------
// Configuration
// ------------------------------------------------------------------
builder.Services.Configure<PollingOptions>(
    builder.Configuration.GetSection(PollingOptions.SectionName));

// ------------------------------------------------------------------
// Observability — Azure Application Insights (Job Runner telemetry)
//   • Reads ApplicationInsights:ConnectionString from configuration.
//   • Auto-collects CPU (cpu-usage) and Memory (working-set) via EventCounters,
//     plus dependency / Service Bus telemetry — non-blocking, off the hot path.
// ------------------------------------------------------------------
builder.Services.AddApplicationInsightsTelemetryWorkerService();

builder.Services.AddSingleton<Microsoft.ApplicationInsights.Extensibility.ITelemetryInitializer, RoleNameInitializer>();

builder.Services.Configure<JobRunnerMetricsOptions>(
    builder.Configuration.GetSection(JobRunnerMetricsOptions.SectionName));
builder.Services.AddSingleton<IJobRunnerMetrics, JobRunnerMetrics>();

// ------------------------------------------------------------------
// Data access + Application layer
// ------------------------------------------------------------------
builder.Services.AddDataAccess(builder.Configuration);
builder.Services.AddApplication();

// ------------------------------------------------------------------
// Azure Service Bus (client + publisher + provisioner)
// ------------------------------------------------------------------
builder.Services.AddMessaging(builder.Configuration);

// ------------------------------------------------------------------
// Consumers  (IHostedService — one processor per entity)
// ------------------------------------------------------------------
builder.Services.AddHostedService<JobLifecycleConsumer>();   // job-lifecycle / orchestrator-sync
builder.Services.AddHostedService<AuditLogConsumer>();       // job-lifecycle / audit-log
builder.Services.AddHostedService<JobRunnerConsumer>();      // job-execution-requests (session)
builder.Services.AddHostedService<JobExecutionResultConsumer>(); // job-execution-results (session)
builder.Services.AddHostedService<NotificationConsumer>();   // notifications

// ------------------------------------------------------------------
// Background services
// ------------------------------------------------------------------
builder.Services.AddHostedService<SchedulePollingService>(); // UC2.1a: 30s batch poll
builder.Services.AddHostedService<DlqMonitorService>();      // DLQ monitor

var host = builder.Build();
host.Run();
