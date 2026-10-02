# Initial accounting book setup

Use `/accounting/books` to propose a new IDR book for an explicitly selected organization, shop and cashier channel. Implementation and environment acceptance are tracked in [the production ledger](../quality/production-progress.md); this runbook is not evidence of a deployed or approved book.

## Operator inputs

- Select the actual named shop/channel within the operator's approved scope. A warehouse assignment does not confer authority over a shop-wide book.
- Enter the first accounting month explicitly. Do not infer it from today's date, the first imported document or a demo record.
- Enter every intended posting mapping and account code from the approved accounting design. The current canonical code format begins with a letter and permits letters, digits, periods, underscores and hyphens, up to 64 characters. Do not silently rewrite external ledger codes; resolve any required external-to-canonical mapping with Accounting before operational use.
- Provide a reason and review the complete mapping, scope and month. No account, mapping, opening balance or financial policy is selected automatically.

The application allocates internal account identities after approval. Different mapping keys may deliberately refer to the same account code; they must resolve to the same account identity within this initial chart. Operators do not type internal database IDs.

## Independent decision

Explicit `accounting.book.read`, `accounting.book.propose` and `accounting.book.approve` grants are separate. Setup uses an exact fulfillment-shop scope; ordinary finance membership, warehouse authority, system administration or journal posting does not imply a setup grant. Proposal and decision require current MFA within five minutes. Approval and rejection require a different, currently verified canonical person from the proposer, with current scope and authority.

Approval creates the book, its initial chart and the selected first open period in one transaction with the immutable setup decision, audit, outbox and command receipt. It creates no opening balance, inventory quantity, journal entry, cash movement or payment. A missing or conflicting prerequisite leaves none of these partial configuration effects committed.

This initial workflow cannot adopt an existing book, overwrite its chart, reopen a period or create a replacement book under the same canonical key. Existing configuration must be reconciled through its proper owner. Do not delete it to retry setup.

## Lost responses and later consumption

Keep the pending operation reference and use the original account's result lookup after a lost response or reload. The operation names are `accounting.book.propose`, `accounting.book.approve` and `accounting.book.reject`. Do not send another command merely because the response was lost. A different signed-in account cannot resolve or close another account's pending operation.

The existing journal owner separately validates the active book, reviewed account mappings, actual business source and open period before posting. Its legitimate updates to period revisions and book fences do not replace the immutable setup history. Configuring a book supplies one dependency; it does not approve an invoice, receiving account, stock source, opening inventory, tax policy or credit decision.

## Migration and release

Forward migration 60 installs the setup proposal/decision owner records and their integrity controls. It retains the existing migration-12 book, chart, journal and period contracts and seeds no financial configuration. Preserve historical records, stronger validators and all original migration receipts.

Actual Accounting-approved mappings, opening month and balances, operator grants, source-control inputs and provider/deployment acceptance remain required before real use. Opening inventory and balances require their own reviewed source and reconciliation. The three-person submit/approve/close policy, reopening controls and real branch pilot remain separate release requirements.
