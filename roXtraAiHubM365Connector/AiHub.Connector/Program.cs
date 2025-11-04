using AiHub.Connector.Data;
using AiHub.Connector.ExternalConnectors;
using AiHub.Connector.ExternalConnectors.M365Connector;
using AiHub.Connector.Roxtra;
using AiHub.Connector.Webhooks;
using Azure.Core;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Graph;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog(
	(context, services, loggerConfiguration) =>
		loggerConfiguration.ReadFrom.Configuration(context.Configuration).ReadFrom.Services(services).Enrich.FromLogContext()
);

// Enable Windows Service hosting and ensure working directory is the app folder when running as a service
builder.Host.UseWindowsService();
if (WindowsServiceHelpers.IsWindowsService())
{
	Directory.SetCurrentDirectory(AppContext.BaseDirectory);
}

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Bind and validate Roxtra options on startup
builder.Services.AddOptions<RoxtraOptions>().Bind(builder.Configuration.GetSection(RoxtraOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

// Bind and validate Microsoft Graph options
builder.Services.AddOptions<GraphOptions>().Bind(builder.Configuration.GetSection(GraphOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

// HTTP client factory and connector singleton
builder.Services.AddHttpClient();
builder.Services.AddScoped<IExternalConnector, M365Connector>();
builder.Services.AddScoped<WebhookHandler>();
builder.Services.AddOptions<WebhooksOptions>().Bind(builder.Configuration.GetSection(WebhooksOptions.SectionName));
builder.Services.AddSingleton<IPdfTextExtractor, PdfTextExtractor>();

builder.Services.AddHostedService<M365ConnectorInitializationService>();

// Shared TokenCredential and GraphServiceClient using client credentials
builder.Services.AddSingleton<TokenCredential>(sp =>
{
	var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GraphOptions>>().Value;
	return new ClientSecretCredential(opts.TenantId, opts.ClientId, opts.ClientSecret);
});
builder.Services.AddSingleton<GraphServiceClient>(sp =>
{
	var credential = sp.GetRequiredService<TokenCredential>();
	return new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
});
builder.Services.AddSingleton<IGraphExternalClient, GraphExternalClient>();

builder.Services.AddDbContext<ConnectorDbContext>(options =>
	options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=connector.db")
);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
	_ = app.UseSwagger();
	_ = app.UseSwaggerUI();
}

app.UseHttpsRedirection();

using (var scope = app.Services.CreateScope())
{
	var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
	db.Database.Migrate();
}

// Single endpoint to receive incoming webhook events, delegated to handler
app.MapPost(
		"/api/v1/webhooks/events/receive",
		(System.Text.Json.JsonElement payload, HttpRequest request, WebhookHandler handler, CancellationToken ct) => handler.HandleAsync(payload, request, ct)
	)
	.WithName("ReceiveWebhookEvent")
	.WithOpenApi()
	.AddEndpointFilter<AiHub.Connector.Webhooks.ApiKeyEndpointFilter>();

app.Run();
