namespace ReplicationDemo.Domain.Entities;

public class Job
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Frequency { get; set; } = string.Empty;
    public TimeSpan ExecutionTime { get; set; }
    public string ApiEndpoint { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ICollection<JobSchedule> Schedules { get; set; } = [];
    public ICollection<JobExecution> Executions { get; set; } = [];
}
