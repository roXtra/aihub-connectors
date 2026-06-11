using AiHub.Connector.Data;
using AiHub.Connector.ExternalConnectors;
using AiHub.Connector.ExternalConnectors.OpenAiVectorStoreConnector;
using AiHub.Connector.Roxtra;
using AiHub.Connector.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
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

// Bind and validate OpenAi options
builder
	.Services.AddOptions<OpenAiVectorStoreOptions>()
	.Bind(builder.Configuration.GetSection(OpenAiVectorStoreOptions.SectionName))
	.ValidateDataAnnotations()
	.ValidateOnStart();

// HTTP client factory and connector singleton
builder.Services.AddHttpClient();
builder.Services.AddScoped<WebhookHandler>();
builder.Services.AddOptions<WebhooksOptions>().Bind(builder.Configuration.GetSection(WebhooksOptions.SectionName));

// Connector
builder.Services.AddScoped<IExternalConnector, OpenAiVectorStoreConnector>();
builder.Services.AddSingleton<IOpenAiVectorStoreGateway, OpenAiVectorStoreGateway>();
builder.Services.AddHostedService<OpenAiVectorStoreInitializationService>();

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
