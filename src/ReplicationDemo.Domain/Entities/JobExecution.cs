namespace ReplicationDemo.Domain.Entities;

public class JobExecution
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string Result { get; set; } = "Running";
    public DateTime CreatedAt { get; set; }

    public Job Job { get; set; } = null!;
}
