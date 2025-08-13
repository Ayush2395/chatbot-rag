// Program.cs

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using OllamaSharp;

class Program
{
    // ==== Ollama settings ====
    const string ChatModel = "llama3:8b"; // or "llama3.1:8b-instruct", "qwen2.5:14b-instruct", "mistral:7b-instruct"
    const string OllamaUrl = "http://localhost:11434";

    // ==== Fixed payload fields ====
    const string FixedDatabase = "SQLGiG";
    const string FixedInstance = "CTS03";

    // Allowed querycodes
    static readonly HashSet<string> AllowedQuerycodes = new(StringComparer.Ordinal)
        { "Fetch_Jobs", "Fetch_History", "Fetch_Steps", "Fetch_SQLVersion", "Fetch_Database", "Fetch_Configuration" };

    // SYSTEM prompt: model must return ONLY two keys in this order
    static readonly string TwoKeySystemPrompt = @"
You are a silent JSON field generator for SQLGIG.

Output ONLY this JSON object with exactly two keys in this order and nothing else:
{
  ""WinServer"": """",
  ""querycode"": """"
}

Rules:
- WinServer: exact SQL instance token from user's text (e.g., ""CTS02"", ""CTS03""); preserve case; if missing, use "".
- querycode: one of ""Fetch_Jobs"", ""Fetch_History"", ""Fetch_Steps"", ""Fetch_SQLVersion"" ""Fetch_Database"" ""Fetch_Configuration"" based on intent.
- No explanations, no markdown, no extra keys, no code fences.

Allowed querycodes (purpose & examples):
- Fetch_Jobs
  Purpose: Job list/status/schedule.
  Triggers: list/show jobs, running, failed, enabled/disabled, schedule, next run, owner/category/agent status, duration.
  Examples: ""failed jobs on CTS03"", ""next run times CTS02"", ""list jobs CTS04"".

- Fetch_History
  Purpose: Past job executions & outcomes.
  Triggers: history, last run(s), outcome/result, errors, recent activity, failures in last N hours/days, retries, run time, messages.
  Examples: ""last run history CTS02"", ""errors in job history CTS03"", ""failures in last 24 hours CTS04"".

- Fetch_Steps
  Purpose: Job step inventory/behavior.
  Triggers: steps, job steps, step details, on success/failure action, proxies, subsystem, step id/sequence.
  Examples: ""what steps does Backup job have on CTS34"", ""list job steps CTS03"".

- Fetch_SQLVersion
  Purpose: SQL Server version/build/edition.
  Triggers: version, edition, product level, build, @@version.
  Examples: ""sql version for CTS03"", ""edition/build CTS02"".

- Fetch_Database
  Purpose: Database catalogue & sizes.
  Triggers: databases list, db sizes, file sizes/paths, recovery model, compatibility level, collation, state, autogrowth, biggest databases.
  Examples: ""list databases on CTS03"", ""top 5 largest databases CTS02"", ""db file paths CTS04"".

- Fetch_Configuration
  Purpose: Server-level configuration (sp_configure).
  Triggers: maxdop, cost threshold for parallelism, max/min server memory, optimize for ad hoc, backup compression default, clr enabled, remote admin connections, xp_cmdshell, default trace, fill factor, configuration/settings.
  Examples: ""maxdop CTS03"", ""max memory and min memory CTS02"", ""configuration list CTS04"".
";


    static async Task Main()
    {
        IChatClient chatClient = new OllamaApiClient(new Uri(OllamaUrl), ChatModel);

        Console.WriteLine("==========Chatbot==========");
        while (true)
        {
            Console.WriteLine("Type 'exit' to close the application");
            Console.Write("> ");

            var userPrompt = Console.ReadLine();
            if (userPrompt == null) continue;
            if (userPrompt.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Exiting from the application");
                return;
            }

            var payload = await BuildPayloadAsync(chatClient, userPrompt);

            if (payload == null)
            {
                Console.WriteLine("SQL Instance name required.");
                continue;
            }

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
        }
    }

