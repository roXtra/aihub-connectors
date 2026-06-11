using System.Net;
using System.Text;
using AiHub.Connector.ExternalConnectors;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiHub.Connector.Tests.Integration;

public class ApiKeyAuthTests : WebApplicationFactory<Program>
{
	protected override IHost CreateHost(IHostBuilder builder)
	{
		builder.UseEnvironment(Environments.Production);
		// Inject test configuration and replace external connector with a no-op implementation
		builder.ConfigureAppConfiguration(cfg =>
		{
			cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Webhooks:ApiKey"] = "test-secret" });
		});

		builder.ConfigureServices(services =>
		{
			services.AddScoped<IExternalConnector, NopExternalConnector>();
		});

		return base.CreateHost(builder);
	}

	private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

	[Fact]
	public async Task Missing_ApiKey_Returns_Unauthorized()
	{
		var client = CreateClient();
		var body = Json("{\"type\":\"knowledgepool.created\",\"knowledgePoolId\":\"kp-x\"}");
		var resp = await client.PostAsync("/api/v1/webhooks/events/receive", body);
		Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
	}

	[Fact]
	public async Task Wrong_ApiKey_Returns_Unauthorized()
	{
		var client = CreateClient();
		client.DefaultRequestHeaders.Add("X-Api-Key", "not-the-secret");
		var body = Json("{\"type\":\"knowledgepool.created\",\"knowledgePoolId\":\"kp-x\"}");
		var resp = await client.PostAsync("/api/v1/webhooks/events/receive", body);
		Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
	}

	// Success path is verified indirectly by unit tests of WebhookHandler and connectors.

	private sealed class NopExternalConnector : IExternalConnector
	{
		public Task HandleKnowledgePoolCreatedAsync(string knowledgePoolId, CancellationToken cancellationToken = default) => Task.CompletedTask;

		public Task HandleKnowledgePoolFileAddedAsync(string knowledgePoolId, Roxtra.RoxFile file, CancellationToken cancellationToken = default) =>
			Task.CompletedTask;

		public Task HandleKnowledgePoolFileRemovedAsync(string knowledgePoolId, string roxFileId, CancellationToken cancellationToken = default) =>
			Task.CompletedTask;

		public Task HandleKnowledgePoolRemovedAsync(string knowledgePoolId, CancellationToken cancellationToken = default) => Task.CompletedTask;

		public Task HandleFileUpdatedAsync(Roxtra.RoxFile file, CancellationToken cancellationToken = default) => Task.CompletedTask;

		public Task HandleKnowledgePoolMemberAddedAsync(
			string knowledgePoolId,
			Guid roxtraGroupGid,
			string externalGroupId,
			CancellationToken cancellationToken = default
		) => Task.CompletedTask;

		public Task HandleKnowledgePoolMemberRemovedAsync(
			string knowledgePoolId,
			Guid roxtraGroupGid,
			string externalGroupId,
			CancellationToken cancellationToken = default
		) => Task.CompletedTask;

		public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
	}
}
