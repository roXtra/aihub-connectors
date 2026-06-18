using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace AiHub.Connector.Webhooks;

public class ApiKeyEndpointFilter : IEndpointFilter
{
	private readonly IOptions<WebhooksOptions> _options;
	private readonly ILogger<ApiKeyEndpointFilter> _logger;

	public ApiKeyEndpointFilter(IOptions<WebhooksOptions> options, ILogger<ApiKeyEndpointFilter> logger)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
	{
		var http = context.HttpContext;
		var configured = _options.Value.ApiKey ?? string.Empty;
		if (string.IsNullOrWhiteSpace(configured))
		{
			_logger.LogWarning("Webhook API key is not configured; rejecting request.");
			return ValueTask.FromResult<object?>(Results.Unauthorized());
		}

		var headers = http.Request.Headers;
		string? provided = null;
		if (headers.TryGetValue("X-Api-Key", out var apiKeyValues))
		{
			provided = apiKeyValues.ToString();
		}
		else if (headers.TryGetValue("Authorization", out var auth) && auth.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
		{
			provided = auth.ToString().Substring("Bearer ".Length);
		}

		if (string.IsNullOrWhiteSpace(provided))
		{
			return ValueTask.FromResult<object?>(Results.Unauthorized());
		}

		var a = Encoding.UTF8.GetBytes(provided);
		var b = Encoding.UTF8.GetBytes(configured);
		var match = a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
		if (!match)
		{
			return ValueTask.FromResult<object?>(Results.Unauthorized());
		}

		return next(context);
	}
}
