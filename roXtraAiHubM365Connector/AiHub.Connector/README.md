# roXtra AI Hub M365 Connector - Quick Install

## Install
- From the extracted release ZIP folder (run PowerShell as Administrator):
  `pwsh -File .\setup.ps1`
- If the service exists, you'll be prompted to replace it.

## Configure
- Create `appsettings.Production.json` in the install directory (e.g., `C:\Program Files\roXtraAiHubM365Connector`).
- Adjust the following values for your environment:

```
{
  "Webhooks": {
    "ApiKey": "CHANGE_ME"
  },
  "Roxtra": {
    "RoxtraUrl": "https://your-roxtra/roxtra"
  },
  "Graph": {
    "TenantId": "CHANGE_ME",
    "ClientId": "CHANGE_ME",
    "ClientSecret": "CHANGE_ME",
    "ExternalConnectionId": "CHANGE_ME"
  },
  "ConnectionStrings": {
    "Default": "Data Source=connector.db"
  }
}
```

The `Graph` section must match the Azure app that you created.

- Optional: HTTP binding (host/port) — copy the `Kestrel` section from `appsettings.json` into `appsettings.Production.json` and adjust if you need a different host/port, e.g.:

```
"Kestrel": {
  "Endpoints": {
    "Http": { "Url": "http://localhost:5254" }
  }
}
```

- Optional: HTTPS binding — add an `Https` endpoint and configure a certificate (file or store). Example with a PFX file:

```
"Kestrel": {
  "Endpoints": {
    "Https": {
      "Url": "https://localhost:5255",
      "Certificate": {
        "Path": "C\\path\\to\\cert.pfx",
        "Password": "CHANGE_ME"
      }
    }
  }
}
```

## Start
- After saving, start the service: `Start-Service roXtraAiHubM365Connector`.
- Logs are written to `Logs/roXtraAiHubConnector.log` (under the install directory).

## Wire roXtra AiHub service
- Configure roXtra to call the connector's webhook endpoint by creating/editing `%RoxtraInstallDir%\config\service.aihub.custom.json` with e.g.:

```
{
  "AiHubConnectorWebhooks": {
    "Webhooks": [
      {
        "Name": "M365Connector",
        "Enabled": true,
        "BaseUrl": "http://localhost:5254",
        "ApiKey": "CHANGE_ME",
        "TimeoutSeconds": 300
      }
    ]
  }
}
```

- Adjust `BaseUrl` to the connector's reachable URL (http://localhost:5254 is the default, if it was not changed) and set a secure `ApiKey` that matches `Webhooks:ApiKey` in `appsettings.Production.json`.
 
- Restart the roXtra service that hosts AiHub to begin syncing knowledge pools.
