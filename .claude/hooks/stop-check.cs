// Claude Code Stop hook: before Claude finishes, make sure what it changed still builds and that no secret
// was typed into appsettings*.json. Exit code 2 sends the problem back to Claude to fix.
// Adapted from the C# hooks in Everything Claude Code (MIT, see .claude/LICENSE-everything-claude-code.txt).
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using var input = JsonDocument.Parse(Console.In.ReadToEnd());

// Claude is already fixing a problem this hook reported: let it stop rather than loop.
if (input.RootElement.TryGetProperty("stop_hook_active", out var active) && active.GetBoolean())
{
    return 0;
}

var root = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") is { Length: > 0 } projectDir
    ? projectDir
    : input.RootElement.TryGetProperty("cwd", out var cwd) ? cwd.GetString()! : Environment.CurrentDirectory;

var (gitExit, status) = Run("git", root, "status", "--porcelain", "--untracked-files=all");
if (gitExit != 0)
{
    return 0; // Not a git checkout: nothing to compare against.
}

var changed = status.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    .Where(line => line.Length > 3 && line[0] != 'D' && line[1] != 'D')
    .Select(line => line[3..].Trim().Trim('"'))
    .Select(path => path.Contains(" -> ") ? path[(path.IndexOf(" -> ") + 4)..] : path)
    .ToList();

var problems = new List<string>();

// Secret keys that must stay empty in committed config (they come from user-secrets or environment variables).
string[] secretKeys = ["Password", "ApiKey", "MerchantCode"];
foreach (var settings in changed.Where(p => Path.GetFileName(p).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                                            && p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
{
    var path = Path.Combine(root, settings);
    if (!File.Exists(path))
    {
        continue;
    }

    JsonNode? json;
    try
    {
        json = JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    }
    catch (JsonException ex)
    {
        problems.Add($"{settings} is not valid JSON: {ex.Message}");
        continue;
    }

    foreach (var (key, value) in Walk(json, ""))
    {
        if (secretKeys.Any(s => key.EndsWith(":" + s, StringComparison.OrdinalIgnoreCase) || key.Equals(s, StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrEmpty(value))
        {
            problems.Add($"{settings} has a value for {key}. Secrets go in user-secrets locally and environment variables on the server; set it back to \"\".");
        }
    }
}

string[] buildInputs = [".cs", ".cshtml", ".csproj", ".resx", ".props", ".targets", ".slnx"];
if (changed.Any(p => buildInputs.Any(ext => p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) && !p.StartsWith(".claude/", StringComparison.Ordinal)))
{
    var (buildExit, buildOutput) = Run("dotnet", root, "build", "IPBlueprintSolutions.slnx", "-nologo", "-v", "q", "-clp:ErrorsOnly");
    if (buildExit != 0)
    {
        var errors = buildOutput.Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Distinct().Take(20);
        problems.Add("dotnet build fails:\n" + string.Join('\n', errors));
    }
}

if (problems.Count == 0)
{
    return 0;
}

Console.Error.WriteLine("Fix these before finishing:\n- " + string.Join("\n- ", problems));
return 2;

static (int ExitCode, string Output) Run(string file, string workingDirectory, params string[] args)
{
    var start = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in args)
    {
        start.ArgumentList.Add(arg);
    }

    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    return (process.ExitCode, output.Result + errors.Result);
}

static IEnumerable<(string Key, string? Value)> Walk(JsonNode? node, string prefix)
{
    switch (node)
    {
        case JsonObject obj:
            foreach (var (name, child) in obj)
            {
                foreach (var pair in Walk(child, prefix.Length == 0 ? name : $"{prefix}:{name}"))
                {
                    yield return pair;
                }
            }
            break;
        case JsonValue value:
            yield return (prefix, value.ToString());
            break;
    }
}
