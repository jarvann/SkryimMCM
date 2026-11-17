#!/usr/bin/env python3
"""Reference implementation of the server's face measurement (FEAT-230), checked against MediaPipe.

The C# FaceMeasurer ports this file step for step; run both on the same image to check the port.
Run with the venv from scripts/face-model-convert.sh:
  ~/.local/share/skylink-face-tools/venv/bin/python scripts/face-model-check.py [--mediapipe] <image> [<image> ...]
Prints the measurements as JSON (with --mediapipe, also how far the landmarks are from MediaPipe's own).
"""
import json
import math
import os
import sys

import cv2
import numpy as np
import onnxruntime as ort

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MODELS = os.path.join(REPO, "SkyrimMCP", "Models")
TASK = os.path.expanduser("~/.local/share/skylink-face-tools/model/download/face_landmarker.task")

# Crops averaged per measurement: the landmark model moves a few percent with small crop changes,
# and averaging 27 crops cut the spread to about a third in testing.
TTA_SCALES = (0.93, 1.0, 1.07)
TTA_SHIFTS = (-0.03, 0.0, 0.03)


def anchors():
    # SSD anchors for face_detection_short_range: strides 8 (2 per cell) and 16 (6 per cell).
    out = []
    for stride, per_cell in ((8, 2), (16, 6)):
        n = 128 // stride
        for y in range(n):
            for x in range(n):
                for _ in range(per_cell):
                    out.append(((x + 0.5) / n, (y + 0.5) / n))
    return np.array(out, dtype=np.float32)


ANCHORS = anchors()


def letterbox(img, size):
    h, w = img.shape[:2]
    s = size / max(h, w)
    nw, nh = max(1, round(w * s)), max(1, round(h * s))
    small = cv2.resize(img, (nw, nh), interpolation=cv2.INTER_AREA)
    px, py = (size - nw) // 2, (size - nh) // 2
    out = cv2.copyMakeBorder(small, py, size - nh - py, px, size - nw - px, cv2.BORDER_CONSTANT, value=(0, 0, 0))
    return out, s, px, py


def iou(a, b):
    ax0, ay0, ax1, ay1 = a[0] - a[2] / 2, a[1] - a[3] / 2, a[0] + a[2] / 2, a[1] + a[3] / 2
    bx0, by0, bx1, by1 = b[0] - b[2] / 2, b[1] - b[3] / 2, b[0] + b[2] / 2, b[1] + b[3] / 2
    inter = max(0, min(ax1, bx1) - max(ax0, bx0)) * max(0, min(ay1, by1) - max(ay0, by0))
    return inter / (a[2] * a[3] + b[2] * b[3] - inter + 1e-9)


def detect_square(sess, rgb):
    inp, s, px, py = letterbox(rgb, 128)
    reg, cls = sess.run(None, {"input": (inp.astype(np.float32) / 127.5 - 1.0)[None]})
    scores = 1 / (1 + np.exp(-np.clip(cls[0, :, 0].astype(np.float64), -100, 100)))
    if scores.max() < 0.5:
        return None
    boxes = reg[0].astype(np.float64) / 128.0
    boxes[:, 0] += ANCHORS[:, 0]
    boxes[:, 1] += ANCHORS[:, 1]
    boxes[:, 4::2] += ANCHORS[:, 0:1]
    boxes[:, 5::2] += ANCHORS[:, 1:2]
    best = int(np.argmax(scores))
    # weighted NMS around the best candidate
    cand = [i for i in range(len(scores)) if scores[i] >= 0.5 and iou(boxes[i], boxes[best]) > 0.3]
    wts = scores[cand]
    r = (boxes[cand] * wts[:, None]).sum(0) / wts.sum()
    to_img = lambda x, y: ((x * 128 - px) / s, (y * 128 - py) / s)
    return {"score": float(scores[best]), "center": to_img(r[0], r[1]), "size": (r[2] * 128 / s, r[3] * 128 / s),
            "keypoints": [to_img(r[4 + 2 * k], r[5 + 2 * k]) for k in range(6)]}


