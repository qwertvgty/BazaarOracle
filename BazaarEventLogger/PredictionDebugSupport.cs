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
            }

            File.WriteAllText(
                Path.Combine(exportDir, "CombatSimEvents.tail.log"),
                ReadTailSafe(Path.Combine(Paths.BepInExRootPath, "CombatSimEvents.log"), 600));
            File.WriteAllText(
                Path.Combine(exportDir, "GameSimEvents.tail.log"),
                ReadTailSafe(Path.Combine(Paths.BepInExRootPath, "GameSimEvents.log"), 600));
            File.WriteAllText(
                Path.Combine(exportDir, "BattleSimulator.log.tail.txt"),
                ReadTailSafe(BattleSimulator.LogFilePath, 300));
            File.WriteAllText(
                Path.Combine(exportDir, "BattleSimulatorTicks.log.tail.txt"),
                ReadTailSafe(BattleSimulator.TraceLogFilePath, 400));

            return exportDir;
        }

        private static string ReadTailSafe(string path, int lineCount)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return string.Empty;

                var queue = new Queue<string>();
                foreach (var line in File.ReadLines(path))
                {
                    queue.Enqueue(line);
                    if (queue.Count > lineCount)
                        queue.Dequeue();
                }

                return string.Join(Environment.NewLine, queue);
            }
            catch (Exception ex)
            {
                return $"Failed to read {path}: {ex}";
            }
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
