using proje1;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "MigrationWorkerService";
});

builder.Services.AddHostedService<Worker>();

var host = builder.Build();

host.Run();