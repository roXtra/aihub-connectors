using System.Text.Json.Serialization;

namespace AiHub.Connector.Webhooks.Events;

public interface IWebhookEvent
{
	[JsonPropertyName("type")]
	string Type { get; }
}
