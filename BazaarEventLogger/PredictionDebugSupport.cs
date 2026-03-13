using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Newtonsoft.Json;

namespace BazaarEventLogger
{
    public class BattlePredictionEncounterRecord
    {
        public string EncounterId;
        public string EncounterName;
        public string MonsterId;
        public string MonsterName;
        public SimEncounterSnapshot EncounterSnapshot;
        public BatchSimulationResult Result;
        public string TraceFilePath;
    }

    public class BattlePredictionSession
    {
        public string Signature;
        public DateTime CreatedAtUtc;
        public bool ManualRerun;
        public SimulationBatchOptions Options;
        public SimCombatantSnapshot PlayerSnapshot;
        public List<BattlePredictionEncounterRecord> Encounters = new List<BattlePredictionEncounterRecord>();
        public string SummaryReport;
    }

    public static class PredictionDebugExporter
    {
        public static string ExportSessionBundle(BattlePredictionSession session, BattlePredictionEncounterRecord selectedEncounter)
        {
            if (session == null)
                return null;

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var encounterLabel = SanitizeFileName(selectedEncounter?.EncounterName ?? "all_encounters");
            var exportDir = Path.Combine(Paths.BepInExRootPath, "DebugExports", $"{stamp}_{encounterLabel}");
            Directory.CreateDirectory(exportDir);

            File.WriteAllText(
                Path.Combine(exportDir, "session.json"),
                JsonConvert.SerializeObject(session, Formatting.Indented));

            File.WriteAllText(
                Path.Combine(exportDir, "summary.txt"),
                session.SummaryReport ?? string.Empty);

            File.WriteAllText(
                Path.Combine(exportDir, "player_snapshot.json"),
                JsonConvert.SerializeObject(session.PlayerSnapshot, Formatting.Indented));

            if (selectedEncounter != null)
            {
                File.WriteAllText(
                    Path.Combine(exportDir, "selected_encounter.json"),
                    JsonConvert.SerializeObject(selectedEncounter, Formatting.Indented));
                CopyTraceIfPresent(
                    selectedEncounter.TraceFilePath,
                    Path.Combine(exportDir, "selected_trace.txt"));

                var matchedCombatText = CombatLogger.GetRecentCombatsText(selectedEncounter.EncounterId);
                if (!string.IsNullOrWhiteSpace(matchedCombatText))
                {
                    File.WriteAllText(
                        Path.Combine(exportDir, "CombatSimEvents.log"),
                        matchedCombatText);
                }

                var matchedCombatJsonl = CombatLoggerJsonl.GetRecentCombatsJsonl(selectedEncounter.EncounterId);
                if (!string.IsNullOrWhiteSpace(matchedCombatJsonl))
                {
                    File.WriteAllText(
                        Path.Combine(exportDir, "CombatSimEvents.jsonl"),
                        matchedCombatJsonl);
                }

                var gameSimTail = EventLogger.GetRecentGameSimsText();
                if (!string.IsNullOrWhiteSpace(gameSimTail))
                {
                    File.WriteAllText(
                        Path.Combine(exportDir, "GameSimEvents.tail.log"),
                        gameSimTail);
                }

                var gameSimJsonlTail = GameSimLoggerJsonl.GetRecentGameSimsJsonl();
                if (!string.IsNullOrWhiteSpace(gameSimJsonlTail))
                {
                    File.WriteAllText(
                        Path.Combine(exportDir, "GameSimEvents.tail.jsonl"),
                        gameSimJsonlTail);
                }

                File.WriteAllText(
                    Path.Combine(exportDir, "coverage_report.txt"),
                    SimulationReporter.FormatCoverageReport(
                        session.PlayerSnapshot,
                        selectedEncounter.EncounterSnapshot,
                        selectedEncounter.Result));
            }

            return exportDir;
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "export";

            foreach (var invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            return value.Trim();
        }

        private static void CopyTraceIfPresent(string sourcePath, string destinationPath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, destinationPath, true);
                    return;
                }

                File.WriteAllText(destinationPath, "No selected trace file available.");
            }
            catch (Exception ex)
            {
                File.WriteAllText(destinationPath, $"Failed to export trace: {ex}");
            }
        }
    }
}
