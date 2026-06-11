using System.ClientModel;
using System.Collections.Concurrent;
using System.Text;
using AiHub.Connector.Data;
using AiHub.Connector.Data.Entities;
using AiHub.Connector.Roxtra;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.VectorStores;

namespace AiHub.Connector.ExternalConnectors.OpenAiVectorStoreConnector;

/// <summary>
/// OpenAI Vector Store connector implementation.
/// Maps roXtra knowledge pools to OpenAI vector stores and rox files to OpenAI files.
///
/// API Constraints (as of 2025):
/// - Vector stores: Up to 10,000 files per store, max 512MB per file, max 5M tokens per file
/// - Supported file types: .pdf, .md, .docx, .txt, .json, .html, .css, .js, .ts, .py, .java, .c, .cpp, .cs, .rb, .php, .go, .rs, .swift, .kt
/// - Note: .xlsx, .csv are NOT YET supported for vector stores (must be converted to supported format)
///
/// See: https://platform.openai.com/docs/assistants/tools/file-search
/// </summary>
public sealed class OpenAiVectorStoreConnector : IExternalConnector
{
	private readonly ILogger<OpenAiVectorStoreConnector> _logger;
	private readonly ConnectorDbContext _db;
	private readonly IOpenAiVectorStoreGateway _openAi;
	private readonly OpenAiVectorStoreOptions _options;

