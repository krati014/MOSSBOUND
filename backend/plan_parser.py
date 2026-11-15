"""Validate the planner's JSON into a safe, bounded Plan.

The planner may say anything; only whitelisted skills with clamped amounts reach Unity. Free-form target names are
kept (open vocabulary - grounding finds them by description) but obvious synonyms of known landmarks are snapped to
the landmark label so the rover's proximity sensor can confirm arrival.
"""
import math
import re

from schema import SKILLS, VIEWS, LIMITS, Plan, PlanStep
from prompts import mentions_mission

LANDMARKS = ["largest_tree", "pond_water", "glowing_mushrooms", "blue_crystal", "red_flower", "portal", "home"]

# word sets that identify a landmark (all words of one set must appear)
_SYNONYMS = [
    ({"ancient"}, "largest_tree"), ({"largest", "tree"}, "largest_tree"), ({"biggest", "tree"}, "largest_tree"),
    ({"giant", "tree"}, "largest_tree"), ({"huge", "tree"}, "largest_tree"), ({"oldest", "tree"}, "largest_tree"),
    ({"fruit"}, "largest_tree"), ({"big", "tree"}, "largest_tree"), ({"apple"}, "largest_tree"), ({"apples"}, "largest_tree"),
    ({"pond"}, "pond_water"), ({"water"}, "pond_water"), ({"lake"}, "pond_water"),
    ({"mushroom"}, "glowing_mushrooms"), ({"mushrooms"}, "glowing_mushrooms"),
    ({"crystal"}, "blue_crystal"), ({"crystals"}, "blue_crystal"),
    ({"flower"}, "red_flower"), ({"bloom"}, "red_flower"), ({"rose"}, "red_flower"),
    ({"portal"}, "portal"), ({"doorway"}, "portal"), ({"ring"}, "portal"), ({"gate"}, "portal"),
    ({"home"}, "home"), ({"start"}, "home"), ({"charging"}, "home"),
]


# player-facing names used in MOSS's own questions
_DISPLAY = [("big tree", "largest_tree"), ("pond", "pond_water"), ("mushroom", "glowing_mushrooms"), ("crystal", "blue_crystal"),
            ("red flower", "red_flower"), ("magic gate", "portal"), ("home", "home")]

_OTHER_OBJECTS = {"rock", "rocks", "stone", "stones", "lantern", "lanterns", "path", "firefly", "fireflies", "small",
                  "pillar", "pillars", "bush", "grass", "pad", "pads"}


_NOT_TARGETS = {"left", "right", "front", "back", "behind", "ahead", "there", "it", "that", "this", "none", "unknown"}


def norm_label(s: str) -> str:
    return re.sub(r"[^a-z0-9]+", "_", (s or "").lower()).strip("_")[:40]


def snap(target: str) -> str:
    t = re.sub(r"^(a|an|the|some)_", "", norm_label(target))
    if t in LANDMARKS:
        return t
    words = set(t.split("_"))
    if "home" in words or t in ("magic_gate", "gate"):
        return "home" if "home" in words else "portal"
    if words & _OTHER_OBJECTS:          # "rock_near_pond" is a rock, not the pond
        return t
    for need, label in _SYNONYMS:
        if need <= words:
            return label
    return t


def _clean(v) -> str:
    """Plain text only (the HUD font has no emoji)."""
    return "".join(ch for ch in str(v or "") if ord(ch) < 0x2600 or 0x2600 <= ord(ch) < 0x2700 and ch in "★☆♪").strip()


def _num(v, default=0.0):
    try:
        f = float(v)
        return f if math.isfinite(f) else default
    except (TypeError, ValueError):
        return default


