using System.IO;
using AiHub.Connector.Data;
using AiHub.Connector.Data.Entities;
using AiHub.Connector.Roxtra;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Models.ExternalConnectors;
using Microsoft.Kiota.Abstractions;

namespace AiHub.Connector.ExternalConnectors.M365Connector;

/// <summary>
/// Microsoft 365 external connector implementation. This maps roXtra knowledge pools to M365 external groups and rox files to M365 external items.
/// The member groups of a knowledge pool are synchronized to the corresponding members of the M365 external group.
/// Known limitations: https://learn.microsoft.com/en-us/graph/api/resources/connectors-api-overview?view=graph-rest-1.0#known-limitations Max file size is 4MB, max 25 concurrent requests per connection.
/// </summary>
public class M365Connector : IExternalConnector
{
	private readonly ILogger<M365Connector> _logger;
	private readonly ConnectorDbContext _db;
	private readonly IGraphExternalClient _graph;
	private readonly GraphOptions _graphOptions;
	private readonly RoxtraOptions _roxtraOptions;
	private readonly IPdfTextExtractor _pdfTextExtractor;

	private static readonly Schema _PredefinedSchema = new()
	{
		BaseType = "microsoft.graph.externalItem",
		Properties =
		[
			new Property
			{
				Name = "title",
				Type = PropertyType.String,
				IsSearchable = true,
				IsQueryable = true,
				IsRetrievable = true,
				Labels = [Label.Title],
			},
			new Property
			{
				Name = "url",
				Type = PropertyType.String,
				IsSearchable = false,
				IsQueryable = false,
				IsRetrievable = true,
				Labels = [Label.Url],
			},
			new Property
			{
				Name = "roxFileId",
				Type = PropertyType.String,
				IsSearchable = true,
				IsQueryable = true,
				IsRetrievable = true,
			},
			new Property
			{
				Name = "iconUrl",
				Type = PropertyType.String,
				IsSearchable = false,
				IsQueryable = false,
				IsRetrievable = true,
				Labels = [Label.IconUrl],
			},
			new Property
			{
				Name = "knowledgePoolIds",
				Type = PropertyType.String,
				IsSearchable = true,
				IsQueryable = true,
				IsRetrievable = true,
			},
			new Property
			{
				Name = "description",
				Type = PropertyType.String,
				IsSearchable = true,
				IsQueryable = true,
				IsRetrievable = true,
			},
		],
	};

	public M365Connector(
		ILogger<M365Connector> logger,
		ConnectorDbContext db,
		IGraphExternalClient graph,
		IOptions<GraphOptions> graphOptions,
		IOptions<RoxtraOptions> roxtraOptions,
		IPdfTextExtractor pdfTextExtractor
	)
	{
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_db = db ?? throw new ArgumentNullException(nameof(db));
		_graph = graph ?? throw new ArgumentNullException(nameof(graph));
		_graphOptions = graphOptions?.Value ?? throw new ArgumentNullException(nameof(graphOptions));
		_roxtraOptions = roxtraOptions?.Value ?? throw new ArgumentNullException(nameof(roxtraOptions));
		_pdfTextExtractor = pdfTextExtractor ?? throw new ArgumentNullException(nameof(pdfTextExtractor));
	}

