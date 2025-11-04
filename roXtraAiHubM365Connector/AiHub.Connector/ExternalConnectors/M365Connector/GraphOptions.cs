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

	/// <summary>
	/// Enables the workaround for a Microsoft Graph SDK bug that prevents adding Azure AD groups
	/// as members of external groups. When enabled, the connector avoids SDK member operations
	/// and grants access to external items via ACLs (e.g., Everyone) instead. Default: true.
	/// </summary>
	public bool UseExternalGroupMembershipWorkaround { get; set; } = true;
}
