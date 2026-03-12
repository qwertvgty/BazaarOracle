using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using Newtonsoft.Json.Linq;

namespace BazaarEventLogger
{
    public static class SimulationSnapshotBuilder
    {
        public static SimCombatantSnapshot BuildPlayerSnapshot(GameSim state)
        {
            var snapshot = new SimCombatantSnapshot
            {
                Name = "Player",
                SourceId = "runtime",
                Health = 300,
                HealthMax = 300,
                RageMax = 100,
                EnragedDurationMax = 5000
            };

            if (state?.Player?.Attributes != null)
            {
                snapshot.HealthMax = GetPlayerAttr(state.Player.Attributes, "HealthMax", 300);
                snapshot.Health = GetPlayerAttr(state.Player.Attributes, "Health", snapshot.HealthMax);
                snapshot.Shield = GetPlayerAttr(state.Player.Attributes, "Shield", 0);
                snapshot.Burn = GetPlayerAttr(state.Player.Attributes, "Burn", 0);
                snapshot.Poison = GetPlayerAttr(state.Player.Attributes, "Poison", 0);
                snapshot.HealthRegen = GetPlayerAttr(state.Player.Attributes, "HealthRegen", 0);
                snapshot.Joy = GetPlayerAttr(state.Player.Attributes, "Joy", 0);
                snapshot.Rage = GetPlayerAttr(state.Player.Attributes, "Rage", 0);
                snapshot.RageMax = GetPlayerAttr(state.Player.Attributes, "RageMax", 100);
                snapshot.EnragedDurationMax = GetPlayerAttr(state.Player.Attributes, "EnragedDurationMax", 5000);
            }

            if (state?.Cards != null)
            {
                var orderedCards = state.Cards
                    .Values
                    .Where(card => card != null)
                    .OrderBy(GetCardSortBucket)
                    .ThenBy(GetCardSocketOrder)
                    .ThenBy(card => CardDatabase.ResolveName(card.InstanceId), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var card in orderedCards)
                {
                    var info = CardDatabase.GetInfo(card.InstanceId);
                    var type = info?.Type ?? "";
                    var isTriggeredSupport = string.Equals(type, "Skill", StringComparison.OrdinalIgnoreCase) ||
                                             string.Equals(type, "PlayerEffect", StringComparison.OrdinalIgnoreCase);
                    if (card == null ||
                        card.State != ECardState.Alive ||
                        (!isTriggeredSupport && card.Placement?.Section != EInventorySection.Hand))
                    {
                        continue;
                    }

                    var cardSnapshot = BuildCardSnapshot(card, info);
                    if (cardSnapshot != null)
                        snapshot.Cards.Add(cardSnapshot);
                }
            }

            snapshot.UnsupportedEffects = snapshot.Cards
                .SelectMany(c => c.UnsupportedEffects)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return snapshot;
        }

        public static SimEncounterSnapshot BuildEncounterSnapshot(string encounterId, string encounterName, string monsterId, JToken monsterObj)
        {
            var opponent = BuildMonsterSnapshot(monsterId, monsterObj);
            return new SimEncounterSnapshot
            {
                EncounterId = encounterId,
                EncounterName = encounterName,
                MonsterId = monsterId,
                Opponent = opponent
            };
        }

