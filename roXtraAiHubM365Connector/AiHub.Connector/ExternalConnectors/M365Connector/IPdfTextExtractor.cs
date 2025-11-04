using System.IO;

namespace AiHub.Connector.ExternalConnectors.M365Connector;

public interface IPdfTextExtractor
{
	Task<string> ExtractAsync(Stream pdfStream, CancellationToken ct);
}
