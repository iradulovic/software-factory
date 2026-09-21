using System.Text.Json;
using System.Text.Json.Serialization;

namespace Factory.Core;

/// <summary>
/// Parses a ".factory/config.json" build/test command in one of three JSON shapes, always producing a literal
/// executable plus already-split arguments — never a runtime string split, and never an unrestricted shell unless
/// the shell shape is used explicitly:
/// <list type="bullet">
/// <item>An array of strings (preferred): <c>["dotnet", "test", "--filter", "My Test With Spaces"]</c>. Each
/// element is one argument verbatim, so a quoted or spaced argument needs no escaping.</item>
/// <item>A plain string (legacy migration path): <c>"dotnet build"</c>, split on whitespace exactly as the
/// orchestrator used to split it at run time. Kept only so existing configuration files keep working unchanged;
/// it cannot represent an argument containing a space.</item>
/// <item>An explicit shell opt-in: <c>{"shell": "dotnet build && dotnet test"}</c>, resolved here into a literal
/// <c>/bin/sh -c &lt;command&gt;</c> (or <c>cmd.exe /c</c> on Windows) <see cref="ValidationCommand"/>. Shell
/// operators are only ever available through this explicit shape.</item>
/// </list>
/// </summary>
public sealed class ValidationCommandJsonConverter : JsonConverter<ValidationCommand>
{
    public override ValidationCommand Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => ParseLegacyString(reader.GetString()),
            JsonTokenType.StartArray => ParseArray(ref reader),
            JsonTokenType.StartObject => ParseShellObject(ref reader),
            _ => throw new JsonException("A validation command must be a string, an array of strings, or a {\"shell\": \"...\"} object.")
        };

    public override void Write(Utf8JsonWriter writer, ValidationCommand value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());

    private static ValidationCommand ParseLegacyString(string? command)
    {
        var tokens = (command ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) throw new JsonException("A validation command string must not be empty.");
        return new ValidationCommand(tokens[0], tokens[1..]);
    }

    private static ValidationCommand ParseArray(ref Utf8JsonReader reader)
    {
        var tokens = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("A validation command array must contain only strings.");
            tokens.Add(reader.GetString()!);
        }
        if (tokens.Count == 0 || string.IsNullOrWhiteSpace(tokens[0]))
            throw new JsonException("A validation command array must not be empty and its first element must be a non-empty executable.");
        return new ValidationCommand(tokens[0], tokens[1..]);
    }

    private static ValidationCommand ParseShellObject(ref Utf8JsonReader reader)
    {
        string? shell = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Invalid validation command object.");
            var propertyName = reader.GetString();
            reader.Read();
            if (string.Equals(propertyName, "shell", StringComparison.OrdinalIgnoreCase)) shell = reader.GetString();
            else reader.Skip();
        }
        if (string.IsNullOrWhiteSpace(shell)) throw new JsonException("A validation command object must set a non-empty \"shell\" command.");
        return OperatingSystem.IsWindows() ? new ValidationCommand("cmd.exe", ["/c", shell]) : new ValidationCommand("/bin/sh", ["-c", shell]);
    }
}