def detect(sess, rgb):
    # A wide frame (32:9) leaves the face too small once letterboxed: fall back to the middle square.
    det = detect_square(sess, rgb)
    h, w = rgb.shape[:2]
    if det or w <= h * 1.2:
        return det
    x0 = (w - h) // 2
    det = detect_square(sess, rgb[:, x0:x0 + h])
    if det:
        det["center"] = (det["center"][0] + x0, det["center"][1])
        det["keypoints"] = [(x + x0, y) for x, y in det["keypoints"]]
    return det


def roi_from_detection(det):
    (x0, y0), (x1, y1) = det["keypoints"][0], det["keypoints"][1]
    return det["center"], max(det["size"]) * 1.5, math.atan2(y1 - y0, x1 - x0)


def landmarks(sess, rgb, roi):
    (cx, cy), side, angle = roi
    f = min(1.0, 512.0 / side)  # area-average big images first so the 256 crop isn't aliased
    if f < 1.0:
        rgb = cv2.resize(rgb, None, fx=f, fy=f, interpolation=cv2.INTER_AREA)
        cx, cy, side = cx * f, cy * f, side * f
    k = 256 / side
    c, s = math.cos(-angle) * k, math.sin(-angle) * k
    m = np.array([[c, -s, 128 - (c * cx - s * cy)], [s, c, 128 - (s * cx + c * cy)]], dtype=np.float64)
    crop = cv2.warpAffine(rgb, m, (256, 256), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT)
    out = sess.run(None, {sess.get_inputs()[0].name: (crop.astype(np.float32) / 255.0)[None]})
    pts = out[0].reshape(478, 3).astype(np.float64)
    presence = 1 / (1 + math.exp(-float(np.ravel(out[1])[0])))
    inv = cv2.invertAffineTransform(m)
    xy = (pts[:, :2] @ inv[:, :2].T + inv[:, 2]) / f
    z = pts[:, 2:3] / k / f  # same scale as x and y
    return np.concatenate([xy, z], axis=1), presence


def find_landmarks(det_sess, lm_sess, rgb):
    det = detect(det_sess, rgb)
    if not det:
        return None, None
    (cx, cy), side, angle = roi_from_detection(det)
    acc, presences = [], []
    for sc in TTA_SCALES:
        for dx in TTA_SHIFTS:
            for dy in TTA_SHIFTS:
                pts, p = landmarks(lm_sess, rgb, ((cx + dx * side, cy + dy * side), side * sc, angle))
                acc.append(pts)
                presences.append(p)
    return np.mean(acc, axis=0), {"detector": det["score"], "landmarks": float(np.min(presences))}


# ---- measurements -------------------------------------------------------------------------------

def ratios(L):
    d = lambda a, b: math.hypot(L[a][0] - L[b][0], L[a][1] - L[b][1])
    face_w, face_h = d(234, 454), d(10, 152)
    eye_w = (d(33, 133) + d(362, 263)) / 2
    inner = d(133, 362)
    tl = math.degrees(math.atan2(L[133][1] - L[33][1], abs(L[133][0] - L[33][0])))
    tr = math.degrees(math.atan2(L[362][1] - L[263][1], abs(L[263][0] - L[362][0])))
    return {
        "eye_width / face_width": eye_w / face_w,
        "eye_opening / eye_width": (d(159, 145) + d(386, 374)) / 2 / eye_w,
        "eye_spacing / eye_width": inner / eye_w,
        "outer_corner_tilt_deg": (tl + tr) / 2,
        "brow_to_eye / eye_width": (d(105, 159) + d(334, 386)) / 2 / eye_w,
        "nose_length / face_height": d(168, 2) / face_h,
        "nose_width / eye_spacing": d(129, 358) / inner,
        "mouth_width / face_width": d(61, 291) / face_w,
        "lip_height / mouth_width": d(0, 17) / d(61, 291),
        "jaw_width / face_width": d(172, 397) / face_w,
        "chin_height / face_height": d(17, 152) / face_h,
        "face_width / face_height": face_w / face_h,
        "iris_diameter / eye_width": (d(469, 471) + d(474, 476)) / 2 / eye_w,
    }


