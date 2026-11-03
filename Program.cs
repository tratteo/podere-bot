using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI;
using PodereBot.Lib;
using PodereBot.Lib.Api;
using PodereBot.Services;
using PodereBot.Services.AiTools;
using PodereBot.Services.Hosted;
using PodereBot.Services.Panel;

Console.Title = "Podere Bot";
Console.WriteLine("========== Podere Bot ==========\n");

DotNetEnv.Env.Load();
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();
var panelOptions = OptionsReader.ReadPanel(builder.Configuration);
builder.WebHost.UseUrls(
    $"http://0.0.0.0:5050;http://127.0.0.1:{PanelOptions.KioskPort};http://0.0.0.0:{panelOptions.LanPort}"
);
builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);
builder.Services.AddCommands();
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<ITemperatureDriver, MockTemperatureDriver>();
    builder.Services.AddSingleton<IPinDriver, MockPinDriver>();
}
else
{
    builder.Services.AddSingleton<ITemperatureDriver, OneWireEmbeddedTemperatureDriver>();
    if (string.IsNullOrEmpty(builder.Configuration.GetValue<string>("SerialPort")))
    {
        builder.Services.AddSingleton<IPinDriver, EmbeddedPinDriver>();
    }
    else
    {
        builder.Services.AddSingleton<IPinDriver, SerialPinDriver>();
    }
}

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<GateDriver>();
builder.Services.AddSingleton<HeatingDriver>();
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<PendingInputRegistry>();
builder.Services.AddSingleton<UserDirectory>();
builder.Services.AddSingleton<StartupGate>();
builder.Services.AddSingleton<ModelPricing>();
builder.Services.AddSingleton<UsageTracker>();
builder.Services.AddSingleton<GateAccessService>();
builder.Services.AddSingleton<HealthService>();

builder.Services.AddSingleton<PanelStore>();
builder.Services.AddSingleton<NotificationLog>();
builder.Services.AddSingleton<PanelDeviceService>();
builder.Services.AddSingleton<HeatingControlService>();
builder.Services.AddSingleton<BoilerNotifier>();
builder.Services.AddSingleton<GateControlService>();
builder.Services.AddSingleton<HeatingStatusService>();
builder.Services.AddSingleton<AdminScopeNotifier>();
builder.Services.AddSingleton<UserAdminService>();
builder.Services.AddSingleton<PanelUserNames>();
builder.Services.AddSingleton<UpdateStateStore>();
builder.Services.AddSingleton<WeatherService>();
builder.Services.AddSingleton<PanelSnapshotService>();

builder.Services.AddHostedService<HeatingProgramDaemon>();
builder.Services.AddHostedService<HeatingMonitorDaemon>();
builder.Services.AddHostedService<GateAccessExpiryDaemon>();
builder.Services.AddHostedService<TemperatureHistoryRecorder>();

builder.Services.AddSingleton<BotHostedService>();
builder.Services.AddHostedService(p => p.GetRequiredService<BotHostedService>());

// ===== AI Configuration =====
var aiSection = builder.Configuration.GetSection("AI");
var endpoint = aiSection["Endpoint"]!;
var model = aiSection["Model"]!;
var zenApiKey = builder.Configuration.GetValue<string>("ZEN_API_KEY")!;

var promptPath = Path.Combine(AppContext.BaseDirectory, "prompts", "system-prompt.md");
if (File.Exists(promptPath))
{
    var prompt = File.ReadAllText(promptPath);
    var memesPath = Path.Combine(AppContext.BaseDirectory, "prompts", "memes.json");
    if (File.Exists(memesPath))
        prompt += "\n\n## Catalogo meme (JSON)\n" + File.ReadAllText(memesPath);
    builder.Configuration["AI:SystemPrompt"] = prompt;
}

builder.Services.AddSingleton<IChatClient>(sp =>
{
    var openAiOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
    openAiOptions.AddPolicy(new OpenCodeSessionPolicy(), PipelinePosition.PerCall);
    var openAiClient = new OpenAIClient(new ApiKeyCredential(zenApiKey), openAiOptions);
    var innerClient = openAiClient.GetChatClient(model).AsIChatClient();
    return new ChatClientBuilder(innerClient).UseFunctionInvocation().Build(sp);
});

builder.Services.AddSingleton<AiToolRegistry>();
builder.Services.AddTransient<AiChatService>();

// ===== MCP Configuration =====
builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true);

// Tools are resolved from the real container when the server options are first built,
// so no secondary service provider is needed at startup.
builder.Services
    .AddOptions<McpServerOptions>()
    .Configure<IServiceProvider>(
        (options, provider) =>
        {
            var collection = new McpServerPrimitiveCollection<McpServerTool>();
            foreach (var tool in provider.GetRequiredService<AiToolRegistry>().AllMcpServerTools)
                collection.Add(tool);
            options.ToolCollection = collection;
        }
    );

var app = builder.Build();

app.Services.GetRequiredService<ModelPricing>();
app.Services.GetRequiredService<WeatherService>();

var apiToken = builder.Configuration["API_TOKEN"];
if (string.IsNullOrWhiteSpace(apiToken))
    apiToken = builder.Configuration["Api:Token"];

