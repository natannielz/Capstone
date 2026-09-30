# Local OCR evaluation

## Status — 30 September 2026

**No production model has been selected.** One Tesseract candidate now runs locally through the bounded .NET process adapter. This is a reproducible synthetic smoke measurement, not the planned held-out payment-image comparison, browser/worker integration or business acceptance. PP-OCRv6 tiny/small, private annotated screenshots and phone photos, approved thresholds, reviewer effort and reference-runtime/ONNX parity remain unmeasured.

The [official PP-OCRv6 documentation](https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/algorithm/PP-OCRv6/PP-OCRv6.en.md) describes tiny/small candidates and inference backends. Its general benchmarks do not establish Unit Toko payment-field accuracy. [Tesseract's model documentation](https://tesseract-ocr.github.io/tessdoc/Data-Files.html) identifies the language-model families; the exact candidate assets are recorded in [tesseract-candidate.json](../../backend/document-processing/tesseract-candidate.json).

## Measured candidate

- Windows x64, Tesseract 5.5.3 / tesserocr 2.11.0, Leptonica 1.87.0; Python 3.12.14, Pillow 12.3.0. The wrapper wheel's published GitHub release digest was checked before local installation. Official `tessdata_fast` Indonesian/English files were fetched from commit `87416418657359cb625c412a48b6e1d6d41c29bd`; observed SHA-256 values are in the asset record. Models and binaries remain ignored local dependencies.
- Model-set SHA-256: `4f8b56151c13140e66f33445784d7b77a00bc53456fb1a99b4d48ddf2bc49c49`. Preprocessing/parser: `original-rgb-psm6-ind+eng-v1/payment-labels-id-en-v1`. Full candidate runtime pins are retained locally in `tmp/production/ocr-fixtures/tesseract-candidate-pins.json`; these are synthetic test pins, not a reviewed production manifest. Python standard-library/OS acceptance remains a separate host gate.
- Host: Windows 10.0.26200, AMD Ryzen 7 5700X, 16 logical processors, 17,063,391,232 bytes physical RAM. Configured process cap: 512 MiB committed memory and 20 seconds including file-pin verification; one child process, bounded input/output, PNG/JPEG only, at most 25 million pixels.
- Eight generated images, each processed twice: clean, blurred, rotated, compressed, no amount, conflicting amounts, uncommon amount, blank. A visible synthetic label, generator source, image/font hashes and expected fields identify the fixture set. It is not held out; repeated observations are not independent accuracy samples.

## Observed results

The reproducible run recorded in `tmp/production/ocr-results/ocr-candidate-20260930033042.trx` passed its process/protocol/parser assertions with no skips. The local aggregate and per-fixture field-match report is `tmp/production/ocr-fixtures/tesseract-candidate-results.json`. A passed smoke test means the process completed and measurements were recorded; it does **not** mean the accuracy requirement passed.

| Measure | Observed value and interpretation |
| --- | --- |
| Expected known fields | 70 observations: 58 exact, 10 unknown, 2 wrong |
| Expected unknown fields | 58 observations; no false known values in this small synthetic set |
| Rotation | Missing amount/currency/date/time/account and a wrong reference; failures retained in the report |
| Clean, blurred, compressed | All eight expected fields matched on these generated samples |
| First process / subsequent clean process | 605.7 ms / 588.4 ms |
| All samples, p50 / p95 | 501.7 ms / 706.3 ms; includes hash checks, fresh Python/model startup, recognition and bounded output |
| Peak memory | 50,323,456 bytes Windows job peak committed memory; this is not RSS |
| Persistent-model warm latency | Not measured; every invocation starts a fresh process, potentially benefiting from OS caches |
| Reviewer correction effort / release thresholds | Not measured / not approved |

The first real run found that Tesseract returns a non-null iterator for a blank page but throws when its empty line is read. The helper now checks the recognized page text before iterating. The original failing TRX remains retained, and blank-image handling passes the corrected/final runs.

Release selection still requires the accepted comparison across varied bank layouts, blur/rotation/clipping/compression, uncommon amounts and repeated references. Report per-field exact/unknown/wrong rates, false matches, actual target hardware, latency/memory, manual correction effort and portability against agreed thresholds. Do not tune and evaluate against the same private holdout or substitute these smoke measurements for it. Approval and received funds always require independent bank corroboration.

See the [execution runbook](../runbooks/document-processing.md) for the actual local command and the remaining integration boundary. Progress is tracked only in [production-progress.md](production-progress.md).
