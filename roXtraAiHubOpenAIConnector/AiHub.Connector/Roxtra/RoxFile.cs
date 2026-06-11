using System.IO;

namespace AiHub.Connector.Roxtra;

public record RoxFile(string Id, string Title)
{
	public Stream? ContentStream { get; init; }

	// Content hash from roXtra, unique per file version.
	public string DocumentHash { get; init; } = string.Empty;
}
