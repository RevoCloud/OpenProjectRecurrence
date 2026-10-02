using OpenProjectRecurrenceService;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddUserSecrets<Program>(optional: true, reloadOnChange: true);

builder.Services.Configure<OpenProjectOptions>(builder.Configuration.GetSection(OpenProjectOptions.SectionName));
builder.Services.AddSystemd();
builder.Services.AddHttpClient<OpenProjectClient>();
builder.Services.AddSingleton<RecurrenceCalculator>();
builder.Services.AddSingleton<GeneratedWorkPackageCopyPolicy>();
builder.Services.AddSingleton<RecurrenceProcessor>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
