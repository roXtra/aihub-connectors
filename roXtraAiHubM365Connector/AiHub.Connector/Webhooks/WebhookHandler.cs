using System.Text.Json;
using AiHub.Connector.ExternalConnectors;
using AiHub.Connector.Roxtra;
using AiHub.Connector.Webhooks.Events;
using Microsoft.Extensions.Options;

namespace AiHub.Connector.Webhooks;

public class WebhookHandler
{
	private readonly IExternalConnector _m365;
	private readonly ILogger<WebhookHandler> _logger;
	private readonly IHttpClientFactory _httpClientFactory;
	private readonly RoxtraOptions _roxtraOptions;

	public WebhookHandler(IExternalConnector m365, ILogger<WebhookHandler> logger, IHttpClientFactory httpClientFactory, IOptions<RoxtraOptions> roxtraOptions)
	{
		_m365 = m365 ?? throw new ArgumentNullException(nameof(m365));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
		_roxtraOptions = roxtraOptions?.Value ?? throw new ArgumentNullException(nameof(roxtraOptions));
	}

	private static int MapExceptionToHttp(Exception ex) =>
		ex switch
		{
			UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
			System.Security.SecurityException => StatusCodes.Status403Forbidden,
			System.Collections.Generic.KeyNotFoundException => StatusCodes.Status404NotFound,
			System.Net.Http.HttpRequestException httpEx when httpEx.StatusCode.HasValue => (int)httpEx.StatusCode.Value,
			System.Net.Http.HttpRequestException => StatusCodes.Status503ServiceUnavailable,
			TaskCanceledException or TimeoutException => StatusCodes.Status504GatewayTimeout,
			_ => StatusCodes.Status502BadGateway,
		};

	private static IResult ProblemFor(Exception ex, string title)
	{
		var statusCode = MapExceptionToHttp(ex);
		return Results.Problem(title: title, statusCode: statusCode);
	}

	private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

