using Microsoft.EntityFrameworkCore;
using ReplicationDemo.Domain.Entities;

namespace ReplicationDemo.DAL.Contexts;

public class PrimaryDbContext : DbContext, IWriteDbContext
{
    public PrimaryDbContext(DbContextOptions<PrimaryDbContext> options) : base(options) { }

    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobSchedule> JobSchedules => Set<JobSchedule>();
    public DbSet<JobExecution> JobExecutions => Set<JobExecution>();

    private const string CanaryName = "__replication_canary__";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PrimaryDbContext).Assembly);
        // Hide the replication canary row from all application queries.
        // Matching filters on dependents suppress EF Core warning EF1622 —
        // the canary never has child rows so the filter is effectively a no-op there.
        modelBuilder.Entity<Job>().HasQueryFilter(j => j.Name != CanaryName);
        modelBuilder.Entity<JobSchedule>().HasQueryFilter(s => s.Job.Name != CanaryName);
        modelBuilder.Entity<JobExecution>().HasQueryFilter(e => e.Job.Name != CanaryName);
    }
}
