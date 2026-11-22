using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Quipu.Api;
using Quipu.Config;
using Quipu.Mcp;
using Quipu.Services;
using Quipu.Tray;
using Microsoft.Extensions.FileProviders;
using Scalar.AspNetCore;

namespace Quipu;

public static class Program
{
    public static string AppVersion => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "dev";

    [STAThread]
    public static int Main(string[] args)
    {
        var headless = args.Contains("--headless");
        if (headless || args.Contains("--help") || args.Contains("--version")) AttachConsole(-1);

        if (args.Contains("--version")) { Console.WriteLine($"Quipu {AppVersion}"); return 0; }
        if (args.Contains("--help"))
        {
            Console.WriteLine("""
                Quipu — llama.cpp manager for Windows

                  Quipu.exe                 run with a tray icon and open the dashboard
                  Quipu.exe --headless      run without UI (for services / scheduled tasks)
                  Quipu.exe --config PATH   use a specific quipu.yaml
                  Quipu.exe --no-browser    don't open the dashboard on start

                Config lookup: --config, %QUIPU_CONFIG%, quipu.yaml next to the exe, %USERPROFILE%\Quipu\quipu.yaml
                """);
            return 0;
        }

        var configPath = ConfigStore.Locate(args);
        using var mutex = new Mutex(true, @"Local\Quipu.SingleInstance", out var first);
        if (!first)
        {
            // already running: just bring up the dashboard
            try { OpenBrowser(new ConfigStore(configPath, NullLogger()).Current.Server.Listen); } catch { }
            return 0;
        }

        WebApplication app;
        try
        {
            app = BuildApp(args, configPath);
        }
        catch (Exception ex)
        {
            Fatal($"Quipu could not start:\n\n{ex.Message}\n\nConfig: {configPath}", headless);
            return 1;
        }

        var listen = app.Services.GetRequiredService<ConfigStore>().Current.Server;
        if (headless)
        {
            Console.WriteLine($"Quipu {AppVersion} listening on {listen.Listen}  (config: {configPath})");
            app.Run();
            return 0;
        }

        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Fatal($"Quipu could not listen on {listen.Listen}:\n\n{ex.Message}", headless);
            return 1;
        }
        if (listen.OpenBrowserOnStart && !args.Contains("--no-browser")) OpenBrowser(listen.Listen);

        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.Run(new TrayContext(app.Services, () => app.StopAsync().GetAwaiter().GetResult()));
        app.StopAsync().GetAwaiter().GetResult();
        return 0;
    }

    private static WebApplication BuildApp(string[] args, string configPath)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });

        // config first: it decides where we listen and where logs go
        var store = new ConfigStore(configPath, NullLogger());
        var logs = new AppLogProvider(string.IsNullOrWhiteSpace(store.Current.Paths.Logs)
            ? Path.Combine(Path.GetDirectoryName(store.FilePath)!, "logs")
            : store.Current.Paths.Logs);
        store.UseLogger(logs.CreateLogger(typeof(ConfigStore).FullName!));

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        if (args.Contains("--headless")) builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

        var listenUri = new Uri(store.Current.Server.Listen);
        builder.WebHost.UseUrls(store.Current.Server.Listen);
        builder.Configuration["AllowedHosts"] = listenUri.IsLoopback || listenUri.Host == "localhost"
            ? "localhost;127.0.0.1;[::1]"
            : "*";

        builder.Services.AddSingleton(logs);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<SecretStore>();
        builder.Services.AddSingleton<JobService>();
        builder.Services.AddSingleton<GpuService>();
        builder.Services.AddSingleton<BuildManager>();
        builder.Services.AddSingleton<HuggingFaceService>();
        builder.Services.AddSingleton<InstanceManager>();
        builder.Services.AddSingleton<ModelService>();
        builder.Services.AddSingleton<ReconcileService>();
        builder.Services.AddSingleton<PiExportService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ReconcileService>());

        var ua = new ProductInfoHeaderValue("Quipu", AppVersion);
        builder.Services.AddHttpClient(BuildManager.GitHubClient, c =>
        {
            c.DefaultRequestHeaders.UserAgent.Add(ua);
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } gh)
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", gh);
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddHttpClient(BuildManager.DownloadClient, c =>
        {
            c.DefaultRequestHeaders.UserAgent.Add(ua);
            c.Timeout = Timeout.InfiniteTimeSpan;
        });
        builder.Services.AddHttpClient(HuggingFaceService.HttpClientName, c =>
        {
            c.DefaultRequestHeaders.UserAgent.Add(ua);
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddHttpClient("local", c => c.Timeout = TimeSpan.FromSeconds(3));

        // surface binding errors (e.g. malformed JSON) through ErrorMiddleware with a message instead of an empty 400
        builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info.Title = "Quipu API";
            doc.Info.Version = AppVersion;
            doc.Info.Description = "Manage llama.cpp builds, GGUF models, model start parameters, running llama-server instances and Hugging Face tokens.";
            return Task.CompletedTask;
        }));
        builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "quipu", Version = AppVersion })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<QuipuTools>();

        var app = builder.Build();

        app.UseMiddleware<SecurityMiddleware>();
        app.UseMiddleware<ErrorMiddleware>();

        var ui = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = ui });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = ui });

        app.MapOpenApi("/openapi/{documentName}.json");
        app.MapScalarApiReference("/docs", o => o.WithTitle("Quipu API"));
        app.MapQuipuApi();
        app.MapMcp("/mcp");

        return app;
    }

    public static void OpenBrowser(string url)
    {
        var u = url.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1").Replace("+", "127.0.0.1").Replace("*", "127.0.0.1");
        try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
    }

    private static void Fatal(string message, bool headless)
    {
        if (headless) Console.Error.WriteLine(message);
        else System.Windows.Forms.MessageBox.Show(message, "Quipu", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
    }

    private static ILogger<ConfigStore> NullLogger() => Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigStore>.Instance;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
