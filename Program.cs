using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

Console.Title = "Datadog Synthetics & SLO Creator";
Console.WriteLine("=================================================");
Console.WriteLine("🚀 Datadog Synthetics & SLO Creator Program Initializing");
Console.WriteLine("=================================================\n");

// 1. Load configuration
var configBuilder = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

var config = configBuilder.Build();

string apiKey = config["Datadog:ApiKey"] ?? string.Empty;
string appKey = config["Datadog:AppKey"] ?? string.Empty;
string site = config["Datadog:Site"] ?? "datadoghq.com";

// 2. Validate configurations
if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_DATADOG_API_KEY" ||
    string.IsNullOrWhiteSpace(appKey) || appKey == "YOUR_DATADOG_APPLICATION_KEY")
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("⚠️ WARNING: Datadog API/Application keys are not configured.");
    Console.WriteLine("Please edit 'appsettings.json' and replace the placeholder values with your real keys.");
    Console.WriteLine("\nRequired keys in appsettings.json:");
    Console.WriteLine("  - Datadog.ApiKey");
    Console.WriteLine("  - Datadog.AppKey");
    Console.ResetColor();
    return 1;
}

// 3. Verify input files exist
const string templatePath = "template.json";
const string urlsPath = "urls.json";

if (!File.Exists(templatePath))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"❌ ERROR: Template file '{templatePath}' not found.");
    Console.ResetColor();
    return 1;
}

if (!File.Exists(urlsPath))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"❌ ERROR: URLs input file '{urlsPath}' not found.");
    Console.ResetColor();
    return 1;
}

// 4. Load inputs
Console.WriteLine("📖 Reading template.json and urls.json...");
JsonNode? templateNode;
JsonArray? urlsArray;

try
{
    var templateContent = File.ReadAllText(templatePath);
    templateNode = JsonNode.Parse(templateContent);
    if (templateNode == null)
    {
        throw new InvalidOperationException("Template parsed as null.");
    }
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"❌ ERROR parsing 'template.json': {ex.Message}");
    Console.ResetColor();
    return 1;
}

try
{
    var urlsContent = File.ReadAllText(urlsPath);
    var parsedNode = JsonNode.Parse(urlsContent);
    urlsArray = parsedNode as JsonArray;
    if (urlsArray == null)
    {
        throw new InvalidOperationException("Input file must be a JSON array.");
    }
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"❌ ERROR parsing 'urls.json': {ex.Message}");
    Console.ResetColor();
    return 1;
}

Console.WriteLine($"Loaded {urlsArray.Count} target URL(s) to create.\n");

// 5. Initialize HTTP Client
using var httpClient = new HttpClient();
httpClient.BaseAddress = new Uri($"https://api.{site}");
httpClient.DefaultRequestHeaders.Add("DD-API-KEY", apiKey);
httpClient.DefaultRequestHeaders.Add("DD-APPLICATION-KEY", appKey);
httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

var createdMonitorIds = new List<long>();
var createdTestDetails = new List<(string Name, string PublicId, long MonitorId)>();

Console.WriteLine("-------------------------------------------------");
Console.WriteLine("🛠️ Starting Creation of Synthetic API Tests...");
Console.WriteLine("-------------------------------------------------");

