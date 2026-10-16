using System;
using System.Collections.Generic;
using System.Globalization;
using PMcp.Util;

namespace PMcp.Tools;

internal static class ToolArgs
{
    public static string? String(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out object? v) || v == null)
            return null;
        return v switch
        {
            string s => s,
            double d => d.ToString(CultureInfo.InvariantCulture),
            _ => throw new ArgumentException($"'{key}' must be a string"),
        };
    }

    public static int Int(Dictionary<string, object?> args, string key, int fallback, int min, int max)
    {
        if (!args.TryGetValue(key, out object? v) || v == null)
            return fallback;

        double d = v switch
        {
            double x => x,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) => x,
            _ => throw new ArgumentException($"'{key}' must be an integer"),
        };
        if (d != Math.Floor(d))
            throw new ArgumentException($"'{key}' must be an integer");
        if (d < min || d > max)
            throw new ArgumentException($"'{key}' must be in range {min}..{max}");
        return (int)d;
    }

    public static bool Bool(Dictionary<string, object?> args, string key, bool fallback)
    {
        if (!args.TryGetValue(key, out object? v) || v == null)
            return fallback;
        return v switch
        {
            bool b => b,
            string s when bool.TryParse(s, out bool b) => b,
            _ => throw new ArgumentException($"'{key}' must be true or false"),
        };
    }

    public static List<string>? StringList(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out object? v) || v == null)
            return null;
        if (v is string single)
            return new List<string> { single };
        if (v is not List<object?> list)
            throw new ArgumentException($"'{key}' must be an array of strings");

        var result = new List<string>(list.Count);
        foreach (object? item in list)
        {
            if (item is not string s)
                throw new ArgumentException($"'{key}' must be an array of strings");
            result.Add(s);
        }
        return result;
    }
}

internal static class Schema
{
    public static JsonObject Object(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Length > 0)
            schema["required"] = required;
        return schema;
    }

    public static JsonObject Str(string description, string[]? values = null)
    {
        var s = new JsonObject { ["type"] = "string", ["description"] = description };
        if (values != null)
            s["enum"] = values;
        return s;
    }

    public static JsonObject Int(string description, int min, int max, int? fallback = null)
    {
        var s = new JsonObject
        {
            ["type"] = "integer",
            ["description"] = description,
            ["minimum"] = min,
            ["maximum"] = max,
        };
        if (fallback.HasValue)
            s["default"] = fallback.Value;
        return s;
    }

    public static JsonObject Bool(string description, bool fallback) =>
        new()
        {
            ["type"] = "boolean",
            ["description"] = description,
            ["default"] = fallback,
        };

    public static JsonObject StrArray(string description, string[] values) =>
        new()
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new JsonObject { ["type"] = "string", ["enum"] = values },
        };
}