	private async Task<IResult> HandleEventAsync<TEvent>(
		JsonElement payload,
		string eventType,
		Func<TEvent, CancellationToken, Task<IResult>> handler,
		CancellationToken ct
	)
	{
		TEvent? evt;
		try
		{
			evt = JsonSerializer.Deserialize<TEvent>(payload.GetRawText(), JsonOptions);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to deserialize {EventType}.", eventType);
			return Results.BadRequest(new { error = "invalid_payload" });
		}

		if (evt is null)
		{
			return Results.BadRequest(new { error = "invalid_payload" });
		}

		return await handler(evt, ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Handles incoming webhook payloads.
	/// </summary>
	/// <param name="payload">Raw JSON payload of the webhook.</param>
	/// <param name="request">Incoming HTTP request (for headers, etc.).</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	public async Task<IResult> HandleAsync(JsonElement payload, HttpRequest request, CancellationToken cancellationToken = default)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			var headers = string.Join(", ", request.Headers.Select(h => $"{h.Key}={h.Value}"));
			_logger.LogInformation("Handling webhook. Headers: {Headers}. Payload: {Payload}", headers, payload.GetRawText());

			if (payload.TryGetProperty("type", out var typeEl))
			{
				var type = typeEl.GetString()?.ToLowerInvariant();
				switch (type)
				{
					case var t when t == KnowledgePoolFileAddedEvent.EventType:
						return await HandleEventAsync<KnowledgePoolFileAddedEvent>(
								payload,
								KnowledgePoolFileAddedEvent.EventType,
								async (evt, ct) =>
								{
									_logger.LogInformation(
										"Received {EventType}: FileId={FileId}, KnowledgePoolId={KnowledgePoolId}",
										KnowledgePoolFileAddedEvent.EventType,
										evt.FileId,
										evt.KnowledgePoolId
									);

									var file = await DownloadFileFromEventAsync(evt.FileId, evt.Title, evt.DownloadUrl, evt.SupportedForKnowledgePools, ct)
										.ConfigureAwait(false);
									await _m365.HandleKnowledgePoolFileAddedAsync(evt.KnowledgePoolId, file, ct).ConfigureAwait(false);
									return Results.Ok(
										new
										{
											status = "processed",
											type = KnowledgePoolFileAddedEvent.EventType,
											fileId = file.Id,
											title = file.Title,
										}
									);
								},
								cancellationToken
							)
							.ConfigureAwait(false);

					case var t when t == KnowledgePoolCreatedEvent.EventType:
						return await HandleEventAsync<KnowledgePoolCreatedEvent>(
								payload,
								KnowledgePoolCreatedEvent.EventType,
								async (evt, ct) =>
								{
									_logger.LogInformation(
										"Received {EventType}: KnowledgePoolId={KnowledgePoolId}",
										KnowledgePoolCreatedEvent.EventType,
										evt.KnowledgePoolId
									);
									await _m365.HandleKnowledgePoolCreatedAsync(evt.KnowledgePoolId, ct).ConfigureAwait(false);
									return Results.Ok(
										new
										{
											status = "processed",
											type = KnowledgePoolCreatedEvent.EventType,
											knowledgePoolId = evt.KnowledgePoolId,
										}
									);
								},
								cancellationToken
							)
							.ConfigureAwait(false);

					case var t when t == FileUpdatedEvent.EventType:
						return await HandleEventAsync<FileUpdatedEvent>(
								payload,
								FileUpdatedEvent.EventType,
								async (evt, ct) =>
								{
									_logger.LogInformation("Received {EventType}: FileId={FileId}", FileUpdatedEvent.EventType, evt.FileId);
									var file = await DownloadFileFromEventAsync(evt.FileId, evt.Title, evt.DownloadUrl, evt.SupportedForKnowledgePools, ct)
										.ConfigureAwait(false);
									await _m365.HandleFileUpdatedAsync(file, ct).ConfigureAwait(false);
									return Results.Ok(
										new
										{
											status = "processed",
											type = FileUpdatedEvent.EventType,
											fileId = file.Id,
										}
									);
								},
								cancellationToken
							)
							.ConfigureAwait(false);

					case var t when t == KnowledgePoolFileRemovedEvent.EventType:
						return await HandleEventAsync<KnowledgePoolFileRemovedEvent>(
								payload,
								KnowledgePoolFileRemovedEvent.EventType,
								async (evt, ct) =>
								{
									_logger.LogInformation(
										"Received {EventType}: FileId={FileId}, KnowledgePoolId={KnowledgePoolId}",
										KnowledgePoolFileRemovedEvent.EventType,
										evt.FileId,
										evt.KnowledgePoolId
									);
									await _m365.HandleKnowledgePoolFileRemovedAsync(evt.KnowledgePoolId, evt.FileId, ct).ConfigureAwait(false);
									return Results.Ok(
										new
										{
											status = "processed",
											type = KnowledgePoolFileRemovedEvent.EventType,
											fileId = evt.FileId,
											knowledgePoolId = evt.KnowledgePoolId,
										}
									);
								},
								cancellationToken
							)
							.ConfigureAwait(false);

					case var t when t == KnowledgePoolMemberAddedEvent.EventType:
						return await HandleEventAsync<KnowledgePoolMemberAddedEvent>(
								payload,
								KnowledgePoolMemberAddedEvent.EventType,
								async (evt, ct) =>
								{
									_logger.LogInformation(
										"Received {EventType}: KnowledgePoolId={KnowledgePoolId}, RoxtraGroupGid={RoxGid}, ExternalGroupId={ExtGid}",
										KnowledgePoolMemberAddedEvent.EventType,
										evt.KnowledgePoolId,
										evt.RoxtraGroupGid,
										evt.ExternalGroupId
									);
									await _m365
										.HandleKnowledgePoolMemberAddedAsync(evt.KnowledgePoolId, evt.RoxtraGroupGid, evt.ExternalGroupId, ct)
										.ConfigureAwait(false);
									return Results.Ok(
										new
										{
											status = "processed",
											type = KnowledgePoolMemberAddedEvent.EventType,
											knowledgePoolId = evt.KnowledgePoolId,
											roxtraGroupGid = evt.RoxtraGroupGid,
											externalGroupId = evt.ExternalGroupId,
										}
									);
								},
								cancellationToken
							)
							.ConfigureAwait(false);

					case var t when t == KnowledgePoolMemberRemovedEvent.EventType:
						return await HandleEventAsync<KnowledgePoolMemberRemovedEvent>(
								payload,
								KnowledgePoolMemberRemovedEvent.EventType,
								async (evt, ct) =>
								{
									_logger.LogInformation(
										"Received {EventType}: KnowledgePoolId={KnowledgePoolId}, RoxtraGroupGid={RoxGid}, ExternalGroupId={ExtGid}",
										KnowledgePoolMemberRemovedEvent.EventType,
										evt.KnowledgePoolId,
										evt.RoxtraGroupGid,
										evt.ExternalGroupId
									);
									await _m365
										.HandleKnowledgePoolMemberRemovedAsync(evt.KnowledgePoolId, evt.RoxtraGroupGid, evt.ExternalGroupId, ct)
										.ConfigureAwait(false);
									return Results.Ok(
										new
										{
											status = "processed",
											type = KnowledgePoolMemberRemovedEvent.EventType,
											knowledgePoolId = evt.KnowledgePoolId,
											roxtraGroupGid = evt.RoxtraGroupGid,
											externalGroupId = evt.ExternalGroupId,
										}
									);
								},
								cancellationToken
							)
							.ConfigureAwait(false);

					case var t when t == KnowledgePoolRemovedEvent.EventType:
						return await HandleEventAsync<KnowledgePoolRemovedEvent>(
								payload,
								KnowledgePoolRemovedEvent.EventType,
								async (evt, ct) =>
								{
									_logger.LogInformation(
										"Received {EventType}: KnowledgePoolId={KnowledgePoolId}",
										KnowledgePoolRemovedEvent.EventType,
										evt.KnowledgePoolId
									);
									await _m365.HandleKnowledgePoolRemovedAsync(evt.KnowledgePoolId, ct).ConfigureAwait(false);
									return Results.Ok(
										new
										{
											status = "processed",
											type = KnowledgePoolRemovedEvent.EventType,
											knowledgePoolId = evt.KnowledgePoolId,
										}
									);
								},
								cancellationToken
							)
							.ConfigureAwait(false);

					default:
						break;
				}
			}

			return Results.Ok(new { status = "received" });
		}
		catch (Exception ex)
		{
			var typeForLog = payload.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
			_logger.LogError(ex, "Failed to handle webhook. Type={Type}, Payload={Payload}", typeForLog ?? "(null)", payload.GetRawText());
			return ProblemFor(ex, "Failed to handle webhook");
		}
	}

