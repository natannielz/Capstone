"""Bounded PDF raster candidate; stdin original -> private NDJSON page frames.

No OCR, field extraction, file output, network, database or acceptance authority.
The .NET owner MUST confine this single child before sending bytes and discard ALL
frames unless it receives one final completed frame, EOF and exit 0 before its
captured deadline. A cooperative check cannot interrupt a stuck native parser.
See pdf-preprocessing.md for the exact protocol and remaining integration work.
"""
import base64
from contextlib import closing
import ctypes
import hashlib
import io
import json
import math
from pathlib import Path
import re
import sys
import time
import warnings
import zipfile


PIPELINE = "pdfium-static-gray-25over9-bounded-pages-v1"
PARSER_VERSION = "5.13.0"
WHEEL_SHA256 = "47dcca2a8d507b5fd24f94c3c9d48fb379430f097bc20f01beff6c963ffbcedb"
MAX_SOURCE = 10 * 1024 * 1024
MAX_EDGE = 4096
MAX_PAGE_PIXELS = 4_000_000
MAX_TOTAL_PIXELS = 25_000_000
MAX_OUTPUT = 32 * 1024 * 1024
MAX_PAGES = 10
SCALE = 25 / 9  # Canvas units, NOT a physical DPI claim (PDF UserUnit may differ).
SAFE_CODES = frozenset(("configuration", "runtime_pin", "source_limit", "source_mismatch",
    "malformed_pdf", "encrypted_pdf", "unsupported_forms", "unsupported_attachments",
    "unsupported_javascript", "page_limit", "geometry_limit", "total_pixel_limit",
    "output_limit", "deadline", "render_failed", "unavailable"))


class Rejected(Exception):
    def __init__(self, code, page_index=None):
        super().__init__(code)
        self.code = code if code in SAFE_CODES else "unavailable"
        self.page_index = page_index


class Deadline:
    def __init__(self, unix_ms, wall=time.time, monotonic=time.monotonic):
        self.wall, self.monotonic = wall, monotonic
        self.unix_ms = unix_ms
        remaining = unix_ms / 1000 - wall()
        if remaining <= 0:
            raise Rejected("deadline")
        if remaining > 60:
            raise Rejected("configuration")
        self.end = monotonic() + remaining

    def check(self):
        # Clock rollback never extends the originally captured finite window.
        if self.wall() * 1000 >= self.unix_ms or self.monotonic() >= self.end:
            raise Rejected("deadline")


def integer(value, maximum):
    if not re.fullmatch(r"[1-9][0-9]{0,15}", value):
        raise Rejected("configuration")
    result = int(value)
    if result > maximum:
        raise Rejected("configuration")
    return result


def limits(pages, page_pixels, total_pixels, output_bytes):
    values = {"maximumPages": integer(pages, MAX_PAGES),
              "maximumPagePixels": integer(page_pixels, MAX_PAGE_PIXELS),
              "maximumTotalPixels": integer(total_pixels, MAX_TOTAL_PIXELS),
              "maximumOutputBytes": integer(output_bytes, MAX_OUTPUT)}
    if values["maximumOutputBytes"] < 4096 or values["maximumTotalPixels"] < values["maximumPagePixels"]:
        raise Rejected("configuration")
    return values


def encoded(value):
    return json.dumps(value, ensure_ascii=True, allow_nan=False, separators=(",", ":")).encode("ascii")


class Frames:
    def __init__(self, stream, maximum, deadline):
        self.stream, self.maximum, self.deadline = stream, maximum, deadline
        self.written = 0

    def write(self, value):
        self.deadline.check()
        payload = encoded(value) + b"\n"
        # Reserve space for a terminal failure; no partially encoded page frame.
        if self.written + len(payload) > self.maximum - 512:
            raise Rejected("output_limit")
        self.stream.write(payload)
        self.stream.flush()
        self.written += len(payload)
        self.deadline.check()

    def failed(self, failure):
        # A deadline failure must itself be reportable after the deadline.
        value = {"event": "failed", "code": failure.code, "pageIndex": failure.page_index}
        payload = encoded(value) + b"\n"
        if self.written + len(payload) <= self.maximum:
            self.stream.write(payload)
            self.stream.flush()


class LimitedBytes(io.BytesIO):
    def __init__(self, maximum):
        super().__init__()
        self.maximum = maximum

    def write(self, value):
        if self.tell() + len(value) > self.maximum:
            raise Rejected("output_limit")
        return super().write(value)


def original(stream, expected_hash, expected_length, deadline):
    if not re.fullmatch(r"[0-9a-f]{64}", expected_hash) or not 0 < expected_length <= MAX_SOURCE:
        raise Rejected("configuration")
    deadline.check()
    content = stream.read(expected_length + 1)
    deadline.check()
    if not content or len(content) > MAX_SOURCE or len(content) > expected_length:
        raise Rejected("source_limit")
    if len(content) != expected_length or hashlib.sha256(content).hexdigest() != expected_hash:
        raise Rejected("source_mismatch")
    if not re.match(rb"%PDF-(?:1\.[0-7]|2\.0)[\r\n]", content) or not re.search(rb"%%EOF[\x00\t\n\x0c\r ]*$", content[-1024:]):
        raise Rejected("malformed_pdf")
    return content


