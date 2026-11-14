using Winche.Database.AspNetCore.DependencyInjection;
using Winche.Database.AspNetCore.Rest.DependencyInjection;
using Winche.Database.AspNetCore.WebSockets.DependencyInjection;
using Winche.Database.DependencyInjection;
using Winche.Database.Sample.Configurations;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddWincheDatabase(opts =>
    {
        opts.ConnectionString =
            builder.Configuration.GetConnectionString("WincheDatabase") ??
            builder.Configuration.GetConnectionString("DefaultConnection") ??
            throw new InvalidOperationException("No connection string found for WincheDatabase.");

        opts.UseHooks(h =>
        {
            h.Add<DocumentUpdateHook>("{document=**}");
        });

        // Sample-grade identity: the caller's access token IS its uid.
        //
        // The Dart SDK sends whatever its WincheAuthService returns from
        // getAuthToken() as ?access_token= on every (re)dial, so this lets the
        // sample app sign in as different users and see the rules actually
        // isolate their data — which is the point of the demo.
        //
        // This trusts the caller completely. A real deployment validates a
        // signed token and reads the subject out of it; doing what this does
        // anywhere but a local sample would let anyone read anyone's data.
        opts.MapClaims(ctx =>
        {
            var token = ctx.Request.Query["access_token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token)) return null; // unauthenticated -> rules deny
            return new Dictionary<string, object?> { ["uid"] = token };
        });

        opts.UseRulesFile("database.rules");

        opts.UseIndexes(ib =>
        {
           ib.Add("some_collection_id", [new ("uid")]); 
        });
    })
    .AddWincheDatabaseWsApi();

// File and user links in the Flutter demo's Expand tab (Winche.Storage and Winche Auth register the real ones).
builder.Services.AddSingleton<Winche.IWincheReferenceResolver, SampleFiles>();
builder.Services.AddSingleton<Winche.IWincheReferenceResolver, SampleUsers>();

builder.Services.AddOpenApi();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    app.UseHttpsRedirection();
}

await app.InitializeWincheDatabaseAsync();
app.UseWebSockets();
app.MapWincheDatabaseWsApi();
app.MapWincheDatabaseRestApi();

app.Start();
app.WaitForShutdown();
