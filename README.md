# ReplicationDemo — SQL Server Replication-Aware DAL

A minimal demo project demonstrating **MS SQL Server transactional replication** with a **read/write splitting Data Access Layer** using .NET 8, Entity Framework Core, and Docker Compose.

## Architecture Overview

```
┌──────────────┐         ┌──────────────────┐
│   API Layer  │         │   Domain Layer   │
│ (Controllers)├────────►│  (Entities,      │
│              │         │   Repositories)  │
└──────┬───────┘         └────────▲─────────┘
       │                          │
       ▼                          │
┌──────────────────────────────────┐
│          DAL Layer               │
│  ┌─────────────┐  ┌────────────┐ │
│  │ WriteRepo   │  │ ReadRepo   │ │
│  │ (Primary)   │  │ (Replica)  │ │
│  └──────┬──────┘  └─────┬──────┘ │
└─────────┼───────────────┼────────┘
          │               │
          ▼               ▼
   ┌────────────┐  ┌────────────┐
   │  Primary   │  │  Replica   │
   │ mssql:1435 │─►│ mssql:1434 │
   │  (R/W)     │  │  (R/O)     │
   └────────────┘  └────────────┘
     Transactional Replication
```

## Replication Strategy

This demo uses **SQL Server Transactional Replication**:

| Role | Container | Port | Purpose |
|------|-----------|------|---------|
| **Publisher + Distributor** | `mssql-primary` | 1435 | All writes go here |
| **Subscriber** | `mssql-replica` | 1434 | Read-only queries served from here |

**How it works:**
1. The **primary** server is configured as both Publisher and Distributor
2. A **publication** (`JobManagerPublication`) is created containing all three tables
3. A **push subscription** delivers changes to the replica in near real-time
4. The Snapshot Agent generates an initial snapshot; the Log Reader Agent then streams ongoing changes

**Replication lag:** Typically sub-second for transactional replication under low load.

## Read/Write Splitting

The DAL enforces read/write separation at the architecture level:

| Operation | Repository | DbContext | Target |
|-----------|------------|-----------|--------|
| SELECT queries | `JobReadRepository` | `ReadOnlyDbContext` | Replica (port 1434) |
| INSERT/UPDATE/DELETE | `JobWriteRepository` | `PrimaryDbContext` | Primary (port 1435) |

**Key design decisions:**
- `ReadOnlyDbContext` uses `AsNoTracking()` for all queries (better performance)
- `ReadOnlyDbContext` throws on `SaveChanges()` to prevent accidental writes
- `IReadDbContext` exposes `IQueryable<T>` (no `DbSet<T>`) to prevent mutation
- `IWriteDbContext` exposes `DbSet<T>` with `SaveChangesAsync`

## How to Run

### Prerequisites
- Docker Desktop
- .NET 8 SDK

### 1. Start the database containers

```bash
docker compose up -d
```

Wait ~60 seconds for replication setup to complete. Check logs:

```bash
docker logs replication-setup -f
```

### 2. Run the API

```bash
cd src/ReplicationDemo.Api
dotnet run
```

### 3. Test the endpoints

```bash
# List all jobs (reads from REPLICA)
curl http://localhost:5000/api/jobs

# Create a job (writes to PRIMARY)
curl -X POST http://localhost:5000/api/jobs \
  -H "Content-Type: application/json" \
  -d '{"name":"Test Job","frequency":"Daily","executionTime":"08:00:00","apiEndpoint":"https://example.com/test"}'

# Get pending schedules (reads from REPLICA)
curl http://localhost:5000/api/jobs/schedules/pending
```

### 4. Verify replication

```bash
# Write to primary
docker exec mssql-primary /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "Str0ng!Passw0rd" \
  -Q "USE JobManager; INSERT INTO Jobs (Name, Frequency, ExecutionTime, ApiEndpoint) VALUES ('Replicated Job', 'Daily', '12:00', 'https://test.com/api');"

# Read from replica (after ~1 second)
docker exec mssql-replica /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "Str0ng!Passw0rd" \
  -Q "USE JobManager; SELECT Name FROM Jobs;"
```

## Project Structure

```
├── docker-compose.yml              # Primary + Replica + Setup containers
├── db/
│   ├── init/
│   │   ├── 01-schema.sql           # Database schema
│   │   └── 02-seed.sql             # Seed data
│   ├── replication/
│   │   └── setup-replication.sql   # Replication configuration
│   └── setup-all.sh                # Orchestration script
├── src/
│   ├── ReplicationDemo.Domain/     # Entities & repository interfaces
│   ├── ReplicationDemo.DAL/        # EF Core contexts & repositories
│   └── ReplicationDemo.Api/        # ASP.NET Core Web API
└── README.md
```

## Use Cases Implemented

### UC1.1 — Job Creation
- User submits job details via `POST /api/jobs`
- Write goes to **PrimaryDbContext** → `mssql-primary`
- Replication delivers the data to `mssql-replica`

### UC2.1 — Job Scheduling & Execution Lookup
- Pending schedules queried via `GET /api/jobs/schedules/pending`
- Execution history queried via `GET /api/jobs/{id}/executions`
- All reads go to **ReadOnlyDbContext** → `mssql-replica`

## Cleanup

```bash
docker compose down -v
```
