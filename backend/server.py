"""MOSSBOUND backend: Unity  <-HTTP/JSON->  FastAPI  <->  Ollama vision-language model.

Run:  python server.py            (real VLM)
      set MOSS_MOCK=1 & python server.py   (DEV MODE: scripted replies, no model; clearly labelled)
"""
import base64
import json
import os
import time
from contextlib import asynccontextmanager
from pathlib import Path

import uvicorn
from fastapi import FastAPI
from fastapi.responses import HTMLResponse

import command_parser
import plan_parser
import vlm
from schema import ActRequest, Command, Plan, PlanRequest, PlanStep, VIEWS, DetectRequest, DetectResult, Detection

MOCK = os.environ.get("MOSS_MOCK", "0") == "1"
LOG_DIR = Path(__file__).parent / "logs"
LOG_DIR.mkdir(exist_ok=True)
_frame_no = -1


@asynccontextmanager
async def lifespan(_app):
    if not MOCK:
        print(f"[moss] warming up {vlm.MODEL} ...")
        await vlm.warmup()
        g = await vlm.gpu_status()
        print(f"[moss] model ready - {g.get('gpu_percent', -1)}% of it on the GPU")
        if 0 <= g.get("gpu_percent", -1) < 100:
            print("[moss] WARNING: the model does not fit on the GPU -> replies will be slow. Close GPU-heavy apps "
                  "(or restart Ollama / the PC if the GPU is in a bad state), and start this backend BEFORE Unity.")
    yield


app = FastAPI(title="MOSSBOUND VLM Backend", lifespan=lifespan)


@app.get("/health")
async def health():
    h = {"status": "ok", "mode": "mock" if MOCK else "vlm"}
    h.update(await vlm.health())
    return h


async def ground_any(req: ActRequest, order: list[str]):
    """Ground the goal in the given views, in order; first hit wins. Returns (view, bbox) or ("", None)."""
    for v in order:
        b = await vlm.ground(req.images_b64[VIEWS.index(v)], req.mission_target, req.target_description, req.time_of_day)
        if b is not None:
            return v, b
    return "", None


