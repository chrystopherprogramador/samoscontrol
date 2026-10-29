using Serilog;
using SamosControl.Application;

// Standalone worker host: use it when the Web app runs with RUN_WORKERS=false.
// Run exactly ONE instance (scheduler and monitoring are DB-polling services).
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog((lc) => lc
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console());
builder.Services.AddSamosDataProtection(builder.Configuration);
builder.Services.AddSamosApplication(builder.Configuration, runWorkers: true);

builder.Build().Run();
