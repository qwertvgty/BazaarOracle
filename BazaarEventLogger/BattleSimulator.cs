using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BepInEx;
using Newtonsoft.Json.Linq;

namespace BazaarEventLogger
{
    #region Simulation Models

    public class SimEffect
    {
        public string Type; // damage, heal, shield, burn_apply, poison_apply, haste, slow, freeze
        public int Value;
        public string Target; // self, opponent, self_card, opponent_card
        public bool IsPassive;
    }

    public class SimCard
    {
        public string Name;
        public string TemplateId;
        public int CooldownMax;
        public int CurrentCooldown;
        public int Multicast;
        public int Freeze; // remaining freeze ticks (in ms)
        public List<SimEffect> Effects = new List<SimEffect>();

        public override string ToString() => $"{Name}(CD={CooldownMax}ms, x{Multicast})";
    }

    public class SimPlayer
    {
        public string Name;
        public int Health;
        public int HealthMax;
        public int Shield;
        public int Burn;
        public int Poison;
        public int HealthRegen;
        public int Joy;
        public int Rage;
        public int RageMax;
        public int EnragedDurationMax;
        public int EnragedDuration;
        public bool IsEnraged;
        public List<SimCard> Cards = new List<SimCard>();
    }

    public class BattleResult
    {
        public string Winner; // "Player" or "Opponent"
        public int PlayerHealthRemaining;
        public int OpponentHealthRemaining;
        public int TotalTicks;
        public double DurationSeconds;
        public bool SandstormTriggered;
        public string Summary;
    }

    #endregion

    public static class BattleSimulator
    {
        private const int TICK_MS = 50;
        private const int SANDSTORM_WARNING_MS = 60000; // 60s sandstorm warning
        private const int SANDSTORM_DAMAGE_START_MS = 75000; // 75s sandstorm starts
        private const int MAX_DURATION_MS = 120000; // 2 minute hard cap

        public static string LogFilePath { get; private set; }

        // Monster data loaded from monster_data.json
        private static JObject _monsterData;
        private static Dictionary<string, string> _encounterToMonster = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static void Initialize()
        {
            LogFilePath = Path.Combine(Paths.BepInExRootPath, "BattleSimulator.log");
            var header = $"=== Battle Simulator Started @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}";
            File.AppendAllText(LogFilePath, header);

            LoadMonsterData();
        }

