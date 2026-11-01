using System.Globalization;
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

Console.Title = "Podere Bot";
Console.WriteLine("========== Podere Bot ==========\n");

DotNetEnv.Env.Load();
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5050");
builder.Configuration.AddEnvironmentVariables();
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
builder.Services.AddSingleton<ModelPricing>();
builder.Services.AddSingleton<UsageTracker>();

builder.Services.AddHostedService<HeatingProgramDaemon>();

builder.Services.AddSingleton<BotHostedService>();
builder.Services.AddHostedService(p => p.GetRequiredService<BotHostedService>());

// ===== AI Configuration =====
var aiSection = builder.Configuration.GetSection("AI");
var endpoint = aiSection["Endpoint"]!;
var model = aiSection["Model"]!;
var zenApiKey = builder.Configuration.GetValue<string>("ZEN_API_KEY")!;

var promptPath = Path.Combine(AppContext.BaseDirectory, "Prompts", "system-prompt.md");
if (File.Exists(promptPath))
{
    var prompt = File.ReadAllText(promptPath);
    var memesPath = Path.Combine(AppContext.BaseDirectory, "Prompts", "memes.json");
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

app.Use(Middleware.RestrictToLocalNetwork);
app.UseApiEndpoints();
app.MapMcp("/mcp");
app.UseSwagger();
app.UseSwaggerUI();

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var loggerProvider = app.Services.GetRequiredService<ILoggerFactory>();
var logger = loggerProvider.CreateLogger(string.Empty);
logger.LogInformation("env: [{env}]", app.Services.GetService<IHostEnvironment>()?.EnvironmentName);
await app.RunAsync();
