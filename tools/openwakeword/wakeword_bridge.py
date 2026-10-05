import os
import sys
import time
import numpy as np
from openwakeword.model import Model

BASE = os.path.dirname(os.path.abspath(__file__))
MODELS = os.path.join(BASE, "models")
CUSTOM = os.path.join(MODELS, "spanky.onnx")
FALLBACK = os.path.join(MODELS, "hey_jarvis_v0.1.onnx")
WAKE_MODEL = CUSTOM if os.path.exists(CUSTOM) else FALLBACK

MELSPEC = os.path.join(MODELS, "melspectrogram.onnx")
EMBED = os.path.join(MODELS, "embedding_model.onnx")

model = Model(
    wakeword_models=[WAKE_MODEL],
    inference_framework="onnx",
    vad_threshold=0.0,
    melspec_model_path=MELSPEC,
    embedding_model_path=EMBED,
)
FRAME_BYTES = 2560
THRESHOLD = 0.55
REFRACTORY_SECONDS = 2.0
buffer = bytearray()
last_wake = 0.0

print("READY " + os.path.basename(WAKE_MODEL), flush=True)

while True:
    chunk = sys.stdin.buffer.read(3200)
    if not chunk:
        break
    buffer.extend(chunk)

    while len(buffer) >= FRAME_BYTES:
        frame = bytes(buffer[:FRAME_BYTES])
        del buffer[:FRAME_BYTES]
        audio = np.frombuffer(frame, dtype=np.int16)
        scores = model.predict(audio)

        if not scores:
            continue

        score = max(float(v) for v in scores.values())
        now = time.monotonic()
        if score >= THRESHOLD and now - last_wake >= REFRACTORY_SECONDS:
            last_wake = now
            print(f"WAKE {os.path.basename(WAKE_MODEL)} {score:.4f}", flush=True)