def pinned_runtime(packages, pillow, wheel_path, deadline):
    """Check the actual installed parser files against the exact trusted wheel.

    Parent still owns immutable-path/reparse/DLL dependencies, Python/Pillow pins
    and read locks. This comparison is not an OS sandbox or a TOCTOU substitute.
    """
    roots = [Path(value) for value in (packages, pillow, wheel_path)]
    if any(not path.is_absolute() for path in roots):
        raise Rejected("configuration")
    packages, pillow, wheel_path = roots
    deadline.check()
    with wheel_path.open("rb") as source:
        wheel = source.read(8 * 1024 * 1024 + 1)
    if len(wheel) > 8 * 1024 * 1024 or hashlib.sha256(wheel).hexdigest() != WHEEL_SHA256:
        raise Rejected("runtime_pin")
    expected = set()
    with zipfile.ZipFile(io.BytesIO(wheel)) as archive:
        members = archive.infolist()
        if len(members) > 512 or sum(member.file_size for member in members) > 64 * 1024 * 1024:
            raise Rejected("runtime_pin")
        for member in members:
            if member.is_dir() or member.filename.split("/", 1)[0] not in ("pypdfium2", "pypdfium2_raw", "pypdfium2_cfg"):
                continue
            relative = Path(member.filename)
            if relative.is_absolute() or ".." in relative.parts or "\\" in member.filename:
                raise Rejected("runtime_pin")
            path = packages / relative
            if not path.is_file() or path.is_symlink():
                raise Rejected("runtime_pin")
            expected.add(member.filename)
            with path.open("rb") as installed:
                actual = installed.read(member.file_size + 1)
            if actual != archive.read(member):
                raise Rejected("runtime_pin")
            deadline.check()
    if not {"pypdfium2/__init__.py", "pypdfium2_raw/__init__.py", "pypdfium2_cfg/__init__.py"}.issubset(expected):
        raise Rejected("runtime_pin")
    for name in ("pypdfium2", "pypdfium2_raw", "pypdfium2_cfg"):
        for path in (packages / name).rglob("*"):
            if path.is_symlink():
                raise Rejected("runtime_pin")
            if path.is_file() and path.relative_to(packages).as_posix() not in expected:
                raise Rejected("runtime_pin")
    # -I -S is required by the parent. Absolute directories contain pinned code only.
    sys.path[:0] = [str(packages), str(pillow)]
    import pypdfium2 as pdfium
    from PIL import Image
    if pdfium.PYPDFIUM_INFO.version != PARSER_VERSION or pdfium.PDFIUM_INFO.flags:
        raise Rejected("runtime_pin")
    warnings.simplefilter("error", pdfium.PdfiumWarning)
    Image.MAX_IMAGE_PIXELS = MAX_PAGE_PIXELS
    warnings.simplefilter("error", Image.DecompressionBombWarning)
    deadline.check()
    return pdfium, {"wrapper": PARSER_VERSION, "pdfium": str(pdfium.PDFIUM_INFO),
                    "wheelSha256": WHEEL_SHA256, "pillow": Image.__version__}


def geometry(page, bounds):
    size, box, rotation = page.get_size(), page.get_bbox(), page.get_rotation()
    if len(size) != 2 or len(box) != 4 or any(not math.isfinite(value) or abs(value) > 14400 for value in (*size, *box)):
        raise Rejected("geometry_limit")
    if min(size) <= 0 or box[2] <= box[0] or box[3] <= box[1] or rotation not in (0, 90, 180, 270):
        raise Rejected("geometry_limit")
    width, height = (math.ceil(value * SCALE) for value in size)
    if width > MAX_EDGE or height > MAX_EDGE or width * height > bounds["maximumPagePixels"]:
        raise Rejected("geometry_limit")
    return {"canvasSize": list(size), "visibleBox": list(box), "rotationDegrees": rotation,
            "width": width, "height": height}


def document_checks(pdf, raw):
    if raw.FPDF_GetSecurityHandlerRevision(pdf.raw) != -1:
        raise Rejected("encrypted_pdf")
    if not raw.FPDF_DocumentHasValidCrossReferenceTable(pdf.raw):
        raise Rejected("malformed_pdf")
    if pdf.get_formtype() != raw.FORMTYPE_NONE:
        raise Rejected("unsupported_forms")
    if pdf.count_attachments() != 0:
        raise Rejected("unsupported_attachments")
    actions = raw.FPDFDoc_GetJavaScriptActionCount(pdf.raw)
    if actions < 0:
        raise Rejected("malformed_pdf")
    if actions:
        raise Rejected("unsupported_javascript")


def page_corners(page, raw, shape):
    result = []
    for x, y in ((0, 0), (shape["width"], 0), (shape["width"], shape["height"]), (0, shape["height"])):
        px, py = ctypes.c_double(), ctypes.c_double()
        if not raw.FPDF_DeviceToPage(page.raw, 0, 0, shape["width"], shape["height"], 0, x, y, ctypes.byref(px), ctypes.byref(py)):
            raise Rejected("render_failed")
        if not math.isfinite(px.value) or not math.isfinite(py.value):
            raise Rejected("geometry_limit")
        result.append([px.value, py.value])
    return result


