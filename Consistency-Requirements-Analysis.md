# Consistency Requirements Analysis

**System:** Job Manager / Orchestrator  
**Database:** MS SQL Server (primary + replica via transactional replication)  
**Date:** 2026-06-15

---

## 1. Consistency Model Definitions

| Model | Definition |
|---|---|
| **Strong** | Every read reflects the most recent committed write. Reads are always served from the primary. |
| **ReadAfterWrite** | A user always sees the results of their own writes. Other users may see stale data. Reads after a user's own write are routed to the primary; all other reads can go to the replica. |
| **Eventual** | Reads may return stale data for a bounded lag period. Under the transactional replication configuration in this system, lag is typically sub-second. Reads are served from the replica for maximum throughput. |

> **Key principle:** The consistency model is a consequence of requirements — it should not be an arbitrary setting. Strong consistency is applied where data correctness and freshness are business-critical. Eventual consistency is applied where availability and read performance take priority. ReadAfterWrite consistency can be applied where a user must immediately see the results of their own writes, but global strong consistency is not required.

---

## 2. System Replication Characteristics

The system uses **SQL Server Transactional Replication** between a single primary (publisher + distributor, port 1435) and a single read replica (subscriber, port 1434).

- **Replication mode:** Push subscription, Log Reader Agent streams changes continuously.
- **Expected lag:** Sub-second under normal load; a few seconds under heavy write bursts.
- **Failure mode:** If the replica lags, reads may be stale until the Log Reader Agent catches up. Writes on the primary are never blocked by replica lag.

This means the window of inconsistency is bounded and typically imperceptible to end users. The analysis below determines, per use case, whether even that sub-second window is acceptable.

---

## 3. Use Case Consistency Analysis

---

### UC1.1 — Job Creation (`POST /api/jobs`)

**Sequence:** User submits job details → `JobWriteRepository` inserts into primary → API returns `201 Created` with the full job entity sourced from the primary write result → replication delivers the row to the replica asynchronously.

#### Is data consistency critical?

Yes, with nuance. The user expects to see their newly created job immediately after submission. However, the `POST` response already returns the complete created entity (read directly from the primary write path, before replication). The user receives all data they need in the HTTP response itself, so they do not need to re-query the replica to confirm the creation.

The critical risk is if the user's client (or a downstream service) immediately issues `GET /api/jobs/{id}` using the returned `id` and the replica has not yet received the row — resulting in a `404 Not Found` from the replica. Under sub-second replication lag this is unlikely in a normal UI interaction, but it is architecturally possible.

#### Is replication lag acceptable?

The lag is acceptable for the common case. A human user will not re-query within the same network round-trip. However, for automated clients or SPA frontends that immediately navigate to the detail view, the lag could produce a transient 404.

#### Required consistency model: **ReadAfterWrite**

The user must immediately see their own created job. Because the `POST` response already carries the full entity, the practical impact of the lag is low. For correctness, if the application routes a user's immediate `GET /api/jobs/{id}` call (following their own `POST`) to the primary, ReadAfterWrite is satisfied. Reads from other users or from a later session can safely go to the replica.

**Strong consistency is not required** — there is no business requirement that every client on the system sees the new job within milliseconds of creation.

---

### UC1.2 — Job Update (`PUT /api/jobs/{id}`)

**Sequence:** User submits updated job configuration → `JobWriteRepository` updates the primary → API returns `204 No Content` → replication propagates the change to the replica.

#### Is data consistency critical?

Moderately. A user who updates a job's `ApiEndpoint` or `Frequency` expects to see the new values if they view the job immediately after saving. Seeing the old values on a dashboard refresh can be confusing and erode trust, even though no data is lost.

There is also an operational risk: if the Orchestrator reads a `Pending` schedule and uses stale job configuration (e.g., old `ApiEndpoint`), it may call the wrong endpoint. However, job configuration changes do not affect already-scheduled `JobSchedule` rows — those are resolved at scheduling time, not at execution time — so this risk is contained.

#### Is replication lag acceptable?

The sub-second lag is acceptable for the configuration data (`Jobs` table) because:
- Users update job config rarely and accept minor UI delays.
- The Orchestrator resolves the `ApiEndpoint` at trigger time; if the replica is briefly stale, the next polling cycle will have the correct value.

#### Required consistency model: **ReadAfterWrite**

The updating user must see their own changes reflected immediately. Other users and the Orchestrator tolerate the sub-second lag. Strong consistency is not required.

---

### UC1.3 — Job Deletion (`DELETE /api/jobs/{id}`)

**Sequence:** User deletes a job → `JobWriteRepository` removes the `Jobs` row (cascade-deletes all `JobSchedules` and `JobExecutions` for that job) → API returns `204 No Content` → replication propagates the deletes to the replica.

