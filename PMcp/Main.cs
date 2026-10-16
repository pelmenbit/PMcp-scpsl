using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using LabApi.Features.Console;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;
using PMcp.Ai;
using PMcp.Director;
using PMcp.Events;
using PMcp.Protocol;
using PMcp.Threading;
using PMcp.Tools;
using PMcp.Transport;

namespace PMcp;

public class Main : Plugin
{
    public static Main? Instance { get; private set; }

    public override string Name => "PMcp";
    public override string Description => "MCP-сервер, ИИ-агент (OpenAI-совместимый API) и ИИ-дирижёр раунда";
    public override string Author => "pelmenbit";
    public override Version Version => new(1, 7, 0);
    public override Version RequiredApiVersion => LabApi.Features.LabApiProperties.CurrentVersion;

    public Config PluginConfig { get; private set; } = new();

    public AiAgent? Agent { get; private set; }
    public RoundDirector? Director { get; private set; }
    public McpActions? McpActions { get; private set; }

    private MainThreadQueue? _queue;
    private EventLog? _events;
    private HttpServer? _http;
    private ToolRunner? _runner;
    private ToolRunner? _mcpRunner;
    private DirectorContext? _ctx;
    private CassieLibrary? _library;
    private ActionTool[] _actions = Array.Empty<ActionTool>();

    public override void LoadConfigs()
    {
        if (this.TryLoadConfig("config", out Config cfg))
        {
            PluginConfig = cfg;
            if (MigrateTriggers(cfg.Director))
                Logger.Info($"director.reaction_triggers был списком по умолчанию из 1.5 - добавлены новые триггеры: {string.Join(", ", cfg.Director.ReactionTriggers.Except(TriggersBefore16))}");
            this.TrySaveConfig(cfg, "config");
        }
        else
            Logger.Error("Не удалось загрузить конфиг, используются значения по умолчанию");
    }

    private static readonly string[] TriggersBefore16 = { "scp_death", "escape", "wave", "team_wiped", "mass_casualty" };

    internal static bool MigrateTriggers(DirectorConfig director)
    {
        List<string> current = director.ReactionTriggers;
        if (current.Count != TriggersBefore16.Length || !TriggersBefore16.All(t => current.Contains(t)))
            return false;
        director.ReactionTriggers = new DirectorConfig().ReactionTriggers;
        return true;
    }

    public override void Enable()
    {
        Instance = this;
        Config cfg = PluginConfig;

        PlayerInfo.CountDummies = cfg.CountDummiesAsPlayers;
        if (cfg.CountDummiesAsPlayers)
            Logger.Warn("count_dummies_as_players: true - боты считаются игроками (режим для теста)");

        _events = new EventLog(cfg.EventBufferSize, cfg.ExposeUserIds);
        _events.Subscribe();

        _queue = new MainThreadQueue(cfg.MaxJobsPerFrame);
        _queue.Start();

        _ctx = new DirectorContext(cfg);
        _ctx.Start();
        _library = CassieLibrary.Load(this.GetConfigDirectory().FullName, message => Logger.Warn(message));
        DirectorContext ctx = _ctx;
        CassieLibrary library = _library;
        _actions = new ActionTool[]
        {
            new CassieActionTool(ctx, library),
            new BroadcastActionTool(ctx),
            new DoorsActionTool(ctx),
            new LightsActionTool(ctx),
            new SetRoleActionTool(ctx),
            new WarheadActionTool(ctx),
            new DecontaminationActionTool(ctx),
            new TeslasActionTool(ctx),
            new ElevatorsActionTool(ctx),
            new RespawnWaveActionTool(ctx),
        };

        var readTools = new ITool[]
        {
            new ServerStatusTool(),
            new ListPlayersTool(cfg),
            new GetPlayerTool(cfg),
            new RecentEventsTool(_events),
            new FacilityInfoTool(ctx),
        };
        _runner = new ToolRunner(cfg, _queue, readTools);
        if (cfg.McpWriteTools)
        {
            McpActions = new McpActions(cfg, ctx);
            _mcpRunner = new ToolRunner(cfg, _queue, readTools.Concat(McpActions.Wrap(_actions)));
        }
        else
            _mcpRunner = _runner;

        StartMcp(cfg);
        StartAi(cfg);
        StartDirector(cfg);
        WarnAboutHints();
    }

