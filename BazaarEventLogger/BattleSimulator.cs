using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Diagnostics;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BepInEx;
using Newtonsoft.Json.Linq;

namespace BazaarEventLogger
{
    public static class BattleSimulator
    {
        public static string LogFilePath { get; private set; }
        public static string TraceLogFilePath { get; private set; }

        private static JObject _monsterData;
        private static readonly Dictionary<string, string> EncounterToMonster =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> MonsterNameToId =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, MonsterFingerprint> MonsterFingerprints =
            new Dictionary<string, MonsterFingerprint>(StringComparer.OrdinalIgnoreCase);

        public static void Initialize()
        {
            LogFilePath = Path.Combine(Paths.BepInExRootPath, "BattleSimulator.log");
            TraceLogFilePath = Path.Combine(Paths.BepInExRootPath, "BattleSimulatorTicks.log");
            var header = $"=== Battle Simulator Started @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}";
            File.AppendAllText(LogFilePath, header);
            File.AppendAllText(TraceLogFilePath, header);
            EncounterLearningStore.Initialize(Path.Combine(Paths.BepInExRootPath, "BattleSimulator.learned_mappings.json"));
            LoadMonsterData();
        }

        public static void PredictCombats(GameSim gameState, List<string> encounterInstanceIds)
        {
            if (_monsterData == null || encounterInstanceIds == null || encounterInstanceIds.Count == 0)
                return;

            var player = SimulationSnapshotBuilder.BuildPlayerSnapshot(gameState);
            var results = new List<BatchSimulationResult>();
            var traces = new List<string>();
            var selectionSummary = string.Join(", ", encounterInstanceIds.Select(CardDatabase.ResolveName));
            Plugin.Log?.LogInfo(
                $"PredictCombats start: day={gameState?.Run?.Day}, hour={gameState?.Run?.Hour}, playerCards={player.Cards.Count}, encounters=[{selectionSummary}]");

            foreach (var encounterId in encounterInstanceIds)
            {
                var stopwatch = Stopwatch.StartNew();
                var encounterName = CardDatabase.ResolveName(encounterId);
                var templateId = CardDatabase.GetInfo(encounterId)?.Id ?? encounterId;
                Plugin.Log?.LogInfo($"PredictCombats encounter start: {encounterName} ({templateId})");
                var resolvedByFallback = false;
                if (!TryResolveMonsterId(templateId, encounterName, out var monsterId, out resolvedByFallback))
                {
                    results.Add(new BatchSimulationResult
                    {
                        EncounterName = encounterName,
                        MonsterName = "unknown",
                        CoverageScore = 0,
                        ConfidenceLabel = "低",
                        Verdict = "未知",
                        UnsupportedEffects = new List<string> { $"monster_mapping:{templateId}" }
                    });
                    Plugin.Log?.LogInfo($"PredictCombats encounter unresolved: {encounterName} ({templateId})");
                    continue;
                }

                var monsterObj = _monsterData["monsters"]?[monsterId];
                if (monsterObj == null)
                {
                    results.Add(new BatchSimulationResult
                    {
                        EncounterName = encounterName,
                        MonsterName = monsterId,
                        CoverageScore = 0,
                        ConfidenceLabel = "低",
                        Verdict = "缺数据",
                        UnsupportedEffects = new List<string> { $"monster_data:{monsterId}" }
                    });
                    Plugin.Log?.LogInfo($"PredictCombats encounter missing data: {encounterName} -> {monsterId}");
                    continue;
                }

                var encounter = SimulationSnapshotBuilder.BuildEncounterSnapshot(encounterId, encounterName, monsterId, monsterObj);
                var result = SimulationEngine.RunBatch(player, encounter);
                if (result.Samples.Count > 0)
                    traces.Add(SimulationReporter.FormatSimulationTrace(encounterName, result.MonsterName, result.Samples[0]));
                if (resolvedByFallback)
                    result.UnsupportedEffects.Insert(0, $"monster_mapping_fallback:{encounterName}->{monsterId}");
                results.Add(result);
                stopwatch.Stop();
                Plugin.Log?.LogInfo(
                    $"PredictCombats encounter done: {encounterName} -> {result.Verdict}, runs={result.Runs}, duration={stopwatch.ElapsedMilliseconds}ms");
            }

            var output = SimulationReporter.FormatPredictionReport(player, results);
            try
            {
                File.AppendAllText(LogFilePath, output);
                if (traces.Count > 0)
                    File.AppendAllText(TraceLogFilePath, string.Join(Environment.NewLine, traces) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Failed to write battle predictions: {ex.Message}");
            }

            Plugin.Log?.LogInfo(
                $"Battle predictions generated for {results.Count} encounter(s): {string.Join(", ", results.Select(result => $"{result.EncounterName}={result.Verdict}"))}");
        }

        private static void LoadMonsterData()
        {
            EncounterToMonster.Clear();
            MonsterNameToId.Clear();
            MonsterFingerprints.Clear();
            var path = Path.Combine(UnityEngine.Application.streamingAssetsPath, "monster_data.json");
            if (!File.Exists(path))
            {
                Plugin.Log.LogWarning($"monster_data.json not found: {path}");
                return;
            }

            try
            {
                _monsterData = JObject.Parse(File.ReadAllText(path));
                foreach (var mapping in _monsterData["encounterToMonster"] as JObject ?? new JObject())
                    EncounterToMonster[mapping.Key] = mapping.Value.ToString();
                foreach (var monster in _monsterData["monsters"] as JObject ?? new JObject())
                {
                    var internalName = monster.Value?["internalName"]?.ToString();
                    var normalized = NormalizeName(internalName);
                    if (!string.IsNullOrEmpty(normalized) && !MonsterNameToId.ContainsKey(normalized))
                        MonsterNameToId[normalized] = monster.Key;
                    MonsterFingerprints[monster.Key] = BuildFingerprint(monster.Key, monster.Value);
                }

                foreach (var learned in EncounterLearningStore.All.Values)
                {
                    if (!string.IsNullOrEmpty(learned.MonsterId))
                        EncounterToMonster[learned.EncounterId] = learned.MonsterId;
                }

                Plugin.Log.LogInfo($"Loaded {EncounterToMonster.Count} encounter->monster mappings");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to load monster_data.json: {ex.Message}");
            }
        }

        public static void TryLearnEncounterMapping(string encounterId, string encounterName, IEnumerable<string> opponentTemplateIds)
        {
            if (_monsterData == null || string.IsNullOrEmpty(encounterId) || opponentTemplateIds == null)
                return;

            var observed = opponentTemplateIds
                .Where(id => !string.IsNullOrEmpty(id))
                .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            if (observed.Count < 2)
                return;

            if (EncounterLearningStore.TryGet(encounterId, out var existing) && existing.Score >= 0.99)
                return;

            MonsterFingerprint best = null;
            var bestScore = 0.0;
            var secondBest = 0.0;

            foreach (var fingerprint in MonsterFingerprints.Values)
            {
                var score = ScoreFingerprint(observed, fingerprint);
                if (score > bestScore)
                {
                    secondBest = bestScore;
                    bestScore = score;
                    best = fingerprint;
                }
                else if (score > secondBest)
                {
                    secondBest = score;
                }
            }

            if (best == null || bestScore < 0.85 || bestScore - secondBest < 0.08)
                return;

            EncounterToMonster[encounterId] = best.MonsterId;
            var mapping = new LearnedEncounterMapping
            {
                EncounterId = encounterId,
                EncounterName = encounterName,
                MonsterId = best.MonsterId,
                MonsterName = best.MonsterName,
                Score = Math.Round(bestScore, 3),
                ObservedTemplateIds = observed.OrderByDescending(kvp => kvp.Value).Select(kvp => kvp.Key).ToList(),
                LearnedAtUtc = DateTime.UtcNow.ToString("O")
            };
            EncounterLearningStore.Upsert(mapping);
            Plugin.Log?.LogInfo($"Learned encounter mapping: {encounterName} ({encounterId}) -> {best.MonsterName} [{best.MonsterId}] score={bestScore:F3}");
        }

        private static bool TryResolveMonsterId(string encounterId, string encounterName, out string monsterId, out bool resolvedByFallback)
        {
            resolvedByFallback = false;
            if (EncounterToMonster.TryGetValue(encounterId, out monsterId))
                return true;

            if (EncounterLearningStore.TryGet(encounterId, out var learned) && !string.IsNullOrEmpty(learned.MonsterId))
            {
                monsterId = learned.MonsterId;
                return true;
            }

            monsterId = ResolveMonsterByEncounterName(encounterName);
            resolvedByFallback = !string.IsNullOrEmpty(monsterId);
            return !string.IsNullOrEmpty(monsterId);
        }

        private static string ResolveMonsterByEncounterName(string encounterName)
        {
            var normalizedEncounter = NormalizeName(encounterName);
            if (string.IsNullOrEmpty(normalizedEncounter))
                return null;

            if (MonsterNameToId.TryGetValue(normalizedEncounter, out var exact))
                return exact;

            var encounterTokens = new HashSet<string>(normalizedEncounter.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            string bestMonsterId = null;
            double bestScore = 0;

            foreach (var kvp in MonsterNameToId)
            {
                var monsterTokens = new HashSet<string>(kvp.Key.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                var score = Similarity(normalizedEncounter, encounterTokens, kvp.Key, monsterTokens);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMonsterId = kvp.Value;
                }
            }

            return bestScore >= 0.72 ? bestMonsterId : null;
        }

        private static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            var normalized = name.ToLowerInvariant();
            normalized = Regex.Replace(normalized, @"\s*\((bronze|silver|gold|diamond|legendary)\)\s*$", "");
            normalized = normalized.Replace("&", " and ");
            normalized = Regex.Replace(normalized, @"[^a-z0-9\s]", " ");
            var tokens = normalized
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token != "monster" &&
                                token != "encounter" &&
                                token != "event" &&
                                token != "the" &&
                                token != "elite" &&
                                token != "boss" &&
                                token != "bronze" &&
                                token != "silver" &&
                                token != "gold" &&
                                token != "diamond" &&
                                token != "legendary");
            return string.Join(" ", tokens);
        }

