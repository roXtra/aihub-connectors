using System.IO;

namespace AiHub.Connector.Roxtra;

public record RoxFile(string Id, string Title)
{
	public Stream? ContentStream { get; init; }
}
