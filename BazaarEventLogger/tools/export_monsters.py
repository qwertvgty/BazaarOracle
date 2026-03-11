"""
Export monster templates and card ability data for the battle simulator.
Generates monster_data.json in StreamingAssets for the BepInEx plugin to load.
"""
import sqlite3
import json
import os
import re

GAME_DIR = r"E:\SteamLibrary\steamapps\common\The Bazaar"
STREAMING = os.path.join(GAME_DIR, "TheBazaar_Data", "StreamingAssets")
DB_PATH = os.path.join(STREAMING, "GameData.db")
CARDS_PATH = os.path.join(STREAMING, "cards.json")
OUTPUT_PATH = os.path.join(STREAMING, "monster_data.json")


def load_cards_json():
    """Load and index cards.json by Id."""
    with open(CARDS_PATH, "r", encoding="utf-8") as f:
        data = json.load(f)
    cards = {}
    for version, card_list in data.items():
        for card in card_list:
            cid = card.get("Id", "")
            if cid and cid not in cards:
                cards[cid.lower()] = card
    return cards


def extract_card_effects(card_template, tier_name):
    """
    Extract simplified effects from a card template's abilities for a given tier.
    Returns list of {type, value, target} dicts.
    """
    effects = []

    # Get inherited tier attributes
    attrs = get_inherited_tier_attrs(card_template, tier_name)

    # Get which ability IDs are active for this tier
    tiers = card_template.get("Tiers", {})
    tier_data = tiers.get(tier_name, {})
    active_abilities = set(tier_data.get("AbilityIds", []))

    # Parse abilities
    abilities = card_template.get("Abilities", {})
    for aid, ability in abilities.items():
        if active_abilities and aid not in active_abilities:
            continue

        action = ability.get("Action", {})
        if not action:
            continue

        effect = parse_action(action, attrs, card_template)
        if effect:
            effects.append(effect)

    # Also extract tier-specific aura effects (passive modifiers)
    active_auras = set(tier_data.get("AuraIds", [])) if tier_data else set()
    auras = card_template.get("Auras", {})
    for aura_id, aura in auras.items():
        if active_auras and aura_id not in active_auras:
            continue
        aura_action = aura.get("Action", {})
        if aura_action:
            effect = parse_aura_action(aura_action, attrs)
            if effect:
                effects.append(effect)

    return effects


def resolve_value(value_obj, attrs):
    """Resolve a value reference to a concrete number."""
    if value_obj is None:
        return 0

    vtype = value_obj.get("$type", "")

    if vtype == "TFixedValue":
        return value_obj.get("Value", 0)

    if vtype == "TReferenceValueCardAttribute":
        attr_name = value_obj.get("AttributeType", "")
        default = value_obj.get("DefaultValue", 0)
        base_val = attrs.get(attr_name, default)

        modifier = value_obj.get("Modifier")
        if modifier:
            mod_val = resolve_value(modifier.get("Value"), attrs)
            mode = modifier.get("ModifyMode", "Add")
            should_round = modifier.get("ShouldRound", False)
            if mode == "Multiply":
                result = base_val * mod_val
            elif mode == "Add":
                result = base_val + mod_val
            else:
                result = base_val
            return round(result) if should_round else result
        return base_val

    if vtype == "TReferenceValueCardAttributeUnscaled":
        attr_name = value_obj.get("AttributeType", "")
        default = value_obj.get("DefaultValue", 0)
        return attrs.get(attr_name, default)

    if vtype == "TReferenceValuePlayerAttributeUnscaled":
        # Player attribute - we'll handle at runtime
        return value_obj.get("DefaultValue", 0)

    return 0


def get_target_mode(target_obj):
    """Get simplified target from a target object."""
    if not target_obj:
        return "unknown"
    ttype = target_obj.get("$type", "")
    if "Opponent" in ttype or target_obj.get("TargetMode") == "Opponent":
        return "opponent"
    if "Self" in ttype or target_obj.get("TargetMode") == "Player":
        return "self"
    if "CardSelf" in ttype:
        return "self_card"
    if "Random" in ttype:
        section = target_obj.get("TargetSection", "")
        if "Opponent" in section:
            return "opponent_card"
        return "self_card"
    if "PlayerAbsolute" in ttype:
        return target_obj.get("TargetMode", "self").lower()
    return "unknown"


