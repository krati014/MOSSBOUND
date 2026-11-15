"""MOSSBOUND data contracts.

Three layers:
  ActRequest   - what Unity sends (4 camera views + instruction + rover sensor state)
  VLM_SCHEMA   - the JSON schema the vision-language model is *forced* to emit
  Command      - the validated, clamped command the backend returns to Unity
"""
from typing import Optional
from pydantic import BaseModel, Field

ACTIONS = ["MOVE_FORWARD", "MOVE_BACKWARD", "TURN_LEFT", "TURN_RIGHT", "STOP", "MOVE_TO_TARGET"]
STATUSES = ["executing", "searching", "arrived", "impossible"]
VIEWS = ["FRONT", "RIGHT", "BACK", "LEFT"]          # mosaic order: top-left, top-right, bottom-left, bottom-right
VIEW_YAWS = [0.0, 90.0, 180.0, -90.0]              # camera yaw of each view relative to rover forward
# High-level skills the language planner may use (each is mapped to bounded rover motion in Unity).
SKILLS = ["GO_TO", "MOVE", "TURN", "LOOK_AROUND", "DANCE", "COME_BACK", "SAY", "STOP"]

# Hard safety limits (enforced server-side, and again in Unity).
LIMITS = {
    "move_duration": (0.3, 3.0),   # s, MOVE_FORWARD / MOVE_BACKWARD
    "turn_duration": (0.2, 1.5),   # s (turn rate fixed in Unity -> max ~90 deg)
    "nav_leg": 20.0,               # s, max length of one MOVE_TO_TARGET navigation leg
    "speed_forward": 3.0,          # m/s
    "speed_backward": 1.2,         # m/s
    "turn_rate": 70.0,             # deg/s
    "move_distance": 10.0,         # m, max |amount| of one planner MOVE step
    "turn_degrees": 360.0,         # deg, max |amount| of one planner TURN step
    "plan_steps": 4,               # max steps in one plan
}


class RoverState(BaseModel):
    step: int = 0
    max_steps: int = 8
    heading_deg: float = 0.0
    obstacle_ahead: bool = False       # forward ray-cast hit something close
    obstacle_distance: float = 99.0
    nearby: list[str] = []             # proximity sensor: landmark labels within ~2.6 m
    last_action: Optional[str] = None
    last_result: Optional[str] = None


class ActRequest(BaseModel):
    instruction: str = Field(min_length=1, max_length=300)
    image_b64: str = ""                # single forward frame (legacy / fallback)
    images_b64: list[str] = []         # 4 views: FRONT, RIGHT, BACK, LEFT
    image_width: int = 644             # size of ONE view
    image_height: int = 364
    hfov_deg: float = 90.0
    state: RoverState = RoverState()
    history: list[str] = []            # short summaries of previous steps
    mission_target: str = ""           # goal chosen by the planner; kept for the rest of the GO_TO
    target_description: str = ""       # planner's visual description of the goal (used for grounding)
    view_hint: str = ""                # view in which the planner already saw the goal -> ground directly
    time_of_day: str = "night"


class Command(BaseModel):
    action: str
    target: str = ""
    intent: str = ""                   # the model's concrete reading of the (possibly vague) instruction
    direction: str = "none"            # ahead / right / behind / left / none
    view: str = ""                     # which camera view the target was found in
    tile: int = -1                     # index into VIEWS (-1 = not located)
    bearing_deg: float = 0.0           # +right, relative to rover forward, -180..180
    duration: float = 0.0
    speed: float = 0.0
    turn_rate: float = LIMITS["turn_rate"]
    status: str = "executing"
    reason: str = ""
    observation: str = ""
    target_visible: bool = False
    target_bbox: list[int] = [0, 0, 0, 0]   # in the pixels of ONE view (Unity image size)
    warnings: list[str] = []
    step: int = 0
    latency_ms: int = 0
    model: str = ""
    mode: str = "vlm"                  # "vlm" or "mock" (dev mode, clearly labelled)


