#!/usr/bin/env bash
# Converts MediaPipe's Face Landmarker model (Apache 2.0) to ONNX for the C# server (FEAT-230).
# One-time setup, not shipped: a Python 3.12 venv with tensorflow-cpu, tf2onnx, onnx, onnxruntime,
# mediapipe and opencv-python-headless at ~/.local/share/skylink-face-tools/venv.
#   uv venv --python 3.12 ~/.local/share/skylink-face-tools/venv
#   uv pip install --python ~/.local/share/skylink-face-tools/venv tensorflow-cpu tf2onnx onnx onnxruntime mediapipe opencv-python-headless
# Usage: scripts/face-model-convert.sh  (writes SkyrimMCP/Models/face_detector.onnx and face_landmarks.onnx)
set -euo pipefail
TOOLS=~/.local/share/skylink-face-tools
REPO=$(cd "$(dirname "$0")/.." && pwd)
URL=https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/1/face_landmarker.task
. "$TOOLS/venv/bin/activate"
mkdir -p "$TOOLS/model/download" "$TOOLS/model/tflite" "$REPO/SkyrimMCP/Models"
[ -f "$TOOLS/model/download/face_landmarker.task" ] || curl -sSfL -o "$TOOLS/model/download/face_landmarker.task" "$URL"
python -c "import zipfile,sys; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])" "$TOOLS/model/download/face_landmarker.task" "$TOOLS/model/tflite"
python -m tf2onnx.convert --tflite "$TOOLS/model/tflite/face_detector.tflite" --output "$REPO/SkyrimMCP/Models/face_detector.onnx" --opset 17 2>&1 | tail -2
python -m tf2onnx.convert --tflite "$TOOLS/model/tflite/face_landmarks_detector.tflite" --output "$REPO/SkyrimMCP/Models/face_landmarks.onnx" --opset 17 2>&1 | tail -2
python - "$REPO/SkyrimMCP/Models" <<'PY'
import onnx, sys, os
for f in ("face_detector.onnx", "face_landmarks.onnx"):
    m = onnx.load(os.path.join(sys.argv[1], f))
    io = lambda vs: [(v.name, [d.dim_value or d.dim_param for d in v.type.tensor_type.shape.dim]) for v in vs]
    print(f, os.path.getsize(os.path.join(sys.argv[1], f)), "in", io(m.graph.input), "out", io(m.graph.output))
PY
