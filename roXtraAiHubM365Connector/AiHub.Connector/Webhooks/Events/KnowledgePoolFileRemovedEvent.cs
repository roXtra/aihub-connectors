using System.Text.Json.Serialization;

namespace AiHub.Connector.Webhooks.Events;

public sealed class KnowledgePoolFileRemovedEvent : IWebhookEvent
{
	public const string EventType = "knowledgepool.file.removed";

	[JsonPropertyName("type")]
	public string Type { get; init; } = EventType;

	[JsonPropertyName("fileId")]
	public string FileId { get; init; } = string.Empty;

	[JsonPropertyName("knowledgePoolId")]
	public string KnowledgePoolId { get; init; } = string.Empty;
}
