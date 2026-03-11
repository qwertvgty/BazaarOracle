using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using HarmonyLib;

namespace BazaarEventLogger
{
    public static class GameSimPatch
    {
        public static void ApplyManualPatches(Harmony harmony)
        {
            PatchHandler(harmony, "TheBazaar.GameSimHandler", "HandleMessage",
                nameof(GameSimHandlePrefix), "GameSim");
            PatchHandler(harmony, "TheBazaar.CombatSimHandler", "HandleMessage",
                nameof(CombatSimHandlePrefix), "CombatSim");

            // Fallback: patch NetMessage Data setters
            PatchSetter(harmony, typeof(NetMessageGameSim), "Data",
                nameof(GameSimDataSetterPostfix));
            PatchSetter(harmony, typeof(NetMessageCombatSim), "Data",
                nameof(CombatSimDataSetterPostfix));
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
                    CombatLogger.LogCombatSim(value, __instance?.MessageId ?? "setter");
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
        private static string _currentEncounterId;
        private static string _currentEncounterName;

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
            // Track player attributes
            if (sim.Player?.Attributes != null && sim.Player.Attributes.Count > 0)
                _playerState = sim.Player;

            // Track run state
            if (sim.Run != null)
                _runState = sim.Run;

            if (!string.IsNullOrEmpty(sim.CurrentState?.CurrentEncounterId))
                UpdateCurrentEncounter(sim.CurrentState.CurrentEncounterId);

            // Accumulate cards - add/update cards, handle disposals
            if (sim.Cards != null)
            {
                foreach (var kvp in sim.Cards)
                {
                    var card = kvp.Value;
                    if (card == null) continue;

                    // Only track player's hand cards for combat
                    if (card.Placement?.Owner == BazaarGameShared.Domain.Core.Types.ECombatantId.Player &&
                        card.Placement?.Section == BazaarGameShared.Domain.Core.Types.EInventorySection.Hand)
                    {
                        _playerCards[kvp.Key] = card;
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
                    else if (evt is GameSimEventCardMoved moved)
                    {
                        // If card moved out of hand, remove from tracking
                        if (moved.ToInventory != BazaarGameShared.Domain.Core.Types.EInventorySection.Hand)
                            _playerCards.Remove(moved.InstanceId);
                    }
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

            // Detect combat selection: state=Choice and SelectionSet contains combat encounter IDs
            if (sim.CurrentState == null) return;
            if (sim.CurrentState.StateName.ToString() != "Choice") return;
            if (sim.CurrentState.SelectionSet == null || sim.CurrentState.SelectionSet.Count == 0) return;

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

            if (combatEncounters.Count == 0) return;

            // Build a synthetic GameSim with accumulated player state
            var syntheticState = new GameSim();
            syntheticState.Player = _playerState ?? sim.Player;
            syntheticState.Cards = new Dictionary<string, SimUpdateCard>(_playerCards);
            syntheticState.Run = _runState ?? sim.Run;
            syntheticState.CurrentState = sim.CurrentState;

            try
            {
                BattleSimulator.PredictCombats(syntheticState, combatEncounters);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Battle prediction error: {ex}");
            }
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
