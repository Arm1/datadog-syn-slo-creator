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

public class DatadogService : IDisposable
{
    private readonly IConfiguration _config;
    private readonly HttpClient _httpClient;
    private readonly string _site;
    private readonly string _apiKey;
    private readonly string _appKey;

    public DatadogService(IConfiguration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _site = _config["Datadog:Site"] ?? "datadoghq.com";
        _apiKey = _config["Datadog:ApiKey"] ?? string.Empty;
        _appKey = _config["Datadog:AppKey"] ?? string.Empty;

        _httpClient = new HttpClient();
        _httpClient.BaseAddress = new Uri($"https://api.{_site}");
        _httpClient.DefaultRequestHeaders.Add("DD-API-KEY", _apiKey);
        _httpClient.DefaultRequestHeaders.Add("DD-APPLICATION-KEY", _appKey);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public bool ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "YOUR_DATADOG_API_KEY" ||
            string.IsNullOrWhiteSpace(_appKey) || _appKey == "YOUR_DATADOG_APPLICATION_KEY")
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("⚠️ WARNING: Datadog API/Application keys are not configured.");
            Console.WriteLine("Please edit 'appsettings.json' and replace the placeholder values with your real keys.");
            Console.WriteLine("\nRequired keys in appsettings.json:");
            Console.WriteLine("  - Datadog:ApiKey");
            Console.WriteLine("  - Datadog:AppKey");
            Console.ResetColor();
            return false;
        }
        return true;
    }

    public async Task<int> RunCreateTestsAsync()
    {
        const string templatePath = "template.json";
        const string urlsPath = "urls.json";
        const string cachePath = "created_tests.json";

        string? sourceTestId = _config["Datadog:SourceTestId"];
        JsonNode? templateNode = null;

        if (!string.IsNullOrWhiteSpace(sourceTestId))
        {
            var fetchedNode = await GetSyntheticTestAsync(sourceTestId);
            if (fetchedNode == null)
            {
                WriteError($"Aborting. Could not retrieve source test config for ID '{sourceTestId}'.");
                return 1;
            }
            templateNode = fetchedNode;
        }
        else
        {
            Console.WriteLine($"ℹ️ No 'Datadog:SourceTestId' found in configuration. Falling back to local template '{templatePath}'.");
            templateNode = LoadJsonFile(templatePath);
            if (templateNode == null) return 1;
        }

        var urlsNode = LoadJsonFile(urlsPath);
        if (urlsNode == null) return 1;

        var urlsArray = urlsNode as JsonArray;
        if (urlsArray == null)
        {
            WriteError("urls.json must be a JSON array.");
            return 1;
        }

        Console.WriteLine("-------------------------------------------------");
        Console.WriteLine("🛠️ Starting Creation of Synthetic API Tests...");
        Console.WriteLine("-------------------------------------------------");
        Console.WriteLine($"Loaded {urlsArray.Count} target URL(s) to process.\n");

        var createdTests = new List<CreatedTestInfo>();

        foreach (var itemNode in urlsArray)
        {
            if (itemNode == null) continue;

            var item = itemNode.AsObject();
            string? url = item["url"]?.ToString();
            string? name = item["name"]?.ToString();

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(name))
            {
                WriteWarning($"Skipping invalid entry: Name='{name}', URL='{url}'");
                continue;
            }

            Console.WriteLine($"\n⚙️ Processing: '{name}' ({url})...");

            var testNode = templateNode.DeepClone()!.AsObject();
            PrepareTestPayload(testNode, name, url, item["tags"] as JsonArray);

            var result = await PostSyntheticTestAsync(testNode, name);
            if (result != null)
            {
                createdTests.Add(result);
            }
        }

        if (createdTests.Count > 0)
        {
            try
            {
                var cacheJson = JsonSerializer.Serialize(createdTests, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(cachePath, cacheJson);
                WriteSuccess($"\n💾 Saved metadata for {createdTests.Count} created test(s) to '{cachePath}'");
            }
            catch (Exception ex)
            {
                WriteWarning($"Could not write created tests cache file: {ex.Message}");
            }
        }
        else
        {
            WriteWarning("\nNo tests were successfully created.");
        }

        Console.WriteLine("\n=================================================");
        Console.WriteLine("🏁 Test Creation Run Complete");
        Console.WriteLine("=================================================");
        return 0;
    }

    public async Task<int> RunCreateSloAsync()
    {
        const string cachePath = "created_tests.json";

        if (!File.Exists(cachePath))
        {
            WriteError($"No created tests cache file found at '{cachePath}'. Please run 'create-tests' first.");
            return 1;
        }

        List<CreatedTestInfo>? createdTests;
        try
        {
            var cacheContent = await File.ReadAllTextAsync(cachePath);
            createdTests = JsonSerializer.Deserialize<List<CreatedTestInfo>>(cacheContent);
        }
        catch (Exception ex)
        {
            WriteError($"Failed to load or parse cache file '{cachePath}': {ex.Message}");
            return 1;
        }

        if (createdTests == null || createdTests.Count == 0)
        {
            WriteWarning("No created tests found in cache file. Skipping SLO creation.");
            return 0;
        }

        var monitorIds = createdTests.Select(t => t.MonitorId).ToList();

        Console.WriteLine("-------------------------------------------------");
        Console.WriteLine("📊 Creating Service Level Objective (SLO)...");
        Console.WriteLine("-------------------------------------------------");
        Console.WriteLine($"Referencing {monitorIds.Count} monitor ID(s) from cache.\n");

        string sloName = _config["Slo:Name"] ?? "Synthetic Tests SLO";
        string sloDescription = _config["Slo:Description"] ?? "Automatically created SLO";
        var sloTags = _config.GetSection("Slo:Tags").Get<string[]>() ?? new[] { "managed-by:automation" };
        var thresholds = _config.GetSection("Slo:Thresholds").Get<List<SloThreshold>>()
            ?? new List<SloThreshold> { new SloThreshold("30d", 99.9, 99.95) };

        var sloPayload = new JsonObject
        {
            ["name"] = sloName,
            ["description"] = sloDescription,
            ["type"] = "monitor",
            ["monitor_ids"] = new JsonArray(monitorIds.Select(id => (JsonNode)id).ToArray()),
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

            var response = await _httpClient.PostAsync("/api/v1/slo", sloContent);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var resNode = JsonNode.Parse(responseContent)?.AsObject();
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
                    Console.WriteLine($"   SLO Link: https://app.{_site}/slo?id={sloId}");
                }
                Console.ResetColor();
            }
            else
            {
                WriteError($"Failed to create SLO. Status: {response.StatusCode}");
                Console.WriteLine($"   Response detail: {responseContent}");
                return 1;
            }
        }
        catch (Exception ex)
        {
            WriteError($"Unexpected exception during SLO creation: {ex.Message}");
            return 1;
        }

        Console.WriteLine("\n=================================================");
        Console.WriteLine("🏁 SLO Creation Run Complete");
        Console.WriteLine("=================================================");
        return 0;
    }

    private JsonNode? LoadJsonFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                WriteError($"File not found: '{path}'");
                return null;
            }
            var content = File.ReadAllText(path);
            return JsonNode.Parse(content);
        }
        catch (Exception ex)
        {
            WriteError($"Error loading/parsing file '{path}': {ex.Message}");
            return null;
        }
    }

    private void PrepareTestPayload(JsonObject testNode, string name, string url, JsonArray? itemTags)
    {
        // Strip read-only properties
        string[] readOnlyFields = { "public_id", "monitor_id", "creator", "created_at", "modified_at" };
        foreach (var field in readOnlyFields)
        {
            testNode.Remove(field);
        }

        // Set name
        testNode["name"] = name;

        // Set URL
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
        if (itemTags != null)
        {
            foreach (var tag in itemTags)
            {
                if (tag != null) combinedTags.Add(tag.ToString());
            }
        }
        testNode["tags"] = new JsonArray(combinedTags.Select(t => (JsonNode)t).ToArray());
    }

    private async Task<CreatedTestInfo?> PostSyntheticTestAsync(JsonObject testPayload, string name)
    {
        try
        {
            var jsonPayload = testPayload.ToJsonString();
            var payloadContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/api/v1/synthetics/tests", payloadContent);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var resNode = JsonNode.Parse(responseContent)?.AsObject();
                string? publicId = resNode?["public_id"]?.ToString();
                long? monitorId = resNode?["monitor_id"]?.GetValue<long>();

                if (monitorId.HasValue && !string.IsNullOrEmpty(publicId))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("✅ Successfully created Synthetic Test!");
                    Console.WriteLine($"   Public ID:  {publicId}");
                    Console.WriteLine($"   Monitor ID: {monitorId.Value}");
                    Console.ResetColor();
                    return new CreatedTestInfo(name, publicId, monitorId.Value);
                }
                else
                {
                    WriteWarning("Test created, but API did not return a valid public_id or monitor_id.");
                    Console.WriteLine($"Response: {responseContent}");
                }
            }
            else
            {
                WriteError($"Failed to create Synthetic test. Status: {response.StatusCode}");
                Console.WriteLine($"   Response detail: {responseContent}");
            }
        }
        catch (Exception ex)
        {
            WriteError($"Unexpected exception during test creation: {ex.Message}");
        }
        return null;
    }

    private async Task<JsonObject?> GetSyntheticTestAsync(string publicId)
    {
        try
        {
            Console.WriteLine($"🔍 Fetching base Synthetic Test config for ID '{publicId}'...");
            var response = await _httpClient.GetAsync($"/api/v1/synthetics/tests/{publicId}");
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var testNode = JsonNode.Parse(responseContent)?.AsObject();
                return testNode;
            }
            else
            {
                WriteError($"Failed to fetch Synthetic test '{publicId}'. Status: {response.StatusCode}");
                Console.WriteLine($"   Response detail: {responseContent}");
            }
        }
        catch (Exception ex)
        {
            WriteError($"Unexpected exception during test retrieval: {ex.Message}");
        }
        return null;
    }

    private static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"❌ ERROR: {message}");
        Console.ResetColor();
    }

    private static void WriteWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"⚠️ {message}");
        Console.ResetColor();
    }

    private static void WriteSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

public record CreatedTestInfo(string Name, string PublicId, long MonitorId);
public record SloThreshold(string Timeframe, double Target, double? Warning);
