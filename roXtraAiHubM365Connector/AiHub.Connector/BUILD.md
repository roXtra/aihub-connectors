# Build From Source

- Install the .NET 10 SDK.
- Run the commands from the repository root.
- Build the full solution first, then publish this connector for `win-x64`.

```powershell
dotnet tool restore
dotnet csharpier check .
dotnet restore .\AiHubConnectors.sln
dotnet build .\AiHubConnectors.sln --configuration Release --no-restore
dotnet test .\AiHubConnectors.sln --configuration Release --no-build --no-restore

dotnet restore .\roXtraAiHubM365Connector\AiHub.Connector\AiHub.Connector.csproj -r win-x64
dotnet build .\roXtraAiHubM365Connector\AiHub.Connector\AiHub.Connector.csproj --configuration Release -r win-x64 --no-restore
dotnet publish .\roXtraAiHubM365Connector\AiHub.Connector\AiHub.Connector.csproj `
  -c Release -r win-x64 --no-build --no-restore `
  -p:PublishSingleFile=true `
  -p:SelfContained=false `
  -p:ContinuousIntegrationBuild=true `
  -o .\out\win-x64
```

The published output will be in `.\out\win-x64` and can be used for installation or packaging.