// The panel is never served by the REST port: the kiosk reaches it on loopback, devices on the
// dedicated LAN port. Everything else is a 404, not a redirect.
app.Use(
    async (context, next) =>
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/panel"))
        {
            await next();
            return;
        }

        var port = context.Connection.LocalPort;
        if (port == panelOptions.LanPort)
        {
            if (!panelOptions.AllowLan)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            context.Items[PanelContext.ModeItem] = "lan";
        }
        else if (port == PanelOptions.KioskPort)
        {
            var remote = context.Connection.RemoteIpAddress;
            if (remote == null || !IPAddress.IsLoopback(remote))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            context.Items[PanelContext.ModeItem] = "kiosk";
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        await next();
    }
);

app.Use(Middleware.RestrictToLocalNetwork);

if (!string.IsNullOrWhiteSpace(apiToken))
{
    var expected = Encoding.UTF8.GetBytes(apiToken);
    app.Use(
        async (context, next) =>
        {
            var path = context.Request.Path;
            var protectedPath =
                path.StartsWithSegments("/api")
                || path.StartsWithSegments("/mcp")
                || (path.StartsWithSegments("/panel/api") && context.Connection.LocalPort == PanelOptions.KioskPort);

            if (protectedPath)
            {
                var provided = context.Request.Headers["X-Api-Token"].ToString();
                var candidate = Encoding.UTF8.GetBytes(provided);
                if (
                    candidate.Length != expected.Length
                    || !CryptographicOperations.FixedTimeEquals(candidate, expected)
                )
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }
            await next();
        }
    );
}

// LAN devices authenticate with the HttpOnly cookie handed out by the one-time pairing link.
// The pairing endpoint, the logout and the session probe are the only public panel API routes.
var panelDevices = app.Services.GetRequiredService<PanelDeviceService>();
app.Use(
    async (context, next) =>
    {
        var path = context.Request.Path;
        if (context.Connection.LocalPort != panelOptions.LanPort || !path.StartsWithSegments("/panel/api"))
        {
            await next();
            return;
        }

        var isPublic =
            path == "/panel/api/pair" || path == "/panel/api/whoami" || path == "/panel/api/logout";
        if (isPublic)
        {
            await next();
            return;
        }

        var session = panelDevices.Authenticate(context.Request.Cookies[PanelDeviceService.CookieName]);
        if (session == null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Sessione del pannello non valida." });
            return;
        }
        context.Items[PanelContext.SessionItem] = session;

        var method = context.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method))
        {
            if (!string.Equals(context.Request.Headers["X-Panel"].ToString(), "1", StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var origin = context.Request.Headers.Origin.ToString();
            if (
                !string.IsNullOrWhiteSpace(origin)
                && (
                    !Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
                    || !string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        await next();
    }
);

app.UseDefaultFiles();
app.UseStaticFiles(
    new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            if (ctx.Context.Request.Path.StartsWithSegments("/panel"))
                ctx.Context.Response.Headers.CacheControl = "no-store";
        },
    }
);

app.UseApiEndpoints();
app.MapMcp("/mcp");
app.UseSwagger();
app.UseSwaggerUI();

// The pairing link and the login redirect are extensionless; serve the matching page while
// keeping the query string intact, so the token reaches pair.html.
app.MapGet(
    "/panel/pair",
    (IWebHostEnvironment env) =>
        Results.File(
            Path.GetFullPath(Path.Join(env.ContentRootPath, "wwwroot", "panel", "pair.html")),
            "text/html; charset=utf-8"
        )
);
app.MapGet(
    "/panel/login",
    (IWebHostEnvironment env) =>
        Results.File(
            Path.GetFullPath(Path.Join(env.ContentRootPath, "wwwroot", "panel", "login.html")),
            "text/html; charset=utf-8"
        )
);

// The panel lives under /panel; the bare address on a panel port lands there instead of a 404.
app.MapGet(
    "/",
    (HttpContext context) =>
    {
        var port = context.Connection.LocalPort;
        return port == PanelOptions.KioskPort || port == panelOptions.LanPort
            ? Results.Redirect("/panel/")
            : Results.NotFound();
    }
);

// The kiosk needs the machine token to call the panel API; a LAN device never receives it.
app.MapGet(
    "/panel/config.js",
    (HttpContext context, UpdateStateStore updates) =>
    {
        var mode = context.Items[PanelContext.ModeItem] as string ?? "lan";
        var session =
            mode == "lan"
                ? panelDevices.Authenticate(context.Request.Cookies[PanelDeviceService.CookieName])
                : PanelContext.Session(context);
        var config = new
        {
            mode,
            token = mode == "kiosk" ? apiToken : null,
            version = updates.Version,
            device = session?.Name,
        };
        var json = JsonSerializer.Serialize(config);
        return Results.Text($"window.PANEL_CONFIG = {json};", "application/javascript; charset=utf-8");
    }
);

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var loggerProvider = app.Services.GetRequiredService<ILoggerFactory>();
var logger = loggerProvider.CreateLogger(string.Empty);
logger.LogInformation("env: [{env}]", app.Services.GetService<IHostEnvironment>()?.EnvironmentName);
await app.RunAsync();
