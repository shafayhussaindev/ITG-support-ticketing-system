using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
// Swashbuckle 10 ships OpenAPI.NET v2, where these types moved out of the
// Microsoft.OpenApi.Models namespace into Microsoft.OpenApi itself.
using Microsoft.OpenApi;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;
using SupportTicketing.Api.Hubs;
using SupportTicketing.Api.Middleware;
using SupportTicketing.Api.Security;
using SupportTicketing.Application;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Auth;
using SupportTicketing.Infrastructure;
using SupportTicketing.Infrastructure.Security;
using SupportTicketing.Workers;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- logging
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "SupportTicketing.Api"));

// ---------------------------------------------------------------- options
builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// ---------------------------------------------------------------- layers
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddSingleton<ITotpValidator, TotpValidator>();
builder.Services.AddScoped<IPermissionResolver, PermissionResolver>();

builder.Services.AddSignalR();

// The SLA sweep runs in-process for now. It is written to be safe if two hosts run it
// at once, so moving it to its own process later needs no code change.
builder.Services.AddOptions<SlaMonitorOptions>()
    .Bind(builder.Configuration.GetSection(SlaMonitorOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHostedService<SlaMonitorService>();
builder.Services.Configure<EmailDispatchOptions>(
    builder.Configuration.GetSection(EmailDispatchOptions.Section));
builder.Services.AddHostedService<EmailDispatchService>();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// ---------------------------------------------------------------- auth
var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
var signingKey = jwtSection["SigningKey"];

if (string.IsNullOrWhiteSpace(signingKey) || signingKey.Length < 32)
{
    throw new InvalidOperationException(
        "Jwt:SigningKey is missing or shorter than 32 characters. " +
        "Set it with 'dotnet user-secrets set \"Jwt:SigningKey\" \"<value>\"' in development, " +
        "or the Jwt__SigningKey environment variable elsewhere. It must never be committed.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.SaveToken = false;

        // Without this the handler rewrites well-known short claim names to long
        // Microsoft URIs — "sub" becomes nameidentifier, "email" becomes the schemas.
        // URI, and so on. Any code reading a claim by the name it was written under
        // then finds nothing, and the failure is silent: the value is simply absent.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ClockSkew = TimeSpan.FromSeconds(
                jwtSection.GetValue("ClockSkewSeconds", 30)),

            // Without this the handler accepts a token signed with "alg":"none" style
            // confusion in some configurations. Pinning the algorithm removes that class of attack.
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // Browsers cannot set headers on a WebSocket handshake, so SignalR
                // passes the token in the query string. Accepted only for the hub
                // path, so a token can never leak into a normal request URL and from
                // there into access logs or a Referer header.
                var accessToken = context.Request.Query["access_token"];

                if (!string.IsNullOrEmpty(accessToken)
                    && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },

            OnAuthenticationFailed = context =>
            {
                if (context.Exception is SecurityTokenExpiredException)
                {
                    context.Response.Headers["X-Token-Expired"] = "true";
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddAuthorization(options =>
{
    // Nothing is anonymous unless an endpoint opts out with [AllowAnonymous].
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// ---------------------------------------------------------------- CORS
const string CorsPolicy = "SupportTicketingSpa";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];


// ------------------------------------------------------------ reverse proxies
// Behind IIS, nginx or App Service the connection is from the proxy, so
// RemoteIpAddress is the proxy's address. Without this every audit row records the
// load balancer rather than the person, and the sign-in rate limiter partitions every
// user in the organization into a single bucket — one attacker would lock out
// everybody, or nobody, depending which way you read it.
//
// KnownProxies matters as much as the middleware: X-Forwarded-For is a request header
// like any other, so accepting it from an arbitrary caller lets that caller choose
// what the audit log says about them. Empty list means the headers are ignored, which
// is the safe default for a direct-to-Kestrel deployment.
var knownProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
var knownNetworks = builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = builder.Configuration.GetValue("ForwardedHeaders:ForwardLimit", 1);

    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();

    foreach (var proxy in knownProxies.Where(p => IPAddress.TryParse(p, out _)))
    {
        options.KnownProxies.Add(IPAddress.Parse(proxy));
    }

    foreach (var network in knownNetworks)
    {
        var parts = network.Split('/');

        if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var prefix)
            && int.TryParse(parts[1], out var length))
        {
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, length));
        }
    }
});

// Checked before anything is wired up, so a misconfigured production instance fails
// at start rather than serving traffic while quietly insecure.
StartupGuard.Validate(builder.Configuration, builder.Environment);

builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
{
    // An allowlist, never AllowAnyOrigin: credentials are sent with these requests.
    policy.WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()
        // Content-Disposition carries the server's own name for an exported file.
        // Without exposing it the browser hides the header cross-origin and every
        // download lands as "download", losing the report name and timestamp.
        .WithExposedHeaders(HttpContextCurrentUser.CorrelationHeader, "Content-Disposition");
}));

// ---------------------------------------------------------------- rate limiting
// Configurable so an operator can tighten them under attack, and so the integration
// suite can raise them — dozens of sign-ins from one loopback address would otherwise
// trip the limiter and produce failures unrelated to what is being tested.
var authPermitLimit = builder.Configuration.GetValue("RateLimiting:Auth:PermitLimit", 10);
var authWindowSeconds = builder.Configuration.GetValue("RateLimiting:Auth:WindowSeconds", 60);
var globalPermitLimit = builder.Configuration.GetValue("RateLimiting:Global:PermitLimit", 300);
var globalWindowSeconds = builder.Configuration.GetValue("RateLimiting:Global:WindowSeconds", 60);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Sign-in is the endpoint worth attacking, so it gets a much tighter budget,
    // keyed by client IP rather than by user (the attacker controls the username).
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authPermitLimit,
            Window = TimeSpan.FromSeconds(authWindowSeconds),
            QueueLimit = 0
        }));

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst(AppClaims.UserId)?.Value
                ?? context.Connection.RemoteIpAddress?.ToString()
                ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = globalPermitLimit,
                Window = TimeSpan.FromSeconds(globalWindowSeconds),
                QueueLimit = 0
            }));
});

// ---------------------------------------------------------------- MVC + docs
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Support Ticketing System API",
        Version = "v1",
        Description = "Enterprise support ticketing platform. All endpoints require a bearer token "
                    + "unless explicitly marked anonymous."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the access token returned by POST /api/v1/auth/login."
    });

    // Swashbuckle 10 takes a factory rather than an instance, because the scheme
    // reference has to be bound to the document being generated.
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        { new OpenApiSecuritySchemeReference("Bearer", document, externalResource: null), [] }
    });

    options.EnableAnnotations();
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<SupportTicketing.Infrastructure.Persistence.AppDbContext>(
        name: "database",
        tags: ["ready"]);

var app = builder.Build();

// ---------------------------------------------------------------- pipeline
app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("CorrelationId",
            httpContext.Response.Headers[HttpContextCurrentUser.CorrelationHeader].ToString());
        diagnosticContext.Set("UserId", httpContext.User.FindFirst(AppClaims.UserId)?.Value);
    };
});

app.Use(async (context, next) =>
{
    // Defence in depth for a JSON API. A strict CSP matters because Swagger UI and any
    // future server-rendered page are served from this origin.
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-Permitted-Cross-Domain-Policies"] = "none";
    headers["Cross-Origin-Resource-Policy"] = "same-origin";

    if (!context.Request.Path.StartsWithSegments("/swagger"))
    {
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    }

    await next();
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Support Ticketing API v1");
        options.DocumentTitle = "Support Ticketing API";
    });
}
else
{
    app.UseHsts();
}

// First in the pipeline: everything downstream — the rate limiter, the audit writer,
// the HTTPS redirect — reads the connection details this corrects.
app.UseForwardedHeaders();

app.UseHttpsRedirection();
app.UseCors(CorsPolicy);
app.UseRateLimiter();
app.UseAuthentication();

// After authentication, before authorization: the check reads a claim from the
// established principal, and it must run for every endpoint rather than only for
// those an attribute was remembered on.
app.UseMiddleware<PasswordChangeRequiredMiddleware>();

app.UseAuthorization();

app.MapControllers();
app.MapHub<TicketHub>("/hubs/tickets");

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous();

// The development seeder is gated inside RunAsync; it refuses to run outside
// Development regardless of how it is invoked.
// Brings a migrated-but-empty database to the point where somebody can sign in:
// the permission catalogue, the system roles, one organization, one administrator.
// Idempotent and safe in every environment, unlike the demo seeder below it.
await SupportTicketing.Infrastructure.Persistence.Seeding.ProductionBootstrapper.RunAsync(app.Services);

await SupportTicketing.Infrastructure.Persistence.Seeding.DevelopmentSeeder.RunAsync(app.Services, app.Environment.EnvironmentName);

// One sign-in per role, for testing. Gated on Development and on an explicit flag,
// and adds accounts only — no fictional company, so it is safe on a database that
// somebody intends to keep.
await SupportTicketing.Infrastructure.Persistence.Seeding.RoleAccountSeeder.RunAsync(app.Services, app.Environment.EnvironmentName);

app.Run();

/// <summary>Exposed so the integration test host can reference this assembly.</summary>
public partial class Program;
