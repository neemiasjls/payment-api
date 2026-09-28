using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using PaymentGateway.Api.Middleware;
using PaymentGateway.Api.Setup;
using PaymentGateway.Data;
using PaymentGateway.Services;
using PaymentGateway.Services.Webhooks;
using Serilog;

// Logger provisório: captura falhas que aconteçam ANTES do host subir
// (ex.: erro de configuração), que de outra forma se perderiam.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Logging estruturado: cada log é um evento com propriedades pesquisáveis
    // (ex.: filtrar por PaymentId), não uma linha de texto solta.
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services));

    // Não anuncia o servidor (Kestrel) no cabeçalho "Server": menos
    // informação de graça para quem está sondando a infraestrutura.
    builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

    builder.Services.AddControllers();

    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite(builder.Configuration.GetConnectionString("Default")
                          ?? "Data Source=paymentgateway.db"));

    builder.Services.Configure<GatewayOptions>(
        builder.Configuration.GetSection(GatewayOptions.SectionName));

    builder.Services.AddScoped<IMerchantService, MerchantService>();
    builder.Services.AddScoped<IPaymentService, PaymentService>();

    // Infra de webhooks: fila singleton + serviço em background que a consome.
    builder.Services.AddSingleton<IWebhookQueue, WebhookQueue>();
    builder.Services.AddHostedService<WebhookDispatcher>();
    builder.Services.AddHttpClient(WebhookDispatcher.HttpClientName,
        client => client.Timeout = TimeSpan.FromSeconds(5));

    // Health check que de fato consulta o banco — é o que um load balancer
    // ou orquestrador usa para decidir se a instância recebe tráfego.
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<AppDbContext>();

    // Rate limiting particionado por API key (ou IP, para anônimos):
    // um lojista abusivo não degrada o serviço dos demais.
    var rateLimitPerMinute = builder.Configuration
        .GetSection(GatewayOptions.SectionName)
        .Get<GatewayOptions>()?.RateLimitPerMinute ?? 100;

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Request.Headers[ApiKeyMiddleware.HeaderName].FirstOrDefault()
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = rateLimitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

        options.OnRejected = async (context, ct) =>
        {
            await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Limite de requisições excedido",
                Detail = $"Máximo de {rateLimitPerMinute} requisições por minuto. Aguarde e tente novamente.",
                Instance = context.HttpContext.Request.Path
            }, ct);
        };
    });

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Payment Gateway API",
            Version = "v1",
            Description = "Gateway de pagamentos com autorização/captura/estorno, " +
                          "ledger de partidas dobradas, idempotência e webhooks assinados."
        });

        // Descrições dos comentários /// aparecem no Swagger UI.
        foreach (var xmlFile in Directory.GetFiles(AppContext.BaseDirectory, "PaymentGateway.*.xml"))
            options.IncludeXmlComments(xmlFile);

        // Habilita o botão "Authorize" do Swagger para preencher o X-Api-Key.
        options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyMiddleware.HeaderName,
            Description = "API key do lojista (obtida no POST /api/v1/merchants)."
        });

        options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("ApiKey", document)] = []
        });
    });

    var app = builder.Build();

    // Aplica as migrations pendentes na inicialização (estratégia adequada a
    // uma instância única; com várias réplicas, o passo rodaria no deploy).
    // Nos testes de integração o schema é criado pela própria suíte.
    if (!app.Environment.IsEnvironment("Testing"))
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.Migrate();

        if (app.Environment.IsDevelopment())
            DbSeeder.Seed(db);
    }

    // HTTPS obrigatório fora do ambiente local: HSTS instrui o navegador a
    // nunca mais usar HTTP para este domínio, e o redirecionamento cobre o
    // primeiro acesso. Em Development/Testing a API roda em HTTP puro.
    if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }

    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseSerilogRequestLogging();
    app.UseMiddleware<ErrorHandlingMiddleware>();
    app.UseRateLimiter();

    app.UseSwagger();
    app.UseSwaggerUI();

    app.UseMiddleware<ApiKeyMiddleware>();

    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
    app.MapHealthChecks("/health");

    app.MapControllers();

    app.Run();
}
// HostAbortedException é lançada de propósito pelas ferramentas de design-time
// do EF Core (dotnet ef) — não é uma falha real, então deixamos passar.
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "A aplicação falhou ao iniciar");
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
/// Torna a classe Program visível para o WebApplicationFactory
/// dos testes de integração.
/// </summary>
public partial class Program
{
}
