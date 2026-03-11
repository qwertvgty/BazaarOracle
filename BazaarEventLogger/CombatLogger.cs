using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BepInEx;

namespace BazaarEventLogger
{
    public static class CombatLogger
    {
        public static string LogFilePath { get; private set; }
        private static int _combatCount;

        public static void Initialize()
        {
            LogFilePath = Path.Combine(Paths.BepInExRootPath, "CombatSimEvents.log");
            var header = $"=== Combat Logger Started @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}";
            File.AppendAllText(LogFilePath, header);
            _combatCount = 0;
        }

        public static void LogCombatSim(CombatSim sim, string messageId)
        {
            _combatCount++;
            var sb = new StringBuilder();

            sb.AppendLine();
            sb.AppendLine($"╔══════════════════════════════════════════════════════════════");
            sb.AppendLine($"║ Combat #{_combatCount} | MsgID: {messageId} | {DateTime.Now:HH:mm:ss.fff}");
            sb.AppendLine($"║ Winner: {sim.Winner}, Loser: {sim.Loser}");
            sb.AppendLine($"║ Frames: {sim.Frames?.Count ?? 0}");
            sb.AppendLine($"╠══════════════════════════════════════════════════════════════");

            // Card stats summary
            if (sim.CardStats != null && sim.CardStats.Count > 0)
            {
                sb.AppendLine($"║ ── Card Stats Summary ──");
                foreach (var kvp in sim.CardStats)
                {
                    var cardName = CardDatabase.FormatId(kvp.Key);
                    var stats = string.Join(", ", kvp.Value.Select(s => $"{s.Key}={s.Value}"));
                    sb.AppendLine($"║   {cardName}: {stats}");
                }
            }

            // Frame-by-frame details
            if (sim.Frames != null)
            {
                for (int i = 0; i < sim.Frames.Count; i++)
                {
                    var frame = sim.Frames[i];
                    if (frame == null) continue;

                    bool hasContent = (frame.Events != null && frame.Events.Count > 0) ||
                                     HasPlayerChanges(frame.PlayerUpdates) ||
                                     HasPlayerChanges(frame.OpponentUpdates) ||
                                     (frame.CardUpdates != null && frame.CardUpdates.Count > 0);

                    if (!hasContent) continue;

                    sb.AppendLine($"║ ── Frame {i} ──");

                    // Events
                    if (frame.Events != null)
                    {
                        foreach (var evt in frame.Events)
                        {
                            sb.AppendLine($"║   {FormatCombatEvent(evt)}");
                        }
                    }

                    // Player health/attribute changes
                    FormatPlayerUpdate(sb, "Player", frame.PlayerUpdates);
                    FormatPlayerUpdate(sb, "Opponent", frame.OpponentUpdates);

                    // Card updates
                    if (frame.CardUpdates != null)
                    {
                        foreach (var kvp in frame.CardUpdates)
                        {
                            FormatCardUpdate(sb, kvp.Key.ToString(), kvp.Value);
                        }
                    }
                }
            }

            sb.AppendLine($"╚══════════════════════════════════════════════════════════════");

            try
            {
                File.AppendAllText(LogFilePath, sb.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Failed to write combat log: {ex.Message}");
            }
        }

        private static bool HasPlayerChanges(CombatSimPlayerUpdate update)
        {
            if (update == null) return false;
            return update.IsPlayerDead ||
                   (update.HealthAdjustments != null && update.HealthAdjustments.Count > 0) ||
                   (update.Attributes != null && update.Attributes.Count > 0);
        }

        private static string FormatCombatEvent(ICombatSimEvent evt)
        {
            switch (evt)
            {
                case CombatSimEventEffectTriggered e:
                    var targets = e.Targets != null
                        ? string.Join(", ", e.Targets.Select(t => t?.ToString() ?? "?"))
                        : "none";
                    return $"[Triggered] Effect={e.EffectId}, Source={FormatInstanceId(e.Source)}, TriggerSrc={FormatInstanceId(e.TriggerSource)}, Targets=[{targets}]";

                case CombatSimEventEffectExecuted e:
                    return $"[Executed] Effect={e.EffectId}, Action={e.ActionType}, Source={FormatInstanceId(e.Source)}, Target={e.Target}";

                case CombatSimEventEffectAuraExecuted e:
                    return $"[Aura] Effect={e.EffectId}, Source={FormatInstanceId(e.Source)}, Applied={e.AppliedTo?.Count ?? 0}, Removed={e.RemovedFrom?.Count ?? 0}";

                case CombatSimEventCombatantDied e:
                    return $"[DIED] {e.CombatantId}";

                case CombatSimEventCardEnchanted e:
                    return $"[Enchanted] {CardDatabase.FormatId(e.InstanceId)}, Type={e.EnchantmentType}, Reverted={e.IsReverted}";

                case CombatSimEventCardTransformed e:
                    return $"[Transformed] Original={CardDatabase.FormatId(e.OriginalInstanceId)}, Context={e.ExecutionContextId}";

                case CombatSimEventCardTransformReverted e:
                    var ids = e.TransformedCardInstanceIds != null
                        ? string.Join(", ", e.TransformedCardInstanceIds.Select(CardDatabase.FormatId))
                        : "none";
                    return $"[TransformReverted] Reverted=[{ids}]";

                case CombatSimEventCardQuestCompleted e:
                    return $"[QuestComplete] {CardDatabase.FormatId(e.InstanceId)}, Group={e.QuestGroupIndex}, Entry={e.QuestEntryIndex}";

                case CombatSimEventCardQuestUpdated e:
                    return $"[QuestUpdate] {CardDatabase.FormatId(e.InstanceId)}, Progress={e.OldProgress}->{e.NewProgress}";

                case CombatSimEventMonsterGoldReceived e:
                    return $"[MonsterGold] HealthAmount={e.HealthAmount}";

                case CombatSimEventMonsterXpReceived e:
                    return $"[MonsterXP] HealthAmount={e.HealthAmount}";

                case CombatSimEventSandstormCountdownStarted _:
                    return $"[SandstormCountdown]";

                case CombatSimEventSandstormStarted _:
                    return $"[SANDSTORM]";

                default:
                    return $"[Unknown] {evt?.GetType().Name}";
            }
        }

        private static string FormatInstanceId(BazaarGameShared.Domain.Core.InstanceId? id)
        {
            if (!id.HasValue) return "?";
            return CardDatabase.FormatId(id.Value.ToString());
        }

        private static void FormatPlayerUpdate(StringBuilder sb, string label, CombatSimPlayerUpdate update)
        {
            if (update == null) return;

            if (update.HealthAdjustments != null && update.HealthAdjustments.Count > 0)
            {
                foreach (var adj in update.HealthAdjustments)
                {
                    var extra = "";
                    if (adj.IsCrit) extra += " CRIT";
                    if (adj.IsDamageReduced) extra += " REDUCED";
                    sb.AppendLine($"║   [{label}] {adj.AttributeChanged}: {adj.Amount} ({adj.DamageType}){extra}");
                }
            }

            if (update.Attributes != null)
            {
                foreach (var attr in update.Attributes)
                {
                    sb.AppendLine($"║   [{label}] {attr.Key}: {attr.Value.PreviousValue} -> {attr.Value.CurrentValue}");
                }
            }

            if (update.IsPlayerDead)
            {
                sb.AppendLine($"║   [{label}] ** DEAD **");
            }
        }

        private static void FormatCardUpdate(StringBuilder sb, string instanceId, CombatSimCardUpdate card)
        {
            if (card == null) return;

            var name = CardDatabase.FormatId(instanceId);
            var parts = new List<string>();

            if (card.State != null)
                parts.Add($"State={card.State.PreviousValue}->{card.State.CurrentValue}");

            if (card.Attributes != null)
            {
                foreach (var attr in card.Attributes)
                {
                    parts.Add($"{attr.Key}={attr.Value.PreviousValue}->{attr.Value.CurrentValue}");
                }
            }

            if (parts.Count > 0)
                sb.AppendLine($"║   [Card] {name}: {string.Join(", ", parts)}");
        }
    }
}
