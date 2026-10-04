# Bounded PDF raster candidate

`pdf_preprocess.py` is a separate, not-yet-connected subprocess candidate. The current payment/supplier image path and `local_ocr.py` are unchanged. Product PDF upload, extraction, multipage review and delivery-document OCR are **not enabled** by these files. The original PDF remains the owning private evidence; generated pages never replace it and carry no business acceptance.

The [parser manifest](pdf-parser-candidate.json) pins the exact free Windows x64 `pypdfium2 5.13.0` wheel and official metadata/checksum. It adds a renderer, not a competing OCR engine. OCR remains [Tesseract 5.5.3/tesserocr 2.11.0](tesseract-candidate.json), `ind+eng`. Provision the wheel with no dependency resolution, compilation, source-build fallback or `.pyc` generation. The helper verifies wheel bytes and every installed parser package file before importing; extra files, including unpinned bytecode, fail. Python/Pillow/native dependency pins and immutable directory/read-lock controls still belong to the .NET parent. Keep the wheel's PDFium and third-party notices in the runtime inventory.

## Invocation and private protocol

The parent launches the pinned Python executable with `-I -S -B`, this helper's absolute path, then **ten** separate arguments (never a shell-composed command):

```text
packages pillow wheel expectedSha256 expectedLength maximumPages maximumPagePixels maximumTotalPixels maximumOutputBytes capturedDeadlineUnixMilliseconds
```

The first three are existing absolute pinned runtime paths. The parent sends exactly one assigned PDF on stdin, closes stdin, and concurrently drains bounded stdout/stderr. Source bytes and expected hash/length must come from the actual immutable evidence owner. There is no input filename, URL, password, database connection or output directory option. The helper writes no source/derivative files and does not extract PDF text, filenames, metadata or financial fields.

Stdout is ASCII NDJSON. `document` identifies source hash/length, actual renderer versions, checked wheel hash, limits and exact page count. Ordered `page` frames contain a PNG in `pngBase64` and facts: zero-based index, PDF source hash, actual canvas size/visible box/rotation, rendered dimensions, four image corners mapped by PDFium into PDF canvas coordinates, PNG hash/length, and explicit human-review reasons. `completed` carries count, total pixels and SHA-256 of the ordered compact page-facts JSON records, each followed by LF. The JSON key order is the emitted order; do not reserialize through an unordered dictionary to verify that digest.

Every frame is provisional until the parent verifies one final `completed`, exact page sequence/count, original/image hashes and decoded dimensions, EOF and exit **0**, all before the already captured private-read/job/policy deadline. Any `failed` frame, unknown/extra field, trailing frame, truncated pipe, process crash, timeout, output overflow or nonzero exit invalidates **all pages**. No partial document may become an extraction. Failure frames contain only a finite safe code and nullable page index, never source text or underlying library/path exceptions. Exit 2 is a rejected input/budget; exit 3 is unavailable runtime/configuration. Keep all page bytes private and out of ordinary logs.

## Bounds and exclusions

The caller may lower, never raise, the manifest ceilings: 10 MiB source, ten pages, 4 million pixels/page, 25 million aggregate pixels, 4096-pixel edges, 32 MiB framed output and at most 60 seconds of **remaining** captured time. Every page's geometry is checked before rendering. The fixed scale is 25/9 pixels per PDF canvas unit, not a physical-DPI assertion: PDF UserUnit is not exposed by this renderer API. Oversized pages are rejected rather than cropped, silently scaled or omitted. A single bitmap is rendered/encoded at a time; Python-level image/output allocations are bounded, but hostile native parser internals still require OS confinement.

Password-required PDFs and encrypted PDFs that open with an empty password are both rejected. Parser failures, invalid header/trailer and rebuilt cross-reference tables are rejected. This is not a PDF conformance certification; PDFium can accept some nonconforming content. Forms, embedded attachments and reported JavaScript actions are unsupported and rejected. The wheel must have no V8/XFA flags; no forms environment/actions are initialized. Static annotations are rasterized with the page. This is not malware approval: genuine security admission/scanning, immutable ownership and current policy must precede processing.

Cooperative absolute-wall and monotonic deadline checks surround reads, parsing, pages, encoding and output. They cannot interrupt a native call or blocked pipe. Before sending bytes, the .NET owner must start the child suspended, attach the existing kill-on-close Windows Job with finite memory/CPU/one-process limits, then resume. It must kill the whole job on the shortest captured deadline, cancellation, stale lease or any protocol fault. Do not renew a private-read window after rendering. No parent confinement, admission, migration, OCR composition or durable result write is implemented in this candidate.

## Focused verification

`test_pdf_preprocess.py` runs standard-library contract tests by default. The separate explicit `--native packages pillow wheel` mode runs those plus genuine generated PDFs through the pinned renderer; absent/wrong native dependencies fail instead of producing skips. Neither mode installs or downloads anything. Native mode is still a local synthetic check, not provider/host/policy or real-document acceptance. Parent cancellation/memory/egress behavior and end-to-end page-aware human review need the actual .NET integration.

```text
PINNED_PYTHON -B backend/document-processing/test_pdf_preprocess.py
PINNED_PYTHON -B backend/document-processing/test_pdf_preprocess.py --native ABS_PACKAGES ABS_PILLOW ABS_WHEEL
```

The initial eight standard-library contract checks passed locally on 4 October 2026 without exercising a native parser. After provisioning the exact wheel, the first native run exposed an incorrect context-manager assumption: this pinned version gives page/bitmap handles `close()` but only documents implement `with` directly. Explicit `contextlib.closing` now releases those handles on success and failure. Runtime-file verification also covers the wheel's imported `pypdfium2_cfg` package.

The unchanged rejection/provenance assertions then passed with the real pinned renderer: thirteen checks in 0.289 seconds, no skips (`tmp/production/pdf-preprocessor-native-candidate-release2.log`). The original three-failure run is retained as `tmp/production/pdf-preprocessor-native-candidate-release1.log`. Native fixtures cover multipage/rotated raster hashes and coordinates, valid encrypted documents with both nonempty and empty passwords, repaired-xref/form refusal, later-page geometry/aggregate limits and output overflow. These function-level native fixtures do not test the future .NET process/job/protocol consumer or establish product PDF support.

API sources: [pypdfium2 render/bitmap/document API](https://pypdfium2.readthedocs.io/en/stable/python_api.html), [PDFium document/encryption/xref/device mapping](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/public/fpdfview.h), [JavaScript action inspection](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/public/fpdf_javascript.h). Stable docs can move; the checked wheel is the executable API pin. See the [current OCR runbook](../../docs/runbooks/document-processing.md) and [progress ledger](../../docs/quality/production-progress.md) for integrated support and acceptance status.
