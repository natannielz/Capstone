"""One-image offline candidate protocol. Invoked by the bounded .NET parent, never as a web service.

No database, network, credential, file-output or automatic model-download interface.
The parent pins the helper, libraries and official model files before giving it an assigned stream.
"""
import hashlib
import io
import json
import math
import sys
import warnings


PREPROCESSING = "bounded-gray-orient4-deskew10-contrast1-psm6-ind+eng-v2"
PROBE_EDGE = 1000
WORK_PIXELS = 4000000
WORK_EDGE = 4096


def quarter_turn(image, turns):
    from PIL import Image
    return image.copy() if turns == 0 else image.transpose(
        (Image.Transpose.ROTATE_90, Image.Transpose.ROTATE_180, Image.Transpose.ROTATE_270)[turns - 1])


def small_image(image, edge, pixels):
    from PIL import Image
    ratio = min(1.0, edge / max(image.size), math.sqrt(pixels / (image.width * image.height)))
    size = (max(1, int(image.width * ratio)), max(1, int(image.height * ratio)))
    return image.copy() if size == image.size else image.resize(size, Image.Resampling.LANCZOS)


def deskew(image):
    """Bounded projection search: at most 20,000 sampled ink pixels and 27 angles."""
    with small_image(image, 600, 360000) as probe:
        pixels = list(probe.getdata())
        threshold = min(180, (min(pixels) + max(pixels)) // 2)
        points = [(index % probe.width - probe.width / 2, index // probe.width - probe.height / 2)
                  for index, value in enumerate(pixels) if value < threshold]
        if len(points) < 30:
            return 0.0, False
        points = points[::max(1, math.ceil(len(points) / 20000))]
        radius = math.ceil(math.hypot(probe.width, probe.height))

        def score(angle):
            radians = math.radians(angle)
            sine, cosine = math.sin(radians), math.cos(radians)
            rows = [0] * (radius * 2 + 1)
            for x, y in points:
                rows[int(-sine * x + cosine * y) + radius] += 1
            return sum(value * value for value in rows)

        candidates = [(score(angle), float(angle)) for angle in range(-10, 11)]
        best_score, best = max(candidates, key=lambda item: (item[0], -abs(item[1]), -item[1]))
        baseline = candidates[10][0]
        if abs(best) == 10 and best_score > baseline * 1.15:
            return 0.0, True
        for step in (-.75, -.5, -.25, .25, .5, .75):
            angle = best + step
            if abs(angle) <= 10:
                candidates.append((score(angle), angle))
        best_score, best = max(candidates, key=lambda item: (item[0], -abs(item[1]), -item[1]))
        return (best if best_score > baseline * 1.15 else 0.0), False


def recognize(api, image):
    import tesserocr
    api.SetImage(image)
    api.Recognize()
    lines, total_chars = [], 0
    iterator = api.GetIterator() if api.GetUTF8Text().strip() else None
    if iterator is not None:
        while True:
            text = (iterator.GetUTF8Text(tesserocr.RIL.TEXTLINE) or "").strip()
            if text:
                box = iterator.BoundingBox(tesserocr.RIL.TEXTLINE)
                confidence = iterator.Confidence(tesserocr.RIL.TEXTLINE) / 100
                total_chars += len(text)
                if box is None or len(text) > 512 or total_chars > 16384 or len(lines) >= 128:
                    raise ValueError("output_limit")
                lines.append((text, confidence, box))
            if not iterator.Next(tesserocr.RIL.TEXTLINE):
                break
    return lines


def original_box(box, original_size, turns, scaled_size, rotated_size, angle):
    """Inverse every transform, then enclose/clamp all four corners in ORIGINAL pixels."""
    width, height = original_size
    oriented_width, oriented_height = (height, width) if turns % 2 else (width, height)
    sine, cosine = math.sin(math.radians(angle)), math.cos(math.radians(angle))
    points = []
    for x, y in ((box[0], box[1]), (box[2], box[1]), (box[0], box[3]), (box[2], box[3])):
        x, y = x - rotated_size[0] / 2, y - rotated_size[1] / 2
        x, y = (cosine * x - sine * y + scaled_size[0] / 2) * oriented_width / scaled_size[0], \
               (sine * x + cosine * y + scaled_size[1] / 2) * oriented_height / scaled_size[1]
        if turns == 1:
            x, y = width - y, x
        elif turns == 2:
            x, y = width - x, height - y
        elif turns == 3:
            x, y = y, height - x
        points.append((max(0, min(width, x)), max(0, min(height, y))))
    x1, y1 = math.floor(min(x for x, _ in points)), math.floor(min(y for _, y in points))
    x2, y2 = math.ceil(max(x for x, _ in points)), math.ceil(max(y for _, y in points))
    if x1 >= x2 or y1 >= y2:
        raise ValueError("mapped_box")
    norm = lambda numerator, denominator: (numerator * 1000000 // denominator) / 1000000
    return [norm(x1, width), norm(y1, height), norm(x2 - x1, width), norm(y2 - y1, height)]


def preprocess_and_read(rgb, models):
    from PIL import Image, ImageOps
    import tesserocr
    diagnostics = {"version": PREPROCESSING, "orientationDegrees": 0, "deskewDegrees": 0.0,
                   "contrastApplied": False, "workingWidth": 0, "workingHeight": 0,
                   "orientationScore": 0.0, "orientationMargin": 0.0, "reviewReasons": []}
    with rgb.convert("L") as gray, ImageOps.autocontrast(gray, cutoff=1) as contrast:
        diagnostics["contrastApplied"] = gray.tobytes() != contrast.tobytes()
        with small_image(contrast, PROBE_EDGE, PROBE_EDGE * PROBE_EDGE) as probe:
            choices = []
            # One bounded engine instance, four fixed orientation probes. No OSD model/download.
            with tesserocr.PyTessBaseAPI(path=models, lang="ind+eng", oem=tesserocr.OEM.LSTM_ONLY, psm=tesserocr.PSM.SINGLE_BLOCK) as api:
                for turns in range(4):
                    with quarter_turn(probe, turns) as oriented:
                        angle, out_of_range = deskew(oriented)
                        with oriented.rotate(angle, resample=Image.Resampling.BICUBIC, expand=True, fillcolor=255) as straight:
                            lines = recognize(api, straight)
                        chars = sum(len(text) for text, _, _ in lines)
                        score = sum(len(text) * confidence for text, confidence, _ in lines) / chars if chars else 0.0
                        choices.append((score, chars, turns, angle, out_of_range))
                choices.sort(key=lambda item: (-item[0], -item[1], item[2]))
                score, chars, turns, angle, out_of_range = choices[0]
                margin = max(0.0, score - choices[1][0])
                diagnostics.update(orientationScore=round(score, 6), orientationMargin=round(margin, 6))
                if chars < 20 or score < .65 or margin < .10 or out_of_range:
                    reason = "no_text" if not any(item[1] for item in choices) else "skew_out_of_range" if out_of_range else "orientation_unresolved"
                    diagnostics["reviewReasons"] = [reason]
                    return [], diagnostics
                diagnostics.update(orientationDegrees=turns * 90, deskewDegrees=angle)
                with quarter_turn(contrast, turns) as oriented, small_image(oriented, WORK_EDGE, WORK_PIXELS) as scaled:
                    with scaled.rotate(angle, resample=Image.Resampling.BICUBIC, expand=True, fillcolor=255) as straight:
                        # Expansion is also finite; do not clip original content to save resources.
                        if straight.width * straight.height > 5500000 or max(straight.size) > 4800:
                            raise ValueError("transform_limit")
                        diagnostics.update(workingWidth=straight.width, workingHeight=straight.height)
                        recognized = recognize(api, straight)
                        lines = [{"text": text, "confidence": round(confidence, 6),
                                  "box": original_box(box, rgb.size, turns, scaled.size, straight.size, angle)}
                                 for text, confidence, box in recognized]
                        if not lines:
                            diagnostics["reviewReasons"] = ["no_text"]
                        elif any(line["confidence"] < .9 for line in lines):
                            diagnostics["reviewReasons"] = ["low_text_confidence"]
                        return lines, diagnostics


def main():
    if len(sys.argv) != 8:
        raise ValueError("configuration")
    packages, pillow, models = sys.argv[1:4]
    maximum_bytes, maximum_width, maximum_height, maximum_pixels = map(int, sys.argv[4:])
    if not (0 < maximum_bytes <= 10485760 and 0 < maximum_pixels <= 25000000
            and 0 < maximum_width <= 32768 and 0 < maximum_height <= 32768):
        raise ValueError("configuration")
    sys.path[:0] = [packages, pillow]
    from PIL import Image
    import tesserocr
    if tesserocr.__version__ != "2.11.0" or tesserocr.tesseract_version().splitlines()[0] != "tesseract 5.5.3":
        raise RuntimeError("runtime")
    Image.MAX_IMAGE_PIXELS = maximum_pixels
    warnings.simplefilter("error", Image.DecompressionBombWarning)
    content = sys.stdin.buffer.read(maximum_bytes + 1)
    if not content or len(content) > maximum_bytes:
        return invalid_media()
    try:
        with Image.open(io.BytesIO(content), formats=["PNG", "JPEG"]) as image:
            width, height = image.size
            if not (0 < width <= maximum_width and 0 < height <= maximum_height and width * height <= maximum_pixels
                    and getattr(image, "n_frames", 1) == 1):
                return invalid_media()
            image.verify()
        with Image.open(io.BytesIO(content), formats=["PNG", "JPEG"]) as image:
            image.load()
            rgb = image.convert("RGB")
    except Exception:
        return invalid_media()
    with rgb:
        lines, preprocessing = preprocess_and_read(rgb, models)
    result = {"status": "succeeded", "engine": "tesseract-5.5.3/tesserocr-2.11.0",
              "sha256": hashlib.sha256(content).hexdigest(), "length": len(content), "width": width, "height": height,
              "preprocessing": preprocessing, "lines": lines}
    encoded = json.dumps(result, ensure_ascii=True, allow_nan=False, separators=(",", ":"))
    if len(encoded.encode("utf-8")) > 131072:
        raise ValueError("output_limit")
    sys.stdout.write(encoded)
    return 0


def invalid_media():
    sys.stdout.write('{"status":"invalid_media"}')
    return 2


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:
        # Never expose paths, private text, bytes or underlying library exceptions to ordinary logs.
        sys.stdout.write('{"status":"unavailable"}')
        sys.exit(3)
