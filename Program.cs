using EVoting.Api.Hubs;
using EVoting.Api.Infrastructure;
using EVoting.Api.Middleware;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Serilog;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using StackExchange.Redis;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting EVoting.Api");

    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog ───────────────────────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "EVoting.Api")
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
        .WriteTo.File("logs/evoting-api-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30));

    /*    // ── Database ──────────────────────────────────────────────────────────────
        var connStr = Environment.GetEnvironmentVariable("EVOTING_DB_CONNECTIONSTRING")
                   ?? builder.Configuration.GetConnectionString("DefaultConnection")
                   ?? throw new InvalidOperationException("Database connection string not configured.");

        builder.Services.AddDbContext<ApiDbContext>(opts =>
            opts.UseMySql(connStr, ServerVersion.AutoDetect(connStr),
                mysql => mysql.SchemaBehavior(MySqlSchemaBehavior.Ignore)));*/

    // ── Database ──────────────────────────────────────────────────────────────
    var connStr = Environment.GetEnvironmentVariable("EVOTING_DB_CONNECTIONSTRING")
               ?? builder.Configuration.GetConnectionString("DefaultConnection")
               ?? throw new InvalidOperationException(
                   "Database connection string not configured.");
    EVoting.Api.Security.SecretSettings.RejectPlaceholder("EVOTING_DB_CONNECTIONSTRING / ConnectionStrings:DefaultConnection", connStr);

    var mysqlServerVersionValue =
        Environment.GetEnvironmentVariable("EVOTING_MYSQL_SERVER_VERSION")
        ?? builder.Configuration["Database:ServerVersion"]
        ?? throw new InvalidOperationException(
            "MySQL server version is not configured.");

    if (!Version.TryParse(mysqlServerVersionValue, out var mysqlVersion))
    {
        throw new InvalidOperationException(
            $"Invalid MySQL server version '{mysqlServerVersionValue}'. " +
            "Expected format: major.minor.patch, e.g. 8.0.46.");
    }

    var mysqlServerVersion = new MySqlServerVersion(mysqlVersion);

    builder.Services.AddDbContext<ApiDbContext>(opts =>
        opts.UseMySql(
            connStr,
            mysqlServerVersion,
            mysql => mysql.SchemaBehavior(MySqlSchemaBehavior.Ignore)));

    // ── Redis ─────────────────────────────────────────────────────────────────
    var redisConn = Environment.GetEnvironmentVariable("EVOTING_REDIS_CONNECTION")
                 ?? builder.Configuration["Redis:ConnectionString"];
    EVoting.Api.Security.SecretSettings.RejectPlaceholder("EVOTING_REDIS_CONNECTION / Redis:ConnectionString", redisConn);

    if (!string.IsNullOrWhiteSpace(redisConn))
    {
        builder.Services.AddStackExchangeRedisCache(opts =>
        {
            opts.Configuration = redisConn;
            opts.InstanceName = "evoting:";
        });

        // SignalR backplane: if you ever run two API instances, SignalR messages
        // from instance A are forwarded to clients connected to instance B via Redis.
        // For a single Server PC this is a no-op but costs nothing to add.
        builder.Services.AddSignalR(hubOpts =>
        {
            // FIX: ClientTimeoutInterval must be > KeepAliveInterval so the server
            // doesn't disconnect clients that are alive but temporarily quiet.
            // Default ClientTimeout is 30s which caused the "hasn't sent a ping" disconnect.
            // 60s timeout + 15s keepalive = ping every 15s, drop after 60s of silence.
            hubOpts.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
            hubOpts.KeepAliveInterval = TimeSpan.FromSeconds(15);
            hubOpts.HandshakeTimeout = TimeSpan.FromSeconds(15);
        }).AddStackExchangeRedis(redisConn, opts =>
        {
            opts.Configuration.ChannelPrefix = RedisChannel.Literal("evoting-signalr");
        });

        // The connection string is deliberately NOT logged: a Redis connection string can carry the password.
        Log.Information("Redis enabled (cache + SignalR backplane)");
    }
    else
    {
        builder.Services.AddDistributedMemoryCache();
        // No Redis backplane — standard in-process SignalR (fine for single instance)
        builder.Services.AddSignalR(hubOpts =>
        {
            // FIX: Same timeout config for non-Redis path
            hubOpts.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
            hubOpts.KeepAliveInterval = TimeSpan.FromSeconds(15);
            hubOpts.HandshakeTimeout = TimeSpan.FromSeconds(15);
        });
        Log.Warning("Redis not configured — using in-process SignalR and memory cache.");
    }

    builder.Services.AddMemoryCache();

    // ── Response Compression ──────────────────────────────────────────────────
    builder.Services.AddResponseCompression(opts =>
    {
        opts.EnableForHttps = true;
        opts.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
        opts.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
        opts.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes
            .Concat(new[] { "application/json" });
    });
    builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(opts =>
        opts.Level = System.IO.Compression.CompressionLevel.Fastest);
    builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(opts =>
        opts.Level = System.IO.Compression.CompressionLevel.Fastest);

    // ── JWT Authentication ────────────────────────────────────────────────────
    var jwtKey = Environment.GetEnvironmentVariable("EVOTING_JWT_KEY")
              ?? builder.Configuration["Jwt:Key"];

    if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
        throw new InvalidOperationException(
            "EVOTING_JWT_KEY is not configured or is too short (minimum 32 characters).");

    // A setting that still holds the text of the deployment template is not a secret: refuse to start (the value is not echoed).
    EVoting.Api.Security.SecretSettings.RejectPlaceholder("EVOTING_JWT_KEY / Jwt:Key", jwtKey);
    EVoting.Api.Security.SecretSettings.RejectPlaceholder("EVOTING_PEPPER / Security:PasswordPepper",
        Environment.GetEnvironmentVariable("EVOTING_PEPPER") ?? builder.Configuration["Security:PasswordPepper"]);
    EVoting.Api.Security.SecretSettings.RejectPlaceholder("EVOTING_KIOSK_SECRET / Security:KioskSecret",
        Environment.GetEnvironmentVariable("EVOTING_KIOSK_SECRET") ?? builder.Configuration["Security:KioskSecret"]);

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(opts =>
        {
            opts.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = builder.Configuration["Jwt:Issuer"],
                ValidAudience = builder.Configuration["Jwt:Audience"],
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(jwtKey)),
                ClockSkew = TimeSpan.FromMinutes(1)
            };

            // ── SignalR: read JWT from query string ───────────────────────────
            // The SignalR JS/C# client passes the token as ?access_token=...
            // because WebSocket frames cannot carry custom HTTP headers.
            opts.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;

                    if (!string.IsNullOrEmpty(accessToken) &&
                        path.StartsWithSegments("/hubs"))
                    {
                        context.Token = accessToken;
                    }

                    return Task.CompletedTask;
                }
            };
        });

    builder.Services.AddAuthorization();

    // ── Rate Limiting ─────────────────────────────────────────────────────────
    builder.Services.AddRateLimiter(opts =>
    {
        opts.AddFixedWindowLimiter("vote", o =>
        {
            o.Window = TimeSpan.FromMinutes(1);
            // FIX (MEDIUM): Raised from 5 → 60.
            // The original limit of 5/min was per-server (shared across ALL kiosks).
            // With 30 tablets voting simultaneously at opening rush, tablets 8-30
            // would get HTTP 429 Too Many Requests and voters would be turned away.
            // 60/min = 2× the number of kiosks — safe margin for concurrent voting.
            o.PermitLimit = int.TryParse(
                builder.Configuration["RateLimit:VoteSubmitPerMinute"], out int vl) ? vl : 60;
            o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            // FIX: Raised queue from 2 → 10 to absorb brief spikes without 429.
            o.QueueLimit = 10;
        });

        // ── SPLIT: was one "auth" policy (10/min) shared by all three endpoints.
        // That caused 20 of 30 kiosks to get HTTP 429 at startup (kiosk-login)
        // and voters to be turned away during the opening rush (voter-verify).
        //
        // Now each endpoint has its own policy sized to its real traffic pattern:
        //   admin-login    — human admins, 10/min is plenty, queue=0 (fail fast)
        //   kiosk-login    — device auth at startup only, 10/min is fine
        //   voter-verify   — the hot path; needs kiosks × 3 headroom
        //                    200/min handles up to 60 kiosks with surge room
        //
        // QueueLimit for voter-verify is 20 so a brief spike queues rather than 429.

        opts.AddFixedWindowLimiter("admin-login", o =>
        {
            o.Window = TimeSpan.FromMinutes(1);
            o.PermitLimit = int.TryParse(
                builder.Configuration["RateLimit:AdminLoginPerMinute"], out int al) ? al : 10;
            o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            o.QueueLimit = 0;
        });

        opts.AddFixedWindowLimiter("kiosk-login", o =>
        {
            o.Window = TimeSpan.FromMinutes(1);
            o.PermitLimit = int.TryParse(
                builder.Configuration["RateLimit:KioskLoginPerMinute"], out int kl) ? kl : 10;
            o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            // Small queue: if all kiosks boot simultaneously, queue absorbs the burst
            // instead of immediately returning 429 to half of them.
            o.QueueLimit = 5;
        });

        opts.AddFixedWindowLimiter("voter-verify", o =>
        {
            o.Window = TimeSpan.FromMinutes(1);
            // 200/min ≈ 3× the max kiosk count (60 kiosks).
            // Voter-verify is called once per voter — this limit is never a constraint
            // in normal operation; it only blocks a true DoS flood.
            o.PermitLimit = int.TryParse(
                builder.Configuration["RateLimit:VoterVerifyPerMinute"], out int vv) ? vv : 200;
            o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            // Queue=20: absorbs opening-minute bursts (all kiosks scan at once)
            // without dropping requests. At 200ms average verify latency,
            // 20 queued = at most 4s of queue depth — acceptable wait.
            o.QueueLimit = 20;
        });

        opts.RejectionStatusCode = 429;
    });

    // ── CORS ──────────────────────────────────────────────────────────────────
    // SignalR requires AllowCredentials() when origins are explicitly listed.
    // AllowAnyOrigin() cannot be combined with AllowCredentials() in ASP.NET Core,
    // so if no origins are configured we use the wildcard policy without credentials.
    var allowedOrigins = (Environment.GetEnvironmentVariable("EVOTING_ALLOWED_ORIGINS")
                         ?? builder.Configuration["Cors:AllowedOrigins"]
                         ?? "")
                         .Split(',', StringSplitOptions.RemoveEmptyEntries);

    builder.Services.AddCors(opts =>
    {
        opts.AddPolicy("LanAndWifi", policy =>
        {
            if (allowedOrigins.Length > 0)
            {
                // Explicit origins — allow credentials (required by SignalR)
                policy.WithOrigins(allowedOrigins)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials();   // <-- required for SignalR
            }
            else
            {
                policy.AllowAnyOrigin()
                      .AllowAnyHeader()
                      .AllowAnyMethod();
                // Note: AllowCredentials() cannot be used with AllowAnyOrigin().
                // Set EVOTING_ALLOWED_ORIGINS in production.
            }
        });
    });

    // ── Health Checks ─────────────────────────────────────────────────────────
    var healthBuilder = builder.Services.AddHealthChecks()
        .AddDbContextCheck<ApiDbContext>("mysql", failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "db", "ready" });

    if (!string.IsNullOrWhiteSpace(redisConn))
    {
        healthBuilder.AddRedis(redisConn, "redis",
            failureStatus: HealthStatus.Degraded,
            tags: new[] { "cache", "ready" });
    }

    // ── Application Services ──────────────────────────────────────────────────
    builder.Services.AddScoped<ITokenService, TokenService>();
    builder.Services.AddScoped<IPasswordService, PasswordService>();
    builder.Services.AddScoped<IVoteService, VoteService>();
    builder.Services.AddSingleton<IKioskStatusService, KioskStatusService>();
    builder.Services.AddHostedService<VotingSchedulerService>();

    // ── Admin management API (WPF Admin client) ───────────────────────────────
    // Permission checks are made against the DATABASE on every call (15 s cache), audit rows are written in the
    // SAME transaction as the change they describe, and election open/close has exactly one implementation.
    builder.Services.AddHttpContextAccessor();
    builder.Services.Configure<EligibilityOptions>(builder.Configuration.GetSection(EligibilityOptions.Section));
    builder.Services.AddScoped<EVoting.Api.Security.IPermissionService, EVoting.Api.Security.PermissionService>();
    builder.Services.AddScoped<IAuditService, AuditService>();
    builder.Services.AddScoped<IEventStateService, EventStateService>();

    // ── Monitoring (API + MySQL + Nginx dashboard) ───────────────────────────
    // Singleton: in-memory counters that need to persist across requests for
    // the process's lifetime (request stats, CPU-sample baseline).
    builder.Services.AddSingleton<IRequestMetricsService, RequestMetricsService>();
    builder.Services.AddSingleton<ISystemMonitoringService, SystemMonitoringService>();
    // Scoped: matches ApiDbContext's lifetime — it borrows EF Core's own
    // connection for the duration of a single request.
    builder.Services.AddScoped<IDatabaseMonitoringService, DatabaseMonitoringService>();
    // Singleton: only holds an IHttpClientFactory + IConfiguration, no
    // per-request state.
    builder.Services.AddSingleton<INginxMonitoringService, NginxMonitoringService>();
    // Powers NginxMonitoringService's call to the loopback-only
    // /nginx_status location (see nginx.conf).
    builder.Services.AddHttpClient();

    // ── Controllers ───────────────────────────────────────────────────────────
    builder.Services.AddControllers()
        .AddJsonOptions(opts =>
        {
            opts.JsonSerializerOptions.PropertyNamingPolicy =
                System.Text.Json.JsonNamingPolicy.CamelCase;
            opts.JsonSerializerOptions.DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        });

    // ── Swagger ───────────────────────────────────────────────────────────────
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "E-Voting API",
            Version = "v1",
            Description = "REST + SignalR API for the E-Voting System.",
            Contact = new OpenApiContact { Name = "E-Voting System" }
        });

        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Enter your JWT token."
        });

        c.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                        { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
                },
                Array.Empty<string>()
            }
        });
    });

    // ─────────────────────────────────────────────────────────────────────────
    var app = builder.Build();
    // ─────────────────────────────────────────────────────────────────────────

    app.UseResponseCompression();
    // Outermost so it times the whole pipeline; must come before
    // ExceptionHandlerMiddleware so context.Response.StatusCode is only ever
    // read once it's final (see RequestMetricsMiddleware's own doc comment).
    app.UseMiddleware<RequestMetricsMiddleware>();
    app.UseMiddleware<ExceptionHandlerMiddleware>();
    app.UseSerilogRequestLogging();

    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        KnownProxies = { System.Net.IPAddress.Loopback }
    });

 
    var swaggerEnabled = app.Environment.IsDevelopment() ||
        string.Equals(
            Environment.GetEnvironmentVariable("EVOTING_ENABLE_SWAGGER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    if (swaggerEnabled)
    {
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "E-Voting API v1");
            c.RoutePrefix = "swagger";
        });
    }

    app.UseCors("LanAndWifi");
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();

    // ── SignalR Hub ───────────────────────────────────────────────────────────
    // ws://server/hubs/election?access_token=<jwt>
    app.MapHub<ElectionHub>("/hubs/election");

    // ── Health check endpoints ────────────────────────────────────────────────
    app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = _ => false
    }).AllowAnonymous();

    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready")
    }).AllowAnonymous();

    app.MapHealthChecks("/health/detail", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        ResponseWriter = EVoting.Api.Infrastructure.HealthReportWriter.WriteResponse
    }).RequireAuthorization();

    // FIX (MEDIUM): Root was redirecting to /swagger which 404s in Production
    // (Swagger is disabled outside Development). Redirect to health endpoint instead.
    app.MapGet("/", () => Results.Redirect("/health/live")).AllowAnonymous();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "EVoting.Api terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

return 0;

namespace EVoting.Api.Infrastructure
{
    internal static class HealthReportWriter
    {
        internal static Task WriteResponse(
            Microsoft.AspNetCore.Http.HttpContext context,
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report)
        {
            context.Response.ContentType = "application/json";
            var result = System.Text.Json.JsonSerializer.Serialize(new
            {
                status = report.Status.ToString(),
                entries = report.Entries.ToDictionary(
                    e => e.Key,
                    e => new
                    {
                        status = e.Value.Status.ToString(),
                        description = e.Value.Description,
                        duration = e.Value.Duration.TotalMilliseconds
                    })
            });
            return context.Response.WriteAsync(result);
        }
    }
}