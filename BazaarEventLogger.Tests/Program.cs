using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BazaarEventLogger;

namespace BazaarEventLogger.Tests
{
    internal static class Program
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true,
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && string.Equals(args[0], "offline", StringComparison.OrdinalIgnoreCase))
                {
                    RunOffline(args);
                    return 0;
                }

                RunSmokeTests();
                Console.WriteLine("Simulation smoke tests passed.");
                Console.WriteLine("Offline runner: dotnet run --project BazaarEventLogger.Tests -- offline <DebugExportDir> [runs] [seedBase] [traceSamples]");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void RunOffline(IReadOnlyList<string> args)
        {
            if (args.Count < 2)
                throw new InvalidOperationException("Usage: offline <DebugExportDir> [runs] [seedBase] [traceSamples]");

            var exportDir = Path.GetFullPath(args[1]);
            var playerPath = Path.Combine(exportDir, "player_snapshot.json");
            var encounterPath = Path.Combine(exportDir, "selected_encounter.json");
            if (!Directory.Exists(exportDir))
                throw new DirectoryNotFoundException(exportDir);
            if (!File.Exists(playerPath))
                throw new FileNotFoundException("player_snapshot.json not found", playerPath);
            if (!File.Exists(encounterPath))
                throw new FileNotFoundException("selected_encounter.json not found", encounterPath);

            var runs = args.Count > 2 && int.TryParse(args[2], out var parsedRuns) ? parsedRuns : 10;
            var seedBase = args.Count > 3 && int.TryParse(args[3], out var parsedSeed) ? parsedSeed : 1337;
            var traceSamples = args.Count > 4 && int.TryParse(args[4], out var parsedTrace) ? parsedTrace : 1;

            var player = LoadJson<SimCombatantSnapshot>(playerPath);
            var selected = LoadJson<OfflineEncounterSelection>(encounterPath);
            if (player == null)
                throw new InvalidOperationException("Failed to deserialize player snapshot.");
            if (selected?.EncounterSnapshot?.Opponent == null)
                throw new InvalidOperationException("Failed to deserialize selected encounter snapshot.");

            CardDatabase.Load(ResolveCardsJsonPath(exportDir));
            RefreshSnapshotFromTemplates(player);
            RefreshSnapshotFromTemplates(selected.EncounterSnapshot.Opponent);

            var options = new SimulationBatchOptions
            {
                Runs = Math.Max(1, runs),
                SeedBase = seedBase,
                TraceSamples = Math.Max(0, traceSamples)
            };

            var result = SimulationEngine.RunBatch(player, selected.EncounterSnapshot, options);
            var summary = SimulationReporter.FormatPredictionReport(player, new[] { result });
            var outputDir = ResolveOutputDirectory(exportDir);
            Directory.CreateDirectory(outputDir);

            var summaryPath = Path.Combine(outputDir, "offline_summary.txt");
            File.WriteAllText(summaryPath, summary);

            string tracePath = null;
            if (result.TraceSample != null)
            {
                tracePath = Path.Combine(outputDir, "offline_trace.txt");
                var traceText = SimulationReporter.FormatSimulationTrace(
                    selected.EncounterName ?? selected.EncounterSnapshot.EncounterName ?? "Encounter",
                    result.MonsterName,
                    result.TraceSample);
                File.WriteAllText(tracePath, traceText);
            }

            var resultPath = Path.Combine(outputDir, "offline_result.json");
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result, JsonOptions));

            Console.WriteLine($"Offline simulation complete: {selected.EncounterName}");
            Console.WriteLine($"WinRate={result.WinRate:P1} Wins={result.Wins}/{result.Runs} AvgHP={result.AveragePlayerHealthRemaining:F1} Median={result.MedianDurationSeconds:F1}s Verdict={result.Verdict}");
            Console.WriteLine($"Summary: {summaryPath}");
            Console.WriteLine($"Result: {resultPath}");
            if (!string.IsNullOrEmpty(tracePath))
                Console.WriteLine($"Trace: {tracePath}");
        }

        private static string ResolveOutputDirectory(string exportDir)
        {
            try
            {
                var probePath = Path.Combine(exportDir, ".offline_write_test");
                File.WriteAllText(probePath, "ok");
                File.Delete(probePath);
                return exportDir;
            }
            catch
            {
                var fallbackRoot = Path.Combine(AppContext.BaseDirectory, "offline-output");
                var exportName = SanitizePathPart(new DirectoryInfo(exportDir).Name);
                return Path.Combine(fallbackRoot, exportName);
            }
        }

        private static string ResolveCardsJsonPath(string exportDir)
        {
            for (var dir = new DirectoryInfo(exportDir); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "TheBazaar_Data", "StreamingAssets", "cards.json");
                if (File.Exists(candidate))
                    return candidate;
            }

            throw new FileNotFoundException("Unable to locate The Bazaar cards.json from export directory.", exportDir);
        }

        private static void RefreshSnapshotFromTemplates(SimCombatantSnapshot snapshot)
        {
            if (snapshot?.Cards == null)
                return;

            foreach (var card in snapshot.Cards)
            {
                if (string.IsNullOrWhiteSpace(card.TemplateId))
                    continue;

                var info = CardDatabase.GetInfo(card.TemplateId);
                if (info == null)
                    continue;

                var tier = string.IsNullOrWhiteSpace(card.Tier) ? info.StartingTier : card.Tier;
                var runtimeAttrs = card.Attributes == null
                    ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(card.Attributes, StringComparer.OrdinalIgnoreCase);
                var profile = EffectNormalizer.NormalizeCard(info, tier, runtimeAttrs);
                if (profile == null)
                    continue;

                card.Name = info.InternalName ?? card.Name;
                card.CardType = info.Type ?? card.CardType;
                card.Size = string.IsNullOrWhiteSpace(profile.Size) ? card.Size : profile.Size;
                card.Tier = tier;
                card.Tags = info.Tags?.ToList() ?? new List<string>();
                card.CooldownMax = profile.CooldownMax > 0 ? profile.CooldownMax : card.CooldownMax;
                card.CurrentCooldown = Math.Max(0, card.CooldownMax);
                card.Multicast = Math.Max(1, profile.Multicast);
                card.Attributes = profile.Attributes ?? runtimeAttrs;
                card.Effects = profile.Effects ?? new List<SimEffectSpec>();
                card.UnsupportedEffects = profile.UnsupportedEffects ?? new List<string>();
                card.CoverageScore = profile.CoverageScore;
            }

            snapshot.UnsupportedEffects = snapshot.Cards
                .SelectMany(card => card.UnsupportedEffects ?? new List<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string SanitizePathPart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "offline-run";

            foreach (var invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');

            return string.IsNullOrWhiteSpace(value) ? "offline-run" : value;
        }

        private static T LoadJson<T>(string path)
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }

        private static void RunSmokeTests()
        {
            TestDeterministicDamageRace();
            TestBurnPressure();
            TestBatchVerdict();
            TestHasteAndSlowTiming();
            TestFreezeStopsCooldownOnly();
            TestAllTargetBuffAppliesToEveryCard();
            TestPlayerAttributeModificationCanStripShield();
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
                Tags = new List<string>(),
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

        private sealed class OfflineEncounterSelection
        {
            public string EncounterId { get; set; }
            public string EncounterName { get; set; }
            public string MonsterId { get; set; }
            public string MonsterName { get; set; }
            public SimEncounterSnapshot EncounterSnapshot { get; set; }
        }
    }
}
