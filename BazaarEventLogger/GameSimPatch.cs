using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using HarmonyLib;

namespace BazaarEventLogger
{
    public static class GameSimPatch
    {
        private const bool EnableSetterFallbackPatches = false;

        public static void ApplyManualPatches(Harmony harmony)
        {
            PatchHandler(harmony, "TheBazaar.GameSimHandler", "HandleMessage",
                nameof(GameSimHandlePrefix), "GameSim");
            PatchHandler(harmony, "TheBazaar.CombatSimHandler", "HandleMessage",
                nameof(CombatSimHandlePrefix), "CombatSim");

            if (EnableSetterFallbackPatches)
            {
                // Fallback: patch NetMessage Data setters only if handler patching fails on a future game version.
                PatchSetter(harmony, typeof(NetMessageGameSim), "Data",
                    nameof(GameSimDataSetterPostfix));
                PatchSetter(harmony, typeof(NetMessageCombatSim), "Data",
                    nameof(CombatSimDataSetterPostfix));
            }
        }

        private static void PatchHandler(Harmony harmony, string typeName, string methodName,
            string prefixName, string label)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    Plugin.Log.LogWarning($"{typeName} not found");
                    return;
                }

                var method = AccessTools.Method(type, methodName);
                if (method != null)
                {
                    harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(GameSimPatch), prefixName));
                    Plugin.Log.LogInfo($"Patched {typeName}.{methodName}");
                }
                else
                {
                    Plugin.Log.LogWarning($"{typeName}.{methodName} not found. Available methods:");
                    foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                      BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        var parms = string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name));
                        Plugin.Log.LogInfo($"  {m.Name}({parms}) -> {m.ReturnType.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to patch {typeName}: {ex}");
            }
        }

        private static void PatchSetter(Harmony harmony, Type targetType, string propertyName,
            string postfixName)
        {
            try
            {
                var setter = AccessTools.PropertySetter(targetType, propertyName);
                if (setter != null)
                {
                    harmony.Patch(setter,
                        postfix: new HarmonyMethod(typeof(GameSimPatch), postfixName));
                    Plugin.Log.LogInfo($"Patched {targetType.Name}.set_{propertyName} (fallback)");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to patch {targetType.Name}.set_{propertyName}: {ex}");
            }
        }

        // === GameSim Prefixes ===

        public static void GameSimHandlePrefix(object[] __args)
        {
            try
            {
                foreach (var arg in __args)
                {
                    GameSim sim = null;
                    string msgId = "?";

                    if (arg is NetMessageGameSim msg)
                    {
                        sim = msg.Data;
                        msgId = msg.MessageId;
                    }
                    else if (arg is GameSim gs)
                    {
                        sim = gs;
                    }

                    if (sim != null)
                    {
                        // Register instance->template mappings from events
                        TrackInstances(sim);
                        EventLogger.LogGameSim(sim, msgId);

                        // Check if this is a combat selection screen (Choice state with combat encounters)
                        TryPredictCombats(sim);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"GameSimHandlePrefix error: {ex.Message}");
            }
        }

        public static void CombatSimHandlePrefix(object[] __args)
        {
            try
            {
                foreach (var arg in __args)
                {
                    CombatSim sim = null;
                    string msgId = "?";

                    if (arg is NetMessageCombatSim msg)
                    {
                        sim = msg.Data;
                        msgId = msg.MessageId;
                    }
                    else if (arg is CombatSim cs)
                    {
                        sim = cs;
                    }

                    if (sim != null)
                    {
                        CombatLogger.LogCombatSim(sim, msgId);
                        CombatLoggerJsonl.LogCombatSim(sim, msgId, _currentEncounterId, _currentEncounterName);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"CombatSimHandlePrefix error: {ex.Message}");
            }
        }

        // === Fallback setters ===

        public static void GameSimDataSetterPostfix(NetMessageGameSim __instance, GameSim value)
        {
            try
            {
                if (value != null)
                {
                    TrackInstances(value);
                    EventLogger.LogGameSim(value, __instance?.MessageId ?? "setter");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"GameSim setter error: {ex.Message}");
            }
        }

        public static void CombatSimDataSetterPostfix(NetMessageCombatSim __instance, CombatSim value)
        {
            try
            {
                if (value != null)
                {
                    CombatLogger.LogCombatSim(value, __instance?.MessageId ?? "setter");
                    CombatLoggerJsonl.LogCombatSim(value, __instance?.MessageId ?? "setter", _currentEncounterId, _currentEncounterName);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"CombatSim setter error: {ex.Message}");
            }
        }

        // === Battle Prediction ===

        // Accumulated player state across messages
        private static Dictionary<string, SimUpdateCard> _playerCards = new Dictionary<string, SimUpdateCard>();
        private static Dictionary<string, SimUpdateCard> _opponentCards = new Dictionary<string, SimUpdateCard>();
        private static SimUpdatePlayer _playerState;
        private static SimUpdateRun _runState;
        private static SimUpdateRunState _currentRunState;
        private static string _currentEncounterId;
        private static string _currentEncounterName;

        /// <summary>Current encounter context for tagging combat logs.</summary>
        public static string CurrentEncounterId => _currentEncounterId;
        public static string CurrentEncounterName => _currentEncounterName;
        private static string _lastPredictionSignature;

        private static void UpdateCurrentEncounter(string encounterId)
        {
            if (string.IsNullOrEmpty(encounterId))
                return;

            if (!string.Equals(_currentEncounterId, encounterId, StringComparison.OrdinalIgnoreCase))
                _opponentCards.Clear();

            _currentEncounterId = encounterId;
            _currentEncounterName = CardDatabase.ResolveName(_currentEncounterId);
        }

        private static void AccumulatePlayerState(GameSim sim)
        {
            // Track player attributes — merge incrementally because GameSim sends
            // delta-based messages that only include changed attributes.  Replacing
            // _playerState wholesale would lose previously-seen keys like Health.
            if (sim.Player?.Attributes != null && sim.Player.Attributes.Count > 0)
            {
                if (_playerState == null)
                {
                    _playerState = sim.Player;
                }
                else
                {
                    foreach (var kvp in sim.Player.Attributes)
                        _playerState.Attributes[kvp.Key] = kvp.Value;
                }
            }

            // Track run state
            if (sim.Run != null)
                _runState = sim.Run;
            if (sim.CurrentState != null)
                _currentRunState = sim.CurrentState;

            if (!string.IsNullOrEmpty(sim.CurrentState?.CurrentEncounterId))
                UpdateCurrentEncounter(sim.CurrentState.CurrentEncounterId);

            // Accumulate cards — merge incrementally because GameSim sends
            // delta-based messages.  A later delta for the same card may only
            // contain changed attributes and omit Placement, so we must keep
            // previously-seen card data and merge new attributes on top.
            if (sim.Cards != null)
            {
                foreach (var kvp in sim.Cards)
                {
                    var card = kvp.Value;
                    if (card == null) continue;
                    var info = CardDatabase.GetInfo(card.InstanceId);
                    var type = info?.Type ?? "";
                    var isTriggeredSupport = string.Equals(type, "Skill", StringComparison.OrdinalIgnoreCase) ||
                                             string.Equals(type, "PlayerEffect", StringComparison.OrdinalIgnoreCase);

                    if (card.Placement?.Owner == BazaarGameShared.Domain.Core.Types.ECombatantId.Player &&
                        (card.Placement?.Section == BazaarGameShared.Domain.Core.Types.EInventorySection.Hand || isTriggeredSupport))
                    {
                        if (_playerCards.TryGetValue(kvp.Key, out var existing))
                        {
                            // Merge new attributes into existing card to avoid losing data
                            if (card.Attributes != null)
                            {
                                foreach (var attr in card.Attributes)
                                    existing.Attributes[attr.Key] = attr.Value;
                            }
                            // Update mutable fields if present in this delta
                            if (card.Placement != null) existing.Placement = card.Placement;
                            if (card.Tier != null) existing.Tier = card.Tier;
                            if (card.State != default) existing.State = card.State;
                        }
                        else
                        {
                            _playerCards[kvp.Key] = card;
                        }
                    }
                    else if (card.Placement == null && _playerCards.ContainsKey(kvp.Key))
                    {
                        // Delta with no Placement — card already tracked, merge attributes only
                        var existing = _playerCards[kvp.Key];
                        if (card.Attributes != null)
                        {
                            foreach (var attr in card.Attributes)
                                existing.Attributes[attr.Key] = attr.Value;
                        }
                        if (card.Tier != null) existing.Tier = card.Tier;
                        if (card.State != default) existing.State = card.State;
                    }
                    else if (card.Placement?.Owner == BazaarGameShared.Domain.Core.Types.ECombatantId.Opponent)
                    {
                        _opponentCards[kvp.Key] = card;
                    }
                }
            }

            // Remove disposed cards
            if (sim.Events != null)
            {
                foreach (var evt in sim.Events)
                {
                    if (evt is GameSimEventStateTransitioned transitioned && !string.IsNullOrEmpty(transitioned.CurrentEncounterId))
                    {
                        UpdateCurrentEncounter(transitioned.CurrentEncounterId);
                    }
                    else if (evt is GameSimEventStateResumed resumed && !string.IsNullOrEmpty(resumed.CurrentEncounterId))
                    {
                        UpdateCurrentEncounter(resumed.CurrentEncounterId);
                    }
                    else if (evt is GameSimEventStateSuspended suspended && !string.IsNullOrEmpty(suspended.CurrentEncounterId))
                    {
                        UpdateCurrentEncounter(suspended.CurrentEncounterId);
                    }

                    if (evt is GameSimEventCardDisposed disposed)
                    {
                        _playerCards.Remove(disposed.InstanceId);
                        _opponentCards.Remove(disposed.InstanceId);
                    }
                    // NOTE: CardMoved events are NOT used to remove cards.
                    // During state transitions the game fires CardMoved with
                    // ToInventory != Hand even though cards are still on the
                    // board.  Rely on CardDisposed/CardSold for removal.
                    else if (evt is GameSimEventCardSold sold)
                    {
                        _playerCards.Remove(sold.InstanceId);
                        _opponentCards.Remove(sold.InstanceId);
                    }
                }
            }
        }

        private static void TryPredictCombats(GameSim sim)
        {
            // Always accumulate state
            AccumulatePlayerState(sim);
            TryLearnEncounterFromObservedOpponent();

            if (sim.CurrentState == null)
            {
                _lastPredictionSignature = null;
                return;
            }

            if (sim.CurrentState.StateName.ToString() != "Choice")
            {
                _lastPredictionSignature = null;
                return;
            }

            if (sim.CurrentState.SelectionSet == null || sim.CurrentState.SelectionSet.Count == 0)
            {
                _lastPredictionSignature = null;
                return;
            }

            // Check if selection contains combat encounters (com_ prefix or CombatEncounter type)
            var combatEncounters = new List<string>();
            foreach (var instanceId in sim.CurrentState.SelectionSet)
            {
                var info = CardDatabase.GetInfo(instanceId);
                if (info != null && (info.Type.Contains("Combat") || info.Type.Contains("combat")))
                {
                    combatEncounters.Add(instanceId);
                }
                else if (instanceId.StartsWith("com_"))
                {
                    combatEncounters.Add(instanceId);
                }
            }

            if (combatEncounters.Count == 0)
            {
                _lastPredictionSignature = null;
                return;
            }

            var predictionSignature = BuildPredictionSignature(sim, combatEncounters);
            if (string.Equals(_lastPredictionSignature, predictionSignature, StringComparison.Ordinal))
                return;

            // Build a synthetic GameSim with accumulated player state
            var syntheticState = new GameSim();
            syntheticState.Player = _playerState ?? sim.Player;
            syntheticState.Cards = new Dictionary<string, SimUpdateCard>(_playerCards);
            syntheticState.Run = _runState ?? sim.Run;
            syntheticState.CurrentState = sim.CurrentState;
            _lastPredictionSignature = predictionSignature;

            try
            {
                BattleSimulator.PredictCombats(syntheticState, combatEncounters);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Battle prediction error: {ex}");
                Plugin.Log?.LogError(
                    $"Battle prediction context: day={sim.Run?.Day}, hour={sim.Run?.Hour}, state={sim.CurrentState?.StateName}, selection=[{string.Join(", ", combatEncounters.Select(CardDatabase.ResolveName))}]");
            }
        }

        public static bool TryBuildCurrentPredictionContext(out GameSim syntheticState, out List<string> combatEncounters)
        {
            syntheticState = null;
            combatEncounters = new List<string>();

            if (_currentRunState == null ||
                _currentRunState.StateName.ToString() != "Choice" ||
                _currentRunState.SelectionSet == null ||
                _currentRunState.SelectionSet.Count == 0)
            {
                return false;
            }

            foreach (var instanceId in _currentRunState.SelectionSet)
            {
                var info = CardDatabase.GetInfo(instanceId);
                if (info != null && (info.Type.Contains("Combat") || info.Type.Contains("combat")))
                    combatEncounters.Add(instanceId);
                else if (instanceId.StartsWith("com_"))
                    combatEncounters.Add(instanceId);
            }

            if (combatEncounters.Count == 0)
                return false;

            syntheticState = new GameSim
            {
                Player = _playerState,
                Cards = new Dictionary<string, SimUpdateCard>(_playerCards),
                Run = _runState,
                CurrentState = _currentRunState
            };
            return true;
        }

        public static bool TryGetCurrentEncounterContext(out string encounterId, out string encounterName)
        {
            encounterId = _currentEncounterId;
            encounterName = _currentEncounterName;
            return !string.IsNullOrWhiteSpace(encounterId);
        }

        private static string BuildPredictionSignature(GameSim sim, IEnumerable<string> combatEncounters)
        {
            var state = sim?.CurrentState;
            var run = sim?.Run;
            var selection = string.Join("|", combatEncounters
                .Where(id => !string.IsNullOrEmpty(id))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
            var playerFingerprint = BuildPlayerPredictionFingerprint();
            return string.Join("::",
                run?.Day.ToString() ?? "?",
                run?.Hour.ToString() ?? "?",
                state?.StateName.ToString() ?? "?",
                state?.CurrentEncounterId ?? "?",
                selection,
                playerFingerprint);
        }

        private static string BuildPlayerPredictionFingerprint()
        {
            var playerState = _playerState?.Attributes == null
                ? "player:?"
                : string.Join(",",
                    _playerState.Attributes
                        .OrderBy(kvp => kvp.Key.ToString(), StringComparer.OrdinalIgnoreCase)
                        .Select(kvp => $"{kvp.Key}={kvp.Value}"));

            var cards = _playerCards.Values
                .Where(card => card != null)
                .OrderBy(GetPredictionCardSortKey, StringComparer.OrdinalIgnoreCase)
                .Select(BuildPredictionCardFingerprint);

            return $"{playerState}||{string.Join("|", cards)}";
        }

        private static string GetPredictionCardSortKey(SimUpdateCard card)
        {
            if (card == null)
                return string.Empty;

            return string.Join(":",
                card.Placement?.Section?.ToString() ?? "?",
                card.Placement?.Socket?.ToString() ?? "?",
                card.InstanceId ?? "?");
        }

        private static string BuildPredictionCardFingerprint(SimUpdateCard card)
        {
            if (card == null)
                return "?";

            var attrs = card.Attributes == null
                ? string.Empty
                : string.Join(",",
                    card.Attributes
                        .Where(kvp => kvp.Value.DeltaType == EAttributeDeltaType.Update)
                        .OrderBy(kvp => kvp.Key.ToString(), StringComparer.OrdinalIgnoreCase)
                        .Select(kvp => $"{kvp.Key}={kvp.Value.Value}"));
            var templateId = CardDatabase.GetInfo(card.InstanceId)?.Id ?? "?";
            return string.Join(":",
                card.InstanceId ?? "?",
                templateId,
                card.Tier?.ToString() ?? "?",
                card.State.ToString(),
                card.Placement?.Section?.ToString() ?? "?",
                card.Placement?.Socket?.ToString() ?? "?",
                attrs);
        }

        private static void TryLearnEncounterFromObservedOpponent()
        {
            if (string.IsNullOrEmpty(_currentEncounterId) || _opponentCards.Count == 0)
                return;

            var templateIds = _opponentCards.Values
                .Where(card => card != null)
                .Select(card => CardDatabase.GetInfo(card.InstanceId)?.Id)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();

            if (templateIds.Count == 0)
                return;

            BattleSimulator.TryLearnEncounterMapping(
                _currentEncounterId,
                string.IsNullOrEmpty(_currentEncounterName) ? CardDatabase.ResolveName(_currentEncounterId) : _currentEncounterName,
                templateIds);
        }

        // === Instance tracking ===

        private static void TrackInstances(GameSim sim)
        {
            if (sim?.Events == null) return;

            foreach (var evt in sim.Events)
            {
                switch (evt)
                {
                    case GameSimEventCardDealt e:
                        CardDatabase.RegisterInstance(e.InstanceId, e.TemplateId);
                        break;
                    case GameSimEventCardSpawned e:
                        CardDatabase.RegisterInstance(e.InstanceId, e.TemplateId);
                        break;
                }
            }
        }
    }
}
