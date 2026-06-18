using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiHub.Connector.ExternalConnectors.OpenAiVectorStoreConnector;

public sealed class OpenAiVectorStoreInitializationService : IHostedService
{
	private readonly ILogger<OpenAiVectorStoreInitializationService> _logger;
	private readonly IServiceProvider _serviceProvider;

	public OpenAiVectorStoreInitializationService(ILogger<OpenAiVectorStoreInitializationService> logger, IServiceProvider serviceProvider)
	{
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		_logger.LogInformation("Starting OpenAI Vector Store connector initialization");

		using var scope = _serviceProvider.CreateScope();
		var connector = scope.ServiceProvider.GetRequiredService<IExternalConnector>();
		await connector.InitializeAsync(cancellationToken).ConfigureAwait(false);

		_logger.LogInformation("OpenAI Vector Store connector initialization completed");
	}

	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
