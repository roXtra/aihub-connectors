namespace AiHub.Connector.Webhooks;

public sealed class WebhooksOptions
{
	public const string SectionName = "Webhooks";

	public string ApiKey { get; set; } = string.Empty;
}
