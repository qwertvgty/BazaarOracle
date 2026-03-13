using System;
using System.Collections.Generic;
using System.Linq;

namespace BazaarEventLogger
{
    public static class SimulationCoverageAnalyzer
    {
        private static readonly HashSet<string> SupportedTriggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            SimEffectTriggers.OnCardFired,
            SimEffectTriggers.OnCardCritted,
            SimEffectTriggers.OnItemUsed,
            SimEffectTriggers.OnPlayerRageGain,
            SimEffectTriggers.OnPlayerHealthLoss,
            SimEffectTriggers.OnPlayerEnraged,
            SimEffectTriggers.OnPlayerEnrageEnded,
            SimEffectTriggers.OnFightStarted,
            SimEffectTriggers.OnFightEnded,
            SimEffectTriggers.Passive
        };

        private static readonly HashSet<string> SupportedDirectEffects = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "damage",
            "heal",
            "shield_apply",
            "burn_apply",
            "poison_apply",
            "burn_remove",
            "poison_remove",
            "regen_apply",
            "modify_HealthRegen",
            "modify_HealthMax",
            "joy",
            "rage",
            "haste",
            "cooldown_charge",
            "ammo_reload",
            "slow",
            "freeze",
            "clear_freeze",
            "clear_slow"
        };

        private static readonly HashSet<string> SupportedCombatantAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Shield",
            "Burn",
            "Poison",
            "HealthRegen",
            "HealthMax",
            "Joy",
            "Rage",
            "RageMax",
            "Enraged",
            "EnragedDuration",
            "EnragedDurationMax"
        };

        private static readonly HashSet<string> SupportedCardAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DamageAmount",
            "ShieldApplyAmount",
            "HealAmount",
            "RegenApplyAmount",
            "BurnApplyAmount",
            "PoisonApplyAmount",
            "HasteAmount",
            "SlowAmount",
            "FreezeAmount",
            "ChargeAmount",
            "ReloadAmount",
            "AmmoMax",
            "Multicast",
            "Flying",
            "Cooldown",
            "CooldownMax",
            "FlatCooldownReduction",
            "CritChance",
            "PercentCooldownReduction",
            "PercentSlowReduction",
            "PercentFreezeReduction"
        };

        public static List<string> AnalyzeCombatant(SimCombatantSnapshot combatant)
        {
            if (combatant?.Cards == null || combatant.Cards.Count == 0)
                return new List<string>();

            return combatant.Cards
                .SelectMany(card => AnalyzeCard(card).Select(note => $"{card.Name}:{note}"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<string> AnalyzeCard(SimCardSnapshot card)
        {
            var notes = new List<string>();
            if (card == null)
                return notes;

            if (card.UnsupportedEffects != null)
            {
                foreach (var unsupported in card.UnsupportedEffects.Where(s => !string.IsNullOrWhiteSpace(s)))
                    notes.Add($"unsupported:{unsupported}");
            }

            if (card.Effects == null || card.Effects.Count == 0)
            {
                notes.Add("no_effects");
                return notes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            foreach (var effect in card.Effects)
            {
                if (effect == null)
                    continue;

                if (!string.IsNullOrWhiteSpace(effect.Trigger) && !SupportedTriggers.Contains(effect.Trigger))
                    notes.Add($"trigger:{effect.Trigger}");

                if (string.IsNullOrWhiteSpace(effect.Type))
                {
                    notes.Add("effect:missing_type");
                    continue;
                }

                if (SupportedDirectEffects.Contains(effect.Type))
                    continue;

                if (effect.Type.StartsWith("modify_", StringComparison.OrdinalIgnoreCase))
                {
                    var attrName = effect.Type.Substring("modify_".Length);
                    if (attrName.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!SupportedCombatantAttributes.Contains(attrName))
                        notes.Add($"modify:{attrName}");
                    continue;
                }

                if (effect.Type.StartsWith("buff_", StringComparison.OrdinalIgnoreCase))
                {
                    var attrName = effect.Type.Substring("buff_".Length);
                    if (attrName.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!SupportedCardAttributes.Contains(attrName))
                        notes.Add($"buff:{attrName}");
                    continue;
                }

                notes.Add($"effect:{effect.Type}");
            }

            return notes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
