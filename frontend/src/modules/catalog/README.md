# Catalog authoring interface

This module owns the staff editor at /administration/catalog. It consumes the generated API contract and server-provided permitted owners; directory lists and browser role names never establish catalog authority.

Design: reuse administration teal #087f8c, ink #1d3640, white #ffffff, background #f2f6f7, border #d7e3e7 and orange #ed7829 focus. Segoe UI carries controls and body copy; Trebuchet MS headings match the existing staff workspace. Left-aligned, 16px body, explicit 44px controls; no decorative statistics.

The owner selector precedes an owner-scoped family list and working editor. The editor keeps family identity and category above a list of explicit packaging variants. Mobile stacks the family list above the editor. Package contents receive the strongest visual emphasis because confusing a sealed package with its contents causes operational mistakes. Every new variant starts empty; neither a package label nor a code supplies a quantity.

A saved family and each saved variant are separate durable outcomes. A failed second variant cannot imply the first was rolled back. Cosmetic definition changes preserve prior definitions; a material change explicitly creates a replacement SKU with a new code while the old identity remains. Retirement and publication require a visible review of affected assortments. Publication never happens automatically.

Owner changes discard the previous editor selection and cancel private reads; existing families lock their immutable owner. IDs and revisions remain exact decimal strings. Unknown outcomes retain one immutable request/key in memory and only actor/operation/key/time in persistent recovery storage. Catalog recovery has lookup only; no person or access closure fallback.

Category/family deactivation, eligible images, prices and stock promises are outside this authoring slice. An empty assortment list leaves saved drafts usable. No demo products, inferred units or buyer access are created.

CAT01.2 frontend acceptance: 33 dedicated package, response, transport and recovery tests; full suite 453/453 plus 6 future-date repository and 7 legacy golden cases passed. The 11 actual-browser checks use clearly synthetic API/identity fixtures: owner isolation, category/family/variant persistence across reload, explicit 12/24 contents, revision/replacement, publication/retirement, same-key uncertain retry, body-free lookup-only recovery, stale owner and actor/permission failures, keyboard and mobile. Production build and generated-contract type checks passed. Browser evidence lives in ignored tmp/production/catalog-authoring-results; it is not evidence of a live provider or database integration. Root owns backend acceptance and production activation.