#### Is data consistency critical?

Yes, more so than for creation or update. If the replica still shows a deleted job as `Pending` in `GetPendingSchedulesAsync`, the Orchestrator could attempt to trigger a job that no longer exists on the primary, resulting in a failed execution or a confusing error.

#### Is replication lag acceptable?

The sub-second lag is generally acceptable because:
- The Orchestrator's execution flow will fail gracefully if it attempts to use a deleted job (FK-protected foreign key lookups on primary will return null/error).
- The risk window is bounded to the replication lag duration.
- An immediate cascade of errors lasting sub-second is operationally tolerable.

However, if the system is under heavy load and replication lag grows, this risk increases. The design should ensure the Orchestrator handles "job not found" as a non-fatal, logged condition rather than a fatal error.

#### Required consistency model: **ReadAfterWrite** (for the deleting user's UI); **Eventual** (for the Orchestrator's schedule polling)

The deleting user should see the job removed from any list they navigate to. The Orchestrator's sub-second exposure to a ghost schedule entry is acceptable provided the execution layer is fault-tolerant to missing jobs.

---

### UC2.1a — Orchestrator: Pending Schedule Polling (`GET /api/jobs/schedules/pending`)

**Sequence:** The Orchestrator runs on an internal timer → calls `JobReadRepository.GetPendingSchedulesAsync()` → replica returns all `JobSchedule` rows where `Status = 'Pending' AND NextRunTime <= NOW()` → Orchestrator batches them for parallel execution.

#### Is data consistency critical?

Moderately. If the replica lags behind the primary by a fraction of a second, a newly created `Pending` schedule may not appear in this query. The Orchestrator would then miss this poll cycle and pick up the schedule on the next tick.

The business impact depends on polling frequency:
- If the Orchestrator polls every 5–30 seconds (typical), a sub-second replication lag has zero observable effect on execution timing.
- Jobs are defined with coarse-grained frequencies (Hourly, Daily, Weekly, Monthly). A one-second slip in the trigger time is well within acceptable tolerance.

No data is lost if a schedule is missed for one poll cycle — it remains `Pending` and will be picked up on the next iteration.

#### Is replication lag acceptable?

**Yes.** The polling interval (seconds to minutes) vastly exceeds the replication lag (sub-second). Missing one poll cycle does not violate any business SLA for the defined frequency granularities.

#### Required consistency model: **Eventual**

The Orchestrator can tolerate stale reads of up to the polling interval. Reading from the replica maximises read throughput and is the correct choice here.

---

### UC2.1b — Orchestrator: Job Execution (Write Path)

**Sequence:** Orchestrator locks the job schedule, triggers execution via message queue → `Job Runner` executes the integration → runner reports status back → `JobWriteRepository.CreateExecutionAsync()` inserts a `JobExecution` row on the primary → `JobWriteRepository` updates the `JobSchedule` status on the primary.

#### Is data consistency critical?

Yes — for the **write side**. The execution record must be durably written to the primary before the Orchestrator considers the job done. Loss of an execution record would corrupt the audit trail and break idempotency checks.

There is no read-consistency concern here because the write path goes exclusively to the primary. The replica receives the execution record asynchronously and serves it to monitoring dashboards later.

#### Is replication lag acceptable?

The lag in making execution results visible to read queries (dashboards, history views) is fully acceptable. The write itself is synchronous to the primary.

#### Required consistency model: **Strong** (for the primary write); **Eventual** (for subsequent reads of the execution record from the replica)

---

### UC2.2 — User/Admin: Job Execution History (`GET /api/jobs/{id}/executions`)

**Sequence:** User navigates to a job's history page → `JobReadRepository.GetExecutionsByJobIdAsync()` queries the replica → returns a list of `JobExecution` rows ordered by `StartedAt DESC`.

#### Is data consistency critical?

No. This is a monitoring and audit view. A user reviewing execution history is interested in the recent trend, not the exact millisecond of the last record. Seeing a history that is 1–2 seconds stale is imperceptible in a dashboard context.

The only scenario where this matters is if a user triggers a manual run and immediately checks the history — but even then, the execution takes measurable time (the job must complete before a result record exists), so the replica will have caught up long before the execution completes.

#### Is replication lag acceptable?

**Yes.** The sub-second lag is negligible relative to the granularity of job execution times (seconds to minutes).

#### Required consistency model: **Eventual**

---

### UC2.3 — Job Catalog Browsing (`GET /api/jobs` and `GET /api/jobs/{id}`)

**Sequence:** User (or admin dashboard) requests the list of all jobs or a single job's detail — `JobReadRepository.GetAllAsync()` / `GetByIdAsync()` queries the replica.

