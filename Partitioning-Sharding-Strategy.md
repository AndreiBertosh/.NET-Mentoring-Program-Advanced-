# Partitioning / Sharding Strategy

**System:** Job Manager / Orchestrator  
**Database:** MS SQL Server (primary + replica via transactional replication)  
**Date:** 2026-06-11

---

## 1. Data Growth and Access Pattern Analysis

### 1.1 Schema Overview

The database contains three tables with fundamentally different growth characteristics:

| Table | Nature | Estimated Row Count |
|---|---|---|
| `Jobs` | Reference / configuration data | Low (hundreds–thousands) |
| `JobSchedules` | Rolling scheduling state | Moderate (bounded by active jobs) |
| `JobExecutions` | Append-only execution history | High (unbounded, time-series) |

---

### 1.2 Expected Data Growth Rate

#### `Jobs`

Jobs are created by users and are relatively static. The table holds job *definitions*, not execution history.

- **Growth rate:** ~10–50 new jobs per month in a typical enterprise deployment.
- **Ceiling:** A few thousand rows total. This table is effectively bounded.
- **Conclusion:** No partitioning needed; the table will remain small.

#### `JobSchedules`

Each job maintains one *active* `Pending` schedule entry at a time. When a job runs, its schedule row is updated or replaced with the next run time.

- **Active rows:** Approximately equal to the number of active jobs (~1,000–10,000 rows).
- **Churn rate:** One insert + one status update per job execution.
- **Conclusion:** This table stays bounded and small. Completed schedule entries can be archived on a rolling basis if desired, but no partitioning is required.

#### `JobExecutions` (primary growth driver)

This is the high-volume, append-only table. Every triggered job creates one `JobExecution` row.

**Estimation model (1,000 active jobs, typical frequency mix):**

| Frequency | Job count | Executions / day |
|---|---|---|
| Hourly | 300 | 7,200 |
| Daily | 500 | 500 |
| Weekly | 150 | ~21 |
| Monthly | 50 | ~2 |
| **Total** | **1,000** | **~7,723 / day** |

| Time horizon | Approximate row count |
|---|---|
| Per day | ~7,700 |
| Per month | ~230,000 |
| Per year | ~2,800,000 |
| 5 years | ~14,000,000 |

**At scale (10,000 active jobs):** multiply by 10 → ~77,000 rows/day, ~2.3 million/month.

---

### 1.3 Query Patterns

The existing repository layer reveals the following access patterns:

| Query | Table | Filter / Order |
|---|---|---|
| List all jobs | `Jobs` | `ORDER BY Name` |
| Get job by ID (with schedules & executions) | `Jobs`, `JobSchedules`, `JobExecutions` | `WHERE Id = @id` |
| Get due schedules | `JobSchedules` | `WHERE Status = 'Pending' AND NextRunTime <= NOW()` `ORDER BY NextRunTime` |
| Get executions for a job | `JobExecutions` | `WHERE JobId = @id` `ORDER BY StartedAt DESC` |

**Key observations:**

- **`JobExecutions`** is queried almost exclusively by `JobId` + recency (`StartedAt DESC`). There is no cross-job time-range query in the current codebase.
- **`JobSchedules`** is queried by time window (`NextRunTime <= NOW()`), making time a natural filter key.
- **`Jobs`** queries are trivial lookups or full scans (bounded data set).
- Write path: inserts to `JobExecutions` are high-frequency and sequential by `CreatedAt` / `StartedAt`.
- There is no multi-tenant dimension (no `TenantId`, `Region`, or `CustomerId`) in the schema.

---

## 2. Partitioning vs. Sharding Decision

### 2.1 Decision: Table Partitioning on a Single Database Instance

**Recommendation: Use table-level partitioning (not sharding).**

#### Rationale

| Criterion | Assessment |
|---|---|
| **Data volume** | ~2.8 M rows/year at current scale — well within what a single SQL Server instance handles. Sharding adds operational complexity without capacity benefit at this scale. |
| **Query isolation** | All queries are scoped to a single `JobId` or a short time window. There is no natural dimension that would require routing queries across multiple shards. |
| **Referential integrity** | `JobExecutions` and `JobSchedules` hold `FOREIGN KEY` constraints to `Jobs`. Sharding across instances would require dropping FK constraints and managing consistency in the application layer — unnecessary complexity. |
| **Existing replication** | The system already has a primary/replica split for read scaling. Horizontal read capacity is already addressed. |
| **Operational cost** | Sharding requires a routing layer, distributed transactions, cross-shard aggregation, and schema management across multiple servers. These costs are not justified here. |

**When to reconsider sharding:** If active jobs grow beyond ~100,000 with predominantly sub-minute frequency intervals, producing >500 million execution records per year, a move to sharding (e.g., by `JobId` hash range) should be re-evaluated.

---

### 2.2 Number of Shards

Not applicable — sharding is not recommended for the current scale.

If sharding becomes necessary in the future, a starting point of **4 shards** would be reasonable, allowing doubling to 8 without key redistribution (using a consistent hash ring).

