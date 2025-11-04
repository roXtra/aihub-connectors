using System.Net;
using System.Text.Json;
using AiHub.Connector.ExternalConnectors;
using AiHub.Connector.Roxtra;
using AiHub.Connector.Webhooks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AiHub.Connector.Tests;

public class WebhookHandlerTests
{
	private const string RoxtraBaseUrl = "https://roxtra.example.com";

	private static WebhookHandler CreateHandler(out Mock<IExternalConnector> external, byte[]? downloadContent = null)
	{
		external = new Mock<IExternalConnector>(MockBehavior.Strict);
		var logger = Mock.Of<ILogger<WebhookHandler>>();
		var factory = MakeHttpClientFactory(downloadContent ?? new byte[] { 1, 2, 3 }, out _);
		var roxtraOptions = Options.Create(new RoxtraOptions { RoxtraUrl = RoxtraBaseUrl });
		return new WebhookHandler(external.Object, logger, factory, roxtraOptions);
	}

	private static WebhookHandler CreateHandler(out Mock<IExternalConnector> external, out HttpClient httpClient, byte[]? downloadContent = null)
	{
		external = new Mock<IExternalConnector>(MockBehavior.Strict);
		var logger = Mock.Of<ILogger<WebhookHandler>>();
		var factory = MakeHttpClientFactory(downloadContent ?? new byte[] { 1, 2, 3 }, out httpClient);
		var roxtraOptions = Options.Create(new RoxtraOptions { RoxtraUrl = RoxtraBaseUrl });
		return new WebhookHandler(external.Object, logger, factory, roxtraOptions);
	}

	private static IHttpClientFactory MakeHttpClientFactory(byte[] content, out HttpClient client)
	{
		var handler = new StubHandler(content);
		client = new HttpClient(handler, disposeHandler: true);
		var mock = new Moq.Mock<IHttpClientFactory>(Moq.MockBehavior.Strict);
		_ = mock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
		return mock.Object;
	}

	private static WebhookHandler CreateHandler(IHttpClientFactory factory, out Mock<IExternalConnector> external)
	{
		external = new Mock<IExternalConnector>(MockBehavior.Strict);
		var logger = Mock.Of<ILogger<WebhookHandler>>();
		var roxtraOptions = Options.Create(new RoxtraOptions { RoxtraUrl = RoxtraBaseUrl });
		return new WebhookHandler(external.Object, logger, factory, roxtraOptions);
	}

	private sealed class StubHandler : HttpMessageHandler
	{
		private readonly byte[] _content;

