using Microsoft.EntityFrameworkCore;
using ReplicationDemo.Domain.Entities;

namespace ReplicationDemo.DAL.Contexts;

public interface IWriteDbContext
{
    DbSet<Job> Jobs { get; }
    DbSet<JobSchedule> JobSchedules { get; }
    DbSet<JobExecution> JobExecutions { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
