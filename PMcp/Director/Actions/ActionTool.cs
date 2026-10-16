using System;
using System.Collections.Generic;
using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed class PreparedAction
{
    public string Summary { get; set; } = "";
    public Action Apply { get; set; } = () => { };
    public JsonObject? Extra { get; set; }
}

public abstract class ActionTool : ITool
{
    protected readonly DirectorContext Ctx;

    protected ActionTool(DirectorContext ctx)
    {
        Ctx = ctx;
    }

    public abstract string Name { get; }
    public abstract string Title { get; }
    public abstract string Description { get; }
    public abstract JsonObject InputSchema { get; }

    public abstract PreparedAction Prepare(Dictionary<string, object?> args);

    public object Execute(Dictionary<string, object?> args)
    {
        if (!Ctx.IsAllowed(Name))
            throw new ArgumentException($"Action {Name} is disabled (director.allowed_actions)");

        PreparedAction action = Prepare(args);
        var result = new JsonObject { ["action"] = Name, ["summary"] = action.Summary };
        if (action.Extra != null)
        {
            foreach (KeyValuePair<string, object?> pair in action.Extra)
                result[pair.Key] = pair.Value;
        }

        if (Ctx.DryRun)
        {
            Ctx.Log($"[dry] {Name}: {action.Summary}");
            Ctx.RecordAction(Name, action.Summary, "dry");
            result["executed"] = false;
            result["dry_run"] = true;
            return result;
        }

        try
        {
            action.Apply();
        }
        catch (ArgumentException e)
        {
            Ctx.RecordAction(Name, action.Summary + " | " + e.Message, "failed");
            throw;
        }
        Ctx.Log($"{Name}: {action.Summary}");
        Ctx.RecordAction(Name, action.Summary, "done");
        result["executed"] = true;
        return result;
    }

    internal const DoorLockReason DirectorLock = DoorLockReason.Isolation;

    internal static readonly string[] ZoneNames = { "all", "LightContainment", "HeavyContainment", "Entrance", "Surface" };

    internal static FacilityZone? ParseZone(string? value)
    {
        switch ((value ?? "all").Trim().ToLowerInvariant())
        {
            case "all":
            case "":
                return null;
            case "lightcontainment":
            case "lcz":
            case "лзс":
                return FacilityZone.LightContainment;
            case "heavycontainment":
            case "hcz":
            case "тзс":
                return FacilityZone.HeavyContainment;
            case "entrance":
            case "ez":
            case "офисная":
                return FacilityZone.Entrance;
            case "surface":
            case "поверхность":
                return FacilityZone.Surface;
            default:
                throw new ArgumentException($"Unknown zone '{value}'. Allowed: {string.Join(", ", ZoneNames)}");
        }
    }

    protected static string ZoneText(FacilityZone? zone) => zone?.ToString() ?? "whole facility";

    protected static string Required(Dictionary<string, object?> args, string key)
    {
        string? value = ToolArgs.String(args, key)?.Trim();
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException($"Argument '{key}' is required");
        return value!;
    }

    protected static string OneOf(Dictionary<string, object?> args, string key, string[] values, string? fallback = null)
    {
        string? value = ToolArgs.String(args, key)?.Trim().ToLowerInvariant() ?? fallback;
        if (value == null || Array.IndexOf(values, value) < 0)
            throw new ArgumentException($"'{key}' must be one of: {string.Join(", ", values)}");
        return value;
    }
}
