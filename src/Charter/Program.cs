using Charter.Configuration;
using Charter.Hosting;
using Charter.Logging;
using Serilog;

// Section 4.1: validate everything once at startup and, if invalid, print *all* problems at once and
// exit non-zero. This runs before the logging pipeline exists, so it writes to stderr directly.
var parsed = CharterConfigParser.Parse();

if (!parsed.IsValid)
{
    await Console.Error.WriteLineAsync("Charter cannot start. Fix the following configuration problems:");
    await Console.Error.WriteLineAsync(parsed.Describe());
    return 1;
}

var config = parsed.ConfigOrThrow();

// The boot subset is projected from the validated config rather than re-read, so the two can never
// disagree about a value they both carry.
var options = config.ToStartupOptions();

Log.Logger = CharterLogging.CreateLogger(options);

// Warnings do not stop startup, but they are the difference between "misconfigured" and "silently
// doing something you did not intend", so they are logged rather than swallowed.
foreach (var warning in parsed.Warnings)
{
    Log.Warning("Configuration: {Problem}", warning.Text);
}

try
{
    // Composition lives in Charter.Hosting.CharterHost so that a test can build the same service
    // graph and the same middleware pipeline this process runs. A suite that assembles its own
    // container proves nothing about the one that gets deployed.
    var app = CharterHost.CreateBuilder(args, config, options).Build();

    await CharterHost.MigrateAsync(app);

    CharterHost.ConfigurePipeline(app);
    CharterHost.LogStartupSummary(options);

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Charter terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
