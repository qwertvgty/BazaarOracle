#!/usr/bin/env python3
"""
Compare real combat JSONL (CombatSimEvents.jsonl) with simulation trace JSONL
(offline_trace.jsonl) to produce a structured diff report.

Usage:
    # Auto-discover files in export directory
    python combat_diff.py <export_dir>

    # Explicit files
    python combat_diff.py <real.jsonl> <sim.jsonl> [--real-index -1]

The report shows:
  - Winner match/mismatch
  - HP timeline divergence (aligned by trigger sequence)
  - Per-card trigger count comparison
  - Damage summary comparison
  - First N event-level mismatches
"""

import argparse
import json
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import List, Dict, Optional, Tuple


# ── Data structures ─────────────────────────────────────────────

@dataclass
class CombatSummary:
    source: str = ""  # "real" or "sim"
    winner: str = ""  # "Player" or "Opponent"
    final_player_hp: int = 0
    final_opponent_hp: int = 0
    initial_player_hp: int = 0
    initial_opponent_hp: int = 0
    frame_or_tick_count: int = 0
    duration_label: str = ""

    # HP recorded after each trigger event: [(label, player_hp, opponent_hp)]
    hp_at_triggers: List[Tuple[str, int, int]] = field(default_factory=list)

    # card_name -> trigger count
    card_triggers: Dict[str, int] = field(default_factory=lambda: defaultdict(int))

    # Aggregate damage
    total_damage_to_player: int = 0
    total_damage_to_opponent: int = 0

    # Ordered trigger sequence: [(card_name, optional_detail)]
    trigger_sequence: List[Tuple[str, str]] = field(default_factory=list)


# ── Extractors ──────────────────────────────────────────────────

def extract_real(data: dict) -> CombatSummary:
    """Extract CombatSummary from a real combat JSONL line."""
    s = CombatSummary(source="real")
    frames = data.get("frames", [])
    s.frame_or_tick_count = data.get("frame_count", len(frames))
    s.duration_label = f"{s.frame_or_tick_count} frames"

    # Pre-scan: find initial HP from first available Health attr (previous value)
    player_hp: Optional[int] = None
    opponent_hp: Optional[int] = None
    for frame in frames:
        if player_hp is None:
            pu = frame.get("player") or {}
            hp_pair = pu.get("attrs", {}).get("Health")
            if isinstance(hp_pair, list) and len(hp_pair) >= 2:
                player_hp = hp_pair[0]
                s.initial_player_hp = hp_pair[0]
        if opponent_hp is None:
            ou = frame.get("opponent") or {}
            hp_pair = ou.get("attrs", {}).get("Health")
            if isinstance(hp_pair, list) and len(hp_pair) >= 2:
                opponent_hp = hp_pair[0]
                s.initial_opponent_hp = hp_pair[0]
        if player_hp is not None and opponent_hp is not None:
            break

    player_dead = False
    opponent_dead = False

    for frame in frames:
        pu = frame.get("player") or {}
        ou = frame.get("opponent") or {}

        # Update HP from attribute changes
        p_attrs = pu.get("attrs", {})
        o_attrs = ou.get("attrs", {})

        if "Health" in p_attrs:
            pair = p_attrs["Health"]
            if isinstance(pair, list) and len(pair) >= 2:
                player_hp = pair[1]

        if "Health" in o_attrs:
            pair = o_attrs["Health"]
            if isinstance(pair, list) and len(pair) >= 2:
                opponent_hp = pair[1]

        # Accumulate damage from health adjustments
        for adj in pu.get("health_adj", []):
            amt = adj.get("amount", 0)
            attr = adj.get("attr", "")
            if attr == "Health" and amt < 0:
                s.total_damage_to_player += abs(amt)

        for adj in ou.get("health_adj", []):
            amt = adj.get("amount", 0)
            attr = adj.get("attr", "")
            if attr == "Health" and amt < 0:
                s.total_damage_to_opponent += abs(amt)

        if pu.get("dead"):
            player_dead = True
        if ou.get("dead"):
            opponent_dead = True

        # Extract trigger events
        for evt in frame.get("events", []):
            if evt.get("t") == "Triggered":
                card = evt.get("source", "?")
                s.card_triggers[card] += 1
                s.trigger_sequence.append((card, ""))

                # Record HP at this trigger
                s.hp_at_triggers.append((
                    card,
                    player_hp if player_hp is not None else s.initial_player_hp,
                    opponent_hp if opponent_hp is not None else s.initial_opponent_hp,
                ))

    # Determine winner
    if opponent_dead and not player_dead:
        s.winner = "Player"
    elif player_dead and not opponent_dead:
        s.winner = "Opponent"
    else:
        # Fallback: winner_raw from the data
        winner_raw = data.get("winner", "")
        loser_raw = data.get("loser", "")
        # Can't reliably map names to Player/Opponent without more context
        s.winner = f"({winner_raw})"

    s.final_player_hp = player_hp if player_hp is not None else s.initial_player_hp
    s.final_opponent_hp = opponent_hp if opponent_hp is not None else s.initial_opponent_hp

    return s


