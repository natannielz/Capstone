import { test } from "node:test";
import assert from "node:assert/strict";
import { dateFormatter, dateTimeFormatter, formatDate, formatDateTime, formatMoney, formatMoneyText, formatMonth, formatDecimal, formatNumber, formatPercent, formatRupiah, formatTime, jakartaDateKey, numberFormatter } from "../src/lib/format";

// EYD V: "Rp1.250.000" (no space after Rp), dot thousands, comma decimals; times "14.30 WIB".
test("formatRupiah writes EYD V rupiah without a space and with explicit fraction digits", () => {
  assert.equal(formatRupiah(1250000), "Rp1.250.000");
  assert.equal(formatRupiah(0), "Rp0");
  assert.equal(formatRupiah(-25000), "-Rp25.000");
  assert.equal(formatRupiah("1250000.5", { decimals: 2 }), "Rp1.250.000,50");
  assert.equal(formatRupiah(1234567.5), "Rp1.234.568");
});

test("formatRupiah accepts decimal strings exactly", () => {
  assert.equal(formatRupiah("189000.00"), "Rp189.000");
  assert.equal(formatRupiah("9007199254740993"), "Rp9.007.199.254.740.993");
  assert.equal(formatRupiah(BigInt("9007199254740993")), "Rp9.007.199.254.740.993");
  assert.equal(formatRupiah("-0.4"), "Rp0");
  assert.throws(() => formatRupiah("12a"), /angka/);
});

test("formatMoney renders server decimal strings: IDR as EYD V rupiah, other currencies by code", () => {
  assert.equal(formatMoney("IDR", "12000.25"), "Rp12.000,25");
  // U-017: rupiah drops ",00" only when the fraction is zero (after rounding); a real fraction keeps two decimals.
  assert.equal(formatMoney("IDR", "0.00"), "Rp0");
  assert.equal(formatMoney("IDR", "1250000.00"), "Rp1.250.000");
  assert.equal(formatMoney("IDR", "561120"), "Rp561.120");
  assert.equal(formatMoney("IDR", "12000.004"), "Rp12.000");
  assert.equal(formatMoney("IDR", "12000.5"), "Rp12.000,50");
  assert.equal(formatMoney("IDR", "-25000.00"), "-Rp25.000");
  assert.equal(formatMoney("IDR", "-999999999999999.99"), "-Rp999.999.999.999.999,99");
  assert.equal(formatMoney("IDR", "1250000.40", 0), "Rp1.250.000", "explicit whole rupiah still rounds");
  assert.equal(formatMoney("USD", "1500.5"), "USD 1.500,50");
  assert.equal(formatMoney("USD", "1500.00"), "USD 1.500,00", "other currencies keep their decimals");
  // Formal documents (invoice and receipt renderers) keep formatRupiah's fixed decimals.
  assert.equal(formatRupiah("1250000.00", { decimals: 2 }), "Rp1.250.000,00");
});

test("numbers and percentages use Indonesian separators", () => {
  assert.equal(formatNumber(1234567), "1.234.567");
  assert.equal(formatNumber("2.5", 1), "2,5");
  assert.equal(formatPercent(0.873, 1), "87,3%");
  assert.equal(formatPercent(1), "100%");
});

test("dates and times render in Asia/Jakarta with WIB", () => {
  assert.equal(formatDate("2026-10-17T07:30:00Z"), "17 Okt 2026");
  assert.equal(formatDate("2026-10-17T07:30:00Z", "long"), "17 Oktober 2026");
  assert.equal(formatDate("2026-10-17T07:30:00Z", "full"), "Sabtu, 17 Oktober 2026");
  assert.equal(formatDate(Date.UTC(2026, 9, 17, 7, 30)), "17 Okt 2026");
  assert.equal(formatMonth("2026-10-17T07:30:00Z"), "Oktober 2026");
  assert.equal(jakartaDateKey("2026-10-16T18:30:00Z"), "2026-10-17");
  assert.equal(formatDateTime("2026-10-17T07:30:05Z", { seconds: true }), "17 Okt 2026 14.30.05 WIB");
  assert.equal(dateTimeFormatter.format(new Date("2026-10-17T07:30:00Z")), "17 Okt 2026 14.30 WIB");
  assert.equal(dateFormatter.format(new Date("2026-10-17T07:30:00Z")), "17 Okt 2026");
  assert.equal(formatNumber(BigInt(1234567)), "1.234.567");
  assert.equal(numberFormatter.format(9876), "9.876");
  assert.equal(formatDate("2026-10-16T18:30:00Z"), "17 Okt 2026");
  assert.equal(formatDateTime("2026-10-17T07:30:00Z"), "17 Okt 2026 14.30 WIB");
  assert.equal(formatTime("2026-10-17T07:30:00Z"), "14.30 WIB");
});

test("formatted output never contains no-break spaces that break EYD V or copy/paste", () => {
  for (const value of [formatRupiah(1250000), formatRupiah("1.5", { decimals: 2 }), formatDate(new Date()), formatDateTime(new Date()), formatTime(new Date()), formatPercent(0.5)])
    assert.equal(/[  ]/.test(value), false, JSON.stringify(value));
});

test("formatMoneyText formats valid amounts and shows text still being typed as entered, never throwing", () => {
  assert.equal(formatMoneyText("7000.00"), "Rp7.000");
  assert.equal(formatMoneyText("1250000"), "Rp1.250.000");
  assert.equal(formatMoneyText("1250000.5"), "Rp1.250.000,50");
  assert.equal(formatMoneyText(" 12. "), "Rp12.");
  assert.equal(formatMoneyText(""), "Rp—");
});

test("formatDecimal keeps up to the given decimals without trailing zeros", () => {
  assert.equal(formatDecimal(1.5, 3), "1,5");
  assert.equal(formatDecimal(0.5, 3), "0,5");
  assert.equal(formatDecimal(2, 3), "2");
  assert.equal(formatDecimal(1.2345, 3), "1,235");
  assert.equal(formatDecimal(12500, 3), "12.500");
});
