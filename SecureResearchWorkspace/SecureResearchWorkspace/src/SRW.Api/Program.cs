using SRW.Core.Abstractions;
using SRW.Core.Services;
using SRW.Infrastructure.Azure;
using SRW.Infrastructure.BackgroundJobs;
using SRW.Infrastructure.Kubernetes;
using SRW.Infrastructure.Messaging;
using SRW.Infrastructure.Persistence;
using SRW.Api.Endpoints;
using SRW.Api.Auth;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────
builder.Services.Configure<AzureOptions>(builder.Configuration.GetSection("Azure"));
builder.Services.Configure<CosmosDbOptions>(builder.Configuration.GetSection("Cosmos"));
builder.Services.Configure<ServiceBusOptions>(builder.Configuration.GetSection("ServiceBus"));
builder.Services.Configure<BackgroundJobOptions>(builder.Configuration.GetSection("BackgroundJobs"));

// ── Persistence ───────────────────────────────────────────────────────────────
builder.Services.AddSingleton<CosmosContainerProvider>();
builder.Services.AddDataProtection();

builder.Services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
builder.Services.AddScoped<ISessionRepository, SessionRepository>();
builder.Services.AddScoped<IWorkspaceSecretStore, WorkspaceSecretStore>();

// ── Infrastructure adapters ───────────────────────────────────────────────────
builder.Services.AddSingleton<IAzureStorageProvisioner, AzureStorageProvisioner>();
builder.Services.AddSingleton<IKubernetesOrchestrator, KubernetesOrchestrator>();

// ── Messaging ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IServiceBusPublisher, ServiceBusSenderAdapter>();
builder.Services.AddScoped<IWorkspaceEnqueuer, WorkspaceEnqueuer>();

// ── Application services ──────────────────────────────────────────────────────
builder.Services.AddScoped<WorkspaceProvisioningService>();
builder.Services.AddScoped<SessionLauncher>();

// ── Background workers ────────────────────────────────────────────────────────
builder.Services.AddHostedService<WorkspaceProvisioningConsumer>();
builder.Services.AddHostedService<SessionStatusPoller>();
builder.Services.AddHostedService<IdleSessionReaper>();
builder.Services.AddHostedService<SessionStopConsumer>();
builder.Services.AddHostedService<OrphanResourceCleaner>();
builder.Services.AddHostedService<WorkspaceCleanupConsumer>();

// ── Auth (placeholder — wire Keycloak later via OIDC) ─────────────────────────
builder.Services.AddCurrentUserAccessor();

// ── Web ───────────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Ensure Cosmos DB database and containers exist before accepting traffic.
await app.Services.GetRequiredService<CosmosContainerProvider>().InitializeAsync();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseCurrentUser();    // sets ICurrentUser per request — replace with real OIDC middleware later

app.MapWorkspaceEndpoints();
app.MapSessionEndpoints();
app.MapApplicationEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
