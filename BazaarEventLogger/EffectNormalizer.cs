using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace BazaarEventLogger
{
    public static class EffectNormalizer
    {
        private class EffectMetadata
        {
            public string Trigger = SimEffectTriggers.OnCardFired;
            public bool RequiresOwnerEnraged;
            public bool RequiresOwnerNotEnraged;
            public List<string> TriggerCardSizes = new List<string>();
            public string RequiresSourceAttributeZero;
            public double? RequiresOwnerHealthBelowRatio;
        }

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
                Size = info.Size,
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
                        var metadata = ParseMetadata(ability.Value as JObject);
                        var effects = ParseActions(ability.Value?["Action"] as JObject, attrs, metadata);
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
                        var metadata = ParseMetadata(aura.Value as JObject);
                        var effects = ParseAuraActions(aura.Value?["Action"] as JObject, attrs, metadata);
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

        private static List<SimEffectSpec> ParseActions(JObject action, IDictionary<string, int> attrs, EffectMetadata metadata)
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
                    return ApplyMetadata(effects, metadata);

                case "TActionPlayerHeal":
                case "TActionCardHeal":
                    AddEffect(effects, "heal", Math.Abs(ResolveActionValue(action, attrs, "HealAmount")), GetTargetMode(action["Target"] as JObject, "self"), actionType);
                    return ApplyMetadata(effects, metadata);

                case "TActionPlayerShield":
                case "TActionPlayerShieldApply":
                case "TActionCardShield":
                    AddEffect(effects, "shield_apply", Math.Abs(ResolveActionValue(action, attrs, "ShieldApplyAmount")), GetTargetMode(action["Target"] as JObject, "self"), actionType);
                    return ApplyMetadata(effects, metadata);

                case "TActionPlayerBurn":
                case "TActionPlayerBurnApply":
                case "TActionCardBurn":
                    AddEffect(effects, "burn_apply", Math.Abs(ResolveActionValue(action, attrs, "BurnApplyAmount")), GetTargetMode(action["Target"] as JObject), actionType);
                    return ApplyMetadata(effects, metadata);

                case "TActionPlayerBurnRemove":
                    AddEffect(effects, "burn_remove", Math.Abs(ResolveActionValue(action, attrs, "BurnRemoveAmount")), GetTargetMode(action["Target"] as JObject, "self"), actionType);
                    return ApplyMetadata(effects, metadata);

                case "TActionPlayerPoison":
                case "TActionPlayerPoisonApply":
                case "TActionCardPoison":
                    AddEffect(effects, "poison_apply", Math.Abs(ResolveActionValue(action, attrs, "PoisonApplyAmount")), GetTargetMode(action["Target"] as JObject), actionType);
                    return ApplyMetadata(effects, metadata);

                case "TActionPlayerPoisonRemove":
                    AddEffect(effects, "poison_remove", Math.Abs(ResolveActionValue(action, attrs, "PoisonRemoveAmount")), GetTargetMode(action["Target"] as JObject, "self"), actionType);
                    return ApplyMetadata(effects, metadata);

                case "TActionPlayerRegenApply":
                    AddEffect(effects, "regen_apply", Math.Abs(ResolveActionValue(action, attrs, "RegenApplyAmount")), GetTargetMode(action["Target"] as JObject, "self"), actionType);
                    return ApplyMetadata(effects, metadata);

                case "TActionCardHaste":
                    AddEffect(effects, "haste", Math.Abs(ResolveActionValue(action, attrs, "HasteAmount", 1000)), GetTargetMode(action["Target"] as JObject, "self_card"), actionType);
                    return ApplyMetadata(effects, metadata);

                case "TActionCardSlow":
                    AddEffect(effects, "slow", Math.Abs(ResolveActionValue(action, attrs, "SlowAmount", 1000)), GetTargetMode(action["Target"] as JObject, "opponent_card"), actionType);
                    return ApplyMetadata(effects, metadata);

                case "TActionCardFreeze":
                    AddEffect(effects, "freeze", Math.Abs(ResolveActionValue(action, attrs, "FreezeAmount", 1000)), GetTargetMode(action["Target"] as JObject, "opponent_card"), actionType);
                    return ApplyMetadata(effects, metadata);

                case "TActionCardFlyingStart":
                    AddEffect(effects, "buff_Flying", 1, GetTargetMode(action["Target"] as JObject, "self_card"), actionType);
                    return ApplyMetadata(effects, metadata);

                case "TActionCardFlyingStop":
                    AddEffect(effects, "buff_Flying", -1, GetTargetMode(action["Target"] as JObject, "self_card"), actionType);
                    return ApplyMetadata(effects, metadata);

                case "TActionCardCharge":
                case "TActionCardReload":
                    AddEffect(effects, "cooldown_charge", Math.Abs(ResolveActionValue(action, attrs, "ChargeAmount", 1000)), GetTargetMode(action["Target"] as JObject, "self_card"), actionType);
                    return ApplyMetadata(effects, metadata);

                case "TActionPlayerJoyApply":
                    AddEffect(effects, "joy", Math.Abs(ResolveActionValue(action, attrs, "JoyAmount", 1)), GetTargetMode(action["Target"] as JObject, "self"), actionType);
                    return ApplyMetadata(effects, metadata);

                case "TActionPlayerRageApply":
                    AddEffect(effects, "rage", Math.Abs(ResolveActionValue(action, attrs, "RageApplyAmount", 1)), GetTargetMode(action["Target"] as JObject, "self"), actionType);
                    return ApplyMetadata(effects, metadata);

                case "TActionPlayerModifyAttribute":
                    AddEffect(effects, ParsePlayerModify(action, attrs, actionType));
                    return ApplyMetadata(effects, metadata);

                case "TActionCardModifyAttribute":
                    AddEffect(effects, ParseCardModify(action, attrs, actionType));
                    return ApplyMetadata(effects, metadata);

                case "TActionComposite":
                case "TActionConditional":
                case "TActionAnd":
                    var nested = action["Actions"] as JArray;
                    if (nested != null)
                    {
                        foreach (var child in nested.OfType<JObject>())
                            effects.AddRange(ParseActions(child, attrs, metadata));
                    }

                    effects.AddRange(ParseActions(action["Action"] as JObject, attrs, metadata));
                    return ApplyMetadata(effects, metadata);

                default:
                    return ApplyMetadata(effects, metadata);
            }
        }

        private static List<SimEffectSpec> ParseAuraActions(JObject action, IDictionary<string, int> attrs, EffectMetadata metadata)
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

            return ApplyMetadata(effects, metadata, isAura: true);
        }

        private static SimEffectSpec ParsePlayerModify(JObject action, IDictionary<string, int> attrs, string actionType)
        {
            var attrType = action["AttributeType"]?.ToString() ?? "";
            var value = ResolveValue(action["Value"] as JObject, attrs);
            if (value == 0)
                value = ResolveActionValue(action, attrs, attrType, 0);
            value = ApplyOperationSign(value, action["Operation"]?.ToString());

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
            var operation = action["Operation"]?.ToString();

            if ((string.Equals(attrType, "Freeze", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(attrType, "Slow", StringComparison.OrdinalIgnoreCase)) &&
                (string.Equals(operation, "Subtract", StringComparison.OrdinalIgnoreCase) && value >= 1000000 ||
                 string.Equals(operation, "Multiply", StringComparison.OrdinalIgnoreCase) && value == 0))
            {
                return NewEffect(
                    string.Equals(attrType, "Freeze", StringComparison.OrdinalIgnoreCase) ? "clear_freeze" : "clear_slow",
                    1,
                    GetTargetMode(action["Target"] as JObject, "self_card"),
                    actionType);
            }

            value = ApplyOperationSign(value, operation);
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

        private static EffectMetadata ParseMetadata(JObject definition)
        {
            var metadata = new EffectMetadata();
            var trigger = definition?["Trigger"] as JObject;
            var triggerType = trigger?["$type"]?.ToString();
            switch (triggerType)
            {
                case "TTriggerOnCardCritted":
                    metadata.Trigger = SimEffectTriggers.OnCardCritted;
                    break;
                case "TTriggerOnPlayerEnraged":
                    metadata.Trigger = SimEffectTriggers.OnPlayerEnraged;
                    break;
                case "TTriggerOnPlayerEnrageEnded":
                    metadata.Trigger = SimEffectTriggers.OnPlayerEnrageEnded;
                    break;
                case "TTriggerOnItemUsed":
                    metadata.Trigger = SimEffectTriggers.OnItemUsed;
                    metadata.TriggerCardSizes = ((((trigger?["Subject"] as JObject)?["Conditions"] as JObject)?["Sizes"] as JArray) ?? new JArray())
                        .Values<string>()
                        .Where(size => !string.IsNullOrWhiteSpace(size))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    break;
                case "TTriggerOnPlayerAttributeChanged":
                    if (string.Equals(trigger?["AttributeType"]?.ToString(), "Rage", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(trigger?["ChangeType"]?.ToString(), "Gain", StringComparison.OrdinalIgnoreCase))
                    {
                        metadata.Trigger = SimEffectTriggers.OnPlayerRageGain;
                        break;
                    }

                    if (string.Equals(trigger?["AttributeType"]?.ToString(), "Health", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(trigger?["ChangeType"]?.ToString(), "Loss", StringComparison.OrdinalIgnoreCase))
                    {
                        metadata.Trigger = SimEffectTriggers.OnPlayerHealthLoss;
                    }
                    break;
                default:
                    metadata.Trigger = SimEffectTriggers.OnCardFired;
                    break;
            }

            foreach (var prereq in definition?["Prerequisites"] as JArray ?? new JArray())
            {
                if (!(prereq is JObject prereqObj))
                    continue;

                if (!string.Equals(prereqObj["$type"]?.ToString(), "TPrerequisitePlayer", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(prereqObj["$type"]?.ToString(), "TPrerequisiteCardCount", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var selfSubject = prereqObj["Subject"] as JObject;
                    var selfCondition = selfSubject?["Conditions"] as JObject;
                    if (selfCondition == null)
                        continue;

                    if (!string.Equals(selfCondition["$type"]?.ToString(), "TCardConditionalAttribute", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!string.Equals(selfCondition["ComparisonOperator"]?.ToString(), "Equal", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (ResolveValue(selfCondition["ComparisonValue"] as JObject, null) != 0)
                        continue;

                    metadata.RequiresSourceAttributeZero = selfCondition["Attribute"]?.ToString();
                    continue;
                }

                var subject = prereqObj["Subject"] as JObject;
                var condition = subject?["Conditions"] as JObject;
                if (condition == null)
                    continue;

                if (!string.Equals(condition["$type"]?.ToString(), "TPlayerConditionalAttribute", StringComparison.OrdinalIgnoreCase))
                    continue;

                var attrName = condition["Attribute"]?.ToString();
                if (string.Equals(attrName, "Enraged", StringComparison.OrdinalIgnoreCase))
                {
                    var comparison = condition["ComparisonOperator"]?.ToString() ?? "";
                    var targetValue = ResolveValue(condition["ComparisonValue"] as JObject, null);
                    if ((comparison == "GreaterThan" || comparison == "GreaterThanOrEqual") && targetValue <= 0)
                        metadata.RequiresOwnerEnraged = true;
                    else if ((comparison == "Equal" || comparison == "LessThanOrEqual") && targetValue <= 0)
                        metadata.RequiresOwnerNotEnraged = true;
                    continue;
                }

                if (string.Equals(attrName, "Health", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(condition["ComparisonOperator"]?.ToString(), "LessThan", StringComparison.OrdinalIgnoreCase))
                {
                    metadata.RequiresOwnerHealthBelowRatio = ResolvePlayerAttributeRatio(condition["ComparisonValue"] as JObject, "HealthMax");
                }
            }

            return metadata;
        }

        private static int ApplyOperationSign(int value, string operation)
        {
            if (string.Equals(operation, "Subtract", StringComparison.OrdinalIgnoreCase))
                return -Math.Abs(value);

            return value;
        }

        private static List<SimEffectSpec> ApplyMetadata(List<SimEffectSpec> effects, EffectMetadata metadata, bool isAura = false)
        {
            foreach (var effect in effects)
            {
                effect.Trigger = effect.IsPassive ? SimEffectTriggers.Passive : metadata.Trigger;
                effect.RequiresOwnerEnraged = metadata.RequiresOwnerEnraged;
                effect.RequiresOwnerNotEnraged = metadata.RequiresOwnerNotEnraged;
                effect.TriggerCardSizes = metadata.TriggerCardSizes.ToList();
                effect.RequiresSourceAttributeZero = metadata.RequiresSourceAttributeZero;
                effect.RequiresOwnerHealthBelowRatio = metadata.RequiresOwnerHealthBelowRatio;
            }

            if (!isAura)
                return effects;

            if (metadata.RequiresOwnerEnraged)
                return ConvertAuraToEnrageWindow(effects, activeWhileEnraged: true);

            if (metadata.RequiresOwnerNotEnraged)
                return ConvertAuraToEnrageWindow(effects, activeWhileEnraged: false);

            return effects;
        }

        private static List<SimEffectSpec> ConvertAuraToEnrageWindow(IEnumerable<SimEffectSpec> effects, bool activeWhileEnraged)
        {
            var converted = new List<SimEffectSpec>();
            foreach (var effect in effects)
            {
                if (!activeWhileEnraged)
                {
                    var baseEffect = CloneEffect(effect);
                    baseEffect.IsPassive = true;
                    baseEffect.Trigger = SimEffectTriggers.Passive;
                    baseEffect.RequiresOwnerEnraged = false;
                    baseEffect.RequiresOwnerNotEnraged = false;
                    converted.Add(baseEffect);
                }

                var enterEffect = CloneEffect(effect);
                enterEffect.IsPassive = false;
                enterEffect.Trigger = activeWhileEnraged ? SimEffectTriggers.OnPlayerEnraged : SimEffectTriggers.OnPlayerEnrageEnded;
                enterEffect.RequiresOwnerEnraged = false;
                enterEffect.RequiresOwnerNotEnraged = false;
                converted.Add(enterEffect);

                var exitEffect = CloneEffect(effect);
                exitEffect.IsPassive = false;
                exitEffect.Trigger = activeWhileEnraged ? SimEffectTriggers.OnPlayerEnrageEnded : SimEffectTriggers.OnPlayerEnraged;
                exitEffect.Value = -exitEffect.Value;
                exitEffect.RequiresOwnerEnraged = false;
                exitEffect.RequiresOwnerNotEnraged = false;
                converted.Add(exitEffect);
            }

            return converted;
        }

        private static SimEffectSpec CloneEffect(SimEffectSpec effect)
        {
            return new SimEffectSpec
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
            };
        }

        private static double? ResolvePlayerAttributeRatio(JObject valueObj, string attributeType)
        {
            if (valueObj == null)
                return null;

            var type = valueObj["$type"]?.ToString() ?? "";
            if (!string.Equals(type, "TReferenceValuePlayerAttribute", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!string.Equals(valueObj["AttributeType"]?.ToString(), attributeType, StringComparison.OrdinalIgnoreCase))
                return null;

            var modifier = valueObj["Modifier"] as JObject;
            if (!string.Equals(modifier?["ModifyMode"]?.ToString(), "Multiply", StringComparison.OrdinalIgnoreCase))
                return null;

            return ResolveRatioValue(modifier["Value"] as JObject);
        }

        private static double? ResolveRatioValue(JObject valueObj)
        {
            if (valueObj == null)
                return null;

            var type = valueObj["$type"]?.ToString() ?? "";
            switch (type)
            {
                case "TFixedValue":
                    return valueObj["Value"]?.Value<double?>() ?? valueObj["Value"]?.Value<int?>() ?? 0;
                default:
                    return null;
            }
        }

        private static string GetTargetMode(JObject target, string fallback = "opponent")
        {
            if (target == null)
                return fallback;

            var type = target["$type"]?.ToString() ?? "";
            var targetMode = target["TargetMode"]?.ToString() ?? "";
            var section = target["TargetSection"]?.ToString() ?? "";
            var targetSuffix = GetTargetSuffix(target);

            if (type.Contains("CardAdjacent"))
            {
                if (type.Contains("Opponent") || section.Contains("Opponent"))
                    return "adjacent_opponent_cards";
                return "adjacent_self_cards";
            }
            if (type.Contains("CardXMost"))
            {
                var side = section.Contains("Opponent") ? "opponent" : "self";
                var direction = string.Equals(targetMode, "RightMostCard", StringComparison.OrdinalIgnoreCase)
                    ? "rightmost"
                    : "leftmost";
                return $"{direction}_{side}{targetSuffix}_card";
            }
            if (type.Contains("CardSection"))
            {
                if (section.Contains("Opponent"))
                    return $"all_opponent{targetSuffix}_cards";
                return $"all_self{targetSuffix}_cards";
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

        private static string GetTargetSuffix(JObject target)
        {
            var conditions = target?["Conditions"];
            if (conditions == null)
                return string.Empty;

            var requiresWeapon = false;
            var requiresNonWeapon = false;
            ParseTargetConditions(conditions, ref requiresWeapon, ref requiresNonWeapon);
            if (requiresWeapon)
                return "_weapon";
            if (requiresNonWeapon)
                return "_nonweapon";

            return string.Empty;
        }

        private static void ParseTargetConditions(JToken conditionToken, ref bool requiresWeapon, ref bool requiresNonWeapon)
        {
            if (!(conditionToken is JObject condition))
                return;

            var type = condition["$type"]?.ToString() ?? "";
            if (string.Equals(type, "TCardConditionalAnd", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var child in condition["Conditions"] as JArray ?? new JArray())
                    ParseTargetConditions(child, ref requiresWeapon, ref requiresNonWeapon);
                return;
            }

            if (!string.Equals(type, "TCardConditionalTag", StringComparison.OrdinalIgnoreCase))
                return;

            var tags = new List<string>();
            if (condition["Tags"] is JArray tagArray)
                tags.AddRange(tagArray.Values<string>());
            else if (condition["Tags"] != null)
                tags.Add(condition["Tags"]?.ToString());

            if (!tags.Any(tag => string.Equals(tag, "Weapon", StringComparison.OrdinalIgnoreCase)))
                return;

            var op = condition["Operator"]?.ToString() ?? "";
            if (string.Equals(op, "None", StringComparison.OrdinalIgnoreCase))
                requiresNonWeapon = true;
            else
                requiresWeapon = true;
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
            effect.Trigger = SimEffectTriggers.Passive;
            target.Add(effect);
        }
    }
}