    // --- Core: build the final 4-field payload (querycode dynamic; no Query field) ---
    static async Task<OrderedPayload?> BuildPayloadAsync(IChatClient chatClient, string userText)
    {
        // 1) Pre-extract CTS## from user text (handles "cts98." etc.)
        var instanceFromText = ExtractInstance(userText); // e.g., "CTS98" or ""

        // 2) Ask model for ONLY { "WinServer": "", "querycode": "" }
        var twoKeyJsonRaw = await CallModelForTwoKeysAsync(chatClient, userText);
        twoKeyJsonRaw = StripCodeFences(twoKeyJsonRaw);

        string winServer = "";
        string querycode = "";

        if (!TryParseTwoKeys(twoKeyJsonRaw, out winServer, out querycode))
        {
            // If the model didn’t produce valid two-key JSON, fall back:
            winServer = instanceFromText;
            querycode = InferQuerycodeFromText(userText); // basic fallback routing
        }

        // 3) Normalize & enforce
        winServer = (winServer ?? "").Trim();
        querycode = (querycode ?? "").Trim();

        // If we detected a CTS## in user text, enforce it (avoid LLM drift)
        if (!string.IsNullOrEmpty(instanceFromText))
            winServer = instanceFromText;

        // Require instance
        if (string.IsNullOrEmpty(winServer))
            return null;

        // Enforce allowed querycodes; if unclear, default by intent
        if (!AllowedQuerycodes.Contains(querycode))
            querycode = InferQuerycodeFromText(userText);

        // Still unclear? Default to Jobs (safe)
        if (!AllowedQuerycodes.Contains(querycode))
            querycode = "Fetch_Jobs";

        // 4) Assemble FINAL 4-field payload (keys in exact order)
        return new OrderedPayload
        {
            querycode = querycode,
            CentralizedSQLDatabase = FixedDatabase,
            CentralizedSQLInstance = FixedInstance,
            WinServer = winServer
        };
    }

    // --- Model call: ONLY WinServer + querycode ---
    static async Task<string> CallModelForTwoKeysAsync(IChatClient chatClient, string userText)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, TwoKeySystemPrompt),
            new(ChatRole.User, $"User request:\n\"{userText}\"")
        };

        var sb = new StringBuilder();
        var stream = chatClient.GetStreamingResponseAsync(messages);
        await foreach (var chunk in stream)
            sb.Append(chunk.Text);

        return sb.ToString().Trim();
    }

    // --- Helpers ---

    // Extract first CTS## token, case-insensitive, return UPPER (CTS98)
    static string ExtractInstance(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var m = Regex.Match(text, @"\bCTS\d{2}\b", RegexOptions.IgnoreCase);
        return m.Success ? m.Value.ToUpperInvariant() : "";
    }

    static string StripCodeFences(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        if (s.StartsWith("```")) s = s.Trim('`').Trim();
        if (s.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(4).Trim();
        return s;
    }

    // Expect exactly two keys in order: WinServer, querycode
    static bool TryParseTwoKeys(string json, out string winServer, out string querycode)
    {
        winServer = "";
        querycode = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            var obj = doc.RootElement;
            int count = 0;
            string? first = null, second = null;
            foreach (var prop in obj.EnumerateObject())
            {
                count++;
                if (count == 1) first = prop.Name;
                if (count == 2) second = prop.Name;
            }

            if (count != 2) return false;
            if (!string.Equals(first, "WinServer", StringComparison.Ordinal)) return false;
            if (!string.Equals(second, "querycode", StringComparison.Ordinal)) return false;

            winServer = obj.GetProperty("WinServer").GetString() ?? "";
            querycode = obj.GetProperty("querycode").GetString() ?? "";
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Simple fallback routing if model classification is unclear
    static string InferQuerycodeFromText(string text)
    {
        var t = (text ?? "").ToLowerInvariant();

        // Version cues
        if (t.Contains("version") || t.Contains("edition") || t.Contains("product level") || t.Contains("build"))
            return "Fetch_SQLVersion";

        // Steps cues
        if (t.Contains("step ") || t.Contains("steps") || t.Contains("job steps"))
            return "Fetch_Steps";

        // History cues
        if (t.Contains("history") || t.Contains("last runs") || t.Contains("errors") || t.Contains("recent activity"))
            return "Fetch_History";

        // Default: jobs (covers failed/running/enabled/disabled/schedule/next run/list)
        return "Fetch_Jobs";
    }

    // Keep output key order stable
    public record OrderedPayload
    {
        public string querycode { get; init; } = "";
        public string CentralizedSQLDatabase { get; init; } = "";
        public string CentralizedSQLInstance { get; init; } = "";
        public string WinServer { get; init; } = "";
    }
}