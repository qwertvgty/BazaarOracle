using System;
using System.Collections.Generic;

namespace BazaarEventLogger.Tests
{
    internal static class Program
    {
        private static void Main()
        {
            TestDeterministicDamageRace();
            TestBurnPressure();
            TestBatchVerdict();
            TestHasteAndSlowTiming();
            TestFreezeStopsCooldownOnly();
            TestAllTargetBuffAppliesToEveryCard();
            TestPlayerAttributeModificationCanStripShield();
            Console.WriteLine("Simulation smoke tests passed.");
        }

        private static void TestDeterministicDamageRace()
        {
            var player = NewCombatant("Player", 100, NewCard("Hit", 1000, "damage", 20));
            var monster = NewCombatant("Monster", 100, NewCard("Bite", 1000, "damage", 10));

            var result = SimulationEngine.RunOnce(player, monster, 42);
            Assert(result.Winner == "Player", "Expected player to win damage race.");
            Assert(result.PlayerHealthRemaining > 0, "Expected player to survive.");
        }

        private static void TestBurnPressure()
        {
            var player = NewCombatant("Player", 80, NewCard("Spark", 1200, "damage", 8));
            var monster = NewCombatant("Monster", 80, NewCard("Fire", 1000, "burn_apply", 6));

            var result = SimulationEngine.RunOnce(player, monster, 42);
            Assert(result.Winner == "Opponent", "Expected burn-focused monster to win.");
        }

        private static void TestBatchVerdict()
        {
            var player = NewCombatant("Player", 120, NewCard("Sword", 1000, "damage", 25));
            var monster = NewCombatant("Monster", 90, NewCard("Claw", 1000, "damage", 8));
            var encounter = new SimEncounterSnapshot
            {
                EncounterName = "Dummy Encounter",
                MonsterId = "dummy",
                Opponent = monster
            };

            var batch = SimulationEngine.RunBatch(player, encounter, new SimulationBatchOptions { Runs = 10, SeedBase = 99 });
            Assert(batch.Wins == 10, "Expected all batch runs to win.");
            Assert(batch.Verdict == "稳胜", "Expected verdict to be stable win.");
        }

        private static void TestHasteAndSlowTiming()
        {
            var card = NewCard("Clock", 1000, "damage", 10);
            card.HasteDuration = 1000;
            var player = NewCombatant("Player", 100, card);
            var opponent = NewCombatant("Opponent", 40, NewCard("Dummy", 999999, "damage", 1));

            var hasteResult = SimulationEngine.RunOnce(player, opponent, 1);

            var slowCard = NewCard("SlowClock", 1000, "damage", 10);
            slowCard.SlowDuration = 1000;
            var slowPlayer = NewCombatant("Player", 100, slowCard);
            var slowOpponent = NewCombatant("Opponent", 40, NewCard("Dummy", 999999, "damage", 1));
            var slowResult = SimulationEngine.RunOnce(slowPlayer, slowOpponent, 1);
            Assert(hasteResult.DurationMs < slowResult.DurationMs, "Haste should finish faster than slow.");
        }

        private static void TestFreezeStopsCooldownOnly()
        {
            var frozen = NewCard("FrozenClock", 1000, "damage", 20);
            frozen.Freeze = 500;
            frozen.HasteDuration = 500;
            var player = NewCombatant("Player", 100, frozen);
            var opponent = NewCombatant("Opponent", 40, NewCard("Dummy", 999999, "damage", 1));

            var frozenResult = SimulationEngine.RunOnce(player, opponent, 1);

            var hasted = NewCard("HastedClock", 1000, "damage", 20);
            hasted.HasteDuration = 500;
            var hastedResult = SimulationEngine.RunOnce(
                NewCombatant("Player", 100, hasted),
                NewCombatant("Opponent", 40, NewCard("Dummy", 999999, "damage", 1)),
                1);

            Assert(frozenResult.DurationMs > hastedResult.DurationMs, "Freeze should prevent cooldown progress while haste duration still decays.");
        }

        private static void TestAllTargetBuffAppliesToEveryCard()
        {
            var banner = new SimCardSnapshot
            {
                Name = "Banner",
                CooldownMax = 1000,
                CurrentCooldown = 1000,
                Effects = new List<SimEffectSpec>
                {
                    new SimEffectSpec
                    {
                        Type = "buff_DamageAmount",
                        Value = 5,
                        Target = "all_self_cards"
                    }
                }
            };

            var strikerA = NewCard("Striker A", 1000, "damage", 10);
            var strikerB = NewCard("Striker B", 1000, "damage", 10);
            var player = NewCombatant("Player", 100, banner, strikerA, strikerB);
            var opponent = NewCombatant("Opponent", 50, NewCard("Dummy", 999999, "damage", 1));

            var result = SimulationEngine.RunOnce(player, opponent, 7);
            Assert(result.DurationMs < 3000, "Team-wide buff should increase total damage throughput.");
        }

        private static void TestPlayerAttributeModificationCanStripShield()
        {
            var baselinePlayer = NewCombatant("Player", 100, NewCard("Sword", 1000, "damage", 20));
            var baselineOpponent = NewCombatant("Opponent", 60, NewCard("Dummy", 999999, "damage", 1));
            baselineOpponent.Shield = 40;
            var baseline = SimulationEngine.RunOnce(baselinePlayer, baselineOpponent, 1);

            var player = NewCombatant("Player", 100, NewCard("Sword", 1000, "damage", 20));
            var opponent = NewCombatant("Opponent", 60, NewCard("Dummy", 999999, "damage", 1));
            opponent.Shield = 40;
            player.Cards.Add(new SimCardSnapshot
            {
                Name = "Sunder",
                CooldownMax = 1000,
                CurrentCooldown = 1000,
                Effects = new List<SimEffectSpec>
                {
                    new SimEffectSpec
                    {
                        Type = "modify_Shield",
                        Value = -40,
                        Target = "opponent",
                        IsPassive = true
                    }
                }
            });

            var result = SimulationEngine.RunOnce(player, opponent, 1);
            Assert(result.DurationMs < baseline.DurationMs, "Generic player attribute modify should be able to strip opponent shield.");
        }

        private static SimCombatantSnapshot NewCombatant(string name, int health, params SimCardSnapshot[] cards)
        {
            return new SimCombatantSnapshot
            {
                Name = name,
                SourceId = name,
                Health = health,
                HealthMax = health,
                Cards = new List<SimCardSnapshot>(cards)
            };
        }

        private static SimCardSnapshot NewCard(string name, int cooldown, string effectType, int value)
        {
            return new SimCardSnapshot
            {
                Name = name,
                CooldownMax = cooldown,
                CurrentCooldown = cooldown,
                Multicast = 1,
                CoverageScore = 1.0,
                Effects = new List<SimEffectSpec>
                {
                    new SimEffectSpec
                    {
                        Type = effectType,
                        Value = value,
                        Target = effectType == "heal" ? "self" : "opponent"
                    }
                }
            };
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
