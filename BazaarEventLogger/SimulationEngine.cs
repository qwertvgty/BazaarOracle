using System;
using System.Collections.Generic;
using System.Linq;

namespace BazaarEventLogger
{
    public static class SimulationEngine
    {
        private const int TickMs = 50;
        private const int BurnTickMs = 500;
        private const int PoisonTickMs = 1000;
        private const int SandstormDamageStartMs = 75000;
        private const int MaxDurationMs = 120000;
        private const int MaxCastsPerTrigger = 32;

        public static BatchSimulationResult RunBatch(
            SimCombatantSnapshot playerSnapshot,
            SimEncounterSnapshot encounterSnapshot,
            SimulationBatchOptions options = null)
        {
            options = options ?? new SimulationBatchOptions();
            var result = new BatchSimulationResult
            {
                EncounterName = encounterSnapshot.EncounterName,
                MonsterName = encounterSnapshot.Opponent.Name,
                Runs = Math.Max(1, options.Runs),
                CoverageScore = Math.Min(playerSnapshot.CoverageScore, encounterSnapshot.Opponent.CoverageScore),
                UnsupportedEffects = playerSnapshot.UnsupportedEffects
                    .Concat(encounterSnapshot.Opponent.UnsupportedEffects)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList(),
                KeyThreats = BuildThreatSummary(encounterSnapshot.Opponent)
            };

            for (var i = 0; i < result.Runs; i++)
            {
                var seed = options.SeedBase + i;
                var captureTrace = i < Math.Max(0, options.TraceSamples);
                var sample = RunOnce(
                    CloneCombatant(playerSnapshot),
                    CloneCombatant(encounterSnapshot.Opponent),
                    seed,
                    captureTrace);
                result.Samples.Add(sample);
                if (sample.Winner == "Player")
                    result.Wins++;
                else
                    result.Losses++;
            }

            result.WinRate = result.Runs == 0 ? 0 : (double)result.Wins / result.Runs;
            result.AveragePlayerHealthRemaining = result.Samples.Average(s => s.PlayerHealthRemaining);
            result.AverageOpponentHealthRemaining = result.Samples.Average(s => s.OpponentHealthRemaining);
            result.MedianDurationSeconds = Median(result.Samples.Select(s => s.DurationMs / 1000.0));
            result.SandstormSeen = result.Samples.Any(s => s.SandstormTriggered);
            result.Verdict = BuildVerdict(result.WinRate, result.CoverageScore);
            result.ConfidenceLabel = BuildConfidenceLabel(result.CoverageScore);
            result.LossReasons = BuildLossReasons(result, encounterSnapshot.Opponent);
            return result;
        }

        public static SingleSimulationResult RunOnce(
            SimCombatantSnapshot player,
            SimCombatantSnapshot opponent,
            int seed,
            bool captureTrace = true)
        {
            var rng = new Random(seed);
            var timeMs = 0;
            var trace = captureTrace ? new List<SimulationTraceEntry>() : null;
            ApplyPassiveEffects(player, opponent, rng);
            ApplyPassiveEffects(opponent, player, rng);
            if (captureTrace)
                RecordTrace(trace, timeMs, player, opponent, "initial_state");

            while (player.Health > 0 && opponent.Health > 0 && timeMs < MaxDurationMs)
            {
                timeMs += TickMs;
                List<string> events = captureTrace ? new List<string>() : null;

                AdvanceEnrageState(player, opponent, rng, events, "Player");
                AdvanceEnrageState(opponent, player, rng, events, "Opponent");
                ProcessDot(player, opponent, rng, events, "Player");
                ProcessDot(opponent, player, rng, events, "Opponent");
                ProcessRegeneration(player, timeMs, events, "Player");
                ProcessRegeneration(opponent, timeMs, events, "Opponent");
                ProcessSandstorm(player, opponent, rng, timeMs, events);

                if (player.Health <= 0 || opponent.Health <= 0)
                {
                    events?.Add("combat_end:dot_or_sandstorm");
                    if (captureTrace)
                        RecordTrace(trace, timeMs, player, opponent, string.Join(" | ", events), events);
                    break;
                }

                ProcessCards(player, opponent, rng, events, "Player");
                ProcessCards(opponent, player, rng, events, "Opponent");
                if (captureTrace)
                    RecordTrace(trace, timeMs, player, opponent, events == null || events.Count == 0 ? "idle" : string.Join(" | ", events), events);
            }

            return new SingleSimulationResult
            {
                Winner = ResolveWinner(player, opponent),
                PlayerHealthRemaining = Math.Max(0, player.Health),
                OpponentHealthRemaining = Math.Max(0, opponent.Health),
                DurationMs = timeMs,
                SandstormTriggered = timeMs >= SandstormDamageStartMs,
                Trace = trace ?? new List<SimulationTraceEntry>()
            };
        }

