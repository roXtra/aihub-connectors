namespace AiHub.Connector.ExternalConnectors.M365Connector;

/// <summary>
/// Hosted service that ensures the M365 external connection is created when the service starts.
/// </summary>
public class M365ConnectorInitializationService : IHostedService
{
	private readonly ILogger<M365ConnectorInitializationService> _logger;
	private readonly IServiceProvider _serviceProvider;

	public M365ConnectorInitializationService(ILogger<M365ConnectorInitializationService> logger, IServiceProvider serviceProvider)
	{
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		_logger.LogInformation("Initializing M365 external connection...");

		try
		{
			using var scope = _serviceProvider.CreateScope();
			var m365Connector = scope.ServiceProvider.GetRequiredService<IExternalConnector>();

			// Call the initialization method
			await m365Connector.InitializeAsync(cancellationToken);

			_logger.LogInformation("M365 external connection initialized successfully.");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to initialize M365 external connection.");
			throw; // Rethrow to prevent the host from starting - if initialization fails, there is probably a config issue
		}
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		// Nothing to clean up
		return Task.CompletedTask;
	}
}
