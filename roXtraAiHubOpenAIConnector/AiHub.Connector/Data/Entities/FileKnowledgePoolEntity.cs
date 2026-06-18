namespace AiHub.Connector.Data.Entities;

public class FileKnowledgePoolEntity
{
	public int Id { get; set; }

	// Roxtra file identifier
	public string RoxFileId { get; set; } = string.Empty;

	// Knowledge pool identifier
	public string KnowledgePoolId { get; set; } = string.Empty;

	public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
