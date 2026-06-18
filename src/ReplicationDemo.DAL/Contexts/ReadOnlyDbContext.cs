using Microsoft.EntityFrameworkCore;
using ReplicationDemo.Domain.Entities;

namespace ReplicationDemo.DAL.Contexts;

public class ReadOnlyDbContext : DbContext, IReadDbContext
{
    public ReadOnlyDbContext(DbContextOptions<ReadOnlyDbContext> options) : base(options) { }

    IQueryable<Job> IReadDbContext.Jobs => Set<Job>().AsNoTracking();
    IQueryable<JobSchedule> IReadDbContext.JobSchedules => Set<JobSchedule>().AsNoTracking();
    IQueryable<JobExecution> IReadDbContext.JobExecutions => Set<JobExecution>().AsNoTracking();

    private const string CanaryName = "__replication_canary__";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReadOnlyDbContext).Assembly);
        // Matching filters on dependents suppress EF Core warning EF1622.
        modelBuilder.Entity<Job>().HasQueryFilter(j => j.Name != CanaryName);
        modelBuilder.Entity<JobSchedule>().HasQueryFilter(s => s.Job.Name != CanaryName);
        modelBuilder.Entity<JobExecution>().HasQueryFilter(e => e.Job.Name != CanaryName);
    }

    public override int SaveChanges() =>
        throw new InvalidOperationException("Read-only context does not support write operations.");

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Read-only context does not support write operations.");
}