def extract_sim(data: dict) -> CombatSummary:
    """Extract CombatSummary from a simulation trace JSONL line."""
    s = CombatSummary(source="sim")
    s.winner = data.get("winner", "?")
    s.final_player_hp = data.get("player_hp_remaining", 0)
    s.final_opponent_hp = data.get("opponent_hp_remaining", 0)
    duration_ms = data.get("duration_ms", 0)
    s.frame_or_tick_count = data.get("tick_count", 0)
    s.duration_label = f"{duration_ms}ms ({s.frame_or_tick_count} ticks)"

    ticks = data.get("ticks", [])

    # Get initial HP from first tick
    if ticks:
        first = ticks[0]
        p = first.get("player", {})
        o = first.get("opponent", {})
        s.initial_player_hp = p.get("hp", 0)
        s.initial_opponent_hp = o.get("hp", 0)

    # Current HP tracking
    player_hp = s.initial_player_hp
    opponent_hp = s.initial_opponent_hp

    for tick in ticks:
        p = tick.get("player", {})
        o = tick.get("opponent", {})
        if "hp" in p:
            player_hp = p["hp"]
        if "hp" in o:
            opponent_hp = o["hp"]

        for evt in tick.get("events", []):
            t = evt.get("t", "")

            if t == "trigger":
                card = evt.get("card", "?")
                owner = evt.get("owner", "?")
                s.card_triggers[card] += 1
                s.trigger_sequence.append((card, owner))
                s.hp_at_triggers.append((card, player_hp, opponent_hp))

            elif t == "damage":
                val = evt.get("value", 0)
                owner = evt.get("owner", "")
                if owner == "Player":
                    s.total_damage_to_opponent += val
                else:
                    s.total_damage_to_player += val

            elif t in ("burn_tick", "poison_tick"):
                val = evt.get("value", 0)
                target = evt.get("target", "")
                if target == "Player":
                    s.total_damage_to_player += val
                else:
                    s.total_damage_to_opponent += val

            elif t == "sandstorm":
                val = evt.get("value", 0)
                s.total_damage_to_player += val
                s.total_damage_to_opponent += val

    s.final_player_hp = player_hp
    s.final_opponent_hp = opponent_hp

    return s


# ── Diff logic ──────────────────────────────────────────────────

