#pragma warning disable OPENAI001 // OpenAI experimental types

using OpenAI.Files;
using OpenAI.VectorStores;

namespace AiHub.Connector.ExternalConnectors.OpenAiVectorStoreConnector;

public interface IOpenAiVectorStoreGateway
{
	Task<VectorStore> CreateVectorStoreAsync(string name, CancellationToken ct = default);
	Task<VectorStore?> GetVectorStoreAsync(string vectorStoreId, CancellationToken ct = default);
	Task DeleteVectorStoreAsync(string vectorStoreId, CancellationToken ct = default);

	Task<OpenAIFile> UploadFileAsync(Stream content, string filename, CancellationToken ct = default);
	Task<OpenAIFile?> GetFileAsync(string fileId, CancellationToken ct = default);
	Task DeleteFileAsync(string fileId, CancellationToken ct = default);

	Task<VectorStoreFile> AddFileToVectorStoreAsync(string vectorStoreId, string fileId, CancellationToken ct = default);
	Task RemoveFileFromVectorStoreAsync(string vectorStoreId, string fileId, CancellationToken ct = default);
}
