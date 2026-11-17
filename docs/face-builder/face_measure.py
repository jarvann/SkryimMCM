"""Measure scale-free face proportions from an image with MediaPipe Face Landmarker.

Usage: python3 face_measure.py label=path [label=path ...]
Prints one table of ratios per image, then differences against the first image.
"""
import sys, math, json
import cv2
import mediapipe as mp
from mediapipe.tasks import python as mpp
from mediapipe.tasks.python import vision

MODEL = "./face_landmarker.task"

def landmarks(path, min_side=900):
    img = cv2.imread(path)
    if img is None:
        raise SystemExit("cannot read " + path)
    h, w = img.shape[:2]
    s = max(1.0, min_side / min(h, w))
    if s > 1:
        img = cv2.resize(img, (int(w * s), int(h * s)), interpolation=cv2.INTER_CUBIC)
    h, w = img.shape[:2]
    opts = vision.FaceLandmarkerOptions(base_options=mpp.BaseOptions(model_asset_path=MODEL), num_faces=3)
    det = vision.FaceLandmarker.create_from_options(opts)
    res = det.detect(mp.Image(image_format=mp.ImageFormat.SRGB, data=cv2.cvtColor(img, cv2.COLOR_BGR2RGB)))
    if not res.face_landmarks:
        return None
    # largest face
    best = max(res.face_landmarks, key=lambda f: (max(p.x for p in f) - min(p.x for p in f)))
    return [(p.x * w, p.y * h) for p in best]

def d(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])

def measure(L):
    P = lambda i: L[i]
    face_w = d(P(234), P(454)); face_h = d(P(10), P(152))
    eL_w = d(P(33), P(133)); eR_w = d(P(362), P(263)); eye_w = (eL_w + eR_w) / 2
    eL_o = d(P(159), P(145)); eR_o = d(P(386), P(374)); eye_o = (eL_o + eR_o) / 2
    inner = d(P(133), P(362))
    # outer-corner tilt: positive = outer corner higher than inner (image y grows down)
    tiltL = math.degrees(math.atan2(P(133)[1] - P(33)[1], abs(P(133)[0] - P(33)[0])))
    tiltR = math.degrees(math.atan2(P(362)[1] - P(263)[1], abs(P(263)[0] - P(362)[0])))
    brow = (d(P(105), P(159)) + d(P(334), P(386))) / 2
    iris = None
    if len(L) > 477:
        iris = (d(P(469), P(471)) + d(P(474), P(476))) / 2
    m = {
        "eye_width / face_width": eye_w / face_w,
        "eye_opening / eye_width": eye_o / eye_w,
        "eye_spacing / eye_width": inner / eye_w,
        "outer_corner_tilt_deg": (tiltL + tiltR) / 2,
        "brow_to_eye / eye_width": brow / eye_w,
        "nose_length / face_height": d(P(168), P(2)) / face_h,
        "nose_width / eye_spacing": d(P(129), P(358)) / inner,
        "mouth_width / face_width": d(P(61), P(291)) / face_w,
        "lip_height / mouth_width": d(P(0), P(17)) / d(P(61), P(291)),
        "jaw_width / face_width": d(P(172), P(397)) / face_w,
        "chin_height / face_height": d(P(17), P(152)) / face_h,
        "face_width / face_height": face_w / face_h,
        "eye_line_height (brow-top to chin fraction)": d(P(10), ((P(33)[0]+P(263)[0])/2, (P(33)[1]+P(263)[1])/2)) / face_h,
    }
    if iris:
        m["iris_diameter / eye_width"] = iris / eye_w
    return m

if __name__ == "__main__":
    out = {}
    for a in sys.argv[1:]:
        label, path = a.split("=", 1)
        L = landmarks(path)
        out[label] = measure(L) if L else None
        print(label, "face found" if L else "NO FACE FOUND")
    labels = [k for k, v in out.items() if v]
    if not labels:
        raise SystemExit(1)
    keys = list(out[labels[0]].keys())
    print()
    print("%-46s" % "ratio" + "".join("%12s" % l[:11] for l in labels) + "".join("%12s" % ("d " + l[:9]) for l in labels[1:]))
    for k in keys:
        row = "%-46s" % k + "".join("%12.3f" % out[l][k] for l in labels)
        ref = out[labels[0]][k]
        for l in labels[1:]:
            v = out[l][k]
            row += "%11.0f%%" % ((v - ref) / abs(ref) * 100) if "deg" not in k else "%12.1f" % (v - ref)
        print(row)
    json.dump(out, open("./face_measure_last.json", "w"), indent=1)
