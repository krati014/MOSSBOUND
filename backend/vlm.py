"""Vision-language model client (Ollama). Swap this module to change model provider."""
import base64
import io
import json
import os
import re

import httpx
from PIL import Image, ImageDraw, ImageFont

from prompts import SYSTEM_PROMPT, PLANNER_PROMPT, build_user_prompt, build_plan_prompt
from schema import VLM_SCHEMA, GROUND_SCHEMA, PLAN_SCHEMA, DETECT_SCHEMA, VIEWS

OLLAMA_URL = os.environ.get("OLLAMA_URL", "http://127.0.0.1:11434")
MODEL = os.environ.get("MOSS_MODEL", "qwen2.5vl:7b")
# Keep the model resident while the backend runs: if it is unloaded and later reloaded while Unity holds GPU memory,
# Ollama can only fit part of it on the GPU (or none) and every reply becomes 10-20x slower.
KEEP_ALIVE = -1
# The planner only needs to recognise things, not locate them precisely -> a half-size mosaic (4x fewer image tokens).
PLAN_SCALE = float(os.environ.get("MOSS_PLAN_SCALE", "0.5"))

# Model-native frame: measured that Qwen2.5-VL (Ollama) returns bbox pixel coordinates 1:1 at this size.
FRAME_W, FRAME_H = 1288, 728


class VLMError(Exception):
    pass


def _decode(b64: str) -> Image.Image:
    return Image.open(io.BytesIO(base64.b64decode(b64))).convert("RGB")


def _encode(im: Image.Image) -> str:
    buf = io.BytesIO()
    im.save(buf, "JPEG", quality=88)
    return base64.b64encode(buf.getvalue()).decode()


def _font(size: int):
    try:
        return ImageFont.load_default(size=size)
    except TypeError:  # very old Pillow
        return ImageFont.load_default()


def to_model_frame(image_b64: str) -> str:
    """Single view -> FRAME_W x FRAME_H."""
    im = _decode(image_b64)
    if im.size != (FRAME_W, FRAME_H):
        im = im.resize((FRAME_W, FRAME_H), Image.BICUBIC)
    return _encode(im)