foreach (var itemNode in urlsArray)
{
    if (itemNode == null) continue;

    var item = itemNode.AsObject();
    string? url = item["url"]?.ToString();
    string? name = item["name"]?.ToString();

    if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(name))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"⚠️ Skipping invalid url entry: Name='{name}', URL='{url}'");
        Console.ResetColor();
        continue;
    }

    Console.WriteLine($"\n⚙️ Processing: '{name}' ({url})...");

    // Clone the template
    var testNode = templateNode.DeepClone()!.AsObject();

    // Strip read-only properties that may come from an API template dump
    string[] readOnlyFields = { "public_id", "monitor_id", "creator", "created_at", "modified_at" };
    foreach (var field in readOnlyFields)
    {
        testNode.Remove(field);
    }

    // Overwrite Dynamic values
    testNode["name"] = name;

    // Overwrite the URL in config.request.url
    var configSection = testNode["config"]?.AsObject();
    if (configSection != null)
    {
        var requestSection = configSection["request"]?.AsObject();
        if (requestSection != null)
        {
            requestSection["url"] = url;
        }
        else
        {
            configSection["request"] = new JsonObject
            {
                ["url"] = url,
                ["method"] = "GET",
                ["timeout"] = 30
            };
        }
    }
    else
    {
        testNode["config"] = new JsonObject
        {
            ["request"] = new JsonObject
            {
                ["url"] = url,
                ["method"] = "GET",
                ["timeout"] = 30
            }
        };
    }

    // Merge Tags
    var combinedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (testNode["tags"] is JsonArray templateTagsArray)
    {
        foreach (var tag in templateTagsArray)
        {
            if (tag != null) combinedTags.Add(tag.ToString());
        }
    }
    if (item["tags"] is JsonArray itemTagsArray)
    {
        foreach (var tag in itemTagsArray)
        {
            if (tag != null) combinedTags.Add(tag.ToString());
        }
    }
    testNode["tags"] = new JsonArray(combinedTags.Select(t => (JsonNode)t).ToArray());

    // Send Creation Request
    try
    {
        var jsonPayload = testNode.ToJsonString();
        var payloadContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync("/api/v1/synthetics/tests", payloadContent);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            var resNode = JsonNode.Parse(responseContent)?.AsObject();
            string? publicId = resNode?["public_id"]?.ToString();
            long? monitorId = resNode?["monitor_id"]?.GetValue<long>();

            if (monitorId.HasValue && !string.IsNullOrEmpty(publicId))
            {
                createdMonitorIds.Add(monitorId.Value);
                createdTestDetails.Add((name, publicId, monitorId.Value));

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✅ Successfully created Synthetic Test!");
                Console.WriteLine($"   Public ID:  {publicId}");
                Console.WriteLine($"   Monitor ID: {monitorId.Value}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("⚠️ Test created, but API did not return a valid public_id or monitor_id.");
                Console.WriteLine($"Response: {responseContent}");
                Console.ResetColor();
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"❌ Failed to create Synthetic test. Status: {response.StatusCode}");
            Console.WriteLine($"   Response detail: {responseContent}");
            Console.ResetColor();
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"❌ Unexpected exception during test creation: {ex.Message}");
        Console.ResetColor();
    }
}

Console.WriteLine("\n-------------------------------------------------");
Console.WriteLine("📊 Creating Service Level Objective (SLO)...");
Console.WriteLine("-------------------------------------------------");

if (createdMonitorIds.Count == 0)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("⚠️ No Synthetic tests were successfully created. Skipping SLO creation.");
    Console.ResetColor();
    return 0;
}

// 6. Read SLO Configuration
string sloName = config["Slo:Name"] ?? "Synthetic Tests SLO";
string sloDescription = config["Slo:Description"] ?? "Automatically created SLO";
var sloTags = config.GetSection("Slo:Tags").Get<string[]>() ?? new[] { "managed-by:automation" };
var thresholds = config.GetSection("Slo:Thresholds").Get<List<SloThreshold>>()
    ?? new List<SloThreshold> { new SloThreshold("30d", 99.9, 99.95) };

// Build SLO Json Payload
var sloPayload = new JsonObject
{
    ["name"] = sloName,
    ["description"] = sloDescription,
    ["type"] = "monitor",
    ["monitor_ids"] = new JsonArray(createdMonitorIds.Select(id => (JsonNode)id).ToArray()),
    ["tags"] = new JsonArray(sloTags.Select(t => (JsonNode)t).ToArray())
};

var thresholdsArray = new JsonArray();
foreach (var th in thresholds)
{
    var thObject = new JsonObject
    {
        ["timeframe"] = th.Timeframe,
        ["target"] = th.Target
    };
    if (th.Warning.HasValue)
    {
        thObject["warning"] = th.Warning.Value;
    }
    thresholdsArray.Add(thObject);
}
sloPayload["thresholds"] = thresholdsArray;

try
{
    var sloJson = sloPayload.ToJsonString();
    var sloContent = new StringContent(sloJson, Encoding.UTF8, "application/json");

    var response = await httpClient.PostAsync("/api/v1/slo", sloContent);
    var responseContent = await response.Content.ReadAsStringAsync();

    if (response.IsSuccessStatusCode)
    {
        var resNode = JsonNode.Parse(responseContent)?.AsObject();
        // The SLO creation API returns a 'data' array containing the created SLO metadata
        var dataArray = resNode?["data"]?.AsArray();
        string? sloId = string.Empty;
        if (dataArray != null && dataArray.Count > 0)
        {
            sloId = dataArray[0]?["id"]?.ToString();
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("🎉 Service Level Objective (SLO) created successfully!");
        if (!string.IsNullOrEmpty(sloId))
        {
            Console.WriteLine($"   SLO ID:   {sloId}");
            Console.WriteLine($"   SLO Link: https://app.{site}/slo?id={sloId}");
        }
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"❌ Failed to create SLO. Status: {response.StatusCode}");
        Console.WriteLine($"   Response detail: {responseContent}");
        Console.ResetColor();
    }
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"❌ Unexpected exception during SLO creation: {ex.Message}");
    Console.ResetColor();
}

Console.WriteLine("\n=================================================");
Console.WriteLine("🏁 Automation Run Complete");
Console.WriteLine("=================================================");

return 0;

public record SloThreshold(string Timeframe, double Target, double? Warning);
