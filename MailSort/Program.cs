using MailSort.Api;
using MailSort.Components;
using MailSort.Data;
using MailSort.Matching;
using MailSort.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- EF Core / SQLite
var connStr = builder.Configuration.GetConnectionString("MailSort")
              ?? "Data Source=data/mailsort.db";
builder.Services.AddDbContext<MailSortDbContext>(o => o.UseSqlite(connStr));

// --- App services
builder.Services.AddSingleton<ImageStore>();
builder.Services.AddMailSortMatching(builder.Configuration); // binds Match section, registers IMatchEngine
builder.Services.AddScoped<IngestService>();
builder.Services.AddScoped<MailSort.Api.DuplicateScanAnalyzer>();
builder.Services.AddScoped<MailSort.Api.RescanSimulatorService>();

// --- Blazor Server
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddHttpClient();
builder.Services.AddScoped<ApiClient>();

var app = builder.Build();

// --- Apply migrations / create DB
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MailSortDbContext>();
    db.Database.EnsureCreated();
    await TrayMapSeeder.SeedAsync(app.Services, app.Configuration,
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Seeder"));
}

// --- Map endpoints
app.UseAntiforgery();
app.UseStaticFiles(); // wwwroot/ (app.css, favicon, _framework/blazor.web.js)
app.MapMailSortEndpoints();
app.MapImageEndpoints();
app.MapDuplicateScanEndpoints();
app.MapRescanSimulatorEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