def lid_gaps(L):
    """How far each lid sits from the iris centre, in iris radii, across the eye (perpendicular to the
    corner-to-corner line). Under 1: the lid covers part of the iris. Over 1: white shows (a stare)."""
    up, low = [], []
    for outer, inner, upper, lower, iris, rim in ((33, 133, 159, 145, 468, (469, 471)), (263, 362, 386, 374, 473, (474, 476))):
        axis = L[inner][:2] - L[outer][:2]
        axis = axis / np.linalg.norm(axis)
        normal = np.array([-axis[1], axis[0]])
        if normal[1] < 0:
            normal = -normal  # pointing down the image
        r = math.hypot(*(L[rim[0]][:2] - L[rim[1]][:2])) / 2
        c = L[iris][:2]
        up.append(float(np.dot(c - L[upper][:2], normal)) / r)
        low.append(float(np.dot(L[lower][:2] - c, normal)) / r)
    return {"upper_lid_gap / iris_radius": sum(up) / 2, "lower_lid_gap / iris_radius": sum(low) / 2}


PITCH_LEVEL = 13.0


def pose_and_expression(L):
    """Head angles from the 3D landmarks (degrees: yaw + = turned to the image's right, pitch + = chin
    down, roll + = clockwise) and simple expression signs. A turned head or a smile skews the ratios."""
    ear = L[454] - L[234]
    vert = L[152] - L[10]
    yaw = math.degrees(math.atan2(ear[2], ear[0]))
    # The forehead top (10) sits further back than the chin (152) on a level face: two frontal faces both read
    # about -13 degrees raw, so that is taken as level.
    pitch = math.degrees(math.atan2(-vert[2], vert[1])) + PITCH_LEVEL
    roll = math.degrees(math.atan2(L[263][1] - L[33][1], L[263][0] - L[33][0]))
    mouth_w = math.hypot(*(L[291][:2] - L[61][:2]))
    lip_mid_y = (L[0][1] + L[17][1]) / 2
    corner_lift = (lip_mid_y - (L[61][1] + L[291][1]) / 2) / mouth_w  # + = corners above the lip centre: a smile
    mouth_open = math.hypot(*(L[14][:2] - L[13][:2])) / mouth_w
    flags = []
    if abs(yaw) > 8: flags.append("head turned: width ratios are skewed")
    if abs(pitch) > 10: flags.append("head tilted up or down: height ratios are skewed")
    if corner_lift > 0.08: flags.append("smiling: mouth width, lip height and eye opening are not neutral")
    if mouth_open > 0.08: flags.append("mouth open: lip height and chin height are not neutral")
    return {"yaw_deg": yaw, "pitch_deg": pitch, "roll_deg": roll, "mouth_corner_lift": corner_lift,
            "mouth_open": mouth_open, "flags": flags}


def srgb_to_lab(rgb):
    c = np.asarray(rgb, dtype=np.float64) / 255.0
    c = np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
    m = np.array([[0.4124564, 0.3575761, 0.1804375], [0.2126729, 0.7151522, 0.0721750], [0.0193339, 0.1191920, 0.9503041]])
    xyz = c @ m.T / np.array([0.95047, 1.0, 1.08883])
    f = np.where(xyz > (6 / 29) ** 3, np.cbrt(xyz), xyz / (3 * (6 / 29) ** 2) + 4 / 29)
    return np.stack([116 * f[..., 1] - 16, 500 * (f[..., 0] - f[..., 1]), 200 * (f[..., 1] - f[..., 2])], axis=-1)


