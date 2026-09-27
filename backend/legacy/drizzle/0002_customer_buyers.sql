-- Applied by Client.migrate(): table rebuilds and the migration marker commit atomically.
-- Existing JSON payloads stay byte-for-byte intact, absent customerId means null.
CREATE TABLE orders_v7 (
  id TEXT PRIMARY KEY NOT NULL, payload TEXT NOT NULL,
  division_id TEXT REFERENCES divisions(id), customer_id TEXT REFERENCES users(id),
  number TEXT NOT NULL,
  CONSTRAINT orders_one_buyer CHECK ((division_id IS NOT NULL) + (customer_id IS NOT NULL) = 1)
);
INSERT INTO orders_v7(id,payload,division_id,customer_id,number) SELECT id,payload,division_id,NULL,number FROM orders;
DROP TABLE orders;
ALTER TABLE orders_v7 RENAME TO orders;
CREATE UNIQUE INDEX orders_number_unique ON orders(number);

CREATE TABLE invoices_v7 (
  id TEXT PRIMARY KEY NOT NULL, payload TEXT NOT NULL,
  division_id TEXT REFERENCES divisions(id), customer_id TEXT REFERENCES users(id),
  number TEXT NOT NULL,
  CONSTRAINT invoices_one_buyer CHECK ((division_id IS NOT NULL) + (customer_id IS NOT NULL) = 1)
);
INSERT INTO invoices_v7(id,payload,division_id,customer_id,number) SELECT id,payload,division_id,NULL,number FROM invoices;
DROP TABLE invoices;
ALTER TABLE invoices_v7 RENAME TO invoices;
CREATE UNIQUE INDEX invoices_number_unique ON invoices(number);

CREATE TABLE complaints_v7 (
  id TEXT PRIMARY KEY NOT NULL, payload TEXT NOT NULL,
  shipment_id TEXT NOT NULL REFERENCES shipments(id),
  shipment_line_id TEXT NOT NULL REFERENCES shipment_lines(id),
  division_id TEXT REFERENCES divisions(id), customer_id TEXT REFERENCES users(id),
  CONSTRAINT complaints_one_buyer CHECK ((division_id IS NOT NULL) + (customer_id IS NOT NULL) = 1)
);
INSERT INTO complaints_v7(id,payload,shipment_id,shipment_line_id,division_id,customer_id)
  SELECT id,payload,shipment_id,shipment_line_id,division_id,NULL FROM complaints;
DROP TABLE complaints;
ALTER TABLE complaints_v7 RENAME TO complaints;

CREATE TABLE payments_v7 (
  id TEXT PRIMARY KEY NOT NULL, payload TEXT NOT NULL,
  division_id TEXT REFERENCES divisions(id), customer_id TEXT REFERENCES users(id),
  invoice_id TEXT REFERENCES invoices(id),
  CONSTRAINT payments_one_buyer_at_most CHECK ((division_id IS NOT NULL) + (customer_id IS NOT NULL) <= 1)
);
INSERT INTO payments_v7(id,payload,division_id,customer_id,invoice_id) SELECT id,payload,division_id,NULL,NULL FROM payments;
DROP TABLE payments;
ALTER TABLE payments_v7 RENAME TO payments;

CREATE TABLE attachments_v7 (
  id TEXT PRIMARY KEY NOT NULL, payload TEXT NOT NULL,
  owner_id TEXT NOT NULL REFERENCES users(id),
  division_id TEXT REFERENCES divisions(id), customer_id TEXT REFERENCES users(id),
  shipment_id TEXT REFERENCES shipments(id),
  CONSTRAINT attachments_one_buyer_at_most CHECK ((division_id IS NOT NULL) + (customer_id IS NOT NULL) <= 1)
);
INSERT INTO attachments_v7(id,payload,owner_id,division_id,customer_id,shipment_id)
  SELECT id,payload,owner_id,division_id,NULL,shipment_id FROM attachments;
DROP TABLE attachments;
ALTER TABLE attachments_v7 RENAME TO attachments;

-- A failed CHECK rolls back the entire migration if any legacy relation is invalid.
INSERT INTO mutation_guards(id,valid)
  SELECT 'migration-0002-foreign-keys',0 FROM pragma_foreign_key_check LIMIT 1;