	/// <summary>
	/// Initializes the M365 external connection. This is called during service startup to ensure
	/// the connection and schema are created if they don't exist yet.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	public async Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		_logger.LogInformation("Initializing M365 external connection: {ConnectionId}", _graphOptions.ExternalConnectionId);
		await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
		await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
		_logger.LogInformation("M365 external connection initialization completed for: {ConnectionId}", _graphOptions.ExternalConnectionId);
	}

	/// <summary>
	/// Handles the Knowledge Pool file added event. If a file is added to a knowledge pool, this ensures that an external item
	/// representing the file exists in M365 and that the external group representing the knowledge pool has access to the item.
	/// If the file was already added to another knowledge pool, the existing external item is updated to include access for the new knowledge pool's external group.
	/// If the file was never added before, a new external item is created granting access to the knowledge pool's external group.
	/// </summary>
	/// <param name="knowledgePoolId">Knowledge pool identifier provided by the event payload.</param>
	/// <param name="file">The Roxtra file resolved from the AI Hub.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	public async Task HandleKnowledgePoolFileAddedAsync(string knowledgePoolId, RoxFile file, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(knowledgePoolId))
		{
			throw new ArgumentException("Knowledge pool id must be provided", nameof(knowledgePoolId));
		}
		if (file is null)
		{
			throw new ArgumentNullException(nameof(file));
		}

		_logger.LogInformation(
			"M365 handling knowledgepool.file.added: KnowledgePoolId={KnowledgePoolId}, FileId={FileId}, Title={Title}, ContentLength={Length}",
			knowledgePoolId,
			file.Id,
			file.Title,
			file.ContentStream != null ? -1 : 0
		);

		// Ensure external connection exists
		await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);

		// Ensure external group exists for the knowledge pool
		var group = await EnsureExternalGroupAsync(knowledgePoolId, cancellationToken).ConfigureAwait(false);

		var connectionId = _graphOptions.ExternalConnectionId;
		var itemId = ComputeStableItemId(file.Id);

		// Determine if this file was previously added (either tracked in DB or already present in Graph)
		var existingMapping = _db.ExternalFiles.FirstOrDefault(x => x.RoxFileId == file.Id);
		bool graphItemExists = false;
		ExternalItem? existingItem = null;
		// Build knowledge pool list including the one currently being added
		var knowledgePools = _db.FileKnowledgePools.Where(x => x.RoxFileId == file.Id).Select(x => x.KnowledgePoolId).ToList();
		if (!knowledgePools.Contains(knowledgePoolId))
			knowledgePools.Add(knowledgePoolId);

		try
		{
			existingItem = await _graph.GetItemAsync(connectionId, itemId, cancellationToken).ConfigureAwait(false);
			graphItemExists = existingItem is not null;
		}
		catch (ApiException ex) when (ex.ResponseStatusCode == 404)
		{
			graphItemExists = false;
		}

		if (existingMapping is not null || graphItemExists)
		{
			// File already exists in M365; merge ACL to include this knowledge pool's external group
			await MergeItemAclAsync(connectionId, itemId, group.ExternalGroupId, existingItem, knowledgePoolId, file.Id, cancellationToken)
				.ConfigureAwait(false);

			// Ensure mapping exists locally
			if (existingMapping is null)
			{
				_ = _db.ExternalFiles.Add(new ExternalFileEntity { RoxFileId = file.Id, ExternalItemId = itemId });
			}
			else
			{
				// keep mapping's ExternalItemId in sync with our stable id
				existingMapping.ExternalItemId = itemId;
			}
		}
		else
		{
			// First time we see this file; create item with ACL granting the external group
			var externalItemId = await UploadFileAndGetExternalIdAsync(file, group.ExternalGroupId, knowledgePoolId, cancellationToken).ConfigureAwait(false);

			// Persist mapping
			_ = _db.ExternalFiles.Add(new ExternalFileEntity { RoxFileId = file.Id, ExternalItemId = externalItemId });
		}

		// Ensure file -> knowledge pool mapping exists locally
		var fk = _db.FileKnowledgePools.FirstOrDefault(x => x.RoxFileId == file.Id && x.KnowledgePoolId == knowledgePoolId);
		if (fk is null)
		{
			_ = _db.FileKnowledgePools.Add(new FileKnowledgePoolEntity { RoxFileId = file.Id, KnowledgePoolId = knowledgePoolId });
		}

		_ = await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Handles the Knowledge Pool file removed event. Removes the external group's ACL from the item's ACL.
	/// If no external-group ACLs remain afterwards, deletes the external item and removes the local mapping.
	/// </summary>
	public async Task HandleKnowledgePoolFileRemovedAsync(string knowledgePoolId, string roxFileId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(knowledgePoolId))
		{
			throw new ArgumentException("Knowledge pool id must be provided", nameof(knowledgePoolId));
		}
		if (string.IsNullOrWhiteSpace(roxFileId))
		{
			throw new ArgumentException("Roxtra file id must be provided", nameof(roxFileId));
		}

		_logger.LogInformation("M365 handling knowledgepool.file.removed: KnowledgePoolId={KnowledgePoolId}, FileId={FileId}", knowledgePoolId, roxFileId);

		await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);

		var connectionId = _graphOptions.ExternalConnectionId;
		var itemId = ComputeStableItemId(roxFileId);
		var externalGroupId =
			_db.ExternalGroups.FirstOrDefault(x => x.KnowledgePoolId == knowledgePoolId)?.ExternalGroupId ?? ComputeStableGroupId(knowledgePoolId);

		// Remove local mapping between this file and knowledge pool
		var toRemove = _db.FileKnowledgePools.Where(x => x.RoxFileId == roxFileId && x.KnowledgePoolId == knowledgePoolId).ToList();
		if (toRemove.Count > 0)
		{
			_db.FileKnowledgePools.RemoveRange(toRemove);
			_ = await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}

		await RemoveGroupFromItemAsync(connectionId, itemId, roxFileId, externalGroupId, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Handles the Knowledge Pool removed event. Deletes the Graph external group and removes the local mapping.
	/// </summary>
	public async Task HandleKnowledgePoolRemovedAsync(string knowledgePoolId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(knowledgePoolId))
		{
			throw new ArgumentException("Knowledge pool id must be provided", nameof(knowledgePoolId));
		}

		_logger.LogInformation("M365 handling knowledgepool.removed: KnowledgePoolId={KnowledgePoolId}", knowledgePoolId);

		await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);

		var connectionId = _graphOptions.ExternalConnectionId;
		var entity = _db.ExternalGroups.FirstOrDefault(x => x.KnowledgePoolId == knowledgePoolId);
		var externalGroupId = entity?.ExternalGroupId ?? ComputeStableGroupId(knowledgePoolId);

		// Only process items that are recorded as belonging to this knowledge pool
		var fileLinks = _db.FileKnowledgePools.Where(x => x.KnowledgePoolId == knowledgePoolId).ToList();
		var fileIds = new HashSet<string>(fileLinks.Select(l => l.RoxFileId));
		foreach (var roxFileId in fileIds)
		{
			// remove local link entry for this pool and file
			var links = fileLinks.Where(l => l.RoxFileId == roxFileId).ToList();
			if (links.Count > 0)
			{
				_db.FileKnowledgePools.RemoveRange(links);
			}

			var externalItemId = _db.ExternalFiles.FirstOrDefault(x => x.RoxFileId == roxFileId)?.ExternalItemId ?? ComputeStableItemId(roxFileId);
			await RemoveGroupFromItemAsync(connectionId, externalItemId, roxFileId, externalGroupId, cancellationToken).ConfigureAwait(false);
		}

		_ = await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		// Delete the external group in Graph
		try
		{
			await _graph.DeleteExternalGroupAsync(connectionId, externalGroupId, cancellationToken).ConfigureAwait(false);
			_logger.LogInformation("Deleted external group {ExternalGroupId}", externalGroupId);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to delete external group {ExternalGroupId}", externalGroupId);
			throw;
		}

		if (entity is not null)
		{
			_db.ExternalGroups.Remove(entity);
			_ = await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Updates the content and properties of the existing external item corresponding to the provided file.
	/// If the item does not exist yet, it is created with ACL built from the file's knowledge pool memberships.
	/// </summary>
	public async Task HandleFileUpdatedAsync(RoxFile file, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (file is null)
		{
			throw new ArgumentNullException(nameof(file));
		}

		_logger.LogInformation(
			"M365 handling file.updated: FileId={FileId}, Title={Title}, ContentLength={Length}",
			file.Id,
			file.Title,
			file.ContentStream != null ? -1 : 0
		);

		await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);

		var connectionId = _graphOptions.ExternalConnectionId;
		var itemId = ComputeStableItemId(file.Id);

		ExternalItem? existing = null;
		try
		{
			existing = await _graph.GetItemAsync(connectionId, itemId, cancellationToken).ConfigureAwait(false);
		}
		catch (ApiException ex) when (ex.ResponseStatusCode == 404)
		{
			existing = null;
		}

		var textContent = await GetTextContentAsync(file, cancellationToken).ConfigureAwait(false);
		var newContent = new ExternalItemContent { Type = ExternalItemContentType.Text, Value = textContent };

		var pools = _db.FileKnowledgePools.Where(x => x.RoxFileId == file.Id).Select(x => x.KnowledgePoolId);
		var additionalData = BuildAdditionalData(file, pools, textContent);
		var newProps = new Properties { AdditionalData = additionalData };

		if (existing is not null)
		{
			// Replace content and properties
			await _graph
				.UpsertItemAsync(connectionId, itemId, new ExternalItem { Content = newContent, Properties = newProps }, _PredefinedSchema, cancellationToken)
				.ConfigureAwait(false);
			_logger.LogInformation("Updated external item content for {ItemId}.", itemId);
			return;
		}

		// Create new item: build ACLs from all pools that contain this file
		var poolLinks = _db.FileKnowledgePools.Where(x => x.RoxFileId == file.Id).ToList();
		if (poolLinks.Count == 0)
		{
			_logger.LogWarning("No knowledge pool links found for file {FileId}; skipping external item creation on update.", file.Id);
			return;
		}
		var acl = new List<Acl>();
		foreach (var link in poolLinks)
		{
			var externalGroupId =
				_db.ExternalGroups.FirstOrDefault(x => x.KnowledgePoolId == link.KnowledgePoolId)?.ExternalGroupId
				?? ComputeStableGroupId(link.KnowledgePoolId);
			acl.Add(
				new Acl
				{
					Type = AclType.ExternalGroup,
					Value = externalGroupId,
					AccessType = AccessType.Grant,
				}
			);
		}

		await _graph
			.UpsertItemAsync(
				connectionId,
				itemId,
				new ExternalItem
				{
					Id = itemId,
					Content = newContent,
					Acl = acl,
					Properties = newProps,
				},
				_PredefinedSchema,
				cancellationToken
			)
			.ConfigureAwait(false);
		_logger.LogInformation("Created external item {ItemId} for updated file {FileId}.", itemId, file.Id);

		// Ensure mapping exists
		var mapping = _db.ExternalFiles.FirstOrDefault(x => x.RoxFileId == file.Id);
		if (mapping is null)
		{
			_ = _db.ExternalFiles.Add(new ExternalFileEntity { RoxFileId = file.Id, ExternalItemId = itemId });
			_ = await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Handles the Knowledge Pool created event by receiving the target knowledge pool id. This ensures that a graph external group
	/// is created for the knowledge pool.
	/// </summary>
	/// <param name="knowledgePoolId"></param>
	/// <param name="cancellationToken"></param>
	/// <returns></returns>
	/// <exception cref="ArgumentException"></exception>
	public async Task HandleKnowledgePoolCreatedAsync(string knowledgePoolId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(knowledgePoolId))
		{
			throw new ArgumentException("Knowledge pool id must be provided", nameof(knowledgePoolId));
		}

		_logger.LogInformation("M365 handling knowledgepool.created: KnowledgePoolId={KnowledgePoolId}", knowledgePoolId);

		await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
		_ = await EnsureExternalGroupAsync(knowledgePoolId, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Handles the Knowledge Pool members updated event by receiving the target knowledge pool id. If the members of a knowledge pool
	/// are updated, the external group in M365 is synchronized to match the provided list of groups. The members of the graph external group
	/// will exactly match the non-empty externalGroupIds (which are expected to be Entra ID group ids the roXtra groups are synchronized with)
	/// provided in the event payload.
	/// </summary>
	/// <param name="knowledgePoolId">knowledge pool id</param>
	/// <param name="roxtraGroupGid">roXtra group gid</param>
	/// <param name="roXtraExternalGroupId">Id of external group that represents the roXtra group. This should be the Entra ID group id.</param>
	/// <summary>
	/// Adds a single directory group as a member of the external group representing the knowledge pool.
	/// </summary>
	public async Task HandleKnowledgePoolMemberAddedAsync(
		string knowledgePoolId,
		Guid roxtraGroupGid,
		string roXtraExternalGroupId,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(knowledgePoolId))
		{
			throw new ArgumentException("Knowledge pool id must be provided", nameof(knowledgePoolId));
		}

		_logger.LogInformation(
			"M365 handling knowledgepool.member.added: KnowledgePoolId={KnowledgePoolId}, RoxtraGroupGid={RoxGid}, ExternalGroupId={ExtGid}",
			knowledgePoolId,
			roxtraGroupGid,
			roXtraExternalGroupId
		);

		await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
		var externalGroup = await EnsureExternalGroupAsync(knowledgePoolId, cancellationToken).ConfigureAwait(false);

		// Only add the member when an external group id was provided; it can be empty/null for roXtra groups that are not synchronized with Entra ID
		if (!string.IsNullOrWhiteSpace(roXtraExternalGroupId))
		{
			if (_graphOptions.UseExternalGroupMembershipWorkaround)
			{
				_logger.LogInformation(
					"Using workaround: skip adding member {MemberId} to external group {ExternalGroupId}; relying on ACL-based access.",
					roXtraExternalGroupId,
					externalGroup.ExternalGroupId
				);
			}
			else
			{
				var connectionId = _graphOptions.ExternalConnectionId;
				try
				{
					var identity = new ConnectorIdentity
					{
						Id = roXtraExternalGroupId,
						Type = IdentityType.Group,
						IdentitySource = "azureActiveDirectory",
					};
					await _graph.AddMemberToExternalGroupAsync(connectionId, externalGroup.ExternalGroupId, identity, cancellationToken).ConfigureAwait(false);
					_logger.LogInformation("Added member {MemberId} to external group {ExternalGroupId}", roXtraExternalGroupId, externalGroup.ExternalGroupId);
				}
				catch (ApiException ex) when (ex.ResponseStatusCode == 409)
				{
					_logger.LogInformation(
						"Member {MemberId} already present in external group {ExternalGroupId}",
						roXtraExternalGroupId,
						externalGroup.ExternalGroupId
					);
				}
			}
		}
		else
		{
			_logger.LogDebug(
				"No external group id provided for member add; ensured external group {ExternalGroupId} exists and skipped adding member.",
				externalGroup.ExternalGroupId
			);
		}
	}

	/// <summary>
	/// Removes a single directory group from the external group representing the knowledge pool.
	/// </summary>
	public async Task HandleKnowledgePoolMemberRemovedAsync(
		string knowledgePoolId,
		Guid roxtraGroupGid,
		string externalGroupId,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(knowledgePoolId))
		{
			throw new ArgumentException("Knowledge pool id must be provided", nameof(knowledgePoolId));
		}
		if (string.IsNullOrWhiteSpace(externalGroupId))
		{
			_logger.LogDebug(
				"No external group id provided for member removal; skipping. KnowledgePoolId={KnowledgePoolId}, RoxtraGroupGid={RoxGid}",
				knowledgePoolId,
				roxtraGroupGid
			);
			return;
		}

		_logger.LogInformation(
			"M365 handling knowledgepool.member.removed: KnowledgePoolId={KnowledgePoolId}, RoxtraGroupGid={RoxGid}, ExternalGroupId={ExtGid}",
			knowledgePoolId,
			roxtraGroupGid,
			externalGroupId
		);

		await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
		var externalGroup = await EnsureExternalGroupAsync(knowledgePoolId, cancellationToken).ConfigureAwait(false);

		var connectionId = _graphOptions.ExternalConnectionId;
		if (_graphOptions.UseExternalGroupMembershipWorkaround)
		{
			_logger.LogInformation(
				"Using workaround: skip removing member {MemberId} from external group {ExternalGroupId}; access is managed via item ACLs.",
				externalGroupId,
				externalGroup.ExternalGroupId
			);
		}
		else
		{
			try
			{
				await _graph
					.RemoveMemberFromExternalGroupAsync(connectionId, externalGroup.ExternalGroupId, externalGroupId, cancellationToken)
					.ConfigureAwait(false);
				_logger.LogInformation("Removed member {MemberId} from external group {ExternalGroupId}", externalGroupId, externalGroup.ExternalGroupId);
			}
			catch (ApiException ex) when (ex.ResponseStatusCode == 404)
			{
				_logger.LogError(
					"Member {MemberId} not found in external group {ExternalGroupId} during removal",
					externalGroupId,
					externalGroup.ExternalGroupId
				);
				// Rethrow this error so that the caller can handle it if needed - perhaps we lost a member add event that will be retried later
				throw;
			}
		}
	}

	private async Task EnsureConnectionAsync(CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		var connectionId = _graphOptions.ExternalConnectionId;
		if (string.IsNullOrWhiteSpace(connectionId))
		{
			throw new InvalidOperationException("Graph:ExternalConnectionId must be configured.");
		}

		try
		{
			var existing = await _graph.GetConnectionAsync(connectionId, ct).ConfigureAwait(false);
			if (existing != null)
			{
				return;
			}
		}
		catch (ApiException ex) when (ex.ResponseStatusCode == 404)
		{
			// Not found, will create below
		}

		var connection = new ExternalConnection
		{
			Id = connectionId,
			Name = $"roXtra AiHub Connector ({connectionId})",
			Description = "Publishes roXtra documents to Microsoft 365 Search",
		};

		try
		{
			await _graph.CreateConnectionAsync(connection, ct).ConfigureAwait(false);
			_logger.LogInformation("Created external connection {ConnectionId} in Graph.", connectionId);
		}
		catch (ApiException ex) when (ex.ResponseStatusCode == 409)
		{
			_logger.LogInformation("External connection {ConnectionId} already exists (race condition).", connectionId);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to ensure external connection {ConnectionId} in Graph.", connectionId);
			throw;
		}

		// Ensure schema exists so that pushing external items is enabled
		await EnsureSchemaAsync(ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Ensures that the external connection schema matches the predefined schema.
	/// If the schema does not exist, it is created. If it exists but does not match the predefined schema, it is updated.
	/// </summary>
	/// <param name="ct">Cancellation token</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	private async Task EnsureSchemaAsync(CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		Schema? existingSchema;
		try
		{
			existingSchema = await _graph.GetSchemaAsync(_graphOptions.ExternalConnectionId, ct).ConfigureAwait(false);
		}
		catch (ApiException ex) when (ex.ResponseStatusCode == 404)
		{
			existingSchema = null;
		}

		// If there is no schema or it doesn't match what we expect, update it
		if (existingSchema is null || !SchemaMatches(existingSchema, _PredefinedSchema))
		{
			try
			{
				await _graph.CreateOrUpdateSchemaAsync(_graphOptions.ExternalConnectionId, _PredefinedSchema, ct).ConfigureAwait(false);
				_logger.LogInformation(
					existingSchema is null
						? "Created external connection schema for {ConnectionId}."
						: "Updated external connection schema for {ConnectionId} to match predefined schema.",
					_graphOptions.ExternalConnectionId
				);
			}
			catch (ApiException ex) when (ex.ResponseStatusCode == 409)
			{
				_logger.LogInformation("Schema already exists for {ConnectionId} (race condition).", _graphOptions.ExternalConnectionId);
			}
		}
		else
		{
			_logger.LogInformation("External connection schema for {ConnectionId} is up-to-date.", _graphOptions.ExternalConnectionId);
		}
	}

	private static bool SchemaMatches(Schema existing, Schema desired)
	{
		if (!string.Equals(existing.BaseType, desired.BaseType, StringComparison.OrdinalIgnoreCase))
			return false;

		var existingProps = existing.Properties?.ToList() ?? new List<Property>();
		var desiredProps = desired.Properties?.ToList() ?? new List<Property>();

		if (existingProps.Count != desiredProps.Count)
			return false;

		foreach (var desiredProp in desiredProps)
		{
			var match = existingProps.FirstOrDefault(p => string.Equals(p.Name, desiredProp.Name, StringComparison.OrdinalIgnoreCase));
			if (match is null)
				return false;

			if (match.Type != desiredProp.Type)
				return false;

			// Treat null as false for flags
			if ((match.IsSearchable ?? false) != (desiredProp.IsSearchable ?? false))
				return false;
			if ((match.IsQueryable ?? false) != (desiredProp.IsQueryable ?? false))
				return false;
			if ((match.IsRetrievable ?? false) != (desiredProp.IsRetrievable ?? false))
				return false;

			// Compare labels as set, ignoring order; null == empty
			var matchLabels = (match.Labels?.Where(l => l.HasValue).Select(l => l!.Value) ?? Enumerable.Empty<Label>()).OrderBy(l => l).ToArray();
			var desiredLabels = (desiredProp.Labels?.Where(l => l.HasValue).Select(l => l!.Value) ?? Enumerable.Empty<Label>()).OrderBy(l => l).ToArray();
			if (matchLabels.Length != desiredLabels.Length)
				return false;
		}

		return true;
	}

	private async Task<ExternalGroupEntity> EnsureExternalGroupAsync(string knowledgePoolId, CancellationToken ct)
	{
		var existing = _db.ExternalGroups.FirstOrDefault(x => x.KnowledgePoolId == knowledgePoolId);
		if (existing != null)
		{
			return existing;
		}

		// Create external group in Graph and store returned id
		var externalGroupId = ComputeStableGroupId(knowledgePoolId);
		var displayName = $"Knowledge Pool {knowledgePoolId}";
		var description = $"External group for knowledge pool {knowledgePoolId}";

		try
		{
			var newGroup = new ExternalGroup
			{
				Id = externalGroupId,
				DisplayName = displayName,
				Description = description,
			};

			await _graph.CreateExternalGroupAsync(_graphOptions.ExternalConnectionId, newGroup, ct).ConfigureAwait(false);
		}
		catch (ApiException ex) when (ex.ResponseStatusCode == 409)
		{
			// Group already exists with this id; continue with local persistence
			_logger.LogInformation("External group {ExternalGroupId} already exists in Graph.", externalGroupId);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to create external group in Graph for KnowledgePoolId={KnowledgePoolId}", knowledgePoolId);
			throw;
		}
		var entity = new ExternalGroupEntity { KnowledgePoolId = knowledgePoolId, ExternalGroupId = externalGroupId };
		_ = _db.ExternalGroups.Add(entity);
		_ = await _db.SaveChangesAsync(ct).ConfigureAwait(false);
		return entity;
	}

	private async Task<string> UploadFileAndGetExternalIdAsync(RoxFile file, string externalGroupId, string knowledgePoolId, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		var connectionId = _graphOptions.ExternalConnectionId;
		if (string.IsNullOrWhiteSpace(connectionId))
		{
			throw new InvalidOperationException("Graph:ExternalConnectionId must be configured.");
		}

		var itemId = ComputeStableItemId(file.Id);

		// Build knowledge pool list including the one currently being added
		var knowledgePools = _db.FileKnowledgePools.Where(x => x.RoxFileId == file.Id).Select(x => x.KnowledgePoolId).ToList();
		if (!knowledgePools.Contains(knowledgePoolId))
			knowledgePools.Add(knowledgePoolId);

		var textContent = await GetTextContentAsync(file, ct).ConfigureAwait(false);

		// Build initial ACL
		var acl = new List<Acl>();
		if (_graphOptions.UseExternalGroupMembershipWorkaround)
		{
			// Grant Everyone when workaround is enabled
			acl.Add(
				new Acl
				{
					Type = AclType.Everyone,
					AccessType = AccessType.Grant,
					Value = Guid.NewGuid().ToString(),
				}
			);
		}
		acl.Add(
			new Acl
			{
				Type = AclType.ExternalGroup,
				Value = externalGroupId,
				AccessType = AccessType.Grant,
			}
		);

		try
		{
			// Upsert the item using PUT
			var additionalData = BuildAdditionalData(file, knowledgePools, textContent);
			await _graph
				.UpsertItemAsync(
					connectionId,
					itemId,
					new ExternalItem
					{
						Id = itemId,
						Content = new ExternalItemContent { Type = ExternalItemContentType.Text, Value = textContent },
						Acl = acl,
						Properties = new Properties { AdditionalData = additionalData },
					},
					_PredefinedSchema,
					ct
				)
				.ConfigureAwait(false);
			_logger.LogInformation("Upserted external item {ItemId} for RoxFile {RoxFileId}.", itemId, file.Id);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to upsert external item for RoxFileId={RoxFileId}.", file.Id);
			throw;
		}

		return itemId;
	}

	// Removed byte[]-based extraction; we now rely on streams end-to-end.

	async Task<string> GetTextContentAsync(Roxtra.RoxFile file, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		if (file.ContentStream != null)
		{
			await using var s = file.ContentStream;
			return await _pdfTextExtractor.ExtractAsync(s, ct).ConfigureAwait(false);
		}
		return string.Empty;
	}

	private Dictionary<string, object> BuildAdditionalData(RoxFile file, IEnumerable<string> knowledgePoolIds, string textContent)
	{
		return new Dictionary<string, object>
		{
			["title"] = file.Title,
			["roxFileId"] = file.Id,
			["url"] = _roxtraOptions.RoxtraUrl.TrimEnd('/') + "/ui/xd/files/" + file.Id,
			["iconUrl"] = _roxtraOptions.RoxtraUrl.TrimEnd('/') + "/doc/images/svgs/mod/default/favicon-32x32.png",
			["knowledgePoolIds"] = string.Join(";", knowledgePoolIds),
			["description"] = textContent.Length > 200 ? textContent[..200] + "…" : textContent,
		};
	}

	private static string ComputeStableGroupId(string knowledgePoolId)
	{
		return $"roXtraKp{knowledgePoolId.Replace("-", "").ToLowerInvariant()}";
	}

	private static string ComputeStableItemId(string roxFileId)
	{
		return $"roXtraFile{roxFileId}";
	}

	private async Task RemoveGroupFromItemAsync(string connectionId, string itemId, string? roxFileId, string externalGroupId, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		ExternalItem? item = null;
		try
		{
			item = await _graph.GetItemAsync(connectionId, itemId, ct).ConfigureAwait(false);
		}
		catch (ApiException ex) when (ex.ResponseStatusCode == 404)
		{
			item = null;
		}

		if (item is null)
		{
			_logger.LogInformation("External item {ItemId} not found; cleaning up local mappings if present.", itemId);
			if (!string.IsNullOrWhiteSpace(roxFileId))
			{
				var mappings = _db.ExternalFiles.Where(x => x.RoxFileId == roxFileId).ToList();
				if (mappings.Count > 0)
				{
					_db.ExternalFiles.RemoveRange(mappings);
					_ = await _db.SaveChangesAsync(ct).ConfigureAwait(false);
				}
			}
			return;
		}

		var acl = item.Acl?.ToList() ?? [];
		var filtered = acl.Where(a => !(a.Type == AclType.ExternalGroup && string.Equals(a.Value, externalGroupId, StringComparison.OrdinalIgnoreCase)))
			.ToList();
		var remainingExternalGroups = filtered.Count(a => a.Type == AclType.ExternalGroup && a.AccessType == AccessType.Grant);

		if (remainingExternalGroups <= 0)
		{
			await _graph.DeleteItemAsync(connectionId, itemId, ct).ConfigureAwait(false);
			_logger.LogInformation("Deleted external item {ItemId} after removing group {ExternalGroupId}.", itemId, externalGroupId);
			if (!string.IsNullOrWhiteSpace(roxFileId))
			{
				var mappings = _db.ExternalFiles.Where(x => x.RoxFileId == roxFileId).ToList();
				if (mappings.Count > 0)
				{
					_db.ExternalFiles.RemoveRange(mappings);
					_ = await _db.SaveChangesAsync(ct).ConfigureAwait(false);
				}
			}
			return;
		}

		// Update ACL and knowledgePoolIds to reflect removal
		var remainingPools = _db.FileKnowledgePools.Where(x => x.RoxFileId == roxFileId).Select(x => x.KnowledgePoolId).Distinct().ToList();
		await _graph
			.UpsertItemAsync(
				connectionId,
				itemId,
				new ExternalItem
				{
					Acl = filtered,
					Properties = new Properties { AdditionalData = new Dictionary<string, object> { ["knowledgePoolIds"] = string.Join(";", remainingPools) } },
				},
				_PredefinedSchema,
				ct
			)
			.ConfigureAwait(false);
		_logger.LogInformation("Removed external group {ExternalGroupId} from item {ItemId} ACL and updated knowledgePoolIds.", externalGroupId, itemId);
	}

	private async Task MergeItemAclAsync(
		string connectionId,
		string itemId,
		string externalGroupId,
		ExternalItem? existingItem,
		string knowledgePoolId,
		string roxFileId,
		CancellationToken ct
	)
	{
		ct.ThrowIfCancellationRequested();

		ExternalItem? item = existingItem;
		if (item is null)
		{
			try
			{
				item = await _graph.GetItemAsync(connectionId, itemId, ct).ConfigureAwait(false);
			}
			catch (ApiException ex) when (ex.ResponseStatusCode == 404)
			{
				item = null;
			}
		}

		if (item is null)
		{
			throw new InvalidOperationException($"External item {itemId} not found in Graph when merging ACL.");
		}

		item.Id = itemId; // ensure id present
		var acl = item.Acl?.ToList() ?? [];

		if (_graphOptions.UseExternalGroupMembershipWorkaround)
		{
			// Workaround path: ensure Everyone and the external group ACL are present
			bool hasEveryone = acl.Any(a => a.Type == AclType.Everyone);
			if (!hasEveryone)
			{
				acl.Add(
					new Acl
					{
						Type = AclType.Everyone,
						AccessType = AccessType.Grant,
						Value = Guid.NewGuid().ToString(),
					}
				);
			}

			bool hasExternalGroup = acl.Any(a =>
				a.Type == AclType.ExternalGroup
				&& string.Equals(a.Value, externalGroupId, StringComparison.OrdinalIgnoreCase)
				&& a.AccessType == AccessType.Grant
			);
			if (!hasExternalGroup)
			{
				acl.Add(
					new Acl
					{
						Type = AclType.ExternalGroup,
						Value = externalGroupId,
						AccessType = AccessType.Grant,
					}
				);
			}

			if (hasEveryone && hasExternalGroup)
			{
				_logger.LogInformation(
					"ACL for external item {ItemId} already contains Everyone and external group {ExternalGroupId}; no update needed.",
					itemId,
					externalGroupId
				);
				return;
			}
		}
		else
		{
			// Normal path: ensure the external group ACL is present
			bool alreadyPresent = acl.Any(a =>
				a.Type == AclType.ExternalGroup
				&& string.Equals(a.Value, externalGroupId, StringComparison.OrdinalIgnoreCase)
				&& a.AccessType == AccessType.Grant
			);
			if (!alreadyPresent)
			{
				acl.Add(
					new Acl
					{
						Type = AclType.ExternalGroup,
						Value = externalGroupId,
						AccessType = AccessType.Grant,
					}
				);
			}
			else
			{
				_logger.LogInformation(
					"ACL for external item {ItemId} already contains external group {ExternalGroupId}; no update needed.",
					itemId,
					externalGroupId
				);
				return;
			}
		}
		// Update ACL and knowledgePoolIds property to include the new pool
		var kpIds = _db.FileKnowledgePools.Where(x => x.RoxFileId == roxFileId).Select(x => x.KnowledgePoolId).ToList();
		if (!kpIds.Contains(knowledgePoolId))
			kpIds.Add(knowledgePoolId);
		await _graph
			.UpsertItemAsync(
				connectionId,
				itemId,
				new ExternalItem
				{
					Acl = acl,
					Properties = new Properties { AdditionalData = new Dictionary<string, object> { ["knowledgePoolIds"] = string.Join(";", kpIds) } },
				},
				_PredefinedSchema,
				ct
			)
			.ConfigureAwait(false);
		_logger.LogInformation("Updated ACL and knowledgePoolIds for external item {ItemId} to include {ExternalGroupId}.", itemId, externalGroupId);
	}
}