def lab_to_hex(lab):
    L, a, b = lab
    fy = (L + 16) / 116
    fx, fz = fy + a / 500, fy - b / 200
    inv = lambda t: t ** 3 if t > 6 / 29 else 3 * (6 / 29) ** 2 * (t - 4 / 29)
    xyz = np.array([inv(fx) * 0.95047, inv(fy), inv(fz) * 1.08883])
    m = np.array([[3.2404542, -1.5371385, -0.4985314], [-0.9692660, 1.8760108, 0.0415560], [0.0556434, -0.2040259, 1.0572252]])
    lin = np.clip(m @ xyz, 0, 1)
    srgb = np.where(lin <= 0.0031308, lin * 12.92, 1.055 * lin ** (1 / 2.4) - 0.055)
    return "#" + "".join(f"{int(round(v * 255)):02X}" for v in srgb)


# Sample spots: a landmark index, or a pair whose midpoint is sampled.
COLOUR_SPOTS = {
    "skin": [199, 200, 208, 428, 6, 197],                   # chin, plus the upper nose bridge: no makeup; mostly chin, as the bridge catches highlights in sunlight
    "cheeks": [50, 280, (50, 205), (280, 425)],              # where blush goes
    "lips": [(0, 13), (14, 17), (37, 82), (267, 312), (84, 87), (314, 317)],
    "brows": [(63, 53), (105, 52), (66, 65), (293, 283), (334, 282), (296, 295)],
}


def sample_disc(rgb, x, y, r):
    h, w = rgb.shape[:2]
    x0, x1 = max(0, int(math.floor(x - r))), min(w, int(math.ceil(x + r)) + 1)
    y0, y1 = max(0, int(math.floor(y - r))), min(h, int(math.ceil(y + r)) + 1)
    if x1 <= x0 or y1 <= y0:
        return None
    yy, xx = np.mgrid[y0:y1, x0:x1]
    mask = (xx - x) ** 2 + (yy - y) ** 2 <= r * r
    px = rgb[y0:y1, x0:x1][mask].reshape(-1, 3)
    return px if len(px) else None


