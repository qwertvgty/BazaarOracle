using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BepInEx;

namespace BazaarEventLogger
{
    public static class EventLogger
    {
        public static string LogFilePath { get; private set; }
        private static int _messageCount;

        public static void Initialize()
        {
            LogFilePath = Path.Combine(Paths.BepInExRootPath, "GameSimEvents.log");
            var header = $"=== Bazaar Event Logger Started @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}";
            File.AppendAllText(LogFilePath, header);
            _messageCount = 0;
        }

        public static void LogGameSim(GameSim sim, string messageId)
        {
            _messageCount++;
            var sb = new StringBuilder();

            sb.AppendLine();
            sb.AppendLine($"╔══════════════════════════════════════════════════════════════");
            sb.AppendLine($"║ GameSim #{_messageCount} | MsgID: {messageId} | {DateTime.Now:HH:mm:ss.fff}");
            sb.AppendLine($"╠══════════════════════════════════════════════════════════════");

            // === Events ===
            if (sim.Events != null && sim.Events.Count > 0)
            {
                sb.AppendLine($"║ Events ({sim.Events.Count}):");
                foreach (var evt in sim.Events)
                {
                    sb.AppendLine($"║   {FormatEvent(evt)}");
                }
            }

            // === Player State ===
            if (sim.Player != null)
            {
                sb.AppendLine($"║ -- Player --");
                FormatPlayer(sb, sim.Player);
            }

            // === Opponent State ===
            if (sim.Opponent != null && sim.Opponent.Attributes != null && sim.Opponent.Attributes.Count > 0)
            {
                sb.AppendLine($"║ -- Opponent --");
                FormatPlayer(sb, sim.Opponent);
            }

            // === Cards ===
            if (sim.Cards != null && sim.Cards.Count > 0)
            {
                sb.AppendLine($"║ -- Cards ({sim.Cards.Count}) --");
                foreach (var kvp in sim.Cards)
                {
                    FormatCard(sb, kvp.Key, kvp.Value);
                }
            }

            // === Run ===
            if (sim.Run != null)
                FormatRun(sb, sim.Run);

            // === Current State ===
            if (sim.CurrentState != null)
                FormatRunState(sb, sim.CurrentState);

            sb.AppendLine($"╚══════════════════════════════════════════════════════════════");

            try
            {
                File.AppendAllText(LogFilePath, sb.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Failed to write log: {ex.Message}");
            }
        }

        // Helper: resolve instance/template ID to readable name
        private static string N(string id) => CardDatabase.FormatId(id);

        private static string FormatEvent(IGameSimEvent evt)
        {
            switch (evt)
            {
                case GameSimEventCardDealt e:
                    return $"[CardDealt] {N(e.TemplateId)} (Instance={e.InstanceId}, Type={e.Type})";

                case GameSimEventCardPurchased e:
                    return $"[CardPurchased] {N(e.InstanceId)}, Price={e.BuyPrice}, Section={e.Section}, Socket={e.LeftSocketId}, Who={e.CombatantId}";

                case GameSimEventCardSold e:
                    return $"[CardSold] {N(e.InstanceId)}, SellPrice={e.SellPrice}";

                case GameSimEventCardSpawned e:
                    return $"[CardSpawned] {N(e.TemplateId)} (Instance={e.InstanceId}, Type={e.Type}, Owner={e.CombatantId}, Section={e.Section})";

                case GameSimEventCardUpgraded e:
                    return $"[CardUpgraded] {N(e.InstanceId)}, NewTier={e.NewTier}";

                case GameSimEventCardMoved e:
                    return $"[CardMoved] {N(e.InstanceId)}, Owner={e.Owner}, To={e.ToInventory}/{e.ToSocket}";

                case GameSimEventCardDisposed e:
                    return $"[CardDisposed] {N(e.InstanceId)}, Reason={e.Reason}";

                case GameSimEventCardEnchanted e:
                    return $"[CardEnchanted] {N(e.InstanceId)}, Enchant={e.EnchantmentType}, Reverted={e.IsReverted}";

                case GameSimEventCardFused e:
                    return $"[CardFused] {N(e.SelectedInstanceId)} + {N(e.FusedInstanceId)} -> Tier={e.NewTier}, Enchant={e.EnchantmentType}";

                case GameSimEventCardTransformed e:
                    var transformed = e.TransformedCards != null
                        ? string.Join(", ", e.TransformedCards.Select(t => t?.ToString() ?? "?"))
                        : "none";
                    return $"[CardTransformed] {N(e.OriginalInstanceId)} -> [{transformed}]";

                case GameSimEventCardTransformReverted e:
                    var revertedIds = e.TransformedCardInstanceIds != null
                        ? string.Join(", ", e.TransformedCardInstanceIds.Select(N))
                        : "none";
                    return $"[CardTransformReverted] Original={e.OriginalCard}, Reverted=[{revertedIds}]";

                case GameSimEventCardQuestCompleted e:
                    return $"[QuestCompleted] {N(e.InstanceId)}, Group={e.QuestGroupIndex}, Entry={e.QuestEntryIndex}";

                case GameSimEventCardQuestUpdated e:
                    return $"[QuestUpdated] {N(e.InstanceId)}, Progress={e.OldProgress}->{e.NewProgress}";

                case GameSimEventEffectExecuted e:
                    return $"[EffectExecuted] Effect={e.EffectId}, Action={e.ActionType}, Source={e.Source}, Target={e.Target}";

                case GameSimEventEffectAuraExecuted e:
                    return $"[AuraExecuted] Effect={e.EffectId}, Source={e.Source}, Applied={e.AppliedTo?.Count ?? 0}, Removed={e.RemovedFrom?.Count ?? 0}";

                case GameSimEventPlayerInitialized e:
                    return $"[PlayerInit] {e.CombatantId}, Hero={e.Hero}";

                case GameSimEventPlayerExperienceGained e:
                    return $"[XPGained] +{e.Amount} ({e.ExperienceType}), Source={N(e.SourceInstanceId)}";

                case GameSimEventPlayerIncomeGained e:
                    return $"[IncomeGained] +{e.Amount}";

                case GameSimEventPlayerPrestigeChanged e:
                    return $"[PrestigeChanged] Delta={e.Delta}";

                case GameSimEventPlayerSkillEquipped e:
                    return $"[SkillEquipped] {N(e.InstanceId)}, Owner={e.Owner}";

                case GameSimEventRunDayChanged e:
                    return $"[DayChanged] Day={e.NewDay}";

                case GameSimEventRunHourChanged e:
                    return $"[HourChanged] Hour={e.NewHour}";

                case GameSimEventRunCompleted e:
                    return $"[RunCompleted] Rewards={e.Rewards}";

                case GameSimEventRunRerollCostChanged e:
                    return $"[RerollCostChanged] Cost={e.NewCost}";

                case GameSimEventStateTransitioned e:
                    return $"[StateTransitioned] -> {e.ToState}, Encounter={N(e.CurrentEncounterId)}";

                case GameSimEventStateSuspended e:
                    return $"[StateSuspended] -> {e.ToState}, Encounter={N(e.CurrentEncounterId)}";

                case GameSimEventStateResumed e:
                    return $"[StateResumed] -> {e.ToState}, Encounter={N(e.CurrentEncounterId)}";

                case GameSimEventInterruptStateEntered e:
                    return $"[InterruptEntered] {e.EnteredState}, IsInterrupt={e.IsInterrupt}";

                case GameSimEventInterruptStateExited e:
                    return $"[InterruptExited] {e.ExitedState}, IsInterrupt={e.IsInterrupt}";

                case GameSimEventPedestalActivated _:
                    return $"[PedestalActivated]";

                case GameSimEventSocketsUnlocked e:
                    var sockets = e.UnlockedSockets != null
                        ? string.Join(", ", e.UnlockedSockets)
                        : "none";
                    return $"[SocketsUnlocked] Section={e.Section}, Sockets=[{sockets}]";

                case GameSimEventBoardOverridden e:
                    return $"[BoardOverridden] Board={e.Board}, Presentation={e.Presentation}";

                default:
                    return $"[Unknown] {evt?.GetType().Name}: {evt}";
            }
        }

        private static void FormatPlayer(StringBuilder sb, SimUpdatePlayer player)
        {
            if (player.Attributes == null || player.Attributes.Count == 0) return;
            var attrs = string.Join(", ", player.Attributes.Select(a => $"{a.Key}={a.Value}"));
            sb.AppendLine($"║   [{player.CombatantId}] {attrs}");
        }

        private static void FormatCard(StringBuilder sb, string key, SimUpdateCard card)
        {
            if (card == null) return;

            var name = CardDatabase.FormatId(card.InstanceId);
            var parts = new List<string>();
            parts.Add(name);

            if (card.Tier.HasValue) parts.Add($"Tier={card.Tier}");
            parts.Add($"State={card.State}");
            if (card.Enchantment.HasValue) parts.Add($"Enchant={card.Enchantment}");
            if (card.Size.HasValue) parts.Add($"Size={card.Size}");

            if (card.Placement != null)
            {
                var p = card.Placement;
                if (p.Owner.HasValue) parts.Add($"Owner={p.Owner}");
                if (p.Section.HasValue) parts.Add($"Section={p.Section}");
                if (p.Socket.HasValue) parts.Add($"Socket={p.Socket}");
            }

            if (card.Attributes != null && card.Attributes.Count > 0)
            {
                var attrStr = string.Join(", ", card.Attributes
                    .Where(a => a.Value.DeltaType == EAttributeDeltaType.Update)
                    .Select(a => $"{a.Key}={a.Value.Value}"));
                if (!string.IsNullOrEmpty(attrStr))
                    parts.Add($"Attrs={{{attrStr}}}");
            }

            if (card.Tags != null && card.Tags.Count > 0)
                parts.Add($"Tags=[{string.Join(",", card.Tags)}]");

            sb.AppendLine($"║   {string.Join(", ", parts)}");
        }

        private static void FormatRun(StringBuilder sb, SimUpdateRun run)
        {
            sb.AppendLine($"║ -- Run --");
            sb.AppendLine($"║   Day={run.Day}, Hour={run.Hour}, Wins={run.Victories}, Losses={run.Defeats}, HourXP={run.CurrentHourXP}");
        }

        private static void FormatRunState(StringBuilder sb, SimUpdateRunState state)
        {
            sb.AppendLine($"║ -- State --");
            sb.AppendLine($"║   State={state.StateName}, Encounter={N(state.CurrentEncounterId)}, RerollCost={state.RerollCost}, RerollsLeft={state.RerollsRemaining}");

            if (state.SelectionSet != null && state.SelectionSet.Count > 0)
            {
                var items = state.SelectionSet.Select(N);
                sb.AppendLine($"║   SelectionSet=[{string.Join(", ", items)}]");
            }

            if (state.PvpOpponent != null)
            {
                var opp = state.PvpOpponent;
                sb.AppendLine($"║   PvpOpponent: {opp.Name} (Hero={opp.Hero}, Rating={opp.Rating}, Wins={opp.Victories})");
            }
        }
    }
}
