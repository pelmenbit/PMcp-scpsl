using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LabApi.Features.Wrappers;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed partial class RoundDirector
{
    private static readonly Dictionary<string, string> TestTriggers =
        new()
        {
            ["scp_death"] = "Scp173 died, killed by NtfSergeant: shot",
            ["escape"] = "Escape: ClassD -> ChaosConscript",
            ["wave"] = "Wave arrived: NineTailedFox, 4 players",
            ["team_wiped"] = "Team Scientists wiped out (was alive: 3)",
            ["mass_casualty"] = "Mass casualties: 6 in 60 s (ClassD 6; acid 6)",
            ["generators"] = "All 3 generators engaged - SCP-079 recontainment begins",
            ["warhead"] = "Warhead started by a player",
            ["decontamination"] = "LCZ decontamination started",
            ["scp079_recontained"] = "SCP-079 recontained",
            ["cuffed"] = "Cuffed: Scientist by ClassD 2",
        };

    public string Status()
    {
        string plan = _plan == null
            ? (_job?.Kind == PlanKind.Lobby ? "пишется" : "нет")
            : $"«{_plan.Theme}», ролей {_plan.Roles.Count}, событий {_plan.Timeline.Count}";
        string next = _nextPlan != null ? $"«{_nextPlan.Theme}», ролей {_nextPlan.Roles.Count}" : _job?.Kind == PlanKind.Ahead ? "пишется" : "нет";
        string pending = _pending == null ? "" : $"\n  ЖДЁТ ОДОБРЕНИЯ: «{_pending.Plan.Theme}»{(_pending.ForNextRound ? " (на следующий раунд)" : "")} - approve / reject";
        int budgetLimit = _agent.Ledger.Budget;
        string budget = budgetLimit > 0 ? $"{_agent.Ledger.Today}/{budgetLimit}" : $"{_agent.Ledger.Today} (без лимита)";
        string tail = D.TailReplanAtSeconds <= 0 ? "выкл" : _tailRun != null ? "пишется" : _tailDone ? "был" : $"на {D.TailReplanAtSeconds} с";
        string prepare = D.AutoPrepareAtSeconds > 0 ? $"на {D.AutoPrepareAtSeconds} с" : D.AutoPrepareOnRoundEnd ? "в конце раунда" : "выкл";
        int players = PlayerInfo.RealPlayers().Count;
        return $"Дирижёр: {(D.Enabled ? "включён" : "выключен")}, {(D.DryRun ? "DRY RUN" : "исполняет")}{(_paused ? ", НА ПАУЗЕ" : "")}, профиль {D.Profile}, CASSIE {D.CassieMode}\n"
            + $"  модели: план {PlannerModel}, реакции {ReactorModel}, идёт прогонов {_agent.Running}\n"
            + $"  план: {plan}, автоплан {(D.AutoPlan ? $"вкл (от {D.PlanMinPlayers} игроков)" : "выкл")}, одобрение {(D.ApprovePlans ? "нужно" : "не нужно")}{pending}\n"
            + $"  следующий раунд: {next}, автоподготовка: {prepare}, переписать хвост: {tail}\n"
            + $"  SCP при {players} игроках: до {_ctx.ScpLimit(players)}, перерыв для бывших SCP: {D.ScpCooldownRounds} р.\n"
            + $"  реакций за раунд: {_reactions}/{D.MaxReactionsPerRound}{(_reactRun != null ? " (идёт)" : "")}\n"
            + $"  токены: раунд {_roundTokens} ({_roundRuns} вызовов), сегодня {budget}";
    }

    public string PlanText()
    {
        var sb = new StringBuilder();
        sb.Append(
            _plan != null ? _plan.Describe()
            : _job?.Kind == PlanKind.Lobby ? "План пишется - обычно 20-90 с, итог придёт сюда же (если вызывал replan) и в консоль сервера"
            : "Плана нет"
        );
        if (_pending != null)
            sb.Append($"\n\nЖдёт одобрения{(_pending.ForNextRound ? " (на следующий раунд)" : "")}:\n").Append(_pending.Plan.Describe());
        if (_nextPlan != null)
            sb.Append("\n\nСледующий раунд:\n").Append(_nextPlan.Describe());
        else if (_job?.Kind == PlanKind.Ahead)
            sb.Append("\n\nСледующий раунд: план пишется");
        return sb.ToString();
    }

    public string LogText()
    {
        var sb = new StringBuilder();
        sb.Append($"Раунд: {_ctx.RoundSec} с, вызовов модели {_roundRuns}, токены за раунд {_roundTokens}, сегодня {_agent.Ledger.Today}\n");
        sb.Append(_runLog.Count == 0 ? "Вызовов модели в этом раунде не было\n" : "Вызовы модели:\n  " + string.Join("\n  ", _runLog) + "\n");
        IReadOnlyList<DirectorAction> actions = _ctx.RecentActions;
        if (actions.Count == 0)
            sb.Append("Действий дирижёра в этом раунде не было");
        else
        {
            sb.Append($"Действия дирижёра (последние {actions.Count}):");
            foreach (DirectorAction a in actions)
                sb.Append($"\n  {a.RoundSec,5} с [{a.Source}] {a.Action}: {TextSanitizer.StripTags(a.Summary)} ({a.Outcome})");
        }
        return sb.ToString();
    }

    public bool Replan(Action<string, bool>? notify, out string message)
    {
        if (_job != null)
        {
            message = _job.Kind == PlanKind.Ahead ? "Сейчас пишется план на следующий раунд, дождись или pmcp director cancel" : "План уже пишется";
            return false;
        }
        _plan = null;
        _pending = null;
        _autoPlanBlocked = false;
        return StartPlanning(notify, PlanKind.Lobby, 0, out message);
    }

    public bool Prepare(Action<string, bool>? notify, out string message)
    {
        if (!Round.IsRoundStarted)
        {
            message = "Раунд не идёт - в лобби используй pmcp director replan";
            return false;
        }
        return StartPlanning(notify, PlanKind.Ahead, 0, out message);
    }

    public bool Bench(int players, Action<string, bool>? notify, out string message)
    {
        if (players < 1 || players > 100)
        {
            message = "pmcp director bench [игроков 1-100]";
            return false;
        }
        return StartPlanning(notify, PlanKind.Bench, players, out message);
    }

    public bool Cancel(out string message)
    {
        var cancelled = new List<string>();
        if (_job != null)
        {
            _job.Run.Cancel();
            cancelled.Add(_job.Kind switch { PlanKind.Ahead => "план на следующий раунд", PlanKind.Bench => "проверка модели", _ => "план" });
        }
        if (_tailRun != null)
        {
            _tailRun.Cancel();
            cancelled.Add("хвост плана");
        }
        if (_reactRun != null)
        {
            _reactRun.Cancel();
            cancelled.Add("реакция");
        }
        message = cancelled.Count == 0 ? "Дирижёр сейчас ничего не ждёт от модели" : "Отменяю: " + string.Join(", ", cancelled);
        _ctx.Log(message);
        return cancelled.Count > 0;
    }

    public string StatsText()
    {
        if (!File.Exists(_metricsPath))
            return "Статистики планов пока нет (director_plans.jsonl появится после первого плана)";

        var rows = new List<Dictionary<string, object?>>();
        int broken = 0;
        foreach (string line in File.ReadAllLines(_metricsPath, Encoding.UTF8))
        {
            try
            {
                if (Json.Parse(line) is Dictionary<string, object?> row)
                    rows.Add(row);
            }
            catch (FormatException)
            {
                broken++;
            }
        }
        if (rows.Count == 0)
            return "Статистики планов пока нет";

        var sb = new StringBuilder($"Планы по моделям (всего {rows.Count}, из {Path.GetFileName(_metricsPath)}{(broken > 0 ? $", битых строк {broken}" : "")}):");
        foreach (IGrouping<string, Dictionary<string, object?>> group in rows.GroupBy(r => r.TryGetValue("model", out object? m) ? m as string ?? "?" : "?"))
        {
            int count = group.Count();
            int accepted = group.Count(r => r.TryGetValue("accepted", out object? a) && a is true);
            sb.Append($"\n  {group.Key}: планов {count}, принято {accepted} ({accepted * 100 / count}%), "
                + $"попыток в среднем {Avg(group, "attempts"):0.0}, вырезано событий {Avg(group, "dropped"):0.0}, "
                + $"токенов {Avg(group, "cost"):0}, время {Avg(group, "seconds"):0} с");
        }
        return sb.ToString();
    }

    private static double Avg(IEnumerable<Dictionary<string, object?>> rows, string key) =>
        rows.Select(r => r.TryGetValue(key, out object? v) && v is double d ? d : 0).DefaultIfEmpty(0).Average();

    public bool Pause(bool paused, out string message)
    {
        if (_paused == paused)
        {
            message = paused ? "Дирижёр уже на паузе" : "Дирижёр и так работает";
            return false;
        }
        _paused = paused;
        message = paused
            ? "Пауза: события плана пропускаются, реакций и переписывания хвоста нет. Вернуть - pmcp director resume"
            : "Дирижёр снова работает";
        _ctx.Log(message);
        return true;
    }

    public bool SkipNext(out string message)
    {
        TimelineEntry? next = NextEntry();
        if (next == null)
        {
            message = "Впереди событий плана нет";
            return false;
        }
        _skipped.Add(next);
        message = "Пропущу:" + next.Describe();
        _ctx.Log(message);
        return true;
    }

    public bool Test(string kind, Action<string, bool>? notify, out string message)
    {
        switch (kind)
        {
            case "end":
                RoundEndReport("Draw");
                message = "Итог раунда отправлен (смотри консоль: в dry-режиме только лог)";
                return true;
            case "tail":
                if (_plan == null || !Round.IsRoundStarted)
                {
                    message = "Нужен идущий раунд с планом";
                    return false;
                }
                if (_tailRun != null)
                {
                    message = "Хвост уже переписывается";
                    return false;
                }
                _tailDone = true;
                return StartTail(notify, out message);
        }

        if (!TestTriggers.TryGetValue(kind, out string trigger))
        {
            message = $"pmcp director test {string.Join("|", TestTriggers.Keys)}|tail|end";
            return false;
        }
        if (_reactRun != null)
        {
            message = "Реакция уже идёт";
            return false;
        }

        var summary = new JsonObject { ["triggers"] = new List<string> { trigger }, ["alive_now"] = ToJson(AliveByTeam()) };
        if (!StartReaction(new List<string> { trigger }, summary, notify, out message))
            return false;
        message = $"Тест реакции «{kind}» запущен ({ReactorModel}){(Round.IsRoundStarted ? "" : " - раунд не идёт, действия сработают в лобби")}";
        return true;
    }
}
