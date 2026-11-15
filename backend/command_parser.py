"""Validate and sanitise raw VLM output into a safe, bounded rover Command.

Coordinates: the model sees a FRAME_W x FRAME_H image - either a 2x2 mosaic of four views or one upscaled FRONT view.
The parser finds which view the target box is in, converts it to that view's pixel space (Unity image size), and turns
the box centre into a bearing relative to the rover (view yaw + pinhole angle inside the view).
"""
import math
import re

from schema import ACTIONS, STATUSES, LIMITS, VIEWS, VIEW_YAWS, Command
from vlm import FRAME_W, FRAME_H


def _clamp(v, lo, hi):
    return max(lo, min(hi, v))


def _norm_label(s: str) -> str:
    return re.sub(r"[^a-z0-9]+", "_", (s or "").lower()).strip("_")[:40]


_GENERIC = {"the", "a", "an", "glowing", "largest", "biggest", "big", "huge", "ancient", "bright", "near", "to", "go"}


def _tokens(label: str) -> set[str]:
    return {w[:-1] if w.endswith("s") and len(w) > 3 else w
            for w in _norm_label(label).split("_") if w and w not in _GENERIC}


def _label_matches(target: str, nearby: list[str]) -> bool:
    """True if every meaningful word of the target appears in a nearby landmark label
    ("blue_crystal" matches "blue_crystal" but not "violet_crystal")."""
    t = _tokens(target)
    return bool(t) and any(t <= _tokens(n) for n in nearby)


def _fix_bbox(bbox, w, h):
    """Return a pixel bbox [x1,y1,x2,y2] in the model frame, or None. Handles normalised (0-1) output."""
    if not isinstance(bbox, list) or len(bbox) != 4:
        return None
    try:
        b = [float(v) for v in bbox]
    except (TypeError, ValueError):
        return None
    if all(v == 0 for v in b):
        return None
    if max(b) <= 1.0:
        b = [b[0] * w, b[1] * h, b[2] * w, b[3] * h]
    x1, x2 = sorted((_clamp(b[0], 0, w), _clamp(b[2], 0, w)))
    y1, y2 = sorted((_clamp(b[1], 0, h), _clamp(b[3], 0, h)))
    if x2 - x1 < 2 or y2 - y1 < 2:
        return None
    return [x1, y1, x2, y2]


def _wrap(a):
    return (a + 180.0) % 360.0 - 180.0


