using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BazaarEventLogger
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.bazaar.eventlogger";
        public const string PluginName = "Bazaar Event Logger";
        public const string PluginVersion = "2.0.0";

        internal static ManualLogSource Log;
        private static GameObject _debugUiRoot;

        private void Awake()
        {
            Log = Logger;

            // Load card database from StreamingAssets
            var cardsJsonPath = Path.Combine(Application.streamingAssetsPath, "cards.json");
            CardDatabase.Load(cardsJsonPath);

            // Initialize loggers
            EventLogger.Initialize();
            CombatLogger.Initialize();
            BattleSimulator.Initialize();
            EnsureDebugUi();
            DontDestroyOnLoad(gameObject);

            // Apply Harmony patches
            var harmony = new Harmony(PluginGuid);
            GameSimPatch.ApplyManualPatches(harmony);

            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded!");
            Logger.LogInfo($"  Card templates: {CardDatabase.TemplateCount}");
            Logger.LogInfo($"  GameSim log: {EventLogger.LogFilePath}");
            Logger.LogInfo($"  CombatSim log: {CombatLogger.LogFilePath}");
        }

        private void EnsureDebugUi()
        {
            if (_debugUiRoot != null)
                return;

            _debugUiRoot = new GameObject("BazaarOracleDebugUi");
            _debugUiRoot.hideFlags = HideFlags.HideAndDontSave;
            _debugUiRoot.AddComponent<PredictionDebugUi>();
            DontDestroyOnLoad(_debugUiRoot);
            Logger.LogInfo("Prediction debug UI attached");
        }
    }
}
