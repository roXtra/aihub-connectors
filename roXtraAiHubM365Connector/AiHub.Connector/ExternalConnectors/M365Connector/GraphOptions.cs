using System.ComponentModel.DataAnnotations;

namespace AiHub.Connector.ExternalConnectors.M365Connector;

public class GraphOptions
{
	public const string SectionName = "Graph";

	[Required]
	public string TenantId { get; set; } = string.Empty;

	[Required]
	public string ClientId { get; set; } = string.Empty;

	[Required]
	public string ClientSecret { get; set; } = string.Empty;

	[Required]
	public string ExternalConnectionId { get; set; } = string.Empty;
}
