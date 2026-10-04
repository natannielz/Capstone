"""Standalone contract checks; --native adds actual pinned PDFium/Pillow fixtures.

No installation, network or application writes. Native mode fails if its runtime
is missing; pure contract tests do not pretend to exercise a native parser.
"""
import base64
import hashlib
import io
import json
from pathlib import Path
import struct
import sys
import time
import unittest
from unittest.mock import patch

import pdf_preprocess as candidate


def bounds(**changed):
    result = candidate.limits("10", "4000000", "25000000", "33554432")
    result.update(changed)
    return result


def deadline():
    return candidate.Deadline(int(time.time() * 1000) + 30_000)


def digest(content):
    return hashlib.sha256(content).hexdigest()


def pdf_fixture(pages=((72, 96, 0),), *, damaged_xref=False, encrypted=False, password="reader", forms=False):
    """Original synthetic vector PDF, classic exact xref; no third-party writer.

    Encrypted variants use the PDF standard security handler revision 2 solely to
    exercise refusal (including empty-password encryption). Native tests prove
    they open with their actual password before using them as rejection evidence.
    """
    padding = bytes.fromhex("28bf4e5e4e758a4164004e56fffa01082e2e00b6d0683e802f0ca9fe6453697a")
    def pad(value):
        return (value.encode("ascii") + padding)[:32]
    def rc4(key, value):
        state = list(range(256)); second = 0
        for first in range(256):
            second = (second + state[first] + key[first % len(key)]) % 256
            state[first], state[second] = state[second], state[first]
        first = second = 0; result = bytearray()
        for byte in value:
            first = (first + 1) % 256; second = (second + state[first]) % 256
            state[first], state[second] = state[second], state[first]
            result.append(byte ^ state[(state[first] + state[second]) % 256])
        return bytes(result)
    identifier = bytes.fromhex("0123456789abcdef0123456789abcdef")
    owner = rc4(hashlib.md5(pad("synthetic-owner")).digest()[:5], pad(password))
    key = hashlib.md5(pad(password) + owner + struct.pack("<i", -4) + identifier).digest()[:5]
    user = rc4(key, padding)
    objects = [b"", b""]
    page_ids = []
    for index, (width, height, rotation) in enumerate(pages):
        page_id, content_id = len(objects) + 1, len(objects) + 2
        page_ids.append(page_id)
        content = f"q 0 g {8 + index} 10 18 22 re f Q\n".encode("ascii")
        if encrypted:
            object_key = hashlib.md5(key + content_id.to_bytes(3, "little") + b"\0\0").digest()[:10]
            content = rc4(object_key, content)
        objects += [f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Rotate {rotation} /Resources << >> /Contents {content_id} 0 R >>".encode("ascii"),
                    b"<< /Length " + str(len(content)).encode("ascii") + b" >>\nstream\n" + content + b"\nendstream"]
    extra = b""
    if forms:
        field_id = len(objects) + 1
        objects.append(b"<< /FT /Tx /T (synthetic-field) /V (unrendered) >>")
        extra = f" /AcroForm << /Fields [{field_id} 0 R] >>".encode("ascii")
    objects[0] = b"<< /Type /Catalog /Pages 2 0 R" + extra + b" >>"
    objects[1] = b"<< /Type /Pages /Count " + str(len(pages)).encode("ascii") + b" /Kids [" + b" ".join(f"{value} 0 R".encode("ascii") for value in page_ids) + b"] >>"
    trailer_extra = b""
    if encrypted:
        encryption_id = len(objects) + 1
        objects.append(b"<< /Filter /Standard /V 1 /R 2 /Length 40 /O <" + owner.hex().encode("ascii") +
                       b"> /U <" + user.hex().encode("ascii") + b"> /P -4 >>")
        trailer_extra = f" /Encrypt {encryption_id} 0 R /ID [<{identifier.hex()}> <{identifier.hex()}>]".encode("ascii")
    result = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
    offsets = [0]
    for index, value in enumerate(objects, 1):
        offsets.append(len(result)); result.extend(f"{index} 0 obj\n".encode("ascii") + value + b"\nendobj\n")
    xref = len(result)
    result.extend(f"xref\n0 {len(offsets)}\n0000000000 65535 f \n".encode("ascii"))
    for offset in offsets[1:]:
        result.extend(f"{offset:010} 00000 n \n".encode("ascii"))
    result.extend(f"trailer\n<< /Size {len(offsets)} /Root 1 0 R".encode("ascii") + trailer_extra +
                  f" >>\nstartxref\n{xref if not damaged_xref else 1}\n%%EOF\n".encode("ascii"))
    return bytes(result)