def parse_action(action, attrs, card_template=None):
    """Parse a card ability action into a simplified effect."""
    atype = action.get("$type", "")

    if atype == "TActionPlayerModifyAttribute":
        attr_type = action.get("AttributeType", "")
        value = resolve_value(action.get("Value"), attrs)
        op = action.get("Operation", "Add")
        target = get_target_mode(action.get("Target"))

        # Determine effect type
        if attr_type == "Health":
            if target == "opponent":
                return {"type": "damage", "value": abs(value), "target": "opponent"}
            else:
                return {"type": "heal", "value": abs(value), "target": "self"}
        elif attr_type in ("Shield",):
            return {"type": "shield", "value": abs(value), "target": target}
        elif attr_type in ("Burn",):
            return {"type": "burn", "value": abs(value), "target": target}
        elif attr_type in ("Poison",):
            return {"type": "poison", "value": abs(value), "target": target}
        elif attr_type in ("Joy",):
            return {"type": "joy", "value": abs(value), "target": target}
        elif attr_type in ("Rage",):
            return {"type": "rage", "value": abs(value), "target": target}
        else:
            return {"type": f"modify_{attr_type}", "value": value, "target": target}

    if atype == "TActionCardBurn":
        value = resolve_value(action.get("Value"), attrs)
        if value == 0:
            value = attrs.get("BurnApplyAmount", 0)
        return {"type": "burn_apply", "value": abs(value) if value else attrs.get("BurnApplyAmount", 1), "target": "opponent"}

    if atype == "TActionCardPoison":
        value = resolve_value(action.get("Value"), attrs)
        if value == 0:
            value = attrs.get("PoisonApplyAmount", 0)
        return {"type": "poison_apply", "value": abs(value) if value else 1, "target": "opponent"}

    if atype == "TActionCardHaste":
        return {"type": "haste", "value": attrs.get("HasteAmount", 1000), "target": "self_card"}

    if atype == "TActionCardSlow":
        return {"type": "slow", "value": attrs.get("SlowAmount", 1000), "target": "opponent_card"}

    if atype == "TActionCardFreeze":
        return {"type": "freeze", "value": attrs.get("FreezeAmount", 1000), "target": "opponent_card"}

    if atype == "TActionCardShield":
        value = resolve_value(action.get("Value"), attrs)
        if value == 0:
            value = attrs.get("ShieldApplyAmount", 0)
        return {"type": "shield", "value": abs(value) if value else 0, "target": "self"}

    if atype == "TActionCardHeal":
        value = resolve_value(action.get("Value"), attrs)
        if value == 0:
            value = attrs.get("HealAmount", 0)
        return {"type": "heal", "value": abs(value) if value else 0, "target": "self"}

    if atype == "TActionCardDamage":
        value = resolve_value(action.get("Value"), attrs)
        if value == 0:
            value = attrs.get("DamageAmount", 0)
        return {"type": "damage", "value": abs(value) if value else 0, "target": "opponent"}

    if atype == "TActionPlayerDamage":
        # Most common damage action - uses card's DamageAmount attribute
        ref = action.get("ReferenceValue")
        value = resolve_value(ref, attrs) if ref else attrs.get("DamageAmount", 0)
        target = get_target_mode(action.get("Target"))
        return {"type": "damage", "value": abs(value) if value else attrs.get("DamageAmount", 0), "target": target or "opponent"}

    if atype == "TActionPlayerHeal":
        ref = action.get("ReferenceValue")
        value = resolve_value(ref, attrs) if ref else attrs.get("HealAmount", 0)
        target = get_target_mode(action.get("Target"))
        return {"type": "heal", "value": abs(value) if value else attrs.get("HealAmount", 0), "target": target or "self"}

    if atype == "TActionPlayerShield":
        ref = action.get("ReferenceValue")
        value = resolve_value(ref, attrs) if ref else attrs.get("ShieldApplyAmount", 0)
        target = get_target_mode(action.get("Target"))
        return {"type": "shield", "value": abs(value) if value else attrs.get("ShieldApplyAmount", 0), "target": target or "self"}

    if atype == "TActionCardModifyAttribute":
        # Modifies another card's attribute - complex but often used for buffs
        modified_attr = action.get("AttributeType", "")
        value = resolve_value(action.get("Value"), attrs)
        if modified_attr in ("ShieldApplyAmount", "DamageAmount", "HealAmount", "BurnApplyAmount"):
            return {"type": f"buff_{modified_attr}", "value": value, "target": "self_card"}
        return None  # Skip other card modifications for now

    if atype == "TActionPlayerBurn":
        value = resolve_value(action.get("ReferenceValue"), attrs) if action.get("ReferenceValue") else attrs.get("BurnApplyAmount", 0)
        return {"type": "burn_apply", "value": abs(value) if value else attrs.get("BurnApplyAmount", 1), "target": "opponent"}

    if atype == "TActionPlayerPoison":
        value = resolve_value(action.get("ReferenceValue"), attrs) if action.get("ReferenceValue") else attrs.get("PoisonApplyAmount", 0)
        return {"type": "poison_apply", "value": abs(value) if value else attrs.get("PoisonApplyAmount", 1), "target": "opponent"}

    # Composite actions
    if atype in ("TActionComposite", "TActionConditional"):
        actions = action.get("Actions", [])
        if actions:
            return parse_action(actions[0], attrs, card_template)
        inner = action.get("Action")
        if inner:
            return parse_action(inner, attrs, card_template)

    return None


