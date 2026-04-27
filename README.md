# roXtra AI Hub Connector Example (Webhook-Based)

This repository demonstrates how to connect roXtra Knowledge Pools to an external service by exposing a webhook endpoint that is called by the roXtra AI Hub service. Incoming events are dispatched to an external connector implementation, which can push content and permissions to your target system.

The included sample targets Microsoft 365 Graph External Connections, but the pattern is generic and easy to adapt to any external system.

The webhook details in this repository reflect roXtra AI Hub with roXtra version 9.137.0.

Earlier behavior:
- The documentation assumed a fixed webhook path at `POST /api/v1/webhooks/events/receive`.
- Additional customer-specific headers and query parameters could not be configured.
- `documentHash` was not sent for `knowledgepool.file.added` and `file.updated`.

## How It Works

![m365 sample connector](/roXtraAiHubM365Connector/docs/images/ai-hub-sample-m365-connector.svg)

- roXtra AI Hub sends events to the connector's webhook. This can be a custom webhook path or the default webhook path as before: `POST /api/v1/webhooks/events/receive`.
- roXtra AI Hub can include additional custom headers and query parameters on webhook calls. These are intended as customer-specific values that customers configure for their systems and communicate to roXtra so they can be sent with the webhook request.
- Webhook events and payloads: see [Events.md](Events.md)
- Configure/install the sample connector: see [roXtraAiHubM365Connector/AiHub.Connector/README.md](roXtraAiHubM365Connector/AiHub.Connector/README.md)
- Quick test requests (httpyac): see [httpyac/webhook.http](httpyac/webhook.http)

## Bring Your Own Connector

You can implement your own connector that receives webhook events from roXtra AI Hub. Key steps are:
- Implement a webhook endpoint that can receive events from roXtra AI Hub. If you use the default route, the webhook URL is `POST https://<your-webhook-host>/api/v1/webhooks/events/receive`. If you use another route, provide the complete webhook URL to roXtra AI Hub.
- Parse incoming events and payloads (see [Events.md](Events.md)).
- Check incoming header `X-Api-Key` for authentication (matches the API key configured in roXtra AI Hub webhook).
- Ensure the webhook endpoint works correctly when roXtra AI Hub sends additional customer-specific headers and query parameters required for integration with the target system.
- Use the provided `downloadUrl` in file events to download the file content.  
    It is valid for a specific file for 1 hour and points to your roXtra server. It looks like:  
    `https://<roxtraBaseUrl>/aihub/files/download?token=<token>` (e.g., `https://example.roxtra.com/roxtra/aihub/files/download?token=<token>`)  
    Extraction of the token is not required, usage of the full URL is sufficient.
- Implement logic to push content and permissions to your target system based on the events.
- Host the webhook endpoint accessible from your roXtra server.