class ContractTests(unittest.TestCase):
    def test_original_is_exact_bounded_immutable_and_never_repaired(self):
        content = pdf_fixture(); unchanged = bytes(content)
        self.assertEqual(candidate.original(io.BytesIO(content), digest(content), len(content), deadline()), unchanged)
        for value, code in ((content + b"x", "source_limit"), (content[:-1], "source_mismatch")):
            with self.assertRaises(candidate.Rejected) as failure:
                candidate.original(io.BytesIO(value), digest(content), len(content), deadline())
            self.assertEqual(failure.exception.code, code)
        with self.assertRaises(candidate.Rejected) as failure:
            candidate.original(io.BytesIO(content), "0" * 64, len(content), deadline())
        self.assertEqual(failure.exception.code, "source_mismatch")
        self.assertEqual(content, unchanged)

    def test_non_pdf_missing_eof_or_trailing_payload_is_rejected(self):
        for value in (b"not a PDF\n%%EOF", pdf_fixture().replace(b"%%EOF", b"broken"), pdf_fixture() + b"private payload"):
            with self.subTest(value=digest(value)), self.assertRaises(candidate.Rejected) as failure:
                candidate.original(io.BytesIO(value), digest(value), len(value), deadline())
            self.assertEqual(failure.exception.code, "malformed_pdf")

    def test_limits_are_canonical_and_cannot_raise_caps(self):
        for invalid in ("0", "01", "-1", "1.0", "true", " 1", "10000000000000000000"):
            with self.subTest(invalid=invalid), self.assertRaises(candidate.Rejected):
                candidate.limits(invalid, "4000000", "25000000", "33554432")
        for values in (("11", "4000000", "25000000", "33554432"), ("1", "4000001", "25000000", "33554432"),
                       ("1", "4000000", "25000001", "33554432"), ("1", "4000000", "25000000", "33554433"),
                       ("1", "4000000", "1", "4096"), ("1", "4000000", "25000000", "4095")):
            with self.subTest(values=values), self.assertRaises(candidate.Rejected):
                candidate.limits(*values)

    def test_monotonic_expiry_survives_wall_clock_rollback(self):
        wall, mono = [100.0], [25.0]
        clock = candidate.Deadline(101_000, lambda: wall[0], lambda: mono[0])
        clock.check(); wall[0] = 0; mono[0] = 26
        with self.assertRaises(candidate.Rejected) as failure:
            clock.check()
        self.assertEqual(failure.exception.code, "deadline")
        with self.assertRaises(candidate.Rejected):
            candidate.Deadline(200_000, lambda: 100, lambda: 0)

    def test_page_dimensions_rotation_and_nan_are_rejected_before_bitmap(self):
        class Page:
            def __init__(self, size, box=(0, 0, 72, 96), rotation=0):
                self.size, self.box, self.rotation = size, box, rotation
            def get_size(self): return self.size
            def get_bbox(self): return self.box
            def get_rotation(self): return self.rotation
        good = candidate.geometry(Page((72, 96)), bounds())
        self.assertEqual((good["width"], good["height"]), (200, 267))
        for page in (Page((float("nan"), 10)), Page((0, 10)), Page((5000, 10)), Page((900, 900)),
                     Page((72, 96), rotation=45), Page((72, 96), box=(10, 0, 1, 10))):
            with self.assertRaises(candidate.Rejected) as failure:
                candidate.geometry(page, bounds())
            self.assertEqual(failure.exception.code, "geometry_limit")

    def test_output_failure_keeps_terminal_error_room_and_no_partial_json(self):
        stream = io.BytesIO(); frames = candidate.Frames(stream, 4096, deadline())
        frames.write({"event": "page", "pageIndex": 0})
        with self.assertRaises(candidate.Rejected) as failure:
            frames.write({"event": "page", "pngBase64": "a" * 4096})
        frames.failed(failure.exception)
        values = [json.loads(line) for line in stream.getvalue().splitlines()]
        self.assertEqual([value["event"] for value in values], ["page", "failed"])
        self.assertEqual(values[-1]["code"], "output_limit")
        self.assertLessEqual(len(stream.getvalue()), 4096)
        with candidate.LimitedBytes(3) as small:
            small.write(b"abc")
            with self.assertRaises(candidate.Rejected): small.write(b"d")
            self.assertEqual(small.getvalue(), b"abc")

    def test_bad_configuration_has_safe_error_only_and_does_not_import_parser(self):
        output = io.BytesIO()
        with patch.object(candidate, "pinned_runtime", side_effect=AssertionError("must not load")):
            self.assertEqual(candidate.main([], io.BytesIO(b"private"), output), 3)
        self.assertEqual(json.loads(output.getvalue()), {"event": "failed", "code": "configuration", "pageIndex": None})

    def test_no_password_or_source_file_interface(self):
        output = io.BytesIO()
        self.assertEqual(candidate.main(["--password", "secret"], io.BytesIO(), output), 3)
        self.assertNotIn(b"secret", output.getvalue())


