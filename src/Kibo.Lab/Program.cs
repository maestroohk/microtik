using Kibo.Lab;
using Kibo.Lab.MobileMoney;
using Kibo.Lab.Radius;
using Kibo.Lab.Router;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<LabOptions>(builder.Configuration.GetSection("Sim"));
builder.Services.AddSingleton<RadiusClient>();
builder.Services.AddSingleton<SimulatedRouter>();
builder.Services.AddSingleton<MobileMoneySimulator>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<RouterTicker>();
builder.Services.AddHostedService<DisconnectListener>();

var app = builder.Build();

app.MapRouterApi();
app.MapMobileMoney();
app.MapLabPages();

app.Run();
