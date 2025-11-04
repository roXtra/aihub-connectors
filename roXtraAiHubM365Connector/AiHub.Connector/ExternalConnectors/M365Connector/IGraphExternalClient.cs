using Microsoft.Graph.Models.ExternalConnectors;

namespace AiHub.Connector.ExternalConnectors.M365Connector;

public interface IGraphExternalClient
{
	Task<ExternalConnection?> GetConnectionAsync(string connectionId, CancellationToken ct);
	Task CreateConnectionAsync(ExternalConnection connection, CancellationToken ct);
	Task DeleteConnectionAsync(string connectionId, CancellationToken ct);

	Task<Schema?> GetSchemaAsync(string connectionId, CancellationToken ct);
	Task CreateOrUpdateSchemaAsync(string connectionId, Schema schema, CancellationToken ct);

	Task CreateExternalGroupAsync(string connectionId, ExternalGroup group, CancellationToken ct);
	Task DeleteExternalGroupAsync(string connectionId, string externalGroupId, CancellationToken ct);
	Task<ExternalGroup?> GetExternalGroupAsync(string connectionId, string externalGroupId, CancellationToken ct);
	Task<ExternalItem?> GetItemAsync(string connectionId, string itemId, CancellationToken ct);
	Task UpsertItemAsync(string connectionId, string itemId, ExternalItem item, Schema predefinedSchema, CancellationToken ct);
	Task DeleteItemAsync(string connectionId, string itemId, CancellationToken ct);

	// External group membership management
	Task AddMemberToExternalGroupAsync(string connectionId, string externalGroupId, ConnectorIdentity member, CancellationToken ct);
	Task RemoveMemberFromExternalGroupAsync(string connectionId, string externalGroupId, string memberId, CancellationToken ct);
}