class PlanRequest(BaseModel):
    instruction: str = Field(min_length=1, max_length=300)
    images_b64: list[str] = []         # 4 views: FRONT, RIGHT, BACK, LEFT
    image_width: int = 644
    image_height: int = 364
    time_of_day: str = "night"
    quest: str = ""                    # current mission objective shown to the player (game context)
    context: str = ""                  # e.g. "MOSS asked for help: could not reach blue_crystal"
    nearby: list[str] = []
    recent: list[str] = []             # previous player instructions this session


class PlanStep(BaseModel):
    request: str = ""                  # the part of the player's words this step fulfils
    skill: str
    target: str = ""
    description: str = ""
    view: str = ""
    amount: float = 0.0


class Plan(BaseModel):
    requests: list[str] = []
    understanding: str = ""
    reply: str = ""
    unsure: bool = False               # MOSS wants to double-check with the player before acting
    options: list[str] = []            # other things the player might mean (short commands)
    steps: list[PlanStep] = []
    warnings: list[str] = []
    latency_ms: int = 0
    model: str = ""
    mode: str = "vlm"


# Planner JSON schema (stage 0, once per player command): understand -> reply -> plan.
PLAN_SCHEMA = {
    "type": "object",
    "properties": {
        "understanding": {"type": "string"},
        "unsure": {"type": "boolean"},
        "reply": {"type": "string"},
        "options": {"type": "array", "items": {"type": "string"}, "maxItems": 3},
        "steps": {
            "type": "array",
            "minItems": 1,
            "maxItems": 4,
            "items": {
                "type": "object",
                "properties": {
                    "request": {"type": "string"},
                    "skill": {"type": "string", "enum": SKILLS},
                    "target": {"type": "string"},
                    "description": {"type": "string"},
                    "view": {"type": "string", "enum": VIEWS + ["NONE"]},
                    "amount": {"type": "number"},
                },
                "required": ["request", "skill", "target", "description", "view", "amount"],
            },
        },
    },
    "required": ["understanding", "unsure", "reply", "options", "steps"],
}


# Stage-2 grounding schema (Qwen2.5-VL's native bbox_2d format, on ONE full-resolution view).
GROUND_SCHEMA = {
    "type": "object",
    "properties": {"bbox_2d": {"type": "array", "items": {"type": "integer"}, "minItems": 4, "maxItems": 4}},
    "required": ["bbox_2d"],
}

# Stage-1 JSON schema handed to Ollama's `format` parameter -> constrained decoding.
# Field order matters: the model interprets, then looks, then decides.
VLM_SCHEMA = {
    "type": "object",
    "properties": {
        "intent": {"type": "string"},
        "observation": {"type": "string"},
        "target": {"type": "string"},
        "target_visible": {"type": "boolean"},
        "target_view": {"type": "string", "enum": VIEWS + ["NONE"]},
        "action": {"type": "string", "enum": ACTIONS},
        "duration": {"type": "number"},
        "status": {"type": "string", "enum": STATUSES},
        "reason": {"type": "string"},
    },
    "required": ["intent", "observation", "target", "target_visible", "target_view",
                 "action", "duration", "status", "reason"],
}


# Live object detection on MOSS's front camera (shown on screen while it drives).
class DetectRequest(BaseModel):
    image_b64: str
    target: str = ""                   # current goal label (for highlighting only)
    time_of_day: str = "night"


class Detection(BaseModel):
    label: str                         # what the VLM calls it (free text, 1-3 words)
    landmark: str = ""                 # garden label it corresponds to, if any (largest_tree, ...)
    bbox: list[int] = [0, 0, 0, 0]     # pixels of the Unity view (644x364)


class DetectResult(BaseModel):
    objects: list[Detection] = []
    latency_ms: int = 0


DETECT_SCHEMA = {
    "type": "object",
    "properties": {"objects": {"type": "array", "maxItems": 6, "items": {
        "type": "object",
        "properties": {"label": {"type": "string"},
                       "bbox_2d": {"type": "array", "items": {"type": "integer"}, "minItems": 4, "maxItems": 4}},
        "required": ["label", "bbox_2d"]}}},
    "required": ["objects"],
}
