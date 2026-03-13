using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BepInEx;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BazaarEventLogger
{
    public static class GameSimLoggerJsonl
    {
        private const int RecentCapacity = 200;
        private const long MaxLogBytesBeforeRotate = 50L * 1024L * 1024L; // 50MB
        private static readonly Queue<string> RecentLines = new Queue<string>();

        public static string LogFilePath { get; private set; }
        private static int _messageCount;

        public static void Initialize()
        {
            LogFilePath = Path.Combine(Paths.BepInExRootPath, "GameSimEvents.jsonl");
            _messageCount = 0;

            try
            {
                RotateIfTooLarge();
                var start = new JObject
                {
                    ["record_type"] = "session_start",
                    ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff"),
                    ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
                    ["plugin_version"] = Plugin.PluginVersion
                };
                AppendLine(start.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Failed to init GameSim jsonl: {ex.Message}");
            }
        }

        public static string GetRecentGameSimsJsonl(int maxRecords = 200)
        {
            lock (RecentLines)
            {
                if (RecentLines.Count == 0)
                    return string.Empty;

                maxRecords = Math.Max(1, maxRecords);
                return string.Join(Environment.NewLine, RecentLines.TakeLast(Math.Min(maxRecords, RecentLines.Count)));
            }
        }

        public static void LogGameSim(GameSim sim, string messageId)
        {
            _messageCount++;

            try
            {
                RotateIfTooLarge();
                var root = BuildGameSimJson(sim, messageId, _messageCount);
                AppendLine(root.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Failed to write GameSim jsonl: {ex.Message}");
            }
        }

        private static void AppendLine(string line)
        {
            lock (RecentLines)
            {
                RecentLines.Enqueue(line);
                while (RecentLines.Count > RecentCapacity)
                    RecentLines.Dequeue();
            }

            File.AppendAllText(LogFilePath, line + Environment.NewLine);
        }

        private static void RotateIfTooLarge()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(LogFilePath) || !File.Exists(LogFilePath))
                    return;

                var info = new FileInfo(LogFilePath);
                if (info.Length < MaxLogBytesBeforeRotate)
                    return;

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var dir = Path.GetDirectoryName(LogFilePath) ?? Paths.BepInExRootPath;
                var rotated = Path.Combine(dir, $"GameSimEvents.{stamp}.jsonl");
                var suffix = 1;
                while (File.Exists(rotated))
                {
                    rotated = Path.Combine(dir, $"GameSimEvents.{stamp}.{suffix}.jsonl");
                    suffix++;
                }

                File.Move(LogFilePath, rotated);
            }
            catch
            {
                // best-effort rotation only
            }
        }

        private static JObject BuildGameSimJson(GameSim sim, string messageId, int seq)
        {
            var root = new JObject
            {
                ["record_type"] = "gamesim",
                ["seq"] = seq,
                ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff"),
                ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
                ["message_id"] = messageId
            };

            if (sim?.Run != null)
            {
                root["run"] = new JObject
                {
                    ["day"] = sim.Run.Day,
                    ["hour"] = sim.Run.Hour,
                    ["wins"] = sim.Run.Victories,
                    ["losses"] = sim.Run.Defeats,
                    ["hour_xp"] = sim.Run.CurrentHourXP
                };
            }

            if (sim?.CurrentState != null)
            {
                root["state"] = new JObject
                {
                    ["name"] = sim.CurrentState.StateName.ToString(),
                    ["encounter_id"] = sim.CurrentState.CurrentEncounterId,
                    ["encounter_name"] = CardDatabase.ResolveName(sim.CurrentState.CurrentEncounterId),
                    ["reroll_cost"] = sim.CurrentState.RerollCost,
                    ["rerolls_left"] = sim.CurrentState.RerollsRemaining,
                    ["selection_set"] = sim.CurrentState.SelectionSet != null
                        ? new JArray(sim.CurrentState.SelectionSet.Select(CardDatabase.ResolveName).ToArray())
                        : new JArray()
                };
            }

            if (sim?.Player != null)
            {
                var p = BuildPlayerJson(sim.Player);
                if (p != null)
                    root["player"] = p;
            }

            if (sim?.Opponent != null)
            {
                var o = BuildPlayerJson(sim.Opponent);
                if (o != null)
                    root["opponent"] = o;
            }

            if (sim?.Events != null && sim.Events.Count > 0)
            {
                var arr = new JArray();
                foreach (var evt in sim.Events)
                {
                    var parsed = BuildEventJson(evt);
                    if (parsed != null)
                        arr.Add(parsed);
                }
                root["events"] = arr;
            }

            if (sim?.Cards != null && sim.Cards.Count > 0)
            {
                var cards = new JArray();
                foreach (var kvp in sim.Cards)
                {
                    var card = kvp.Value;
                    if (card == null)
                        continue;

                    var instanceId = card.InstanceId ?? kvp.Key;
                    var templateId = CardDatabase.GetInfo(instanceId)?.Id;
                    var obj = new JObject
                    {
                        ["instance_id"] = instanceId,
                        ["name"] = CardDatabase.FormatId(instanceId),
                        ["template_id"] = templateId,
                        ["template_name"] = string.IsNullOrEmpty(templateId) ? null : CardDatabase.ResolveName(templateId),
                        ["state"] = card.State.ToString()
                    };

                    if (card.Tier.HasValue) obj["tier"] = card.Tier.Value.ToString();
                    if (card.Enchantment.HasValue) obj["enchant"] = card.Enchantment.Value.ToString();
                    if (card.Size.HasValue) obj["size"] = card.Size.Value.ToString();

                    if (card.Placement != null)
                    {
                        obj["placement"] = new JObject
                        {
                            ["owner"] = card.Placement.Owner?.ToString(),
                            ["section"] = card.Placement.Section?.ToString(),
                            ["socket"] = card.Placement.Socket?.ToString()
                        };
                    }

                    if (card.Attributes != null && card.Attributes.Count > 0)
                    {
                        var attrs = new JObject();
                        foreach (var attr in card.Attributes.Where(a => a.Value.DeltaType == EAttributeDeltaType.Update))
                            attrs[attr.Key.ToString()] = attr.Value.Value;
                        if (attrs.Count > 0)
                            obj["attrs"] = attrs;
                    }

                    if (card.Tags != null && card.Tags.Count > 0)
                        obj["tags"] = new JArray(card.Tags.ToArray());

                    cards.Add(obj);
                }
                root["cards"] = cards;
            }

            return root;
        }

        private static JObject BuildPlayerJson(SimUpdatePlayer player)
        {
            if (player?.Attributes == null || player.Attributes.Count == 0)
                return null;

            var attrs = new JObject();
            foreach (var a in player.Attributes)
                attrs[a.Key.ToString()] = a.Value;

            return new JObject
            {
                ["id"] = player.CombatantId.ToString(),
                ["attrs"] = attrs
            };
        }

        private static JObject BuildEventJson(IGameSimEvent evt)
        {
            if (evt == null) return null;

            switch (evt)
            {
                case GameSimEventCardDealt e:
                    return new JObject
                    {
                        ["t"] = "CardDealt",
                        ["template_id"] = e.TemplateId,
                        ["template_name"] = CardDatabase.ResolveName(e.TemplateId),
                        ["instance_id"] = e.InstanceId,
                        ["instance_name"] = CardDatabase.FormatId(e.InstanceId),
                        ["card_type"] = e.Type.ToString()
                    };

                case GameSimEventCardPurchased e:
                    return new JObject
                    {
                        ["t"] = "CardPurchased",
                        ["instance_id"] = e.InstanceId,
                        ["instance_name"] = CardDatabase.FormatId(e.InstanceId),
                        ["price"] = e.BuyPrice,
                        ["section"] = e.Section.ToString(),
                        ["socket_left_id"] = e.LeftSocketId.HasValue ? e.LeftSocketId.Value.ToString() : null,
                        ["who"] = e.CombatantId.ToString()
                    };

                case GameSimEventCardSold e:
                    return new JObject
                    {
                        ["t"] = "CardSold",
                        ["instance_id"] = e.InstanceId,
                        ["instance_name"] = CardDatabase.FormatId(e.InstanceId),
                        ["sell_price"] = e.SellPrice
                    };

                case GameSimEventCardSpawned e:
                    return new JObject
                    {
                        ["t"] = "CardSpawned",
                        ["template_id"] = e.TemplateId,
                        ["template_name"] = CardDatabase.ResolveName(e.TemplateId),
                        ["instance_id"] = e.InstanceId,
                        ["instance_name"] = CardDatabase.FormatId(e.InstanceId),
                        ["card_type"] = e.Type.ToString(),
                        ["owner"] = e.CombatantId.ToString(),
                        ["section"] = e.Section.ToString()
                    };

                case GameSimEventCardUpgraded e:
                    return new JObject
                    {
                        ["t"] = "CardUpgraded",
                        ["instance_id"] = e.InstanceId,
                        ["instance_name"] = CardDatabase.FormatId(e.InstanceId),
                        ["new_tier"] = e.NewTier.ToString()
                    };

                case GameSimEventCardMoved e:
                    return new JObject
                    {
                        ["t"] = "CardMoved",
                        ["instance_id"] = e.InstanceId,
                        ["instance_name"] = CardDatabase.FormatId(e.InstanceId),
                        ["owner"] = e.Owner.ToString(),
                        ["to_inventory"] = e.ToInventory.ToString(),
                        ["to_socket"] = e.ToSocket.ToString()
                    };

                case GameSimEventCardDisposed e:
                    return new JObject
                    {
                        ["t"] = "CardDisposed",
                        ["instance_id"] = e.InstanceId,
                        ["instance_name"] = CardDatabase.FormatId(e.InstanceId),
                        ["reason"] = e.Reason.ToString()
                    };

                case GameSimEventCardEnchanted e:
                    return new JObject
                    {
                        ["t"] = "CardEnchanted",
                        ["instance_id"] = e.InstanceId,
                        ["instance_name"] = CardDatabase.FormatId(e.InstanceId),
                        ["enchant"] = e.EnchantmentType.ToString(),
                        ["reverted"] = e.IsReverted
                    };

                case GameSimEventCardFused e:
                    return new JObject
                    {
                        ["t"] = "CardFused",
                        ["selected_instance_id"] = e.SelectedInstanceId,
                        ["selected_instance_name"] = CardDatabase.FormatId(e.SelectedInstanceId),
                        ["fused_instance_id"] = e.FusedInstanceId,
                        ["fused_instance_name"] = CardDatabase.FormatId(e.FusedInstanceId),
                        ["new_tier"] = e.NewTier.ToString(),
                        ["enchant"] = e.EnchantmentType.ToString()
                    };

                case GameSimEventCardTransformed e:
                    return new JObject
                    {
                        ["t"] = "CardTransformed",
                        ["instance_id"] = e.OriginalInstanceId,
                        ["instance_name"] = CardDatabase.FormatId(e.OriginalInstanceId),
                        ["ctx"] = e.ExecutionContextId
                    };

                case GameSimEventCardTransformReverted e:
                    return new JObject
                    {
                        ["t"] = "CardTransformReverted",
                        ["cards"] = e.TransformedCardInstanceIds != null
                            ? new JArray(e.TransformedCardInstanceIds.Select(CardDatabase.FormatId).ToArray())
                            : new JArray()
                    };

                case GameSimEventCardQuestCompleted e:
                    return new JObject
                    {
                        ["t"] = "QuestComplete",
                        ["instance_id"] = e.InstanceId,
                        ["instance_name"] = CardDatabase.FormatId(e.InstanceId),
                        ["group"] = e.QuestGroupIndex,
                        ["entry"] = e.QuestEntryIndex
                    };

                case GameSimEventCardQuestUpdated e:
                    return new JObject
                    {
                        ["t"] = "QuestUpdate",
                        ["instance_id"] = e.InstanceId,
                        ["instance_name"] = CardDatabase.FormatId(e.InstanceId),
                        ["group"] = e.QuestGroupIndex,
                        ["entry"] = e.QuestEntryIndex,
                        ["prev"] = e.OldProgress,
                        ["curr"] = e.NewProgress
                    };

                case GameSimEventEffectExecuted e:
                    return new JObject
                    {
                        ["t"] = "EffectExecuted",
                        ["effect_id"] = e.EffectId,
                        ["action"] = e.ActionType.ToString(),
                        ["source"] = e.Source.ToString(),
                        ["target"] = e.Target.ToString()
                    };

                case GameSimEventEffectAuraExecuted e:
                    return new JObject
                    {
                        ["t"] = "EffectAura",
                        ["effect_id"] = e.EffectId,
                        ["source"] = e.Source?.ToString(),
                        ["applied"] = e.AppliedTo?.Count ?? 0,
                        ["removed"] = e.RemovedFrom?.Count ?? 0
                    };

                case GameSimEventPlayerInitialized e:
                    return new JObject { ["t"] = "PlayerInitialized", ["player"] = e.CombatantId.ToString(), ["hero"] = e.Hero.ToString() };

                case GameSimEventPlayerExperienceGained e:
                    return new JObject { ["t"] = "PlayerXP", ["amount"] = e.Amount };

                case GameSimEventPlayerIncomeGained e:
                    return new JObject { ["t"] = "PlayerIncome", ["amount"] = e.Amount };

                case GameSimEventPlayerPrestigeChanged e:
                    return new JObject { ["t"] = "PlayerPrestige", ["delta"] = e.Delta };

                case GameSimEventPlayerSkillEquipped e:
                    return new JObject
                    {
                        ["t"] = "SkillEquipped",
                        ["instance_id"] = e.InstanceId,
                        ["instance_name"] = CardDatabase.FormatId(e.InstanceId),
                        ["owner"] = e.Owner.ToString()
                    };

                case GameSimEventRunDayChanged e:
                    return new JObject { ["t"] = "DayChanged", ["day"] = e.NewDay };

                case GameSimEventRunHourChanged e:
                    return new JObject { ["t"] = "HourChanged", ["hour"] = e.NewHour };

                case GameSimEventRunCompleted e:
                    return new JObject { ["t"] = "RunCompleted", ["rewards"] = e.Rewards?.ToString() };

                case GameSimEventRunRerollCostChanged e:
                    return new JObject { ["t"] = "RerollCostChanged", ["cost"] = e.NewCost };

                case GameSimEventStateTransitioned e:
                    return new JObject
                    {
                        ["t"] = "StateTransitioned",
                        ["to"] = e.ToState.ToString(),
                        ["encounter_id"] = e.CurrentEncounterId,
                        ["encounter_name"] = CardDatabase.ResolveName(e.CurrentEncounterId)
                    };

                case GameSimEventStateSuspended e:
                    return new JObject
                    {
                        ["t"] = "StateSuspended",
                        ["to"] = e.ToState.ToString(),
                        ["encounter_id"] = e.CurrentEncounterId,
                        ["encounter_name"] = CardDatabase.ResolveName(e.CurrentEncounterId)
                    };

                case GameSimEventStateResumed e:
                    return new JObject
                    {
                        ["t"] = "StateResumed",
                        ["to"] = e.ToState.ToString(),
                        ["encounter_id"] = e.CurrentEncounterId,
                        ["encounter_name"] = CardDatabase.ResolveName(e.CurrentEncounterId)
                    };

                case GameSimEventInterruptStateEntered e:
                    return new JObject { ["t"] = "InterruptEntered", ["state"] = e.EnteredState.ToString(), ["is_interrupt"] = e.IsInterrupt };

                case GameSimEventInterruptStateExited e:
                    return new JObject { ["t"] = "InterruptExited", ["state"] = e.ExitedState.ToString(), ["is_interrupt"] = e.IsInterrupt };

                case GameSimEventPedestalActivated _:
                    return new JObject { ["t"] = "PedestalActivated" };

                case GameSimEventSocketsUnlocked e:
                    return new JObject
                    {
                        ["t"] = "SocketsUnlocked",
                        ["section"] = e.Section.ToString(),
                        ["sockets"] = e.UnlockedSockets != null ? new JArray(e.UnlockedSockets.ToArray()) : new JArray()
                    };

                case GameSimEventBoardOverridden e:
                    return new JObject { ["t"] = "BoardOverridden", ["board"] = e.Board.ToString(), ["presentation"] = e.Presentation.ToString() };

                default:
                    return new JObject
                    {
                        ["t"] = "Unknown",
                        ["type_name"] = evt.GetType().Name,
                        ["raw"] = evt.ToString()
                    };
            }
        }
    }
}
