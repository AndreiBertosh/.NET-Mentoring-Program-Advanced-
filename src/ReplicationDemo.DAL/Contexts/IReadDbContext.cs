using Microsoft.EntityFrameworkCore;
using ReplicationDemo.Domain.Entities;

namespace ReplicationDemo.DAL.Contexts;

public interface IReadDbContext
{
    IQueryable<Job> Jobs { get; }
    IQueryable<JobSchedule> JobSchedules { get; }
    IQueryable<JobExecution> JobExecutions { get; }
}
