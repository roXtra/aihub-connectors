using System.Text.Json.Serialization;

namespace AiHub.Connector.Webhooks.Events;

public sealed class KnowledgePoolMemberAddedEvent : IWebhookEvent
{
	public const string EventType = "knowledgepool.member.added";

	[JsonPropertyName("type")]
	public string Type { get; init; } = EventType;

	[JsonPropertyName("knowledgePoolId")]
	public string KnowledgePoolId { get; init; } = string.Empty;

	[JsonPropertyName("roxtraGroupGid")]
	public Guid RoxtraGroupGid { get; init; } = Guid.Empty;

	// External directory group id (e.g., Entra ID) to add
	[JsonPropertyName("externalGroupId")]
	public string ExternalGroupId { get; init; } = string.Empty;
}
