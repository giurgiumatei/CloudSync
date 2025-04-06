namespace CloudSync.Data.Entities;

public class DataEntity
{
    public int Id { get; set; }
    public string Data { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