    private static void WarnAboutHints()
    {
        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.GetName().Name.StartsWith("HintServiceMeow", StringComparison.OrdinalIgnoreCase))
                continue;
            Logger.Warn("PMcp: на сервере HintServiceMeow - подсказки style=hint от дирижёра может перекрыть его HUD. Проверь на сервере или оставь style=broadcast.");
            return;
        }
    }

    private void StartDirector(Config cfg)
    {
        if (Agent == null)
        {
            if (cfg.Director.Enabled)
                Logger.Error("Дирижёру нужен ИИ: включи ai.enabled и задай ai.base_url / ai.model. Дирижёр не запущен.");
            return;
        }

        DirectorContext ctx = _ctx!;
        CassieLibrary library = _library!;
        ActionTool[] actions = _actions;
        var shared = new List<ITool>
        {
            new ServerStatusTool(),
            new ListPlayersTool(cfg),
            new GetPlayerTool(cfg),
            new RecentEventsTool(_events!),
            new FacilityInfoTool(ctx),
            new CassieExamplesTool(library, ctx),
        };

        var actor = new ToolRunner(cfg, _queue!, shared.Concat(actions));
        var planTool = new PlanRoundTool(ctx, actions);
        var planner = new ToolRunner(cfg, _queue!, shared.Concat(new ITool[] { planTool }));
        var tail = new ToolRunner(cfg, _queue!, shared.Concat(new ITool[] { new ReplanTailTool(ctx, actions) }));

        Director = new RoundDirector(cfg, Agent, planner, actor, tail, _events!, ctx, library, this.GetConfigDirectory().FullName);
        Director.Start();

        DirectorConfig d = cfg.Director;
        Logger.Info(
            $"Дирижёр: {(d.Enabled ? "включён" : "выключен")}, {(d.DryRun ? "DRY RUN (только консоль)" : "ИСПОЛНЯЕТ")}, профиль {d.Profile}, "
                + $"действий {d.AllowedActions.Count}, образцов CASSIE {library.Entries.Count} (шаблонов {library.Entries.Count(e => e.Vars.Count > 0)}, режим {d.CassieMode}), "
                + $"модели: план {(string.IsNullOrWhiteSpace(d.PlannerModel) ? cfg.Ai.Model : d.PlannerModel)}, реакции {(string.IsNullOrWhiteSpace(d.ReactorModel) ? cfg.Ai.Model : d.ReactorModel)}, "
                + $"хвост плана {(d.TailReplanAtSeconds > 0 ? $"на {d.TailReplanAtSeconds} с" : "выкл")}, итог раунда {(d.RoundEndSummary ? "вкл" : "выкл")}, "
                + $"стартовые роли: SCP + {string.Join(", ", PlanRoundTool.StartHumanRoles(d))}, одобрение планов {(d.ApprovePlans ? "нужно" : "не нужно")}, "
                + $"лимит токенов в сутки {d.DailyTokenBudget}"
        );
    }

    private void StartMcp(Config cfg)
    {
        if (!PrepareToken(cfg))
            return;

        if (!cfg.RequireToken && !IsLoopback(cfg.BindAddress))
        {
            Logger.Error($"RequireToken: false допустим только на 127.0.0.1, а BindAddress = {cfg.BindAddress}. MCP не запущен.");
            return;
        }

        var dispatcher = new McpDispatcher(_mcpRunner!, Version.ToString(3), cfg.McpToolPrefix);
        MainThreadQueue queue = _queue!;
        _http = new HttpServer(cfg, dispatcher, message => queue.Post(() => Logger.Warn(message)));
        try
        {
            _http.Start();
        }
        catch (Exception e)
        {
            Logger.Error($"Не удалось занять {_http.Prefix}: {e.GetType().Name}: {e.Message}. MCP не запущен.");
            _http = null;
            return;
        }

        Logger.Info(
            $"MCP слушает {_http.Prefix}mcp - инструментов {dispatcher.ToolCount}, "
                + $"токен {(cfg.RequireToken ? "обязателен" : "ВЫКЛЮЧЕН")}, "
                + $"действия {(!cfg.McpWriteTools ? "выкл (только чтение)" : cfg.McpConfirm ? "с подтверждением pmcp confirm" : "БЕЗ ПОДТВЕРЖДЕНИЯ")}, "
                + $"user_id {(cfg.ExposeUserIds ? "отдаются" : "скрыты")}, буфер событий {cfg.EventBufferSize}, "
                + $"Origin: {(cfg.AllowedOrigins.Count == 0 ? "только без Origin" : string.Join(", ", cfg.AllowedOrigins))}"
        );
    }

    private void StartAi(Config cfg)
    {
        AiConfig ai = cfg.Ai;
        if (!ai.Enabled)
        {
            Logger.Info("ИИ выключен (ai.enabled: false)");
            return;
        }
        if (string.IsNullOrWhiteSpace(ai.BaseUrl) || string.IsNullOrWhiteSpace(ai.Model))
        {
            Logger.Error("ИИ: не заданы ai.base_url или ai.model - ИИ не запущен.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ai.ApiKey) && Uri.TryCreate(ai.BaseUrl, UriKind.Absolute, out Uri uri) && !uri.IsLoopback)
            Logger.Warn($"ИИ: ai.api_key пустой, а {uri.Host} - внешний провайдер: на каждый запрос будет 401. Впиши ключ в config.yml.");

        Agent = new AiAgent(ai, _runner!, _queue!, new TokenLedger(cfg, this.GetConfigDirectory().FullName));
        Logger.Info(
            $"ИИ: {Agent.Endpoint}, модель {ai.Model}, ключ {(string.IsNullOrEmpty(ai.ApiKey) ? "не задан" : "задан")}, "
                + $"до {ai.MaxToolRounds} раундов инструментов, до {ai.MaxConcurrentRuns} прогонов одновременно, "
                + $"токенов сегодня {Agent.Ledger.Today}/{(Agent.Ledger.Budget > 0 ? Agent.Ledger.Budget.ToString() : "без лимита")}"
        );
    }

    public string StatusText()
    {
        Config cfg = PluginConfig;
        string mcp = _http != null ? $"{_http.Prefix}mcp" : "не запущен";
        string ai = Agent == null
            ? "выключен"
            : $"{Agent.Endpoint}, модель {cfg.Ai.Model}, ключ {(string.IsNullOrEmpty(cfg.Ai.ApiKey) ? "не задан" : "задан")}, идёт прогонов: {Agent.Running}, токенов сегодня {Agent.Ledger.Today}";
        return $"PMcp {Version.ToString(3)}\n  MCP: {mcp}, инструментов {_mcpRunner?.Tools.Count ?? 0}{(McpActions != null ? ", с действиями" : "")}\n  ИИ: {ai}\n  "
            + (Director?.Status() ?? "Дирижёр: не запущен (нужен ai.enabled)");
    }

    public override void Disable()
    {
        Director?.Stop();
        Director = null;

        Agent?.Stop();
        Agent = null;

        _http?.Stop();
        _http = null;

        _queue?.Stop();
        _queue = null;

        _events?.Unsubscribe();
        _events = null;

        _ctx?.Stop();
        _ctx = null;
        _library = null;
        _actions = Array.Empty<ActionTool>();
        McpActions = null;
        _runner = null;
        _mcpRunner = null;
        Instance = null;
    }

    private bool PrepareToken(Config cfg)
    {
        if (!cfg.RequireToken || !string.IsNullOrWhiteSpace(cfg.Token))
            return true;

        var bytes = new byte[24];
        using (var rng = new RNGCryptoServiceProvider())
            rng.GetBytes(bytes);
        cfg.Token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        if (this.TrySaveConfig(cfg, "config"))
        {
            Logger.Info($"Сгенерирован токен доступа, он в {this.GetConfigPath("config")} (поле token).");
            return true;
        }

        Logger.Error("Не удалось сохранить сгенерированный токен в config.yml - задай token руками. MCP не запущен.");
        return false;
    }

    private static bool IsLoopback(string address) =>
        string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(address, out IPAddress ip) && IPAddress.IsLoopback(ip));
}
