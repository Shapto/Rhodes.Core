using System.Text.Json;

namespace Rhodes.Core.Tools
{
    /// <summary>
    /// Lenient readers for tool arguments.
    /// </summary>
    public static class ToolArguments
    {
        public static string GetRequiredString(JsonElement arguments, string name)
        {
            var value = GetOptionalString(arguments, name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ToolArgumentException($"Missing \"{name}\". Call again with {name} set.");
            }

            return value;
        }

        public static string? GetOptionalString(JsonElement arguments, string name)
        {
            if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var element))
            {
                return null;
            }

            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
                _ => null,
            };
        }

        public static int GetOptionalInteger(JsonElement arguments, string name, int defaultValue, int minimum, int maximum)
        {
            var text = GetOptionalString(arguments, name);
            if (text is null)
            {
                return defaultValue;
            }

            if (!int.TryParse(text, out var value))
            {
                throw new ToolArgumentException($"\"{name}\" must be a whole number from {minimum} to {maximum}.");
            }

            return Math.Clamp(value, minimum, maximum);
        }

        public static IReadOnlyList<string> GetOptionalStringList(JsonElement arguments, string name)
        {
            if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var element))
            {
                return [];
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                return element.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .ToList();
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                return element.GetString()!
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }

            return [];
        }
    }
}