def parse(raw: dict, req, mosaic: bool) -> Command:
    st = req.state
    warn: list[str] = []

    action = str(raw.get("action", "STOP")).upper().strip()
    if action not in ACTIONS:
        warn.append(f"invalid action '{action}' -> STOP")
        action = "STOP"
    status = str(raw.get("status", "executing")).lower().strip()
    if status not in STATUSES:
        warn.append(f"invalid status '{status}'")
        status = "executing"
    try:
        duration = float(raw.get("duration", 1.0))
    except (TypeError, ValueError):
        duration = 1.0
        warn.append("bad duration -> 1.0")
    if not math.isfinite(duration):
        duration = 1.0

    target = _norm_label(raw.get("target", "")) or "unknown"
    if req.mission_target and target != req.mission_target:
        warn.append(f"target '{target}' -> mission goal '{req.mission_target}'")
        target = req.mission_target
    visible = bool(raw.get("target_visible", False))
    box = _fix_bbox(raw.get("target_bbox"), FRAME_W, FRAME_H) if visible else None
    if raw.get("grounding"):
        warn.append(raw["grounding"])
    if visible and box is None:
        visible = False
        warn.append("target marked visible but could not be localised")

    # ---- which view, and where inside it -------------------------------------------------
    tile, bearing, tile_box, area = -1, 0.0, None, 0.0
    if box:
        cx, cy = (box[0] + box[2]) / 2, (box[1] + box[3]) / 2
        if mosaic:
            tw, th = FRAME_W / 2, FRAME_H / 2
            tile = (1 if cx >= tw else 0) + (2 if cy >= th else 0)
            ox, oy = (tile % 2) * tw, (tile // 2) * th
        else:
            tw, th, tile, ox, oy = FRAME_W, FRAME_H, 0, 0, 0
        # box in view-local pixels, clipped to the view, then scaled to Unity's image size
        lx1, lx2 = _clamp(box[0] - ox, 0, tw), _clamp(box[2] - ox, 0, tw)
        ly1, ly2 = _clamp(box[1] - oy, 0, th), _clamp(box[3] - oy, 0, th)
        sx, sy = req.image_width / tw, req.image_height / th
        tile_box = [int(lx1 * sx), int(ly1 * sy), int(lx2 * sx), int(ly2 * sy)]
        area = (lx2 - lx1) * (ly2 - ly1) / (tw * th)
        if area > 0.85 and status != "arrived":
            # A box covering (almost) the whole view is not a localisation - don't navigate on it.
            warn.append(f"box covers {area:.0%} of the view -> not a usable localisation")
            box = None; tile = -1; tile_box = None; area = 0.0; visible = False
        else:
            u = ((lx1 + lx2) / 2) / tw
            half = math.radians(req.hfov_deg / 2)
            bearing = _wrap(VIEW_YAWS[tile] + math.degrees(math.atan((u - 0.5) * 2 * math.tan(half))))

    # ---- rule layer ---------------------------------------------------------------------
    seen = [h for h in req.history if "MOVE_TO_TARGET" in h]
    if status == "impossible" and seen and st.step + 1 < st.max_steps:
        warn.append("target seen earlier -> keep searching instead of giving up")
        action, status, duration = "MOVE_FORWARD", "searching", 2.5
    if (status == "arrived" and not tile_box and not st.obstacle_ahead and not _label_matches(target, st.nearby)
            and st.step + 1 < st.max_steps):
        # "Arrived" with nothing in front, no proximity confirmation and no box to check in 3D: not believable.
        warn.append("arrival claim unverifiable (no box, nothing nearby) -> keep exploring")
        action, status, duration = "MOVE_FORWARD", "searching", 2.5
    if status in ("arrived", "impossible") and action != "STOP":
        warn.append(f"status {status} -> STOP")
        action = "STOP"
    if action in ("MOVE_FORWARD", "TURN_LEFT", "TURN_RIGHT") and tile_box and status != "arrived":
        warn.append(f"{action} with located target -> MOVE_TO_TARGET (grounded navigation)")
        action, status = "MOVE_TO_TARGET", "executing"
    if action == "MOVE_TO_TARGET" and not tile_box:
        warn.append("MOVE_TO_TARGET without a located target -> explore forward")
        action, status, duration = "MOVE_FORWARD", "searching", 2.5
    if action != "STOP" and _label_matches(target, st.nearby):
        warn.append("proximity sensor confirms target nearby -> arrived")
        action, status = "STOP", "arrived"
    if st.step + 1 >= st.max_steps and action != "STOP":
        warn.append("step budget exhausted -> STOP")
        action = "STOP"

    # ---- hard limits --------------------------------------------------------------------
    speed = 0.0
    if action == "MOVE_TO_TARGET":
        duration = LIMITS["nav_leg"]; speed = LIMITS["speed_forward"]       # bounded navigation leg
    elif action == "MOVE_FORWARD":
        duration = _clamp(duration, *LIMITS["move_duration"]); speed = LIMITS["speed_forward"]
    elif action == "MOVE_BACKWARD":
        duration = _clamp(duration, *LIMITS["move_duration"]); speed = LIMITS["speed_backward"]
    elif action in ("TURN_LEFT", "TURN_RIGHT"):
        duration = _clamp(duration, *LIMITS["turn_duration"])
    else:
        duration = 0.0

    direction = "none"
    if tile_box:
        direction = ("ahead" if abs(bearing) < 12 else "behind" if abs(bearing) > 135
                     else "right" if bearing > 0 else "left")

    return Command(
        action=action, target=target, intent=str(raw.get("intent", ""))[:120], direction=direction,
        view=VIEWS[tile] if tile >= 0 else "", tile=tile, bearing_deg=round(bearing, 1),
        duration=round(duration, 2), speed=speed, status=status,
        reason=str(raw.get("reason", ""))[:200], observation=str(raw.get("observation", ""))[:200],
        target_visible=visible, target_bbox=tile_box or [0, 0, 0, 0], warnings=warn, step=st.step,
    )
