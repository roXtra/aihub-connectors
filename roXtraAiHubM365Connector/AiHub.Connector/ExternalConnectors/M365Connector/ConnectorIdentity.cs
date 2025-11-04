namespace AiHub.Connector.ExternalConnectors.M365Connector;

using Microsoft.Graph.Models.ExternalConnectors;

public class ConnectorIdentity
{
	public string Id { get; set; } = string.Empty;
	public IdentityType Type { get; set; } = IdentityType.Group;
	public string? IdentitySource { get; set; }
}
