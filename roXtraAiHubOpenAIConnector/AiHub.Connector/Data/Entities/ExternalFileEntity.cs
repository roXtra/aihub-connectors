namespace AiHub.Connector.Data.Entities;

public class ExternalFileEntity
{
	public int Id { get; set; }

	// Roxtra file identifier
	public string RoxFileId { get; set; } = string.Empty;

	// External (M365/Graph) item id
	public string ExternalItemId { get; set; } = string.Empty;

	// Content hash from roXtra, unique per file version.
	public string DocumentHash { get; set; } = string.Empty;

	public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
