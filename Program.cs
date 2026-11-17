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

    // ── Registration Station enrolment (non-interactive sign-in) ──────────────
    // Loaded once at start-up. Template text in any of these settings stops start-up (same rule as the secrets above);
    // a malformed individual entry is skipped (that station simply cannot sign in) and named in the log - never its value.
    var stationPolicy = EVoting.Api.Security.StationCredentialSettings.Load(builder.Configuration);
    foreach (var rejected in stationPolicy.RejectedEntries)
        Log.Error("Registration station enrolment entry ignored: {Reason}", rejected);
    if (stationPolicy.IsConfigured)
        Log.Information("Registration station sign-in enabled ({Count} station(s) enrolled individually)", stationPolicy.EnrolledStationCount);
    else
        Log.Warning("Registration station sign-in is NOT configured (EVOTING_REGISTRATION_STATIONS / EVOTING_REGISTRATION_STATION_SECRET); stations cannot sign in non-interactively.");
    builder.Services.AddSingleton(stationPolicy);

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
                },

                // Single session (Security/SessionRegistry): a user token from an older sign-in of the same account is refused.
                OnTokenValidated = async context =>
                {
                    if (context.Principal == null) return;
                    var sessions = context.HttpContext.RequestServices.GetRequiredService<EVoting.Api.Security.ISessionRegistry>();
                    if (!await sessions.IsCurrentAsync(context.Principal, context.HttpContext.RequestAborted))
                    {
                        context.HttpContext.Items[EVoting.Api.Security.SessionRegistry.ReplacedItem] = true;
                        context.Fail(EVoting.Api.Security.SessionRegistry.ReplacedCode);
                    }
                },

                // The refused token gets a 401 WITH a reason, so the app can say why the user was signed out.
                OnChallenge = async context =>
                {
                    if (!context.HttpContext.Items.ContainsKey(EVoting.Api.Security.SessionRegistry.ReplacedItem)) return;
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(
                        new EVoting.Api.Models.Responses.ApiError
                        {
                            Error = EVoting.Api.Security.SessionRegistry.ReplacedMessage,
                            Code = EVoting.Api.Security.SessionRegistry.ReplacedCode
                        },
                        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
                }
            };
        });

    builder.Services.AddAuthorization();

    // ── Rate Limiting ─────────────────────────────────────────────────────────
    builder.Services.AddRateLimiter(opts =>
    {
        // SECURITY: "vote" is counted PER CALLER, no longer one window for the whole server.
        // It used to be global (RateLimit:VoteSubmitPerMinute for everybody together): a single voter replaying his own token in
        // a loop (seen in testing: curl + "goto start") used up that budget and every other voter got 429 - one person could
        // stop the election. Now:
        //   Voter token  -> partition "voter:{memberId}:{eventId}", RateLimit:VotePerVoterPerMinute (default 5). A real voter
        //                   submits once (plus a few safe retries with the same receipt code); a loop gets 429 immediately,
        //                   without touching the database.
        //   Kiosk token  -> partition "kiosk:{kioskId}" (offline-sync of queued ballots), RateLimit:VoteSubmitPerMinute.
        //   anything else -> partition by client IP, small budget.
        opts.AddPolicy("vote", ctx =>
        {
            var u = ctx.User;
            var memberId = u.FindFirst("memberId")?.Value;
            if (u.IsInRole("Voter") && !string.IsNullOrEmpty(memberId))
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"voter:{memberId}:{u.FindFirst("eventId")?.Value}", _ => new FixedWindowRateLimiterOptions
                    {
                        Window = TimeSpan.FromMinutes(1),
                        PermitLimit = Cfg("RateLimit:VotePerVoterPerMinute", 5),
                        QueueLimit = 0
                    });

            var kioskId = u.FindFirst("kioskId")?.Value;
            if (u.IsInRole("Kiosk") && !string.IsNullOrEmpty(kioskId))
                return RateLimitPartition.GetFixedWindowLimiter(
                    "kiosk:" + kioskId.ToUpperInvariant(), _ => new FixedWindowRateLimiterOptions
                    {
                        Window = TimeSpan.FromMinutes(1),
                        PermitLimit = Cfg("RateLimit:VoteSubmitPerMinute", 60),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 10
                    });

            return RateLimitPartition.GetFixedWindowLimiter("ip:" + ClientIp(ctx), _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 10,
                QueueLimit = 0
            });
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

        // SECURITY: the sign-in limits are counted PER CLIENT IP. They used to be one window for the whole server, so anybody on
        // the internet could keep the only administrator (and every kiosk) locked out simply by sending 10 bad logins a minute.
        // The client IP is the real one: nginx sets X-Forwarded-For from CF-Connecting-IP (see nginx.conf) and
        // UseForwardedHeaders runs first in the pipeline.
        static string ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        int Cfg(string key, int fallback) => int.TryParse(builder.Configuration[key], out var v) && v > 0 ? v : fallback;

        opts.AddPolicy("admin-login", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ =>
            new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = Cfg("RateLimit:AdminLoginPerMinute", 10),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

        // Registration desks sign in by hand at opening time, often ~30 of them behind ONE office/venue IP: their own budget,
        // so they neither wait behind each other (seen in the 40-desk test) nor share the administrator's.
        opts.AddPolicy("registration-login", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ =>
            new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = Cfg("RateLimit:RegistrationLoginPerMinute", 60),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 10
            }));

        opts.AddPolicy("kiosk-login", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ =>
            new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = Cfg("RateLimit:KioskLoginPerMinute", 10),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                // Small queue: kiosks behind one NAT that boot together are absorbed instead of half of them getting 429.
                QueueLimit = 5
            }));

        // ADDITIVE: Registration Stations sign themselves in at start-up (a morning rush of stations
        // is one login each, then one every ~8 hours). Failed attempts count against the same budget,
        // so a guessing flood is throttled; the station client treats 429 as "retry later", not as bad credentials.
        opts.AddPolicy("station-login", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ =>
            new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = Cfg("RateLimit:StationLoginPerMinute", 30),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 10
            }));

        // voter-verify is counted PER KIOSK (the kioskId claim of the caller's kiosk token; the limiter runs after authentication).
        // A venue kiosk serves one voter at a time, so it gets a small budget (RateLimit:VoterVerifyPerKioskPerMinute, default 30):
        // a stolen or misbehaving kiosk can no longer use up the whole server's budget and turn every other kiosk's voters away.
        // A mobile gateway (kioskId with the MobileGatewayKioskPrefix, default "MOBILE-") carries every remote voter, so it keeps the
        // large budget (RateLimit:VoterVerifyPerMinute). Per-PERSON limits for the mobile pages belong in Cloudflare (WAF rate
        // limiting rule + Turnstile) because only Cloudflare sees the phone's IP.
        var mobileGatewayPrefix = builder.Configuration["Security:VotingPass:MobileGatewayKioskPrefix"];
        if (string.IsNullOrWhiteSpace(mobileGatewayPrefix)) mobileGatewayPrefix = "MOBILE-";
        opts.AddPolicy("voter-verify", ctx =>
        {
            var kioskId = ctx.User.FindFirst("kioskId")?.Value;
            var key = string.IsNullOrEmpty(kioskId) ? "ip:" + ClientIp(ctx) : "kiosk:" + kioskId.ToUpperInvariant();
            bool mobile = kioskId != null && kioskId.StartsWith(mobileGatewayPrefix, StringComparison.OrdinalIgnoreCase);
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = mobile ? Cfg("RateLimit:VoterVerifyPerMinute", 200) : Cfg("RateLimit:VoterVerifyPerKioskPerMinute", 30),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = mobile ? 20 : 2
            });
        });

        opts.RejectionStatusCode = 429;
        // Tell the caller when to come back (the mobile pages and kiosks honour Retry-After instead of hammering the server).
        opts.OnRejected = (ctx, _) =>
        {
            if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                ctx.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            return ValueTask.CompletedTask;
        };
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
    builder.Services.AddSingleton<EVoting.Api.Security.ISessionRegistry, EVoting.Api.Security.SessionRegistry>();
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

    // ── Representative Election module + centralised eligibility ──────────────
    builder.Services.Configure<RepresentativeImportOptions>(builder.Configuration.GetSection(RepresentativeImportOptions.Section));
    builder.Services.AddScoped<IEligibilityService, EligibilityService>();
    builder.Services.AddScoped<IEventOpenGate, EventOpenGate>();
    builder.Services.AddScoped<IRepresentativeRosterService, RepresentativeRosterService>();
    builder.Services.AddScoped<IVoterLookupService, VoterLookupService>();
    builder.Services.AddScoped<IRepresentativeImportService, RepresentativeImportService>();
    builder.Services.AddScoped<IRosterVersionProvider, RosterVersionProvider>();
    builder.Services.AddScoped<IRepresentativeBallotService, RepresentativeBallotService>();
    builder.Services.AddScoped<IRepresentativeVoteService, RepresentativeVoteService>();
    builder.Services.AddScoped<IRepresentativeResultsService, RepresentativeResultsService>();
    builder.Services.AddSingleton<IRepresentativeLiveUpdates, RepresentativeLiveUpdates>();

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

    // FIRST: the real client IP (nginx on loopback sets X-Forwarded-For) must be in place before anything logs, meters or
    // rate-limits by IP. Only the loopback proxy is trusted, so a client cannot pick its own IP with a forged header.
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        KnownProxies = { System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback }
    });

    app.UseResponseCompression();
    // Outermost so it times the whole pipeline; must come before
    // ExceptionHandlerMiddleware so context.Response.StatusCode is only ever
    // read once it's final (see RequestMetricsMiddleware's own doc comment).
    app.UseMiddleware<RequestMetricsMiddleware>();
    app.UseMiddleware<ExceptionHandlerMiddleware>();
    // A caller that closes the connection (the Admin's search box cancels the previous request) is not a server error: see RequestLogLevel.
    app.UseSerilogRequestLogging(options => options.GetLevel = RequestLogLevel.For);


 
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
    app.UseAuthentication();
    // After authentication, so a policy can partition by the caller's kiosk (voter-verify). Anonymous sign-ins are partitioned by IP.
    app.UseRateLimiter();
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