        public static SimCombatantSnapshot BuildMonsterSnapshot(string monsterId, JToken monsterObj)
        {
            var snapshot = new SimCombatantSnapshot
            {
                Name = monsterObj?["internalName"]?.ToString() ?? "Monster",
                SourceId = monsterId,
                Health = 300,
                HealthMax = 300,
                RageMax = 100,
                EnragedDurationMax = 5000
            };

            var attrs = monsterObj?["player"]?["attributes"] as JObject;
            if (attrs != null)
            {
                snapshot.HealthMax = attrs["HealthMax"]?.Value<int>() ?? 300;
                snapshot.Health = attrs["Health"]?.Value<int>() ?? snapshot.HealthMax;
                snapshot.Shield = attrs["Shield"]?.Value<int>() ?? 0;
                snapshot.Burn = attrs["Burn"]?.Value<int>() ?? 0;
                snapshot.Poison = attrs["Poison"]?.Value<int>() ?? 0;
                snapshot.HealthRegen = attrs["HealthRegen"]?.Value<int>() ?? 0;
                snapshot.RageMax = attrs["RageMax"]?.Value<int>() ?? 100;
                snapshot.EnragedDurationMax = attrs["EnragedDurationMax"]?.Value<int>() ?? 5000;
            }

            foreach (var cardObj in monsterObj?["cards"] as JArray ?? new JArray())
            {
                var cardSnapshot = BuildCardSnapshot(cardObj as JObject);
                if (cardSnapshot != null)
                    snapshot.Cards.Add(cardSnapshot);
            }

            foreach (var cardObj in monsterObj?["skills"] as JArray ?? new JArray())
            {
                var cardSnapshot = BuildCardSnapshot(cardObj as JObject);
                if (cardSnapshot != null)
                    snapshot.Cards.Add(cardSnapshot);
            }

            snapshot.UnsupportedEffects = snapshot.Cards
                .SelectMany(c => c.UnsupportedEffects)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return snapshot;
        }

        private static SimCardSnapshot BuildCardSnapshot(SimUpdateCard card, CardInfo info = null)
        {
            var runtimeAttrs = GetRuntimeCardAttributes(card);
            info = info ?? CardDatabase.GetInfo(card.InstanceId);
            var tier = card.Tier?.ToString() ?? info?.StartingTier ?? "Bronze";
            var profile = EffectNormalizer.NormalizeCard(info, tier, runtimeAttrs);

            var snapshot = new SimCardSnapshot
            {
                Name = CardDatabase.ResolveName(card.InstanceId),
                InstanceId = card.InstanceId,
                TemplateId = info?.Id ?? card.InstanceId,
                CardType = info?.Type ?? "",
                Tier = tier,
                Size = info?.Size ?? "",
                Tags = info?.Tags?.ToList() ?? new List<string>(),
                CooldownMax = profile?.CooldownMax ?? GetRuntimeAttr(runtimeAttrs, "CooldownMax"),
                Multicast = profile?.Multicast ?? Math.Max(1, GetRuntimeAttr(runtimeAttrs, "Multicast", 1)),
                Attributes = profile?.Attributes ?? runtimeAttrs,
                Effects = profile?.Effects ?? new List<SimEffectSpec>(),
                UnsupportedEffects = profile?.UnsupportedEffects ?? new List<string>(),
                CoverageScore = profile?.CoverageScore ?? 0.25
            };

            var hasActiveCooldownEffect = snapshot.Effects.Any(effect => effect.Trigger == SimEffectTriggers.OnCardFired);
            if (snapshot.CooldownMax <= 0 && !hasActiveCooldownEffect && snapshot.Effects.Count == 0)
                return null;

            snapshot.CurrentCooldown = Math.Max(0, snapshot.CooldownMax);
            if (snapshot.Effects.Count == 0)
                snapshot.UnsupportedEffects.Add("runtime:no_effects");

            return snapshot;
        }

