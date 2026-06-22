using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

Console.Title = "Datadog Synthetics & SLO Creator";
Console.WriteLine("=================================================");
Console.WriteLine("🚀 Datadog Synthetics & SLO Creator Program");
Console.WriteLine("=================================================\n");

// Parse arguments
if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

string command = args[0].ToLowerInvariant();
if (command != "create-tests" && command != "tests" && command != "create-slo" && command != "slo" && command != "get-slo-tests" && command != "slo-tests")
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"❌ ERROR: Unknown command: '{args[0]}'");
    Console.ResetColor();
    PrintUsage();
    return 1;
}

// 1. Load configuration
var config = LoadConfig();
if (config == null)
{
    return 1;
}

// 2. Run service logic
using var service = new DatadogService(config);

if (!service.ValidateCredentials())
{
    return 1;
}

if (command == "create-tests" || command == "tests")
{
    return await service.RunCreateTestsAsync();
}
else if (command == "create-slo" || command == "slo")
{
    return await service.RunCreateSloAsync();
}
else if (command == "get-slo-tests" || command == "slo-tests")
{
    if (args.Length < 2)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("❌ ERROR: Missing SLO ID argument.");
        Console.ResetColor();
        Console.WriteLine("Usage: dotnet run -- get-slo-tests <slo_id>");
        return 1;
    }
    string sloId = args[1];
    return await service.RunGetSloTestsAsync(sloId);
}

return 0;

// --- Helper Methods ---

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run -- <command> [arguments]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  create-tests / tests           Create synthetic tests based on urls.json and template.json");
    Console.WriteLine("  create-slo / slo               Create an SLO using the monitor IDs from the last test creation run");
    Console.WriteLine("  get-slo-tests / slo-tests <id> Get all synthetic tests and their target URLs associated with the SLO");
}

static IConfigurationRoot? LoadConfig()
{
    try
    {
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"❌ ERROR: Failed to load configuration: {ex.Message}");
        Console.ResetColor();
        return null;
    }
}
