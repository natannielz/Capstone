"""One-image offline candidate protocol. Invoked by the bounded .NET parent, never as a web service.

No database, network, credential, file-output or automatic model-download interface.
The parent pins the helper, libraries and official model files before giving it an assigned stream.
"""
import hashlib
import io
import json
import sys
import warnings


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
    lines = []
    total_chars = 0
    with rgb, tesserocr.PyTessBaseAPI(path=models, lang="ind+eng", oem=tesserocr.OEM.LSTM_ONLY, psm=tesserocr.PSM.SINGLE_BLOCK) as api:
        api.SetImage(rgb)
        api.Recognize()
        # Tesseract returns a non-null iterator for a blank page, but reading its line throws.
        # A successfully recognized blank image is an unknown-field result, not a runtime outage.
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
                    x1, y1, x2, y2 = box
                    # Integer pixel boundaries are converted downwards to avoid x+width exceeding one through rounding.
                    norm = lambda numerator, denominator: (numerator * 1000000 // denominator) / 1000000
                    lines.append({"text": text, "confidence": round(confidence, 6),
                                  "box": [norm(x1, width), norm(y1, height), norm(x2 - x1, width), norm(y2 - y1, height)]})
                if not iterator.Next(tesserocr.RIL.TEXTLINE):
                    break
    result = {"status": "succeeded", "engine": "tesseract-5.5.3/tesserocr-2.11.0",
              "sha256": hashlib.sha256(content).hexdigest(), "length": len(content), "width": width, "height": height, "lines": lines}
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