        private static void ProcessCards(SimCombatantSnapshot owner, SimCombatantSnapshot target, Random rng, List<string> events, string ownerLabel)
        {
            foreach (var card in owner.Cards)
            {
                if (card.CooldownMax <= 0)
                    continue;

                AdvanceCardTimers(card);
                AdvanceCooldown(owner, card);
                if (card.CurrentCooldown > 0)
                    continue;

                events?.Add($"{ownerLabel}:trigger:{card.Name}");
                var castCount = Math.Max(1, Math.Min(card.Multicast, MaxCastsPerTrigger));
                if (card.Multicast > MaxCastsPerTrigger)
                    Plugin.Log?.LogWarning($"Clamped multicast for {ownerLabel}:{card.Name} from {card.Multicast} to {MaxCastsPerTrigger}");

                for (var i = 0; i < castCount; i++)
                    ExecuteCard(card, owner, target, rng, events, ownerLabel, i);

                TriggerItemUsedEffects(owner, target, card, rng, events, ownerLabel);

                card.CurrentCooldown = card.CooldownMax;
                events?.Add($"{ownerLabel}:reset_cd:{card.Name}={card.CooldownMax}");
            }
        }

        private static void ExecuteCard(
            SimCardSnapshot card,
            SimCombatantSnapshot owner,
            SimCombatantSnapshot target,
            Random rng,
            List<string> events,
            string ownerLabel,
            int multicastIndex)
        {
            foreach (var effect in card.Effects.Where(e => !e.IsPassive && e.Trigger == SimEffectTriggers.OnCardFired).ToList())
            {
                if (!ShouldActivateEffect(effect, owner, card))
                    continue;

                ApplyEffect(effect, owner, target, rng, card, events, ownerLabel, multicastIndex);
            }
        }

        private static void ApplyPassiveEffects(SimCombatantSnapshot owner, SimCombatantSnapshot target, Random rng)
        {
            foreach (var card in owner.Cards)
            {
                foreach (var effect in card.Effects.Where(e => e.IsPassive && e.Trigger == SimEffectTriggers.Passive).ToList())
                {
                    if (!ShouldActivateEffect(effect, owner, card))
                        continue;

                    ApplyEffect(effect, owner, target, rng, card);
                }
            }
        }

