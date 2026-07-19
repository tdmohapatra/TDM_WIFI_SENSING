using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using StarSensing.Core.Interfaces;
using StarSensing.Engine.Services;
using StarSensing.Engine.Workers;

namespace StarSensing.Engine;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(5050, o => o.Protocols = HttpProtocols.Http2);
        });

        builder.Services.AddGrpc();

        builder.Services.AddSingleton<SignalAggregator>();
        builder.Services.AddSingleton<EnvironmentStateCache>();
        builder.Services.AddSingleton<IWiFiScanner, WiFiScannerService>();
        builder.Services.AddSingleton<ISignalStore, SqlServerStoreService>();
        builder.Services.AddSingleton<MotionDetectorService>();
        builder.Services.AddSingleton<SmartMotionDetectorService>();
        builder.Services.AddSingleton<IMotionDetector>(sp => sp.GetRequiredService<SmartMotionDetectorService>());
        builder.Services.AddSingleton<ISignalProcessor>(sp => sp.GetRequiredService<SmartMotionDetectorService>());

        builder.Services.AddHostedService<ScanWorker>();

        var app = builder.Build();

        app.MapGrpcService<GrpcSensingService>();
        app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client.");

        app.Run();
    }
}
