using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace BazaarEventLogger
{
    public sealed class LearnedEncounterMapping
    {
        public string EncounterId;
        public string EncounterName;
        public string MonsterId;
        public string MonsterName;
        public double Score;
        public List<string> ObservedTemplateIds = new List<string>();
        public string LearnedAtUtc;
    }

    public static class EncounterLearningStore
    {
        private static readonly Dictionary<string, LearnedEncounterMapping> LearnedMappings =
            new Dictionary<string, LearnedEncounterMapping>(StringComparer.OrdinalIgnoreCase);

        public static string FilePath { get; private set; }

        public static IReadOnlyDictionary<string, LearnedEncounterMapping> All => LearnedMappings;

        public static void Initialize(string filePath)
        {
            FilePath = filePath;
            LearnedMappings.Clear();

            if (!File.Exists(filePath))
                return;

            try
            {
                var items = JsonConvert.DeserializeObject<List<LearnedEncounterMapping>>(File.ReadAllText(filePath))
                    ?? new List<LearnedEncounterMapping>();
                foreach (var item in items.Where(item => !string.IsNullOrEmpty(item?.EncounterId)))
                    LearnedMappings[item.EncounterId] = item;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Failed to load learned encounter mappings: {ex.Message}");
            }
        }

        public static bool TryGet(string encounterId, out LearnedEncounterMapping mapping)
        {
            return LearnedMappings.TryGetValue(encounterId, out mapping);
        }

        public static void Upsert(LearnedEncounterMapping mapping)
        {
            if (mapping == null || string.IsNullOrEmpty(mapping.EncounterId))
                return;

            LearnedMappings[mapping.EncounterId] = mapping;
            Persist();
        }

        private static void Persist()
        {
            if (string.IsNullOrEmpty(FilePath))
                return;

            try
            {
                var payload = LearnedMappings.Values
                    .OrderBy(item => item.EncounterName)
                    .ThenBy(item => item.EncounterId)
                    .ToList();
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(payload, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Failed to save learned encounter mappings: {ex.Message}");
            }
        }
    }
}
