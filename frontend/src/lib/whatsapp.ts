/**
 * wa.me link for contact or help. The number becomes digits-only with the 62 prefix; an unset or invalid number gives
 * null so no broken link is rendered. Message text must not carry amounts or identifiers (it leaves the app in clear text).
 */
export function whatsappLink(phone: string | undefined, text: string): string | null {
  if (/Rp|\bIDR\b|[0-9]{10,}/.test(text)) throw new Error("WhatsApp text must not contain amounts or identifiers");
  const digits = (phone ?? "").replace(/[\s()-]/g, "").replace(/^\+/, "");
  const national = digits.startsWith("62") ? digits : digits.startsWith("0") ? "62" + digits.slice(1) : "";
  if (!/^62[0-9]{8,13}$/.test(national)) return null;
  return `https://wa.me/${national}?text=${encodeURIComponent(text)}`;
}
