#pragma warning disable OPENAI001 // OpenAI experimental types

using System.ClientModel.Primitives;
using AiHub.Connector.Data;
using AiHub.Connector.Data.Entities;
using AiHub.Connector.ExternalConnectors.OpenAiVectorStoreConnector;
using AiHub.Connector.Roxtra;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using OpenAI.Files;
using OpenAI.VectorStores;
using Xunit;
using Sut = AiHub.Connector.ExternalConnectors.OpenAiVectorStoreConnector.OpenAiVectorStoreConnector;

namespace AiHub.Connector.Tests.ExternalConnectors.OpenAiVectorStoreConnector;

public class OpenAiVectorStoreConnectorTests
{
	#region Test Setup

	private static ConnectorDbContext CreateDb()
	{
		var opts = new DbContextOptionsBuilder<ConnectorDbContext>().UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
		return new ConnectorDbContext(opts);
	}

	private static OpenAiVectorStoreOptions CreateOptions()
	{
		return new OpenAiVectorStoreOptions { ApiKey = "test-api-key", VectorStoreNamePrefix = "test" };
	}

	private static (Sut sut, Mock<IOpenAiVectorStoreGateway> gateway, ConnectorDbContext db) CreateSut(OpenAiVectorStoreOptions? options = null)
	{
		var db = CreateDb();
		var gateway = new Mock<IOpenAiVectorStoreGateway>(MockBehavior.Strict);
		var logger = Mock.Of<ILogger<Sut>>();
		var opts = Options.Create(options ?? CreateOptions());
		var sut = new Sut(logger, db, gateway.Object, opts);
		return (sut, gateway, db);
	}

	// OpenAI SDK 2.x types are unmockable via Moq (no parameterless ctor, non-virtual props) — use ModelReaderWriter.

	private static VectorStore CreateMockVectorStore(string id = "vs_test", string name = "test")
	{
		// The connector only checks null vs non-null — no properties are accessed on the instance.
		var json =
			$@"{{""id"":""{id}"",""object"":""vector_store"",""created_at"":1234567890,""name"":""{name}"",""status"":""completed"",""file_counts"":{{""in_progress"":0,""completed"":0,""failed"":0,""cancelled"":0,""total"":0}},""usage_bytes"":0}}";
		return ModelReaderWriter.Read<VectorStore>(BinaryData.FromString(json))!;
	}

	private static OpenAIFile CreateMockOpenAIFile(string id, string filename)
	{
		// The connector reads .Id from the uploaded file to persist as the OpenAI file reference.
		var json = $@"{{""id"":""{id}"",""object"":""file"",""bytes"":100,""created_at"":1234567890,""filename"":""{filename}"",""purpose"":""assistants""}}";
		return ModelReaderWriter.Read<OpenAIFile>(BinaryData.FromString(json))!;
	}

	private static VectorStoreFile CreateMockVectorStoreFile(string fileId, VectorStoreFileStatus status)
	{
		// The connector reads .Status only for a debug log line — no control flow depends on it.
		var statusStr = status == VectorStoreFileStatus.InProgress ? "in_progress" : status.ToString().ToLowerInvariant();
		var json =
			$@"{{""id"":""{fileId}"",""object"":""vector_store.file"",""created_at"":1234567890,""vector_store_id"":""vs_test"",""status"":""{statusStr}"",""usage_bytes"":0}}";
		return ModelReaderWriter.Read<VectorStoreFile>(BinaryData.FromString(json))!;
	}

	#endregion

	#region HandleKnowledgePoolCreatedAsync Tests

