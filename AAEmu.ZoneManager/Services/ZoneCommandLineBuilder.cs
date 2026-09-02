using System.Text;
using AAEmu.ZoneManager.Models;

namespace AAEmu.ZoneManager.Services;

public static class ZoneCommandLineBuilder
{
    public static IReadOnlyList<string> Build(ZoneDefinition zone, ZoneManagerSettings settings)
    {
        var arguments = new List<string>();

        if (settings.Dedicated)
            arguments.Add("-dedicated");
        if (settings.DevMode)
            arguments.Add("-devmode");
        if (settings.FullDump)
            arguments.Add("-fulldump");

        AddCVar(arguments, "world_ip", settings.WorldIp);
        AddCVar(arguments, "world_port", settings.WorldPort.ToString());
        AddCVar(arguments, "world_serveraddr", settings.WorldIp);
        AddCVar(arguments, "world_serverport", settings.WorldPort.ToString());
        AddCVar(arguments, "zone", zone.Name);
        AddCVar(arguments, "sv_map", zone.Name);
        AddCVar(arguments, "db_location", settings.DbLocation);
        AddCVar(arguments, "localized_texts_db_location", settings.LocalizedTextsDbLocation);
        AddCVar(arguments, "locale", settings.Locale);
        AddCVar(arguments, "sys_dedicated_server", settings.SystemDedicatedServer ? "1" : "0");
        AddCVar(arguments, "e_render", settings.DisableRendering ? "0" : "1");
        AddCVar(arguments, "r_Driver", settings.Renderer);
        AddCVar(arguments, "r_Width", settings.RenderWidth.ToString());
        AddCVar(arguments, "r_Height", settings.RenderHeight.ToString());
        AddCVar(arguments, "sys_PakPriority", settings.PakPriority.ToString());
        if (settings.UseFpsLimit && settings.MaxFps > 0)
        {
            AddCVar(arguments, "sys_use_limit_fps", "1");
            AddCVar(arguments, "sys_max_fps", settings.MaxFps.ToString());
        }
        AddCVar(arguments, "npc_move_skip_standing", settings.NpcMoveSkipStanding.ToString());
        AddCVar(arguments, "npc_move_skip_disabledAI", settings.NpcMoveSkipDisabledAi.ToString());
        AddCVar(arguments, "npc_movement_skip", settings.NpcMovementSkip.ToString());
        AddCVar(arguments, "ai_systemupdate", settings.AiSystemUpdate.ToString());
        AddCVar(arguments, "log_Verbosity", settings.LogVerbosity.ToString());

        // NOTE: dedicate sizes its unit vector from default max_unit (101000) before late
        // cvars apply. Unit ObjIds come from ObjectIdManager under that cap; doodads/gimmicks
        // use NonUnitObjectIdManager at 101000+.

        arguments.AddRange(SplitArguments(settings.ExtraArguments));
        return arguments;
    }

    public static string FormatPreview(string executable, IEnumerable<string> arguments) =>
        string.Join(' ', new[] { Quote(executable) }.Concat(arguments.Select(Quote)));

    private static void AddCVar(List<string> arguments, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        arguments.Add($"+{name}");
        arguments.Add(value.Trim());
    }

    private static List<string> SplitArguments(string value)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    private static string Quote(string value) =>
        value.Length == 0 || value.Any(char.IsWhiteSpace)
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
}
