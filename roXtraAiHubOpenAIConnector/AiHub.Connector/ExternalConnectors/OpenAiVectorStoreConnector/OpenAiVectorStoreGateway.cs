#pragma warning disable OPENAI001 // OpenAI experimental types

using System.ClientModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Files;
using OpenAI.VectorStores;

namespace AiHub.Connector.ExternalConnectors.OpenAiVectorStoreConnector;

/// <summary>
/// Gateway wrapping OpenAI .NET SDK (https://github.com/openai/openai-dotnet) for vector store operations.
/// Uses VectorStoreClient and OpenAIFileClient from the OpenAI NuGet package.
/// </summary>
public sealed class OpenAiVectorStoreGateway : IOpenAiVectorStoreGateway
{
	private readonly OpenAIFileClient _files;
	private readonly VectorStoreClient _stores;
	private readonly OpenAiVectorStoreOptions _options;
	private readonly ILogger<OpenAiVectorStoreGateway> _logger;

	public OpenAiVectorStoreGateway(IOptions<OpenAiVectorStoreOptions> options, ILogger<OpenAiVectorStoreGateway> logger)
	{
		ArgumentNullException.ThrowIfNull(options?.Value);
		_options = options.Value;
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		OpenAIClient client = CreateClient(_options);
		_files = client.GetOpenAIFileClient();
		_stores = client.GetVectorStoreClient();
	}

	private static OpenAIClient CreateClient(OpenAiVectorStoreOptions opts)
	{
		if (string.IsNullOrWhiteSpace(opts.BaseUrl))
		{
			return new OpenAIClient(opts.ApiKey);
		}

		if (!opts.BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException($"BaseUrl must use HTTPS to protect the API key in transit. Got: {opts.BaseUrl}");
		}

		var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(opts.BaseUrl, UriKind.Absolute) };
		return new OpenAIClient(new ApiKeyCredential(opts.ApiKey), clientOptions);
	}

	public async Task<VectorStore> CreateVectorStoreAsync(string name, CancellationToken ct = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		var creationOptions = new VectorStoreCreationOptions { Name = name };

		// Configure chunking strategy if using static chunking
		if (string.Equals(_options.ChunkingStrategy, "static", StringComparison.OrdinalIgnoreCase))
		{
			creationOptions.ChunkingStrategy = FileChunkingStrategy.CreateStaticStrategy(_options.MaxChunkSizeTokens, _options.ChunkOverlapTokens);
		}

		ClientResult<VectorStore> result = await _stores.CreateVectorStoreAsync(creationOptions, ct).ConfigureAwait(false);

		_logger.LogInformation(
			"Created vector store: Id={VectorStoreId}, Name={Name}, Status={Status}",
			result.Value.Id,
			result.Value.Name,
			result.Value.Status
		);

		return result.Value;
	}

	public async Task<VectorStore?> GetVectorStoreAsync(string vectorStoreId, CancellationToken ct = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(vectorStoreId);

		try
		{
			ClientResult<VectorStore> result = await _stores.GetVectorStoreAsync(vectorStoreId, ct).ConfigureAwait(false);
			return result.Value;
		}
		catch (ClientResultException ex) when (ex.Status == 404)
		{
			return null;
		}
	}

	public async Task DeleteVectorStoreAsync(string vectorStoreId, CancellationToken ct = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(vectorStoreId);

		try
		{
			await _stores.DeleteVectorStoreAsync(vectorStoreId, ct).ConfigureAwait(false);
			_logger.LogInformation("Deleted vector store: {VectorStoreId}", vectorStoreId);
		}
		catch (ClientResultException ex) when (ex.Status == 404)
		{
			_logger.LogDebug("Vector store already deleted or not found: {VectorStoreId}", vectorStoreId);
		}
	}

	public async Task<OpenAIFile> UploadFileAsync(Stream content, string filename, CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(content);
		ArgumentException.ThrowIfNullOrWhiteSpace(filename);

		// Files for vector stores must use purpose "assistants"
		return await _files.UploadFileAsync(content, filename, FileUploadPurpose.Assistants, cancellationToken: ct).ConfigureAwait(false);
	}

	public async Task<OpenAIFile?> GetFileAsync(string fileId, CancellationToken ct = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

		try
		{
			return await _files.GetFileAsync(fileId, cancellationToken: ct).ConfigureAwait(false);
		}
		catch (ClientResultException ex) when (ex.Status == 404)
		{
			return null;
		}
	}

	public async Task DeleteFileAsync(string fileId, CancellationToken ct = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

		try
		{
			await _files.DeleteFileAsync(fileId, cancellationToken: ct).ConfigureAwait(false);
			_logger.LogDebug("Deleted file: {FileId}", fileId);
		}
		catch (ClientResultException ex) when (ex.Status == 404)
		{
			_logger.LogDebug("File already deleted or not found: {FileId}", fileId);
		}
	}

	public async Task<VectorStoreFile> AddFileToVectorStoreAsync(string vectorStoreId, string fileId, CancellationToken ct = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(vectorStoreId);
		ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

		// CRITICAL: Do NOT wait for indexing - just attach and return immediately
		// This matches the previous working version's behavior
		// Indexing happens asynchronously on OpenAI's servers (takes 1-5 minutes)
		ClientResult<VectorStoreFile> result = await _stores.AddFileToVectorStoreAsync(vectorStoreId, fileId, ct).ConfigureAwait(false);

		return result.Value;
	}

	public async Task RemoveFileFromVectorStoreAsync(string vectorStoreId, string fileId, CancellationToken ct = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(vectorStoreId);
		ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

		try
		{
			// Note: Removing files from vector store is eventually consistent
			// Search results may still include content from removed files briefly
			await _stores.RemoveFileFromVectorStoreAsync(vectorStoreId, fileId, ct).ConfigureAwait(false);
			_logger.LogDebug("Removed file from vector store: VectorStoreId={VectorStoreId}, FileId={FileId}", vectorStoreId, fileId);
		}
		catch (ClientResultException ex) when (ex.Status == 404)
		{
			_logger.LogDebug("File already removed or not found in vector store: VectorStoreId={VectorStoreId}, FileId={FileId}", vectorStoreId, fileId);
		}
	}
}