def colours(rgb, L):
    eye_w = (math.hypot(*(L[33][:2] - L[133][:2])) + math.hypot(*(L[362][:2] - L[263][:2]))) / 2
    radius = max(1.0, eye_w * 0.06)
    lab = {}
    for name, spots in COLOUR_SPOTS.items():
        px = []
        for s in spots:
            x, y = ((L[s[0]][:2] + L[s[1]][:2]) / 2) if isinstance(s, tuple) else L[s][:2]
            p = sample_disc(rgb, x, y, radius)
            if p is not None:
                px.append(p)
        lab[name] = np.median(srgb_to_lab(np.concatenate(px)), axis=0)
    # iris: points at 60% of the radius on the lower half and the sides (lids and lashes shade the top)
    px = []
    for iris, rim in ((468, (469, 471)), (473, (474, 476))):
        r = math.hypot(*(L[rim[0]][:2] - L[rim[1]][:2])) / 2
        for a in (0, 45, 90, 135, 180):
            t = math.radians(a)
            p = sample_disc(rgb, L[iris][0] + 0.6 * r * math.cos(t), L[iris][1] + 0.6 * r * math.sin(t), max(1.0, r * 0.15))
            if p is not None:
                px.append(p)
    lab["iris"] = np.median(srgb_to_lab(np.concatenate(px)), axis=0)

    sides = []
    for idx in (50, 280):
        p = sample_disc(rgb, L[idx][0], L[idx][1], radius * 2)
        sides.append(float(np.median(srgb_to_lab(p), axis=0)[0]))
    lighting = {"skin_L": float(lab["skin"][0]), "side_balance (image-left cheek L - image-right cheek L)": sides[0] - sides[1],
                "warning": "lit from one side: colours and shading differ from an evenly lit capture" if abs(sides[0] - sides[1]) > 12 else None}
    skin = lab["skin"]
    hue = lambda v: math.degrees(math.atan2(v[2], v[1])) % 360
    # The image's overall cast: the mean a*/b* of the whole face area. Strong casts (sepia, purple light)
    # shift every absolute colour; the "relative" numbers mostly cancel them.
    xs, ys = L[:, 0], L[:, 1]
    face = rgb[max(0, int(ys.min())):int(ys.max()) + 1, max(0, int(xs.min())):int(xs.max()) + 1].reshape(-1, 3)
    face_lab = srgb_to_lab(face[:: max(1, len(face) // 20000)])
    cast = [float(np.mean(face_lab[:, 1])), float(np.mean(face_lab[:, 2]))]
    return {
        "hex": {k: lab_to_hex(v) for k, v in lab.items()},
        "lab": {k: [float(x) for x in v] for k, v in lab.items()},
        "relative": {
            "brow_darkness (skin L - brow L)": float(skin[0] - lab["brows"][0]),
            "lip_redness (lip a - skin a)": float(lab["lips"][1] - skin[1]),
            "lip_lightness (lip L - skin L)": float(lab["lips"][0] - skin[0]),
            "blush (cheek a - skin a)": float(lab["cheeks"][1] - skin[1]),
            "iris_lightness (iris L - skin L)": float(lab["iris"][0] - skin[0]),
            "iris_hue_deg": hue(lab["iris"]),
            "iris_chroma": float(math.hypot(lab["iris"][1], lab["iris"][2])),
            "skin_hue_deg": hue(skin),
        },
        "lighting": lighting,
        "cast": {"a": cast[0], "b": cast[1],
                 "warning": "strong colour cast: compare relative values, not hex" if math.hypot(*cast) > 25 else None},
    }


def measure(path, det_sess=None, lm_sess=None):
    det_sess = det_sess or ort.InferenceSession(os.path.join(MODELS, "face_detector.onnx"))
    lm_sess = lm_sess or ort.InferenceSession(os.path.join(MODELS, "face_landmarks.onnx"))
    rgb = cv2.cvtColor(cv2.imread(path), cv2.COLOR_BGR2RGB)
    L, conf = find_landmarks(det_sess, lm_sess, rgb)
    if L is None:
        return None, None
    return {"confidence": conf, "pose": pose_and_expression(L), "ratios": ratios(L), "eyes": lid_gaps(L),
            "colour": colours(rgb, L)}, L


def mediapipe_landmarks(path):
    import mediapipe as mp
    from mediapipe.tasks import python as mpp
    from mediapipe.tasks.python import vision
    rgb = cv2.cvtColor(cv2.imread(path), cv2.COLOR_BGR2RGB)
    h, w = rgb.shape[:2]
    opts = vision.FaceLandmarkerOptions(base_options=mpp.BaseOptions(model_asset_path=TASK), num_faces=1)
    res = vision.FaceLandmarker.create_from_options(opts).detect(mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb))
    return np.array([(p.x * w, p.y * h, p.z * w) for p in res.face_landmarks[0]]) if res.face_landmarks else None


def rounded(v):
    if isinstance(v, dict):
        return {k: rounded(x) for k, x in v.items()}
    if isinstance(v, list):
        return [rounded(x) for x in v]
    return round(v, 3) if isinstance(v, float) else v


if __name__ == "__main__":
    for path in [a for a in sys.argv[1:] if not a.startswith("--")]:
        out, L = measure(path)
        result = {"image": path, "measurement": rounded(out) if out else "no face found"}
        if "--mediapipe" in sys.argv and L is not None:
            ref = mediapipe_landmarks(path)
            if ref is not None:
                err = np.linalg.norm(L[:, :2] - ref[:, :2], axis=1)
                span = np.linalg.norm(ref[33, :2] - ref[263, :2])
                result["vsMediapipe"] = {"meanPercentOfEyeSpan": round(float(err.mean() / span * 100), 2),
                                         "ratios": rounded(ratios(ref)), "eyes": rounded(lid_gaps(ref)),
                                         "pose": rounded(pose_and_expression(ref))}
        print(json.dumps(result, indent=1))