def diff_report(real: CombatSummary, sim: CombatSummary) -> str:
    lines: List[str] = []
    W = 60

    lines.append("=" * W)
    lines.append("  COMBAT DIFF REPORT")
    lines.append("=" * W)
    lines.append("")

    # ── Winner ──
    winner_match = real.winner == sim.winner
    mark = "MATCH" if winner_match else "MISMATCH"
    lines.append(f"Winner:     Real={real.winner:<12s} Sim={sim.winner:<12s} {'[OK]' if winner_match else '[!!]'} {mark}")

    # ── Final HP ──
    php_delta = sim.final_player_hp - real.final_player_hp
    ohp_delta = sim.final_opponent_hp - real.final_opponent_hp
    lines.append(f"Player HP:  Real={real.final_player_hp:<12d} Sim={sim.final_player_hp:<12d} Δ={php_delta:+d}")
    lines.append(f"Opponent HP:Real={real.final_opponent_hp:<12d} Sim={sim.final_opponent_hp:<12d} Δ={ohp_delta:+d}")

    # ── Duration ──
    lines.append(f"Duration:   Real={real.duration_label}  Sim={sim.duration_label}")
    lines.append("")

    # ── Damage summary ──
    lines.append("─" * W)
    lines.append("  Damage Summary")
    lines.append("─" * W)

    def fmt_delta(real_v, sim_v):
        d = sim_v - real_v
        pct = f" ({d/real_v:+.0%})" if real_v > 0 else ""
        return f"Δ={d:+d}{pct}"

    lines.append(f"  To Opponent:  Real={real.total_damage_to_opponent:<8d} Sim={sim.total_damage_to_opponent:<8d} {fmt_delta(real.total_damage_to_opponent, sim.total_damage_to_opponent)}")
    lines.append(f"  To Player:    Real={real.total_damage_to_player:<8d} Sim={sim.total_damage_to_player:<8d} {fmt_delta(real.total_damage_to_player, sim.total_damage_to_player)}")
    lines.append("")

    # ── Per-card triggers ──
    lines.append("─" * W)
    lines.append("  Per-Card Trigger Counts")
    lines.append("─" * W)

    all_cards = sorted(
        set(real.card_triggers.keys()) | set(sim.card_triggers.keys()),
        key=lambda c: -(real.card_triggers.get(c, 0) + sim.card_triggers.get(c, 0)),
    )

    name_w = max((len(c) for c in all_cards), default=8)
    name_w = min(name_w, 30)
    lines.append(f"  {'Card':<{name_w}s}  {'Real':>6s}  {'Sim':>6s}  {'Δ':>6s}")

    mismatch_cards = []
    for card in all_cards:
        rc = real.card_triggers.get(card, 0)
        sc = sim.card_triggers.get(card, 0)
        delta = sc - rc
        marker = ""
        if delta != 0:
            if rc == 0:
                marker = "  ← SIM ONLY"
            elif sc == 0:
                marker = "  ← MISSING IN SIM"
            else:
                marker = "  ← MISMATCH"
            mismatch_cards.append((card, rc, sc))
        lines.append(f"  {card:<{name_w}s}  {rc:>6d}  {sc:>6d}  {delta:>+6d}{marker}")

    lines.append("")

    # ── HP divergence ──
    lines.append("─" * W)
    lines.append("  HP Divergence (aligned by trigger sequence)")
    lines.append("─" * W)

    r_triggers = real.hp_at_triggers
    s_triggers = sim.hp_at_triggers
    max_len = max(len(r_triggers), len(s_triggers))

    if max_len > 0:
        # Sample at intervals to keep output manageable
        sample_points = _pick_sample_points(max_len, max_samples=15)
        max_player_div = 0
        max_player_div_idx = 0
        max_opp_div = 0
        max_opp_div_idx = 0
        first_div_idx = None

        divergence_lines = []
        for idx in range(max_len):
            r_php = r_triggers[idx][1] if idx < len(r_triggers) else None
            r_ohp = r_triggers[idx][2] if idx < len(r_triggers) else None
            s_php = s_triggers[idx][1] if idx < len(s_triggers) else None
            s_ohp = s_triggers[idx][2] if idx < len(s_triggers) else None

            if r_php is not None and s_php is not None:
                d = abs(s_php - r_php)
                if d > max_player_div:
                    max_player_div = d
                    max_player_div_idx = idx
                if d > 0 and first_div_idx is None:
                    first_div_idx = idx

            if r_ohp is not None and s_ohp is not None:
                d = abs(s_ohp - r_ohp)
                if d > max_opp_div:
                    max_opp_div = d
                    max_opp_div_idx = idx

            if idx in sample_points:
                r_card = r_triggers[idx][0] if idx < len(r_triggers) else "?"
                s_card = s_triggers[idx][0] if idx < len(s_triggers) else "?"

                r_php_s = str(r_php) if r_php is not None else "N/A"
                s_php_s = str(s_php) if s_php is not None else "N/A"
                r_ohp_s = str(r_ohp) if r_ohp is not None else "N/A"
                s_ohp_s = str(s_ohp) if s_ohp is not None else "N/A"

                pdelta = f"Δ={s_php - r_php:+d}" if r_php is not None and s_php is not None else ""
                odelta = f"Δ={s_ohp - r_ohp:+d}" if r_ohp is not None and s_ohp is not None else ""

                divergence_lines.append(
                    f"  #{idx + 1:>3d}  PlayerHP: R={r_php_s:>5s} S={s_php_s:>5s} {pdelta:>8s}  |  OppHP: R={r_ohp_s:>5s} S={s_ohp_s:>5s} {odelta:>8s}"
                )

        for line in divergence_lines:
            lines.append(line)

        lines.append("")
        if first_div_idx is not None:
            lines.append(f"  First divergence at trigger #{first_div_idx + 1}")
        lines.append(f"  Max Player HP divergence: {max_player_div} at trigger #{max_player_div_idx + 1}")
        lines.append(f"  Max Opponent HP divergence: {max_opp_div} at trigger #{max_opp_div_idx + 1}")
    else:
        lines.append("  (no trigger events to compare)")

    lines.append("")

    # ── Trigger sequence diff ──
    lines.append("─" * W)
    lines.append("  Trigger Sequence Diff (first 10 mismatches)")
    lines.append("─" * W)

    mismatches_shown = 0
    max_seq = max(len(real.trigger_sequence), len(sim.trigger_sequence))
    for i in range(max_seq):
        r_card = real.trigger_sequence[i][0] if i < len(real.trigger_sequence) else None
        s_card = sim.trigger_sequence[i][0] if i < len(sim.trigger_sequence) else None

        if r_card != s_card:
            r_label = r_card if r_card else "(end)"
            s_label = s_card if s_card else "(end)"
            lines.append(f"  #{i + 1:>3d}: Real=[{r_label}]  Sim=[{s_label}]")
            mismatches_shown += 1
            if mismatches_shown >= 10:
                remaining = sum(1 for j in range(i + 1, max_seq)
                                if (real.trigger_sequence[j][0] if j < len(real.trigger_sequence) else None) !=
                                   (sim.trigger_sequence[j][0] if j < len(sim.trigger_sequence) else None))
                if remaining > 0:
                    lines.append(f"  ... and {remaining} more mismatches")
                break

    if mismatches_shown == 0:
        lines.append("  All trigger events match in sequence order.")

    lines.append("")

    # ── Actionable summary ──
    lines.append("─" * W)
    lines.append("  Actionable Summary")
    lines.append("─" * W)

    if not winner_match:
        lines.append("  [!!] Winner mismatch -- simulation prediction is WRONG for this combat.")

    if mismatch_cards:
        missing = [(c, r, s) for c, r, s in mismatch_cards if s == 0]
        extra = [(c, r, s) for c, r, s in mismatch_cards if r == 0]
        count_diff = [(c, r, s) for c, r, s in mismatch_cards if r > 0 and s > 0]

        if missing:
            lines.append(f"  Cards firing in real but NOT in sim ({len(missing)}):")
            for c, r, _ in missing:
                lines.append(f"    → {c} (real={r}x): check EffectNormalizer for this card")
        if extra:
            lines.append(f"  Cards firing in sim but NOT in real ({len(extra)}):")
            for c, _, s in extra:
                lines.append(f"    → {c} (sim={s}x): sim may have wrong effects/cooldown")
        if count_diff:
            lines.append(f"  Cards with different trigger counts ({len(count_diff)}):")
            for c, r, s in count_diff:
                lines.append(f"    → {c} (real={r} sim={s}): check cooldown/haste/slow/freeze logic")

    if max_player_div > 0 or max_opp_div > 0:
        total_div = max_player_div + max_opp_div
        if total_div > 20:
            lines.append(f"  HP divergence is significant (max {total_div}) — likely missing or wrong effect values.")
        elif total_div > 5:
            lines.append(f"  HP divergence is moderate (max {total_div}) — minor effect value differences.")
        else:
            lines.append(f"  HP divergence is small (max {total_div}) — simulation is close to reality.")

    if not mismatch_cards and winner_match and max_player_div <= 5 and max_opp_div <= 5:
        lines.append("  [OK] Simulation closely matches real combat!")

    lines.append("")
    lines.append("=" * W)

    return "\n".join(lines)


