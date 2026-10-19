/**
 * Checks before a payment proof is uploaded. The original bytes are the evidence (the server keeps them as received),
 * so nothing is compressed or converted here; the person instead learns exactly why a file cannot be used.
 */
export type ProofFileProblem = "empty" | "too_large" | "heic" | "type" | "too_small";

export const PROOF_MAX_BYTES = 10 * 1024 * 1024;
export const PROOF_MIN_SIDE = 500;

export function proofFileProblem(file: { name: string; type: string; size: number }): ProofFileProblem | null {
  if (/^image\/hei[cf]$/i.test(file.type) || /\.hei[cf]$/i.test(file.name)) return "heic";
  if (!["image/jpeg", "image/png"].includes(file.type)) return "type";
  if (file.size <= 0) return "empty";
  if (file.size > PROOF_MAX_BYTES) return "too_large";
  return null;
}

/** Below 500 px on either side the amount, date and reference may not be readable by a reviewer or OCR. Advisory only:
 * the server decides what evidence it accepts, so the client never refuses a file the server would take. */
export function proofDimensionProblem(width: number, height: number): ProofFileProblem | null {
  return Math.min(width, height) < PROOF_MIN_SIDE ? "too_small" : null;
}

export function proofFileMessage(problem: ProofFileProblem): string {
  switch (problem) {
    case "heic": return "Format HEIC dari kamera iPhone belum dapat diperiksa. Kirim tangkapan layar bukti transfer dari aplikasi bank (JPG atau PNG).";
    case "type": return "Gunakan gambar JPG atau PNG, misalnya tangkapan layar bukti transfer.";
    case "empty": return "Berkas kosong. Pilih ulang bukti transfer.";
    case "too_large": return "Ukuran gambar lebih dari 10 MB. Kirim tangkapan layar bukti transfer, bukan foto beresolusi penuh.";
    case "too_small": return "Gambar ini kecil (sisi terpendek kurang dari 500 piksel) sehingga mungkin sulit dibaca. Jika ada, kirim tangkapan layar asli tanpa dipotong atau diperkecil.";
  }
}
