using Microsoft.Graph;
using Microsoft.Graph.Models.ExternalConnectors;
using Microsoft.Graph.Models.ODataErrors;

namespace AiHub.Connector.ExternalConnectors.M365Connector;

public class GraphExternalClient : IGraphExternalClient
{
	private readonly GraphServiceClient _client;

	public GraphExternalClient(GraphServiceClient client)
	{
		_client = client ?? throw new ArgumentNullException(nameof(client));
	}

	public Task<ExternalConnection?> GetConnectionAsync(string connectionId, CancellationToken ct)
	{
		return _client.External.Connections[connectionId].GetAsync(cancellationToken: ct);
	}

	public Task CreateConnectionAsync(ExternalConnection connection, CancellationToken ct)
	{
		return _client.External.Connections.PostAsync(connection, cancellationToken: ct);
	}

	public Task DeleteConnectionAsync(string connectionId, CancellationToken ct)
	{
		return _client.External.Connections[connectionId].DeleteAsync(cancellationToken: ct);
	}

	public Task<Schema?> GetSchemaAsync(string connectionId, CancellationToken ct)
	{
		return _client.External.Connections[connectionId].Schema.GetAsync(cancellationToken: ct);
	}

	public Task CreateOrUpdateSchemaAsync(string connectionId, Schema schema, CancellationToken ct)
	{
		return _client.External.Connections[connectionId].Schema.PatchAsync(schema, cancellationToken: ct);
	}

	public Task CreateExternalGroupAsync(string connectionId, ExternalGroup group, CancellationToken ct)
	{
		return _client.External.Connections[connectionId].Groups.PostAsync(group, cancellationToken: ct);
	}

	public Task<ExternalGroup?> GetExternalGroupAsync(string connectionId, string externalGroupId, CancellationToken ct)
	{
		return _client.External.Connections[connectionId].Groups[externalGroupId].GetAsync(cancellationToken: ct);
	}

	public Task DeleteExternalGroupAsync(string connectionId, string externalGroupId, CancellationToken ct)
	{
		return _client.External.Connections[connectionId].Groups[externalGroupId].DeleteAsync(cancellationToken: ct);
	}

	public Task<ExternalItem?> GetItemAsync(string connectionId, string itemId, CancellationToken ct)
	{
		return _client.External.Connections[connectionId].Items[itemId].GetAsync(cancellationToken: ct);
	}

	public async Task UpsertItemAsync(string connectionId, string itemId, ExternalItem item, Schema predefinedSchema, CancellationToken ct)
	{
		var requestBody = new ExternalItem
		{
			Acl = item.Acl,
			Content = item.Content,
			Properties = item.Properties,
		};
		ExternalItem? existingItem = null;
		try
		{
			existingItem = await GetItemAsync(connectionId, itemId, ct).ConfigureAwait(false);
			if (existingItem != null)
			{
				var properties = item.Properties ?? new Properties { AdditionalData = new Dictionary<string, object>() };
				// Merge existing properties if not provided in the new item
				if (existingItem.Properties?.AdditionalData != null)
				{
					foreach (var key in predefinedSchema.Properties?.Select(p => p.Name) ?? [])
					{
						if (!properties.AdditionalData.ContainsKey(key) && existingItem.Properties.AdditionalData.ContainsKey(key))
						{
							properties.AdditionalData[key] = existingItem.Properties.AdditionalData[key];
						}
					}
				}

				// Update existing item - merge properties and content if not provided in the new item
				var updateRequestBody = new ExternalItem
				{
					Properties = properties,
					Content = item.Content ?? new ExternalItemContent { Type = ExternalItemContentType.Text, Value = existingItem.Content?.Value },
					Acl = item.Acl ?? existingItem.Acl,
				};
				await _client.External.Connections[connectionId].Items[itemId].PutAsync(updateRequestBody, cancellationToken: ct).ConfigureAwait(false);
				return;
			}
		}
		catch (ODataError ex) when (ex.ResponseStatusCode == 404)
		{
			// Item does not exist; will be created
			requestBody.Id = itemId; // Ensure Id is set for create operation
		}
		await _client.External.Connections[connectionId].Items[itemId].PutAsync(requestBody, cancellationToken: ct).ConfigureAwait(false);
	}

	public Task DeleteItemAsync(string connectionId, string itemId, CancellationToken ct)
	{
		return _client.External.Connections[connectionId].Items[itemId].DeleteAsync(cancellationToken: ct);
	}

	public async Task AddMemberToExternalGroupAsync(string connectionId, string externalGroupId, ConnectorIdentity member, CancellationToken ct)
	{
		var requestBody = new Identity { Id = member.Id, Type = member.Type };
		await _client.External.Connections[connectionId].Groups[externalGroupId].Members.PostAsync(requestBody, cancellationToken: ct).ConfigureAwait(false);
	}

	public Task RemoveMemberFromExternalGroupAsync(string connectionId, string externalGroupId, string memberId, CancellationToken ct)
	{
		return _client.External.Connections[connectionId].Groups[externalGroupId].Members[memberId].DeleteAsync(cancellationToken: ct);
	}
}