class NativeTests(unittest.TestCase):
    runtime_paths = None

    @classmethod
    def setUpClass(cls):
        cls.pdfium, cls.provenance = candidate.pinned_runtime(*cls.runtime_paths, deadline())

    def process(self, content, selected=None, clock=None):
        output = io.BytesIO(); clock = clock or deadline()
        frames = candidate.Frames(output, (selected or bounds())["maximumOutputBytes"], clock)
        failure = None
        try:
            checked = candidate.original(io.BytesIO(content), digest(content), len(content), clock)
            candidate.render(checked, self.pdfium, self.provenance, selected or bounds(), clock, frames)
        except candidate.Rejected as error:
            failure = error; frames.failed(error)
        return [json.loads(line) for line in output.getvalue().splitlines()], failure

    def test_genuine_multiple_pages_have_exact_original_png_hashes_rotation_and_manifest(self):
        from PIL import Image
        content = pdf_fixture(((72, 96, 0), (120, 72, 90), (36, 54, 180)))
        unchanged = bytes(content); values, error = self.process(content)
        self.assertIsNone(error)
        self.assertEqual([value["event"] for value in values], ["document", "page", "page", "page", "completed"])
        header, complete = values[0], values[-1]
        self.assertEqual(header["source"], {"sha256": digest(content), "length": len(content), "mime": "application/pdf"})
        self.assertEqual(header["runtime"]["wrapper"], candidate.PARSER_VERSION)
        self.assertEqual(header["runtime"]["wheelSha256"], candidate.WHEEL_SHA256)
        manifest = hashlib.sha256(); pixels = 0
        for index, frame in enumerate(values[1:-1]):
            facts = frame["page"]; png = base64.b64decode(frame["pngBase64"], validate=True)
            self.assertEqual(facts["pageIndex"], index); self.assertEqual(facts["sourceSha256"], digest(content))
            self.assertEqual(facts["sha256"], digest(png)); self.assertEqual(facts["length"], len(png))
            with Image.open(io.BytesIO(png)) as image:
                image.load(); self.assertEqual(image.mode, "L")
                self.assertEqual(image.size, (facts["width"], facts["height"]))
                self.assertLess(image.getextrema()[0], image.getextrema()[1])
            self.assertEqual(facts["rotationDegrees"], (0, 90, 180)[index])
            for x, y in facts["pixelCornersInPdfCanvas"]:
                self.assertTrue(facts["visibleBox"][0] - .001 <= x <= facts["visibleBox"][2] + .001)
                self.assertTrue(facts["visibleBox"][1] - .001 <= y <= facts["visibleBox"][3] + .001)
            self.assertEqual(facts["reviewReasons"], ["human_review_required"])
            manifest.update(candidate.encoded(facts) + b"\n"); pixels += facts["width"] * facts["height"]
        self.assertEqual(complete, {"event": "completed", "sourceSha256": digest(content), "pageCount": 3,
                                    "totalPixels": pixels, "pagesSha256": manifest.hexdigest()})
        self.assertEqual(content, unchanged)
        repeated, error = self.process(content); self.assertIsNone(error); self.assertEqual(values, repeated)

    def test_genuine_encryption_is_rejected_even_with_empty_user_password(self):
        for password in ("reader", ""):
            content = pdf_fixture(encrypted=True, password=password)
            with self.pdfium.PdfDocument(content, password=password) as opened:
                self.assertEqual(len(opened), 1)
                self.assertEqual(self.pdfium.raw.FPDF_GetSecurityHandlerRevision(opened.raw), 2)
            values, error = self.process(content)
            self.assertEqual(error.code, "encrypted_pdf"); self.assertEqual(len(values), 1)
            self.assertEqual(values[0]["event"], "failed")

    def test_genuine_repaired_xref_and_forms_never_render(self):
        for content, code in ((pdf_fixture(damaged_xref=True), "malformed_pdf"),
                              (pdf_fixture(forms=True), "unsupported_forms")):
            values, error = self.process(content)
            self.assertEqual(error.code, code); self.assertEqual([value["event"] for value in values], ["failed"])

    def test_later_page_geometry_and_aggregate_limits_emit_no_page_frames(self):
        cases = [(pdf_fixture(((72, 96, 0), (5000, 5000, 0))), bounds(), "geometry_limit"),
                 (pdf_fixture(((72, 96, 0),) * 3), bounds(maximumPages=2), "page_limit"),
                 (pdf_fixture(((72, 96, 0),) * 3), bounds(maximumPagePixels=100000, maximumTotalPixels=100000), "total_pixel_limit")]
        for content, selected, code in cases:
            values, error = self.process(content, selected)
            self.assertEqual(error.code, code); self.assertEqual([value["event"] for value in values], ["failed"])

    def test_real_pages_cannot_turn_output_overflow_into_completed_document(self):
        values, error = self.process(pdf_fixture(((72, 96, 0),) * 8), bounds(maximumOutputBytes=4096))
        self.assertEqual(error.code, "output_limit")
        self.assertEqual(values[-1]["event"], "failed")
        self.assertNotIn("completed", [value["event"] for value in values])


if __name__ == "__main__":
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(ContractTests)
    if len(sys.argv) > 1:
        if len(sys.argv) != 5 or sys.argv[1] != "--native" or not all(Path(value).is_absolute() for value in sys.argv[2:]):
            raise SystemExit("Usage: test_pdf_preprocess.py [--native ABS_PACKAGES ABS_PILLOW ABS_WHEEL]")
        NativeTests.runtime_paths = sys.argv[2:]
        suite.addTests(unittest.defaultTestLoader.loadTestsFromTestCase(NativeTests))
    outcome = unittest.TextTestRunner(verbosity=2).run(suite)
    raise SystemExit(0 if outcome.wasSuccessful() and outcome.testsRun > 0 and not outcome.skipped else 1)
