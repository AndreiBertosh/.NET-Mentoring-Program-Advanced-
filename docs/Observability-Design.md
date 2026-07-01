# Observability Design — Job Runner (ReplicationDemo.Worker)

Monitoring stack: **Azure Application Insights** (Azure Monitor).
Scope: the **Job Runner** — `JobRunnerConsumer` in `ReplicationDemo.Worker`, which consumes
`job-execution-requests` and executes jobs. API gateway, databases, and the orchestrator
polling loop are out of scope.

NFRs addressed: Reliability, Operability, Monitoring & Alerting, Time-to-detect / Time-to-recover.

---

## 1. Instrumentation

| Concern | Implementation |
|---|---|
| SDK | `Microsoft.ApplicationInsights.WorkerService` (registered in [Program.cs](../src/ReplicationDemo.Worker/Program.cs)) |
| Connection | `ApplicationInsights:ConnectionString` in [appsettings.json](../src/ReplicationDemo.Worker/appsettings.json) |
| Role identity | [RoleNameInitializer.cs](../src/ReplicationDemo.Worker/Services/RoleNameInitializer.cs) stamps `cloud_RoleName = ReplicationDemo.Worker` |
| Custom metrics | [JobRunnerMetrics.cs](../src/ReplicationDemo.Worker/Services/JobRunnerMetrics.cs) via `TelemetryClient.GetMetric(...)` (server-side pre-aggregation) |
| Recording point | [JobRunnerConsumer.cs](../src/ReplicationDemo.Worker/Consumers/JobRunnerConsumer.cs) — `RecordExecution(status, durationMs)` on success **and** failure |
| CPU / Memory | Auto-collected (`performanceCounters`: `% Processor Time`, working-set) by the SDK |

### High-load / scalability compliance

- **Metric cardinality** — only one bounded dimension, `Status` (`Succeeded` / `Failed`). No `JobId`, `ScheduleId`, or `UserId` is ever used as a dimension.
- **Efficient aggregation** — `GetMetric()` pre-aggregates in-process and emits one point per series per 60s, so ingestion volume is independent of throughput.
- **Alert stability** — alerts read stable counters; only the dashboard does finer breakdowns.
- **Telemetry resilience** — `RecordExecution` is wrapped in try/catch and the AI channel is async/buffered; telemetry can never block or fail job execution.

---

## 2. Custom Metrics (≥ 2 required — 4 implemented)

| Metric | Type | Dimension | Meaning |
|---|---|---|---|
| `LongRunningTasksCount` | Counter | — | Tasks whose duration ≥ `JobRunnerMetrics:LongRunningThresholdMs` (default 2000 ms; set 30000/60000 in prod) |
| `FailedTasksCount` | Counter | — | Tasks that ended in error / were not completed |
| `TasksProcessedCount` | Counter | `Status` | Throughput source → **TasksProcessedPerHour** via `sum() by bin(1h)` |
| `JobExecutionDurationMs` | Measurement | `Status` | Duration distribution → p50/p95/p99 |

> `TasksProcessedPerHour` is derived on the dashboard (`sum(valueSum) by bin(timestamp, 1h)`)
> rather than emitted as a pre-divided rate, keeping the raw counter reusable.

---

## 3. Dashboard

Deployable workbook: [infra/observability/workbook.bicep](../infra/observability/workbook.bicep). Tiles:

1. **Tasks Processed Per Hour** — throughput trend.
2. **Failed Tasks & Failure Rate (%)** — failures over total.
3. **Long-Running Tasks** — count over threshold.
4. **Execution Duration p50 / p95 / p99**.
5. **CPU & Memory**.

Key KQL (also embedded in the workbook):

```kusto
// TasksProcessedPerHour
customMetrics
| where name == "TasksProcessedCount" and cloud_RoleName == "ReplicationDemo.Worker"
| summarize TasksPerHour = sum(valueSum) by bin(timestamp, 1h)
| render timechart
```

```kusto
// p95 / p99 execution duration
customMetrics
| where name == "JobExecutionDurationMs" and cloud_RoleName == "ReplicationDemo.Worker"
| summarize p95 = percentile(todouble(valueMax), 95),
            p99 = percentile(todouble(valueMax), 99) by bin(timestamp, 15m)
| render timechart
```

---

## 4. Alerts

Deployable: [infra/observability/observability.bicep](../infra/observability/observability.bicep) (action group + 6 scheduled-query rules).
Notification channel: action group `ag-jobrunner-oncall` — **email** (+ optional **Teams/Slack** webhook).

| # | Alert | Threshold | Window / Freq | Severity | Channel | First check (op-hint) |
|---|---|---|---|---|---|---|
| 1 | CPU high | **≥ 70%** | 15 m / 5 m | Sev 2 | Email/Teams | Queue backlog & `MaxConcurrentCalls`; scale out; rule out retry storm |
| 2 | Memory high | ≥ 85% | 15 m / 5 m | Sev 2 | Email/Teams | Memory growth/leak, large batches; check GC heap counter; scale up |
| 3 | Execution burst | > 5000 / 15 m | 15 m / 5 m | Sev 3 | Email | Upstream scheduler / poison-retry loop; Service Bus delivery counts |
| 4 | p95 duration | > 5000 ms | 15 m / 5 m | Sev 2 | Email/Teams | Downstream API latency, replica lag, throttling; check long-running tile |
| 5 | **Failed tasks** | **> 10 count OR > 20% rate** | 15 m / 5 m | **Sev 1** | Email/Teams | DLQ size & dead-letter reason; downstream errors; recent deploy; `exceptions` table |
| 6 | Long-running tasks | > 20 / 15 m | 15 m / 5 m | Sev 2 | Email/Teams | Dependency slowness, lock contention, large payloads; review p95/p99 |

All thresholds, windows, email, and webhook are Bicep parameters. `autoMitigate` is on so alerts
auto-resolve when the metric returns to normal (reduces noise, improves operability).

Required by the task: alert **#1 CPU ≥ 70%** and alert **#5 failed-tasks** (absolute count **and** failure rate).

---

## 5. Deploy

```powershell
cd infra/observability
./deploy.ps1 -ResourceGroup <rg> -AppInsightsName <ai-name> -AlertEmail you@example.com `
             -TeamsWebhookUrl "<optional-incoming-webhook>"
```

The Worker emits telemetry automatically once it runs with the configured connection string;
metrics appear in `customMetrics` within ~2–3 minutes (60 s aggregation + ingestion latency).