#### Is data consistency critical?

No. Job catalog data (`Jobs` table) changes infrequently (tens of rows per month). A user browsing the list will not notice if the data is sub-second stale. The catalog is configuration data, not operational state.

The exception is a user who just created or updated a job (UC1.1 / UC1.2) and immediately navigates to the list or detail view. In that scenario, ReadAfterWrite semantics apply (see UC1.1 and UC1.2 above).

#### Is replication lag acceptable?

**Yes**, for general browsing. For a user's own post-write navigation, a ReadAfterWrite routing strategy is appropriate.

#### Required consistency model: **Eventual** (general browsing); **ReadAfterWrite** (user's own post-write navigation)

---

## 4. Summary Table

| Use Case | Endpoint | Operation | Consistency Required | Lag Acceptable? | Rationale |
|---|---|---|---|---|---|
| UC1.1 — Job Creation | `POST /api/jobs` | Write (primary) | **ReadAfterWrite** | Yes (sub-second) | User sees their own job via `201` response; subsequent GET by same user should hit primary |
| UC1.2 — Job Update | `PUT /api/jobs/{id}` | Write (primary) | **ReadAfterWrite** | Yes (sub-second) | Updating user must see new values; other users tolerate lag |
| UC1.3 — Job Deletion | `DELETE /api/jobs/{id}` | Write (primary) | **ReadAfterWrite** (UI) / **Eventual** (Orchestrator) | Yes, with fault-tolerance | Ghost schedule visible to Orchestrator for sub-second only; must handle gracefully |
| UC2.1a — Pending Schedule Polling | `GET /api/jobs/schedules/pending` | Read (replica) | **Eventual** | Yes | Polling interval >> replication lag; missed cycle has no business impact |
| UC2.1b — Execution Record Write | `POST` (internal) | Write (primary) | **Strong** (write) / **Eventual** (subsequent reads) | N/A (write is synchronous) | Execution audit trail must be durable on primary |
| UC2.2 — Execution History | `GET /api/jobs/{id}/executions` | Read (replica) | **Eventual** | Yes | Monitoring dashboard; sub-second lag imperceptible |
| UC2.3 — Catalog Browsing | `GET /api/jobs` / `GET /api/jobs/{id}` | Read (replica) | **Eventual** / **ReadAfterWrite** (own writes) | Yes | Infrequent config data; stale list is invisible to end users |

---

## 5. Implications for DAL Design

### 5.1 Current Architecture

The current DAL hard-routes all reads to the replica and all writes to the primary with no session-level awareness:

```
POST  /api/jobs  →  JobWriteRepository  →  PrimaryDbContext   (correct)
GET   /api/jobs  →  JobReadRepository   →  ReadOnlyDbContext  (correct for general reads)
GET   /api/jobs/{id}  →  JobReadRepository  →  ReadOnlyDbContext  (potentially stale for own writes)
```

This is correct for **Eventual** consistency use cases (UC2.1a, UC2.2, UC2.3 general browsing) and is a sound default.

### 5.2 ReadAfterWrite Gap

For UC1.1, UC1.2, and UC1.3, the current architecture does not guarantee ReadAfterWrite consistency. The `CreatedAtAction` URL returned in the `201 Created` response points to `GET /api/jobs/{id}`, which reads from the replica. A fast automated client could observe a transient `404`.

**Mitigation options (in order of complexity):**

1. **Application-level caching / response reuse:** The `POST` response already contains the full entity. The client should use this data for the immediate display rather than re-fetching. This is the cheapest option and resolves the practical concern.

2. **Primary read fallback for own writes:** After a write, route the next read for that resource to the primary by passing a flag or header. Requires a context-aware repository that knows whether the current request follows a write by the same user.

3. **Synchronous replication read:** After a write, wait for an explicit replication confirmation before returning `201`. This eliminates the lag window but adds latency to every `POST` and negates the purpose of the replica.

**Recommended approach:** Option 1 for UC1.1 (already partially satisfied by returning the entity in the `201` body). For UC1.2 and UC1.3, clients should treat a `204 No Content` as confirmation and update local state without re-fetching.

### 5.3 Orchestrator Fault Tolerance Requirement

UC1.3 identifies a window where a deleted job's `Pending` schedule may still appear in the replica's `GetPendingSchedulesAsync` result. The Orchestrator **must** handle a "job not found" condition on the primary as a non-fatal, idempotent outcome — log the event, skip the schedule, and continue. This is an application-level guard, not a database-level consistency guarantee.

### 5.4 No Strong Consistency Requirement on Reads

No read use case in the current system requires **Strong** consistency (i.e., always reading from the primary for queries). Routing all reads to the replica is the correct default. Strong consistency is only applied to writes, which by definition go to the primary.