	// Limits concurrent OpenAI operations per file to 1 across all scoped instances.
	// Grows proportionally to unique file IDs processed (~100 bytes each) – bound by
	// the total number of files in the system and acceptable for production use.
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);

	private const int MaxVectorStoreNameLength = 64;

	public OpenAiVectorStoreConnector(
		ILogger<OpenAiVectorStoreConnector> logger,
		ConnectorDbContext db,
		IOpenAiVectorStoreGateway openAi,
		IOptions<OpenAiVectorStoreOptions> options
	)
	{
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_db = db ?? throw new ArgumentNullException(nameof(db));
		_openAi = openAi ?? throw new ArgumentNullException(nameof(openAi));
		_options = options?.Value ?? throw new ArgumentNullException(nameof(options));
	}

	private static async Task<IDisposable> LockFileAsync(string fileId, CancellationToken ct)
	{
		var sem = _fileLocks.GetOrAdd(fileId, _ => new SemaphoreSlim(1, 1));
		await sem.WaitAsync(ct).ConfigureAwait(false);
		return new SemaphoreReleaser(sem);
	}

	private sealed class SemaphoreReleaser(SemaphoreSlim semaphore) : IDisposable
	{
		public void Dispose() => semaphore.Release();
	}

	public async Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		_logger.LogInformation("OpenAI Vector Store connector initializing - verifying API connectivity");

		// Verify connectivity via a probe request.
		// A 404 is expected (store doesn't exist), but confirms the API is reachable and the key is valid.
		// Any other exception (401, network error) propagates and prevents startup.
		try
		{
			_ = await _openAi.GetVectorStoreAsync("vs_nonexistent_probe", cancellationToken).ConfigureAwait(false);
		}
		catch (ClientResultException ex) when (ex.Status == 404)
		{
			// Expected
		}

		_logger.LogInformation("OpenAI Vector Store connector initialized successfully");
	}

	public async Task HandleKnowledgePoolCreatedAsync(string knowledgePoolId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ArgumentException.ThrowIfNullOrWhiteSpace(knowledgePoolId);

		_logger.LogInformation("OpenAI handling knowledgepool.created: KnowledgePoolId={KnowledgePoolId}", knowledgePoolId);
		_ = await EnsureVectorStoreAsync(knowledgePoolId, cancellationToken).ConfigureAwait(false);
	}

	public Task HandleKnowledgePoolMemberAddedAsync(
		string knowledgePoolId,
		Guid roxtraGroupGid,
		string externalGroupId,
		CancellationToken cancellationToken = default
	)
	{
		_logger.LogDebug(
			"OpenAI ignoring knowledgepool.member.added (no ACL support): KnowledgePoolId={KnowledgePoolId}, RoxtraGroupGid={RoxtraGroupGid}",
			knowledgePoolId,
			roxtraGroupGid
		);
		return Task.CompletedTask;
	}

	public Task HandleKnowledgePoolMemberRemovedAsync(
		string knowledgePoolId,
		Guid roxtraGroupGid,
		string externalGroupId,
		CancellationToken cancellationToken = default
	)
	{
		_logger.LogDebug(
			"OpenAI ignoring knowledgepool.member.removed (no ACL support): KnowledgePoolId={KnowledgePoolId}, RoxtraGroupGid={RoxtraGroupGid}",
			knowledgePoolId,
			roxtraGroupGid
		);
		return Task.CompletedTask;
	}

	public async Task HandleKnowledgePoolFileAddedAsync(string knowledgePoolId, RoxFile file, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ArgumentException.ThrowIfNullOrWhiteSpace(knowledgePoolId);
		ArgumentNullException.ThrowIfNull(file);

		if (file.ContentStream is null)
		{
			throw new ArgumentException("RoxFile.ContentStream is required for file uploads", nameof(file));
		}

		_logger.LogInformation(
			"OpenAI handling knowledgepool.file.added: KnowledgePoolId={KnowledgePoolId}, FileId={FileId}, Title={Title}",
			knowledgePoolId,
			file.Id,
			file.Title
		);

		using var _ = await LockFileAsync(file.Id, cancellationToken).ConfigureAwait(false);

		var vectorStoreId = await GetVectorStoreIdAsync(knowledgePoolId, cancellationToken).ConfigureAwait(false);
		var openAiFileId = await EnsureFileUploadedAsync(file, cancellationToken).ConfigureAwait(false);

		// Track file-to-pool membership
		bool membershipExists = await _db
			.FileKnowledgePools.AnyAsync(x => x.RoxFileId == file.Id && x.KnowledgePoolId == knowledgePoolId, cancellationToken)
			.ConfigureAwait(false);

		if (!membershipExists)
		{
			_db.FileKnowledgePools.Add(new FileKnowledgePoolEntity { RoxFileId = file.Id, KnowledgePoolId = knowledgePoolId });
			await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}

		await AttachFileToVectorStoreAsync(vectorStoreId, openAiFileId, cancellationToken).ConfigureAwait(false);
	}

	public async Task HandleKnowledgePoolFileRemovedAsync(string knowledgePoolId, string roxFileId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ArgumentException.ThrowIfNullOrWhiteSpace(knowledgePoolId);
		ArgumentException.ThrowIfNullOrWhiteSpace(roxFileId);

		_logger.LogInformation("OpenAI handling knowledgepool.file.removed: KnowledgePoolId={KnowledgePoolId}, FileId={FileId}", knowledgePoolId, roxFileId);

		using var _ = await LockFileAsync(roxFileId, cancellationToken).ConfigureAwait(false);

		var vectorStoreId = await GetVectorStoreIdAsync(knowledgePoolId, cancellationToken).ConfigureAwait(false);

		// Remove membership record
		var memberships = await _db
			.FileKnowledgePools.Where(x => x.RoxFileId == roxFileId && x.KnowledgePoolId == knowledgePoolId)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);

		if (memberships.Count > 0)
		{
			_db.FileKnowledgePools.RemoveRange(memberships);
			await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}

		// Get file mapping
		var fileMapping = await _db.ExternalFiles.FirstOrDefaultAsync(x => x.RoxFileId == roxFileId, cancellationToken).ConfigureAwait(false);

		if (fileMapping is null)
		{
			_logger.LogDebug("No OpenAI file mapping found for RoxFileId={FileId}; nothing to detach", roxFileId);
			return;
		}

		// Detach from this vector store
		await _openAi.RemoveFileFromVectorStoreAsync(vectorStoreId, fileMapping.ExternalItemId, cancellationToken).ConfigureAwait(false);

		// Check if file is still referenced by other pools
		bool stillReferenced = await _db.FileKnowledgePools.AnyAsync(x => x.RoxFileId == roxFileId, cancellationToken).ConfigureAwait(false);

		if (!stillReferenced)
		{
			_logger.LogInformation("OpenAI deleting orphaned file: RoxFileId={RoxFileId}, OpenAiFileId={OpenAiFileId}", roxFileId, fileMapping.ExternalItemId);

			await _openAi.DeleteFileAsync(fileMapping.ExternalItemId, cancellationToken).ConfigureAwait(false);
			_db.ExternalFiles.Remove(fileMapping);
			await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	public async Task HandleKnowledgePoolRemovedAsync(string knowledgePoolId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ArgumentException.ThrowIfNullOrWhiteSpace(knowledgePoolId);

		_logger.LogInformation("OpenAI handling knowledgepool.removed: KnowledgePoolId={KnowledgePoolId}", knowledgePoolId);

		// Delete vector store
		var groupEntity = await _db.ExternalGroups.FirstOrDefaultAsync(x => x.KnowledgePoolId == knowledgePoolId, cancellationToken).ConfigureAwait(false);

		if (groupEntity is not null)
		{
			await _openAi.DeleteVectorStoreAsync(groupEntity.ExternalGroupId, cancellationToken).ConfigureAwait(false);
			_db.ExternalGroups.Remove(groupEntity);
		}

		// Remove all file memberships for this pool
		var memberships = await _db.FileKnowledgePools.Where(x => x.KnowledgePoolId == knowledgePoolId).ToListAsync(cancellationToken).ConfigureAwait(false);

		if (memberships.Count > 0)
		{
			_db.FileKnowledgePools.RemoveRange(memberships);
		}

		await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		// Garbage collect orphaned files
		await DeleteOrphanedFilesAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task HandleFileUpdatedAsync(RoxFile file, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ArgumentNullException.ThrowIfNull(file);

		using var fileLock = await LockFileAsync(file.Id, cancellationToken).ConfigureAwait(false);

		if (file.ContentStream is null)
		{
			_logger.LogError("OpenAI file.updated received without content stream; removing the indexed file and failing. FileId={FileId}", file.Id);
			await RemoveFileEverywhereAsync(file.Id, cancellationToken).ConfigureAwait(false);
			throw new InvalidOperationException($"file.updated for '{file.Id}' contained no content stream; the previously indexed file was removed.");
		}

		var existingMapping = await _db.ExternalFiles.FirstOrDefaultAsync(x => x.RoxFileId == file.Id, cancellationToken).ConfigureAwait(false);

		if (existingMapping is null)
		{
			_logger.LogDebug("OpenAI file.updated for unknown file; uploading as new. FileId={FileId}", file.Id);
			_ = await EnsureFileUploadedAsync(file, cancellationToken).ConfigureAwait(false);
			return;
		}

		// Fast path: identical content (hash match) needs no reindex.
		if (file.DocumentHash == existingMapping.DocumentHash)
		{
			_logger.LogInformation("OpenAI file.updated: content unchanged (hash match); skipping reindex. FileId={FileId}", file.Id);
			return;
		}

		_logger.LogInformation("OpenAI reindexing updated file: FileId={FileId}", file.Id);

		string oldOpenAiFileId = existingMapping.ExternalItemId;
		string oldDocumentHash = existingMapping.DocumentHash;
		string newOpenAiFileId = await UploadFileAsync(file, cancellationToken).ConfigureAwait(false);

		// Swap file in all associated vector stores, then update DB
		var poolIds = await _db
			.FileKnowledgePools.Where(x => x.RoxFileId == file.Id)
			.Select(x => x.KnowledgePoolId)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);

		foreach (var poolId in poolIds)
		{
			var vectorStoreId = await GetVectorStoreIdAsync(poolId, cancellationToken).ConfigureAwait(false);
			await _openAi.RemoveFileFromVectorStoreAsync(vectorStoreId, oldOpenAiFileId, cancellationToken).ConfigureAwait(false);
			await AttachFileToVectorStoreAsync(vectorStoreId, newOpenAiFileId, cancellationToken).ConfigureAwait(false);
		}

		// Delete old file
		await _openAi.DeleteFileAsync(oldOpenAiFileId, cancellationToken).ConfigureAwait(false);

		// Update mapping only after all OpenAI operations succeeded
		existingMapping.ExternalItemId = newOpenAiFileId;
		existingMapping.DocumentHash = file.DocumentHash;
		await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		// Mark the previous version record as superseded so it is no longer mistaken for a valid upload.
		var oldVersion = await _db
			.ExternalFileVersions.FirstOrDefaultAsync(v => v.RoxFileId == file.Id && v.DocumentHash == oldDocumentHash, cancellationToken)
			.ConfigureAwait(false);
		if (oldVersion is not null)
		{
			oldVersion.Status = ExternalFileVersionStatus.Superseded;
			oldVersion.UpdatedAtUtc = DateTimeOffset.UtcNow;
			await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	#region Private Helpers

	/// <summary>
	/// Creates a vector store for the given knowledge pool if none exists, or recovers it if deleted in OpenAI.
	/// Called only from <see cref="HandleKnowledgePoolCreatedAsync"/>.
	/// For file operations use <see cref="GetVectorStoreIdAsync"/> instead, which enforces that the pool was created first.
	/// </summary>
	private async Task<string> EnsureVectorStoreAsync(string knowledgePoolId, CancellationToken ct)
	{
		var existing = await _db.ExternalGroups.FirstOrDefaultAsync(x => x.KnowledgePoolId == knowledgePoolId, ct).ConfigureAwait(false);

		if (existing is not null)
		{
			// Verify store still exists in OpenAI
			var store = await _openAi.GetVectorStoreAsync(existing.ExternalGroupId, ct).ConfigureAwait(false);
			if (store is not null)
			{
				return existing.ExternalGroupId;
			}

			_logger.LogWarning("Vector store mapping exists but store not found in OpenAI; recreating. KnowledgePoolId={KnowledgePoolId}", knowledgePoolId);
		}

		// Create new vector store
		string name = BuildVectorStoreName(knowledgePoolId);
		var created = await _openAi.CreateVectorStoreAsync(name, ct).ConfigureAwait(false);

		if (existing is null)
		{
			_db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = knowledgePoolId, ExternalGroupId = created.Id });
		}
		else
		{
			existing.ExternalGroupId = created.Id;
		}

		await _db.SaveChangesAsync(ct).ConfigureAwait(false);
		return created.Id;
	}

	/// <summary>
	/// Returns the vector store ID for an existing knowledge pool.
	/// Throws if no DB mapping is found — the knowledge pool must be created via
	/// <see cref="HandleKnowledgePoolCreatedAsync"/> before files can be managed.
	/// Recovers transparently if the store was deleted or expired in OpenAI.
	/// </summary>
	private async Task<string> GetVectorStoreIdAsync(string knowledgePoolId, CancellationToken ct)
	{
		var existing = await _db.ExternalGroups.FirstOrDefaultAsync(x => x.KnowledgePoolId == knowledgePoolId, ct).ConfigureAwait(false);

		if (existing is null)
		{
			throw new InvalidOperationException(
				$"No vector store mapping found for KnowledgePoolId={knowledgePoolId}. " + "The knowledge pool must be created before files can be managed."
			);
		}

		// Verify store still exists in OpenAI (may have been deleted externally or expired)
		var store = await _openAi.GetVectorStoreAsync(existing.ExternalGroupId, ct).ConfigureAwait(false);
		if (store is not null)
		{
			return existing.ExternalGroupId;
		}

		// Recovery: recreate the store using the existing DB mapping
		_logger.LogWarning("Vector store not found in OpenAI; recreating. KnowledgePoolId={KnowledgePoolId}", knowledgePoolId);

		var created = await _openAi.CreateVectorStoreAsync(BuildVectorStoreName(knowledgePoolId), ct).ConfigureAwait(false);
		existing.ExternalGroupId = created.Id;
		await _db.SaveChangesAsync(ct).ConfigureAwait(false);
		return created.Id;
	}

	private string BuildVectorStoreName(string knowledgePoolId)
	{
		string name = $"{_options.VectorStoreNamePrefix}-{knowledgePoolId}";
		return name.Length <= MaxVectorStoreNameLength ? name : name[..MaxVectorStoreNameLength];
	}

	private async Task<string> EnsureFileUploadedAsync(RoxFile file, CancellationToken ct)
	{
		var existing = await _db.ExternalFiles.FirstOrDefaultAsync(x => x.RoxFileId == file.Id, ct).ConfigureAwait(false);

		if (existing is not null)
		{
			// Fast path: identical content (hash match) needs no re-upload.
			if (file.DocumentHash == existing.DocumentHash)
			{
				_logger.LogDebug("File content unchanged (hash match); skipping re-upload. RoxFileId={RoxFileId}", file.Id);
				return existing.ExternalItemId;
			}
			_logger.LogWarning("File mapping exists but file not found in OpenAI; re-uploading. RoxFileId={RoxFileId}", file.Id);
		}

		string openAiFileId = await UploadFileAsync(file, ct).ConfigureAwait(false);

		if (existing is null)
		{
			_db.ExternalFiles.Add(
				new ExternalFileEntity
				{
					RoxFileId = file.Id,
					ExternalItemId = openAiFileId,
					DocumentHash = file.DocumentHash,
				}
			);
		}
		else
		{
			existing.ExternalItemId = openAiFileId;
			existing.DocumentHash = file.DocumentHash;
		}

		await _db.SaveChangesAsync(ct).ConfigureAwait(false);
		return openAiFileId;
	}

	private async Task<string> UploadFileAsync(RoxFile file, CancellationToken ct)
	{
		if (file.ContentStream is null)
		{
			throw new InvalidOperationException($"Cannot upload file without content stream: {file.Id}");
		}

		string filename = SanitizeFilename(file.Title);

		// Two-phase commit: write "Uploading" before the API call so a mid-upload crash leaves a recoverable record.
		var version = await _db
			.ExternalFileVersions.FirstOrDefaultAsync(v => v.RoxFileId == file.Id && v.DocumentHash == file.DocumentHash, ct)
			.ConfigureAwait(false);

		if (version?.Status == ExternalFileVersionStatus.Uploaded && version.ExternalItemId is not null)
		{
			var remote = await _openAi.GetFileAsync(version.ExternalItemId, ct).ConfigureAwait(false);
			if (remote is not null)
			{
				_logger.LogDebug("Reusing previously completed upload. RoxFileId={RoxFileId}, Hash={Hash}", file.Id, file.DocumentHash);
				return version.ExternalItemId;
			}

			// File was deleted from OpenAI externally; reset and re-upload.
			_logger.LogWarning("Version record found but file missing in OpenAI; re-uploading. RoxFileId={RoxFileId}", file.Id);
			version.ExternalItemId = null;
			version.Status = ExternalFileVersionStatus.Uploading;
			version.UpdatedAtUtc = DateTimeOffset.UtcNow;
			await _db.SaveChangesAsync(ct).ConfigureAwait(false);
		}
		else if (version is null)
		{
			version = new ExternalFileVersionEntity
			{
				RoxFileId = file.Id,
				DocumentHash = file.DocumentHash,
				Filename = filename,
				Status = ExternalFileVersionStatus.Uploading,
			};
			_db.ExternalFileVersions.Add(version);
			await _db.SaveChangesAsync(ct).ConfigureAwait(false);
		}
		// else Status=Uploading: previous attempt failed before OpenAI responded — fall through to re-upload.

		// Ensure stream is seekable for upload
		Stream uploadStream = file.ContentStream;
		MemoryStream? buffer = null;

		try
		{
			if (!file.ContentStream.CanSeek)
			{
				buffer = new MemoryStream();
				await file.ContentStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
				buffer.Position = 0;
				uploadStream = buffer;
			}
			else
			{
				file.ContentStream.Position = 0;
			}

			if (uploadStream.CanSeek)
			{
				const long MaxFileSizeBytes = 512L * 1024 * 1024; // 512 MB
				if (uploadStream.Length > MaxFileSizeBytes)
				{
					_logger.LogWarning(
						"File exceeds OpenAI 512MB limit: RoxFileId={RoxFileId}, Filename={Filename}, Size={SizeMB:F2}MB",
						file.Id,
						filename,
						uploadStream.Length / (1024.0 * 1024.0)
					);
				}
			}

			_logger.LogDebug("Uploading file to OpenAI: RoxFileId={RoxFileId}, Filename={Filename}", file.Id, filename);
			var uploaded = await _openAi.UploadFileAsync(uploadStream, filename, ct).ConfigureAwait(false);

			if (version is not null)
			{
				version.ExternalItemId = uploaded.Id;
				version.Status = ExternalFileVersionStatus.Uploaded;
				version.UpdatedAtUtc = DateTimeOffset.UtcNow;
				await _db.SaveChangesAsync(ct).ConfigureAwait(false);
			}

			return uploaded.Id;
		}
		finally
		{
			if (buffer is not null)
			{
				await buffer.DisposeAsync().ConfigureAwait(false);
			}
		}
	}

	private async Task AttachFileToVectorStoreAsync(string vectorStoreId, string openAiFileId, CancellationToken ct)
	{
		try
		{
			var file = await _openAi.AddFileToVectorStoreAsync(vectorStoreId, openAiFileId, ct).ConfigureAwait(false);

			_logger.LogDebug(
				"File attached to vector store: VectorStoreId={VectorStoreId}, FileId={FileId}, Status={Status}",
				vectorStoreId,
				openAiFileId,
				file.Status
			);
		}
		catch (ClientResultException ex) when (ex.Status == 409)
		{
			// File already attached - idempotent
			_logger.LogDebug("File already attached to vector store: VectorStoreId={VectorStoreId}, FileId={FileId}", vectorStoreId, openAiFileId);
		}
	}

	private async Task DeleteOrphanedFilesAsync(CancellationToken ct)
	{
		// Single query to find orphaned files (files with no pool memberships)
		var orphanedFiles = await _db
			.ExternalFiles.Where(ef => !_db.FileKnowledgePools.Any(fkp => fkp.RoxFileId == ef.RoxFileId))
			.ToListAsync(ct)
			.ConfigureAwait(false);

		foreach (var orphan in orphanedFiles)
		{
			_logger.LogInformation(
				"OpenAI deleting orphaned file: RoxFileId={RoxFileId}, OpenAiFileId={OpenAiFileId}",
				orphan.RoxFileId,
				orphan.ExternalItemId
			);

			await _openAi.DeleteFileAsync(orphan.ExternalItemId, ct).ConfigureAwait(false);
			_db.ExternalFiles.Remove(orphan);
		}

		if (orphanedFiles.Count > 0)
		{
			await _db.SaveChangesAsync(ct).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Fully removes a file: detaches it from every associated vector store, deletes it in OpenAI,
	/// and drops all local records (mapping, pool memberships, and version history). No-ops gracefully
	/// when no mapping exists. Caller must hold the per-file lock.
	/// </summary>
	private async Task RemoveFileEverywhereAsync(string roxFileId, CancellationToken ct)
	{
		var mapping = await _db.ExternalFiles.FirstOrDefaultAsync(x => x.RoxFileId == roxFileId, ct).ConfigureAwait(false);

		if (mapping is not null)
		{
			var poolIds = await _db
				.FileKnowledgePools.Where(x => x.RoxFileId == roxFileId)
				.Select(x => x.KnowledgePoolId)
				.ToListAsync(ct)
				.ConfigureAwait(false);

			foreach (var poolId in poolIds)
			{
				var vectorStoreId = await GetVectorStoreIdAsync(poolId, ct).ConfigureAwait(false);
				await _openAi.RemoveFileFromVectorStoreAsync(vectorStoreId, mapping.ExternalItemId, ct).ConfigureAwait(false);
			}

			await _openAi.DeleteFileAsync(mapping.ExternalItemId, ct).ConfigureAwait(false);

			_db.ExternalFiles.Remove(mapping);

			var memberships = await _db.FileKnowledgePools.Where(x => x.RoxFileId == roxFileId).ToListAsync(ct).ConfigureAwait(false);
			_db.FileKnowledgePools.RemoveRange(memberships);
		}

		var versions = await _db.ExternalFileVersions.Where(v => v.RoxFileId == roxFileId).ToListAsync(ct).ConfigureAwait(false);
		_db.ExternalFileVersions.RemoveRange(versions);

		await _db.SaveChangesAsync(ct).ConfigureAwait(false);
	}

	private string SanitizeFilename(string? title)
	{
		if (string.IsNullOrWhiteSpace(title))
		{
			_logger.LogWarning("File title is empty or whitespace; using default filename 'file'.");
			return "file";
		}

		var sb = new StringBuilder(title.Length);
		foreach (char ch in title)
		{
			sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' or ' ' ? ch : '_');
		}

		string result = sb.ToString().Trim();

		return result;
	}

	#endregion
}
