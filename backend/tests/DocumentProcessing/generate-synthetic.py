"""Generate clearly synthetic OCR smoke fixtures; this is not a held-out bank corpus or model benchmark acceptance."""
import argparse
import hashlib
import json
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter, ImageFont


parser = argparse.ArgumentParser()
parser.add_argument("--output", required=True, type=Path)
parser.add_argument("--font", required=True, type=Path)
parser.add_argument("--supplier-only", action="store_true")
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
font = ImageFont.truetype(str(args.font), 32)
title = ImageFont.truetype(str(args.font), 26)


def receipt(lines, heading="SYNTHETIC TEST - BUKAN BUKTI PEMBAYARAN"):
    image = Image.new("RGB", (1000, 650), "white")
    draw = ImageDraw.Draw(image)
    draw.text((40, 25), heading, font=title, fill="black")
    for index, line in enumerate(lines):
        draw.text((40, 90 + index * 65), line, font=font, fill="black")
    return image


lines = ["Nominal: Rp 1.234.567,89", "Tanggal: 30/09/2026", "Waktu: 12:34:56", "Penerima: KOPERASI CONTOH",
         "Rekening tujuan: 0012345678", "Bank tujuan: BNI", "No. Referensi: DEMO-20260930-1"]
clean = receipt(lines)
truth = {"amount": "1234567.89", "currency": "IDR", "transfer_date": "2026-09-30", "transfer_time": "12:34:56",
         "recipient": "KOPERASI CONTOH", "account": "0012345678", "bank": "BNI", "reference": "DEMO-20260930-1"}
cases = []


def save(name, image, expected, **options):
    path = args.output / name
    image.save(path, **options)
    cases.append({"id": path.stem, "image": name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "expected": expected})


if args.supplier_only:
    supplier_lines = ["Nomor invoice: SYNTH-INVOICE-001", "Tanggal invoice: 03/10/2026", "Nama pemasok: PEMASOK CONTOH",
                      "Total tagihan: Rp 36.000,00", "Nomor PO: SYNTH-PO-001", "Nomor surat jalan: SYNTH-SJ-001",
                      "Kode barang pemasok: PROC-PENCIL-12", "Pensil: 3 dus, isi 12, harga Rp 12.000,00"]
    save("supplier-invoice.png", receipt(supplier_lines, "SYNTHETIC TEST - BUKAN INVOICE ASLI"),
         {"document_reference": "SYNTH-INVOICE-001", "document_date": "2026-10-03", "supplier_name": "PEMASOK CONTOH",
          "total_amount": "36000.00", "currency": "IDR", "purchase_order_reference": "SYNTH-PO-001",
          "delivery_reference": "SYNTH-SJ-001", "source_sku": "PROC-PENCIL-12"})
    owner_lines = ["Nomor invoice: SYNTHETIC-INVOICE-0001", "Tanggal invoice: 03/10/2026", "Nama pemasok: PEMASOK CONTOH",
                   "Total tagihan: Rp 34.000,00", "Nomor surat jalan: SYNTHETIC-SUPPLIER-DELIVERY-001",
                   "Kode barang pemasok: 000012", "Pensil: 3 dus; harga satuan Rp 10.000,00",
                   "PPN: Rp 3.300,00; biaya pemasok: Rp 700,00"]
    save("supplier-owner-invoice.png", receipt(owner_lines, "SYNTHETIC TEST - BUKAN INVOICE ASLI"),
         {"document_reference": "SYNTHETIC-INVOICE-0001", "document_date": "2026-10-03", "supplier_name": "PEMASOK CONTOH",
          "total_amount": "34000.00", "currency": "IDR", "purchase_order_reference": None,
          "delivery_reference": "SYNTHETIC-SUPPLIER-DELIVERY-001", "source_sku": "000012"})
    (args.output / "manifest.json").write_text(json.dumps({"schemaVersion": 1, "synthetic": True, "heldOut": False,
        "fontSha256": hashlib.sha256(args.font.read_bytes()).hexdigest(), "cases": cases}, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"syntheticFixtures": len(cases), "productionAcceptance": False}))
    raise SystemExit(0)

save("payment.png", clean, truth)
save("blurred.png", clean.filter(ImageFilter.GaussianBlur(1.5)), truth)
save("rotated.png", clean.rotate(8, expand=False, fillcolor="white"), truth)
save("compressed.jpg", clean, truth, quality=35)
save("no-amount.png", receipt(["Saldo: Rp 9.999.999,00", "Biaya: Rp 2.500,00", "Penerima: KOPERASI CONTOH"]),
     {**{field: None for field in truth}, "recipient": "KOPERASI CONTOH"})
save("conflicting-amount.png", receipt(["Nominal: Rp 100,00", "Nominal: Rp 200,00"]), {field: None for field in truth})
save("uncommon-amount.png", receipt(["Nominal: Rp 987.654.321,07"]),
     {**{field: None for field in truth}, "amount": "987654321.07", "currency": "IDR"})
save("blank.png", Image.new("RGB", (1000, 650), "white"), {field: None for field in truth})
(args.output / "manifest.json").write_text(json.dumps({"schemaVersion": 1, "synthetic": True, "heldOut": False,
    "fontSha256": hashlib.sha256(args.font.read_bytes()).hexdigest(), "cases": cases}, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"syntheticFixtures": len(cases), "productionAcceptance": False}))
