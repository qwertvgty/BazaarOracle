using System;
using System.Linq;
using UnityEngine;

namespace BazaarEventLogger
{
    public class PredictionDebugUi : MonoBehaviour
    {
        private readonly Rect _defaultRect = new Rect(40, 40, 1100, 760);
        private readonly Rect _launcherRect = new Rect(12, 12, 96, 32);
        private Rect _windowRect;
        private bool _visible;
        private bool _inputUnavailableLogged;
        private Vector2 _encounterScroll;
        private Vector2 _detailScroll;
        private int _selectedEncounterIndex;
        private string _statusMessage = "F8 打开/关闭调试面板";

        private void Awake()
        {
            _windowRect = _defaultRect;
            DontDestroyOnLoad(gameObject);
            Plugin.Log?.LogInfo("PredictionDebugUi awake");
        }

        private void Update()
        {
            TryHandleHotkeys();
        }

        private void OnGUI()
        {
            HandleGuiHotkey();

            if (!_visible)
            {
                if (GUI.Button(_launcherRect, "Oracle UI"))
                    ToggleVisible();
                return;
            }

            if (!_visible)
                return;

            GUI.depth = 0;
            _windowRect = GUILayout.Window(238041, _windowRect, DrawWindow, "Bazaar Oracle Debug");
        }

        private void DrawWindow(int windowId)
        {
            var sessionAvailable = BattleSimulator.TryGetLastSession(out var session);

            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("重新模拟当前选择", GUILayout.Height(28), GUILayout.Width(180)))
            {
                if (BattleSimulator.TryRerunCurrentPredictions(out var rerunSession))
                {
                    session = rerunSession;
                    sessionAvailable = true;
                    _selectedEncounterIndex = 0;
                    SetStatus($"已重新模拟 {rerunSession.Encounters.Count} 场候选战斗");
                }
                else
                {
                    SetStatus("当前不在可模拟的战斗选择界面");
                }
            }

            GUI.enabled = sessionAvailable && session != null && session.Encounters.Count > 0;
            if (GUILayout.Button("导出当前选中调试包", GUILayout.Height(28), GUILayout.Width(180)))
            {
                var selected = GetSelectedEncounter(session);
                var exportDir = PredictionDebugExporter.ExportSessionBundle(session, selected);
                SetStatus(string.IsNullOrEmpty(exportDir) ? "导出失败" : $"已导出到 {exportDir}");
            }
            GUI.enabled = true;

            GUILayout.Label(_statusMessage, GUILayout.Height(28));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (!sessionAvailable || session == null)
            {
                GUILayout.Space(10);
                GUILayout.Label("暂无预测结果。进入战斗选择界面后会自动生成，或在该界面点击“重新模拟当前选择”。");
                GUILayout.EndVertical();
                GUI.DragWindow(new Rect(0, 0, 10000, 24));
                return;
            }

            _selectedEncounterIndex = Mathf.Clamp(_selectedEncounterIndex, 0, Math.Max(0, session.Encounters.Count - 1));

            GUILayout.Space(8);
            GUILayout.Label($"生成时间: {session.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} | 手动重算: {(session.ManualRerun ? "是" : "否")} | 玩家卡牌: {session.PlayerSnapshot?.Cards.Count ?? 0}");
            GUILayout.Label($"样本数: {session.Options?.Runs ?? 0} | Trace 文件数: {session.Encounters.Count(record => !string.IsNullOrWhiteSpace(record.TraceFilePath))}");

