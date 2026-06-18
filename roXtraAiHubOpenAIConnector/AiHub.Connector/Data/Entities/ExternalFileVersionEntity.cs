namespace AiHub.Connector.Data.Entities;

public class ExternalFileVersionEntity
{
	public int Id { get; set; }
	public string RoxFileId { get; set; } = string.Empty;
	public string DocumentHash { get; set; } = string.Empty;
	public string Filename { get; set; } = string.Empty;
	public string? ExternalItemId { get; set; }
	public ExternalFileVersionStatus Status { get; set; } = ExternalFileVersionStatus.Uploading;
	public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
	public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
