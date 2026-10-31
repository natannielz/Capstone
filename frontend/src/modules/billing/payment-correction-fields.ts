// Leaf module: zod 4 reads enum values when a schema is built, so this must not sit on an import cycle.
export const correctionFields = ['amount', 'currency', 'transfer_date', 'transfer_time', 'recipient', 'account', 'bank', 'reference'] as const;
