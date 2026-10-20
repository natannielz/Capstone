/** The checkout's delivery form: what the buyer types, checked field by field before it is saved into the draft.
 *  The rules match the draft's own parser, so a form that passes here is never refused for its shape. */
export type DeliveryFields = { recipientName: string; phone: string; address: string; city: string; postalCode: string; instructions: string; purchaseReference: string };
export type DeliveryField = keyof DeliveryFields;
export type OrderDelivery = { recipientName: string; phone: string; address: string; city: string; postalCode: string | null; instructions: string | null };
export const emptyDelivery: DeliveryFields = { recipientName: '', phone: '', address: '', city: '', postalCode: '', instructions: '', purchaseReference: '' };

const singleLine = /[\u0000-\u001f\u007f]/, multiLine = /[\u0000-\u0009\u000b-\u001f\u007f]/;

export function checkDelivery(fields: DeliveryFields): { delivery: OrderDelivery; purchaseReference: string | null } | { errors: Partial<Record<DeliveryField, string>> } {
  const value = (field: DeliveryField) => fields[field].trim();
  const errors: Partial<Record<DeliveryField, string>> = {};
  const required = (field: DeliveryField, label: string, limit: number, multiline = false) => {
    const text = value(field);
    if (!text) errors[field] = label + ' wajib diisi.';
    else if (text.length > limit) errors[field] = label + ' paling banyak ' + limit + ' karakter.';
    else if ((multiline ? multiLine : singleLine).test(text)) errors[field] = label + ' berisi karakter yang tidak dapat disimpan.';
  };
  required('recipientName', 'Nama penerima', 160);
  required('address', 'Alamat', 1000, true);
  required('city', 'Kota', 120);
  const phone = value('phone'), digits = phone.replace(/[^0-9]/g, '').length;
  if (!phone) errors.phone = 'Nomor telepon wajib diisi.';
  else if (phone.length > 32 || !/^\+?[0-9][0-9 ()-]*$/.test(phone) || digits < 7 || digits > 15) errors.phone = 'Tulis nomor telepon dengan 7 sampai 15 angka, misalnya 0812-3456-7890.';
  if (value('postalCode') && !/^[0-9]{5}$/.test(value('postalCode'))) errors.postalCode = 'Kode pos terdiri dari 5 angka.';
  if (value('instructions').length > 1000 || multiLine.test(value('instructions'))) errors.instructions = 'Catatan paling banyak 1000 karakter.';
  if (value('purchaseReference').length > 160 || singleLine.test(value('purchaseReference'))) errors.purchaseReference = 'Referensi paling banyak 160 karakter.';
  if (Object.keys(errors).length > 0) return { errors };
  return { delivery: { recipientName: value('recipientName'), phone, address: value('address'), city: value('city'),
    postalCode: value('postalCode') || null, instructions: value('instructions') || null }, purchaseReference: value('purchaseReference') || null };
}

export function deliveryFields(delivery: OrderDelivery | null, purchaseReference: string | null): DeliveryFields {
  return delivery === null ? { ...emptyDelivery, purchaseReference: purchaseReference ?? '' } : { recipientName: delivery.recipientName, phone: delivery.phone,
    address: delivery.address, city: delivery.city, postalCode: delivery.postalCode ?? '', instructions: delivery.instructions ?? '', purchaseReference: purchaseReference ?? '' };
}

/** Fills the address from the branch address the organisation approved. Who receives it and their phone stay the buyer's to write. */
export function deliveryFromApproved(fields: DeliveryFields, approved: { line1: string; line2: string | null; city: string; postalCode: string | null; region?: string | null; countryCode?: string }): DeliveryFields {
  return { ...fields, address: [approved.line1, approved.line2].filter(Boolean).join('\n'), city: approved.city,
    postalCode: approved.postalCode && /^[0-9]{5}$/.test(approved.postalCode) ? approved.postalCode : '' };
}