@app.post("/act", response_model=Command)
async def act(req: ActRequest):
    t0 = time.perf_counter()
    # Qwen2.5-VL in Ollama rescales small inputs internally, which breaks pixel coordinates.
    # Measured: at 1288x728 its bbox coordinates match the image 1:1, so everything is normalised to that frame.
    mosaic = len(req.images_b64) == 4
    if mosaic:
        frame_b64, _ = vlm.make_mosaic(req.images_b64)     # 360-degree view in a single image
    elif req.image_b64:
        frame_b64 = vlm.to_model_frame(req.image_b64)
    else:
        return Command(action="STOP", status="impossible", reason="No image received.", warnings=["no_image"])
    try:  # keep the exact frame the model saw (GET /), plus a rolling set of the last 40 for debugging
        (LOG_DIR / "last_frame.jpg").write_bytes(base64.b64decode(frame_b64))
        global _frame_no
        _frame_no = (_frame_no + 1) % 40
        (LOG_DIR / "frames").mkdir(exist_ok=True)
        (LOG_DIR / "frames" / f"{_frame_no:02d}.jpg").write_bytes(base64.b64decode(frame_b64))
    except Exception:  # noqa: BLE001
        pass

    raw_text = ""
    if MOCK:
        raw = {"observation": "DEV MODE - no model consulted", "target": "mock", "target_visible": False,
               "target_bbox": [0, 0, 0, 0], "action": "TURN_RIGHT" if req.state.step < 3 else "STOP",
               "duration": 0.8, "status": "searching" if req.state.step < 3 else "impossible",
               "reason": "Mock mode: scripted reply for testing Unity movement."}
    else:
        try:
            raw, b, view = None, None, ""
            hint = req.view_hint.upper()
            if mosaic and req.mission_target:
                # Grounding first: look for the (already decided) goal in every view, the planner's view first.
                # Found -> drive there without the slower reasoning pass.
                first = hint if hint in VIEWS else "FRONT"
                view, b = await ground_any(req, [first] + [v for v in VIEWS if v != first])
                if b is not None:
                    name = req.mission_target.replace('_', ' ')
                    raw = {"intent": f"go to the {name}", "target": req.mission_target,
                           "observation": f"I can see the {name} in my {view.lower()} camera.",
                           "target_visible": True, "target_view": view, "action": "MOVE_TO_TARGET", "duration": 0,
                           "status": "executing", "reason": f"Grounding located the {name} in the {view.lower()} view.",
                           "grounding": f"grounded in {view}" + (" (planner's view)" if view == hint else "")}
                    raw_text = json.dumps(raw)
            if raw is None:
                # Not found by grounding (or no fixed goal): the reasoning pass decides how to search / whether it exists.
                raw, raw_text = await vlm.query(req, frame_b64, mosaic)
                view = str(raw.get("target_view", "NONE")).upper()
                if req.mission_target and mosaic:
                    if raw.get("target_visible"):
                        raw["grounding"] = "reasoning claims it is visible, but grounding found it in no view"
                    raw["target_visible"] = False
                    b = None
                elif raw.get("target_visible") and (view in VIEWS or not mosaic):
                    vi = VIEWS.index(view) if (mosaic and view in VIEWS) else 0
                    src = req.images_b64[vi] if mosaic else req.image_b64
                    b = await vlm.ground(src, str(raw.get("target", "")), req.target_description, req.time_of_day)
            # Stage 2 result: place the view's box into model-frame coordinates.
            raw["target_bbox"] = [0, 0, 0, 0]
            if raw.get("target_visible") and (view in VIEWS or not mosaic):
                vi = VIEWS.index(view) if (mosaic and view in VIEWS) else 0
                if b is None:
                    raw["target_visible"] = False
                    raw["grounding"] = f"grounding found no {raw.get('target')} in {view}"
                elif mosaic:  # place the view's box into mosaic coordinates (each view = half the model frame)
                    ox, oy = (vi % 2) * vlm.FRAME_W / 2, (vi // 2) * vlm.FRAME_H / 2
                    raw["target_bbox"] = [ox + b[0] / 2, oy + b[1] / 2, ox + b[2] / 2, oy + b[3] / 2]
                else:
                    raw["target_bbox"] = b
        except vlm.VLMError as e:
            cmd = Command(action="STOP", status="impossible", reason=str(e), warnings=["vlm_error"],
                          step=req.state.step, model=vlm.MODEL)
            cmd.latency_ms = int((time.perf_counter() - t0) * 1000)
            return cmd

    cmd = command_parser.parse(raw, req, mosaic)
    cmd.latency_ms = int((time.perf_counter() - t0) * 1000)
    cmd.model = "mock" if MOCK else vlm.MODEL
    cmd.mode = "mock" if MOCK else "vlm"

    with open(LOG_DIR / "requests.jsonl", "a", encoding="utf-8") as f:
        f.write(json.dumps({"t": time.time(), "instruction": req.instruction, "state": req.state.model_dump(),
                            "raw": raw_text or raw, "command": cmd.model_dump()}) + "\n")
    print(f"[moss] step {req.state.step} '{req.instruction}' -> {cmd.action} {cmd.target} "
          f"bearing={cmd.bearing_deg} dur={cmd.duration} status={cmd.status} ({cmd.latency_ms} ms) {cmd.warnings}")
    return cmd


def _mock_plan(req: PlanRequest) -> Plan:
    return Plan(understanding="DEV MODE - no model consulted; scripted plan.", reply="Mock mode: I'll do a little dance!",
                steps=[PlanStep(skill="DANCE")], mode="mock", model="mock")


@app.post("/plan", response_model=Plan)
async def plan(req: PlanRequest):
    """Stage 0: understand a free-form player command and turn it into a short, validated skill plan."""
    t0 = time.perf_counter()
    if MOCK:
        return _mock_plan(req)
    mosaic_b64 = vlm.make_mosaic(req.images_b64)[0] if len(req.images_b64) == 4 else None
    try:
        raw, raw_text = await vlm.plan(req, mosaic_b64)
        p = plan_parser.parse(raw, req.instruction)
    except vlm.VLMError as e:
        p = Plan(understanding="", reply="Oops, my thoughts got tangled. Try again?", steps=[PlanStep(skill="SAY")],
                 warnings=[f"planner_error: {e}"])
        raw_text = ""
    p.latency_ms = int((time.perf_counter() - t0) * 1000)
    p.model = vlm.MODEL
    with open(LOG_DIR / "plans.jsonl", "a", encoding="utf-8") as f:
        f.write(json.dumps({"t": time.time(), "instruction": req.instruction, "quest": req.quest, "time": req.time_of_day,
                            "raw": raw_text, "plan": p.model_dump()}) + "\n")
    print(f"[moss] plan '{req.instruction}' -> {[(s.skill, s.target or s.amount) for s in p.steps]} "
          f"({p.latency_ms} ms) {p.understanding!r} unsure={p.unsure} reply={p.reply!r} options={p.options} {p.warnings}")
    return p


@app.post("/detect", response_model=DetectResult)
async def detect(req: DetectRequest):
    """What MOSS sees right now (FRONT camera): free-text labels + boxes, for the on-screen overlay while it drives."""
    t0 = time.perf_counter()
    if MOCK:
        return DetectResult()
    try:
        objs = await vlm.detect(req.image_b64, req.time_of_day)
    except Exception as e:  # noqa: BLE001
        print(f"[moss] detect failed: {e}")
        objs = []
    sx, sy = 644 / vlm.FRAME_W, 364 / vlm.FRAME_H        # model frame -> Unity view pixels
    res = DetectResult(objects=[Detection(label=o["label"], landmark=plan_parser.snap(o["label"]) if plan_parser.snap(o["label"]) in plan_parser.LANDMARKS else "",
                                          bbox=[int(o["bbox_2d"][0] * sx), int(o["bbox_2d"][1] * sy), int(o["bbox_2d"][2] * sx), int(o["bbox_2d"][3] * sy)])
                                for o in objs])
    res.latency_ms = int((time.perf_counter() - t0) * 1000)
    print(f"[moss] detect -> {[(d.label, d.landmark) for d in res.objects]} ({res.latency_ms} ms)")
    return res


@app.get("/", response_class=HTMLResponse)
async def index():
    return ("<body style='background:#0b1420;color:#bfe;font-family:sans-serif;padding:2em'>"
            "<h2>MOSSBOUND backend is running</h2><p>Last frame seen by MOSS:</p>"
            "<img src='/frame' style='max-width:640px;border-radius:8px'>"
            "<p><a style='color:#6fd' href='/health'>/health</a> &middot; <a style='color:#6fd' href='/docs'>/docs</a></p></body>")


@app.get("/frame")
async def frame():
    from fastapi.responses import FileResponse
    return FileResponse(LOG_DIR / "last_frame.jpg")


if __name__ == "__main__":
    uvicorn.run(app, host="127.0.0.1", port=int(os.environ.get("MOSS_PORT", "8765")))
