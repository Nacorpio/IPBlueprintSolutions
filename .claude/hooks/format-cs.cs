// Claude Code PostToolUse hook: after Claude edits a C# file, fix its whitespace with `dotnet format`
// so the file matches .editorconfig. Never blocks: a formatting problem is reported, not fatal.
// Adapted from the C# hooks in Everything Claude Code (MIT, see .claude/LICENSE-everything-claude-code.txt).
using System.Diagnostics;
using System.Text.Json;

using var input = JsonDocument.Parse(Console.In.ReadToEnd());
if (!input.RootElement.TryGetProperty("tool_input", out var toolInput)
    || !toolInput.TryGetProperty("file_path", out var pathElement)
    || pathElement.GetString() is not { Length: > 0 } filePath)
{
    return 0;
}

var root = input.RootElement.TryGetProperty("cwd", out var cwd) ? cwd.GetString()! : Environment.CurrentDirectory;
root = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") is { Length: > 0 } projectDir ? projectDir : root;

var fullPath = Path.GetFullPath(filePath, root);
var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
if (!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
    || relative.StartsWith("..", StringComparison.Ordinal)
    || relative.Contains("/Migrations/", StringComparison.Ordinal)
    || relative.Contains("/obj/", StringComparison.Ordinal)
    || !File.Exists(fullPath))
{
    return 0;
}

var format = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var arg in new[] { "format", "whitespace", "--folder", "--include", relative })
{
    format.ArgumentList.Add(arg);
}

using var process = Process.Start(format)!;
var output = process.StandardOutput.ReadToEndAsync();
var errors = process.StandardError.ReadToEndAsync();
process.WaitForExit();

if (process.ExitCode != 0)
{
    Console.Error.WriteLine($"dotnet format could not format {relative}:\n{await output}{await errors}");
}

return 0;