        private static void LoadMonsterData()
        {
            var path = Path.Combine(UnityEngine.Application.streamingAssetsPath, "monster_data.json");
            if (!File.Exists(path))
            {
                Plugin.Log.LogWarning($"monster_data.json not found: {path}");
                return;
            }

            try
            {
                var json = File.ReadAllText(path);
                _monsterData = JObject.Parse(json);

                var mapping = _monsterData["encounterToMonster"] as JObject;
                if (mapping != null)
                {
                    foreach (var kvp in mapping)
                        _encounterToMonster[kvp.Key] = kvp.Value.ToString();
                }

                Plugin.Log.LogInfo($"Loaded {_encounterToMonster.Count} encounter->monster mappings");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to load monster_data.json: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when the player is at the combat selection screen.
        /// Simulates battles against all available monsters.
        /// </summary>
        public static void PredictCombats(GameSim gameState, List<string> encounterInstanceIds)
        {
            if (_monsterData == null) return;

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"╔══════════════════════════════════════════════════════════════");
            sb.AppendLine($"║ BATTLE PREDICTIONS @ {DateTime.Now:HH:mm:ss}");
            sb.AppendLine($"╠══════════════════════════════════════════════════════════════");

            // Build player from current game state
            var player = BuildPlayerFromGameState(gameState);
            sb.AppendLine($"║ Player: HP={player.HealthMax}, Shield={player.Shield}, Cards={player.Cards.Count}");
            foreach (var card in player.Cards)
            {
                var fx = string.Join(", ", card.Effects.Where(e => !e.IsPassive).Select(e => $"{e.Type}={e.Value}"));
                sb.AppendLine($"║   {card.Name}: CD={card.CooldownMax}ms, x{card.Multicast}, [{fx}]");
            }
            sb.AppendLine($"║");

            foreach (var instanceId in encounterInstanceIds)
            {
                // Resolve instance -> template -> monster
                var templateId = "";
                if (CardDatabase.GetInfo(instanceId) != null)
                    templateId = instanceId;
                else
                {
                    // Look up template from instance tracking
                    var info = CardDatabase.GetInfo(instanceId);
                    if (info != null)
                        templateId = info.Id;
                }

                // Try resolving through CardDatabase
                var cardInfo = CardDatabase.GetInfo(instanceId);
                string resolvedTemplateId = cardInfo?.Id ?? instanceId;

                string monsterId = null;
                _encounterToMonster.TryGetValue(resolvedTemplateId, out monsterId);

                var encounterName = CardDatabase.ResolveName(instanceId);

                if (monsterId == null)
                {
                    sb.AppendLine($"║ {encounterName}: ??? (unknown monster)");
                    continue;
                }

                var monsterObj = _monsterData["monsters"]?[monsterId];
                if (monsterObj == null)
                {
                    sb.AppendLine($"║ {encounterName}: ??? (monster data missing)");
                    continue;
                }

                var opponent = BuildMonsterFromData(monsterObj);
                sb.AppendLine($"║ vs {opponent.Name} (HP={opponent.HealthMax}, Cards={opponent.Cards.Count}):");

                // Run simulation
                var result = Simulate(ClonePlayer(player), ClonePlayer(opponent));

                var icon = result.Winner == "Player" ? "WIN" : "LOSE";
                sb.AppendLine($"║   >> [{icon}] {result.Summary}");
                sb.AppendLine($"║");
            }

            sb.AppendLine($"╚══════════════════════════════════════════════════════════════");

            var output = sb.ToString();
            try
            {
                File.AppendAllText(LogFilePath, output);
            }
            catch { }

            Plugin.Log.LogInfo(output);
        }

        #region Build Simulation Models

        private static SimPlayer BuildPlayerFromGameState(GameSim state)
        {
            var player = new SimPlayer { Name = "Player" };

            // Player attributes
            if (state.Player?.Attributes != null)
            {
                var attrs = state.Player.Attributes;
                player.HealthMax = GetAttr(attrs, "HealthMax", 300);
                player.Health = GetAttr(attrs, "Health", player.HealthMax);
                player.Shield = GetAttr(attrs, "Shield", 0);
                player.HealthRegen = GetAttr(attrs, "HealthRegen", 0);
                player.RageMax = GetAttr(attrs, "RageMax", 100);
                player.EnragedDurationMax = GetAttr(attrs, "EnragedDurationMax", 5000);
            }

            // Player cards (only Hand cards, alive)
            if (state.Cards != null)
            {
                foreach (var kvp in state.Cards)
                {
                    var card = kvp.Value;
                    if (card == null) continue;
                    if (card.State != BazaarGameShared.Domain.Core.Types.ECardState.Alive) continue;

                    // Only hand cards participate in combat
                    if (card.Placement?.Section != BazaarGameShared.Domain.Core.Types.EInventorySection.Hand)
                        continue;

                    var simCard = BuildSimCardFromGameState(card);
                    if (simCard != null && simCard.CooldownMax > 0)
                        player.Cards.Add(simCard);
                }
            }

            return player;
        }

        private static SimCard BuildSimCardFromGameState(SimUpdateCard card)
        {
            var simCard = new SimCard
            {
                Name = CardDatabase.ResolveName(card.InstanceId),
                TemplateId = card.InstanceId
            };

            if (card.Attributes == null) return null;

            // Extract key attributes
            foreach (var attr in card.Attributes)
            {
                if (attr.Value.DeltaType != EAttributeDeltaType.Update) continue;
                var val = attr.Value.Value;
                var key = attr.Key.ToString();

                switch (key)
                {
                    case "CooldownMax": simCard.CooldownMax = val; break;
                    case "Multicast": simCard.Multicast = Math.Max(1, val); break;
                }
            }

            if (simCard.CooldownMax <= 0) return null;
            simCard.CurrentCooldown = simCard.CooldownMax;

            // Determine effects from card template abilities
            var cardInfo = CardDatabase.GetInfo(card.InstanceId);
            if (cardInfo != null)
            {
                var effects = GetEffectsFromTemplate(cardInfo.Id, card);
                simCard.Effects.AddRange(effects);
            }

            // If no effects found from template, try to infer from attributes
            if (simCard.Effects.Count == 0)
            {
                InferEffectsFromAttributes(simCard, card);
            }

            return simCard;
        }

        private static List<SimEffect> GetEffectsFromTemplate(string templateId, SimUpdateCard runtimeCard)
        {
            var effects = new List<SimEffect>();

            // Look up card in cards.json via CardDatabase
            // For now, use attribute-based inference since parsing all ability types is complex
            // The monster_data.json has pre-parsed effects, but player cards need runtime inference

            return effects;
        }

        private static void InferEffectsFromAttributes(SimCard simCard, SimUpdateCard card)
        {
            if (card.Attributes == null) return;

            var attrs = new Dictionary<string, int>();
            foreach (var a in card.Attributes)
            {
                if (a.Value.DeltaType == EAttributeDeltaType.Update)
                    attrs[a.Key.ToString()] = a.Value.Value;
            }

            // Infer primary effect from common attribute patterns
            int burnAmount = GetVal(attrs, "BurnApplyAmount");
            int healAmount = GetVal(attrs, "HealAmount");
            int shieldAmount = GetVal(attrs, "ShieldApplyAmount");
            int poisonAmount = GetVal(attrs, "PoisonApplyAmount");
            int custom0 = GetVal(attrs, "Custom_0");
            int regenAmount = GetVal(attrs, "RegenApplyAmount");
            int damageAmount = GetVal(attrs, "DamageAmount");
            int hasteAmount = GetVal(attrs, "HasteAmount");
            int slowAmount = GetVal(attrs, "SlowAmount");
            int freezeAmount = GetVal(attrs, "FreezeAmount");

            // Damage is the most common effect
            if (damageAmount > 0)
                simCard.Effects.Add(new SimEffect { Type = "damage", Value = damageAmount, Target = "opponent" });

            if (burnAmount > 0)
                simCard.Effects.Add(new SimEffect { Type = "burn_apply", Value = burnAmount, Target = "opponent" });

            if (poisonAmount > 0)
                simCard.Effects.Add(new SimEffect { Type = "poison_apply", Value = poisonAmount, Target = "opponent" });

            if (healAmount > 0)
                simCard.Effects.Add(new SimEffect { Type = "heal", Value = healAmount, Target = "self" });

            if (shieldAmount > 0)
                simCard.Effects.Add(new SimEffect { Type = "shield", Value = shieldAmount, Target = "self" });

            if (regenAmount > 0)
                simCard.Effects.Add(new SimEffect { Type = "regen_apply", Value = regenAmount, Target = "self" });

            if (hasteAmount > 0)
                simCard.Effects.Add(new SimEffect { Type = "haste", Value = hasteAmount, Target = "self_card" });

            if (slowAmount > 0)
                simCard.Effects.Add(new SimEffect { Type = "slow", Value = slowAmount, Target = "opponent_card" });

            if (freezeAmount > 0)
                simCard.Effects.Add(new SimEffect { Type = "freeze", Value = freezeAmount, Target = "opponent_card" });

            // Fallback: if no effects found, try Custom_0 as damage or burn
            if (simCard.Effects.Count == 0)
            {
                int heated = GetVal(attrs, "Heated");
                if (heated > 0 && custom0 > 0)
                    simCard.Effects.Add(new SimEffect { Type = "burn_apply", Value = custom0, Target = "opponent" });
                else if (custom0 > 0)
                    simCard.Effects.Add(new SimEffect { Type = "damage", Value = custom0, Target = "opponent" });
            }
        }

        private static SimPlayer BuildMonsterFromData(JToken monsterObj)
        {
            var monster = new SimPlayer
            {
                Name = monsterObj["internalName"]?.ToString() ?? "Monster"
            };

            var playerAttrs = monsterObj["player"]?["attributes"];
            if (playerAttrs != null)
            {
                monster.HealthMax = playerAttrs["HealthMax"]?.Value<int>() ?? 300;
                monster.Health = monster.HealthMax;
                monster.HealthRegen = playerAttrs["HealthRegen"]?.Value<int>() ?? 0;
                monster.RageMax = playerAttrs["RageMax"]?.Value<int>() ?? 100;
                monster.EnragedDurationMax = playerAttrs["EnragedDurationMax"]?.Value<int>() ?? 5000;
            }

            var cards = monsterObj["cards"] as JArray;
            if (cards != null)
            {
                foreach (var cardObj in cards)
                {
                    var simCard = new SimCard
                    {
                        Name = cardObj["name"]?.ToString() ?? "?",
                        TemplateId = cardObj["templateId"]?.ToString() ?? "",
                        CooldownMax = cardObj["cooldownMax"]?.Value<int>() ?? 0,
                        Multicast = Math.Max(1, cardObj["multicast"]?.Value<int>() ?? 1),
                    };

                    if (simCard.CooldownMax <= 0) continue;
                    simCard.CurrentCooldown = simCard.CooldownMax;

                    // Load pre-parsed effects
                    var effects = cardObj["effects"] as JArray;
                    if (effects != null)
                    {
                        foreach (var eff in effects)
                        {
                            var passive = eff["passive"]?.Value<bool>() ?? false;
                            if (passive) continue;

                            simCard.Effects.Add(new SimEffect
                            {
                                Type = eff["type"]?.ToString() ?? "unknown",
                                Value = (int)(eff["value"]?.Value<double>() ?? 0),
                                Target = eff["target"]?.ToString() ?? "opponent",
                                IsPassive = passive
                            });
                        }
                    }

                    // Fallback: infer from attributes
                    if (simCard.Effects.Count == 0)
                    {
                        var tierAttrs = cardObj["attributes"] as JObject;
                        if (tierAttrs != null)
                        {
                            InferEffectsFromMonsterAttrs(simCard, tierAttrs);
                        }
                    }

                    monster.Cards.Add(simCard);
                }
            }

            return monster;
        }

        private static void InferEffectsFromMonsterAttrs(SimCard card, JObject attrs)
        {
            int burn = attrs["BurnApplyAmount"]?.Value<int>() ?? 0;
            int heal = attrs["HealAmount"]?.Value<int>() ?? 0;
            int shield = attrs["ShieldApplyAmount"]?.Value<int>() ?? 0;
            int poison = attrs["PoisonApplyAmount"]?.Value<int>() ?? 0;
            int custom0 = attrs["Custom_0"]?.Value<int>() ?? 0;
            int regen = attrs["RegenApplyAmount"]?.Value<int>() ?? 0;
            int damageAmount = attrs["DamageAmount"]?.Value<int>() ?? 0;
            int hasteAmount = attrs["HasteAmount"]?.Value<int>() ?? 0;
            int slowAmount = attrs["SlowAmount"]?.Value<int>() ?? 0;
            int freezeAmount = attrs["FreezeAmount"]?.Value<int>() ?? 0;

            // Damage is the most common effect
            if (damageAmount > 0) card.Effects.Add(new SimEffect { Type = "damage", Value = damageAmount, Target = "opponent" });
            if (burn > 0) card.Effects.Add(new SimEffect { Type = "burn_apply", Value = burn, Target = "opponent" });
            if (poison > 0) card.Effects.Add(new SimEffect { Type = "poison_apply", Value = poison, Target = "opponent" });
            if (heal > 0) card.Effects.Add(new SimEffect { Type = "heal", Value = heal, Target = "self" });
            if (shield > 0) card.Effects.Add(new SimEffect { Type = "shield", Value = shield, Target = "self" });
            if (regen > 0) card.Effects.Add(new SimEffect { Type = "regen_apply", Value = regen, Target = "self" });
            if (hasteAmount > 0) card.Effects.Add(new SimEffect { Type = "haste", Value = hasteAmount, Target = "self_card" });
            if (slowAmount > 0) card.Effects.Add(new SimEffect { Type = "slow", Value = slowAmount, Target = "opponent_card" });
            if (freezeAmount > 0) card.Effects.Add(new SimEffect { Type = "freeze", Value = freezeAmount, Target = "opponent_card" });

            if (card.Effects.Count == 0 && custom0 > 0)
                card.Effects.Add(new SimEffect { Type = "damage", Value = custom0, Target = "opponent" });
        }

        #endregion

        #region Core Simulation

        public static BattleResult Simulate(SimPlayer player, SimPlayer opponent)
        {
            int tick = 0;
            int sandstormDamage = 0;

            while (player.Health > 0 && opponent.Health > 0 && tick < MAX_DURATION_MS)
            {
                tick += TICK_MS;

                // Burn DOT (each tick, burn deals its value as damage then decays by 1)
                ProcessDot(ref player.Health, ref player.Shield, ref player.Burn);
                ProcessDot(ref opponent.Health, ref opponent.Shield, ref opponent.Burn);

                // Poison DOT (poison deals damage but doesn't decay)
                if (player.Poison > 0) ApplyDamage(ref player.Health, ref player.Shield, player.Poison);
                if (opponent.Poison > 0) ApplyDamage(ref opponent.Health, ref opponent.Shield, opponent.Poison);

                // Health regen
                if (player.HealthRegen > 0 && tick % 1000 == 0)
                    player.Health = Math.Min(player.HealthMax, player.Health + player.HealthRegen);
                if (opponent.HealthRegen > 0 && tick % 1000 == 0)
                    opponent.Health = Math.Min(opponent.HealthMax, opponent.Health + opponent.HealthRegen);

                // Sandstorm damage after 75s
                if (tick >= SANDSTORM_DAMAGE_START_MS)
                {
                    sandstormDamage = (tick - SANDSTORM_DAMAGE_START_MS) / 1000 + 1;
                    if (tick % 1000 == 0)
                    {
                        ApplyDamage(ref player.Health, ref player.Shield, sandstormDamage * 50);
                        ApplyDamage(ref opponent.Health, ref opponent.Shield, sandstormDamage * 50);
                    }
                }

                if (player.Health <= 0 || opponent.Health <= 0) break;

                // Process player cards
                ProcessCards(player.Cards, player, opponent, tick);
                // Process opponent cards
                ProcessCards(opponent.Cards, opponent, player, tick);

                if (player.Health <= 0 || opponent.Health <= 0) break;
            }

            var result = new BattleResult
            {
                Winner = player.Health > opponent.Health ? "Player" : "Opponent",
                PlayerHealthRemaining = Math.Max(0, player.Health),
                OpponentHealthRemaining = Math.Max(0, opponent.Health),
                TotalTicks = tick / TICK_MS,
                DurationSeconds = tick / 1000.0,
                SandstormTriggered = tick >= SANDSTORM_DAMAGE_START_MS
            };

            var winLose = result.Winner == "Player" ? "WIN" : "LOSE";
            result.Summary = $"{winLose} in {result.DurationSeconds:F1}s | " +
                           $"Player HP: {result.PlayerHealthRemaining}/{player.HealthMax} | " +
                           $"Monster HP: {result.OpponentHealthRemaining}/{opponent.HealthMax}" +
                           (result.SandstormTriggered ? " [SANDSTORM]" : "");

            return result;
        }

        private static void ProcessCards(List<SimCard> cards, SimPlayer owner, SimPlayer target, int tick)
        {
            foreach (var card in cards)
            {
                // Handle freeze
                if (card.Freeze > 0)
                {
                    card.Freeze -= TICK_MS;
                    continue;
                }

                card.CurrentCooldown -= TICK_MS;

                if (card.CurrentCooldown <= 0)
                {
                    // Card triggers!
                    for (int i = 0; i < card.Multicast; i++)
                    {
                        ExecuteCardEffects(card, owner, target);
                    }
                    card.CurrentCooldown = card.CooldownMax;
                }
            }
        }

        private static void ExecuteCardEffects(SimCard card, SimPlayer owner, SimPlayer target)
        {
            foreach (var effect in card.Effects)
            {
                var effectTarget = effect.Target == "opponent" ? target : owner;
                var effectOwner = effect.Target == "opponent" ? owner : effectTarget;

                switch (effect.Type)
                {
                    case "damage":
                        ApplyDamage(ref target.Health, ref target.Shield, effect.Value);
                        break;

                    case "heal":
                        owner.Health = Math.Min(owner.HealthMax, owner.Health + effect.Value);
                        break;

                    case "shield":
                        owner.Shield += effect.Value;
                        break;

                    case "burn_apply":
                        target.Burn += effect.Value;
                        break;

                    case "poison_apply":
                        target.Poison += effect.Value;
                        break;

                    case "regen_apply":
                        owner.HealthRegen += effect.Value;
                        break;

                    case "haste":
                        // Reduce a friendly card's cooldown
                        var friendlyCards = owner.Cards.Where(c => c != card && c.CurrentCooldown > 0).ToList();
                        if (friendlyCards.Count > 0)
                        {
                            var fastest = friendlyCards.OrderBy(c => c.CurrentCooldown).First();
                            fastest.CurrentCooldown = Math.Max(0, fastest.CurrentCooldown - effect.Value);
                        }
                        break;

                    case "slow":
                        var enemyCards = target.Cards.Where(c => c.CooldownMax > 0).ToList();
                        if (enemyCards.Count > 0)
                        {
                            var targetCard = enemyCards[new Random().Next(enemyCards.Count)];
                            targetCard.CurrentCooldown += effect.Value;
                        }
                        break;

                    case "freeze":
                        var freezeTargets = target.Cards.Where(c => c.Freeze <= 0 && c.CooldownMax > 0).ToList();
                        if (freezeTargets.Count > 0)
                        {
                            var targetCard = freezeTargets[new Random().Next(freezeTargets.Count)];
                            targetCard.Freeze += effect.Value;
                        }
                        break;

                    case "modify_HealthMax":
                        owner.HealthMax += effect.Value;
                        owner.Health += effect.Value;
                        break;

                    case "modify_HealthRegen":
                        owner.HealthRegen += effect.Value;
                        break;

                    case "joy":
                        owner.Joy += effect.Value;
                        break;
                }
            }
        }

        private static void ProcessDot(ref int health, ref int shield, ref int dot)
        {
            if (dot > 0)
            {
                ApplyDamage(ref health, ref shield, dot);
                dot = Math.Max(0, dot - 1);
            }
        }

        private static void ApplyDamage(ref int health, ref int shield, int damage)
        {
            if (shield > 0)
            {
                if (shield >= damage)
                {
                    shield -= damage;
                    return;
                }
                damage -= shield;
                shield = 0;
            }
            health -= damage;
        }

        #endregion

        #region Helpers

        private static SimPlayer ClonePlayer(SimPlayer p)
        {
            return new SimPlayer
            {
                Name = p.Name,
                Health = p.Health,
                HealthMax = p.HealthMax,
                Shield = p.Shield,
                Burn = p.Burn,
                Poison = p.Poison,
                HealthRegen = p.HealthRegen,
                Joy = p.Joy,
                Rage = p.Rage,
                RageMax = p.RageMax,
                EnragedDurationMax = p.EnragedDurationMax,
                EnragedDuration = p.EnragedDuration,
                Cards = p.Cards.Select(c => new SimCard
                {
                    Name = c.Name,
                    TemplateId = c.TemplateId,
                    CooldownMax = c.CooldownMax,
                    CurrentCooldown = c.CooldownMax,
                    Multicast = c.Multicast,
                    Freeze = 0,
                    Effects = c.Effects.ToList()
                }).ToList()
            };
        }

        private static int GetAttr(Dictionary<BazaarGameShared.Domain.Core.Types.EPlayerAttributeType, int> attrs,
            string name, int defaultVal)
        {
            foreach (var kvp in attrs)
            {
                if (kvp.Key.ToString() == name)
                    return kvp.Value;
            }
            return defaultVal;
        }

        private static int GetVal(Dictionary<string, int> attrs, string key)
        {
            return attrs.TryGetValue(key, out var val) ? val : 0;
        }

        #endregion
    }
}