def parse(raw: dict, instruction: str = "") -> Plan:
    warn: list[str] = []
    steps: list[PlanStep] = []
    for s in (raw.get("steps") or [])[: LIMITS["plan_steps"] + 2]:
        if not isinstance(s, dict):
            continue
        skill = str(s.get("skill", "")).upper().strip()
        if skill not in SKILLS:
            warn.append(f"unknown skill '{skill}' dropped")
            continue
        target, desc = str(s.get("target", "")), str(s.get("description", ""))[:120]
        view = str(s.get("view", "NONE")).upper()
        view = view if view in VIEWS else ""
        amount = _num(s.get("amount"))
        if skill == "GO_TO":
            if norm_label(target) in _NOT_TARGETS or len(norm_label(target)) < 3:
                target = desc                      # "left" is where it is, not what it is
            t = snap(target or desc)
            if not t:
                warn.append("GO_TO without a target dropped")
                continue
            if t == "home":
                skill, t, view = "COME_BACK", "", ""
            if t != norm_label(target) and t:
                warn.append(f"target '{target}' -> landmark '{t}'")
            target = t
        elif skill == "MOVE":
            lim = LIMITS["move_distance"]
            if abs(amount) < 0.3:
                amount = 3.0
            if abs(amount) > lim:
                warn.append(f"MOVE {amount:g} m clamped to ±{lim:g} m")
            amount = max(-lim, min(lim, amount))
            target, desc, view = "", "", ""
        elif skill == "TURN":
            lim = LIMITS["turn_degrees"]
            if abs(amount) < 5:
                amount = 90.0
            amount = max(-lim, min(lim, amount))
            target, desc, view = "", "", ""
        else:
            target, desc, view, amount = "", "", "", 0.0
        steps.append(PlanStep(request=_clean(s.get("request", ""))[:80], skill=skill, target=target, description=desc, view=view, amount=round(amount, 1)))
        if skill == "STOP":
            break
    # Padding guard: a step that repeats an earlier step's request (or is an identical copy) was not asked for.
    kept, seen = [], set()
    for st in steps:
        key = f"{st.skill}:{norm_label(st.request) or st.target + str(st.amount)}"
        if key in seen or (kept and kept[-1].skill == st.skill and kept[-1].target == st.target and st.skill not in ("MOVE", "TURN")):
            warn.append(f"padding step {st.skill} dropped")
            continue
        seen.add(key); kept.append(st)
    steps = kept
    reqs = [st.request for st in steps]
    if len(steps) > LIMITS["plan_steps"]:
        warn.append(f"plan truncated to {LIMITS['plan_steps']} steps")
        steps = steps[: LIMITS["plan_steps"]]
    reply = _clean(raw.get("reply", ""))[:140]
    if not steps:
        steps = [PlanStep(skill="SAY")]
        if not reply:
            reply = "Hmm, I'm not sure what you mean. Can you say it another way?"
    opts = []
    for o in (raw.get("options") or [])[:3]:
        o = _clean(o)[:60]
        if o and o.lower() not in (x.lower() for x in opts):
            opts.append(o)
    unsure = bool(raw.get("unsure")) and bool(reply)
    if not unsure and reply.rstrip().endswith("?") and mentions_mission(instruction) and any(st.skill not in ("SAY", "STOP") for st in steps):
        unsure = True        # "Next is the Big Tree. Shall we go?" -> wait for the player's YES instead of driving off
    if unsure and any(st.skill not in ("SAY", "STOP") for st in steps):
        # MOSS's question is already the talking part; keep only the actions it offers to do if the player says yes.
        acts = [st for st in steps if st.skill not in ("SAY", "STOP", "LOOK_AROUND")]
        steps = acts or steps
    if unsure:
        # YES must do what MOSS's question says: "Do you mean the Red Flower?" -> GO_TO red_flower.
        asked = [lm for name, lm in _DISPLAY if name in reply.lower()]
        if len(asked) == 1 and not any(st.target == asked[0] or (asked[0] == "home" and st.skill == "COME_BACK") for st in steps):
            warn.append(f"YES-plan aligned with the question -> {asked[0]}")
            steps = [PlanStep(request=reply[:80], skill="COME_BACK" if asked[0] == "home" else "GO_TO",
                              target="" if asked[0] == "home" else asked[0])]
    return Plan(requests=reqs, understanding=_clean(raw.get("understanding", ""))[:220], reply=reply, unsure=unsure,
                options=opts if unsure else [], steps=steps, warnings=warn)