            GUILayout.BeginHorizontal();
            DrawEncounterList(session);
            DrawEncounterDetail(session);
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, 10000, 24));
        }

        private void DrawEncounterList(BattlePredictionSession session)
        {
            GUILayout.BeginVertical(GUILayout.Width(360));
            GUILayout.Label("候选战斗");
            _encounterScroll = GUILayout.BeginScrollView(_encounterScroll, GUI.skin.box);
            for (var i = 0; i < session.Encounters.Count; i++)
            {
                var record = session.Encounters[i];
                var selected = i == _selectedEncounterIndex;
                var label = $"{record.EncounterName}\n{record.Result?.Verdict} | 胜率 {record.Result?.WinRate:P0} | 置信 {record.Result?.ConfidenceLabel}";
                if (GUILayout.Toggle(selected, label, "Button", GUILayout.Height(56)))
                    _selectedEncounterIndex = i;
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void DrawEncounterDetail(BattlePredictionSession session)
        {
            var record = GetSelectedEncounter(session);
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            if (record == null)
            {
                GUILayout.Label("没有可显示的战斗详情。");
                GUILayout.EndVertical();
                return;
            }

            _detailScroll = GUILayout.BeginScrollView(_detailScroll, GUI.skin.box);
            GUILayout.Label($"{record.EncounterName} -> {record.MonsterName}");
            if (record.Result != null)
            {
                GUILayout.Label($"结论: {record.Result.Verdict}");
                GUILayout.Label($"胜率: {record.Result.WinRate:P1} | 平均剩余生命: {record.Result.AveragePlayerHealthRemaining:F1} | 中位时长: {record.Result.MedianDurationSeconds:F1}s");
                GUILayout.Label($"覆盖率: {record.Result.CoverageScore:P0} | 置信度: {record.Result.ConfidenceLabel}");
                if (record.Result.KeyThreats.Any())
                    GUILayout.Label($"关键威胁: {string.Join(" ; ", record.Result.KeyThreats)}");
                if (record.Result.LossReasons.Any())
                    GUILayout.Label($"风险: {string.Join(" ; ", record.Result.LossReasons)}");
                if (record.Result.UnsupportedEffects.Any())
                    GUILayout.Label($"未覆盖: {string.Join(" ; ", record.Result.UnsupportedEffects)}");
            }

            GUILayout.Space(8);
            GUILayout.Label("玩家当前顺序");
            if (session.PlayerSnapshot?.Cards != null)
            {
                foreach (var card in session.PlayerSnapshot.Cards)
                    GUILayout.Label($"- {card.Name} [{card.Size}] CD={card.CooldownMax} x{card.Multicast}");
            }

            GUILayout.Space(8);
            GUILayout.Label("详细时间轴");
            if (string.IsNullOrWhiteSpace(record.TraceFilePath))
            {
                GUILayout.Label("当前没有详细 trace 文件。");
            }
            else
            {
                GUILayout.Label("详细 trace 已写入文件，不在 UI 中渲染。");
                GUILayout.Label(record.TraceFilePath);
                GUILayout.Label("需要分析时请点“导出当前选中调试包”。");
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private BattlePredictionEncounterRecord GetSelectedEncounter(BattlePredictionSession session)
        {
            if (session == null || session.Encounters.Count == 0)
                return null;

            var index = Mathf.Clamp(_selectedEncounterIndex, 0, session.Encounters.Count - 1);
            return session.Encounters[index];
        }

        private void SetStatus(string message)
        {
            _statusMessage = message;
        }

        private void TryHandleHotkeys()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F8) || Input.GetKeyDown(KeyCode.Insert))
                    ToggleVisible();
            }
            catch (Exception ex)
            {
                if (_inputUnavailableLogged)
                    return;

                _inputUnavailableLogged = true;
                Plugin.Log?.LogWarning($"PredictionDebugUi input hotkey unavailable: {ex.Message}");
                SetStatus("F8/Insert 热键不可用，请点击左上角 Oracle UI 按钮");
            }
        }

        private void HandleGuiHotkey()
        {
            var current = Event.current;
            if (current == null || current.type != EventType.KeyDown)
                return;

            if (current.keyCode != KeyCode.F8 && current.keyCode != KeyCode.Insert)
                return;

            ToggleVisible();
            current.Use();
        }

        private void ToggleVisible()
        {
            _visible = !_visible;
            SetStatus(_visible ? "调试面板已打开" : "调试面板已隐藏");
        }
    }
}
