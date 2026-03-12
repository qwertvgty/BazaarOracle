using System.Collections.Generic;
using System.Linq;

namespace BazaarEventLogger
{
    public static class SimEffectTriggers
    {
        public const string OnCardFired = "on_card_fired";
        public const string OnCardCritted = "on_card_critted";
        public const string OnItemUsed = "on_item_used";
        public const string OnPlayerRageGain = "on_player_rage_gain";
        public const string OnPlayerHealthLoss = "on_player_health_loss";
        public const string OnPlayerEnraged = "on_player_enraged";
        public const string OnPlayerEnrageEnded = "on_player_enrage_ended";
        public const string OnFightStarted = "on_fight_started";
        public const string OnFightEnded = "on_fight_ended";
        public const string Passive = "passive";
    }

    public class SimulationTraceEntry
    {
        public int TimeMs;
        public string Summary;
        public string PlayerState;
        public string OpponentState;
        public List<string> CardStates = new List<string>();
        public List<string> Events = new List<string>();
    }

    public class SimEffectSpec
    {
        public string Type;
        public int Value;
        public string Target;
        public bool IsPassive;
        public string Source;
        public string Trigger = SimEffectTriggers.OnCardFired;
        public bool RequiresOwnerEnraged;
        public bool RequiresOwnerNotEnraged;
        public List<string> TriggerCardSizes = new List<string>();
        public string RequiresSourceAttributeZero;
        public double? RequiresOwnerHealthBelowRatio;
        public string DynamicValueSourceAttribute;
        public string DynamicCountScope;
        public string DynamicCountSourceAttribute;
        public int DynamicCountMultiplier = 1;
        public bool DynamicCountExcludeSource;
        public int DynamicValueSign = 1;
        public int TargetCount = 1;
        public bool UseTriggerSourceForTargeting;

        public override string ToString() => $"{Type}:{Value}->{Target}";
    }

    public class SimCardSnapshot
    {
        public string Name;
        public string InstanceId;
        public string TemplateId;
        public string CardType;
        public string Tier;
        public string Size;
        public List<string> Tags = new List<string>();
        public int CooldownMax;
        public int CurrentCooldown;
        public int Multicast;
        public int Freeze;
        public int HasteDuration;
        public int SlowDuration;
        public bool IsDisabled;
        public Dictionary<string, int> Attributes = new Dictionary<string, int>();
        public List<SimEffectSpec> Effects = new List<SimEffectSpec>();
        public List<string> UnsupportedEffects = new List<string>();
        public List<string> CoverageNotes = new List<string>();
        public double CoverageScore = 1.0;
    }

    public class SimCombatantSnapshot
    {
        public string Name;
        public string SourceId;
        public int Health;
        public int HealthMax;
        public int Shield;
        public int Burn;
        public int Poison;
        public int HealthRegen;
        public int Joy;
        public int Rage;
        public int RageMax;
        public int EnragedDurationMax;
        public int EnragedDuration;
        public bool IsEnraged;
        public int BurnTickProgress;
        public int PoisonTickProgress;
        public List<SimCardSnapshot> Cards = new List<SimCardSnapshot>();
        public List<string> UnsupportedEffects = new List<string>();
        public List<string> CoverageNotes = new List<string>();

        public double CoverageScore =>
            Cards.Count == 0 ? 1.0 : Cards.Average(c => c.CoverageScore);
    }

    public class SimEncounterSnapshot
    {
        public string EncounterId;
        public string EncounterName;
        public string MonsterId;
        public SimCombatantSnapshot Opponent;
    }

    public class SimulationBatchOptions
    {
        public int Runs = 10;
        public int SeedBase = 1337;
        public int TraceSamples = 1;
    }

    public class SingleSimulationResult
    {
        public string Winner;
        public int PlayerHealthRemaining;
        public int OpponentHealthRemaining;
        public int DurationMs;
        public bool SandstormTriggered;
        public List<SimulationTraceEntry> Trace = new List<SimulationTraceEntry>();
    }

    public class PendingSimEffect
    {
        public int DueTimeMs;
        public SimEffectSpec Effect;
        public SimCombatantSnapshot Owner;
        public SimCombatantSnapshot Target;
        public SimCardSnapshot SourceCard;
        public SimCardSnapshot TriggerSourceCard;
        public string OwnerLabel;
    }

    public class BatchSimulationResult
    {
        public string EncounterName;
        public string MonsterName;
        public int Runs;
        public int Wins;
        public int Losses;
        public double WinRate;
        public double AveragePlayerHealthRemaining;
        public double AverageOpponentHealthRemaining;
        public double MedianDurationSeconds;
        public bool SandstormSeen;
        public double CoverageScore;
        public string ConfidenceLabel;
        public string Verdict;
        public List<string> UnsupportedEffects = new List<string>();
        public List<string> CoverageGaps = new List<string>();
        public List<string> KeyThreats = new List<string>();
        public List<string> LossReasons = new List<string>();
        public SingleSimulationResult TraceSample;
    }

    public class NormalizedCardProfile
    {
        public string Name;
        public string TemplateId;
        public string Tier;
        public string Size;
        public int CooldownMax;
        public int Multicast;
        public Dictionary<string, int> Attributes = new Dictionary<string, int>();
        public List<SimEffectSpec> Effects = new List<SimEffectSpec>();
        public List<string> UnsupportedEffects = new List<string>();
        public double CoverageScore = 1.0;
    }
}
