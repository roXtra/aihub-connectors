using AiHub.Connector.Data;
using AiHub.Connector.ExternalConnectors.M365Connector;
using AiHub.Connector.Roxtra;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Models.ExternalConnectors;
using Moq;
using Xunit;

namespace AiHub.Connector.Tests;

public class M365ConnectorTests
{
	private static ConnectorDbContext CreateDb()
	{
		var opts = new DbContextOptionsBuilder<ConnectorDbContext>().UseInMemoryDatabase(databaseName: System.Guid.NewGuid().ToString()).Options;
		return new ConnectorDbContext(opts);
	}

	private static void SetupConnectionAndSchema(Mock<IGraphExternalClient> graph, bool connectionExists = true)
	{
		_ = graph
			.Setup(g => g.GetConnectionAsync("conn-1", It.IsAny<CancellationToken>()))
			.ReturnsAsync(connectionExists ? new ExternalConnection() : (ExternalConnection?)null);
		_ = graph
			.Setup(g => g.GetSchemaAsync("conn-1", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new Schema { Properties = [new Property { Name = "title", Type = PropertyType.String }] });
		_ = graph.Setup(g => g.CreateOrUpdateSchemaAsync("conn-1", It.IsAny<Schema>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
	}

	private static void SetupCreateExternalGroup(Mock<IGraphExternalClient> graph)
	{
		_ = graph.Setup(g => g.CreateExternalGroupAsync("conn-1", It.IsAny<ExternalGroup>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
	}

	private static void SetupGetItem(Mock<IGraphExternalClient> graph, string itemId, ExternalItem? item)
	{
		_ = graph.Setup(g => g.GetItemAsync("conn-1", itemId, It.IsAny<CancellationToken>())).ReturnsAsync(item);
	}

	[Fact]
	public async Task HandleKnowledgePoolMemberAdded_AllowsEmptyExternalGroupId_SkipsAddMember()
	{
		var (sut, graph, db) = CreateSut();
		await using var dbScope = db;
		// Ensure connection and schema paths succeed
		_ = graph.Setup(g => g.GetConnectionAsync("conn-1", It.IsAny<CancellationToken>())).ReturnsAsync(new ExternalConnection());
		_ = graph
			.Setup(g => g.GetSchemaAsync("conn-1", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new Schema { Properties = [new Property { Name = "title", Type = PropertyType.String }] });
		_ = graph.Setup(g => g.CreateOrUpdateSchemaAsync("conn-1", It.IsAny<Schema>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		// EnsureExternalGroupAsync should attempt to create the external group if not present locally
		_ = graph.Setup(g => g.CreateExternalGroupAsync("conn-1", It.IsAny<ExternalGroup>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		// Call with empty externalGroupId; should not call AddMemberToExternalGroupAsync
		await sut.HandleKnowledgePoolMemberAddedAsync("kp-empty", System.Guid.NewGuid(), string.Empty, CancellationToken.None);

		graph.Verify(g => g.CreateExternalGroupAsync("conn-1", It.IsAny<ExternalGroup>(), It.IsAny<CancellationToken>()), Times.Once);
		graph.Verify(
			g => g.AddMemberToExternalGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ConnectorIdentity>(), It.IsAny<CancellationToken>()),
			Times.Never
		);
	}

	private static (M365Connector sut, Mock<IGraphExternalClient> graph, ConnectorDbContext db) CreateSut()
	{
		var db = CreateDb();
		var graph = new Mock<IGraphExternalClient>(MockBehavior.Strict);
		var logger = Mock.Of<ILogger<M365Connector>>();
		var pdf = new Mock<IPdfTextExtractor>(MockBehavior.Strict);
		_ = pdf.Setup(p => p.ExtractAsync(It.IsAny<System.IO.Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
		var gopts = Options.Create(
			new GraphOptions
			{
				ExternalConnectionId = "conn-1",
				TenantId = "t",
				ClientId = "c",
				ClientSecret = "s",
			}
		);
		var ropts = Options.Create(new RoxtraOptions { RoxtraUrl = "https://roxtra.example.com" });
		var sut = new M365Connector(logger, db, graph.Object, gopts, ropts, pdf.Object);
		return (sut, graph, db);
	}

	private static (M365Connector sut, Mock<IGraphExternalClient> graph, ConnectorDbContext db) CreateSut(bool useWorkaround)
	{
		var db = CreateDb();
		var graph = new Mock<IGraphExternalClient>(MockBehavior.Strict);
		var logger = Mock.Of<ILogger<M365Connector>>();
		var pdf = new Mock<IPdfTextExtractor>(MockBehavior.Strict);
		_ = pdf.Setup(p => p.ExtractAsync(It.IsAny<System.IO.Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
		var gopts = Options.Create(
			new GraphOptions
			{
				ExternalConnectionId = "conn-1",
				TenantId = "t",
				ClientId = "c",
				ClientSecret = "s",
				UseExternalGroupMembershipWorkaround = useWorkaround,
			}
		);
		var ropts = Options.Create(new RoxtraOptions { RoxtraUrl = "https://roxtra.example.com" });
		var sut = new M365Connector(logger, db, graph.Object, gopts, ropts, pdf.Object);
		return (sut, graph, db);
	}

	[Fact]
	public async Task HandleKnowledgePoolCreated_EnsuresConnection_AndGroup()
	{
		var (sut, graph, db) = CreateSut();
		await using var dbScope = db;
		_ = graph.Setup(g => g.GetConnectionAsync("conn-1", It.IsAny<CancellationToken>())).ReturnsAsync((ExternalConnection?)null);
		_ = graph.Setup(g => g.CreateConnectionAsync(It.IsAny<ExternalConnection>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
		_ = graph.Setup(g => g.GetSchemaAsync("conn-1", It.IsAny<CancellationToken>())).ReturnsAsync(new Schema { Properties = [] });
		_ = graph.Setup(g => g.CreateOrUpdateSchemaAsync("conn-1", It.IsAny<Schema>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
		_ = graph.Setup(g => g.CreateExternalGroupAsync("conn-1", It.IsAny<ExternalGroup>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		await sut.HandleKnowledgePoolCreatedAsync("kp-1", CancellationToken.None);

		graph.VerifyAll();
		Assert.True(db.ExternalGroups.Any());
	}

	[Fact]
	public async Task HandleKnowledgePoolFileAdded_UpsertsItem()
	{
		var (sut, graph, db) = CreateSut();
		await using var dbScope = db;
		_ = graph.Setup(g => g.GetConnectionAsync("conn-1", It.IsAny<CancellationToken>())).ReturnsAsync(new ExternalConnection());
		_ = graph
			.Setup(g => g.GetSchemaAsync("conn-1", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new Schema { Properties = [new Property { Name = "title", Type = PropertyType.String }] });
		_ = graph.Setup(g => g.CreateOrUpdateSchemaAsync("conn-1", It.IsAny<Schema>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
		_ = graph.Setup(g => g.CreateExternalGroupAsync("conn-1", It.IsAny<ExternalGroup>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
		// No existing item in Graph for first upload
		_ = graph.Setup(g => g.GetItemAsync("conn-1", "roXtraFilefile-42", It.IsAny<CancellationToken>())).ReturnsAsync((ExternalItem?)null);
		ExternalItem? captured = null;
		_ = graph
			.Setup(g => g.UpsertItemAsync("conn-1", It.IsAny<string>(), It.IsAny<ExternalItem>(), It.IsAny<Schema>(), It.IsAny<CancellationToken>()))
			.Callback<string, string, ExternalItem, Schema, CancellationToken>((cid, id, item, schema, ct) => captured = item)
			.Returns(Task.CompletedTask);

		var file = new RoxFile("file-42", "Doc.txt") { ContentStream = new System.IO.MemoryStream(new byte[] { 65, 66 }) };
		await sut.HandleKnowledgePoolFileAddedAsync("kp-99", file, CancellationToken.None);

		Assert.NotNull(captured);
		Assert.NotNull(captured!.Acl);
		Assert.Contains(captured!.Properties!.AdditionalData!, kv => kv.Key == "title" && (string)kv.Value == "Doc.txt");
		Assert.True(db.ExternalFiles.Any(f => f.RoxFileId == "file-42"));
	}

	[Theory]
	[InlineData(true, true)]
	[InlineData(false, false)]
	public async Task HandleKnowledgePoolFileAdded_MergesAcl_DependsOnWorkaround(bool useWorkaround, bool expectEveryone)
	{
		var (sut, graph, db) = CreateSut(useWorkaround);
		await using var dbScope = db;
		// Connection/schema exist and ensure group creation path
		SetupConnectionAndSchema(graph);
		SetupCreateExternalGroup(graph);

		// Existing mapping indicates the item already exists in Graph
		_ = db.ExternalFiles.Add(new Data.Entities.ExternalFileEntity { RoxFileId = "file-42", ExternalItemId = "roXtraFilefile-42" });
		_ = await db.SaveChangesAsync();

		// Graph returns existing item with one ACL entry
		var existing = new ExternalItem
		{
			Id = "roXtraFilefile-42",
			Acl =
			[
				new Acl
				{
					Type = AclType.ExternalGroup,
					Value = "existing-group",
					AccessType = AccessType.Grant,
				},
			],
			Content = new ExternalItemContent { Type = ExternalItemContentType.Text, Value = "abc" },
			Properties = new Properties { AdditionalData = new Dictionary<string, object> { ["title"] = "Doc.txt" } },
		};
		SetupGetItem(graph, "roXtraFilefile-42", existing);

		ExternalItem? patched = null;
		_ = graph
			.Setup(g => g.UpsertItemAsync("conn-1", "roXtraFilefile-42", It.IsAny<ExternalItem>(), It.IsAny<Schema>(), It.IsAny<CancellationToken>()))
			.Callback<string, string, ExternalItem, Schema, CancellationToken>((cid, id, item, schema, ct) => patched = item)
			.Returns(Task.CompletedTask);

		var file = new RoxFile("file-42", "Doc.txt") { ContentStream = new System.IO.MemoryStream(new byte[] { 65, 66 }) };
		await sut.HandleKnowledgePoolFileAddedAsync("kp-99", file, CancellationToken.None);

		Assert.NotNull(patched);
		Assert.NotNull(patched!.Acl);
		Assert.Contains(patched!.Acl!, a => a.Value == "existing-group");
		Assert.Contains(patched!.Acl!, a => a.Value == "roXtraKpkp99");
		if (expectEveryone)
		{
			Assert.Contains(patched!.Acl!, a => a.Type == AclType.Everyone);
		}
		else
		{
			Assert.DoesNotContain(patched!.Acl!, a => a.Type == AclType.Everyone);
		}
	}

	[Fact]
	public async Task HandleKnowledgePoolFileAdded_MergesAcl_NoEveryone_WhenWorkaroundDisabled()
	{
		var (sut, graph, db) = CreateSut(false);
		await using var dbScope = db;
		SetupConnectionAndSchema(graph);
		SetupCreateExternalGroup(graph);

		// Existing mapping indicates the item already exists in Graph
		_ = db.ExternalFiles.Add(new Data.Entities.ExternalFileEntity { RoxFileId = "file-98", ExternalItemId = "roXtraFilefile-98" });
		_ = await db.SaveChangesAsync();

		// Graph returns existing item with one ACL entry
		var existing = new ExternalItem
		{
			Id = "roXtraFilefile-98",
			Acl =
			[
				new Acl
				{
					Type = AclType.ExternalGroup,
					Value = "existing-group",
					AccessType = AccessType.Grant,
				},
			],
			Content = new ExternalItemContent { Type = ExternalItemContentType.Text, Value = "abc" },
			Properties = new Properties { AdditionalData = new Dictionary<string, object> { ["title"] = "Doc.txt" } },
		};
		SetupGetItem(graph, "roXtraFilefile-98", existing);

		ExternalItem? patched = null;
		_ = graph
			.Setup(g => g.UpsertItemAsync("conn-1", "roXtraFilefile-98", It.IsAny<ExternalItem>(), It.IsAny<Schema>(), It.IsAny<CancellationToken>()))
			.Callback<string, string, ExternalItem, Schema, CancellationToken>((cid, id, item, schema, ct) => patched = item)
			.Returns(Task.CompletedTask);

		var file = new RoxFile("file-98", "Doc.txt") { ContentStream = new System.IO.MemoryStream(new byte[] { 65, 66 }) };
		await sut.HandleKnowledgePoolFileAddedAsync("kp-98", file, CancellationToken.None);

		Assert.NotNull(patched);
		Assert.NotNull(patched!.Acl);
		Assert.Contains(patched!.Acl!, a => a.Value == "existing-group");
		Assert.Contains(patched!.Acl!, a => a.Value == "roXtraKpkp98");
		Assert.DoesNotContain(patched!.Acl!, a => a.Type == AclType.Everyone);
	}

	[Theory]
	[InlineData(true, 0)]
	[InlineData(false, 1)]
	public async Task HandleKnowledgePoolMemberAdded_WithGroupId_Parameterized(bool useWorkaround, int expectedAddCalls)
	{
		var (sut, graph, db) = CreateSut(useWorkaround);
		await using var dbScope = db;
		SetupConnectionAndSchema(graph);
		SetupCreateExternalGroup(graph);

		_ = graph
			.Setup(g => g.AddMemberToExternalGroupAsync("conn-1", "roXtraKpkpadd", It.IsAny<ConnectorIdentity>(), It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		await sut.HandleKnowledgePoolMemberAddedAsync("kp-add", System.Guid.NewGuid(), "aad-group-1", CancellationToken.None);

		graph.Verify(
			g =>
				g.AddMemberToExternalGroupAsync(
					"conn-1",
					"roXtraKpkpadd",
					It.Is<ConnectorIdentity>(i => i.Id == "aad-group-1" && i.Type == IdentityType.Group && i.IdentitySource == "azureActiveDirectory"),
					It.IsAny<CancellationToken>()
				),
			Times.Exactly(expectedAddCalls)
		);
	}

	[Theory]
	[InlineData(true, 0)]
	[InlineData(false, 1)]
	public async Task HandleKnowledgePoolMemberRemoved_Parameterized(bool useWorkaround, int expectedRemoveCalls)
	{
		var (sut, graph, db) = CreateSut(useWorkaround);
		await using var dbScope = db;
		SetupConnectionAndSchema(graph);
		SetupCreateExternalGroup(graph);

		_ = graph
			.Setup(g => g.RemoveMemberFromExternalGroupAsync("conn-1", "roXtraKpkpremove", "aad-group-2", It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		await sut.HandleKnowledgePoolMemberRemovedAsync("kp-remove", System.Guid.NewGuid(), "aad-group-2", CancellationToken.None);

		graph.Verify(
			g => g.RemoveMemberFromExternalGroupAsync("conn-1", "roXtraKpkpremove", "aad-group-2", It.IsAny<CancellationToken>()),
			Times.Exactly(expectedRemoveCalls)
		);
	}

	[Theory]
	[InlineData(true, true)]
	[InlineData(false, false)]
	public async Task HandleKnowledgePoolFileAdded_CreatesItem_Parameterized(bool useWorkaround, bool expectEveryone)
	{
		var (sut, graph, db) = CreateSut(useWorkaround);
		await using var dbScope = db;
		SetupConnectionAndSchema(graph);
		SetupCreateExternalGroup(graph);
		SetupGetItem(graph, "roXtraFilefile-55", null);

		ExternalItem? created = null;
		_ = graph
			.Setup(g => g.UpsertItemAsync("conn-1", "roXtraFilefile-55", It.IsAny<ExternalItem>(), It.IsAny<Schema>(), It.IsAny<CancellationToken>()))
			.Callback<string, string, ExternalItem, Schema, CancellationToken>((cid, id, item, schema, ct) => created = item)
			.Returns(Task.CompletedTask);

		var file = new RoxFile("file-55", "Doc55.txt") { ContentStream = new System.IO.MemoryStream(new byte[] { 1, 2 }) };
		await sut.HandleKnowledgePoolFileAddedAsync("kp-55", file, CancellationToken.None);

		Assert.NotNull(created);
		Assert.NotNull(created!.Acl);
		if (expectEveryone)
		{
			Assert.Contains(created!.Acl!, a => a.Type == AclType.Everyone);
		}
		else
		{
			Assert.DoesNotContain(created!.Acl!, a => a.Type == AclType.Everyone);
		}
		Assert.Contains(created!.Acl!, a => a.Type == AclType.ExternalGroup && a.Value == "roXtraKpkp55");
	}

	[Fact]
	public async Task HandleKnowledgePoolFileRemoved_UpdatesAcl_WhenOtherGroupsRemain()
	{
		var (sut, graph, db) = CreateSut();
		await using var dbScope = db;
		_ = graph.Setup(g => g.GetConnectionAsync("conn-1", It.IsAny<CancellationToken>())).ReturnsAsync(new ExternalConnection());
		_ = graph
			.Setup(g => g.GetSchemaAsync("conn-1", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new Schema { Properties = [new Property { Name = "title", Type = PropertyType.String }] });
		_ = graph.Setup(g => g.CreateOrUpdateSchemaAsync("conn-1", It.IsAny<Schema>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		// Item exists with two groups
		var existing = new ExternalItem
		{
			Id = "roXtraFilefile-42",
			Acl =
			[
				new Acl
				{
					Type = AclType.ExternalGroup,
					Value = "roXtraKpkp99",
					AccessType = AccessType.Grant,
				},
				new Acl
				{
					Type = AclType.ExternalGroup,
					Value = "roXtraKpkp77",
					AccessType = AccessType.Grant,
				},
			],
			Content = new ExternalItemContent { Type = ExternalItemContentType.Text, Value = "abc" },
			Properties = new Properties { AdditionalData = new Dictionary<string, object>() },
		};
		_ = graph.Setup(g => g.GetItemAsync("conn-1", "roXtraFilefile-42", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

		ExternalItem? patched = null;
		_ = graph
			.Setup(g => g.UpsertItemAsync("conn-1", "roXtraFilefile-42", It.IsAny<ExternalItem>(), It.IsAny<Schema>(), It.IsAny<CancellationToken>()))
			.Callback<string, string, ExternalItem, Schema, CancellationToken>((cid, id, item, schema, ct) => patched = item)
			.Returns(Task.CompletedTask);

		await sut.HandleKnowledgePoolFileRemovedAsync("kp-99", "file-42", CancellationToken.None);

		Assert.NotNull(patched);
		Assert.Single(patched!.Acl!);
		Assert.Equal("roXtraKpkp77", patched!.Acl![0].Value);
	}

	[Fact]
	public async Task HandleKnowledgePoolFileRemoved_Deletes_When_LastGroup()
	{
		var (sut, graph, db) = CreateSut();
		await using var dbScope = db;
		_ = graph.Setup(g => g.GetConnectionAsync("conn-1", It.IsAny<CancellationToken>())).ReturnsAsync(new ExternalConnection());
		_ = graph
			.Setup(g => g.GetSchemaAsync("conn-1", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new Schema { Properties = [new Property { Name = "title", Type = PropertyType.String }] });
		_ = graph.Setup(g => g.CreateOrUpdateSchemaAsync("conn-1", It.IsAny<Schema>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		// Add mapping to db
		_ = db.ExternalFiles.Add(new Data.Entities.ExternalFileEntity { RoxFileId = "file-99", ExternalItemId = "roXtraFilefile-99" });
		_ = await db.SaveChangesAsync();

		// Item exists with single group
		var existing = new ExternalItem
		{
			Id = "roXtraFilefile-99",
			Acl =
			[
				new Acl
				{
					Type = AclType.ExternalGroup,
					Value = "roXtraKpkp11",
					AccessType = AccessType.Grant,
				},
			],
			Content = new ExternalItemContent { Type = ExternalItemContentType.Text, Value = "abc" },
			Properties = new Properties { AdditionalData = new Dictionary<string, object>() },
		};
		_ = graph.Setup(g => g.GetItemAsync("conn-1", "roXtraFilefile-99", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

		bool deleted = false;
		_ = graph
			.Setup(g => g.DeleteItemAsync("conn-1", "roXtraFilefile-99", It.IsAny<CancellationToken>()))
			.Callback(() => deleted = true)
			.Returns(Task.CompletedTask);

		await sut.HandleKnowledgePoolFileRemovedAsync("kp-11", "file-99", CancellationToken.None);

		Assert.True(deleted);
		Assert.False(db.ExternalFiles.Any(f => f.RoxFileId == "file-99"));
	}

	[Fact]
	public async Task HandleKnowledgePoolRemoved_DeletesGroup_AndDbMapping()
	{
		var (sut, graph, db) = CreateSut();
		await using var dbScope = db;
		_ = graph.Setup(g => g.GetConnectionAsync("conn-1", It.IsAny<CancellationToken>())).ReturnsAsync(new ExternalConnection());
		_ = graph
			.Setup(g => g.GetSchemaAsync("conn-1", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new Schema { Properties = [new Property { Name = "title", Type = PropertyType.String }] });
		_ = graph.Setup(g => g.CreateOrUpdateSchemaAsync("conn-1", It.IsAny<Schema>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		// Existing group mapping
		_ = db.ExternalGroups.Add(new Data.Entities.ExternalGroupEntity { KnowledgePoolId = "kp-123", ExternalGroupId = "roXtraKpkp123" });
		_ = await db.SaveChangesAsync();

		bool deleted = false;
		_ = graph
			.Setup(g => g.DeleteExternalGroupAsync("conn-1", "roXtraKpkp123", It.IsAny<CancellationToken>()))
			.Callback(() => deleted = true)
			.Returns(Task.CompletedTask);

		await sut.HandleKnowledgePoolRemovedAsync("kp-123", CancellationToken.None);

		Assert.True(deleted);
		Assert.False(db.ExternalGroups.Any(g => g.KnowledgePoolId == "kp-123"));
	}

	[Fact]
	public async Task HandleKnowledgePoolRemoved_CleansUpItems()
	{
		var (sut, graph, db) = CreateSut();
		await using var dbScope = db;
		_ = graph.Setup(g => g.GetConnectionAsync("conn-1", It.IsAny<CancellationToken>())).ReturnsAsync(new ExternalConnection());
		_ = graph
			.Setup(g => g.GetSchemaAsync("conn-1", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new Schema { Properties = [new Property { Name = "title", Type = PropertyType.String }] });
		_ = graph.Setup(g => g.CreateOrUpdateSchemaAsync("conn-1", It.IsAny<Schema>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		// Two items in DB
		_ = db.ExternalFiles.Add(new Data.Entities.ExternalFileEntity { RoxFileId = "file-a", ExternalItemId = "roXtraFilefile-a" });
		_ = db.ExternalFiles.Add(new Data.Entities.ExternalFileEntity { RoxFileId = "file-b", ExternalItemId = "roXtraFilefile-b" });
		// Link both items to the knowledge pool
		_ = db.FileKnowledgePools.Add(new Data.Entities.FileKnowledgePoolEntity { RoxFileId = "file-a", KnowledgePoolId = "kp-123" });
		_ = db.FileKnowledgePools.Add(new Data.Entities.FileKnowledgePoolEntity { RoxFileId = "file-b", KnowledgePoolId = "kp-123" });
		_ = db.ExternalGroups.Add(new Data.Entities.ExternalGroupEntity { KnowledgePoolId = "kp-123", ExternalGroupId = "roXtraKpkp123" });
		_ = await db.SaveChangesAsync();

		// Item A has only this group's ACL -> should be deleted
		_ = graph
			.Setup(g => g.GetItemAsync("conn-1", "roXtraFilefile-a", It.IsAny<CancellationToken>()))
			.ReturnsAsync(
				new ExternalItem
				{
					Id = "roXtraFilefile-a",
					Acl =
					[
						new Acl
						{
							Type = AclType.ExternalGroup,
							Value = "roXtraKpkp123",
							AccessType = AccessType.Grant,
						},
					],
					Content = new ExternalItemContent { Type = ExternalItemContentType.Text, Value = "" },
					Properties = new Properties { AdditionalData = new Dictionary<string, object>() },
				}
			);
		bool deletedA = false;
		_ = graph
			.Setup(g => g.DeleteItemAsync("conn-1", "roXtraFilefile-a", It.IsAny<CancellationToken>()))
			.Callback(() => deletedA = true)
			.Returns(Task.CompletedTask);

		// Item B has this group plus another -> should upsert without this group
		_ = graph
			.Setup(g => g.GetItemAsync("conn-1", "roXtraFilefile-b", It.IsAny<CancellationToken>()))
			.ReturnsAsync(
				new ExternalItem
				{
					Id = "roXtraFilefile-b",
					Acl =
					[
						new Acl
						{
							Type = AclType.ExternalGroup,
							Value = "roXtraKpkp123",
							AccessType = AccessType.Grant,
						},
						new Acl
						{
							Type = AclType.ExternalGroup,
							Value = "roXtraKpkp999",
							AccessType = AccessType.Grant,
						},
					],
					Content = new ExternalItemContent { Type = ExternalItemContentType.Text, Value = "" },
					Properties = new Properties { AdditionalData = new Dictionary<string, object>() },
				}
			);
		ExternalItem? patchedB = null;
		_ = graph
			.Setup(g => g.UpsertItemAsync("conn-1", "roXtraFilefile-b", It.IsAny<ExternalItem>(), It.IsAny<Schema>(), It.IsAny<CancellationToken>()))
			.Callback<string, string, ExternalItem, Schema, CancellationToken>((cid, id, item, schema, ct) => patchedB = item)
			.Returns(Task.CompletedTask);

		bool deletedGroup = false;
		_ = graph
			.Setup(g => g.DeleteExternalGroupAsync("conn-1", "roXtraKpkp123", It.IsAny<CancellationToken>()))
			.Callback(() => deletedGroup = true)
			.Returns(Task.CompletedTask);

		await sut.HandleKnowledgePoolRemovedAsync("kp-123", CancellationToken.None);

		Assert.True(deletedA);
		Assert.NotNull(patchedB);
		Assert.DoesNotContain(patchedB!.Acl!, a => a.Value == "roXtraKpkp123");
		Assert.True(deletedGroup);
		Assert.False(db.ExternalFiles.Any(f => f.RoxFileId == "file-a"));
		Assert.True(db.ExternalFiles.Any(f => f.RoxFileId == "file-b"));
	}

	[Fact]
	public async Task HandleFileUpdated_UpdatesExistingItem_ContentOnly()
	{
		var (sut, graph, db) = CreateSut();
		await using var dbScope = db;
		_ = graph.Setup(g => g.GetConnectionAsync("conn-1", It.IsAny<CancellationToken>())).ReturnsAsync(new ExternalConnection());
		_ = graph
			.Setup(g => g.GetSchemaAsync("conn-1", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new Schema { Properties = [new Property { Name = "title", Type = PropertyType.String }] });
		_ = graph.Setup(g => g.CreateOrUpdateSchemaAsync("conn-1", It.IsAny<Schema>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		// Existing item with ACL
		var existing = new ExternalItem
		{
			Id = "roXtraFilefile-7",
			Acl =
			[
				new Acl
				{
					Type = AclType.ExternalGroup,
					Value = "roXtraKpkp1",
					AccessType = AccessType.Grant,
				},
			],
			Content = new ExternalItemContent { Type = ExternalItemContentType.Text, Value = "old" },
			Properties = new Properties { AdditionalData = new Dictionary<string, object> { ["title"] = "Old" } },
		};
		_ = graph.Setup(g => g.GetItemAsync("conn-1", "roXtraFilefile-7", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

		ExternalItem? upserted = null;
		_ = graph
			.Setup(g => g.UpsertItemAsync("conn-1", "roXtraFilefile-7", It.IsAny<ExternalItem>(), It.IsAny<Schema>(), It.IsAny<CancellationToken>()))
			.Callback<string, string, ExternalItem, Schema, CancellationToken>((cid, id, item, schema, ct) => upserted = item)
			.Returns(Task.CompletedTask);

		var file = new Roxtra.RoxFile("file-7", "New.pdf") { ContentStream = new System.IO.MemoryStream(new byte[] { 0x25, 0x50 }) };
		await sut.HandleFileUpdatedAsync(file, CancellationToken.None);

		Assert.NotNull(upserted);
		// For updates, the connector passes only content/properties; Id and Acl are handled by the client
		Assert.Null(upserted!.Id);
		Assert.Null(upserted!.Acl);
	}

	[Fact]
	public async Task HandleFileUpdated_CreatesItem_FromMemberships_WhenMissing()
	{
		var (sut, graph, db) = CreateSut();
		_ = graph.Setup(g => g.GetConnectionAsync("conn-1", It.IsAny<CancellationToken>())).ReturnsAsync(new ExternalConnection());
		_ = graph
			.Setup(g => g.GetSchemaAsync("conn-1", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new Schema { Properties = [new Property { Name = "title", Type = PropertyType.String }] });

		// No existing item
		_ = graph.Setup(g => g.GetItemAsync("conn-1", "roXtraFilefile-8", It.IsAny<CancellationToken>())).ReturnsAsync((ExternalItem?)null);

		// Memberships
		_ = db.ExternalGroups.Add(new Data.Entities.ExternalGroupEntity { KnowledgePoolId = "kp-1", ExternalGroupId = "roXtraKpkp1" });
		_ = db.FileKnowledgePools.Add(new Data.Entities.FileKnowledgePoolEntity { RoxFileId = "file-8", KnowledgePoolId = "kp-1" });
		_ = await db.SaveChangesAsync();

		ExternalItem? created = null;
		_ = graph
			.Setup(g => g.UpsertItemAsync("conn-1", "roXtraFilefile-8", It.IsAny<ExternalItem>(), It.IsAny<Schema>(), It.IsAny<CancellationToken>()))
			.Callback<string, string, ExternalItem, Schema, CancellationToken>((cid, id, item, schema, ct) => created = item)
			.Returns(Task.CompletedTask);

		var file = new Roxtra.RoxFile("file-8", "New.pdf") { ContentStream = new System.IO.MemoryStream(new byte[] { 0x25, 0x50 }) };
		await sut.HandleFileUpdatedAsync(file, CancellationToken.None);

		Assert.NotNull(created);
		Assert.NotEmpty(created!.Acl!);
		Assert.Contains(created!.Acl!, a => a.Value == "roXtraKpkp1");
	}
}
