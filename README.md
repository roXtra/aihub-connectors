# roXtra AI Hub Connector Example (Webhook-Based)

This repository demonstrates how to connect roXtra Knowledge Pools to an external service by exposing a webhook endpoint that is called by the roXtra AI Hub service. Incoming events are dispatched to an external connector implementation, which can push content and permissions to your target system.

The included sample targets Microsoft 365 Graph External Connections, but the pattern is generic and easy to adapt to any external system.

## How It Works

![m365 sample connector](/roXtraAiHubM365Connector/docs/images/ai-hub-sample-m365-connector.svg)

- roXtra AI Hub sends events to the connector’s webhook: `POST /api/v1/webhooks/events/receive`.
- Webhook events and payloads: see [Events.md](Events.md)
- Configure/install the sample connector: see [roXtraAiHubM365Connector/AiHub.Connector/README.md](roXtraAiHubM365Connector/AiHub.Connector/README.md)
- Quick test requests (httpyac): see [httpyac/webhook.http](httpyac/webhook.http)

## Bring Your Own Connector

You can implement your own connector that receives webhook events from roXtra AI Hub. Key steps are:
- Implement a webhook endpoint that receives events from roXtra AI Hub listening to `POST https://<your-webhook-host>/api/v1/webhooks/events/receive`.
- Parse incoming events and payloads (see [Events.md](Events.md)).
- Check incoming header `X-Api-Key` for authentication (matches the API key configured in roXtra AI Hub webhook).
- Use the provided `downloadUrl` in file events to download the file content.  
    It is valid for a specific file for 1 hour and points to your roXtra server. It looks like:  
    `https://<roxtraBaseUrl>/aihub/files/download?token=<token>` (e.g., `https://example.roxtra.com/roxtra/aihub/files/download?token=<token>`)  
    Extraction of the token is not required, usage of the full URL is sufficient.
- Implement logic to push content and permissions to your target system based on the events.
- Host the webhook endpoint accessible from your roXtra server.