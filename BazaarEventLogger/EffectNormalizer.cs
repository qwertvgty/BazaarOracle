using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace BazaarEventLogger
{
    public static class EffectNormalizer
    {
        public static NormalizedCardProfile NormalizeCard(CardInfo info, string tierName, IDictionary<string, int> runtimeAttributes = null)
        {
            if (info == null)
                return null;

            var tier = string.IsNullOrEmpty(tierName) ? info.StartingTier : tierName;
            var attrs = info.GetMergedTierAttributes(tier, runtimeAttributes);
            var profile = new NormalizedCardProfile
            {
                Name = info.InternalName,
                TemplateId = info.Id,
                Tier = tier,
                CooldownMax = GetValue(attrs, "CooldownMax"),
                Multicast = Math.Max(1, GetValue(attrs, "Multicast", 1)),
                Attributes = attrs
            };

            var recognized = 0;
            var total = 0;
            var template = info.RawTemplate;
            if (template != null)
            {
                var tiers = template["Tiers"] as JObject;
                var tierData = tiers?[tier] as JObject;
                var activeAbilities = new HashSet<string>(
                    tierData?["AbilityIds"]?.Values<string>() ?? Enumerable.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);

                var abilities = template["Abilities"] as JObject;
                if (abilities != null)
                {
                    foreach (var ability in abilities.Properties())
                    {
                        if (activeAbilities.Count > 0 && !activeAbilities.Contains(ability.Name))
                            continue;

                        total++;
                        var effects = ParseActions(ability.Value?["Action"] as JObject, attrs);
                        if (effects.Count > 0)
                        {
                            profile.Effects.AddRange(effects);
                            recognized++;
                        }
                        else
                        {
                            profile.UnsupportedEffects.Add(DescribeUnsupported("ability", ability.Name, ability.Value?["Action"]?["$type"]?.ToString()));
                        }
                    }
                }

                var activeAuras = new HashSet<string>(
                    tierData?["AuraIds"]?.Values<string>() ?? Enumerable.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);

                var auras = template["Auras"] as JObject;
                if (auras != null)
                {
                    foreach (var aura in auras.Properties())
                    {
                        if (activeAuras.Count > 0 && !activeAuras.Contains(aura.Name))
                            continue;

                        total++;
                        var effects = ParseAuraActions(aura.Value?["Action"] as JObject, attrs);
                        if (effects.Count > 0)
                        {
                            profile.Effects.AddRange(effects);
                            recognized++;
                        }
                        else
                        {
                            profile.UnsupportedEffects.Add(DescribeUnsupported("aura", aura.Name, aura.Value?["Action"]?["$type"]?.ToString()));
                        }
                    }
                }
            }

            if (profile.Effects.Count == 0)
                InferEffectsFromAttributes(profile, attrs);

            if (profile.Effects.Count > 0 && total == 0)
            {
                recognized = profile.Effects.Count;
                total = profile.Effects.Count;
            }

            profile.UnsupportedEffects = profile.UnsupportedEffects.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            profile.CoverageScore = total == 0 ? (profile.Effects.Count > 0 ? 0.75 : 0.25) : (double)recognized / total;
            return profile;
        }

        private static string DescribeUnsupported(string sourceKind, string id, string typeName)
        {
            var cleaned = string.IsNullOrEmpty(typeName) ? "unknown" : typeName;
            return $"{sourceKind}:{id}:{cleaned}";
        }

        private static void InferEffectsFromAttributes(NormalizedCardProfile profile, IDictionary<string, int> attrs)
        {
            AddIfPositive(profile.Effects, "damage", GetValue(attrs, "DamageAmount"), "opponent", "attr");
            AddIfPositive(profile.Effects, "burn_apply", GetValue(attrs, "BurnApplyAmount"), "opponent", "attr");
            AddIfPositive(profile.Effects, "poison_apply", GetValue(attrs, "PoisonApplyAmount"), "opponent", "attr");
            AddIfPositive(profile.Effects, "heal", GetValue(attrs, "HealAmount"), "self", "attr");
            AddIfPositive(profile.Effects, "shield_apply", GetValue(attrs, "ShieldApplyAmount"), "self", "attr");
            AddIfPositive(profile.Effects, "regen_apply", GetValue(attrs, "RegenApplyAmount"), "self", "attr");
            AddIfPositive(profile.Effects, "haste", GetValue(attrs, "HasteAmount"), "self_card", "attr");
            AddIfPositive(profile.Effects, "slow", GetValue(attrs, "SlowAmount"), "opponent_card", "attr");
            AddIfPositive(profile.Effects, "freeze", GetValue(attrs, "FreezeAmount"), "opponent_card", "attr");
            AddIfPositive(profile.Effects, "cooldown_charge", GetValue(attrs, "ChargeAmount"), "self_card", "attr");

            if (profile.Effects.Count == 0)
                AddIfPositive(profile.Effects, "damage", GetValue(attrs, "Custom_0"), "opponent", "attr");
        }

        private static List<SimEffectSpec> ParseActions(JObject action, IDictionary<string, int> attrs)
        {
            var effects = new List<SimEffectSpec>();
            if (action == null)
                return effects;

            var actionType = action["$type"]?.ToString() ?? "";
            switch (actionType)
            {
                case "TActionPlayerDamage":
                case "TActionCardDamage":
                    AddEffect(effects, "damage", Math.Abs(ResolveActionValue(action, attrs, "DamageAmount")), GetTargetMode(action["Target"] as JObject), actionType);
                    return effects;

                case "TActionPlayerHeal":
                case "TActionCardHeal":
                    AddEffect(effects, "heal", Math.Abs(ResolveActionValue(action, attrs, "HealAmount")), GetTargetMode(action["Target"] as JObject, "self"), actionType);
                    return effects;

                case "TActionPlayerShield":
                case "TActionPlayerShieldApply":
                case "TActionCardShield":
                    AddEffect(effects, "shield_apply", Math.Abs(ResolveActionValue(action, attrs, "ShieldApplyAmount")), GetTargetMode(action["Target"] as JObject, "self"), actionType);
                    return effects;

                case "TActionPlayerBurn":
                case "TActionPlayerBurnApply":
                case "TActionCardBurn":
                    AddEffect(effects, "burn_apply", Math.Abs(ResolveActionValue(action, attrs, "BurnApplyAmount")), GetTargetMode(action["Target"] as JObject), actionType);
                    return effects;

                case "TActionPlayerPoison":
                case "TActionPlayerPoisonApply":
                case "TActionCardPoison":
                    AddEffect(effects, "poison_apply", Math.Abs(ResolveActionValue(action, attrs, "PoisonApplyAmount")), GetTargetMode(action["Target"] as JObject), actionType);
                    return effects;

                case "TActionPlayerRegenApply":
                    AddEffect(effects, "regen_apply", Math.Abs(ResolveActionValue(action, attrs, "RegenApplyAmount")), GetTargetMode(action["Target"] as JObject, "self"), actionType);
                    return effects;

                case "TActionCardHaste":
                    AddEffect(effects, "haste", Math.Abs(ResolveActionValue(action, attrs, "HasteAmount", 1000)), GetTargetMode(action["Target"] as JObject, "self_card"), actionType);
                    return effects;

                case "TActionCardSlow":
                    AddEffect(effects, "slow", Math.Abs(ResolveActionValue(action, attrs, "SlowAmount", 1000)), GetTargetMode(action["Target"] as JObject, "opponent_card"), actionType);
                    return effects;

                case "TActionCardFreeze":
                    AddEffect(effects, "freeze", Math.Abs(ResolveActionValue(action, attrs, "FreezeAmount", 1000)), GetTargetMode(action["Target"] as JObject, "opponent_card"), actionType);
                    return effects;

                case "TActionCardCharge":
                case "TActionCardReload":
                    AddEffect(effects, "cooldown_charge", Math.Abs(ResolveActionValue(action, attrs, "ChargeAmount", 1000)), GetTargetMode(action["Target"] as JObject, "self_card"), actionType);
                    return effects;

                case "TActionPlayerJoyApply":
                    AddEffect(effects, "joy", Math.Abs(ResolveActionValue(action, attrs, "JoyAmount", 1)), GetTargetMode(action["Target"] as JObject, "self"), actionType);
                    return effects;

                case "TActionPlayerRageApply":
                    AddEffect(effects, "rage", Math.Abs(ResolveActionValue(action, attrs, "RageAmount", 1)), GetTargetMode(action["Target"] as JObject, "self"), actionType);
                    return effects;

                case "TActionPlayerModifyAttribute":
                    AddEffect(effects, ParsePlayerModify(action, attrs, actionType));
                    return effects;

                case "TActionCardModifyAttribute":
                    AddEffect(effects, ParseCardModify(action, attrs, actionType));
                    return effects;

                case "TActionComposite":
                case "TActionConditional":
                case "TActionAnd":
                    var nested = action["Actions"] as JArray;
                    if (nested != null)
                    {
                        foreach (var child in nested.OfType<JObject>())
                            effects.AddRange(ParseActions(child, attrs));
                    }

                    effects.AddRange(ParseActions(action["Action"] as JObject, attrs));
                    return effects;

                default:
                    return effects;
            }
        }

        private static List<SimEffectSpec> ParseAuraActions(JObject action, IDictionary<string, int> attrs)
        {
            var effects = new List<SimEffectSpec>();
            if (action == null)
                return effects;

            var actionType = action["$type"]?.ToString() ?? "";
            if (actionType == "TAuraActionCardModifyAttribute")
            {
                AddPassiveEffect(effects, ParseCardModify(action, attrs, actionType));
            }
            else if (actionType == "TAuraActionPlayerModifyAttribute")
            {
                AddPassiveEffect(effects, ParsePlayerModify(action, attrs, actionType));
            }
            else if (actionType.Contains("ModifyAttribute"))
            {
                var attrType = action["AttributeType"]?.ToString() ?? "";
                var value = ResolveValue(action["Value"] as JObject, attrs);
                if (value == 0)
                    return effects;

                AddPassiveEffect(effects, new SimEffectSpec
                {
                    Type = $"aura_{attrType}",
                    Value = value,
                    Target = GetTargetMode(action["Target"] as JObject, "self"),
                    Source = actionType
                });
            }

            return effects;
        }

        private static SimEffectSpec ParsePlayerModify(JObject action, IDictionary<string, int> attrs, string actionType)
        {
            var attrType = action["AttributeType"]?.ToString() ?? "";
            var value = ResolveValue(action["Value"] as JObject, attrs);
            if (value == 0)
                value = ResolveActionValue(action, attrs, attrType, 0);

            switch (attrType)
            {
                case "Health":
                    return NewEffect(value >= 0 ? "heal" : "damage", Math.Abs(value), GetTargetMode(action["Target"] as JObject, value >= 0 ? "self" : "opponent"), actionType);
                case "Shield":
                    return NewEffect(value >= 0 ? "shield_apply" : "modify_Shield", Math.Abs(value), GetTargetMode(action["Target"] as JObject, "self"), actionType);
                case "HealthRegen":
                    return NewEffect("modify_HealthRegen", value, GetTargetMode(action["Target"] as JObject, "self"), actionType);
                case "HealthMax":
                    return NewEffect("modify_HealthMax", value, GetTargetMode(action["Target"] as JObject, "self"), actionType);
                case "Burn":
                    return NewEffect(value >= 0 ? "burn_apply" : "modify_Burn", Math.Abs(value), GetTargetMode(action["Target"] as JObject, "opponent"), actionType);
                case "Poison":
                    return NewEffect(value >= 0 ? "poison_apply" : "modify_Poison", Math.Abs(value), GetTargetMode(action["Target"] as JObject, "opponent"), actionType);
                case "Joy":
                    return NewEffect("joy", value, GetTargetMode(action["Target"] as JObject, "self"), actionType);
                case "Rage":
                    return NewEffect("rage", value, GetTargetMode(action["Target"] as JObject, "self"), actionType);
                case "RageMax":
                case "EnragedDuration":
                case "EnragedDurationMax":
                case "Experience":
                    return NewEffect($"modify_{attrType}", value, GetTargetMode(action["Target"] as JObject, "self"), actionType);
                default:
                    return NewEffect($"modify_{attrType}", value, GetTargetMode(action["Target"] as JObject, "self"), actionType);
            }
        }

        private static SimEffectSpec ParseCardModify(JObject action, IDictionary<string, int> attrs, string actionType)
        {
            var attrType = action["AttributeType"]?.ToString() ?? "";
            var value = ResolveValue(action["Value"] as JObject, attrs);
            if (value == 0)
                return null;

            switch (attrType)
            {
                case "Cooldown":
                case "CooldownMax":
                    return NewEffect("cooldown_charge", Math.Abs(value), GetTargetMode(action["Target"] as JObject, "self_card"), actionType);
                case "DamageAmount":
                case "ShieldApplyAmount":
                case "HealAmount":
                case "BurnApplyAmount":
                case "PoisonApplyAmount":
                case "HasteAmount":
                case "SlowAmount":
                case "FreezeAmount":
                case "ChargeAmount":
                case "Multicast":
                    return NewEffect($"buff_{attrType}", value, GetTargetMode(action["Target"] as JObject, "self_card"), actionType);
                default:
                    return NewEffect($"buff_{attrType}", value, GetTargetMode(action["Target"] as JObject, "self_card"), actionType);
            }
        }

        private static SimEffectSpec NewEffect(string type, int value, string target, string source)
        {
            if (value == 0)
                return null;

            return new SimEffectSpec
            {
                Type = type,
                Value = value,
                Target = string.IsNullOrEmpty(target) ? "opponent" : target,
                Source = source
            };
        }

        private static string GetTargetMode(JObject target, string fallback = "opponent")
        {
            if (target == null)
                return fallback;

            var type = target["$type"]?.ToString() ?? "";
            var targetMode = target["TargetMode"]?.ToString() ?? "";
            var section = target["TargetSection"]?.ToString() ?? "";

            if (type.Contains("CardAdjacent"))
            {
                if (type.Contains("Opponent") || section.Contains("Opponent"))
                    return "adjacent_opponent_cards";
                return "adjacent_self_cards";
            }
            if (type.Contains("CardAll") || section.Contains("All"))
            {
                if (type.Contains("Opponent") || targetMode == "Opponent" || section.Contains("Opponent"))
                    return "all_opponent_cards";
                return "all_self_cards";
            }
            if (type.Contains("CardSelf"))
                return "self_card";
            if (type.Contains("Opponent") || targetMode == "Opponent")
                return "opponent";
            if (type.Contains("Self") || targetMode == "Player")
                return "self";
            if (type.Contains("Random") && section.Contains("Opponent"))
                return "opponent_card";
            if (type.Contains("Random"))
                return "self_card";

            return fallback;
        }

        private static int ResolveActionValue(JObject action, IDictionary<string, int> attrs, string attrFallback, int defaultValue = 0)
        {
            var value = ResolveValue(action["Value"] as JObject, attrs);
            if (value != 0)
                return value;

            value = ResolveValue(action["ReferenceValue"] as JObject, attrs);
            if (value != 0)
                return value;

            return GetValue(attrs, attrFallback, defaultValue);
        }

        private static int ResolveValue(JObject valueObj, IDictionary<string, int> attrs)
        {
            if (valueObj == null)
                return 0;

            var type = valueObj["$type"]?.ToString() ?? "";
            switch (type)
            {
                case "TFixedValue":
                    return valueObj["Value"]?.Value<int>() ?? 0;

                case "TReferenceValueCardAttribute":
                case "TReferenceValueCardAttributeUnscaled":
                    return GetValue(attrs, valueObj["AttributeType"]?.ToString(), valueObj["DefaultValue"]?.Value<int>() ?? 0);

                case "TReferenceValuePlayerAttributeUnscaled":
                    return valueObj["DefaultValue"]?.Value<int>() ?? 0;

                default:
                    return 0;
            }
        }

        private static int GetValue(IDictionary<string, int> attrs, string key, int defaultValue = 0)
        {
            if (string.IsNullOrEmpty(key) || attrs == null)
                return defaultValue;

            return attrs.TryGetValue(key, out var value) ? value : defaultValue;
        }

        private static void AddIfPositive(ICollection<SimEffectSpec> target, string type, int value, string targetMode, string source)
        {
            if (value <= 0)
                return;

            target.Add(new SimEffectSpec
            {
                Type = type,
                Value = value,
                Target = targetMode,
                Source = source
            });
        }

        private static void AddEffect(ICollection<SimEffectSpec> target, string type, int value, string targetMode, string source)
        {
            var effect = NewEffect(type, value, targetMode, source);
            if (effect != null)
                target.Add(effect);
        }

        private static void AddEffect(ICollection<SimEffectSpec> target, SimEffectSpec effect)
        {
            if (effect != null)
                target.Add(effect);
        }

        private static void AddPassiveEffect(ICollection<SimEffectSpec> target, SimEffectSpec effect)
        {
            if (effect == null)
                return;

            effect.IsPassive = true;
            target.Add(effect);
        }
    }
}
