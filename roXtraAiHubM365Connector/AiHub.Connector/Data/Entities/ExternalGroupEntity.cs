namespace AiHub.Connector.Data.Entities;

public class ExternalGroupEntity
{
	public int Id { get; set; }

	// Knowledge pool identifier from event
	public string KnowledgePoolId { get; set; } = string.Empty;

	// External group id in M365
	public string ExternalGroupId { get; set; } = string.Empty;

	public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
