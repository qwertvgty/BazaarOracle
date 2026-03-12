using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace BazaarEventLogger
{
    /// <summary>
    /// Loads cards.json and provides TemplateId -> card name/info mapping.
    /// Also tracks InstanceId -> TemplateId from runtime events.
    /// </summary>
    public static class CardDatabase
    {
        // TemplateId (GUID) -> Card info
        private static readonly Dictionary<string, CardInfo> _templates =
            new Dictionary<string, CardInfo>(StringComparer.OrdinalIgnoreCase);

        // Runtime InstanceId -> TemplateId mapping (populated from game events)
        private static readonly Dictionary<string, string> _instanceToTemplate =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static int TemplateCount => _templates.Count;

        public static void Load(string cardsJsonPath)
        {
            if (!File.Exists(cardsJsonPath))
            {
                Plugin.Log.LogWarning($"cards.json not found: {cardsJsonPath}");
                return;
            }

            try
            {
                var json = File.ReadAllText(cardsJsonPath);
                var root = JObject.Parse(json);

                foreach (var versionEntry in root)
                {
                    var cards = versionEntry.Value as JArray;
                    if (cards == null) continue;

                    foreach (var card in cards)
                    {
                        var id = card["Id"]?.ToString();
                        if (string.IsNullOrEmpty(id)) continue;

                        var info = new CardInfo
                        {
                            Id = id,
                            InternalName = card["InternalName"]?.ToString() ?? "?",
                            Type = card["Type"]?.ToString() ?? card["$type"]?.ToString() ?? "?",
                            StartingTier = card["StartingTier"]?.ToString() ?? "?",
                            Size = card["Size"]?.ToString() ?? "?",
                            Tags = card["Tags"]?.ToObject<List<string>>() ?? new List<string>(),
                            Heroes = card["Heroes"]?.ToObject<List<string>>() ?? new List<string>(),
                            RawTemplate = card as JObject
                        };

                        // Extract base tier attributes (Bronze by default)
                        var tiers = card["Tiers"] as JObject;
                        if (tiers != null)
                        {
                            foreach (var tier in tiers)
                            {
                                var attrs = tier.Value?["Attributes"] as JObject;
                                if (attrs != null)
                                {
                                    info.TierAttributes[tier.Key] = attrs.Properties()
                                        .ToDictionary(p => p.Name, p => p.Value.ToString());
                                }
                            }
                        }

                        // Extract ability descriptions
                        var abilities = card["Abilities"] as JObject;
                        if (abilities != null)
                        {
                            foreach (var ability in abilities)
                            {
                                var desc = ability.Value?["InternalDescription"]?.ToString();
                                if (!string.IsNullOrEmpty(desc))
                                    info.AbilityDescriptions[ability.Key] = desc;
                            }
                        }

                        if (!_templates.ContainsKey(id))
                            _templates[id] = info;
                    }
                }

                Plugin.Log.LogInfo($"Loaded {_templates.Count} card templates from cards.json");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to load cards.json: {ex.Message}");
            }
        }

        /// <summary>
        /// Register a runtime instance -> template mapping (from CardDealt/CardSpawned events)
        /// </summary>
        public static void RegisterInstance(string instanceId, string templateId)
        {
            if (!string.IsNullOrEmpty(instanceId) && !string.IsNullOrEmpty(templateId))
                _instanceToTemplate[instanceId] = templateId;
        }

        /// <summary>
        /// Resolve an InstanceId or TemplateId to a readable card name
        /// </summary>
        public static string ResolveName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "?";

            // Try as template ID first
            if (_templates.TryGetValue(id, out var info))
                return info.InternalName;

            // Try as instance ID -> template ID
            if (_instanceToTemplate.TryGetValue(id, out var templateId))
            {
                if (_templates.TryGetValue(templateId, out var tInfo))
                    return tInfo.InternalName;
            }

            return id; // Return raw ID if not found
        }

        /// <summary>
        /// Get full card info by TemplateId or InstanceId
        /// </summary>
        public static CardInfo GetInfo(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            if (_templates.TryGetValue(id, out var info))
                return info;

            if (_instanceToTemplate.TryGetValue(id, out var templateId))
            {
                if (_templates.TryGetValue(templateId, out var tInfo))
                    return tInfo;
            }

            return null;
        }

        /// <summary>
        /// Format: "CardName (instanceId)" or just "CardName" if template
        /// </summary>
        public static string FormatId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "?";

            var name = ResolveName(id);
            if (name == id)
                return id; // Couldn't resolve, just show ID

            // If it was an instance ID, show "Name [short-id]"
            if (_instanceToTemplate.ContainsKey(id))
            {
                var shortId = id.Length > 8 ? id.Substring(0, 8) : id;
                return $"{name} [{shortId}]";
            }

            return name;
        }
    }

    public class CardInfo
    {
        public string Id;
        public string InternalName;
        public string Type;
        public string StartingTier;
        public string Size;
        public List<string> Tags = new List<string>();
        public List<string> Heroes = new List<string>();
        public Dictionary<string, Dictionary<string, string>> TierAttributes = new Dictionary<string, Dictionary<string, string>>();
        public Dictionary<string, string> AbilityDescriptions = new Dictionary<string, string>();
        public JObject RawTemplate;

        public override string ToString() => $"{InternalName} ({Type}, {StartingTier})";

        public Dictionary<string, int> GetMergedTierAttributes(string tierName, IDictionary<string, int> runtimeAttributes = null)
        {
            var merged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var tier in GetTierInheritanceOrder(tierName))
            {
                if (!TierAttributes.TryGetValue(tier, out var attrs))
                    continue;

                foreach (var attr in attrs)
                {
                    if (int.TryParse(attr.Value, out var parsed))
                        merged[attr.Key] = parsed;
                }
            }

            if (runtimeAttributes != null)
            {
                foreach (var attr in runtimeAttributes)
                    merged[attr.Key] = attr.Value;
            }

            return merged;
        }

        private static IEnumerable<string> GetTierInheritanceOrder(string tierName)
        {
            var order = new[] { "Bronze", "Silver", "Gold", "Diamond", "Legendary" };
            var result = new List<string>();
            foreach (var tier in order)
            {
                result.Add(tier);
                if (string.Equals(tier, tierName, StringComparison.OrdinalIgnoreCase))
                    break;
            }

            if (result.Count == 0)
                result.Add("Bronze");

            return result;
        }
    }
}
