using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using PaymentGateway.Api.Middleware;
using PaymentGateway.Api.Setup;
using PaymentGateway.Data;
using PaymentGateway.Services;
using PaymentGateway.Services.Webhooks;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Payment Gateway API",
        Version = "v1",
        Description = "Gateway de pagamentos com autorização/captura/estorno, " +
                      "ledger de partidas dobradas, idempotência e webhooks."
    });

    // Habilita o botão "Authorize" do Swagger para preencher o X-Api-Key.
    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = ApiKeyMiddleware.HeaderName,
        Description = "API key do lojista (obtida no POST /api/merchants)."
    });

    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("ApiKey", document)] = []
    });
});

var app = builder.Build();

// Cria o banco SQLite na primeira execução e, em desenvolvimento, um lojista
// demo para facilitar testes manuais.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    if (app.Environment.IsDevelopment())
        DbSeeder.Seed(db);
}

app.UseMiddleware<ErrorHandlingMiddleware>();

app.UseSwagger();
app.UseSwaggerUI();

app.UseMiddleware<ApiKeyMiddleware>();

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).ExcludeFromDescription();

app.MapControllers();

app.Run();