def parse_aura_action(action, attrs):
    """Parse aura actions (passive effects)."""
    atype = action.get("$type", "")
    if "ModifyAttribute" in atype:
        attr_type = action.get("AttributeType", "")
        return {"type": f"aura_{attr_type}", "value": resolve_value(action.get("Value"), attrs), "target": get_target_mode(action.get("Target")), "passive": True}
    return None


TIER_ORDER = ["Bronze", "Silver", "Gold", "Diamond", "Legendary"]


def get_inherited_tier_attrs(card_template, target_tier):
    """
    Get the full attributes for a tier by inheriting from lower tiers.
    Bronze is base, Silver inherits Bronze + overrides, Gold inherits Silver + overrides, etc.
    """
    tiers = card_template.get("Tiers", {})
    merged = {}
    for tier_name in TIER_ORDER:
        tier_data = tiers.get(tier_name, {})
        tier_attrs = tier_data.get("Attributes", {})
        merged.update(tier_attrs)
        if tier_name == target_tier:
            break
    return merged


def resolve_card_for_monster(template_id, tier, cards_db):
    """Resolve a card template ID + tier into simulation-ready card data."""
    card = cards_db.get(template_id.lower())
    if not card:
        return None

    name = card.get("InternalName", "?")

    # Get inherited tier attributes (Bronze -> Silver -> Gold -> ...)
    tier_attrs = get_inherited_tier_attrs(card, tier)

    result = {
        "templateId": template_id,
        "name": name,
        "tier": tier,
        "cooldownMax": tier_attrs.get("CooldownMax", 0),
        "multicast": tier_attrs.get("Multicast", 1),
        "attributes": tier_attrs,
        "effects": extract_card_effects(card, tier)
    }

    return result