		public StubHandler(byte[] content) => _content = content;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_content) };
			return Task.FromResult(response);
		}
	}

	private sealed class CountingHandler : HttpMessageHandler
	{
		public int Calls { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Calls++;
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x01 }) });
		}
	}

	[Fact]
	public async Task KnowledgePoolFileAdded_DoesNotDownload_When_Flag_False()
	{
		var counting = new CountingHandler();
		using var httpClient = new HttpClient(counting, disposeHandler: true);
		var factory = new Moq.Mock<IHttpClientFactory>(Moq.MockBehavior.Strict);
		_ = factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

		var handler = CreateHandler(factory.Object, out var external);
		_ = external
			.Setup(x =>
				x.HandleKnowledgePoolFileAddedAsync(
					"kp-123",
					It.Is<RoxFile>(f => f.Id == "file-1" && f.Title == "Test.pdf" && f.ContentStream == null),
					It.IsAny<CancellationToken>()
				)
			)
			.Returns(Task.CompletedTask);

		var (payload, req) = MakeRequest(
			"{"
				+ "\"type\":\"knowledgepool.file.added\","
				+ "\"fileId\":\"file-1\","
				+ "\"knowledgePoolId\":\"kp-123\","
				+ "\"supportedForKnowledgePools\":false,"
				+ $"\"downloadUrl\":\"{RoxtraBaseUrl}/download/file-1\","
				+ "\"title\":\"Test.pdf\"}"
		);

		var result = await handler.HandleAsync(payload, req, CancellationToken.None);

		Assert.NotNull(result);
		Assert.Equal(0, counting.Calls);
		external.VerifyAll();
	}

	private static (JsonElement payload, HttpRequest request) MakeRequest(string json)
	{
		using var doc = JsonDocument.Parse(json);
		var ctx = new DefaultHttpContext();
		var req = ctx.Request;
		return (doc.RootElement.Clone(), req);
	}

	[Fact]
	public async Task KnowledgePoolFileAdded_Uses_Payload_And_Calls_M365()
	{
		var expected = new byte[] { 9, 8, 7 };
		var handler = CreateHandler(out var external, out var httpClient, expected);
		using var clientScope = httpClient;
		_ = external
			.Setup(x =>
				x.HandleKnowledgePoolFileAddedAsync(
					"kp-123",
					It.Is<RoxFile>(f => f.Id == "file-1" && f.Title == "Test.pdf" && f.ContentStream != null),
					It.IsAny<CancellationToken>()
				)
			)
			.Returns(Task.CompletedTask);

		var (payload, req) = MakeRequest(
			"{"
				+ "\"type\":\"knowledgepool.file.added\","
				+ "\"fileId\":\"file-1\","
				+ "\"knowledgePoolId\":\"kp-123\","
				+ $"\"downloadUrl\":\"{RoxtraBaseUrl}/download/file-1\","
				+ "\"title\":\"Test.pdf\"}"
		);

		var result = await handler.HandleAsync(payload, req, CancellationToken.None);

		external.VerifyAll();
		Assert.NotNull(result);
	}

	[Fact]
	public async Task KnowledgePoolFileAdded_Uses_Payload_Metadata_Even_When_Flag_False()
	{
		var handler = CreateHandler(out var external);
		_ = external
			.Setup(x =>
				x.HandleKnowledgePoolFileAddedAsync(
					"kp-123",
					It.Is<RoxFile>(f => f.Id == "file-1" && f.Title == "Test.pdf" && f.ContentStream == null),
					It.IsAny<CancellationToken>()
				)
			)
			.Returns(Task.CompletedTask);

		var (payload, req) = MakeRequest(
			"{"
				+ "\"type\":\"knowledgepool.file.added\","
				+ "\"fileId\":\"file-1\","
				+ "\"knowledgePoolId\":\"kp-123\","
				+ "\"supportedForKnowledgePools\":false,"
				+ $"\"downloadUrl\":\"{RoxtraBaseUrl}/download/file-1\","
				+ "\"title\":\"Test.pdf\"}"
		);

		var result = await handler.HandleAsync(payload, req, CancellationToken.None);

		external.VerifyAll();
		Assert.NotNull(result);
	}

	[Fact]
	public async Task KnowledgePoolCreated_Calls_M365()
	{
		var handler = CreateHandler(out var external);
		_ = external.Setup(x => x.HandleKnowledgePoolCreatedAsync("kp-777", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		var (payload, req) = MakeRequest("{" + "\"type\":\"knowledgepool.created\"," + "\"knowledgePoolId\":\"kp-777\"}");

		var result = await handler.HandleAsync(payload, req, CancellationToken.None);

		external.VerifyAll();
		Assert.NotNull(result);
	}

	[Fact]
	public async Task FileUpdated_Uses_Payload_And_Calls_M365()
	{
		var expected = new byte[] { 4, 3, 2, 1 };
		var handler = CreateHandler(out var external, out var httpClient, expected);
		using var clientScope = httpClient;
		_ = external
			.Setup(x =>
				x.HandleFileUpdatedAsync(
					It.Is<Roxtra.RoxFile>(f => f.Id == "file-2" && f.Title == "Updated.pdf" && f.ContentStream != null),
					It.IsAny<CancellationToken>()
				)
			)
			.Returns(Task.CompletedTask);

		var (payload, req) = MakeRequest(
			"{"
				+ "\"type\":\"file.updated\","
				+ "\"fileId\":\"file-2\","
				+ $"\"downloadUrl\":\"{RoxtraBaseUrl}/download/file-2\","
				+ "\"title\":\"Updated.pdf\"}"
		);

		var result = await handler.HandleAsync(payload, req, CancellationToken.None);

		external.VerifyAll();
		Assert.NotNull(result);
	}

	[Fact]
	public async Task FileUpdated_Uses_Payload_Metadata_Even_When_Flag_False()
	{
		var handler = CreateHandler(out var external);
		_ = external
			.Setup(x =>
				x.HandleFileUpdatedAsync(
					It.Is<Roxtra.RoxFile>(f => f.Id == "file-2" && f.Title == "Updated.pdf" && f.ContentStream == null),
					It.IsAny<CancellationToken>()
				)
			)
			.Returns(Task.CompletedTask);

		var (payload, req) = MakeRequest(
			"{"
				+ "\"type\":\"file.updated\","
				+ "\"fileId\":\"file-2\","
				+ $"\"supportedForKnowledgePools\":false,\"downloadUrl\":\"{RoxtraBaseUrl}/download/file-2\",\"title\":\"Updated.pdf\"}}"
		);

		var result = await handler.HandleAsync(payload, req, CancellationToken.None);

		external.VerifyAll();
		Assert.NotNull(result);
	}

	[Fact]
	public async Task KnowledgePoolRemoved_Calls_M365()
	{
		var handler = CreateHandler(out var external);
		_ = external.Setup(x => x.HandleKnowledgePoolRemovedAsync("kp-777", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		var (payload, req) = MakeRequest("{" + "\"type\":\"knowledgepool.removed\"," + "\"knowledgePoolId\":\"kp-777\"}");

		var result = await handler.HandleAsync(payload, req, CancellationToken.None);

		external.VerifyAll();
		Assert.NotNull(result);
	}

	[Fact]
	public async Task KnowledgePoolFileRemoved_Calls_M365()
	{
		var handler = CreateHandler(out var external);
		_ = external.Setup(x => x.HandleKnowledgePoolFileRemovedAsync("kp-123", "file-1", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		var (payload, req) = MakeRequest("{" + "\"type\":\"knowledgepool.file.removed\"," + "\"fileId\":\"file-1\"," + "\"knowledgePoolId\":\"kp-123\"}");

		var result = await handler.HandleAsync(payload, req, CancellationToken.None);

		external.VerifyAll();
		Assert.NotNull(result);
	}

	[Fact]
	public async Task FileAdded_When_External_Fails_Returns_Problem()
	{
		var handler = CreateHandler(out var external);
		_ = external
			.Setup(x => x.HandleKnowledgePoolFileAddedAsync("kp-1", It.IsAny<RoxFile>(), It.IsAny<CancellationToken>()))
			.Throws(new InvalidOperationException("failed"));

		var (payload, req) = MakeRequest(
			"{"
				+ "\"type\":\"knowledgepool.file.added\","
				+ "\"fileId\":\"missing\","
				+ $"\"knowledgePoolId\":\"kp-1\",\"downloadUrl\":\"{RoxtraBaseUrl}/download/missing\",\"title\":\"Doc.pdf\"}}"
		);

		var result = await handler.HandleAsync(payload, req, CancellationToken.None);
		Assert.NotNull(result);
	}

	[Fact]
	public async Task FileAdded_When_Unauthenticated_Maps_To_401()
	{
		var handler = CreateHandler(out var external);
		_ = external
			.Setup(x => x.HandleKnowledgePoolFileAddedAsync("kp-1", It.IsAny<RoxFile>(), It.IsAny<CancellationToken>()))
			.Throws(new UnauthorizedAccessException("invalid token"));

		var (payload, req) = MakeRequest(
			"{"
				+ "\"type\":\"knowledgepool.file.added\","
				+ "\"fileId\":\"file-unauth\","
				+ $"\"knowledgePoolId\":\"kp-1\",\"downloadUrl\":\"{RoxtraBaseUrl}/download/file-unauth\",\"title\":\"Doc.pdf\"}}"
		);

		var result = await handler.HandleAsync(payload, req, CancellationToken.None);
		var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
		Assert.Equal(StatusCodes.Status401Unauthorized, statusResult.StatusCode);
	}

	[Fact]
	public async Task FileAdded_When_NotFound_Maps_To_404()
	{
		var handler = CreateHandler(out var external);
		_ = external
			.Setup(x => x.HandleKnowledgePoolFileAddedAsync("kp-1", It.IsAny<RoxFile>(), It.IsAny<CancellationToken>()))
			.Throws(new KeyNotFoundException("missing"));

		var (payload, req) = MakeRequest(
			"{"
				+ "\"type\":\"knowledgepool.file.added\","
				+ "\"fileId\":\"file-missing\","
				+ $"\"knowledgePoolId\":\"kp-1\",\"downloadUrl\":\"{RoxtraBaseUrl}/download/file-missing\",\"title\":\"Doc.pdf\"}}"
		);

		var result = await handler.HandleAsync(payload, req, CancellationToken.None);
		var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
		Assert.Equal(StatusCodes.Status404NotFound, statusResult.StatusCode);
	}

	[Fact]
	public async Task KnowledgePoolMemberAdded_Calls_M365()
	{
		var handler = CreateHandler(out var external);
		var groupGid = Guid.NewGuid();
		var externalGroupId = "ext-group-123";
		_ = external
			.Setup(x => x.HandleKnowledgePoolMemberAddedAsync("kp-888", groupGid, externalGroupId, It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		var (payload, req) = MakeRequest(
			$"{{"
				+ $"\"type\":\"knowledgepool.member.added\","
				+ $"\"knowledgePoolId\":\"kp-888\","
				+ $"\"roxtraGroupGid\":\"{groupGid}\","
				+ $"\"externalGroupId\":\"{externalGroupId}\"}}"
		);

		var result = await handler.HandleAsync(payload, req, CancellationToken.None);

		external.VerifyAll();
		Assert.NotNull(result);
	}

	[Fact]
	public async Task KnowledgePoolMemberRemoved_Calls_M365()
	{
		var handler = CreateHandler(out var external);
		var groupGid = Guid.NewGuid();
		var externalGroupId = "ext-group-456";
		_ = external
			.Setup(x => x.HandleKnowledgePoolMemberRemovedAsync("kp-999", groupGid, externalGroupId, It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		var (payload, req) = MakeRequest(
			$"{{"
				+ $"\"type\":\"knowledgepool.member.removed\","
				+ $"\"knowledgePoolId\":\"kp-999\","
				+ $"\"roxtraGroupGid\":\"{groupGid}\","
				+ $"\"externalGroupId\":\"{externalGroupId}\"}}"
		);

		var result = await handler.HandleAsync(payload, req, CancellationToken.None);

		external.VerifyAll();
		Assert.NotNull(result);
	}

	[Fact]
	public async Task FileAdded_When_DownloadUrl_NotFromRoxtra_ReturnsSecurityError()
	{
		var handler = CreateHandler(out var external);

		var (payload, req) = MakeRequest(
			"{"
				+ "\"type\":\"knowledgepool.file.added\","
				+ "\"fileId\":\"file-malicious\","
				+ "\"knowledgePoolId\":\"kp-1\","
				+ "\"downloadUrl\":\"http://malicious.example.com/download/file\","
				+ "\"title\":\"Doc.pdf\"}"
		);

		var result = await handler.HandleAsync(payload, req, CancellationToken.None);

		var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
		Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
	}
}