        private static void ApplyEffect(
            SimEffectSpec effect,
            SimCombatantSnapshot owner,
            SimCombatantSnapshot target,
            Random rng,
            SimCardSnapshot sourceCard,
            List<string> events = null,
            string ownerLabel = null,
            int multicastIndex = 0)
        {
            var targetCombatants = ResolveCombatantTargets(owner, target, effect.Target);
            var effectLabel = $"{ownerLabel ?? owner.Name}:{sourceCard?.Name ?? "passive"}:{effect.Type}={effect.Value}->{effect.Target}";
            if (multicastIndex > 0)
                effectLabel += $"#cast{multicastIndex + 1}";
            switch (effect.Type)
            {
                case "damage":
                    foreach (var combatant in targetCombatants)
                    {
                        ApplyDamage(combatant, effect.Value);
                        TriggerHealthLossEffectsIfNeeded(combatant, ReferenceEquals(combatant, owner) ? target : owner, rng, events, combatant.Name);
                        events?.Add($"{effectLabel}:{combatant.Name}:hp={combatant.Health}:shield={combatant.Shield}");
                    }
                    break;

                case "heal":
                    foreach (var combatant in targetCombatants)
                    {
                        combatant.Health = Math.Min(combatant.HealthMax, combatant.Health + effect.Value);
                        CleanseOnHeal(combatant, effect.Value);
                        events?.Add($"{effectLabel}:{combatant.Name}:hp={combatant.Health}:burn={combatant.Burn}:poison={combatant.Poison}");
                    }
                    break;

                case "shield":
                case "shield_apply":
                    foreach (var combatant in targetCombatants)
                    {
                        combatant.Shield += effect.Value;
                        events?.Add($"{effectLabel}:{combatant.Name}:shield={combatant.Shield}");
                    }
                    break;

                case "burn":
                case "burn_apply":
                    foreach (var combatant in targetCombatants)
                    {
                        combatant.Burn += effect.Value;
                        events?.Add($"{effectLabel}:{combatant.Name}:burn={combatant.Burn}");
                    }
                    break;

                case "poison":
                case "poison_apply":
                    foreach (var combatant in targetCombatants)
                    {
                        combatant.Poison += effect.Value;
                        events?.Add($"{effectLabel}:{combatant.Name}:poison={combatant.Poison}");
                    }
                    break;

                case "burn_remove":
                    foreach (var combatant in targetCombatants)
                    {
                        var removeAmount = effect.Value > 0 ? effect.Value : (int)Math.Round(combatant.Burn * 0.5, MidpointRounding.AwayFromZero);
                        combatant.Burn = Math.Max(0, combatant.Burn - removeAmount);
                        events?.Add($"{effectLabel}:{combatant.Name}:burn={combatant.Burn}");
                    }
                    break;

                case "poison_remove":
                    foreach (var combatant in targetCombatants)
                    {
                        var removeAmount = effect.Value > 0 ? effect.Value : (int)Math.Round(combatant.Poison * 0.5, MidpointRounding.AwayFromZero);
                        combatant.Poison = Math.Max(0, combatant.Poison - removeAmount);
                        events?.Add($"{effectLabel}:{combatant.Name}:poison={combatant.Poison}");
                    }
                    break;

                case "regen_apply":
                case "modify_HealthRegen":
                    foreach (var combatant in targetCombatants)
                    {
                        combatant.HealthRegen += effect.Value;
                        events?.Add($"{effectLabel}:{combatant.Name}:regen={combatant.HealthRegen}");
                    }
                    break;

                case "modify_HealthMax":
                    foreach (var combatant in targetCombatants)
                    {
                        combatant.HealthMax = Math.Max(1, combatant.HealthMax + effect.Value);
                        combatant.Health = Math.Min(combatant.HealthMax, combatant.Health + effect.Value);
                        events?.Add($"{effectLabel}:{combatant.Name}:hp={combatant.Health}/{combatant.HealthMax}");
                    }
                    break;

                case "joy":
                    foreach (var combatant in targetCombatants)
                    {
                        combatant.Joy += effect.Value;
                        events?.Add($"{effectLabel}:{combatant.Name}:joy={combatant.Joy}");
                    }
                    break;

                case "rage":
                    foreach (var combatant in targetCombatants)
                    {
                        var otherCombatant = ReferenceEquals(combatant, owner) ? target : owner;
                        ApplyRage(combatant, otherCombatant, effect.Value, rng, events, ownerLabel ?? owner.Name, effectLabel);
                    }
                    break;

                case "haste":
                    ApplyStatusDuration(ResolveCardTargets(owner, target, sourceCard, effect.Target), effect.Value, isHaste: true, rng: rng, scope: effect.Target);
                    events?.Add(effectLabel);
                    break;

                case "cooldown_charge":
                    ModifyCardCooldown(ResolveCardTargets(owner, target, sourceCard, effect.Target), -effect.Value, rng, effect.Target);
                    events?.Add(effectLabel);
                    break;

                case "slow":
                    ApplyStatusDuration(ResolveCardTargets(owner, target, sourceCard, effect.Target), effect.Value, isHaste: false, rng: rng, scope: effect.Target);
                    events?.Add(effectLabel);
                    break;

                case "freeze":
                    FreezeRandomCard(ResolveCardTargets(owner, target, sourceCard, effect.Target), effect.Value, rng, effect.Target);
                    events?.Add(effectLabel);
                    break;

                case "clear_freeze":
                    ClearCardStatus(ResolveCardTargets(owner, target, sourceCard, effect.Target), clearFreeze: true, clearSlow: false);
                    events?.Add(effectLabel);
                    break;

                case "clear_slow":
                    ClearCardStatus(ResolveCardTargets(owner, target, sourceCard, effect.Target), clearFreeze: false, clearSlow: true);
                    events?.Add(effectLabel);
                    break;

                default:
                    if (effect.Type.StartsWith("buff_", StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyCardAttributeBuff(owner, target, sourceCard, effect, rng);
                        events?.Add(effectLabel);
                    }
                    else if (effect.Type.StartsWith("modify_", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var combatant in targetCombatants)
                        {
                            ApplyCombatantAttributeModification(combatant, effect.Type.Substring("modify_".Length), effect.Value);
                            events?.Add($"{effectLabel}:{combatant.Name}");
                        }
                    }
                    break;
            }
        }

        private static void ApplyCardAttributeBuff(
            SimCombatantSnapshot owner,
            SimCombatantSnapshot target,
            SimCardSnapshot sourceCard,
            SimEffectSpec effect,
            Random rng)
        {
            var attrName = effect.Type.Substring("buff_".Length);
            var selected = ResolveCardTargets(owner, target, sourceCard, effect.Target);
            if (selected.Count == 0)
                return;

            foreach (var card in SelectBuffTargets(selected, attrName, rng, effect.Target))
            {
                card.Attributes[attrName] = card.Attributes.TryGetValue(attrName, out var current)
                    ? current + effect.Value
                    : effect.Value;

                switch (attrName)
                {
                    case "DamageAmount":
                        AdjustEffectValue(card, "damage", effect.Value);
                        break;
                    case "ShieldApplyAmount":
                        AdjustEffectValue(card, "shield_apply", effect.Value);
                        break;
                    case "HealAmount":
                        AdjustEffectValue(card, "heal", effect.Value);
                        break;
                    case "BurnApplyAmount":
                        AdjustEffectValue(card, "burn_apply", effect.Value);
                        break;
                    case "PoisonApplyAmount":
                        AdjustEffectValue(card, "poison_apply", effect.Value);
                        break;
                    case "HasteAmount":
                        AdjustEffectValue(card, "haste", effect.Value);
                        break;
                    case "SlowAmount":
                        AdjustEffectValue(card, "slow", effect.Value);
                        break;
                    case "FreezeAmount":
                        AdjustEffectValue(card, "freeze", effect.Value);
                        break;
                    case "ChargeAmount":
                        AdjustEffectValue(card, "cooldown_charge", effect.Value);
                        break;
                    case "Multicast":
                        card.Multicast = Math.Max(1, card.Multicast + effect.Value);
                        break;
                    case "Flying":
                        var flying = card.Attributes.TryGetValue("Flying", out var currentFlying) ? currentFlying : 0;
                        card.Attributes["Flying"] = Math.Max(0, flying + effect.Value);
                        break;
                    case "Cooldown":
                    case "CooldownMax":
                        card.CooldownMax = Math.Max(250, card.CooldownMax - effect.Value);
                        card.CurrentCooldown = Math.Min(card.CurrentCooldown, card.CooldownMax);
                        break;
                }
            }
        }

        private static bool ShouldActivateEffect(SimEffectSpec effect, SimCombatantSnapshot owner, SimCardSnapshot sourceCard)
        {
            if (effect == null)
                return false;

            if (effect.RequiresOwnerEnraged && !owner.IsEnraged)
                return false;

            if (effect.RequiresOwnerNotEnraged && owner.IsEnraged)
                return false;

            if (!string.IsNullOrEmpty(effect.RequiresSourceAttributeZero))
            {
                var currentValue = sourceCard != null && sourceCard.Attributes.TryGetValue(effect.RequiresSourceAttributeZero, out var attrValue)
                    ? attrValue
                    : 0;
                if (currentValue != 0)
                    return false;
            }

            if (effect.RequiresOwnerHealthBelowRatio.HasValue)
            {
                var threshold = owner.HealthMax * effect.RequiresOwnerHealthBelowRatio.Value;
                if (!(owner.Health < threshold))
                    return false;
            }

            return true;
        }

        private static void ApplyRage(
            SimCombatantSnapshot combatant,
            SimCombatantSnapshot opponent,
            int delta,
            Random rng,
            List<string> events,
            string ownerLabel,
            string effectLabel)
        {
            if (delta > 0 && combatant.IsEnraged)
            {
                events?.Add($"{effectLabel}:{combatant.Name}:rage_blocked_enraged");
                return;
            }

            var maxRage = combatant.RageMax == 0 ? int.MaxValue : combatant.RageMax;
            combatant.Rage = Math.Max(0, Math.Min(maxRage, combatant.Rage + delta));
            events?.Add($"{effectLabel}:{combatant.Name}:rage={combatant.Rage}/{combatant.RageMax}");

            if (!combatant.IsEnraged && combatant.RageMax > 0 && combatant.Rage >= combatant.RageMax)
                EnterEnrage(combatant, opponent, rng, events, ownerLabel);
        }

        private static void AdvanceEnrageState(
            SimCombatantSnapshot combatant,
            SimCombatantSnapshot opponent,
            Random rng,
            List<string> events,
            string ownerLabel)
        {
            if (!combatant.IsEnraged)
                return;

            combatant.EnragedDuration = Math.Max(0, combatant.EnragedDuration - TickMs);
            events?.Add($"{ownerLabel}:enraged_tick:{combatant.EnragedDuration}");
            if (combatant.EnragedDuration == 0)
                EndEnrage(combatant, opponent, rng, events, ownerLabel);
        }

        private static void EnterEnrage(
            SimCombatantSnapshot combatant,
            SimCombatantSnapshot opponent,
            Random rng,
            List<string> events,
            string ownerLabel)
        {
            if (combatant.IsEnraged)
                return;

            combatant.IsEnraged = true;
            combatant.Rage = Math.Max(combatant.Rage, combatant.RageMax);
            combatant.EnragedDuration = Math.Max(0, combatant.EnragedDurationMax);
            ClearCombatantItemTempoDebuffs(combatant);
            events?.Add($"{ownerLabel}:enraged_start:{combatant.EnragedDuration}");
            TriggerEffects(combatant, opponent, rng, SimEffectTriggers.OnPlayerEnraged, events, ownerLabel);
        }

        private static void EndEnrage(
            SimCombatantSnapshot combatant,
            SimCombatantSnapshot opponent,
            Random rng,
            List<string> events,
            string ownerLabel)
        {
            if (!combatant.IsEnraged)
                return;

            combatant.IsEnraged = false;
            combatant.EnragedDuration = 0;
            combatant.Rage = 0;
            events?.Add($"{ownerLabel}:enraged_end");
            TriggerEffects(combatant, opponent, rng, SimEffectTriggers.OnPlayerEnrageEnded, events, ownerLabel);
        }

        private static void TriggerEffects(
            SimCombatantSnapshot owner,
            SimCombatantSnapshot target,
            Random rng,
            string trigger,
            List<string> events,
            string ownerLabel)
        {
            foreach (var card in owner.Cards)
            {
                foreach (var effect in card.Effects.Where(e => !e.IsPassive && e.Trigger == trigger).ToList())
                {
                    if (!ShouldActivateEffect(effect, owner, card))
                        continue;

                    ApplyEffect(effect, owner, target, rng, card, events, ownerLabel);
                }
            }
        }

        private static void TriggerItemUsedEffects(
            SimCombatantSnapshot owner,
            SimCombatantSnapshot target,
            SimCardSnapshot usedCard,
            Random rng,
            List<string> events,
            string ownerLabel)
        {
            foreach (var card in owner.Cards)
            {
                foreach (var effect in card.Effects.Where(e => !e.IsPassive && e.Trigger == SimEffectTriggers.OnItemUsed).ToList())
                {
                    if (!ShouldActivateEffect(effect, owner, card) || !MatchesUsedCardTrigger(effect, usedCard))
                        continue;

                    ApplyEffect(effect, owner, target, rng, card, events, ownerLabel);
                }
            }
        }

        private static void TriggerHealthLossEffectsIfNeeded(
            SimCombatantSnapshot damaged,
            SimCombatantSnapshot opponent,
            Random rng,
            List<string> events,
            string ownerLabel)
        {
            if (damaged.Health <= 0)
                return;

            foreach (var card in damaged.Cards)
            {
                foreach (var effect in card.Effects.Where(e => !e.IsPassive && e.Trigger == SimEffectTriggers.OnPlayerHealthLoss).ToList())
                {
                    if (!ShouldActivateEffect(effect, damaged, card))
                        continue;

                    ApplyEffect(effect, damaged, opponent, rng, card, events, ownerLabel);
                }
            }
        }

        private static bool MatchesUsedCardTrigger(SimEffectSpec effect, SimCardSnapshot usedCard)
        {
            if (effect.TriggerCardSizes == null || effect.TriggerCardSizes.Count == 0)
                return true;

            return effect.TriggerCardSizes.Any(size => string.Equals(size, usedCard.Size, StringComparison.OrdinalIgnoreCase));
        }

        private static void ClearCombatantItemTempoDebuffs(SimCombatantSnapshot combatant)
        {
            foreach (var card in combatant.Cards)
            {
                card.SlowDuration = 0;
                card.Freeze = 0;
            }
        }

        private static void ClearCardStatus(IEnumerable<SimCardSnapshot> cards, bool clearFreeze, bool clearSlow)
        {
            foreach (var card in cards)
            {
                if (clearFreeze)
                    card.Freeze = 0;
                if (clearSlow)
                    card.SlowDuration = 0;
            }
        }

        private static List<SimCardSnapshot> SelectBuffTargets(IList<SimCardSnapshot> cards, string attrName, Random rng, string scope)
        {
            if (cards == null || cards.Count == 0)
                return new List<SimCardSnapshot>();

            if (IsMultiTargetScope(scope))
                return cards.ToList();

            var mappedEffectType = GetEffectTypeForAttribute(attrName);
            var prioritized = cards
                .Where(card => card.CooldownMax > 0 &&
                               (mappedEffectType == null || card.Effects.Any(effect => effect.Type == mappedEffectType)))
                .ToList();

            var pool = prioritized.Count > 0 ? prioritized : cards.Where(card => card.CooldownMax > 0).ToList();
            if (pool.Count == 0)
                return new List<SimCardSnapshot>();

            return new List<SimCardSnapshot> { pool[rng.Next(pool.Count)] };
        }

        private static string GetEffectTypeForAttribute(string attrName)
        {
            switch (attrName)
            {
                case "DamageAmount": return "damage";
                case "ShieldApplyAmount": return "shield_apply";
                case "HealAmount": return "heal";
                case "BurnApplyAmount": return "burn_apply";
                case "PoisonApplyAmount": return "poison_apply";
                case "HasteAmount": return "haste";
                case "SlowAmount": return "slow";
                case "FreezeAmount": return "freeze";
                default: return null;
            }
        }

        private static void AdjustEffectValue(SimCardSnapshot card, string effectType, int delta)
        {
            var existing = card.Effects.FirstOrDefault(effect => effect.Type == effectType);
            if (existing != null)
            {
                existing.Value = Math.Max(0, existing.Value + delta);
                return;
            }

            if (delta <= 0)
                return;

            card.Effects.Add(new SimEffectSpec
            {
                Type = effectType,
                Value = delta,
                Target = effectType == "heal" || effectType == "shield_apply" ? "self" : "opponent",
                Source = "runtime_buff"
            });
        }

        private static void AdvanceCardTimers(SimCardSnapshot card)
        {
            if (card.HasteDuration > 0)
                card.HasteDuration = Math.Max(0, card.HasteDuration - TickMs);
            if (card.SlowDuration > 0)
                card.SlowDuration = Math.Max(0, card.SlowDuration - TickMs);
            if (card.Freeze > 0)
                card.Freeze = Math.Max(0, card.Freeze - TickMs);
        }

        private static void AdvanceCooldown(SimCombatantSnapshot owner, SimCardSnapshot card)
        {
            if (card.Freeze > 0)
                return;

            var reduction = TickMs;
            var hasted = card.HasteDuration > 0;
            var slowed = card.SlowDuration > 0;

            if (hasted && !slowed)
                reduction = TickMs * 2;
            else if (slowed && !hasted)
                reduction = TickMs / 2;

            var cooldownReduction = GetCooldownReductionPercent(owner, card);
            if (cooldownReduction > 0 && cooldownReduction < 100)
                reduction = (int)Math.Ceiling(reduction * 100.0 / (100 - cooldownReduction));

            card.CurrentCooldown = Math.Max(0, card.CurrentCooldown - reduction);
        }

        private static void ModifyCardCooldown(IList<SimCardSnapshot> cards, int delta, Random rng, string scope)
        {
            var candidates = cards.Where(c => c.CooldownMax > 0).ToList();
            if (candidates.Count == 0)
                return;

            if (IsMultiTargetScope(scope))
            {
                foreach (var card in candidates)
                    card.CurrentCooldown = Math.Max(0, card.CurrentCooldown + delta);
                return;
            }

            var selected = candidates[rng.Next(candidates.Count)];
            selected.CurrentCooldown = Math.Max(0, selected.CurrentCooldown + delta);
        }

        private static void FreezeRandomCard(IList<SimCardSnapshot> cards, int duration, Random rng, string scope)
        {
            var candidates = cards.Where(c => c.CooldownMax > 0).ToList();
            if (candidates.Count == 0)
                return;

            if (IsMultiTargetScope(scope))
            {
                foreach (var card in candidates)
                    card.Freeze = Math.Max(card.Freeze, GetAdjustedStatusDuration(card, duration, isFreeze: true));
                return;
            }

            var selected = candidates[rng.Next(candidates.Count)];
            selected.Freeze = Math.Max(selected.Freeze, GetAdjustedStatusDuration(selected, duration, isFreeze: true));
        }

        private static void ApplyStatusDuration(IList<SimCardSnapshot> cards, int duration, bool isHaste, Random rng, string scope)
        {
            var candidates = cards.Where(c => c.CooldownMax > 0).ToList();
            if (candidates.Count == 0 || duration <= 0)
                return;

            if (IsMultiTargetScope(scope))
            {
                foreach (var card in candidates)
                    ApplyStatusDuration(card, duration, isHaste);
                return;
            }

            ApplyStatusDuration(candidates[rng.Next(candidates.Count)], duration, isHaste);
        }

        private static void ProcessDot(SimCombatantSnapshot target, SimCombatantSnapshot opponent, Random rng, List<string> events, string label)
        {
            target.BurnTickProgress += TickMs;
            while (target.Burn > 0 && target.BurnTickProgress >= BurnTickMs)
            {
                ApplyDamage(target, target.Burn);
                TriggerHealthLossEffectsIfNeeded(target, opponent, rng, events, label);
                events?.Add($"{label}:burn_tick:{target.Burn}:hp={target.Health}:shield={target.Shield}");
                target.Burn -= Math.Max(1, (int)Math.Floor(target.Burn * 0.03));
                target.Burn = Math.Max(0, target.Burn);
                target.BurnTickProgress -= BurnTickMs;
                events?.Add($"{label}:burn_decay:{target.Burn}");
            }

            target.PoisonTickProgress += TickMs;
            while (target.Poison > 0 && target.PoisonTickProgress >= PoisonTickMs)
            {
                ApplyDamage(target, target.Poison);
                TriggerHealthLossEffectsIfNeeded(target, opponent, rng, events, label);
                events?.Add($"{label}:poison_tick:{target.Poison}:hp={target.Health}:shield={target.Shield}");
                target.PoisonTickProgress -= PoisonTickMs;
            }
        }

        private static void ProcessRegeneration(SimCombatantSnapshot target, int timeMs, List<string> events, string label)
        {
            if (target.HealthRegen > 0 && timeMs % 1000 == 0)
            {
                target.Health = Math.Min(target.HealthMax, target.Health + target.HealthRegen);
                events?.Add($"{label}:regen_tick:{target.HealthRegen}:hp={target.Health}/{target.HealthMax}");
            }
        }

        private static void ProcessSandstorm(SimCombatantSnapshot player, SimCombatantSnapshot opponent, Random rng, int timeMs, List<string> events)
        {
            if (timeMs < SandstormDamageStartMs || timeMs % 1000 != 0)
                return;

            var sandstormDamage = ((timeMs - SandstormDamageStartMs) / 1000 + 1) * 50;
            ApplyDamage(player, sandstormDamage);
            TriggerHealthLossEffectsIfNeeded(player, opponent, rng, events, "Player");
            ApplyDamage(opponent, sandstormDamage);
            TriggerHealthLossEffectsIfNeeded(opponent, player, rng, events, "Opponent");
            events?.Add($"sandstorm:{sandstormDamage}:player_hp={player.Health}:opponent_hp={opponent.Health}");
        }

        private static void ApplyDamage(SimCombatantSnapshot target, int damage)
        {
            if (damage <= 0)
                return;

            if (target.Shield > 0)
            {
                var absorbed = Math.Min(target.Shield, damage);
                target.Shield -= absorbed;
                damage -= absorbed;
            }

            if (damage > 0)
                target.Health -= damage;
        }

        private static void CleanseOnHeal(SimCombatantSnapshot target, int healAmount)
        {
            if (healAmount <= 0)
                return;

            var cleanse = (int)Math.Floor(healAmount * 0.05);
            if (cleanse <= 0)
                return;

            target.Burn = Math.Max(0, target.Burn - cleanse);
            target.Poison = Math.Max(0, target.Poison - cleanse);
        }

        private static string ResolveWinner(SimCombatantSnapshot player, SimCombatantSnapshot opponent)
        {
            if (player.Health > 0 && opponent.Health <= 0)
                return "Player";
            if (opponent.Health > 0 && player.Health <= 0)
                return "Opponent";

            return player.Health >= opponent.Health ? "Player" : "Opponent";
        }

        private static SimCombatantSnapshot CloneCombatant(SimCombatantSnapshot source)
        {
            return new SimCombatantSnapshot
            {
                Name = source.Name,
                SourceId = source.SourceId,
                Health = source.Health,
                HealthMax = source.HealthMax,
                Shield = source.Shield,
                Burn = source.Burn,
                Poison = source.Poison,
                HealthRegen = source.HealthRegen,
                Joy = source.Joy,
                Rage = source.Rage,
                RageMax = source.RageMax,
                EnragedDurationMax = source.EnragedDurationMax,
                EnragedDuration = source.EnragedDuration,
                IsEnraged = source.IsEnraged,
                BurnTickProgress = source.BurnTickProgress,
                PoisonTickProgress = source.PoisonTickProgress,
                UnsupportedEffects = source.UnsupportedEffects.ToList(),
                Cards = source.Cards.Select(card => new SimCardSnapshot
                {
                    Name = card.Name,
                    InstanceId = card.InstanceId,
                    TemplateId = card.TemplateId,
                    Tier = card.Tier,
                    Size = card.Size,
                    CooldownMax = card.CooldownMax,
                    CurrentCooldown = card.CooldownMax,
                    Multicast = card.Multicast,
                    Freeze = 0,
                    HasteDuration = 0,
                    SlowDuration = 0,
                    Attributes = new Dictionary<string, int>(card.Attributes, StringComparer.OrdinalIgnoreCase),
                    CoverageScore = card.CoverageScore,
                    UnsupportedEffects = card.UnsupportedEffects.ToList(),
                    Effects = card.Effects.Select(effect => new SimEffectSpec
                    {
                        Type = effect.Type,
                        Value = effect.Value,
                        Target = effect.Target,
                        IsPassive = effect.IsPassive,
                        Source = effect.Source,
                        Trigger = effect.Trigger,
                        RequiresOwnerEnraged = effect.RequiresOwnerEnraged,
                        RequiresOwnerNotEnraged = effect.RequiresOwnerNotEnraged,
                        TriggerCardSizes = effect.TriggerCardSizes.ToList(),
                        RequiresSourceAttributeZero = effect.RequiresSourceAttributeZero,
                        RequiresOwnerHealthBelowRatio = effect.RequiresOwnerHealthBelowRatio
                    }).ToList()
                }).ToList()
            };
        }

        private static double Median(IEnumerable<double> values)
        {
            var ordered = values.OrderBy(v => v).ToList();
            if (ordered.Count == 0)
                return 0;

            var mid = ordered.Count / 2;
            return ordered.Count % 2 == 0
                ? (ordered[mid - 1] + ordered[mid]) / 2.0
                : ordered[mid];
        }

        private static string BuildVerdict(double winRate, double coverageScore)
        {
            if (coverageScore < 0.4)
                return "低可信度";
            if (winRate >= 0.8)
                return "稳胜";
            if (winRate >= 0.6)
                return "偏优";
            if (winRate >= 0.4)
                return "五五开";
            if (winRate >= 0.2)
                return "偏劣";
            return "稳输";
        }

        private static string BuildConfidenceLabel(double coverageScore)
        {
            if (coverageScore >= 0.85)
                return "高";
            if (coverageScore >= 0.6)
                return "中";
            return "低";
        }

        private static List<string> BuildThreatSummary(SimCombatantSnapshot opponent)
        {
            return opponent.Cards
                .SelectMany(card => card.Effects.Where(effect => !effect.IsPassive)
                    .Select(effect => new { card.Name, effect.Type, effect.Value }))
                .OrderByDescending(item => item.Value)
                .Take(4)
                .Select(item => $"{item.Name}:{item.Type}={item.Value}")
                .ToList();
        }

        private static List<string> BuildLossReasons(BatchSimulationResult result, SimCombatantSnapshot opponent)
        {
            var reasons = new List<string>();
            if (result.WinRate < 0.5)
            {
                if (opponent.Cards.SelectMany(c => c.Effects).Any(e => e.Type == "freeze" || e.Type == "slow"))
                    reasons.Add("对手有节奏干扰");
                if (opponent.Cards.SelectMany(c => c.Effects).Any(e => e.Type == "burn_apply"))
                    reasons.Add("对手灼烧压力高");
                if (opponent.Cards.SelectMany(c => c.Effects).Any(e => e.Type == "poison_apply"))
                    reasons.Add("对手持续中毒压力高");
                if (opponent.Cards.SelectMany(c => c.Effects).Any(e => e.Type == "damage" && e.Value >= 20))
                    reasons.Add("对手爆发伤害高");
            }

            if (result.CoverageScore < 0.6)
                reasons.Add("存在未覆盖机制，结果需人工复核");

            return reasons.Distinct().ToList();
        }

        private static void ApplyStatusDuration(SimCardSnapshot card, int duration, bool isHaste)
        {
            if (isHaste)
                card.HasteDuration = Math.Max(card.HasteDuration, duration);
            else
                card.SlowDuration = Math.Max(card.SlowDuration, GetAdjustedStatusDuration(card, duration, isFreeze: false));
        }

        private static int GetAdjustedStatusDuration(SimCardSnapshot card, int duration, bool isFreeze)
        {
            if (duration <= 0)
                return 0;

            var reduction = GetAttribute(card, isFreeze ? "PercentFreezeReduction" : "PercentSlowReduction");
            if (HasAttribute(card, "Flying"))
                reduction += 50;

            reduction = Math.Max(0, Math.Min(100, reduction));
            return (int)Math.Ceiling(duration * (100 - reduction) / 100.0);
        }

        private static bool HasAttribute(SimCardSnapshot card, string attrName)
        {
            return GetAttribute(card, attrName) > 0;
        }

        private static int GetCooldownReductionPercent(SimCombatantSnapshot owner, SimCardSnapshot card)
        {
            var reduction = GetAttribute(card, "PercentCooldownReduction");
            if (owner.IsEnraged)
                reduction = Math.Max(reduction, 10);

            return Math.Max(0, Math.Min(99, reduction));
        }

        private static int GetAttribute(SimCardSnapshot card, string attrName)
        {
            return card.Attributes.TryGetValue(attrName, out var value) ? value : 0;
        }

        private static List<SimCombatantSnapshot> ResolveCombatantTargets(
            SimCombatantSnapshot owner,
            SimCombatantSnapshot opponent,
            string targetMode)
        {
            switch (targetMode)
            {
                case "self":
                    return new List<SimCombatantSnapshot> { owner };
                case "all":
                    return new List<SimCombatantSnapshot> { owner, opponent };
                case "opponent":
                default:
                    return new List<SimCombatantSnapshot> { opponent };
            }
        }

        private static List<SimCardSnapshot> ResolveCardTargets(
            SimCombatantSnapshot owner,
            SimCombatantSnapshot opponent,
            SimCardSnapshot sourceCard,
            string targetMode)
        {
            IList<SimCardSnapshot> pool;
            switch (targetMode)
            {
                case "self_card":
                case "all_self_cards":
                case "adjacent_self_cards":
                    pool = owner.Cards;
                    break;
                case "opponent_card":
                case "all_opponent_cards":
                case "adjacent_opponent_cards":
                    pool = opponent.Cards;
                    break;
                default:
                    pool = targetMode != null && targetMode.Contains("opponent", StringComparison.OrdinalIgnoreCase)
                        ? opponent.Cards
                        : owner.Cards;
                    break;
            }

            if (targetMode == "self_card" && sourceCard != null && pool.Contains(sourceCard))
                return new List<SimCardSnapshot> { sourceCard };

            if (targetMode == "adjacent_self_cards" || targetMode == "adjacent_opponent_cards")
                return ResolveAdjacentCards(pool, sourceCard);

            if ((targetMode == "all_self_cards" || targetMode == "all_opponent_cards") && sourceCard != null && pool.Contains(sourceCard))
                return pool.Where(card => card != sourceCard).ToList();

            return pool.ToList();
        }

        private static List<SimCardSnapshot> ResolveAdjacentCards(IList<SimCardSnapshot> cards, SimCardSnapshot sourceCard)
        {
            if (cards == null || cards.Count == 0)
                return new List<SimCardSnapshot>();

            var anchor = sourceCard != null && cards.Contains(sourceCard)
                ? cards.IndexOf(sourceCard)
                : cards.Count / 2;
            var result = new List<SimCardSnapshot>();
            if (anchor > 0)
                result.Add(cards[anchor - 1]);
            if (anchor + 1 < cards.Count)
                result.Add(cards[anchor + 1]);
            return result;
        }

        private static bool IsMultiTargetScope(string scope)
        {
            return scope == "all_self_cards" ||
                   scope == "all_opponent_cards" ||
                   scope == "adjacent_self_cards" ||
                   scope == "adjacent_opponent_cards";
        }

        private static void ApplyCombatantAttributeModification(SimCombatantSnapshot target, string attrName, int value)
        {
            switch (attrName)
            {
                case "Shield":
                    target.Shield = Math.Max(0, target.Shield + value);
                    break;
                case "Burn":
                    target.Burn = Math.Max(0, target.Burn + value);
                    break;
                case "Poison":
                    target.Poison = Math.Max(0, target.Poison + value);
                    break;
                case "RageMax":
                    target.RageMax = Math.Max(0, target.RageMax + value);
                    target.Rage = Math.Min(target.Rage, target.RageMax);
                    break;
                case "EnragedDuration":
                    target.EnragedDuration = Math.Max(0, target.EnragedDuration + value);
                    break;
                case "EnragedDurationMax":
                    target.EnragedDurationMax = Math.Max(0, target.EnragedDurationMax + value);
                    target.EnragedDuration = Math.Min(target.EnragedDuration, target.EnragedDurationMax);
                    break;
                case "Enraged":
                    target.IsEnraged = value > 0;
                    if (!target.IsEnraged)
                        target.EnragedDuration = 0;
                    break;
                default:
                    break;
            }
        }

        private static void RecordTrace(
            ICollection<SimulationTraceEntry> trace,
            int timeMs,
            SimCombatantSnapshot player,
            SimCombatantSnapshot opponent,
            string summary,
            List<string> events = null)
        {
            trace.Add(new SimulationTraceEntry
            {
                TimeMs = timeMs,
                Summary = summary,
                PlayerState = FormatCombatantState("Player", player),
                OpponentState = FormatCombatantState("Opponent", opponent),
                CardStates = player.Cards.Select(card => FormatCardState("P", card))
                    .Concat(opponent.Cards.Select(card => FormatCardState("O", card)))
                    .ToList(),
                Events = events?.ToList() ?? new List<string>()
            });
        }

        private static string FormatCombatantState(string label, SimCombatantSnapshot combatant)
        {
            return $"{label}:HP={Math.Max(0, combatant.Health)}/{combatant.HealthMax},Shield={combatant.Shield},Burn={combatant.Burn},Poison={combatant.Poison},Regen={combatant.HealthRegen},Rage={combatant.Rage}/{combatant.RageMax},Enraged={(combatant.IsEnraged ? combatant.EnragedDuration : 0)}";
        }

        private static string FormatCardState(string prefix, SimCardSnapshot card)
        {
            return $"{prefix}:{card.Name}:CD={card.CurrentCooldown}/{card.CooldownMax},Freeze={card.Freeze},Haste={card.HasteDuration},Slow={card.SlowDuration},x{card.Multicast}";
        }
    }
}