def render(content, pdfium, runtime, bounds, deadline, frames):
    raw = pdfium.raw
    try:
        pdf = pdfium.PdfDocument(content)  # No password interface, forms initialization or action execution.
    except pdfium.PdfiumError as error:
        raise Rejected("encrypted_pdf" if error.err_code in (raw.FPDF_ERR_PASSWORD, raw.FPDF_ERR_SECURITY) else "malformed_pdf") from None
    with pdf:
        deadline.check()
        document_checks(pdf, raw)
        count = len(pdf)
        if not 0 < count <= bounds["maximumPages"]:
            raise Rejected("page_limit")
        plans, pixels = [], 0
        # Validate every page's geometry before rendering/output. Never silently drop pages.
        for index in range(count):
            deadline.check()
            try:
                with closing(pdf[index]) as page:
                    shape = geometry(page, bounds)
                pixels += shape["width"] * shape["height"]
                if pixels > bounds["maximumTotalPixels"]:
                    raise Rejected("total_pixel_limit")
                plans.append(shape)
            except Rejected as error:
                error.page_index = index
                raise
            except Exception:
                raise Rejected("malformed_pdf", index) from None
        source_hash = hashlib.sha256(content).hexdigest()
        frames.write({"event": "document", "protocol": 1, "pipeline": PIPELINE,
                      "source": {"sha256": source_hash, "length": len(content), "mime": "application/pdf"},
                      "runtime": runtime, "limits": bounds, "pageCount": count,
                      "scale": {"numerator": 25, "denominator": 9}, "requiresHumanReview": True})
        digest = hashlib.sha256()
        for index, shape in enumerate(plans):
            try:
                deadline.check()
                with closing(pdf[index]) as page:
                    if geometry(page, bounds) != shape:
                        raise Rejected("malformed_pdf")
                    corners = page_corners(page, raw, shape)
                    with closing(page.render(scale=SCALE, rotation=0, crop=(0, 0, 0, 0), may_draw_forms=False,
                                     grayscale=True, draw_annots=True, limit_image_cache=True,
                                     fill_color=(255, 255, 255, 255))) as bitmap:
                        deadline.check()
                        with bitmap.to_pil() as image, LimitedBytes(MAX_SOURCE) as png:
                            if image.mode != "L" or image.size != (shape["width"], shape["height"]):
                                raise Rejected("render_failed")
                            extrema = image.getextrema()
                            image.save(png, format="PNG", compress_level=6, optimize=False)
                            image_bytes = png.getvalue()
                    deadline.check()
                facts = {"pageIndex": index, "sourceSha256": source_hash, **shape,
                         "pixelCornersInPdfCanvas": corners, "mode": "L", "mime": "image/png",
                         "length": len(image_bytes), "sha256": hashlib.sha256(image_bytes).hexdigest(),
                         "reviewReasons": ["human_review_required"] + (["no_visible_marks"] if extrema[0] == extrema[1] else [])}
                digest.update(encoded(facts) + b"\n")
                frames.write({"event": "page", "page": facts, "pngBase64": base64.b64encode(image_bytes).decode("ascii")})
            except Rejected as error:
                error.page_index = index
                raise
            except Exception:
                raise Rejected("render_failed", index) from None
        deadline.check()
        frames.write({"event": "completed", "sourceSha256": source_hash, "pageCount": count,
                      "totalPixels": pixels, "pagesSha256": digest.hexdigest()})


def main(argv=None, source=None, output=None):
    argv = sys.argv[1:] if argv is None else argv
    source, output = source or sys.stdin.buffer, output or sys.stdout.buffer
    frames = None
    try:
        if len(argv) != 10:
            raise Rejected("configuration")
        packages, pillow, wheel, expected_hash, expected_length, pages, page_pixels, total_pixels, output_bytes, unix_ms = argv
        bounds = limits(pages, page_pixels, total_pixels, output_bytes)
        expected_length = integer(expected_length, MAX_SOURCE)
        deadline = Deadline(integer(unix_ms, 9_999_999_999_999_999))
        frames = Frames(output, bounds["maximumOutputBytes"], deadline)
        content = original(source, expected_hash, expected_length, deadline)
        runtime, provenance = pinned_runtime(packages, pillow, wheel, deadline)
        render(content, runtime, provenance, bounds, deadline, frames)
        return 0
    except Exception as error:
        failure = error if isinstance(error, Rejected) else Rejected("unavailable")
        if frames is None:
            output.write(encoded({"event": "failed", "code": failure.code, "pageIndex": None}) + b"\n")
            output.flush()
        else:
            frames.failed(failure)
        return 2 if failure.code not in ("configuration", "runtime_pin", "unavailable") else 3


if __name__ == "__main__":
    # Never print underlying exceptions, paths, PDF metadata/text, or original bytes.
    sys.exit(main())
