using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BepInEx;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BazaarEventLogger
{
    public static class CombatLoggerJsonl
    {
        private sealed class RecentCombatRecord
        {
            public string EncounterId;
            public string EncounterName;
            public string Payload;
        }

        public static string LogFilePath { get; private set; }

        private const int RecentCombatCapacity = 5;
        private static readonly Queue<RecentCombatRecord> RecentCombats = new Queue<RecentCombatRecord>();
        private static int _combatCount;

        public static string GetRecentCombatsJsonl(string encounterId = null)
        {
            lock (RecentCombats)
            {
                var records = FilterRecentCombats(encounterId).ToList();
                return records.Count == 0
                    ? string.Empty
                    : string.Join(Environment.NewLine, records.Select(record => record.Payload));
            }
        }

        public static void Initialize()
        {
            LogFilePath = Path.Combine(Paths.BepInExRootPath, "CombatSimEvents.jsonl");
            _combatCount = 0;
        }

        public static void LogCombatSim(CombatSim sim, string messageId,
            string encounterId = null, string encounterName = null)
        {
            _combatCount++;

            try
            {
                var root = BuildCombatJson(sim, messageId, _combatCount, encounterId, encounterName);
                var line = root.ToString(Formatting.None);

                lock (RecentCombats)
                {
                    RecentCombats.Enqueue(new RecentCombatRecord
                    {
                        EncounterId = encounterId,
                        EncounterName = encounterName,
                        Payload = line
                    });
                    while (RecentCombats.Count > RecentCombatCapacity)
                        RecentCombats.Dequeue();
                }

                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Failed to write combat jsonl: {ex.Message}");
            }
        }

        private static JObject BuildCombatJson(CombatSim sim, string messageId, int combatNumber,
            string encounterId = null, string encounterName = null)
        {
            var root = new JObject
            {
                ["combat_id"] = combatNumber,
                ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff"),
                ["message_id"] = messageId,
                ["encounter_id"] = encounterId,
                ["encounter_name"] = encounterName,
                ["winner"] = sim.Winner.ToString(),
                ["loser"] = sim.Loser.ToString(),
                ["frame_count"] = sim.Frames?.Count ?? 0
            };

            if (!string.IsNullOrWhiteSpace(encounterId))
            {
                root["encounter_id"] = encounterId;
                root["encounter_name"] = encounterName ?? encounterId;
            }

            // Card stats
            if (sim.CardStats != null && sim.CardStats.Count > 0)
            {
                var statsObj = new JObject();
                foreach (var kvp in sim.CardStats)
                {
                    var cardName = CardDatabase.FormatId(kvp.Key);
                    var cardStats = new JObject();
                    foreach (var s in kvp.Value)
                        cardStats[s.Key.ToString()] = s.Value;
                    statsObj[cardName] = cardStats;
                }
                root["card_stats"] = statsObj;
            }

            // Frames — only non-empty ones
            if (sim.Frames != null)
            {
                var framesArr = new JArray();
                for (int i = 0; i < sim.Frames.Count; i++)
                {
                    var frame = sim.Frames[i];
                    if (frame == null) continue;

                    var frameObj = BuildFrameJson(i, frame);
                    if (frameObj != null)
                        framesArr.Add(frameObj);
                }
                root["frames"] = framesArr;
            }

            return root;
        }

        private static IEnumerable<RecentCombatRecord> FilterRecentCombats(string encounterId)
        {
            return string.IsNullOrWhiteSpace(encounterId)
                ? RecentCombats
                : RecentCombats.Where(record => string.Equals(record.EncounterId, encounterId, StringComparison.OrdinalIgnoreCase));
        }

        private static JObject BuildFrameJson(int index, CombatSimFrame frame)
        {
            bool hasEvents = frame.Events != null && frame.Events.Count > 0;
            bool hasPlayer = HasPlayerChanges(frame.PlayerUpdates);
            bool hasOpponent = HasPlayerChanges(frame.OpponentUpdates);
            bool hasCards = frame.CardUpdates != null && frame.CardUpdates.Count > 0;

            if (!hasEvents && !hasPlayer && !hasOpponent && !hasCards)
                return null;

            var obj = new JObject { ["i"] = index };

            if (hasEvents)
            {
                var eventsArr = new JArray();
                foreach (var evt in frame.Events)
                    eventsArr.Add(BuildEventJson(evt));
                obj["events"] = eventsArr;
            }

            if (hasPlayer)
                obj["player"] = BuildPlayerUpdateJson(frame.PlayerUpdates);

            if (hasOpponent)
                obj["opponent"] = BuildPlayerUpdateJson(frame.OpponentUpdates);

            if (hasCards)
            {
                var cardsObj = new JObject();
                foreach (var kvp in frame.CardUpdates)
                {
                    var cardJson = BuildCardUpdateJson(kvp.Value);
                    if (cardJson != null)
                        cardsObj[CardDatabase.FormatId(kvp.Key.ToString())] = cardJson;
                }
                if (cardsObj.Count > 0)
                    obj["cards"] = cardsObj;
            }

            return obj;
        }

        private static JObject BuildEventJson(ICombatSimEvent evt)
        {
            switch (evt)
            {
                case CombatSimEventEffectExecuted e:
                    var exec = new JObject
                    {
                        ["t"] = "Executed",
                        ["effect"] = e.EffectId,
                        ["action"] = e.ActionType.ToString(),
                        ["source"] = FormatId(e.Source)
                    };
                    if (e.TriggerSource.HasValue)
                        exec["trigger_source"] = FormatId(e.TriggerSource);
                    exec["target"] = FormatTarget(e.Target);
                    return exec;

                case CombatSimEventEffectTriggered e:
                    var trig = new JObject
                    {
                        ["t"] = "Triggered",
                        ["effect"] = e.EffectId,
                        ["source"] = FormatId(e.Source)
                    };
                    if (e.TriggerSource.HasValue)
                        trig["trigger_source"] = FormatId(e.TriggerSource);
                    if (e.Targets != null && e.Targets.Count > 0)
                        trig["targets"] = new JArray(e.Targets.Select(FormatTarget).ToArray());
                    return trig;

                case CombatSimEventEffectAuraExecuted e:
                    var aura = new JObject
                    {
                        ["t"] = "Aura",
                        ["effect"] = e.EffectId,
                        ["source"] = FormatId(e.Source)
                    };
                    if (e.AppliedTo != null && e.AppliedTo.Count > 0)
                        aura["applied_to"] = new JArray(e.AppliedTo.Select(FormatTarget).ToArray());
                    if (e.RemovedFrom != null && e.RemovedFrom.Count > 0)
                        aura["removed_from"] = new JArray(e.RemovedFrom.Select(FormatTarget).ToArray());
                    return aura;

                case CombatSimEventCombatantDied e:
                    return new JObject { ["t"] = "Died", ["combatant"] = e.CombatantId.ToString() };

                case CombatSimEventCardEnchanted e:
                    return new JObject
                    {
                        ["t"] = "Enchanted",
                        ["card"] = CardDatabase.FormatId(e.InstanceId),
                        ["enchantment"] = e.EnchantmentType?.ToString(),
                        ["reverted"] = e.IsReverted
                    };

                case CombatSimEventCardTransformed e:
                    return new JObject
                    {
                        ["t"] = "Transformed",
                        ["original"] = CardDatabase.FormatId(e.OriginalInstanceId),
                        ["context"] = e.ExecutionContextId
                    };

                case CombatSimEventCardTransformReverted e:
                    return new JObject
                    {
                        ["t"] = "TransformReverted",
                        ["cards"] = e.TransformedCardInstanceIds != null
                            ? new JArray(e.TransformedCardInstanceIds.Select(CardDatabase.FormatId).ToArray())
                            : new JArray()
                    };

                case CombatSimEventCardQuestCompleted e:
                    return new JObject
                    {
                        ["t"] = "QuestComplete",
                        ["card"] = CardDatabase.FormatId(e.InstanceId),
                        ["group"] = e.QuestGroupIndex,
                        ["entry"] = e.QuestEntryIndex
                    };

                case CombatSimEventCardQuestUpdated e:
                    return new JObject
                    {
                        ["t"] = "QuestUpdate",
                        ["card"] = CardDatabase.FormatId(e.InstanceId),
                        ["group"] = e.QuestGroupIndex,
                        ["entry"] = e.QuestEntryIndex,
                        ["prev"] = e.OldProgress,
                        ["curr"] = e.NewProgress
                    };

                case CombatSimEventMonsterGoldReceived e:
                    return new JObject { ["t"] = "MonsterGold", ["health_amount"] = e.HealthAmount };

                case CombatSimEventMonsterXpReceived e:
                    return new JObject { ["t"] = "MonsterXP", ["health_amount"] = e.HealthAmount };

                case CombatSimEventSandstormCountdownStarted _:
                    return new JObject { ["t"] = "SandstormCountdown" };

                case CombatSimEventSandstormStarted _:
                    return new JObject { ["t"] = "Sandstorm" };

                default:
                    return new JObject { ["t"] = "Unknown", ["type_name"] = evt?.GetType().Name };
            }
        }

        private static JObject BuildPlayerUpdateJson(CombatSimPlayerUpdate update)
        {
            if (update == null) return null;

            var obj = new JObject();

            if (update.IsPlayerDead)
                obj["dead"] = true;

            if (update.HealthAdjustments != null && update.HealthAdjustments.Count > 0)
            {
                var arr = new JArray();
                foreach (var adj in update.HealthAdjustments)
                {
                    var a = new JObject
                    {
                        ["attr"] = adj.AttributeChanged.ToString(),
                        ["amount"] = adj.Amount,
                        ["dmg_type"] = adj.DamageType.ToString()
                    };
                    if (adj.IsCrit) a["crit"] = true;
                    if (adj.IsDamageReduced) a["reduced"] = true;
                    arr.Add(a);
                }
                obj["health_adj"] = arr;
            }

            if (update.Attributes != null && update.Attributes.Count > 0)
            {
                var attrs = new JObject();
                foreach (var attr in update.Attributes)
                {
                    attrs[attr.Key.ToString()] = new JArray(attr.Value.PreviousValue, attr.Value.CurrentValue);
                }
                obj["attrs"] = attrs;
            }

            return obj.Count > 0 ? obj : null;
        }

        private static JObject BuildCardUpdateJson(CombatSimCardUpdate card)
        {
            if (card == null) return null;

            var obj = new JObject();

            if (card.State != null)
                obj["state"] = new JArray(card.State.PreviousValue.ToString(), card.State.CurrentValue.ToString());

            if (card.Attributes != null && card.Attributes.Count > 0)
            {
                var attrs = new JObject();
                foreach (var attr in card.Attributes)
                    attrs[attr.Key.ToString()] = new JArray(attr.Value.PreviousValue, attr.Value.CurrentValue);
                obj["attrs"] = attrs;
            }

            return obj.Count > 0 ? obj : null;
        }

        private static bool HasPlayerChanges(CombatSimPlayerUpdate update)
        {
            if (update == null) return false;
            return update.IsPlayerDead ||
                   (update.HealthAdjustments != null && update.HealthAdjustments.Count > 0) ||
                   (update.Attributes != null && update.Attributes.Count > 0);
        }

        private static string FormatId(InstanceId? id)
        {
            return id.HasValue ? CardDatabase.FormatId(id.Value.ToString()) : null;
        }

        private static JToken FormatTarget(IEffectTarget target)
        {
            switch (target)
            {
                case EffectTargetCard c:
                    return CardDatabase.FormatId(c.Target.ToString());
                case EffectTargetPlayer p:
                    return p.Target.ToString();
                default:
                    return target?.ToString();
            }
        }
    }
}
