using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BazaarEventLogger
{
    /// <summary>
    /// Converts SimulationEngine trace output into structured JSONL,
    /// enabling automated comparison with real combat logs (CombatSimEvents.jsonl).
    /// </summary>
    public static class SimulationJsonlWriter
    {
        /// <summary>
        /// Serialize a single simulation result as one JSON line.
        /// </summary>
        public static string WriteTraceJsonl(
            SingleSimulationResult result,
            string encounterName,
            string monsterName,
            int? seed = null)
        {
            var root = new JObject
            {
                ["encounter"] = encounterName,
                ["monster"] = monsterName,
                ["winner"] = result.Winner,
                ["duration_ms"] = result.DurationMs,
                ["player_hp_remaining"] = result.PlayerHealthRemaining,
                ["opponent_hp_remaining"] = result.OpponentHealthRemaining,
                ["sandstorm"] = result.SandstormTriggered,
                ["tick_count"] = result.Trace.Count
            };

            if (seed.HasValue)
                root["seed"] = seed.Value;

            var ticks = new JArray();
            foreach (var entry in result.Trace)
            {
                var tick = new JObject
                {
                    ["t"] = entry.TimeMs,
                    ["summary"] = entry.Summary
                };

                var playerState = ParseCombatantState(entry.PlayerState);
                if (playerState != null)
                    tick["player"] = playerState;

                var opponentState = ParseCombatantState(entry.OpponentState);
                if (opponentState != null)
                    tick["opponent"] = opponentState;

                if (entry.CardStates != null && entry.CardStates.Count > 0)
                {
                    var cards = new JObject();
                    foreach (var cs in entry.CardStates)
                    {
                        var parsed = ParseCardState(cs);
                        if (parsed.HasValue)
                            cards[parsed.Value.Key] = parsed.Value.Value;
                    }
                    tick["cards"] = cards;
                }

                if (entry.Events != null && entry.Events.Count > 0)
                {
                    var events = new JArray();
                    foreach (var evt in entry.Events)
                    {
                        var parsed = ParseEvent(evt);
                        if (parsed != null)
                            events.Add(parsed);
                    }
                    tick["events"] = events;
                }

                ticks.Add(tick);
            }

            root["ticks"] = ticks;
            return root.ToString(Formatting.None);
        }

        // ── State parsers ──────────────────────────────────────────────

        // Format: "Player:HP=100/200,Shield=50,Burn=0,Poison=0,Regen=5,Rage=10/50,Enraged=0"
        private static JObject ParseCombatantState(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return null;

            var colonIdx = raw.IndexOf(':');
            if (colonIdx < 0)
                return null;

            var obj = new JObject();
            var kvPart = raw.Substring(colonIdx + 1); // "HP=100/200,Shield=50,..."

            foreach (var token in kvPart.Split(','))
            {
                var eqIdx = token.IndexOf('=');
                if (eqIdx < 0)
                    continue;

                var key = token.Substring(0, eqIdx);
                var val = token.Substring(eqIdx + 1);

                switch (key)
                {
                    case "HP":
                        var slashIdx = val.IndexOf('/');
                        if (slashIdx >= 0)
                        {
                            obj["hp"] = ParseInt(val.Substring(0, slashIdx));
                            obj["hp_max"] = ParseInt(val.Substring(slashIdx + 1));
                        }
                        else
                        {
                            obj["hp"] = ParseInt(val);
                        }
                        break;

                    case "Shield":
                        obj["shield"] = ParseInt(val);
                        break;
                    case "Burn":
                        obj["burn"] = ParseInt(val);
                        break;
                    case "Poison":
                        obj["poison"] = ParseInt(val);
                        break;
                    case "Regen":
                        obj["regen"] = ParseInt(val);
                        break;
                    case "Rage":
                        var rageSlash = val.IndexOf('/');
                        if (rageSlash >= 0)
                        {
                            obj["rage"] = ParseInt(val.Substring(0, rageSlash));
                            obj["rage_max"] = ParseInt(val.Substring(rageSlash + 1));
                        }
                        else
                        {
                            obj["rage"] = ParseInt(val);
                        }
                        break;

                    case "Enraged":
                        obj["enraged"] = ParseInt(val);
                        break;

                    default:
                        obj[key.ToLowerInvariant()] = ParseInt(val);
                        break;
                }
            }

            return obj;
        }

        // Format: "P:CardName:CD=1500/2000,Freeze=0,Haste=0,Slow=0,x1"
        private static KeyValuePair<string, JToken>? ParseCardState(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return null;

            // Split into prefix:name:kvPairs
            var firstColon = raw.IndexOf(':');
            if (firstColon < 0)
                return null;

            var prefix = raw.Substring(0, firstColon);
            var rest = raw.Substring(firstColon + 1);

            // Find the last segment that starts with "CD="
            var cdIdx = rest.IndexOf(":CD=", StringComparison.Ordinal);
            if (cdIdx < 0)
                return null;

            var cardName = rest.Substring(0, cdIdx);
            var kvPart = rest.Substring(cdIdx + 1);
            var key = $"{prefix}:{cardName}";

            var obj = new JObject();
            foreach (var token in kvPart.Split(','))
            {
                if (token.StartsWith("x", StringComparison.Ordinal) && int.TryParse(token.Substring(1), out var mc))
                {
                    obj["multicast"] = mc;
                    continue;
                }

                var eqIdx = token.IndexOf('=');
                if (eqIdx < 0)
                    continue;

                var k = token.Substring(0, eqIdx);
                var v = token.Substring(eqIdx + 1);

                switch (k)
                {
                    case "CD":
                        var slashIdx = v.IndexOf('/');
                        if (slashIdx >= 0)
                        {
                            obj["cd"] = ParseInt(v.Substring(0, slashIdx));
                            obj["cd_max"] = ParseInt(v.Substring(slashIdx + 1));
                        }
                        else
                        {
                            obj["cd"] = ParseInt(v);
                        }
                        break;
                    case "Freeze":
                        obj["freeze"] = ParseInt(v);
                        break;
                    case "Haste":
                        obj["haste"] = ParseInt(v);
                        break;
                    case "Slow":
                        obj["slow"] = ParseInt(v);
                        break;
                    default:
                        obj[k.ToLowerInvariant()] = ParseInt(v);
                        break;
                }
            }

            return new KeyValuePair<string, JToken>(key, obj);
        }

        // ── Event parser ───────────────────────────────────────────────

        // Effect label: "{owner}:{card}:{effectType}={effectValue}->{scope}"
        // Optionally followed by "#cast{N}"
        // Optionally followed by ":{target}:{key}={val}:..."
        private static readonly Regex EffectLabelRegex = new Regex(
            @"^(?<owner>\w+):(?<card>.+?):(?<effectType>[\w_]+)=(?<effectValue>-?\d+)->(?<scope>[^:#]+?)(?:#cast(?<cast>\d+))?(?::(?<rest>.*))?$",
            RegexOptions.Compiled);

        private static JObject ParseEvent(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return null;

            // ── Simple / fixed patterns first ──

            if (raw == "combat_end:dot_or_sandstorm")
                return new JObject { ["t"] = "combat_end", ["reason"] = "dot_or_sandstorm" };

            if (raw.StartsWith("sandstorm:", StringComparison.Ordinal))
                return ParseSandstormEvent(raw);

            // "{owner}:trigger:{card}"
            if (TrySplitSimple(raw, out var parts))
            {
                if (parts.Length >= 3 && parts[1] == "trigger")
                    return new JObject { ["t"] = "trigger", ["owner"] = parts[0], ["card"] = string.Join(":", parts.Skip(2)) };

                if (parts.Length >= 3 && parts[1] == "reset_cd")
                {
                    var resetPart = string.Join(":", parts.Skip(2));
                    var eqIdx = resetPart.LastIndexOf('=');
                    if (eqIdx >= 0)
                        return new JObject
                        {
                            ["t"] = "reset_cd",
                            ["owner"] = parts[0],
                            ["card"] = resetPart.Substring(0, eqIdx),
                            ["cd"] = ParseInt(resetPart.Substring(eqIdx + 1))
                        };
                }

                // Enrage events
                if (parts.Length >= 2)
                {
                    if (parts[1] == "enraged_end")
                        return new JObject { ["t"] = "enraged_end", ["owner"] = parts[0] };

                    if (parts[1] == "enraged_tick" && parts.Length >= 3)
                        return new JObject { ["t"] = "enraged_tick", ["owner"] = parts[0], ["duration"] = ParseInt(parts[2]) };

                    if (parts[1] == "enraged_start" && parts.Length >= 3)
                        return new JObject { ["t"] = "enraged_start", ["owner"] = parts[0], ["duration"] = ParseInt(parts[2]) };
                }

                // DoT events: "{target}:burn_tick:{val}:hp={hp}:shield={shield}"
                if (parts.Length >= 2)
                {
                    if (parts[1] == "burn_tick" && parts.Length >= 3)
                        return ParseDotTickEvent(raw, parts[0], "burn_tick");

                    if (parts[1] == "burn_decay" && parts.Length >= 3)
                        return new JObject { ["t"] = "burn_decay", ["target"] = parts[0], ["value"] = ParseInt(parts[2]) };

                    if (parts[1] == "poison_tick" && parts.Length >= 3)
                        return ParseDotTickEvent(raw, parts[0], "poison_tick");

                    if (parts[1] == "regen_tick" && parts.Length >= 3)
                        return ParseRegenTickEvent(raw, parts[0]);
                }
            }

            // ── Effect-based events (contain "->") ──
            if (raw.Contains("->"))
                return ParseEffectEvent(raw);

            // Fallback: return raw string in a wrapper
            return new JObject { ["t"] = "unknown", ["raw"] = raw };
        }

        private static JObject ParseSandstormEvent(string raw)
        {
            // "sandstorm:{damage}:player_hp={hp}:opponent_hp={hp}"
            var obj = new JObject { ["t"] = "sandstorm" };
            var kvPairs = ParseKvPairsFromColonString(raw, 1);
            if (kvPairs.TryGetValue("_positional_1", out var dmg))
                obj["value"] = dmg;
            if (kvPairs.TryGetValue("player_hp", out var php))
                obj["player_hp"] = php;
            if (kvPairs.TryGetValue("opponent_hp", out var ohp))
                obj["opponent_hp"] = ohp;
            return obj;
        }

        private static JObject ParseDotTickEvent(string raw, string target, string type)
        {
            // "{target}:burn_tick:{val}:hp={hp}:shield={shield}"
            var obj = new JObject { ["t"] = type, ["target"] = target };
            var kvPairs = ParseKvPairsFromColonString(raw, 2);
            if (kvPairs.TryGetValue("_positional_2", out var val))
                obj["value"] = val;
            if (kvPairs.TryGetValue("hp", out var hp))
                obj["hp"] = hp;
            if (kvPairs.TryGetValue("shield", out var sh))
                obj["shield"] = sh;
            return obj;
        }

        private static JObject ParseRegenTickEvent(string raw, string target)
        {
            // "{target}:regen_tick:{val}:hp={hp}/{max}"
            var obj = new JObject { ["t"] = "regen_tick", ["target"] = target };
            var colonParts = raw.Split(':');
            if (colonParts.Length >= 3)
                obj["value"] = ParseInt(colonParts[2]);
            if (colonParts.Length >= 4)
            {
                var hpPart = colonParts[3]; // "hp=100/200"
                var eqIdx = hpPart.IndexOf('=');
                if (eqIdx >= 0)
                {
                    var hpVal = hpPart.Substring(eqIdx + 1);
                    var slashIdx = hpVal.IndexOf('/');
                    if (slashIdx >= 0)
                    {
                        obj["hp"] = ParseInt(hpVal.Substring(0, slashIdx));
                        obj["hp_max"] = ParseInt(hpVal.Substring(slashIdx + 1));
                    }
                    else
                    {
                        obj["hp"] = ParseInt(hpVal);
                    }
                }
            }
            return obj;
        }

        private static JObject ParseEffectEvent(string raw)
        {
            var m = EffectLabelRegex.Match(raw);
            if (!m.Success)
                return new JObject { ["t"] = "effect", ["raw"] = raw };

            var owner = m.Groups["owner"].Value;
            var card = m.Groups["card"].Value;
            var effectType = m.Groups["effectType"].Value;
            var effectValue = ParseInt(m.Groups["effectValue"].Value);
            var scope = m.Groups["scope"].Value;
            var castIndex = m.Groups["cast"].Success ? ParseInt(m.Groups["cast"].Value) : 0;
            var rest = m.Groups["rest"].Success ? m.Groups["rest"].Value : null;

            var obj = new JObject
            {
                ["t"] = NormalizeEffectType(effectType),
                ["owner"] = owner,
                ["card"] = card,
                ["value"] = effectValue,
                ["scope"] = scope
            };

            if (castIndex > 0)
                obj["cast"] = castIndex;

            // Parse rest: "{targetName}:{key}={val}:{key}={val}[:crit]"
            if (!string.IsNullOrEmpty(rest))
                ParseEffectRestFields(obj, effectType, rest);

            return obj;
        }

        private static void ParseEffectRestFields(JObject obj, string effectType, string rest)
        {
            // Split rest into parts; first part is target name (unless it contains '=')
            var segments = rest.Split(':');
            var startIdx = 0;

            if (segments.Length > 0 && !segments[0].Contains("=") && segments[0] != "crit")
            {
                obj["target"] = segments[0];
                startIdx = 1;
            }

            for (var i = startIdx; i < segments.Length; i++)
            {
                var seg = segments[i];

                if (seg == "crit")
                {
                    obj["crit"] = true;
                    continue;
                }

                if (seg == "rage_blocked_enraged")
                {
                    obj["rage_blocked"] = true;
                    continue;
                }

                var eqIdx = seg.IndexOf('=');
                if (eqIdx < 0)
                    continue;

                var key = seg.Substring(0, eqIdx);
                var val = seg.Substring(eqIdx + 1);

                // Handle "hp=100/200" style
                var slashIdx = val.IndexOf('/');
                if (slashIdx >= 0)
                {
                    obj[key] = ParseInt(val.Substring(0, slashIdx));
                    obj[key + "_max"] = ParseInt(val.Substring(slashIdx + 1));
                }
                else
                {
                    obj[key] = ParseInt(val);
                }
            }
        }

        private static string NormalizeEffectType(string effectType)
        {
            // Collapse burn/burn_apply, poison/poison_apply, shield/shield_apply
            switch (effectType)
            {
                case "burn_apply": return "burn";
                case "poison_apply": return "poison";
                case "shield_apply": return "shield";
                default: return effectType;
            }
        }

        // ── Helpers ────────────────────────────────────────────────────

        private static bool TrySplitSimple(string raw, out string[] parts)
        {
            // Only split events without "->" by ":"
            if (raw.Contains("->"))
            {
                parts = null;
                return false;
            }
            parts = raw.Split(':');
            return parts.Length >= 2;
        }

        /// <summary>
        /// Parse colon-delimited string where some segments are key=value and some are positional.
        /// "sandstorm:50:player_hp=100:opponent_hp=80"
        /// Returns: { "_positional_1": 50, "player_hp": 100, "opponent_hp": 80 }
        /// </summary>
        private static Dictionary<string, int> ParseKvPairsFromColonString(string raw, int skipParts)
        {
            var result = new Dictionary<string, int>();
            var segments = raw.Split(':');
            var positionalCounter = skipParts;

            for (var i = skipParts; i < segments.Length; i++)
            {
                var seg = segments[i];
                var eqIdx = seg.IndexOf('=');
                if (eqIdx >= 0)
                {
                    var key = seg.Substring(0, eqIdx);
                    var val = seg.Substring(eqIdx + 1);
                    result[key] = ParseInt(val);
                }
                else
                {
                    result[$"_positional_{positionalCounter}"] = ParseInt(seg);
                }
                positionalCounter++;
            }

            return result;
        }

        private static int ParseInt(string s)
        {
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
    }
}
