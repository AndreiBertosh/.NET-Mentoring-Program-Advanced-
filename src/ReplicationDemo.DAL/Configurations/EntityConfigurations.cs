using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReplicationDemo.Domain.Entities;

namespace ReplicationDemo.DAL.Configurations;

public class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("Jobs");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
        builder.Property(j => j.Name).HasMaxLength(256).IsRequired();
        builder.Property(j => j.Frequency).HasMaxLength(50).IsRequired();
        builder.Property(j => j.ExecutionTime).IsRequired();
        builder.Property(j => j.ApiEndpoint).HasMaxLength(2048).IsRequired();
        builder.Property(j => j.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");

        builder.HasMany(j => j.Schedules).WithOne(s => s.Job).HasForeignKey(s => s.JobId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(j => j.Executions).WithOne(e => e.Job).HasForeignKey(e => e.JobId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class JobScheduleConfiguration : IEntityTypeConfiguration<JobSchedule>
{
    public void Configure(EntityTypeBuilder<JobSchedule> builder)
    {
        builder.ToTable("JobSchedules");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
        builder.Property(s => s.Status).HasMaxLength(50).HasDefaultValue("Pending");
        builder.Property(s => s.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasIndex(s => s.JobId);
        builder.HasIndex(s => s.NextRunTime).HasFilter("[Status] = 'Pending'");
    }
}

public class JobExecutionConfiguration : IEntityTypeConfiguration<JobExecution>
{
    public void Configure(EntityTypeBuilder<JobExecution> builder)
    {
        builder.ToTable("JobExecutions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
        builder.Property(e => e.Result).HasMaxLength(50).HasDefaultValue("Running");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasIndex(e => e.JobId);
    }
}
