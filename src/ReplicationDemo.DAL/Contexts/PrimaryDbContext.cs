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
        modelBuilder.Entity<Job>().HasQueryFilter(j => j.Name != CanaryName);
    }
}
