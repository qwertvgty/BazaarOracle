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

            // Apply Harmony patches
            var harmony = new Harmony(PluginGuid);
            GameSimPatch.ApplyManualPatches(harmony);

            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded!");
            Logger.LogInfo($"  Card templates: {CardDatabase.TemplateCount}");
            Logger.LogInfo($"  GameSim log: {EventLogger.LogFilePath}");
            Logger.LogInfo($"  CombatSim log: {CombatLogger.LogFilePath}");
        }
    }
}
