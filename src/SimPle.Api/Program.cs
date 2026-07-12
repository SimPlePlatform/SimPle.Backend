using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using DotNetEnv.Configuration;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Microsoft.IdentityModel.Tokens;
using SimPle.Api.Middleware;
using SimPle.Api.Models;
using SimPle.Api.OpenApi;
using SimPle.Application;
using SimPle.Application.Auth.Validators;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.GameHost.Services;
using SimPle.Domain.GameHost;
using SimPle.Domain.Games;
using SimPle.Domain.Lobbies;
using SimPle.Infrastructure;
using SimPle.Infrastructure.Auth;
using SimPle.Infrastructure.Capabilities;
using SimPle.Infrastructure.Games;
using SimPle.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    var localEnvPath = Path.Combine(builder.Environment.ContentRootPath, ".env");
    if (File.Exists(localEnvPath))
        builder.Configuration.AddDotNetEnv(localEnvPath);

    // Process-level environment variables override developer .env values.
    builder.Configuration.AddEnvironmentVariables();
}

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "SimPle API",
        Version = "v1",
        Description = "Backend API for SimPle. Auth uses HttpOnly cookies; call login first, then authorize the CSRF header with value XMLHttpRequest for Auth POST actions."
    });
    o.EnableAnnotations();
    o.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "SimPle.Api.xml"));
    o.AddSecurityDefinition(AuthSecurityRequirementsOperationFilter.AccessCookieScheme, new OpenApiSecurityScheme
    {
        Name = "access_token",
        In = ParameterLocation.Cookie,
        Type = SecuritySchemeType.ApiKey,
        Description = "JWT access cookie set automatically by POST /api/auth/login. It is not returned in JSON."
    });
    o.AddSecurityDefinition(AuthSecurityRequirementsOperationFilter.CsrfHeaderScheme, new OpenApiSecurityScheme
    {
        Name = "X-Requested-With",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Description = "Required for Auth POST requests. Enter: XMLHttpRequest"
    });
    o.SchemaFilter<AuthSchemaFilter>();
    o.OperationFilter<AuthSecurityRequirementsOperationFilter>();
    o.DocumentFilter<TagDescriptionsDocumentFilter>();
});

builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();
builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

// The composition root's list of installed Phase 2 game engines. Empty today — Module 5 hosts no product
// game yet, only the test-only HiddenTokenDraft reference engine, which is never registered here. A duplicate
// (Slug, EngineVersion) across two real entries throws from Create() and fails application startup, never a
// call-time ambiguity.
builder.Services.AddSingleton<IGameRegistry>(_ => GameRegistry.Create(Array.Empty<IHostedGameDefinition>()));

builder.Services.AddOptions<JwtSettings>()
    .Bind(builder.Configuration.GetSection(JwtSettings.SectionName))
    .Validate(settings =>
        !string.IsNullOrWhiteSpace(settings.SecretKey) &&
        settings.SecretKey.Length >= 32 &&
        !settings.SecretKey.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase) &&
        !settings.SecretKey.StartsWith("CONFIGURE", StringComparison.OrdinalIgnoreCase),
        "Jwt:SecretKey must be configured outside committed appsettings with at least 32 characters.")
    .ValidateOnStart();

// Module 6 — the server key that keys every lobby join-code / link-token digest. Validated on start for the same
// reason as Jwt:SecretKey: a join code carries only ~60 bits, so an unkeyed or well-known digest would make every
// code in the database offline-guessable. There is deliberately no dev fallback — the module fails closed rather
// than silently degrading to a weak key.
builder.Services.AddOptions<LobbyCredentialOptions>()
    .Bind(builder.Configuration.GetSection(LobbyCredentialOptions.SectionName))
    .Validate(options =>
        !string.IsNullOrWhiteSpace(options.Key) &&
        options.Key.Length >= 32 &&
        !options.Key.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase) &&
        !options.Key.StartsWith("CONFIGURE", StringComparison.OrdinalIgnoreCase),
        "LobbyCredential:Key must be configured outside committed appsettings with at least 32 characters.")
    .Validate(options => LobbyAllowLists.Regions.Contains(options.DefaultRegion),
        "LobbyCredential:DefaultRegion must be an allow-listed region (see LobbyAllowLists.Regions).")
    .ValidateOnStart();

