#!/usr/bin/env python3
"""Reference implementation of the server's body measurement (FEAT-255): MediaPipe's pose landmark model (ONNX), with the
body found from the face detector, then refined from its own landmarks. Prints the body ratios and draws the landmarks.

  ~/.local/share/skylink-face-tools/venv/bin/python scripts/body-model-check.py <image> [<out.jpg>]
"""
import importlib.util, json, math, os, sys
import cv2, numpy as np, onnxruntime as ort

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("fm", os.path.join(HERE, "face-model-check.py"))
fm = importlib.util.module_from_spec(spec); spec.loader.exec_module(fm)
MODELS = fm.MODELS

def run(sess, rgb, cx, cy, side):
    # axis-aligned square crop (the body is upright in captures and pin-ups), 256 x 256, [0,1]
    k = 256 / side
    m = np.array([[k, 0, 128 - k * cx], [0, k, 128 - k * cy]], dtype=np.float64)
    crop = cv2.warpAffine(rgb, m, (256, 256), flags=cv2.INTER_AREA if k < 1 else cv2.INTER_LINEAR, borderValue=(0, 0, 0))
    out = sess.run(None, {sess.get_inputs()[0].name: (crop.astype(np.float32) / 255.0)[None]})
    lm = out[0].reshape(39, 5)
    presence = 1 / (1 + math.exp(-float(np.ravel(out[1])[0])))
    xy = lm[:, :2] / k + np.array([cx - 128 / k, cy - 128 / k])
    vis = 1 / (1 + np.exp(-lm[:, 3]))
    mask = out[2][0, :, :, 0]
    return xy, vis, presence, mask, (cx, cy, side)

def find_body(rgb, det_sess, pose_sess):
    h, w = rgb.shape[:2]
    det = fm.detect(det_sess, rgb)
    if det:
        fh = max(det["size"])
        # a standing body is about 10 face boxes tall, its middle about 4.5 below the face
        cx, cy = det["center"][0], det["center"][1] + 6.0 * fh
        side = 15.0 * fh
    else:
        cx, cy, side = w / 2, h / 2, max(w, h)
    best = None
    for _ in range(3):  # refine from the body's own landmarks
        xy, vis, presence, mask, roi = run(pose_sess, rgb, cx, cy, side)
        best = (xy, vis, presence, mask, roi)
        good = xy[:33][vis[:33] > 0.3]
        if len(good) < 8: break
        x0, y0 = good.min(0); x1, y1 = good.max(0)
        # grow only: a crop that cuts the legs off makes the model put the hips on the chest
        nx0, ny0 = min(x0, cx - side / 2), min(y0, cy - side / 2); nx1, ny1 = max(x1, cx + side / 2), max(y1, cy + side / 2)
        cx, cy = (nx0 + nx1) / 2, (ny0 + ny1) / 2
        side = max(nx1 - nx0, ny1 - ny0)
    return best

def ratios(xy, vis, mask, roi):
    d = lambda a, b: float(np.linalg.norm(xy[a] - xy[b]))
    mid = lambda a, b: (xy[a] + xy[b]) / 2
    shoulders = d(11, 12); hips = d(23, 24)
    sh_mid, hip_mid = mid(11, 12), mid(23, 24)
    ankle_mid = mid(27, 28)
    torso = float(np.linalg.norm(sh_mid - hip_mid)); legs = float(np.linalg.norm(hip_mid - ankle_mid))
    arm = (d(11, 13) + d(13, 15) + d(12, 14) + d(14, 16)) / 2
    # head top from the silhouette mask, above the nose
    cx, cy, side = roi
    k = 256 / side
    mx = int(round((xy[0][0] - (cx - 128 / k)) * k)); col = mask[:, max(0, min(255, mx))]
    rows = np.where(col > 0.5)[0]
    top = (rows.min() / k + cy - 128 / k) if len(rows) else xy[0][1]
    height = float(ankle_mid[1] - top)
    def width_at(y):  # silhouette width at an image row
        r = int(round((y - (cy - 128 / k)) * k))
        if r < 0 or r > 255: return float('nan')
        cols = np.where(mask[r] > 0.5)[0]
        return (cols.max() - cols.min()) / k if len(cols) else float('nan')
    waist_y = sh_mid[1] + 0.62 * (hip_mid[1] - sh_mid[1])
    chest_y = sh_mid[1] + 0.25 * (hip_mid[1] - sh_mid[1])
    return {
        "height_px": height,
        "shoulder_width / height": shoulders / height,
        "hip_width / shoulder_width": hips / shoulders,
        "torso / height": torso / height,
        "legs / height": legs / height,
        "arm / height": arm / height,
        "head / height": float(sh_mid[1] - top) / height,
        "silhouette_chest / shoulder_width": width_at(chest_y) / shoulders,
        "silhouette_waist / shoulder_width": width_at(waist_y) / shoulders,
        "silhouette_hips / shoulder_width": width_at(hip_mid[1]) / shoulders,
        "visibility_min": float(vis[[11, 12, 23, 24, 27, 28]].min()),
    }

if __name__ == "__main__":
    det = ort.InferenceSession(os.path.join(MODELS, "face_detector.onnx"))
    pose = ort.InferenceSession(os.path.join(MODELS, "pose_landmarks.onnx"))
    rgb = cv2.cvtColor(cv2.imread(sys.argv[1]), cv2.COLOR_BGR2RGB)
    xy, vis, presence, mask, roi = find_body(rgb, det, pose)
    print(json.dumps({"presence": round(presence, 3), "ratios": {k: round(v, 3) for k, v in ratios(xy, vis, mask, roi).items()}}, indent=1))
    if len(sys.argv) > 2:
        img = cv2.imread(sys.argv[1])
        for i, (x, y) in enumerate(xy[:33]):
            if vis[i] > 0.5: cv2.circle(img, (int(x), int(y)), 4, (0, 255, 255), -1)
        cx, cy, side = roi
        crop = img[max(0, int(cy - side / 2)):int(cy + side / 2), max(0, int(cx - side / 2)):int(cx + side / 2)]
        cv2.imwrite(sys.argv[2], cv2.resize(crop, (600, int(600 * crop.shape[0] / max(1, crop.shape[1])))))
