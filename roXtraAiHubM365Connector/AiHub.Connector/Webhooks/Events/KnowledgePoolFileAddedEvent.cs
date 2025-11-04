using System.Text.Json.Serialization;

namespace AiHub.Connector.Webhooks.Events;

public sealed class KnowledgePoolFileAddedEvent : IWebhookEvent
{
	public const string EventType = "knowledgepool.file.added";

	[JsonPropertyName("type")]
	public string Type { get; init; } = EventType;

	[JsonPropertyName("fileId")]
	public string FileId { get; init; } = string.Empty;

	[JsonPropertyName("knowledgePoolId")]
	public string KnowledgePoolId { get; init; } = string.Empty;

	[JsonPropertyName("downloadUrl")]
	public string DownloadUrl { get; init; } = string.Empty;

	[JsonPropertyName("title")]
	public string Title { get; init; } = string.Empty;

	[JsonPropertyName("supportedForKnowledgePools")]
	public bool SupportedForKnowledgePools { get; init; } = true;
}