builder.Services.AddOptions<RecaptchaOptions>()
    .Bind(builder.Configuration.GetSection(RecaptchaOptions.SectionName))
    .Validate(options =>
        !string.IsNullOrWhiteSpace(options.SecretKey) &&
        !options.SecretKey.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase) &&
        !options.SecretKey.StartsWith("CONFIGURE", StringComparison.OrdinalIgnoreCase),
        "Recaptcha:SecretKey must be configured outside committed appsettings.")
    .Validate(options => Uri.TryCreate(options.VerificationUrl, UriKind.Absolute, out _),
        "Recaptcha:VerificationUrl must be an absolute URL.")
    .ValidateOnStart();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtSettings>>((options, configuredSettings) =>
    {
        var jwtSettings = configuredSettings.Value;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SecretKey)),
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                context.Token = context.Request.Cookies["access_token"];
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                // Check if this session's family was revoked (e.g. "sign out of device").
                // The 'sid' claim carries the session family ID, which is stable across token rotation.
                var sid = context.Principal?.FindFirst("sid")?.Value;
                if (sid is not null)
                {
                    var jtiStore = context.HttpContext.RequestServices.GetRequiredService<IRevokedJtiStore>();
                    if (jtiStore.IsRevoked(sid))
                    {
                        context.Fail("Session has been revoked.");
                    }
                }
                return Task.CompletedTask;
            },
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new ApiErrorResponse(
                    new ApiErrorDetail("Auth.Unauthorized", "You are not authorized to perform this action.")));
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddCors(options => options.AddPolicy("AllowFrontend", policy =>
    policy.WithOrigins(builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:3000")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

builder.Services.AddOptions<GoogleOptions>()
    .Bind(builder.Configuration.GetSection(GoogleOptions.SectionName))
    .Validate(opts =>
        !string.IsNullOrWhiteSpace(opts.ClientId) &&
        !opts.ClientId.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase) &&
        !opts.ClientId.StartsWith("CONFIGURE", StringComparison.OrdinalIgnoreCase),
        "Google:ClientId must be configured with your OAuth 2.0 client ID from Google Cloud Console.")
    .ValidateOnStart();

builder.Services.AddOptions<EmailOptions>()
    .Bind(builder.Configuration.GetSection(EmailOptions.SectionName))
    .Validate(opts =>
        !string.IsNullOrWhiteSpace(opts.SmtpHost) &&
        !opts.SmtpHost.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase),
        "Email:SmtpHost must be configured (e.g. smtp.gmail.com).")
    .Validate(opts =>
        !string.IsNullOrWhiteSpace(opts.Password) &&
        !opts.Password.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase),
        "Email:Password must be configured with your SMTP password or app password.")
    .Validate(opts =>
        !string.IsNullOrWhiteSpace(opts.From) &&
        !opts.From.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase),
        "Email:From must be configured with your sender email address.")
    .Validate(opts =>
        !string.IsNullOrWhiteSpace(opts.Username) &&
        !opts.Username.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase),
        "Email:Username must be configured with your SMTP username (Gmail address).")
    .ValidateOnStart();

