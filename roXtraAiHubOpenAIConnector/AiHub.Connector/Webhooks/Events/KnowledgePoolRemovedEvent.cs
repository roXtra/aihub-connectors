using System.Text.Json.Serialization;

namespace AiHub.Connector.Webhooks.Events;

public sealed class KnowledgePoolRemovedEvent : IWebhookEvent
{
	public const string EventType = "knowledgepool.removed";

	[JsonPropertyName("type")]
	public string Type { get; init; } = EventType;

	[JsonPropertyName("knowledgePoolId")]
	public string KnowledgePoolId { get; init; } = string.Empty;
}