	[Fact]
	public async Task HandleKnowledgePoolCreatedAsync_CreatesVectorStore_AndSavesMapping()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		var vectorStore = CreateMockVectorStore("vs_123", "test-kp-1");
		gateway.Setup(g => g.GetVectorStoreAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((VectorStore?)null);
		gateway.Setup(g => g.CreateVectorStoreAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(vectorStore);

		await sut.HandleKnowledgePoolCreatedAsync("kp-1", CancellationToken.None);

		gateway.Verify(g => g.CreateVectorStoreAsync(It.Is<string>(n => n.StartsWith("test-")), It.IsAny<CancellationToken>()), Times.Once);
		Assert.Single(db.ExternalGroups);
		Assert.Equal("kp-1", db.ExternalGroups.First().KnowledgePoolId);
		Assert.Equal("vs_123", db.ExternalGroups.First().ExternalGroupId);
	}

	[Fact]
	public async Task HandleKnowledgePoolCreatedAsync_ReusesExistingVectorStore_WhenMappingExists()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		// Pre-existing mapping
		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "vs_existing" });
		await db.SaveChangesAsync();

		var vectorStore = CreateMockVectorStore("vs_existing", "test-kp-1");
		gateway.Setup(g => g.GetVectorStoreAsync("vs_existing", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStore);

		await sut.HandleKnowledgePoolCreatedAsync("kp-1", CancellationToken.None);

		gateway.Verify(g => g.CreateVectorStoreAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task HandleKnowledgePoolCreatedAsync_RecreatesVectorStore_WhenMappingExistsButStoreDeleted()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		// Pre-existing mapping but vector store was deleted
		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "vs_deleted" });
		await db.SaveChangesAsync();

		gateway.Setup(g => g.GetVectorStoreAsync("vs_deleted", It.IsAny<CancellationToken>())).ReturnsAsync((VectorStore?)null);

		var newVectorStore = CreateMockVectorStore("vs_new", "test-kp-1");
		gateway.Setup(g => g.CreateVectorStoreAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(newVectorStore);

		await sut.HandleKnowledgePoolCreatedAsync("kp-1", CancellationToken.None);

		gateway.Verify(g => g.CreateVectorStoreAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
		Assert.Equal("vs_new", db.ExternalGroups.First().ExternalGroupId);
	}

	[Fact]
	public async Task HandleKnowledgePoolCreatedAsync_ThrowsOnNullKnowledgePoolId()
	{
		var (sut, _, db) = CreateSut();
		await using var dbScope = db;

		// ArgumentNullException is a subclass of ArgumentException; use ThrowsAnyAsync to accept either.
		await Assert.ThrowsAnyAsync<ArgumentException>(() => sut.HandleKnowledgePoolCreatedAsync(null!, CancellationToken.None));
	}

	#endregion

	#region HandleKnowledgePoolFileAddedAsync Tests

	[Fact]
	public async Task HandleKnowledgePoolFileAddedAsync_UploadsFile_AndAttachesToVectorStore()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		// ExternalGroup required by GetVectorStoreIdAsync; null from GetVectorStoreAsync triggers store-recreation path.
		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "vs_123" });
		await db.SaveChangesAsync();

		var vectorStore = CreateMockVectorStore("vs_123", "test-kp-1");
		var openAiFile = CreateMockOpenAIFile("file_abc", "doc");
		var vectorStoreFile = CreateMockVectorStoreFile("file_abc", VectorStoreFileStatus.Completed);

		gateway.Setup(g => g.GetVectorStoreAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((VectorStore?)null);
		gateway.Setup(g => g.CreateVectorStoreAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(vectorStore);
		gateway.Setup(g => g.GetFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((OpenAIFile?)null);
		gateway.Setup(g => g.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(openAiFile);
		gateway.Setup(g => g.AddFileToVectorStoreAsync("vs_123", "file_abc", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStoreFile);

		var file = new RoxFile("rox-1", "doc") { ContentStream = new MemoryStream([65, 66, 67]) };
		await sut.HandleKnowledgePoolFileAddedAsync("kp-1", file, CancellationToken.None);

		gateway.Verify(g => g.UploadFileAsync(It.IsAny<Stream>(), "doc.pdf", It.IsAny<CancellationToken>()), Times.Once);
		gateway.Verify(g => g.AddFileToVectorStoreAsync("vs_123", "file_abc", It.IsAny<CancellationToken>()), Times.Once);

		Assert.Single(db.ExternalFiles);
		Assert.Equal("rox-1", db.ExternalFiles.First().RoxFileId);
		Assert.Equal("file_abc", db.ExternalFiles.First().ExternalItemId);

		Assert.Single(db.FileKnowledgePools);
		Assert.Equal("rox-1", db.FileKnowledgePools.First().RoxFileId);
		Assert.Equal("kp-1", db.FileKnowledgePools.First().KnowledgePoolId);
	}

	[Fact]
	public async Task HandleKnowledgePoolFileAddedAsync_RestoresMissingVectorStore_AndReattachesExistingFiles()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "vs_missing" });
		db.ExternalFiles.Add(new ExternalFileEntity { RoxFileId = "rox-existing", ExternalItemId = "file_existing" });
		db.FileKnowledgePools.Add(new FileKnowledgePoolEntity { RoxFileId = "rox-existing", KnowledgePoolId = "kp-1" });
		await db.SaveChangesAsync();

		var restoredVectorStore = CreateMockVectorStore("vs_restored", "test-kp-1");
		var uploadedFile = CreateMockOpenAIFile("file_new", "doc.pdf");
		var restoredExistingFile = CreateMockVectorStoreFile("file_existing", VectorStoreFileStatus.Completed);
		var attachedNewFile = CreateMockVectorStoreFile("file_new", VectorStoreFileStatus.Completed);

		gateway.Setup(g => g.GetVectorStoreAsync("vs_missing", It.IsAny<CancellationToken>())).ReturnsAsync((VectorStore?)null);
		gateway.Setup(g => g.CreateVectorStoreAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(restoredVectorStore);
		gateway.Setup(g => g.UploadFileAsync(It.IsAny<Stream>(), "doc.pdf", It.IsAny<CancellationToken>())).ReturnsAsync(uploadedFile);
		gateway.Setup(g => g.AddFileToVectorStoreAsync("vs_restored", "file_existing", It.IsAny<CancellationToken>())).ReturnsAsync(restoredExistingFile);
		gateway.Setup(g => g.AddFileToVectorStoreAsync("vs_restored", "file_new", It.IsAny<CancellationToken>())).ReturnsAsync(attachedNewFile);

		var file = new RoxFile("rox-new", "doc.pdf") { ContentStream = new MemoryStream([65, 66, 67]) };
		await sut.HandleKnowledgePoolFileAddedAsync("kp-1", file, CancellationToken.None);

		gateway.Verify(g => g.CreateVectorStoreAsync(It.Is<string>(n => n.StartsWith("test-")), It.IsAny<CancellationToken>()), Times.Once);
		gateway.Verify(g => g.AddFileToVectorStoreAsync("vs_restored", "file_existing", It.IsAny<CancellationToken>()), Times.Once);
		gateway.Verify(g => g.AddFileToVectorStoreAsync("vs_restored", "file_new", It.IsAny<CancellationToken>()), Times.Once);

		Assert.Equal("vs_restored", db.ExternalGroups.Single().ExternalGroupId);
		Assert.Equal(2, db.FileKnowledgePools.Count());
		Assert.Contains(db.FileKnowledgePools, x => x.RoxFileId == "rox-existing" && x.KnowledgePoolId == "kp-1");
		Assert.Contains(db.FileKnowledgePools, x => x.RoxFileId == "rox-new" && x.KnowledgePoolId == "kp-1");
	}

	[Fact]
	public async Task HandleKnowledgePoolFileAddedAsync_ReusesExistingFile_WhenAlreadyUploaded()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		// ExternalGroup required by GetVectorStoreIdAsync; ExternalFile represents the pre-existing upload.
		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "vs_123" });
		db.ExternalFiles.Add(new ExternalFileEntity { RoxFileId = "rox-1", ExternalItemId = "file_existing" });
		await db.SaveChangesAsync();

		var vectorStore = CreateMockVectorStore("vs_123", "test-kp-1");
		var openAiFile = CreateMockOpenAIFile("file_existing", "doc.txt");
		var vectorStoreFile = CreateMockVectorStoreFile("file_existing", VectorStoreFileStatus.Completed);

		gateway.Setup(g => g.GetVectorStoreAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((VectorStore?)null);
		gateway.Setup(g => g.CreateVectorStoreAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(vectorStore);
		gateway.Setup(g => g.GetFileAsync("file_existing", It.IsAny<CancellationToken>())).ReturnsAsync(openAiFile);
		gateway.Setup(g => g.AddFileToVectorStoreAsync("vs_123", "file_existing", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStoreFile);

		var file = new RoxFile("rox-1", "doc.txt") { ContentStream = new MemoryStream([65, 66, 67]) };
		await sut.HandleKnowledgePoolFileAddedAsync("kp-1", file, CancellationToken.None);

		gateway.Verify(g => g.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
		gateway.Verify(g => g.AddFileToVectorStoreAsync("vs_123", "file_existing", It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task HandleKnowledgePoolFileAddedAsync_ThrowsOnNullContentStream()
	{
		var (sut, _, db) = CreateSut();
		await using var dbScope = db;

		var file = new RoxFile("rox-1", "doc.txt") { ContentStream = null };

		await Assert.ThrowsAsync<ArgumentException>(() => sut.HandleKnowledgePoolFileAddedAsync("kp-1", file, CancellationToken.None));
	}

	[Fact]
	public async Task HandleKnowledgePoolFileAddedAsync_DoesNotDuplicateMembership()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		// Pre-existing mappings
		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "vs_123" });
		db.ExternalFiles.Add(new ExternalFileEntity { RoxFileId = "rox-1", ExternalItemId = "file_abc" });
		db.FileKnowledgePools.Add(new FileKnowledgePoolEntity { RoxFileId = "rox-1", KnowledgePoolId = "kp-1" });
		await db.SaveChangesAsync();

		var vectorStore = CreateMockVectorStore("vs_123", "test-kp-1");
		var openAiFile = CreateMockOpenAIFile("file_abc", "doc.txt");
		var vectorStoreFile = CreateMockVectorStoreFile("file_abc", VectorStoreFileStatus.Completed);

		gateway.Setup(g => g.GetVectorStoreAsync("vs_123", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStore);
		gateway.Setup(g => g.GetFileAsync("file_abc", It.IsAny<CancellationToken>())).ReturnsAsync(openAiFile);
		gateway.Setup(g => g.AddFileToVectorStoreAsync("vs_123", "file_abc", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStoreFile);

		var file = new RoxFile("rox-1", "doc.txt") { ContentStream = new MemoryStream([65, 66, 67]) };
		await sut.HandleKnowledgePoolFileAddedAsync("kp-1", file, CancellationToken.None);

		Assert.Single(db.FileKnowledgePools);
	}

	[Fact]
	public async Task HandleKnowledgePoolFileAddedAsync_SkipsUpload_WhenDocumentHashMatchesExistingFile()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "vs_123" });
		db.ExternalFiles.Add(
			new ExternalFileEntity
			{
				RoxFileId = "rox-1",
				ExternalItemId = "file_existing",
				DocumentHash = "hash1",
			}
		);
		await db.SaveChangesAsync();

		var vectorStore = CreateMockVectorStore("vs_123", "test-kp-1");
		var vectorStoreFile = CreateMockVectorStoreFile("file_existing", VectorStoreFileStatus.Completed);

		gateway.Setup(g => g.GetVectorStoreAsync("vs_123", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStore);
		gateway.Setup(g => g.AddFileToVectorStoreAsync("vs_123", "file_existing", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStoreFile);

		var file = new RoxFile("rox-1", "doc.txt") { ContentStream = new MemoryStream([65, 66, 67]), DocumentHash = "hash1" };
		await sut.HandleKnowledgePoolFileAddedAsync("kp-1", file, CancellationToken.None);

		// Hash match: neither a file lookup nor an upload should happen
		gateway.Verify(g => g.GetFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
		gateway.Verify(g => g.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task HandleKnowledgePoolFileAddedAsync_ReusesVersion_WhenUploadedVersionExistsInOpenAI()
	{
		// Crash after OpenAI upload but before ExternalFiles SaveChanges: version record exists, file must be reused.
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "vs_123" });
		db.ExternalFileVersions.Add(
			new ExternalFileVersionEntity
			{
				RoxFileId = "rox-1",
				DocumentHash = "hash1",
				Filename = "doc.txt",
				ExternalItemId = "file_existing",
				Status = ExternalFileVersionStatus.Uploaded,
			}
		);
		await db.SaveChangesAsync();

		var vectorStore = CreateMockVectorStore("vs_123", "test-kp-1");
		var openAiFile = CreateMockOpenAIFile("file_existing", "doc.txt");
		var vectorStoreFile = CreateMockVectorStoreFile("file_existing", VectorStoreFileStatus.Completed);

		gateway.Setup(g => g.GetVectorStoreAsync("vs_123", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStore);
		gateway.Setup(g => g.GetFileAsync("file_existing", It.IsAny<CancellationToken>())).ReturnsAsync(openAiFile);
		gateway.Setup(g => g.AddFileToVectorStoreAsync("vs_123", "file_existing", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStoreFile);

		var file = new RoxFile("rox-1", "doc.txt") { ContentStream = new MemoryStream([65, 66, 67]), DocumentHash = "hash1" };
		await sut.HandleKnowledgePoolFileAddedAsync("kp-1", file, CancellationToken.None);

		gateway.Verify(g => g.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
		gateway.Verify(g => g.GetFileAsync("file_existing", It.IsAny<CancellationToken>()), Times.Once);
		Assert.Equal("file_existing", db.ExternalFiles.First().ExternalItemId);
	}

	[Fact]
	public async Task HandleKnowledgePoolFileAddedAsync_Reuploads_WhenVersionIsUploading()
	{
		// Crash before OpenAI responded: "Uploading" record left behind — retry must re-upload.
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "vs_123" });
		db.ExternalFileVersions.Add(
			new ExternalFileVersionEntity
			{
				RoxFileId = "rox-1",
				DocumentHash = "hash1",
				Filename = "doc.txt",
				Status = ExternalFileVersionStatus.Uploading,
			}
		);
		await db.SaveChangesAsync();

		var vectorStore = CreateMockVectorStore("vs_123", "test-kp-1");
		var newFile = CreateMockOpenAIFile("file_new", "doc.txt");
		var vectorStoreFile = CreateMockVectorStoreFile("file_new", VectorStoreFileStatus.Completed);

		gateway.Setup(g => g.GetVectorStoreAsync("vs_123", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStore);
		gateway.Setup(g => g.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(newFile);
		gateway.Setup(g => g.AddFileToVectorStoreAsync("vs_123", "file_new", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStoreFile);

		var file = new RoxFile("rox-1", "doc.txt") { ContentStream = new MemoryStream([65, 66, 67]), DocumentHash = "hash1" };
		await sut.HandleKnowledgePoolFileAddedAsync("kp-1", file, CancellationToken.None);

		gateway.Verify(g => g.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

		var version = db.ExternalFileVersions.First();
		Assert.Equal(ExternalFileVersionStatus.Uploaded, version.Status);
		Assert.Equal("file_new", version.ExternalItemId);
	}

	#endregion

	#region HandleKnowledgePoolFileRemovedAsync Tests

	[Fact]
	public async Task HandleKnowledgePoolFileRemovedAsync_DetachesFile_AndDeletesIfOrphaned()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		// Setup: file attached to only one knowledge pool
		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "vs_123" });
		db.ExternalFiles.Add(new ExternalFileEntity { RoxFileId = "rox-1", ExternalItemId = "file_abc" });
		db.FileKnowledgePools.Add(new FileKnowledgePoolEntity { RoxFileId = "rox-1", KnowledgePoolId = "kp-1" });
		await db.SaveChangesAsync();

		var vectorStore = CreateMockVectorStore("vs_123", "test-kp-1");

		gateway.Setup(g => g.GetVectorStoreAsync("vs_123", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStore);
		gateway.Setup(g => g.RemoveFileFromVectorStoreAsync("vs_123", "file_abc", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
		gateway.Setup(g => g.DeleteFileAsync("file_abc", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		await sut.HandleKnowledgePoolFileRemovedAsync("kp-1", "rox-1", CancellationToken.None);

		gateway.Verify(g => g.RemoveFileFromVectorStoreAsync("vs_123", "file_abc", It.IsAny<CancellationToken>()), Times.Once);
		gateway.Verify(g => g.DeleteFileAsync("file_abc", It.IsAny<CancellationToken>()), Times.Once);

		Assert.Empty(db.FileKnowledgePools);
		Assert.Empty(db.ExternalFiles);
	}

	[Fact]
	public async Task HandleKnowledgePoolFileRemovedAsync_DetachesFile_ButKeepsIfStillReferenced()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		// Setup: file attached to two knowledge pools
		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "vs_123" });
		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-2", ExternalGroupId = "vs_456" });
		db.ExternalFiles.Add(new ExternalFileEntity { RoxFileId = "rox-1", ExternalItemId = "file_abc" });
		db.FileKnowledgePools.Add(new FileKnowledgePoolEntity { RoxFileId = "rox-1", KnowledgePoolId = "kp-1" });
		db.FileKnowledgePools.Add(new FileKnowledgePoolEntity { RoxFileId = "rox-1", KnowledgePoolId = "kp-2" });
		await db.SaveChangesAsync();

		var vectorStore = CreateMockVectorStore("vs_123", "test-kp-1");

		gateway.Setup(g => g.GetVectorStoreAsync("vs_123", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStore);
		gateway.Setup(g => g.RemoveFileFromVectorStoreAsync("vs_123", "file_abc", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		await sut.HandleKnowledgePoolFileRemovedAsync("kp-1", "rox-1", CancellationToken.None);

		gateway.Verify(g => g.RemoveFileFromVectorStoreAsync("vs_123", "file_abc", It.IsAny<CancellationToken>()), Times.Once);
		gateway.Verify(g => g.DeleteFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

		Assert.Single(db.FileKnowledgePools);
		Assert.Single(db.ExternalFiles);
	}

	[Fact]
	public async Task HandleKnowledgePoolFileRemovedAsync_HandlesNoMapping_Gracefully()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "vs_123" });
		await db.SaveChangesAsync();

		var vectorStore = CreateMockVectorStore("vs_123", "test-kp-1");
		gateway.Setup(g => g.GetVectorStoreAsync("vs_123", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStore);

		// No file mapping exists - should complete without error
		await sut.HandleKnowledgePoolFileRemovedAsync("kp-1", "rox-nonexistent", CancellationToken.None);

		gateway.Verify(g => g.RemoveFileFromVectorStoreAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	#endregion

	#region HandleKnowledgePoolRemovedAsync Tests

	[Fact]
	public async Task HandleKnowledgePoolRemovedAsync_DeletesVectorStore_AndCleansUpOrphanedFiles()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		// Setup
		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "vs_123" });
		db.ExternalFiles.Add(new ExternalFileEntity { RoxFileId = "rox-1", ExternalItemId = "file_abc" });
		db.FileKnowledgePools.Add(new FileKnowledgePoolEntity { RoxFileId = "rox-1", KnowledgePoolId = "kp-1" });
		await db.SaveChangesAsync();

		gateway.Setup(g => g.DeleteVectorStoreAsync("vs_123", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
		gateway.Setup(g => g.DeleteFileAsync("file_abc", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		await sut.HandleKnowledgePoolRemovedAsync("kp-1", CancellationToken.None);

		gateway.Verify(g => g.DeleteVectorStoreAsync("vs_123", It.IsAny<CancellationToken>()), Times.Once);
		gateway.Verify(g => g.DeleteFileAsync("file_abc", It.IsAny<CancellationToken>()), Times.Once);

		Assert.Empty(db.ExternalGroups);
		Assert.Empty(db.FileKnowledgePools);
		Assert.Empty(db.ExternalFiles);
	}

	[Fact]
	public async Task HandleKnowledgePoolRemovedAsync_KeepsFilesReferencedByOtherPools()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		// Setup: file attached to two knowledge pools
		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "vs_123" });
		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-2", ExternalGroupId = "vs_456" });
		db.ExternalFiles.Add(new ExternalFileEntity { RoxFileId = "rox-1", ExternalItemId = "file_abc" });
		db.FileKnowledgePools.Add(new FileKnowledgePoolEntity { RoxFileId = "rox-1", KnowledgePoolId = "kp-1" });
		db.FileKnowledgePools.Add(new FileKnowledgePoolEntity { RoxFileId = "rox-1", KnowledgePoolId = "kp-2" });
		await db.SaveChangesAsync();

		gateway.Setup(g => g.DeleteVectorStoreAsync("vs_123", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		await sut.HandleKnowledgePoolRemovedAsync("kp-1", CancellationToken.None);

		gateway.Verify(g => g.DeleteFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

		Assert.Single(db.ExternalGroups);
		Assert.Single(db.FileKnowledgePools);
		Assert.Single(db.ExternalFiles);
	}

	[Fact]
	public async Task HandleKnowledgePoolRemovedAsync_HandlesNoMapping_Gracefully()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		// No mapping exists - should complete without error
		await sut.HandleKnowledgePoolRemovedAsync("kp-nonexistent", CancellationToken.None);

		gateway.Verify(g => g.DeleteVectorStoreAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	#endregion

	#region HandleFileUpdatedAsync Tests

	[Fact]
	public async Task HandleFileUpdatedAsync_ReplacesFile_InAllKnowledgePools()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		// Setup: file in two knowledge pools
		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "vs_123" });
		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-2", ExternalGroupId = "vs_456" });
		db.ExternalFiles.Add(
			new ExternalFileEntity
			{
				RoxFileId = "rox-1",
				ExternalItemId = "file_old",
				DocumentHash = "old_hash",
			}
		);
		db.FileKnowledgePools.Add(new FileKnowledgePoolEntity { RoxFileId = "rox-1", KnowledgePoolId = "kp-1" });
		db.FileKnowledgePools.Add(new FileKnowledgePoolEntity { RoxFileId = "rox-1", KnowledgePoolId = "kp-2" });
		await db.SaveChangesAsync();

		var vectorStore1 = CreateMockVectorStore("vs_123", "test-kp-1");
		var vectorStore2 = CreateMockVectorStore("vs_456", "test-kp-2");
		var newFile = CreateMockOpenAIFile("file_new", "doc.txt");
		var vectorStoreFile = CreateMockVectorStoreFile("file_new", VectorStoreFileStatus.Completed);

		gateway.Setup(g => g.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(newFile);
		gateway.Setup(g => g.GetVectorStoreAsync("vs_123", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStore1);
		gateway.Setup(g => g.GetVectorStoreAsync("vs_456", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStore2);
		gateway.Setup(g => g.RemoveFileFromVectorStoreAsync(It.IsAny<string>(), "file_old", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
		gateway.Setup(g => g.AddFileToVectorStoreAsync(It.IsAny<string>(), "file_new", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStoreFile);
		gateway.Setup(g => g.DeleteFileAsync("file_old", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		var file = new RoxFile("rox-1", "doc.txt") { ContentStream = new MemoryStream([65, 66, 67]), DocumentHash = "new_hash" };
		await sut.HandleFileUpdatedAsync(file, CancellationToken.None);

		gateway.Verify(g => g.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
		gateway.Verify(g => g.RemoveFileFromVectorStoreAsync("vs_123", "file_old", It.IsAny<CancellationToken>()), Times.Once);
		gateway.Verify(g => g.RemoveFileFromVectorStoreAsync("vs_456", "file_old", It.IsAny<CancellationToken>()), Times.Once);
		gateway.Verify(g => g.AddFileToVectorStoreAsync("vs_123", "file_new", It.IsAny<CancellationToken>()), Times.Once);
		gateway.Verify(g => g.AddFileToVectorStoreAsync("vs_456", "file_new", It.IsAny<CancellationToken>()), Times.Once);
		gateway.Verify(g => g.DeleteFileAsync("file_old", It.IsAny<CancellationToken>()), Times.Once);

		Assert.Equal("file_new", db.ExternalFiles.First().ExternalItemId);
	}

	[Fact]
	public async Task HandleFileUpdatedAsync_RemovesFile_WhenNoContentStream()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "vs_123" });
		db.ExternalFiles.Add(
			new ExternalFileEntity
			{
				RoxFileId = "rox-1",
				ExternalItemId = "file_old",
				DocumentHash = "hash1",
			}
		);
		db.FileKnowledgePools.Add(new FileKnowledgePoolEntity { RoxFileId = "rox-1", KnowledgePoolId = "kp-1" });
		db.ExternalFileVersions.Add(
			new ExternalFileVersionEntity
			{
				RoxFileId = "rox-1",
				DocumentHash = "hash1",
				Filename = "doc.txt",
				ExternalItemId = "file_old",
				Status = ExternalFileVersionStatus.Uploaded,
			}
		);
		await db.SaveChangesAsync();

		var vectorStore = CreateMockVectorStore("vs_123", "test-kp-1");
		gateway.Setup(g => g.GetVectorStoreAsync("vs_123", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStore);
		gateway.Setup(g => g.RemoveFileFromVectorStoreAsync("vs_123", "file_old", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
		gateway.Setup(g => g.DeleteFileAsync("file_old", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		var file = new RoxFile("rox-1", "doc.txt") { ContentStream = null, DocumentHash = "hash1" };

		await sut.HandleFileUpdatedAsync(file, CancellationToken.None);

		// The indexed file was detached, deleted in OpenAI, and all local records removed.
		gateway.Verify(g => g.RemoveFileFromVectorStoreAsync("vs_123", "file_old", It.IsAny<CancellationToken>()), Times.Once);
		gateway.Verify(g => g.DeleteFileAsync("file_old", It.IsAny<CancellationToken>()), Times.Once);
		gateway.Verify(g => g.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
		Assert.Empty(db.ExternalFiles);
		Assert.Empty(db.FileKnowledgePools);
		Assert.Empty(db.ExternalFileVersions);
	}

	[Fact]
	public async Task HandleFileUpdatedAsync_DoesNothing_WhenNoContentStreamAndNoMapping()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		var file = new RoxFile("rox-1", "doc.txt") { ContentStream = null };

		await sut.HandleFileUpdatedAsync(file, CancellationToken.None);

		// Nothing to delete: no OpenAI calls, no upload.
		gateway.Verify(g => g.DeleteFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
		gateway.Verify(g => g.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	#endregion

	[Fact]
	public async Task HandleFileUpdatedAsync_UploadsAsNew_WhenNoExistingMapping()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		var newFile = CreateMockOpenAIFile("file_new", "doc.txt");
		gateway.Setup(g => g.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(newFile);

		var file = new RoxFile("rox-1", "doc.txt") { ContentStream = new MemoryStream([65, 66, 67]) };
		await sut.HandleFileUpdatedAsync(file, CancellationToken.None);

		gateway.Verify(g => g.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
		Assert.Single(db.ExternalFiles);
		Assert.Equal("file_new", db.ExternalFiles.First().ExternalItemId);
	}

	[Fact]
	public async Task HandleFileUpdatedAsync_SkipsReindex_WhenDocumentHashMatches()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		db.ExternalFiles.Add(
			new ExternalFileEntity
			{
				RoxFileId = "rox-1",
				ExternalItemId = "file_existing",
				DocumentHash = "hash1",
			}
		);
		await db.SaveChangesAsync();

		var file = new RoxFile("rox-1", "doc.txt") { ContentStream = new MemoryStream([65, 66, 67]), DocumentHash = "hash1" };
		await sut.HandleFileUpdatedAsync(file, CancellationToken.None);

		gateway.Verify(g => g.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task HandleFileUpdatedAsync_MarksOldVersionAsSuperseded_WhenReindexing()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		db.ExternalGroups.Add(new ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "vs_123" });
		db.ExternalFiles.Add(
			new ExternalFileEntity
			{
				RoxFileId = "rox-1",
				ExternalItemId = "file_old",
				DocumentHash = "old_hash",
			}
		);
		db.ExternalFileVersions.Add(
			new ExternalFileVersionEntity
			{
				RoxFileId = "rox-1",
				DocumentHash = "old_hash",
				Filename = "doc.txt",
				ExternalItemId = "file_old",
				Status = ExternalFileVersionStatus.Uploaded,
			}
		);
		db.FileKnowledgePools.Add(new FileKnowledgePoolEntity { RoxFileId = "rox-1", KnowledgePoolId = "kp-1" });
		await db.SaveChangesAsync();

		var vectorStore = CreateMockVectorStore("vs_123", "test-kp-1");
		var newFile = CreateMockOpenAIFile("file_new", "doc.txt");
		var vectorStoreFile = CreateMockVectorStoreFile("file_new", VectorStoreFileStatus.Completed);

		gateway.Setup(g => g.GetVectorStoreAsync("vs_123", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStore);
		gateway.Setup(g => g.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(newFile);
		gateway.Setup(g => g.RemoveFileFromVectorStoreAsync("vs_123", "file_old", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
		gateway.Setup(g => g.AddFileToVectorStoreAsync("vs_123", "file_new", It.IsAny<CancellationToken>())).ReturnsAsync(vectorStoreFile);
		gateway.Setup(g => g.DeleteFileAsync("file_old", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		var file = new RoxFile("rox-1", "doc.txt") { ContentStream = new MemoryStream([65, 66, 67]), DocumentHash = "new_hash" };
		await sut.HandleFileUpdatedAsync(file, CancellationToken.None);

		var oldVersion = db.ExternalFileVersions.First(v => v.DocumentHash == "old_hash");
		Assert.Equal(ExternalFileVersionStatus.Superseded, oldVersion.Status);

		// The new version record was created by UploadFileAsync
		var newVersion = db.ExternalFileVersions.First(v => v.DocumentHash == "new_hash");
		Assert.Equal(ExternalFileVersionStatus.Uploaded, newVersion.Status);
		Assert.Equal("file_new", newVersion.ExternalItemId);
	}

	#region HandleKnowledgePoolMemberAddedAsync / RemovedAsync Tests

	[Fact]
	public async Task HandleKnowledgePoolMemberAddedAsync_CompletesWithoutAction()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		// Should complete without calling any gateway methods (OpenAI has no ACL)
		await sut.HandleKnowledgePoolMemberAddedAsync("kp-1", Guid.NewGuid(), "ext-group-1", CancellationToken.None);

		gateway.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task HandleKnowledgePoolMemberRemovedAsync_CompletesWithoutAction()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		// Should complete without calling any gateway methods (OpenAI has no ACL)
		await sut.HandleKnowledgePoolMemberRemovedAsync("kp-1", Guid.NewGuid(), "ext-group-1", CancellationToken.None);

		gateway.VerifyNoOtherCalls();
	}

	#endregion

	#region InitializeAsync Tests

	[Fact]
	public async Task InitializeAsync_VerifiesApiConnectivity()
	{
		var (sut, gateway, db) = CreateSut();
		await using var dbScope = db;

		gateway.Setup(g => g.GetVectorStoreAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((VectorStore?)null);

		await sut.InitializeAsync(CancellationToken.None);

		gateway.Verify(g => g.GetVectorStoreAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	#endregion
}