// Forwarded headers — safe by default (only loopback proxies trusted).
// In production behind a reverse proxy, set Infrastructure:KnownProxies to the proxy IP(s).
// Never use ForwardedHeadersDefaults.AllowAll — it would let any client spoof IPs.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Only loopback is trusted by default. Clear built-in networks first.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);

    // Parse additional trusted proxy IPs from configuration (Infrastructure:KnownProxies).
    // Example appsettings: "Infrastructure": { "KnownProxies": ["10.0.0.1", "192.168.1.0/24"] }
    var configuredProxies = builder.Configuration.GetSection("Infrastructure:KnownProxies").Get<string[]>();
    if (configuredProxies is not null)
    {
        foreach (var entry in configuredProxies)
        {
            if (entry.Contains('/'))
            {
                var parts = entry.Split('/');
                if (IPAddress.TryParse(parts[0], out var network) &&
                    int.TryParse(parts[1], out var prefix))
                    options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(network, prefix));
            }
            else if (IPAddress.TryParse(entry, out var proxy))
            {
                options.KnownProxies.Add(proxy);
            }
        }
    }
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        // Surface Retry-After (seconds) so clients back off precisely (spec: rate limits emit Retry-After).
        DateTime? retryAfterUtc = null;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            retryAfterUtc = DateTime.UtcNow + retryAfter;
        }

        // Security event (brief: rate-limit rejections are logged with actor, target, action, result).
        // No correlation-id infrastructure exists in this codebase yet (pre-existing gap, tracked separately).
        var rateLimitLogger = context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>().CreateLogger("RateLimiting");
        var policyName = context.HttpContext.GetEndpoint()?.Metadata
            .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        var actor = context.HttpContext.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "Anonymous";
        rateLimitLogger.LogWarning(
            "Security: Rate limit exceeded. ActorId={ActorId} Policy={Policy} Path={Path} Action={Action} Result={Result}",
            actor, policyName ?? "unknown", context.HttpContext.Request.Path.Value, "RateLimitCheck", "Rejected");

        await context.HttpContext.Response.WriteAsJsonAsync(new ApiErrorResponse(
            new ApiErrorDetail("RateLimit.Exceeded", "Too many requests. Please try again later.", retryAfterUtc)),
            cancellationToken);
    };
    options.AddPolicy("auth-login", context => AuthWindow(context, 5, TimeSpan.FromMinutes(1)));
    options.AddPolicy("auth-register", context => AuthWindow(context, 3, TimeSpan.FromMinutes(1)));
    options.AddPolicy("auth-refresh", context => AuthWindow(context, 10, TimeSpan.FromMinutes(1)));
    options.AddPolicy("auth-check-email", context => AuthWindow(context, 20, TimeSpan.FromMinutes(1)));
    options.AddPolicy("auth-resend-verification", context => AuthWindow(context, 3, TimeSpan.FromMinutes(5)));
    options.AddPolicy("auth-forgot-password", context => AuthWindow(context, 3, TimeSpan.FromMinutes(10)));
    options.AddPolicy("auth-reset-password", context => AuthWindow(context, 5, TimeSpan.FromMinutes(10)));
    options.AddPolicy("auth-google", context => AuthWindow(context, 10, TimeSpan.FromMinutes(1)));
    // Profile mutation: generous but bounded to prevent username enumeration and spam updates.
    options.AddPolicy("profile-update", context => AuthWindow(context, 30, TimeSpan.FromMinutes(1)));
    options.AddPolicy("profile-username", context => AuthWindow(context, 5, TimeSpan.FromMinutes(1)));
    // Public profile reads: per-account when authenticated, coarse per-IP fallback when anonymous (FriendWindow).
    options.AddPolicy("profile-public", context => FriendWindow(context, "ppub", 120, TimeSpan.FromMinutes(1)));
    options.AddPolicy("profile-viewer-context", context => FriendWindow(context, "pctx", 120, TimeSpan.FromMinutes(1)));
    // Friends/social policies: partition by authenticated subject ID so each account has its own window
    // (falls back to remote IP for unauthenticated callers, which [Authorize] normally rejects first).
    // Middleware order below is UseAuthentication() → UseRateLimiter() → UseAuthorization(), so the subject
    // claim is populated here. Rejections carry Retry-After via OnRejected above.
    //
    // The spec's secondary abuse caps are now both enforced: the per-account-target send cap (3/day) is a
    // durable counter on the Friendship row (see FriendsService.SendFriendRequestAsync), and the discovery/
    // people-search per-IP cap (120/hour) is enforced below via GlobalLimiter, chained with these per-account
    // windows so both dimensions must pass.
    options.AddPolicy("friend-send", context => FriendWindow(context, "fsnd", 10, TimeSpan.FromMinutes(1)));
    options.AddPolicy("friend-discovery", context => FriendWindow(context, "fdsc", 30, TimeSpan.FromMinutes(1)));
    options.AddPolicy("friend-suggestions", context => FriendWindow(context, "fsug", 30, TimeSpan.FromMinutes(1)));
    options.AddPolicy("friend-block", context => FriendWindow(context, "fblk", 20, TimeSpan.FromMinutes(1)));
    options.AddPolicy("people-search", context => FriendWindow(context, "psrc", 30, TimeSpan.FromMinutes(1)));
    // Friend/mutual-friend list drill-downs: same generous-but-bounded window as other profile reads.
    options.AddPolicy("profile-friends", context => FriendWindow(context, "pfrd", 120, TimeSpan.FromMinutes(1)));
    options.AddPolicy("profile-mutual-friends", context => FriendWindow(context, "pmut", 120, TimeSpan.FromMinutes(1)));

    // Game catalog policies (per IP for anonymous public reads; per account, IP-fallback for favorites).
    options.AddPolicy("catalog-read", context => AuthWindow(context, 120, TimeSpan.FromMinutes(1)));
    options.AddPolicy("game-favorites", context => FriendWindow(context, "gfav", 60, TimeSpan.FromMinutes(1)));

    // Module 6 lobby policies. All per-account (every lobby route is [Authorize]) with the usual coarse per-IP
    // fallback. Creating a lobby is the expensive one — it mints a credential and takes a seat — so it is the
    // tightest; reads are generous because the lobby page polls.
    //
    // Note that lobby-join throttles *all* join attempts, successful or not. It is NOT the defense against
    // join-code guessing: a window loose enough to let invited members join freely is far too loose to protect a
    // ~60-bit code. That job belongs to ILobbyJoinThrottle, which counts failures only (see
    // MemoryCacheLobbyJoinThrottle) — the two are chained, and both must pass.
    options.AddPolicy("lobby-create", context => FriendWindow(context, "lbcr", 10, TimeSpan.FromMinutes(1)));
    options.AddPolicy("lobby-read", context => FriendWindow(context, "lbrd", 120, TimeSpan.FromMinutes(1)));
    options.AddPolicy("lobby-join", context => FriendWindow(context, "lbjn", 20, TimeSpan.FromMinutes(1)));
    options.AddPolicy("lobby-write", context => FriendWindow(context, "lbwr", 60, TimeSpan.FromMinutes(1)));
    options.AddPolicy("lobby-invite", context => FriendWindow(context, "lbiv", 20, TimeSpan.FromMinutes(1)));

    // Module 6 matchmaking policies (slice 6C). Per-account, with the usual coarse per-IP fallback.
    //
    // matchmaking-status is by far the loosest of the three, and deliberately so: the queue modal polls a ticket
    // every 2 seconds for up to its 60-second deadline, which is ~30 requests per ticket before the user has done
    // anything at all. A limit tight enough to look prudent here would throttle the module's own normal operation.
    // Enqueue is the tight one — it takes the user's single active slot, so repeating it fast is either a bug or an
    // attempt to churn the queue (OWASP API4:2023).
    options.AddPolicy("matchmaking-enqueue", context => FriendWindow(context, "mmen", 10, TimeSpan.FromMinutes(1)));
    options.AddPolicy("matchmaking-status", context => FriendWindow(context, "mmst", 120, TimeSpan.FromMinutes(1)));
    options.AddPolicy("matchmaking-write", context => FriendWindow(context, "mmwr", 30, TimeSpan.FromMinutes(1)));

    // Chained per-IP ceiling for discovery/people-search (spec: 30/min/account + 120/hour/IP), and for
    // catalog search (spec: 120/min/IP catalog-read + 30/min/IP catalog-search when `query` is present).
    // Scoped by request path/query so it only "bites" on these routes; every other route gets a permanent
    // no-op lease.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var path = context.Request.Path;
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (path.StartsWithSegments("/api/people/search") || path.StartsWithSegments("/api/friends/discovery"))
        {
            return RateLimitPartition.GetFixedWindowLimiter($"people-ip:{ip}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
        }

        if (HttpMethods.IsGet(context.Request.Method) &&
            path.StartsWithSegments("/api/games", out var remaining) && remaining == PathString.Empty &&
            !string.IsNullOrEmpty(context.Request.Query["query"]))
        {
            return RateLimitPartition.GetFixedWindowLimiter($"catalog-search-ip:{ip}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
        }

        return RateLimitPartition.GetNoLimiter("no-limit");
    });
});