        private static double Similarity(string aName, HashSet<string> aTokens, string bName, HashSet<string> bTokens)
        {
            if (string.IsNullOrEmpty(aName) || string.IsNullOrEmpty(bName))
                return 0;
            if (aName == bName)
                return 1;

            var intersection = aTokens.Intersect(bTokens).Count();
            var union = aTokens.Union(bTokens).Count();
            var tokenOverlap = union == 0 ? 0 : (double)intersection / union;

            if ((aTokens.Count > 0 && aTokens.IsSubsetOf(bTokens)) || (bTokens.Count > 0 && bTokens.IsSubsetOf(aTokens)))
                tokenOverlap += 0.15;

            var editScore = 1.0 - ((double)LevenshteinDistance(aName, bName) / Math.Max(aName.Length, bName.Length));
            return Math.Max(tokenOverlap, (editScore * 0.7) + (tokenOverlap * 0.3));
        }

        private static int LevenshteinDistance(string a, string b)
        {
            var dp = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++)
                dp[i, 0] = i;
            for (var j = 0; j <= b.Length; j++)
                dp[0, j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    dp[i, j] = Math.Min(
                        Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                        dp[i - 1, j - 1] + cost);
                }
            }

            return dp[a.Length, b.Length];
        }

        private static MonsterFingerprint BuildFingerprint(string monsterId, JToken monster)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            AddTemplates(counts, monster?["cards"] as JArray);
            AddTemplates(counts, monster?["skills"] as JArray);

            return new MonsterFingerprint
            {
                MonsterId = monsterId,
                MonsterName = monster?["internalName"]?.ToString() ?? monsterId,
                TemplateCounts = counts
            };
        }

        private static void AddTemplates(Dictionary<string, int> target, JArray items)
        {
            if (items == null)
                return;

            foreach (var item in items.OfType<JObject>())
            {
                var templateId = item["templateId"]?.ToString();
                if (string.IsNullOrEmpty(templateId))
                    continue;

                target[templateId] = target.TryGetValue(templateId, out var count) ? count + 1 : 1;
            }
        }

        private static double ScoreFingerprint(
            Dictionary<string, int> observedTemplateCounts,
            MonsterFingerprint fingerprint)
        {
            if (observedTemplateCounts.Count == 0 || fingerprint.TemplateCounts.Count == 0)
                return 0;

            var intersection = 0;
            var observedTotal = observedTemplateCounts.Values.Sum();
            var expectedTotal = fingerprint.TemplateCounts.Values.Sum();

            foreach (var observed in observedTemplateCounts)
            {
                if (fingerprint.TemplateCounts.TryGetValue(observed.Key, out var expected))
                    intersection += Math.Min(observed.Value, expected);
            }

            var precision = observedTotal == 0 ? 0 : (double)intersection / observedTotal;
            var recall = expectedTotal == 0 ? 0 : (double)intersection / expectedTotal;
            if (precision <= 0 || recall <= 0)
                return 0;

            return (2 * precision * recall) / (precision + recall);
        }

        private sealed class MonsterFingerprint
        {
            public string MonsterId;
            public string MonsterName;
            public Dictionary<string, int> TemplateCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