def _pick_sample_points(total: int, max_samples: int = 15) -> set:
    """Pick evenly spaced indices plus first and last."""
    if total <= max_samples:
        return set(range(total))
    step = max(1, total // (max_samples - 2))
    points = {0, total - 1}
    for i in range(0, total, step):
        points.add(i)
    return points


# ── File loading ────────────────────────────────────────────────

def load_jsonl_lines(path: Path) -> List[dict]:
    """Load all JSON lines from a JSONL file."""
    results = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                results.append(json.loads(line))
    return results


def auto_discover(export_dir: Path) -> Tuple[Path, Path]:
    """Find real and sim JSONL files in an export directory."""
    real_path = export_dir / "CombatSimEvents.jsonl"
    sim_path = export_dir / "offline_trace.jsonl"

    if not real_path.exists():
        print(f"Error: {real_path} not found", file=sys.stderr)
        sys.exit(1)
    if not sim_path.exists():
        print(f"Error: {sim_path} not found", file=sys.stderr)
        print("  Run offline simulation first:", file=sys.stderr)
        print(f'  dotnet run --project BazaarEventLogger.Tests -- offline "{export_dir}"', file=sys.stderr)
        sys.exit(1)

    return real_path, sim_path


# ── Auto-matching ────────────────────────────────────────────────

def _extract_opponent_card_names(export_dir: Path) -> Optional[set]:
    """Extract opponent card names from selected_encounter.json."""
    enc_path = export_dir / "selected_encounter.json"
    if not enc_path.exists():
        return None
    try:
        enc = json.loads(enc_path.read_text(encoding="utf-8"))
        snapshot = enc.get("EncounterSnapshot", {})
        opponent = snapshot.get("Opponent", {})
        cards = opponent.get("Cards", [])
        return {c["Name"] for c in cards if "Name" in c}
    except Exception:
        return None


def _extract_card_stats_names(combat: dict) -> set:
    """Extract card names from a real combat's card_stats keys."""
    stats = combat.get("card_stats", {})
    # card_stats keys are formatted as "Name [InstanceId]" or just names
    names = set()
    for key in stats:
        # Strip instance id suffix like " [itm_xxx]"
        if " [" in key:
            name = key.rsplit(" [", 1)[0]
        else:
            name = key
        names.add(name)
    return names


def _match_by_card_fingerprint(real_lines: List[dict], export_dir: Path) -> int:
    """Find the real combat that best matches the encounter's opponent cards.

    Returns index into real_lines, or -1 if no match found.
    """
    opponent_names = _extract_opponent_card_names(export_dir)
    if not opponent_names:
        return -1

    best_idx = -1
    best_score = 0

    for i, combat in enumerate(real_lines):
        combat_names = _extract_card_stats_names(combat)
        # Count how many opponent card names appear in this combat's card_stats
        overlap = len(opponent_names & combat_names)
        if overlap > best_score:
            best_score = overlap
            best_idx = i

    # Require at least half the opponent cards to match
    if best_score >= max(1, len(opponent_names) // 2):
        return best_idx

    return -1


# ── Main ────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description="Compare real combat log with simulation trace.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument(
        "paths", nargs="+",
        help="Export directory (auto-discover), or two files: <real.jsonl> <sim.jsonl>",
    )
    parser.add_argument(
        "--real-index", type=int, default=-1,
        help="Which combat from real JSONL to use (default: -1 = last/most recent)",
    )
    parser.add_argument(
        "--output", "-o", type=str, default=None,
        help="Write report to file instead of stdout",
    )

    args = parser.parse_args()

    if len(args.paths) == 1:
        export_dir = Path(args.paths[0])
        if export_dir.is_dir():
            real_path, sim_path = auto_discover(export_dir)
        else:
            print(f"Error: {export_dir} is not a directory.", file=sys.stderr)
            print("Usage: combat_diff.py <export_dir>  OR  combat_diff.py <real.jsonl> <sim.jsonl>", file=sys.stderr)
            sys.exit(1)
    elif len(args.paths) == 2:
        real_path = Path(args.paths[0])
        sim_path = Path(args.paths[1])
    else:
        print("Error: provide either 1 directory or 2 file paths.", file=sys.stderr)
        sys.exit(1)

    # Load data
    real_lines = load_jsonl_lines(real_path)
    sim_lines = load_jsonl_lines(sim_path)

    if not real_lines:
        print(f"Error: no data in {real_path}", file=sys.stderr)
        sys.exit(1)
    if not sim_lines:
        print(f"Error: no data in {sim_path}", file=sys.stderr)
        sys.exit(1)

    # Select which real combat to compare
    # Try auto-matching by encounter_name or card fingerprint
    idx = args.real_index
    auto_matched = False

    if idx == -1 and len(real_lines) > 1:
        # Try encounter_name match first (Direction A: field present in newer logs)
        sim_data = sim_lines[0]
        sim_encounter = sim_data.get("encounter_name") or sim_data.get("encounter", "")

        if sim_encounter:
            for i, rl in enumerate(real_lines):
                if rl.get("encounter_name") == sim_encounter:
                    idx = i
                    auto_matched = True
                    print(f"[auto-match] Found real combat #{i+1} by encounter_name='{sim_encounter}'")
                    break

        # Fallback: card fingerprint matching
        if not auto_matched:
            idx = _match_by_card_fingerprint(real_lines, sim_path.parent)
            if idx >= 0:
                auto_matched = True
                print(f"[auto-match] Found real combat #{idx+1} by opponent card fingerprint")

    if not auto_matched:
        if idx < 0:
            idx = len(real_lines) + idx
        if idx < 0 or idx >= len(real_lines):
            print(f"Error: real-index {args.real_index} out of range (have {len(real_lines)} combats)", file=sys.stderr)
            sys.exit(1)

    print(f"Comparing: real combat #{idx + 1}/{len(real_lines)} from {real_path.name}")
    print(f"      with: simulation from {sim_path.name}")
    print()

    real_data = real_lines[idx]
    sim_data = sim_lines[0]  # sim always has one line

    real_summary = extract_real(real_data)
    sim_summary = extract_sim(sim_data)

    report = diff_report(real_summary, sim_summary)
    print(report)

    if args.output:
        out_path = Path(args.output)
        out_path.write_text(report, encoding="utf-8")
        print(f"Report saved to: {out_path}")


if __name__ == "__main__":
    main()