var app = builder.Build();

// Fail-fast: every installed game engine must agree with its Module 4 catalog row on player bounds and
// modes, or a lobby could advertise a match shape the engine will reject at runtime. Skipped entirely (no DB
// round trip) while zero engines are installed, which is the current state and also keeps WebApplicationFactory
// integration tests that don't touch GameHost from needing a live database just to boot the app.
using (var startupScope = app.Services.CreateScope())
{
    var gameRegistry = startupScope.ServiceProvider.GetRequiredService<IGameRegistry>();
    if (gameRegistry.RegisteredDefinitions.Count > 0)
    {
        var startupDb = startupScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var games = startupDb.Games.AsNoTracking().ToList();
        var gameIds = games.Select(g => g.Id).ToList();
        var modesByGameId = startupDb.GameModeCapabilities.AsNoTracking()
            .Where(c => gameIds.Contains(c.GameId))
            .ToList()
            .GroupBy(c => c.GameId)
            .ToDictionary(g => g.Key, g => (IEnumerable<string>)g.Select(c => c.Mode).ToList());

        var catalogSnapshots = games.Select(g => CatalogGameSnapshot.Create(
            g.Slug,
            g.MinPlayers,
            g.MaxPlayers,
            modesByGameId.TryGetValue(g.Id, out var modes) ? modes : Enumerable.Empty<string>()));

        var catalogValidator = startupScope.ServiceProvider.GetRequiredService<ICatalogEngineCompatibilityValidator>();
        var violations = catalogValidator.Validate(gameRegistry.RegisteredDefinitions, catalogSnapshots);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                "Game engine / catalog compatibility check failed at startup: " + string.Join("; ", violations));
        }
    }
}

