# roXtra AI Hub OpenAI Connector - Quick Install

## Install

- From the extracted release ZIP folder (run PowerShell as Administrator):
  `pwsh -File .\setup.ps1`
- If the service exists, you'll be prompted to replace it.

## Configure

- Create `appsettings.Production.json` in the install directory (e.g., `C:\Program Files\roXtraAiHubOpenAIConnector`).
- Adjust the following values for your environment:

```
{
  "Webhooks": {
    "ApiKey": "CHANGE_ME"
  },
  "Roxtra": {
    "RoxtraUrl": "https://your-roxtra/roxtra"
  },
   "OpenAI": {
   "ApiKey": "your-open-ai-key",
   "VectorStoreNamePrefix": "roXtra-aihub"
  },
  "ConnectionStrings": {
    "Default": "Data Source=connector.db"
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
        "Path": "C:\\path\\to\\cert.pfx",
        "Password": "CHANGE_ME"
      }
    }
  }
}
```

## Start

- After saving, start the service: `Start-Service roXtraAiHubOpenAIConnector`.
- Logs are written to `Logs/roXtraAiHubConnector.log` (under the install directory).

## Connect roXtra AiHub service

The connection between roXtra AiHub and the external connector is configured by the Roxtra GmbH. To have your knowledge pools synced, please contact roXtra support or your roXtra contact person to set up the connection.

When requesting the connection setup, please provide the following details:

- The connector's webhook URL: `https://<your-host>:<your-port>`

- The `ApiKey` you set in `appsettings.Production.json`