def make_mosaic(images_b64: list[str]) -> tuple[str, Image.Image]:
    """4 views -> labelled 2x2 mosaic of exactly FRAME_W x FRAME_H (each tile FRAME_W/2 x FRAME_H/2)."""
    tw, th = FRAME_W // 2, FRAME_H // 2
    mosaic = Image.new("RGB", (FRAME_W, FRAME_H))
    d = ImageDraw.Draw(mosaic)
    f = _font(22)
    for i, b64 in enumerate(images_b64[:4]):
        tile = _decode(b64)
        if tile.size != (tw, th):
            tile = tile.resize((tw, th), Image.BICUBIC)
        ox, oy = (i % 2) * tw, (i // 2) * th
        mosaic.paste(tile, (ox, oy))
        label = VIEWS[i]
        d.rectangle([ox + 6, oy + 6, ox + 18 + 14 * len(label), oy + 36], fill=(0, 0, 0))
        d.text((ox + 12, oy + 9), label, fill=(255, 255, 255), font=f)
    d.line([(tw, 0), (tw, FRAME_H)], fill=(255, 255, 255), width=2)
    d.line([(0, th), (FRAME_W, th)], fill=(255, 255, 255), width=2)
    return _encode(mosaic), mosaic


async def health() -> dict:
    try:
        async with httpx.AsyncClient(timeout=3) as c:
            tags = (await c.get(f"{OLLAMA_URL}/api/tags")).json()
        names = [m["name"] for m in tags.get("models", [])]
        return {"ollama": True, "model": MODEL, "model_available": MODEL in names, "installed": names, **(await gpu_status())}
    except Exception as e:  # noqa: BLE001
        return {"ollama": False, "model": MODEL, "model_available": False, "error": str(e)}


async def gpu_status() -> dict:
    """How much of the loaded model sits in GPU memory (Ollama /api/ps). 100 = fast; less = partly on the CPU (slow)."""
    try:
        async with httpx.AsyncClient(timeout=3) as c:
            ps = (await c.get(f"{OLLAMA_URL}/api/ps")).json()
        for m in ps.get("models", []):
            if m.get("name") == MODEL or m.get("model") == MODEL:
                size = max(1, int(m.get("size") or 1))
                return {"loaded": True, "gpu_percent": int(round(100 * int(m.get("size_vram") or 0) / size))}
        return {"loaded": False, "gpu_percent": -1}
    except Exception:  # noqa: BLE001
        return {"loaded": False, "gpu_percent": -1}


async def warmup():
    """Load the model into VRAM so the first real request is fast."""
    try:
        async with httpx.AsyncClient(timeout=180) as c:
            await c.post(f"{OLLAMA_URL}/api/generate",
                         json={"model": MODEL, "prompt": "ok", "keep_alive": KEEP_ALIVE, "options": {"num_predict": 1}})
    except Exception:  # noqa: BLE001
        pass


def _extract_json(text: str) -> dict:
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        m = re.search(r"\{.*\}", text, re.S)
        if not m:
            raise VLMError("model returned no JSON")
        return json.loads(m.group(0))


# What to ask the grounding pass to find, per target label (appearance only, never position).
DESCRIPTIONS = {
    "blue_crystal": "tall glowing blue crystal on a grey rock",
    "glowing_mushrooms": "group of glowing cyan and purple mushrooms",
    "largest_tree": "giant tree that towers far above all other trees, with a huge glowing turquoise canopy and hanging orange lanterns",
    "red_flower": "big glowing red flower",
    "portal": "huge glowing golden ring gateway, a circle as tall as a house standing between two stone pillars",
    "pond_water": "dark teal pond of water",
}


def describe(target: str, description: str = "", time_of_day: str = "night") -> str:
    """Known landmarks use a tested appearance description; anything else (open vocabulary) uses the planner's own."""
    d = None
    for k, v in DESCRIPTIONS.items():
        if k == target or (target and (k in target or target in k)):
            d = v
            break
    if d is None:
        d = (description or target.replace("_", " ")).strip()
    if time_of_day == "day":  # in daylight the glow is faint - describe shape and colour only
        d = d.replace("glowing ", "")
    return d[:120]


async def ground(view_b64: str, target: str, description: str = "", time_of_day: str = "night") -> list[float] | None:
    """Stage 2: precise localisation in ONE view using Qwen2.5-VL's native grounding format.
    Returns [x1,y1,x2,y2] in FRAME_W x FRAME_H pixels, or None if not found."""
    desc = describe(target, description, time_of_day)
    payload = {
        "model": MODEL, "stream": False, "keep_alive": KEEP_ALIVE,
        "format": GROUND_SCHEMA,
        "options": {"temperature": 0, "seed": 7, "num_ctx": 4096, "num_predict": 40},
        "messages": [{"role": "user", "images": [to_model_frame(view_b64)],
                      "content": f"Locate the {desc} in this image and output its bounding box as JSON "
                                 f'{{"bbox_2d": [x1, y1, x2, y2]}}. If it is not in the image, output {{"bbox_2d": [0, 0, 0, 0]}}.'}],
    }
    try:
        async with httpx.AsyncClient(timeout=90) as c:
            r = await c.post(f"{OLLAMA_URL}/api/chat", json=payload)
            r.raise_for_status()
            b = _extract_json(r.json()["message"]["content"]).get("bbox_2d")
    except Exception:  # noqa: BLE001
        return None
    if not isinstance(b, list) or len(b) != 4 or all(v == 0 for v in b):
        return None
    return [float(v) for v in b]


async def query(req, frame_b64: str, mosaic: bool) -> tuple[dict, str]:
    """Send image + instruction to the VLM. Returns (parsed_json, raw_text). Retries once."""
    payload = {
        "model": MODEL,
        "stream": False,
        "format": VLM_SCHEMA,          # constrained decoding to our schema
        "keep_alive": KEEP_ALIVE,
        "options": {"temperature": 0, "seed": 7, "num_ctx": 4096, "num_predict": 200},
        "messages": [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": build_user_prompt(req, mosaic), "images": [frame_b64]},
        ],
    }
    last_err = None
    async with httpx.AsyncClient(timeout=120) as c:
        for _ in range(2):
            try:
                r = await c.post(f"{OLLAMA_URL}/api/chat", json=payload)
                r.raise_for_status()
                raw = r.json()["message"]["content"]
                return _extract_json(raw), raw
            except (httpx.HTTPError, VLMError, json.JSONDecodeError, KeyError) as e:
                last_err = e
    raise VLMError(f"VLM request failed: {last_err}")


async def plan(req, mosaic_b64: str | None) -> tuple[dict, str]:
    """Stage 0: free-form player language (+ what MOSS sees) -> {understanding, reply, steps[]}."""
    msg = {"role": "user", "content": build_plan_prompt(req)}
    if mosaic_b64:
        if PLAN_SCALE < 1:
            im = _decode(mosaic_b64)
            im = im.resize((int(FRAME_W * PLAN_SCALE) // 28 * 28, int(FRAME_H * PLAN_SCALE) // 28 * 28), Image.BICUBIC)
            mosaic_b64 = _encode(im)
        msg["images"] = [mosaic_b64]
    payload = {
        "model": MODEL, "stream": False, "format": PLAN_SCHEMA, "keep_alive": KEEP_ALIVE,
        "options": {"temperature": 0, "seed": 7, "num_ctx": 4096, "num_predict": 450},
        "messages": [{"role": "system", "content": PLANNER_PROMPT}, msg],
    }
    last_err = None
    async with httpx.AsyncClient(timeout=120) as c:
        for _ in range(2):
            try:
                r = await c.post(f"{OLLAMA_URL}/api/chat", json=payload)
                r.raise_for_status()
                raw = r.json()["message"]["content"]
                return _extract_json(raw), raw
            except (httpx.HTTPError, VLMError, json.JSONDecodeError, KeyError) as e:
                last_err = e
    raise VLMError(f"planner request failed: {last_err}")


async def detect(view_b64: str, time_of_day: str = "night") -> list[dict]:
    """Live 'what MOSS sees' overlay: Qwen2.5-VL's native multi-object grounding on the FRONT view.
    Returns [{label, bbox_2d}] in FRAME_W x FRAME_H pixels (at most 6)."""
    payload = {
        "model": MODEL, "stream": False, "keep_alive": KEEP_ALIVE, "format": DETECT_SCHEMA,
        "options": {"temperature": 0, "seed": 7, "num_ctx": 4096, "num_predict": 220},
        "messages": [{"role": "user", "images": [to_model_frame(view_b64)],
                      "content": "Detect the main distinct objects in this garden image (for example a big tree, a pond, "
                                 "mushrooms, a crystal, a flower, a golden ring gate, rocks, lanterns, small trees). "
                                 "Give each a short name of 1-3 words and its bounding box. At most 6 objects, biggest "
                                 'first. Output JSON {"objects": [{"label": "...", "bbox_2d": [x1, y1, x2, y2]}]}.'}],
    }
    async with httpx.AsyncClient(timeout=90) as c:
        r = await c.post(f"{OLLAMA_URL}/api/chat", json=payload)
        r.raise_for_status()
        objs = _extract_json(r.json()["message"]["content"]).get("objects") or []
    out = []
    for o in objs:
        b = o.get("bbox_2d") if isinstance(o, dict) else None
        if isinstance(b, list) and len(b) == 4 and b[2] > b[0] and b[3] > b[1]:
            out.append({"label": str(o.get("label", ""))[:30], "bbox_2d": [float(v) for v in b]})
    return out[:6]