// Must be first — sets RemoteIpAddress from X-Forwarded-For before any other middleware reads it.
app.UseForwardedHeaders();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "SimPle API v1");
        options.DocumentTitle = "SimPle Auth API";
        options.DisplayRequestDuration();
    });
}

if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();
app.UseCors("AllowFrontend");
app.UseAuthentication();   // parses JWT cookie and populates User.Identity
app.UseRateLimiter();      // can now read authenticated subject claim for per-user partitions
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", utc = DateTime.UtcNow }));

static RateLimitPartition<string> AuthWindow(HttpContext context, int permitLimit, TimeSpan window) =>
    RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });

// Per-account fixed window for friends/social endpoints. Keyed by the JWT subject claim; unauthenticated
// callers fall back to a per-IP partition under the same quota.
static RateLimitPartition<string> FriendWindow(HttpContext context, string prefix, int permitLimit, TimeSpan window)
{
    var sub = context.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
    var key = sub is not null
        ? $"{prefix}:{sub}"
        : $"{prefix}-ip:{context.Connection.RemoteIpAddress}";
    return RateLimitPartition.GetFixedWindowLimiter(
        key,
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
}

if (args.Contains("--seed-game-catalog"))
{
    using var scope = app.Services.CreateScope();
    var seedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var seedLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<GameCatalogSeeder>();
    var seeder = new GameCatalogSeeder(seedDb, seedLogger);
    var seedResult = seeder.SeedAsync().GetAwaiter().GetResult();
    Console.WriteLine(seedResult.Message);
    Environment.Exit(seedResult.Success ? 0 : 1);
}

// Module 6 — capability profiles (D2). Runs after --seed-game-catalog: every profile is FK'd to games.slug and is
// validated as a subset of its catalog row, so seeding capabilities into an unseeded catalog fails closed with a
// message naming the missing game rather than writing a profile nothing can resolve.
if (args.Contains("--seed-game-capabilities"))
{
    using var scope = app.Services.CreateScope();
    var seedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var seedClock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
    var seedLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<GameCapabilitySeeder>();
    var seeder = new GameCapabilitySeeder(seedDb, seedClock, seedLogger);
    var seedResult = seeder.SeedAsync().GetAwaiter().GetResult();
    Console.WriteLine(seedResult.Message);
    Environment.Exit(seedResult.Success ? 0 : 1);
}

// Local/CI-only lifecycle promotion. GameCatalogSeeder always seeds new games as ComingSoon (Module 4);
// no controller or seeder ever called Game.MakeAvailable(), so no game could reach Available anywhere.
// Mirrors the --seed-game-* flags above: never exposed as an HTTP endpoint, CLI-only.
if (args.Contains("--publish-game"))
{
    var slugIndex = Array.IndexOf(args, "--publish-game") + 1;
    var slug = slugIndex > 0 && slugIndex < args.Length ? args[slugIndex] : null;
    if (string.IsNullOrWhiteSpace(slug))
    {
        Console.WriteLine("--publish-game requires a slug argument.");
        Environment.Exit(1);
    }

    using var scope = app.Services.CreateScope();
    var seedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var game = seedDb.Games.FirstOrDefault(g => g.Slug == slug);
    if (game is null)
    {
        Console.WriteLine($"No game with slug '{slug}' found.");
        Environment.Exit(1);
    }

    if (game!.Lifecycle != GameLifecycle.Available)
    {
        game.MakeAvailable();
        seedDb.SaveChanges();
        Console.WriteLine($"Promoted '{slug}' to Available.");
    }
    else
    {
        Console.WriteLine($"'{slug}' is already Available; no-op.");
    }
    Environment.Exit(0);
}

app.Run();

public partial class Program { }
