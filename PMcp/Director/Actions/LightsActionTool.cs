using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using MapGeneration;
using PMcp.Tools;
using PMcp.Util;
using UnityEngine;

namespace PMcp.Director;

public sealed class LightsActionTool : ActionTool
{
    private static readonly string[] Actions = { "off", "on", "color", "reset", "flicker" };

    public LightsActionTool(DirectorContext ctx)
        : base(ctx) { }

    public override string Name => "lights";
    public override string Title => "Lights";

    public override string Description =>
        "Lights of a zone or the whole facility. off = blackout for duration_sec (restores itself), on = turn on, "
        + "color = #RRGGBB tint (normal color returns after duration_sec if set), reset = normal color, flicker = flicker.";

    public override JsonObject InputSchema { get; } =
        Schema.Object(
            new JsonObject
            {
                ["action"] = Schema.Str("What to do", Actions),
                ["zone"] = Schema.Str("Zone", ZoneNames),
                ["duration_sec"] = Schema.Int("Duration, seconds", 1, 600),
                ["color"] = Schema.Str("Color #RRGGBB for action=color"),
            },
            "action"
        );

    public override PreparedAction Prepare(Dictionary<string, object?> args)
    {
        string action = OneOf(args, "action", Actions);
        FacilityZone? zone = ParseZone(ToolArgs.String(args, "zone"));
        int maxOff = Ctx.Config.MaxBlackoutSeconds;
        int duration = action switch
        {
            "off" => ToolArgs.Int(args, "duration_sec", 30, 1, maxOff),
            "flicker" => ToolArgs.Int(args, "duration_sec", 3, 1, 10),
            "color" => ToolArgs.Int(args, "duration_sec", 0, 0, 600),
            _ => 0,
        };

        Color color = default;
        if (action == "color")
        {
            string raw = Required(args, "color");
            if (!ColorUtility.TryParseHtmlString(raw.StartsWith("#") ? raw : "#" + raw, out color))
                throw new ArgumentException($"Cannot parse color '{raw}', expected #RRGGBB");
        }

        string where = ZoneText(zone);
        return new PreparedAction
        {
            Summary = duration > 0 ? $"{action} {where} for {duration} s" : $"{action} {where}",
            Apply = () =>
            {
                switch (action)
                {
                    case "off":
                        if (zone == null)
                            Map.TurnOffLights(duration);
                        else
                            Map.TurnOffLights(duration, zone.Value);
                        break;
                    case "on":
                        if (zone == null)
                            Map.TurnOnLights();
                        else
                            Map.TurnOnLights(zone.Value);
                        break;
                    case "color":
                        SetColor(zone, color);
                        if (duration > 0)
                            Ctx.Schedule(duration, $"сброс цвета {where}", () => ResetColor(zone));
                        break;
                    case "reset":
                        ResetColor(zone);
                        break;
                    case "flicker":
                        foreach (LightsController light in LightsController.List)
                        {
                            if (light != null && (zone == null || light.Room?.Zone == zone))
                                light.FlickerLights(duration);
                        }
                        break;
                }
            },
        };
    }

    private static void SetColor(FacilityZone? zone, Color color)
    {
        if (zone == null)
            Map.SetColorOfLights(color);
        else
            Map.SetColorOfLights(color, zone.Value);
    }

    private static void ResetColor(FacilityZone? zone)
    {
        if (zone == null)
            Map.ResetColorOfLights();
        else
            Map.ResetColorOfLights(zone.Value);
    }
}
