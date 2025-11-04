using System.Text.Json.Serialization;

namespace AiHub.Connector.Webhooks.Events;

public sealed class KnowledgePoolCreatedEvent : IWebhookEvent
{
	public const string EventType = "knowledgepool.created";

	[JsonPropertyName("type")]
	public string Type { get; init; } = EventType;

	[JsonPropertyName("knowledgePoolId")]
	public string KnowledgePoolId { get; init; } = string.Empty;
}
