# Datadog Synthetic Tests and SLO Creator

This is a utility program written in **.NET 10** to automatically create multiple Datadog Synthetic API (HTTP) tests and link them to a newly created Service Level Objective (SLO).

## Features
- **Template Merging**: Loads standard Synthetic test attributes (like headers, location, assertion list, monitoring options) from [template.json](file:///Users/armandpaghent/.gemini/antigravity-ide/scratch/datadog-syn-slo-creator/template.json).
- **Target URL Lists**: Reads target names, URLs, and optional custom tags from [urls.json](file:///Users/armandpaghent/.gemini/antigravity-ide/scratch/datadog-syn-slo-creator/urls.json).
- **Automatic monitor_id Capture**: Extracts `monitor_id` from each successfully created test response.
- **SLO Creation**: Groups the created monitors into a Service Level Objective (SLO) with warning and target thresholds.
- **Configurable Sites**: Supports US1 (`datadoghq.com`), EU1 (`datadoghq.eu`), US3, US5, and AP1 Datadog region endpoints.

---

## Configuration

Before running the project, you must set your Datadog API keys and Datadog Site details in **[appsettings.json](file:///Users/armandpaghent/.gemini/antigravity-ide/scratch/datadog-syn-slo-creator/appsettings.json)**:

```json
{
  "Datadog": {
    "ApiKey": "YOUR_DATADOG_API_KEY",
    "AppKey": "YOUR_DATADOG_APPLICATION_KEY",
    "Site": "datadoghq.com" // Use datadoghq.eu for EU region, etc.
  },
  "Slo": {
    "Name": "Auto-Created Synthetic Tests SLO",
    "Description": "This SLO automatically tracks all Synthetic HTTP tests created by the .NET automation script.",
    "Tags": [
      "env:production",
      "managed-by:datadog-syn-slo-creator"
    ],
    "Thresholds": [
      {
        "Timeframe": "30d",
        "Target": 99.9,
        "Warning": 99.95
      }
    ]
  }
}
```

---

## How to Run

1. Open your terminal in the project directory `/Users/armandpaghent/.gemini/antigravity-ide/scratch/datadog-syn-slo-creator`.
2. Clean and build the application:
   ```bash
   dotnet build
   ```
3. Run the executable:
   ```bash
   dotnet run
   ```

If your Datadog API/App keys are not set, the application will exit gracefully with a warning. Once real keys are configured, it will output the created Synthetic IDs and a clickable URL for the generated SLO.