        private static SimCardSnapshot BuildCardSnapshot(JObject cardObj)
        {
            if (cardObj == null)
                return null;

            var templateId = cardObj["templateId"]?.ToString() ?? "";
            var tier = cardObj["tier"]?.ToString() ?? "Bronze";
            var exportedAttrs = (cardObj["attributes"] as JObject ?? new JObject())
                .Properties()
                .Where(p => int.TryParse(p.Value.ToString(), out _))
                .ToDictionary(p => p.Name, p => int.Parse(p.Value.ToString()), StringComparer.OrdinalIgnoreCase);
            var info = CardDatabase.GetInfo(templateId);
            NormalizedCardProfile profile = null;
            try
            {
                profile = EffectNormalizer.NormalizeCard(info, tier, exportedAttrs);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Monster card normalize fallback: {templateId} ({tier}) -> {ex.Message}");
            }

            var snapshot = new SimCardSnapshot
            {
                Name = info?.InternalName ?? cardObj["name"]?.ToString() ?? "?",
                TemplateId = templateId,
                CardType = info?.Type ?? cardObj["type"]?.ToString() ?? "",
                Tier = tier,
                Size = info?.Size ?? cardObj["size"]?.ToString() ?? "",
                Tags = info?.Tags?.ToList() ?? new List<string>(),
                CooldownMax = profile?.CooldownMax ?? cardObj["cooldownMax"]?.Value<int>() ?? 0,
                Multicast = profile?.Multicast ?? Math.Max(1, cardObj["multicast"]?.Value<int>() ?? 1),
                Attributes = profile?.Attributes ?? exportedAttrs,
                Effects = profile?.Effects ?? new List<SimEffectSpec>(),
                UnsupportedEffects = profile?.UnsupportedEffects ?? new List<string>(),
                CoverageScore = profile?.CoverageScore ?? cardObj["coverageScore"]?.Value<double?>() ?? 0.75
            };

            if (snapshot.Effects.Count == 0)
            {
                foreach (var effectObj in cardObj["effects"] as JArray ?? new JArray())
                {
                    var effect = new SimEffectSpec
                    {
                        Type = effectObj["type"]?.ToString() ?? "unknown",
                        Value = effectObj["value"]?.Value<int?>() ?? 0,
                        Target = effectObj["target"]?.ToString() ?? "opponent",
                        IsPassive = effectObj["passive"]?.Value<bool>() ?? false,
                        Source = effectObj["source"]?.ToString() ?? "export",
                        Trigger = effectObj["trigger"]?.ToString() ?? (effectObj["passive"]?.Value<bool>() ?? false ? SimEffectTriggers.Passive : SimEffectTriggers.OnCardFired),
                        RequiresOwnerEnraged = effectObj["requiresOwnerEnraged"]?.Value<bool>() ?? false,
                        RequiresOwnerNotEnraged = effectObj["requiresOwnerNotEnraged"]?.Value<bool>() ?? false,
                        TriggerCardSizes = (effectObj["triggerCardSizes"] as JArray ?? new JArray()).Values<string>().ToList(),
                        RequiresSourceAttributeZero = effectObj["requiresSourceAttributeZero"]?.ToString(),
                        RequiresOwnerHealthBelowRatio = effectObj["requiresOwnerHealthBelowRatio"]?.Value<double?>()
                    };

                    if (effect.Value > 0 || effect.IsPassive)
                        snapshot.Effects.Add(effect);
                }

                snapshot.UnsupportedEffects = (cardObj["unsupportedEffects"] as JArray ?? new JArray())
                    .Values<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var hasActiveCooldownEffect = snapshot.Effects.Any(effect => effect.Trigger == SimEffectTriggers.OnCardFired);
            if (snapshot.CooldownMax <= 0 && !hasActiveCooldownEffect && snapshot.Effects.Count == 0)
                return null;

            snapshot.CurrentCooldown = Math.Max(0, snapshot.CooldownMax);
            return snapshot;
        }

        private static Dictionary<string, int> GetRuntimeCardAttributes(SimUpdateCard card)
        {
            var attrs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (card?.Attributes == null)
                return attrs;

            foreach (var attr in card.Attributes)
            {
                if (attr.Value.DeltaType == EAttributeDeltaType.Update)
                    attrs[attr.Key.ToString()] = attr.Value.Value;
            }

            return attrs;
        }

        private static int GetPlayerAttr(Dictionary<EPlayerAttributeType, int> attrs, string attrName, int defaultValue)
        {
            foreach (var attr in attrs)
            {
                if (attr.Key.ToString() == attrName)
                    return attr.Value;
            }

            return defaultValue;
        }

        private static int GetRuntimeAttr(IDictionary<string, int> attrs, string key, int defaultValue = 0)
        {
            return attrs != null && attrs.TryGetValue(key, out var value) ? value : defaultValue;
        }

        private static int GetCardSortBucket(SimUpdateCard card)
        {
            var info = CardDatabase.GetInfo(card?.InstanceId);
            var type = info?.Type ?? "";
            if (string.Equals(type, "Skill", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "PlayerEffect", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return 0;
        }

        private static int GetCardSocketOrder(SimUpdateCard card)
        {
            var socket = card?.Placement?.Socket?.ToString();
            if (string.IsNullOrEmpty(socket))
                return int.MaxValue;

            var match = Regex.Match(socket, @"(\d+)$");
            return match.Success && int.TryParse(match.Groups[1].Value, out var value)
                ? value
                : int.MaxValue;
        }
    }
}
