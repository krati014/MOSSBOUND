"""Backend smoke test without Unity.

python test_client.py                      -> synthetic frame, several instructions
python test_client.py path/to/frame.jpg "Find the blue crystal"
"""
import base64
import io
import json
import sys

import httpx
from PIL import Image, ImageDraw

URL = "http://127.0.0.1:8765"


def synthetic_frame() -> bytes:
    w, h = 644, 364
    im = Image.new("RGB", (w, h), (10, 18, 32))
    d = ImageDraw.Draw(im)
    d.rectangle([0, 200, w, h], fill=(16, 48, 34))                       # ground
    d.polygon([(500, 120), (530, 60), (560, 120), (530, 230)], fill=(60, 140, 255))  # blue crystal (right)
    d.line([(110, 260), (110, 210)], fill=(40, 120, 60), width=4)        # red flower (left)
    d.ellipse([92, 190, 128, 222], fill=(230, 40, 60))
    buf = io.BytesIO(); im.save(buf, "JPEG", quality=85)
    return buf.getvalue()


def send(img: bytes, instruction: str):
    req = {"instruction": instruction, "image_b64": base64.b64encode(img).decode(),
           "image_width": 644, "image_height": 364, "hfov_deg": 90,
           "state": {"step": 0, "max_steps": 10}}
    r = httpx.post(f"{URL}/act", json=req, timeout=180)
    r.raise_for_status()
    return r.json()


if __name__ == "__main__":
    print(httpx.get(f"{URL}/health").json())
    if len(sys.argv) >= 3:
        img = open(sys.argv[1], "rb").read(); tests = [sys.argv[2]]
    else:
        img = synthetic_frame()
        tests = ["Find the blue crystal and move towards it.",
                 "Go to the red flower.",
                 "Reach the glowing portal."]
    for t in tests:
        c = send(img, t)
        print(f"\n> {t}\n  {c['action']} target={c['target']} dir={c['direction']} bearing={c['bearing_deg']} "
              f"dur={c['duration']} status={c['status']} bbox={c['target_bbox']} ({c['latency_ms']} ms)\n"
              f"  saw: {c['observation']}\n  why: {c['reason']}\n  warnings: {c['warnings']}")
