# roXtra AI Hub Connector Example (Webhook-Based)

This repository demonstrates how to connect roXtra Knowledge Pools to an external service by exposing a webhook endpoint that is called by the roXtra AI Hub service. Incoming events are dispatched to an external connector implementation, which can push content and permissions to your target system.

The included sample targets Microsoft 365 Graph External Connections, but the pattern is generic and easy to adapt to any external system.

## How It Works

![m365 sample connector](/roXtraAiHubM365Connector/docs/images/ai-hub-sample-m365-connector.svg)

- roXtra AI Hub sends events to the connector’s webhook: `POST /api/v1/webhooks/events/receive`.
- Webhook events and payloads: see [Events.md](Events.md)
- Configure/install the sample connector: see [roXtraAiHubM365Connector/AiHub.Connector/README.md](roXtraAiHubM365Connector/AiHub.Connector/README.md)
- Quick test requests (httpyac): see [httpyac/webhook.http](httpyac/webhook.http)