	private async Task<RoxFile> DownloadFileFromEventAsync(string fileId, string title, string downloadUrl, bool includeContent, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		if (!includeContent)
		{
			return new RoxFile(fileId, title);
		}

		if (string.IsNullOrWhiteSpace(downloadUrl))
		{
			throw new InvalidOperationException("downloadUrl must be provided in the event payload.");
		}

		// Validate that downloadUrl starts with the configured roxtra base URL to prevent SSRF attacks
		if (!downloadUrl.StartsWith(_roxtraOptions.RoxtraUrl, StringComparison.OrdinalIgnoreCase))
		{
			throw new System.Security.SecurityException(
				$"downloadUrl must start with the configured roxtra base URL. Expected: {_roxtraOptions.RoxtraUrl}, Actual: {downloadUrl}"
			);
		}

		var client = _httpClientFactory.CreateClient();
		_logger.LogInformation("Preparing content stream from {DownloadUrl} for file {FileId}.", downloadUrl, fileId);

		var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
		var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();

		var contentLength = resp.Content.Headers.ContentLength;
		var networkStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
		var responseStream = new HttpResponseStream(resp, networkStream);
		_logger.LogInformation("Prepared content stream for file {FileId}. Length header: {Length}", fileId, contentLength?.ToString() ?? "(unknown)");
		return new RoxFile(fileId, title) { ContentStream = responseStream };
	}
}