def build_encounter_to_monster_map(cards_db, monsters):
    """Build a mapping from encounter template ID to monster template."""
    encounter_map = {}

    # Get all combat encounters
    encounters = {}
    for cid, card in cards_db.items():
        if card.get("$type") == "TCardEncounterCombat":
            encounters[card["Id"].lower()] = card

    # Build monster name lookup
    monster_by_name = {}
    for mid, mdata in monsters.items():
        name = mdata.get("internalName", mdata.get("InternalName", ""))
        if name:
            monster_by_name[name.lower()] = mid

    for enc_id, enc in encounters.items():
        enc_name = enc.get("InternalName", "")
        enc_id_upper = enc["Id"]

        # Try exact match
        if enc_name.lower() in monster_by_name:
            encounter_map[enc_id_upper] = monster_by_name[enc_name.lower()]
            continue

        # Try without tier suffix "(Bronze)", "(Silver)", "(Gold)", "(Diamond)"
        base_name = re.sub(r'\s*\((?:Bronze|Silver|Gold|Diamond|Legendary)\)\s*$', '', enc_name)
        if base_name.lower() in monster_by_name:
            encounter_map[enc_id_upper] = monster_by_name[base_name.lower()]
            continue

        # Try adding "Monster" suffix
        if (base_name + " Monster").lower() in monster_by_name:
            encounter_map[enc_id_upper] = monster_by_name[(base_name + " Monster").lower()]
            continue

        # Fuzzy: check if encounter name contains a monster name
        for mname, mid in monster_by_name.items():
            if mname in enc_name.lower() or enc_name.lower() in mname:
                encounter_map[enc_id_upper] = mid
                break

    return encounter_map


def main():
    print("Loading cards.json...")
    cards_db = load_cards_json()
    print(f"  Loaded {len(cards_db)} card templates")

    print("Loading PlayerMonsterTemplates from GameData.db...")
    conn = sqlite3.connect(DB_PATH)
    cur = conn.cursor()
    cur.execute("SELECT Id, Data FROM PlayerMonsterTemplates")

    monsters = {}
    for row in cur.fetchall():
        mid = row[0]
        mdata = json.loads(row[1])

        # Resolve monster's cards
        hand_items = mdata.get("Player", {}).get("Hand", {}).get("Items", [])
        resolved_cards = []
        for item in hand_items:
            tid = item.get("TemplateId", "")
            tier = item.get("Tier", "Bronze")
            card_data = resolve_card_for_monster(tid, tier, cards_db)
            if card_data:
                card_data["socketId"] = item.get("SocketId", "")
                resolved_cards.append(card_data)

        # Resolve skills
        skills = mdata.get("Player", {}).get("Skills", [])
        resolved_skills = []
        for skill in skills:
            tid = skill.get("TemplateId", "")
            tier = skill.get("Tier", "Bronze")
            skill_data = resolve_card_for_monster(tid, tier, cards_db)
            if skill_data:
                resolved_skills.append(skill_data)

        monsters[mid] = {
            "id": mid,
            "internalName": mdata.get("InternalName", "?"),
            "player": {
                "attributes": mdata.get("Player", {}).get("Attributes", {}),
                "unlockedSlots": mdata.get("Player", {}).get("Hand", {}).get("UnlockedSlots", 0)
            },
            "cards": resolved_cards,
            "skills": resolved_skills
        }

    conn.close()
    print(f"  Loaded {len(monsters)} monster templates")

    # Build encounter mapping
    encounter_map = build_encounter_to_monster_map(cards_db, monsters)
    print(f"  Matched {len(encounter_map)} encounters to monster templates")

    # Output
    output = {
        "version": "1.0",
        "encounterToMonster": encounter_map,
        "monsters": monsters
    }

    with open(OUTPUT_PATH, "w", encoding="utf-8") as f:
        json.dump(output, f, indent=2, ensure_ascii=False)

    print(f"\nExported to {OUTPUT_PATH}")
    print(f"File size: {os.path.getsize(OUTPUT_PATH) / 1024:.1f} KB")

    # Print summary
    print("\n=== Monster Summary ===")
    for mid, m in sorted(monsters.items(), key=lambda x: x[1]["internalName"]):
        hp = m["player"]["attributes"].get("HealthMax", "?")
        cards_info = []
        for c in m["cards"]:
            fx = [e["type"] for e in c["effects"] if not e.get("passive")]
            cards_info.append(f"{c['name']}({c['tier']})[{','.join(fx) or '?'}]")
        print(f"  {m['internalName']}: HP={hp}, Cards=[{', '.join(cards_info)}]")


if __name__ == "__main__":
    main()