---

### 2.3 Partitioning Strategy: Range-Based by Time

**Chosen strategy: Range partitioning on `StartedAt` (monthly intervals) for `JobExecutions`.**

#### Why range-based?

- `JobExecutions` is a classic time-series table: rows are inserted in temporal order and queried by recency.
- Range partitioning on time aligns partition boundaries with natural data lifecycle (archive/delete old partitions by month).
- SQL Server's partition elimination automatically limits scans to relevant month partitions when a time filter is applied.
- Operational tasks (backup, archive, purge) can target individual partitions without table-wide locks.

#### Why not hash-based?

- Hash partitioning distributes rows evenly across partitions by `JobId`, but it destroys temporal locality.
- Queries filtering by `StartedAt` (date range) would span all partitions — eliminating the main benefit.
- Archival and purging by age becomes expensive (requires row-by-row deletion, not partition switching).

#### Why not list-based?

- There is no finite enumerable dimension (e.g., region codes, status values) with high cardinality that maps naturally to execution records.

---

### 2.4 Partition / Shard Key

**Primary partition key: `StartedAt` (DATETIME2) on `JobExecutions`**

#### Justification

| Criterion | `StartedAt` |
|---|---|
| **Write pattern** | Rows are inserted with `StartedAt` set to the current time → all new inserts land in the latest (hottest) partition, which is the expected behaviour for a sliding-window range partition scheme. |
| **Read pattern** | Recent executions are the most queried; the current-month partition stays in the buffer pool. Historical partitions are cold and can be moved to cheaper storage tiers. |
| **Archival** | Monthly partitions older than the retention threshold can be switched out to an archive table and dropped in O(1) time (metadata operation). |
| **Cardinality** | Continuous, monotonically increasing — ideal for range partitioning. |

**Secondary index consideration:** Because the dominant read query is `WHERE JobId = @id ORDER BY StartedAt DESC`, a composite index `(JobId, StartedAt DESC)` should be placed on each partition to support efficient per-job lookups after partition elimination.

---

## 3. Partition Implementation Sketch (SQL Server)

```sql
-- 1. Partition function: one range boundary per month
CREATE PARTITION FUNCTION PF_JobExecutions_ByMonth (DATETIME2)
AS RANGE RIGHT FOR VALUES (
    '2025-01-01', '2025-02-01', '2025-03-01',
    '2025-04-01', '2025-05-01', '2025-06-01',
    '2025-07-01', '2025-08-01', '2025-09-01',
    '2025-10-01', '2025-11-01', '2025-12-01',
    '2026-01-01'  -- add a new boundary each month via ALTER PARTITION FUNCTION ... SPLIT RANGE
);

-- 2. Partition scheme: map all partitions to the PRIMARY filegroup
--    (in production, map cold partitions to a secondary filegroup on cheaper storage)
CREATE PARTITION SCHEME PS_JobExecutions_ByMonth
AS PARTITION PF_JobExecutions_ByMonth
ALL TO ([PRIMARY]);

-- 3. Partitioned table (replaces the existing JobExecutions table)
CREATE TABLE JobExecutions (
    Id          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
    JobId       UNIQUEIDENTIFIER NOT NULL,
    StartedAt   DATETIME2        NOT NULL,
    FinishedAt  DATETIME2        NULL,
    Result      NVARCHAR(50)     NOT NULL DEFAULT 'Running',
    CreatedAt   DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_JobExecutions PRIMARY KEY CLUSTERED (Id, StartedAt) -- StartedAt must be part of PK for partitioned tables
) ON PS_JobExecutions_ByMonth (StartedAt);

-- 4. Per-job lookup index aligned to the partition scheme
CREATE NONCLUSTERED INDEX IX_JobExecutions_JobId_StartedAt
ON JobExecutions (JobId, StartedAt DESC)
ON PS_JobExecutions_ByMonth (StartedAt);

-- 5. Monthly maintenance: add next month's boundary before it arrives
-- ALTER PARTITION FUNCTION PF_JobExecutions_ByMonth() SPLIT RANGE ('2026-07-01');

-- 6. Archive and drop an old partition (sliding window)
-- ALTER TABLE JobExecutions SWITCH PARTITION 1 TO JobExecutions_Archive PARTITION 1;
-- ALTER PARTITION FUNCTION PF_JobExecutions_ByMonth() MERGE RANGE ('2025-01-01');
```

---

## 4. Summary

| Decision | Choice | Rationale |
|---|---|---|
| **Partitioning vs. Sharding** | **Table partitioning** (single instance) | Data volume does not justify multi-instance sharding; FK constraints and operational simplicity favour a single instance |
| **Number of shards** | N/A (revisit at >100K jobs) | — |
| **Partitioning strategy** | **Range-based** | Time-series write and read patterns; enables efficient archival |
| **Partition key** | **`StartedAt`** on `JobExecutions` | Monotonically increasing writes; most recent data queried most; supports clean monthly archival |
| **Other tables** | No partitioning | `Jobs` and `JobSchedules` remain small and bounded |
