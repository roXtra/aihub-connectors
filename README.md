# roXtra AI Hub Connectors (Webhook-Based)

This repository demonstrates how to connect roXtra Knowledge Pools to an external service by exposing a webhook endpoint that is called by the roXtra AI Hub service. Incoming events are dispatched to an external connector implementation, which can push content and permissions to your target system.

The webhook pattern used here is generic and can be adapted to different target systems. This repository currently includes multiple connector implementations:

- Microsoft 365 Graph External Connections: see [roXtraAiHubM365Connector/AiHub.Connector/README.md](roXtraAiHubM365Connector/AiHub.Connector/README.md)
- OpenAI Vector Store: see [roXtraAiHubOpenAIConnector/AiHub.Connector/README.md](roXtraAiHubOpenAIConnector/AiHub.Connector/README.md)

You can use one of these connectors as-is, adapt one of them to your needs, or implement your own connector against the same webhook contract.

The webhook details in this repository reflect roXtra AI Hub with the latest roXtra version. Older versions of roXtra AI Hub may have different webhook behavior. If you are using an older version of roXtra, please refer to the [Events.md](Events.md) documentation for the specific behavior of your version.

## How It Works

The architecture is the same across connector implementations: roXtra AI Hub sends webhook events, the connector processes those events, and then synchronizes content to the target platform. The diagram below illustrates the interaction between the components in a sample architecture with Microsoft 365 as the target system.

![connector sample architecture](/roXtraAiHubM365Connector/docs/images/ai-hub-sample-m365-connector.svg)

- roXtra AI Hub sends events to the connector's webhook. This can be a custom webhook path or the default webhook path as before: `POST /api/v1/webhooks/events/receive`.
- roXtra AI Hub can include additional custom headers and query parameters on webhook calls. These are intended as customer-specific values that customers configure for their systems and communicate to roXtra so they can be sent with the webhook request.
- Webhook events and payloads: see [Events.md](Events.md)
- Available connector implementations:
  - Microsoft 365 Graph External Connections: [roXtraAiHubM365Connector/AiHub.Connector/README.md](roXtraAiHubM365Connector/AiHub.Connector/README.md)
  - OpenAI Vector Store: [roXtraAiHubOpenAIConnector/AiHub.Connector/README.md](roXtraAiHubOpenAIConnector/AiHub.Connector/README.md)
- Quick test requests (httpyac): see [httpyac/webhook.http](httpyac/webhook.http)

## Bring Your Own Connector

You can implement your own connector that receives webhook events from roXtra AI Hub. Key steps are:

- Implement a webhook endpoint that can receive events from roXtra AI Hub. It could look like this: `POST https://<your-webhook-host>/api/v1/webhooks/events/receive`.
- Parse incoming events and payloads (see [Events.md](Events.md)).
- Check incoming header `X-Api-Key` for authentication (matches the API key configured in roXtra AI Hub).
- Ensure the webhook endpoint works correctly when roXtra AI Hub sends additional customer-specific headers and query parameters required for integration with the target system.
- Use one of the provided `downloadUrls` in file events to download the file content in the provided format.  
   It is valid for a specific file for 1 hour and points to your roXtra server. It looks like:  
   `https://<roxtraBaseUrl>/aihub/files/download?token=<token>&fileExtension=<fileExtension>` (e.g., `https://example.roxtra.com/roxtra/aihub/files/download?token=<token>&fileExtension=pdf`)  
   Extraction of the token is not required, usage of the full URL is sufficient.
- Implement logic to push content and permissions to your target system based on the events.
- Host the webhook endpoint accessible from your roXtra server.
