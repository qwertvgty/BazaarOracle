using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BazaarEventLogger;
using Newtonsoft.Json.Linq;

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

            string jsonlPath = null;
            if (result.TraceSample != null)
            {
                jsonlPath = Path.Combine(outputDir, "offline_trace.jsonl");
                var encName = selected.EncounterName ?? selected.EncounterSnapshot.EncounterName ?? "Encounter";
                var jsonlLine = SimulationJsonlWriter.WriteTraceJsonl(
                    result.TraceSample, encName, result.MonsterName, options.SeedBase);
                File.WriteAllText(jsonlPath, jsonlLine + Environment.NewLine);
            }

            Console.WriteLine($"Offline simulation complete: {selected.EncounterName}");
            Console.WriteLine($"WinRate={result.WinRate:P1} Wins={result.Wins}/{result.Runs} AvgHP={result.AveragePlayerHealthRemaining:F1} Median={result.MedianDurationSeconds:F1}s Verdict={result.Verdict}");
            Console.WriteLine($"Summary: {summaryPath}");
            Console.WriteLine($"Result: {resultPath}");
            if (!string.IsNullOrEmpty(tracePath))
                Console.WriteLine($"Trace: {tracePath}");
            if (!string.IsNullOrEmpty(jsonlPath))
                Console.WriteLine($"JSONL: {jsonlPath}");
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
                card.AmmoMax = profile.AmmoMax;
                card.CurrentAmmo = profile.AmmoMax;
                card.Attributes = profile.Attributes ?? runtimeAttrs;
                card.Effects = profile.Effects ?? new List<SimEffectSpec>();
                card.UnsupportedEffects = profile.UnsupportedEffects ?? new List<string>();
                card.CoverageNotes = SimulationCoverageAnalyzer.AnalyzeCard(card);
                card.CoverageScore = profile.CoverageScore;
            }

            snapshot.UnsupportedEffects = snapshot.Cards
                .SelectMany(card => card.UnsupportedEffects ?? new List<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            snapshot.CoverageNotes = snapshot.Cards
                .SelectMany(card => card.CoverageNotes ?? new List<string>())
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
            TestDisabledCardDoesNotReTrigger();
            TestFreezeTargetBuffIncreasesFrozenCards();
            TestPositionalRightCardTargetsNeighbor();
            TestAllTargetBuffAppliesToEveryCard();
            TestPlayerAttributeModificationCanStripShield();
            TestJsonlWriterProducesValidOutput();
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

        private static void TestDisabledCardDoesNotReTrigger()
        {
            var fireBomb = new SimCardSnapshot
            {
                Name = "Fire Bomb",
                CooldownMax = 1000,
                CurrentCooldown = 1000,
                Tags = new List<string>(),
                Multicast = 1,
                CoverageScore = 1.0,
                Effects = new List<SimEffectSpec>
                {
                    new SimEffectSpec
                    {
                        Type = "burn_apply",
                        Value = 5,
                        Target = "opponent"
                    },
                    new SimEffectSpec
                    {
                        Type = "disable",
                        Value = 1,
                        Target = "self_card"
                    }
                }
            };

            var player = NewCombatant("Player", 100, NewCard("Sword", 5000, "damage", 50));
            var opponent = NewCombatant("Opponent", 100, fireBomb);

            var result = SimulationEngine.RunOnce(player, opponent, 7);
            var burnEvents = result.Trace
                .SelectMany(entry => entry.Events)
                .Count(evt => evt.Contains("Opponent:Fire Bomb:burn_apply=5->opponent"));
            Assert(burnEvents == 1, $"Disabled Fire Bomb should only trigger once. Actual triggers: {burnEvents}");
        }

        private static void TestFreezeTargetBuffIncreasesFrozenCards()
        {
            var snowWisp = new SimCardSnapshot
            {
                Name = "Snow Wisp",
                CooldownMax = 6000,
                CurrentCooldown = 6000,
                Tags = new List<string>(),
                Multicast = 1,
                CoverageScore = 1.0,
                Effects = new List<SimEffectSpec>
                {
                    new SimEffectSpec
                    {
                        Type = "freeze",
                        Value = 1000,
                        Target = "random_opponent_card",
                        TargetCount = 1
                    },
                    new SimEffectSpec
                    {
                        Type = "buff_FreezeTargets",
                        Value = 1,
                        Target = "self_card",
                        Trigger = SimEffectTriggers.OnPlayerEnraged
                    }
                }
            };

            var rageDriver = NewCard("Pebble", 1000, "damage", 1);
            rageDriver.Size = "Small";
            var rage = new SimCardSnapshot
            {
                Name = "Karnok's Rage",
                CooldownMax = 0,
                CurrentCooldown = 0,
                Size = "Medium",
                Multicast = 1,
                CoverageScore = 1.0,
                Effects = new List<SimEffectSpec>
                {
                    new SimEffectSpec
                    {
                        Type = "rage",
                        Value = 100,
                        Target = "self",
                        Trigger = SimEffectTriggers.OnItemUsed,
                        TriggerCardSizes = new List<string> { "Small" }
                    }
                }
            };

            var player = NewCombatant("Player", 100, snowWisp, rageDriver, rage);
            player.RageMax = 100;
            player.EnragedDurationMax = 5000;

            var opponent = NewCombatant(
                "Opponent",
                100,
                NewCard("Dummy A", 999999, "damage", 1),
                NewCard("Dummy B", 999999, "damage", 1),
                NewCard("Dummy C", 999999, "damage", 1));

            var result = SimulationEngine.RunOnce(player, opponent, 7);
            var snowEntry = result.Trace.FirstOrDefault(entry =>
                entry.Events.Any(evt => evt.Contains("Player:trigger:Snow Wisp")));
            Assert(snowEntry != null, "Expected Snow Wisp to trigger during the test.");

            var frozenCards = snowEntry.CardStates.Count(state =>
                state.StartsWith("O:", StringComparison.Ordinal) &&
                (state.Contains("Freeze=1000", StringComparison.Ordinal) ||
                 state.Contains("Freeze=950", StringComparison.Ordinal)));
            Assert(frozenCards >= 2, $"Expected FreezeTargets buff to freeze at least 2 opponent cards. Actual frozen cards: {frozenCards}");
        }

        private static void TestPositionalRightCardTargetsNeighbor()
        {
            var squirrel = new SimCardSnapshot
            {
                Name = "Flying Squirrel",
                CooldownMax = 1000,
                CurrentCooldown = 1000,
                Tags = new List<string>(),
                Multicast = 1,
                CoverageScore = 1.0,
                Effects = new List<SimEffectSpec>
                {
                    new SimEffectSpec
                    {
                        Type = "haste",
                        Value = 1000,
                        Target = "right_self_card"
                    }
                }
            };

            var spear = NewCard("Spear", 4000, "damage", 20);
            var player = NewCombatant("Player", 100, squirrel, spear);
            var opponent = NewCombatant("Opponent", 100, NewCard("Dummy", 999999, "damage", 1));

            var result = SimulationEngine.RunOnce(player, opponent, 3);
            var firstTrigger = result.Trace.FirstOrDefault(entry =>
                entry.Events.Any(evt => evt.Contains("Player:trigger:Flying Squirrel")));
            Assert(firstTrigger != null, "Expected Flying Squirrel to trigger.");

            var spearState = firstTrigger.CardStates.FirstOrDefault(state => state.StartsWith("P:Spear:", StringComparison.Ordinal));
            Assert(spearState != null && (spearState.Contains("Haste=1000", StringComparison.Ordinal) || spearState.Contains("Haste=950", StringComparison.Ordinal)),
                $"Expected right neighbor Spear to receive haste. Actual state: {spearState}");
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

        private static void TestNormalizerSupportsCardCountScaledPassiveAuras()
        {
            var rawTemplate = JObject.Parse(@"
{
  'Tiers': {
    'Silver': {
      'Attributes': {
        'Custom_0': 10,
        'Custom_1': 10
      },
      'AuraIds': ['0', '1']
    }
  },
  'Abilities': {},
  'Auras': {
    '0': {
      'Action': {
        '$type': 'TAuraActionPlayerModifyAttribute',
        'AttributeType': 'HealthRegen',
        'Operation': 'Add',
        'Value': {
          '$type': 'TReferenceValueCardAttribute',
          'Target': {
            '$type': 'TTargetCardSelf',
            'Conditions': null
          },
          'AttributeType': 'RegenApplyAmount',
          'DefaultValue': 0
        },
        'Target': {
          '$type': 'TTargetPlayerRelative',
          'TargetMode': 'Self',
          'Conditions': null
        }
      }
    },
    '1': {
      'Action': {
        '$type': 'TAuraActionCardModifyAttribute',
        'AttributeType': 'RegenApplyAmount',
        'Operation': 'Add',
        'Value': {
          '$type': 'TReferenceValueCardCount',
          'Target': {
            '$type': 'TTargetCardSection',
            'TargetSection': 'SelfHand',
            'ExcludeSelf': false,
            'Conditions': {
              '$type': 'TCardConditionalTag',
              'Tags': ['Weapon'],
              'Operator': 'None'
            }
          },
          'DefaultValue': 0,
          'Modifier': {
            'ModifyMode': 'Multiply',
            'Value': {
              '$type': 'TReferenceValueCardAttribute',
              'Target': {
                '$type': 'TTargetCardSelf',
                'Conditions': null
              },
              'AttributeType': 'Custom_0',
              'DefaultValue': 0
            },
            'ShouldRound': true
          }
        },
        'Target': {
          '$type': 'TTargetCardSelf',
          'Conditions': null
        }
      }
    }
  }
}");

            var info = new CardInfo
            {
                Id = "waters",
                InternalName = "Waters of Infinity",
                Type = "Skill",
                StartingTier = "Silver",
                Size = "Medium",
                RawTemplate = rawTemplate
            };
            info.TierAttributes["Silver"] = new Dictionary<string, string>
            {
                ["Custom_0"] = "10",
                ["Custom_1"] = "10"
            };

            var profile = EffectNormalizer.NormalizeCard(info, "Silver");
            var regenAura = profile.Effects.FirstOrDefault(effect => effect.Type == "modify_HealthRegen");
            var scalingAura = profile.Effects.FirstOrDefault(effect => effect.Type == "buff_RegenApplyAmount");

            Assert(regenAura != null, "Expected Waters of Infinity to produce a passive HealthRegen modifier.");
            Assert(regenAura.DynamicValueSourceAttribute == "RegenApplyAmount", "Expected HealthRegen aura to resolve from RegenApplyAmount at runtime.");
            Assert(scalingAura != null, "Expected Waters of Infinity to produce a passive RegenApplyAmount buff.");
            Assert(scalingAura.DynamicCountScope == "all_self_nonweapon_cards", "Expected Regen aura to scale with self non-weapon card count.");
            Assert(scalingAura.DynamicCountSourceAttribute == "Custom_0", "Expected Regen aura to scale with the source card Custom_0 attribute.");
            Assert(profile.UnsupportedEffects.Count == 0, "Expected Waters of Infinity auras to be fully normalized.");
        }

        private static void TestPassiveCardCountScaledRegenAuraAppliesBeforeCombat()
        {
            var waters = new SimCardSnapshot
            {
                Name = "Waters of Infinity",
                Tags = new List<string>(),
                Attributes = new Dictionary<string, int>
                {
                    ["Custom_0"] = 10,
                    ["RegenApplyAmount"] = 0
                },
                Effects = new List<SimEffectSpec>
                {
                    new SimEffectSpec
                    {
                        Type = "buff_RegenApplyAmount",
                        Target = "self_card",
                        IsPassive = true,
                        Trigger = SimEffectTriggers.Passive,
                        DynamicCountScope = "all_self_nonweapon_cards",
                        DynamicCountSourceAttribute = "Custom_0"
                    },
                    new SimEffectSpec
                    {
                        Type = "modify_HealthRegen",
                        Target = "self",
                        IsPassive = true,
                        Trigger = SimEffectTriggers.Passive,
                        DynamicValueSourceAttribute = "RegenApplyAmount"
                    }
                }
            };

            var player = NewCombatant(
                "Player",
                100,
                waters,
                new SimCardSnapshot { Name = "Relic A", Tags = new List<string>() },
                new SimCardSnapshot { Name = "Relic B", Tags = new List<string>() },
                new SimCardSnapshot { Name = "Relic C", Tags = new List<string>() },
                new SimCardSnapshot { Name = "Relic D", Tags = new List<string>() },
                NewWeaponCard("Sword", 1000, "damage", 10));

            var opponent = NewCombatant("Opponent", 5, NewCard("Dummy", 999999, "damage", 1));
            var result = SimulationEngine.RunOnce(player, opponent, 1);

            Assert(result.Trace.Count > 0, "Expected trace output for passive aura test.");
            Assert(
                result.Trace[0].PlayerState.Contains("Regen=50"),
                $"Expected Waters of Infinity passive aura to grant 50 regen before the first tick. Actual: {result.Trace[0].PlayerState}");
        }

        private static void TestFightEndedEffectsDoNotApplyDuringCombat()
        {
            var baselinePlayer = NewCombatant("Player", 100, NewWeaponCard("Sword", 1000, "damage", 20));
            var baselineOpponent = NewCombatant("Opponent", 60, NewWeaponCard("Dummy", 999999, "damage", 1));
            var baseline = SimulationEngine.RunOnce(baselinePlayer, baselineOpponent, 3);

            var player = NewCombatant("Player", 100, NewWeaponCard("Sword", 1000, "damage", 20));
            player.Cards.Add(new SimCardSnapshot
            {
                Name = "Afterparty",
                Effects = new List<SimEffectSpec>
                {
                    new SimEffectSpec
                    {
                        Type = "buff_DamageAmount",
                        Value = 100,
                        Target = "all_self_weapon_cards",
                        Trigger = SimEffectTriggers.OnFightEnded
                    }
                }
            });

            var opponent = NewCombatant("Opponent", 60, NewWeaponCard("Dummy", 999999, "damage", 1));
            var result = SimulationEngine.RunOnce(player, opponent, 3);
            Assert(result.DurationMs == baseline.DurationMs, "Fight-ended effects should not change the current combat.");
        }

        private static void TestFightStartedRandomTargetCountCanAffectMultipleCards()
        {
            var player = NewCombatant(
                "Player",
                100,
                NewWeaponCard("A", 4000, "damage", 10),
                NewWeaponCard("B", 4000, "damage", 10),
                NewWeaponCard("C", 4000, "damage", 10));

            player.Cards.Add(new SimCardSnapshot
            {
                Name = "Starter",
                Effects = new List<SimEffectSpec>
                {
                    new SimEffectSpec
                    {
                        Type = "haste",
                        Value = 1000,
                        Target = "random_self_card",
                        TargetCount = 2,
                        Trigger = SimEffectTriggers.OnFightStarted
                    }
                }
            });

            var opponent = NewCombatant("Opponent", 200, NewWeaponCard("Dummy", 999999, "damage", 1));
            var result = SimulationEngine.RunOnce(player, opponent, 5);
            var hastedCount = result.Trace[0].CardStates.Count(state => state.StartsWith("P:") && state.Contains("Haste=1000"));
            Assert(hastedCount == 2, $"Expected 2 cards to be hasted before combat. Actual count: {hastedCount}");
        }

        private static void TestItemUsedEffectsCanBuffTriggerNeighbors()
        {
            var left = NewWeaponCard("Left Blade", 2000, "damage", 10);
            var center = NewWeaponCard("Great Axe", 1000, "damage", 1);
            center.Size = "Large";
            var right = NewWeaponCard("Right Blade", 2000, "damage", 10);

            var baselinePlayer = NewCombatant("Player", 100, left, center, right);
            var baselineOpponent = NewCombatant("Opponent", 221, NewWeaponCard("Dummy", 999999, "damage", 1));
            var baseline = SimulationEngine.RunOnce(baselinePlayer, baselineOpponent, 7);

            left = NewWeaponCard("Left Blade", 2000, "damage", 10);
            center = NewWeaponCard("Great Axe", 1000, "damage", 1);
            center.Size = "Large";
            right = NewWeaponCard("Right Blade", 2000, "damage", 10);

            var player = NewCombatant("Player", 100, left, center, right);
            player.Cards.Add(new SimCardSnapshot
            {
                Name = "Flank Test",
                Effects = new List<SimEffectSpec>
                {
                    new SimEffectSpec
                    {
                        Type = "buff_DamageAmount",
                        Value = 100,
                        Target = "adjacent_self_cards",
                        Trigger = SimEffectTriggers.OnItemUsed,
                        TriggerCardSizes = new List<string> { "Large" },
                        UseTriggerSourceForTargeting = true
                    }
                }
            });

            var opponent = NewCombatant("Opponent", 221, NewWeaponCard("Dummy", 999999, "damage", 1));
            var result = SimulationEngine.RunOnce(player, opponent, 7);
            Assert(result.DurationMs < baseline.DurationMs, "Neighbor-targeted item-used effects should buff cards adjacent to the triggering item.");
        }

        private static void TestItemUsedEffectsResolveNextTick()
        {
            var trigger = NewWeaponCard("Trigger", 1000, "damage", 1);
            trigger.Size = "Large";
            var followUp = NewWeaponCard("Follow Up", 1050, "damage", 40);
            var player = NewCombatant("Player", 100, trigger, followUp);
            player.Cards.Add(new SimCardSnapshot
            {
                Name = "Delay Test",
                Effects = new List<SimEffectSpec>
                {
                    new SimEffectSpec
                    {
                        Type = "haste",
                        Value = 1000,
                        Target = "right_self_card",
                        Trigger = SimEffectTriggers.OnItemUsed,
                        TriggerCardSizes = new List<string> { "Large" },
                        UseTriggerSourceForTargeting = true
                    }
                }
            });

            var opponent = NewCombatant("Opponent", 100, NewWeaponCard("Dummy", 999999, "damage", 1));
            var result = SimulationEngine.RunOnce(player, opponent, 11);

            var triggerTick = result.Trace.FirstOrDefault(entry =>
                entry.Summary.Contains("Player:trigger:Trigger", StringComparison.Ordinal));
            Assert(triggerTick != null, "Expected Trigger to fire.");
            Assert(
                !triggerTick.Events.Any(evt => evt.Contains("Delay Test:haste=1000->right_self_card", StringComparison.Ordinal)),
                "Item-used effect should not resolve in the same tick as the triggering card.");

            var delayedTick = result.Trace.FirstOrDefault(entry =>
                entry.Summary.Contains("Delay Test:haste=1000->right_self_card", StringComparison.Ordinal));
            Assert(delayedTick != null, "Expected delayed item-used effect to resolve on a later tick.");
            Assert(delayedTick.TimeMs == triggerTick.TimeMs + 50, $"Expected delayed item-used effect on next tick. Trigger={triggerTick.TimeMs}, delayed={delayedTick.TimeMs}");
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

        private static SimCardSnapshot NewWeaponCard(string name, int cooldown, string effectType, int value)
        {
            var card = NewCard(name, cooldown, effectType, value);
            card.Tags = new List<string> { "Weapon" };
            card.CardType = "Item";
            return card;
        }

        private static void TestJsonlWriterProducesValidOutput()
        {
            var player = NewCombatant("Player", 100, NewCard("Sword", 1000, "damage", 20));
            var monster = NewCombatant("Monster", 80,
                NewCard("Burn", 1000, "burn_apply", 10),
                NewCard("Heal", 2000, "heal", 15));
            monster.Cards[1].Effects[0].Target = "self";

            var result = SimulationEngine.RunOnce(player, monster, 42);
            Assert(result.Trace.Count > 0, "Trace should have entries.");

            var jsonl = SimulationJsonlWriter.WriteTraceJsonl(result, "Test Encounter", "TestMonster", 42);
            Assert(!string.IsNullOrEmpty(jsonl), "JSONL should not be empty.");

            // Verify it's valid JSON
            var parsed = System.Text.Json.JsonDocument.Parse(jsonl);
            var root = parsed.RootElement;
            Assert(root.GetProperty("winner").GetString() == result.Winner, "Winner should match.");
            Assert(root.GetProperty("duration_ms").GetInt32() == result.DurationMs, "Duration should match.");
            Assert(root.GetProperty("seed").GetInt32() == 42, "Seed should be recorded.");

            var ticks = root.GetProperty("ticks");
            Assert(ticks.GetArrayLength() == result.Trace.Count, "Tick count should match trace count.");

            // Verify first tick has structured state
            var firstTick = ticks[0];
            Assert(firstTick.GetProperty("player").GetProperty("hp").GetInt32() == 100, "Initial player HP should be 100.");
            Assert(firstTick.GetProperty("opponent").GetProperty("hp").GetInt32() == 80, "Initial opponent HP should be 80.");

            // Verify events contain structured data (check a tick with events)
            var hasEventTick = false;
            for (var i = 0; i < ticks.GetArrayLength(); i++)
            {
                var tick = ticks[i];
                if (tick.TryGetProperty("events", out var events) && events.GetArrayLength() > 0)
                {
                    hasEventTick = true;
                    var firstEvent = events[0];
                    Assert(firstEvent.TryGetProperty("t", out _), "Event should have type field 't'.");
                    break;
                }
            }
            Assert(hasEventTick, "Should have at least one tick with events.");

            Console.WriteLine($"  JSONL test: {jsonl.Length} chars, {ticks.GetArrayLength()} ticks");
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
