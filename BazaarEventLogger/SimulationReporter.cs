using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BazaarEventLogger
{
    public static class SimulationReporter
    {
        public static string FormatPredictionReport(
            SimCombatantSnapshot player,
            IEnumerable<BatchSimulationResult> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("╔══════════════════════════════════════════════════════════════");
            sb.AppendLine($"║ BATTLE PREDICTIONS | Player HP={player.Health}/{player.HealthMax} | Cards={player.Cards.Count}");
            sb.AppendLine($"║ Confidence Baseline={ToPercent(player.CoverageScore)}");
            foreach (var card in player.Cards)
            {
                var effects = string.Join(", ", card.Effects.Where(e => !e.IsPassive).Select(e => $"{e.Type}={e.Value}"));
                var displayCooldown = GetDisplayCooldown(card);
                sb.AppendLine($"║   {card.Name} [{card.Tier}] CD={displayCooldown} x{card.Multicast} | {effects}");
            }
            sb.AppendLine("╠══════════════════════════════════════════════════════════════");

            foreach (var result in results)
            {
                sb.AppendLine($"║ {result.EncounterName} -> {result.MonsterName}");
                sb.AppendLine(
                    $"║   {result.Verdict} | WinRate={ToPercent(result.WinRate)} | AvgHP={result.AveragePlayerHealthRemaining:F1} | MedianTTK={result.MedianDurationSeconds:F1}s | Confidence={result.ConfidenceLabel}({ToPercent(result.CoverageScore)})");

                if (result.KeyThreats.Count > 0)
                    sb.AppendLine($"║   Threats: {string.Join("; ", result.KeyThreats)}");
                if (result.LossReasons.Count > 0)
                    sb.AppendLine($"║   Risks: {string.Join("; ", result.LossReasons)}");
                if (result.UnsupportedEffects.Count > 0)
                    sb.AppendLine($"║   Unsupported: {string.Join(", ", result.UnsupportedEffects.Take(6))}");
                if (result.CoverageGaps.Count > 0)
                    sb.AppendLine($"║   Coverage Gaps: {string.Join(", ", result.CoverageGaps.Take(6))}");
                sb.AppendLine("║");
            }

            sb.AppendLine("╚══════════════════════════════════════════════════════════════");
            return sb.ToString();
        }

        public static string FormatCoverageReport(
            SimCombatantSnapshot player,
            SimEncounterSnapshot encounter,
            BatchSimulationResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Coverage Report");
            sb.AppendLine($"Player gaps: {(player?.CoverageNotes?.Count ?? 0)}");
            foreach (var note in player?.CoverageNotes?.Take(20) ?? Enumerable.Empty<string>())
                sb.AppendLine($"- P {note}");

            if (encounter?.Opponent != null)
            {
                var opponent = encounter.Opponent;
                sb.AppendLine($"Opponent gaps: {(opponent.CoverageNotes?.Count ?? 0)}");
                foreach (var note in opponent.CoverageNotes?.Take(20) ?? Enumerable.Empty<string>())
                    sb.AppendLine($"- O {note}");
            }

            if (result?.CoverageGaps?.Count > 0)
            {
                sb.AppendLine("Encounter summary:");
                foreach (var note in result.CoverageGaps.Take(20))
                    sb.AppendLine($"- {note}");
            }

            return sb.ToString();
        }

        public static string FormatSimulationTrace(string encounterName, string monsterName, SingleSimulationResult sample)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("╔══════════════════════════════════════════════════════════════");
            sb.AppendLine($"║ SIM TRACE | {encounterName} -> {monsterName}");
            sb.AppendLine($"║ Winner={sample.Winner} | Duration={sample.DurationMs}ms | Ticks={sample.Trace.Count}");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════");

            foreach (var entry in sample.Trace)
            {
                sb.AppendLine($"║ T+{entry.TimeMs,6}ms | {entry.Summary}");
                sb.AppendLine($"║   {entry.PlayerState}");
                sb.AppendLine($"║   {entry.OpponentState}");
                foreach (var cardState in entry.CardStates)
                    sb.AppendLine($"║   [Card] {cardState}");
                foreach (var evt in entry.Events)
                    sb.AppendLine($"║   [Event] {evt}");
            }

            sb.AppendLine("╚══════════════════════════════════════════════════════════════");
            return sb.ToString();
        }

        private static string ToPercent(double value)
        {
            return $"{value * 100:F0}%";
        }

        private static string GetDisplayCooldown(SimCardSnapshot card)
        {
            if (card == null)
                return "0";

            var effective = GetEffectiveCooldown(card);
            if (effective == card.CooldownMax)
                return effective.ToString();

            return $"{effective} ({card.CooldownMax})";
        }

        private static int GetEffectiveCooldown(SimCardSnapshot card)
        {
            if (card == null)
                return 0;

            var flatReduction = GetAttribute(card, "FlatCooldownReduction");
            return System.Math.Max(250, card.CooldownMax + flatReduction);
        }

        private static int GetAttribute(SimCardSnapshot card, string key)
        {
            return card?.Attributes != null && card.Attributes.TryGetValue(key, out var value) ? value : 0;
        }
    }
}
