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
                CoverageGaps = playerSnapshot.CoverageNotes
                    .Concat(encounterSnapshot.Opponent.CoverageNotes)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList(),
                KeyThreats = BuildThreatSummary(encounterSnapshot.Opponent)
            };
            var totalPlayerHealthRemaining = 0.0;
            var totalOpponentHealthRemaining = 0.0;
            var durations = new List<double>(result.Runs);

            for (var i = 0; i < result.Runs; i++)
            {
                var seed = options.SeedBase + i;
                var captureTrace = i < Math.Max(0, options.TraceSamples);
                var sample = RunOnce(
                    CloneCombatant(playerSnapshot),
                    CloneCombatant(encounterSnapshot.Opponent),
                    seed,
                    captureTrace);
                if (captureTrace && result.TraceSample == null)
                    result.TraceSample = sample;

                totalPlayerHealthRemaining += sample.PlayerHealthRemaining;
                totalOpponentHealthRemaining += sample.OpponentHealthRemaining;
                durations.Add(sample.DurationMs / 1000.0);
                if (sample.Winner == "Player")
                    result.Wins++;
                else
                    result.Losses++;
                if (sample.SandstormTriggered)
                    result.SandstormSeen = true;
            }

            result.WinRate = result.Runs == 0 ? 0 : (double)result.Wins / result.Runs;
            result.AveragePlayerHealthRemaining = result.Runs == 0 ? 0 : totalPlayerHealthRemaining / result.Runs;
            result.AverageOpponentHealthRemaining = result.Runs == 0 ? 0 : totalOpponentHealthRemaining / result.Runs;
            result.MedianDurationSeconds = Median(durations);
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
            var pendingEffects = new List<PendingSimEffect>();
            TriggerEffects(player, opponent, rng, SimEffectTriggers.OnFightStarted, events: null, ownerLabel: "Player");
            TriggerEffects(opponent, player, rng, SimEffectTriggers.OnFightStarted, events: null, ownerLabel: "Opponent");
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

                ProcessPendingEffects(pendingEffects, timeMs, rng, events);
                if (player.Health <= 0 || opponent.Health <= 0)
                {
                    events?.Add("combat_end:pending_effect");
                    if (captureTrace)
                        RecordTrace(trace, timeMs, player, opponent, string.Join(" | ", events), events);
                    break;
                }

                ProcessCards(player, opponent, rng, events, "Player", pendingEffects, timeMs);
                ProcessCards(opponent, player, rng, events, "Opponent", pendingEffects, timeMs);
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

        private static void ProcessCards(
            SimCombatantSnapshot owner,
            SimCombatantSnapshot target,
            Random rng,
            List<string> events,
            string ownerLabel,
            List<PendingSimEffect> pendingEffects,
            int timeMs)
        {
            foreach (var card in owner.Cards)
            {
                if (card.IsDisabled)
                    continue;

                if (card.CooldownMax <= 0)
                    continue;

                AdvanceCardTimers(card);
                AdvanceCooldown(owner, card);
                if (card.CurrentCooldown > 0)
                    continue;

                if (card.AmmoMax > 0 && card.CurrentAmmo <= 0)
                    continue;

                events?.Add($"{ownerLabel}:trigger:{card.Name}");

                if (card.AmmoMax > 0)
                {
                    card.CurrentAmmo = Math.Max(0, card.CurrentAmmo - 1);
                    events?.Add($"{ownerLabel}:ammo:{card.Name}={card.CurrentAmmo}/{card.AmmoMax}");
                }

                var castCount = Math.Max(1, Math.Min(card.Multicast, MaxCastsPerTrigger));
                if (card.Multicast > MaxCastsPerTrigger)
                    Plugin.Log?.LogWarning($"Clamped multicast for {ownerLabel}:{card.Name} from {card.Multicast} to {MaxCastsPerTrigger}");

                for (var i = 0; i < castCount; i++)
                    ExecuteCard(card, owner, target, rng, events, ownerLabel, i);

                TriggerItemUsedEffects(owner, target, card, rng, events, ownerLabel, pendingEffects, timeMs);

                card.CurrentCooldown = GetEffectiveCooldownMax(card);
                events?.Add($"{ownerLabel}:reset_cd:{card.Name}={card.CurrentCooldown}");
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
            var passiveEffects = owner.Cards
                .SelectMany(card => card.Effects
                    .Where(effect => effect.IsPassive)
                    .Select(effect => new { Card = card, Effect = effect }))
                .ToList();

            foreach (var entry in passiveEffects.Where(entry => entry.Effect.Type.StartsWith("buff_", StringComparison.OrdinalIgnoreCase)))
            {
                if (!ShouldActivateEffect(entry.Effect, owner, entry.Card))
                    continue;

                ApplyEffect(entry.Effect, owner, target, rng, entry.Card);
            }

            foreach (var entry in passiveEffects.Where(entry => !entry.Effect.Type.StartsWith("buff_", StringComparison.OrdinalIgnoreCase)))
            {
                if (!ShouldActivateEffect(entry.Effect, owner, entry.Card))
                    continue;

                ApplyEffect(entry.Effect, owner, target, rng, entry.Card);
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
            int multicastIndex = 0,
            SimCardSnapshot triggerSourceCard = null)
        {
            var resolvedValue = ResolveEffectValue(effect, owner, target, sourceCard);
            if (resolvedValue == 0)
                return;

            var targetCombatants = ResolveCombatantTargets(owner, target, effect.Target);
            var targetAnchor = effect.UseTriggerSourceForTargeting ? triggerSourceCard ?? sourceCard : sourceCard;
            var effectLabel = $"{ownerLabel ?? owner.Name}:{sourceCard?.Name ?? "passive"}:{effect.Type}={resolvedValue}->{effect.Target}";
            if (multicastIndex > 0)
                effectLabel += $"#cast{multicastIndex + 1}";
            switch (effect.Type)
            {
                case "damage":
                    foreach (var combatant in targetCombatants)
                    {
                        var damageAmount = RollDamageAmount(sourceCard, resolvedValue, rng, out var isCrit);
                        ApplyDamage(combatant, damageAmount);
                        TriggerHealthLossEffectsIfNeeded(combatant, ReferenceEquals(combatant, owner) ? target : owner, rng, events, combatant.Name);
                        if (isCrit &&
                            sourceCard != null &&
                            !string.Equals(effect.Trigger, SimEffectTriggers.OnCardCritted, StringComparison.OrdinalIgnoreCase))
                        {
                            TriggerCardCritEffects(owner, target, sourceCard, rng, events, ownerLabel);
                        }

                        events?.Add($"{effectLabel}:{combatant.Name}:hp={combatant.Health}:shield={combatant.Shield}{(isCrit ? ":crit" : "")}");
                    }
                    break;

                case "heal":
                    foreach (var combatant in targetCombatants)
                    {
                        combatant.Health = Math.Min(combatant.HealthMax, combatant.Health + resolvedValue);
                        CleanseOnHeal(combatant, resolvedValue);
                        events?.Add($"{effectLabel}:{combatant.Name}:hp={combatant.Health}:burn={combatant.Burn}:poison={combatant.Poison}");
                    }
                    break;

                case "shield":
                case "shield_apply":
                    foreach (var combatant in targetCombatants)
                    {
                        combatant.Shield += resolvedValue;
                        events?.Add($"{effectLabel}:{combatant.Name}:shield={combatant.Shield}");
                    }
                    break;

                case "burn":
                case "burn_apply":
                    foreach (var combatant in targetCombatants)
                    {
                        combatant.Burn += resolvedValue;
                        events?.Add($"{effectLabel}:{combatant.Name}:burn={combatant.Burn}");
                    }
                    break;

                case "poison":
                case "poison_apply":
                    foreach (var combatant in targetCombatants)
                    {
                        combatant.Poison += resolvedValue;
                        events?.Add($"{effectLabel}:{combatant.Name}:poison={combatant.Poison}");
                    }
                    break;

                case "burn_remove":
                    foreach (var combatant in targetCombatants)
                    {
                        var removeAmount = resolvedValue > 0 ? resolvedValue : (int)Math.Round(combatant.Burn * 0.5, MidpointRounding.AwayFromZero);
                        combatant.Burn = Math.Max(0, combatant.Burn - removeAmount);
                        events?.Add($"{effectLabel}:{combatant.Name}:burn={combatant.Burn}");
                    }
                    break;

                case "poison_remove":
                    foreach (var combatant in targetCombatants)
                    {
                        var removeAmount = resolvedValue > 0 ? resolvedValue : (int)Math.Round(combatant.Poison * 0.5, MidpointRounding.AwayFromZero);
                        combatant.Poison = Math.Max(0, combatant.Poison - removeAmount);
                        events?.Add($"{effectLabel}:{combatant.Name}:poison={combatant.Poison}");
                    }
                    break;

                case "regen_apply":
                case "modify_HealthRegen":
                    foreach (var combatant in targetCombatants)
                    {
                        combatant.HealthRegen += resolvedValue;
                        events?.Add($"{effectLabel}:{combatant.Name}:regen={combatant.HealthRegen}");
                    }
                    break;

                case "modify_HealthMax":
                    foreach (var combatant in targetCombatants)
                    {
                        combatant.HealthMax = Math.Max(1, combatant.HealthMax + resolvedValue);
                        combatant.Health = Math.Min(combatant.HealthMax, combatant.Health + resolvedValue);
                        events?.Add($"{effectLabel}:{combatant.Name}:hp={combatant.Health}/{combatant.HealthMax}");
                    }
                    break;

                case "joy":
                    foreach (var combatant in targetCombatants)
                    {
                        combatant.Joy += resolvedValue;
                        events?.Add($"{effectLabel}:{combatant.Name}:joy={combatant.Joy}");
                    }
                    break;

                case "rage":
                    foreach (var combatant in targetCombatants)
                    {
                        var otherCombatant = ReferenceEquals(combatant, owner) ? target : owner;
                        ApplyRage(combatant, otherCombatant, resolvedValue, rng, events, ownerLabel ?? owner.Name, effectLabel);
                    }
                    break;

                case "haste":
                    ApplyStatusDuration(ResolveCardTargets(owner, target, targetAnchor, effect.Target), resolvedValue, effect.TargetCount, isHaste: true, rng: rng, scope: effect.Target);
                    events?.Add(effectLabel);
                    break;

                case "cooldown_charge":
                    ModifyCardCooldown(ResolveCardTargets(owner, target, targetAnchor, effect.Target), -resolvedValue, rng, effect.Target);
                    events?.Add(effectLabel);
                    break;

                case "ammo_reload":
                    ReloadAmmo(ResolveCardTargets(owner, target, targetAnchor, effect.Target), resolvedValue, rng, effect.Target);
                    events?.Add(effectLabel);
                    break;

                case "slow":
                    ApplyStatusDuration(ResolveCardTargets(owner, target, targetAnchor, effect.Target), resolvedValue, effect.TargetCount, isHaste: false, rng: rng, scope: effect.Target);
                    events?.Add(effectLabel);
                    break;

                case "freeze":
                    FreezeRandomCard(ResolveCardTargets(owner, target, targetAnchor, effect.Target), resolvedValue, effect.TargetCount, rng, effect.Target);
                    events?.Add(effectLabel);
                    break;

                case "disable":
                    DisableCards(ResolveCardTargets(owner, target, targetAnchor, effect.Target), effect.TargetCount, rng, effect.Target);
                    events?.Add(effectLabel);
                    break;

                case "clear_freeze":
                    ClearCardStatus(ResolveCardTargets(owner, target, targetAnchor, effect.Target), clearFreeze: true, clearSlow: false);
                    events?.Add(effectLabel);
                    break;

                case "clear_slow":
                    ClearCardStatus(ResolveCardTargets(owner, target, targetAnchor, effect.Target), clearFreeze: false, clearSlow: true);
                    events?.Add(effectLabel);
                    break;

                default:
                    if (effect.Type.StartsWith("buff_", StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyCardAttributeBuff(owner, target, targetAnchor, effect, resolvedValue, rng);
                        events?.Add(effectLabel);
                    }
                    else if (effect.Type.StartsWith("modify_", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var combatant in targetCombatants)
                        {
                            ApplyCombatantAttributeModification(combatant, effect.Type.Substring("modify_".Length), resolvedValue);
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
            int resolvedValue,
            Random rng)
        {
            var attrName = effect.Type.Substring("buff_".Length);
            var selected = ResolveCardTargets(owner, target, sourceCard, effect.Target);
            if (selected.Count == 0)
                return;

            foreach (var card in SelectBuffTargets(selected, attrName, rng, effect.Target))
            {
                card.Attributes[attrName] = card.Attributes.TryGetValue(attrName, out var current)
                    ? current + resolvedValue
                    : resolvedValue;

                switch (attrName)
                {
                    case "DamageAmount":
                        AdjustEffectValue(card, "damage", resolvedValue);
                        break;
                    case "ShieldApplyAmount":
                        AdjustEffectValue(card, "shield_apply", resolvedValue);
                        break;
                    case "HealAmount":
                        AdjustEffectValue(card, "heal", resolvedValue);
                        break;
                    case "RegenApplyAmount":
                        AdjustEffectValue(card, "regen_apply", resolvedValue);
                        break;
                    case "BurnApplyAmount":
                        AdjustEffectValue(card, "burn_apply", resolvedValue);
                        break;
                    case "PoisonApplyAmount":
                        AdjustEffectValue(card, "poison_apply", resolvedValue);
                        break;
                    case "HasteAmount":
                        AdjustEffectValue(card, "haste", resolvedValue);
                        break;
                    case "SlowAmount":
                        AdjustEffectValue(card, "slow", resolvedValue);
                        break;
                    case "FreezeAmount":
                        AdjustEffectValue(card, "freeze", resolvedValue);
                        break;
                    case "HasteTargets":
                        AdjustEffectTargetCount(card, "haste", resolvedValue);
                        break;
                    case "SlowTargets":
                        AdjustEffectTargetCount(card, "slow", resolvedValue);
                        break;
                    case "FreezeTargets":
                        AdjustEffectTargetCount(card, "freeze", resolvedValue);
                        break;
                    case "ChargeTargets":
                        AdjustEffectTargetCount(card, "cooldown_charge", resolvedValue);
                        break;
                    case "DisableTargets":
                        AdjustEffectTargetCount(card, "disable", resolvedValue);
                        break;
                    case "ChargeAmount":
                        AdjustEffectValue(card, "cooldown_charge", resolvedValue);
                        break;
                    case "ReloadAmount":
                        AdjustEffectValue(card, "ammo_reload", resolvedValue);
                        break;
                    case "ReloadTargets":
                        AdjustEffectTargetCount(card, "ammo_reload", resolvedValue);
                        break;
                    case "Multicast":
                        card.Multicast = Math.Max(1, card.Multicast + resolvedValue);
                        break;
                    case "AmmoMax":
                        card.AmmoMax = Math.Max(0, card.AmmoMax + resolvedValue);
                        card.CurrentAmmo = Math.Min(card.CurrentAmmo + Math.Max(0, resolvedValue), card.AmmoMax);
                        break;
                    case "Flying":
                        var flying = card.Attributes.TryGetValue("Flying", out var currentFlying) ? currentFlying : 0;
                        card.Attributes["Flying"] = Math.Max(0, flying + resolvedValue);
                        break;
                    case "Cooldown":
                    case "CooldownMax":
                        card.CooldownMax = Math.Max(250, card.CooldownMax - resolvedValue);
                        card.CurrentCooldown = Math.Min(card.CurrentCooldown, GetEffectiveCooldownMax(card));
                        break;
                    case "FlatCooldownReduction":
                        card.CurrentCooldown = Math.Min(card.CurrentCooldown, GetEffectiveCooldownMax(card));
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

            var previousRage = combatant.Rage;
            var maxRage = combatant.RageMax == 0 ? int.MaxValue : combatant.RageMax;
            combatant.Rage = Math.Max(0, Math.Min(maxRage, combatant.Rage + delta));
            events?.Add($"{effectLabel}:{combatant.Name}:rage={combatant.Rage}/{combatant.RageMax}");

            if (delta > 0 && combatant.Rage > previousRage)
                TriggerEffects(combatant, opponent, rng, SimEffectTriggers.OnPlayerRageGain, events, ownerLabel);

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
            string ownerLabel,
            List<PendingSimEffect> pendingEffects,
            int timeMs)
        {
            foreach (var card in owner.Cards)
            {
                foreach (var effect in card.Effects.Where(e => !e.IsPassive && e.Trigger == SimEffectTriggers.OnItemUsed).ToList())
                {
                    if (!ShouldActivateEffect(effect, owner, card) || !MatchesUsedCardTrigger(effect, usedCard))
                        continue;

                    pendingEffects.Add(new PendingSimEffect
                    {
                        DueTimeMs = timeMs + TickMs,
                        Effect = effect,
                        Owner = owner,
                        Target = target,
                        SourceCard = card,
                        TriggerSourceCard = usedCard,
                        OwnerLabel = ownerLabel
                    });
                }
            }
        }

        private static void ProcessPendingEffects(List<PendingSimEffect> pendingEffects, int timeMs, Random rng, List<string> events)
        {
            if (pendingEffects.Count == 0)
                return;

            var dueEffects = pendingEffects
                .Where(entry => entry.DueTimeMs <= timeMs)
                .ToList();
            if (dueEffects.Count == 0)
                return;

            pendingEffects.RemoveAll(entry => entry.DueTimeMs <= timeMs);
            foreach (var entry in dueEffects)
            {
                ApplyEffect(
                    entry.Effect,
                    entry.Owner,
                    entry.Target,
                    rng,
                    entry.SourceCard,
                    events,
                    entry.OwnerLabel,
                    triggerSourceCard: entry.TriggerSourceCard);
            }
        }

        private static void TriggerCardCritEffects(
            SimCombatantSnapshot owner,
            SimCombatantSnapshot target,
            SimCardSnapshot critCard,
            Random rng,
            List<string> events,
            string ownerLabel)
        {
            foreach (var effect in critCard.Effects.Where(e => !e.IsPassive && e.Trigger == SimEffectTriggers.OnCardCritted).ToList())
            {
                if (!ShouldActivateEffect(effect, owner, critCard))
                    continue;

                ApplyEffect(effect, owner, target, rng, critCard, events, ownerLabel);
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

            if (IsMultiTargetScope(scope) || scope == "self_card")
                return cards.ToList();

            var mappedEffectType = GetEffectTypeForAttribute(attrName);
            var prioritized = cards
                .Where(card => card.CooldownMax > 0 &&
                               (mappedEffectType == null || card.Effects.Any(effect => effect.Type == mappedEffectType)))
                .ToList();

            var pool = prioritized.Count > 0 ? prioritized : cards.Where(card => card.CooldownMax > 0).ToList();
            if (pool.Count == 0)
                pool = cards.ToList();
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
                case "RegenApplyAmount": return "regen_apply";
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
            }
        }

        private static void AdjustEffectTargetCount(SimCardSnapshot card, string effectType, int delta)
        {
            foreach (var effect in card.Effects.Where(effect => effect.Type == effectType))
                effect.TargetCount = Math.Max(1, effect.TargetCount + delta);
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

        private static int GetEffectiveCooldownMax(SimCardSnapshot card)
        {
            if (card == null)
                return 0;

            var flatReduction = GetAttribute(card, "FlatCooldownReduction");
            return Math.Max(250, card.CooldownMax + flatReduction);
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

        private static void ReloadAmmo(IList<SimCardSnapshot> cards, int amount, Random rng, string scope)
        {
            var candidates = cards.Where(c => c.AmmoMax > 0 && c.CurrentAmmo < c.AmmoMax).ToList();
            if (candidates.Count == 0)
                return;

            if (IsMultiTargetScope(scope))
            {
                foreach (var card in candidates)
                    card.CurrentAmmo = Math.Min(card.AmmoMax, card.CurrentAmmo + amount);
                return;
            }

            var selected = candidates[rng.Next(candidates.Count)];
            selected.CurrentAmmo = Math.Min(selected.AmmoMax, selected.CurrentAmmo + amount);
        }

        private static void FreezeRandomCard(IList<SimCardSnapshot> cards, int duration, int targetCount, Random rng, string scope)
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

            foreach (var card in PickRandomCards(candidates, targetCount, rng))
                card.Freeze = Math.Max(card.Freeze, GetAdjustedStatusDuration(card, duration, isFreeze: true));
        }

        private static void DisableCards(IList<SimCardSnapshot> cards, int targetCount, Random rng, string scope)
        {
            var candidates = cards.Where(c => !c.IsDisabled).ToList();
            if (candidates.Count == 0)
                return;

            if (IsMultiTargetScope(scope))
            {
                foreach (var card in candidates)
                    card.IsDisabled = true;
                return;
            }

            foreach (var card in PickRandomCards(candidates, targetCount, rng))
                card.IsDisabled = true;
        }

        private static void ApplyStatusDuration(IList<SimCardSnapshot> cards, int duration, int targetCount, bool isHaste, Random rng, string scope)
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

            foreach (var card in PickRandomCards(candidates, targetCount, rng))
                ApplyStatusDuration(card, duration, isHaste);
        }

        private static void ProcessDot(SimCombatantSnapshot target, SimCombatantSnapshot opponent, Random rng, List<string> events, string label)
        {
            if (target.Burn > 0)
                target.BurnTickProgress += TickMs;
            else
                target.BurnTickProgress = 0;
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

            if (target.Poison > 0)
                target.PoisonTickProgress += TickMs;
            else
                target.PoisonTickProgress = 0;
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

        private static int RollDamageAmount(SimCardSnapshot sourceCard, int baseDamage, Random rng, out bool isCrit)
        {
            isCrit = false;
            if (baseDamage <= 0 || sourceCard == null || rng == null)
                return baseDamage;

            var critChance = Math.Max(0, Math.Min(100, GetAttribute(sourceCard, "CritChance")));
            if (critChance <= 0 || rng.Next(100) >= critChance)
                return baseDamage;

            isCrit = true;
            var critBonusPercent = GetAttribute(sourceCard, "CritDamage");
            if (critBonusPercent <= 0)
                critBonusPercent = 100;

            return (int)Math.Round(baseDamage * (1.0 + critBonusPercent / 100.0), MidpointRounding.AwayFromZero);
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
                CoverageNotes = source.CoverageNotes.ToList(),
                Cards = source.Cards.Select(card => new SimCardSnapshot
                {
                    Name = card.Name,
                    InstanceId = card.InstanceId,
                    TemplateId = card.TemplateId,
                    CardType = card.CardType,
                    Tier = card.Tier,
                    Size = card.Size,
                    Tags = card.Tags.ToList(),
                    CooldownMax = card.CooldownMax,
                    CurrentCooldown = card.CooldownMax,
                    Multicast = card.Multicast,
                    AmmoMax = card.AmmoMax,
                    CurrentAmmo = card.AmmoMax,
                    Freeze = 0,
                    HasteDuration = 0,
                    SlowDuration = 0,
                    IsDisabled = card.IsDisabled,
                    Attributes = new Dictionary<string, int>(card.Attributes, StringComparer.OrdinalIgnoreCase),
                    CoverageScore = card.CoverageScore,
                    UnsupportedEffects = card.UnsupportedEffects.ToList(),
                    CoverageNotes = card.CoverageNotes.ToList(),
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
                        RequiresOwnerHealthBelowRatio = effect.RequiresOwnerHealthBelowRatio,
                        DynamicValueSourceAttribute = effect.DynamicValueSourceAttribute,
                        DynamicCountScope = effect.DynamicCountScope,
                        DynamicCountSourceAttribute = effect.DynamicCountSourceAttribute,
                        DynamicCountMultiplier = effect.DynamicCountMultiplier,
                        DynamicCountExcludeSource = effect.DynamicCountExcludeSource,
                        DynamicValueSign = effect.DynamicValueSign,
                        TargetCount = effect.TargetCount,
                        UseTriggerSourceForTargeting = effect.UseTriggerSourceForTargeting
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

        private static IEnumerable<SimCardSnapshot> PickRandomCards(IList<SimCardSnapshot> candidates, int targetCount, Random rng)
        {
            if (candidates == null || candidates.Count == 0)
                return Enumerable.Empty<SimCardSnapshot>();

            var count = Math.Max(1, targetCount);
            if (count >= candidates.Count)
                return candidates;

            return candidates
                .OrderBy(_ => rng.Next())
                .Take(count)
                .ToList();
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
            if (card?.Attributes == null || string.IsNullOrWhiteSpace(attrName))
                return 0;

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
                case "random_self_card":
                case "all_self_cards":
                case "adjacent_self_cards":
                case "all_self_weapon_cards":
                case "all_self_nonweapon_cards":
                case "left_self_card":
                case "right_self_card":
                case "leftmost_self_weapon_card":
                case "rightmost_self_weapon_card":
                    pool = owner.Cards;
                    break;
                case "opponent_card":
                case "random_opponent_card":
                case "all_opponent_cards":
                case "adjacent_opponent_cards":
                case "all_opponent_weapon_cards":
                case "all_opponent_nonweapon_cards":
                case "left_opponent_card":
                case "right_opponent_card":
                case "leftmost_opponent_weapon_card":
                case "rightmost_opponent_weapon_card":
                    pool = opponent.Cards;
                    break;
                default:
                    pool = targetMode != null && targetMode.Contains("opponent", StringComparison.OrdinalIgnoreCase)
                        ? opponent.Cards
                        : owner.Cards;
                    break;
            }

            var filteredPool = ApplyTargetCardFilter(pool, targetMode);

            if (targetMode == "self_card" && sourceCard != null && filteredPool.Contains(sourceCard))
                return new List<SimCardSnapshot> { sourceCard };

            if (targetMode == "adjacent_self_cards" || targetMode == "adjacent_opponent_cards")
                return ResolveAdjacentCards(filteredPool, sourceCard);

            if (targetMode == "left_self_card" || targetMode == "left_opponent_card")
                return ResolveDirectionalCard(filteredPool, sourceCard, -1);

            if (targetMode == "right_self_card" || targetMode == "right_opponent_card")
                return ResolveDirectionalCard(filteredPool, sourceCard, 1);

            if (targetMode == "leftmost_self_weapon_card" || targetMode == "leftmost_opponent_weapon_card")
                return filteredPool.Take(1).ToList();

            if (targetMode == "rightmost_self_weapon_card" || targetMode == "rightmost_opponent_weapon_card")
                return filteredPool.TakeLast(1).ToList();

            if ((targetMode == "all_self_cards" ||
                 targetMode == "all_opponent_cards" ||
                 targetMode == "all_self_weapon_cards" ||
                 targetMode == "all_opponent_weapon_cards" ||
                 targetMode == "all_self_nonweapon_cards" ||
                 targetMode == "all_opponent_nonweapon_cards") &&
                sourceCard != null &&
                filteredPool.Contains(sourceCard))
            {
                return filteredPool.Where(card => card != sourceCard).ToList();
            }

            return filteredPool.ToList();
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

        private static List<SimCardSnapshot> ResolveDirectionalCard(IList<SimCardSnapshot> cards, SimCardSnapshot sourceCard, int offset)
        {
            if (cards == null || cards.Count == 0 || sourceCard == null || !cards.Contains(sourceCard))
                return new List<SimCardSnapshot>();

            var targetIndex = cards.IndexOf(sourceCard) + offset;
            if (targetIndex < 0 || targetIndex >= cards.Count)
                return new List<SimCardSnapshot>();

            return new List<SimCardSnapshot> { cards[targetIndex] };
        }

        private static bool IsMultiTargetScope(string scope)
        {
            return scope == "all_self_cards" ||
                   scope == "all_opponent_cards" ||
                   scope == "all_self_weapon_cards" ||
                   scope == "all_opponent_weapon_cards" ||
                   scope == "all_self_nonweapon_cards" ||
                   scope == "all_opponent_nonweapon_cards" ||
                   scope == "adjacent_self_cards" ||
                   scope == "adjacent_opponent_cards";
        }

        private static List<SimCardSnapshot> ApplyTargetCardFilter(IEnumerable<SimCardSnapshot> cards, string targetMode)
        {
            var result = cards?.ToList() ?? new List<SimCardSnapshot>();
            if (targetMode == null)
                return result;

            if (targetMode.Contains("_weapon_", StringComparison.OrdinalIgnoreCase))
                return result.Where(IsWeaponCard).ToList();

            if (targetMode.Contains("_nonweapon_", StringComparison.OrdinalIgnoreCase))
                return result.Where(card => !IsWeaponCard(card)).ToList();

            return result;
        }

        private static bool IsWeaponCard(SimCardSnapshot card)
        {
            if (card?.Tags == null || card.Tags.Count == 0)
                return false;

            return card.Tags.Any(tag => string.Equals(tag, "Weapon", StringComparison.OrdinalIgnoreCase));
        }

        private static int ResolveEffectValue(
            SimEffectSpec effect,
            SimCombatantSnapshot owner,
            SimCombatantSnapshot opponent,
            SimCardSnapshot sourceCard)
        {
            if (effect == null)
                return 0;

            var value = effect.Value;
            if (!string.IsNullOrWhiteSpace(effect.DynamicCountScope))
            {
                var countedCards = CountCardsForScope(owner, opponent, sourceCard, effect.DynamicCountScope, effect.DynamicCountExcludeSource);
                var multiplier = !string.IsNullOrWhiteSpace(effect.DynamicCountSourceAttribute)
                    ? GetAttribute(sourceCard, effect.DynamicCountSourceAttribute)
                    : effect.DynamicCountMultiplier;
                value += effect.DynamicValueSign * countedCards * multiplier;
            }
            else if (!string.IsNullOrWhiteSpace(effect.DynamicValueSourceAttribute))
            {
                value += effect.DynamicValueSign * GetAttribute(sourceCard, effect.DynamicValueSourceAttribute);
            }

            return value;
        }

        private static int CountCardsForScope(
            SimCombatantSnapshot owner,
            SimCombatantSnapshot opponent,
            SimCardSnapshot sourceCard,
            string scope,
            bool excludeSource)
        {
            IEnumerable<SimCardSnapshot> pool =
                scope != null && scope.Contains("opponent", StringComparison.OrdinalIgnoreCase)
                    ? opponent.Cards
                    : owner.Cards;

            IEnumerable<SimCardSnapshot> filtered = ApplyTargetCardFilter(pool, scope);
            if (excludeSource && sourceCard != null)
                filtered = filtered.Where(card => !ReferenceEquals(card, sourceCard));

            return filtered.Count();
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
            var ammoStr = card.AmmoMax > 0 ? $",Ammo={card.CurrentAmmo}/{card.AmmoMax}" : "";
            return $"{prefix}:{card.Name}:CD={card.CurrentCooldown}/{card.CooldownMax}{ammoStr},Freeze={card.Freeze},Haste={card.HasteDuration},Slow={card.SlowDuration},Disabled={(card.IsDisabled ? 1 : 0)},x{card.Multicast}";
        }
    }
}
