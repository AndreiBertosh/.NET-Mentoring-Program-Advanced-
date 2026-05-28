namespace ReplicationDemo.Domain.Entities;

public class JobSchedule
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public DateTime NextRunTime { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTime CreatedAt { get; set; }

    public Job Job { get; set; } = null!;
}
