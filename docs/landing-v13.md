# Landing v13: an editorial retail cover

23 September 2026. Scope: public landing presentation and its motion. Authentication, catalogue data, shopping state and role permissions are unchanged.

## Design direction

The landing introduces a shop for pantry supplies, meetings and team merchandise. It must also make the customer and internal entry points unambiguous. The previous two-column hero with small product cards was serviceable, but its structure still resembled a generic software landing page. This revision changes the composition, section rhythm and motion narrative together.

The signature is a large typographic cover over a wide product still-life, followed by three complete collection chapters. Pantry uses pale green, meetings a restrained warm paper colour, and merchandise deep petroleum. Each chapter has a real shop destination, a clear description and a large photograph. The alternating composition follows the change in collection; nothing is positioned over text or interactive controls.

Tokens:

- Petroleum `#073B45`, white `#FFFFFF`, green accent `#23796F`, pale green `#EAF3EF`, warm paper `#F5EDE2`, muted copy `#526C70`.
- Archivo Black for the single cover headline; DM Sans for chapter headings, body and utilities. Large type has explicit line height and wrapping rules down to 320 px.
- A shared outer grid, square photographic frames, thin rules, and a split-arrow action treatment. The arrow has its own fixed space; it does not displace the label.

```
brand                   collections / process / internal        access
small shop label
LARGE TWO-LINE TITLE                    introduction + entry links
WIDE PRODUCT STILL-LIFE
caption                                             collection link

collection introduction
pantry link                 meeting link                 merchandise link
pantry copy + shop action                         pantry photograph
meeting photograph                               meeting copy + action
merchandise copy + action                        merchandise photograph

shopping process: choose / prepare / receive / invoice
customer access card                             internal access card
footer and simulation disclosure
```

The alternative of another set of floating product cards was rejected: it would add decoration without changing the page's information hierarchy. The new photographic chapters make the three actual collections the core of the page. The cover includes direct customer and internal links, so the longer editorial content does not block entry.

## Choreography

The cover title rises into its line frames, followed by the brief introduction. A white curtain reveals the large photograph while the image settles into its frame. The curtain is decorative, ignores pointer events and is invisible in the static document; the page remains usable without animation.

While scrolling normally, desktop collection photographs move slightly within their own frames. Thin rules show progress through each chapter and the collection navigation highlights the chapter occupying the middle of the viewport. There is no pinned scene, artificial scroll distance, wheel interception, snapping or horizontal page translation. On smaller screens the photos receive one short scale entrance instead of continuous depth.

Collection copy enters in reading order. Buttons, their labels, navigation and account entry links retain their positions. Hover or keyboard focus on a primary action wipes a contrasting colour underneath the same text and moves its arrow by two pixels. There is one accessible label and one focus target per action.

Reduced motion disables the timelines, depth, progress animation and curtain. All text and photographs are visible. Keyboard focus completes entrance timelines immediately. Animation and event cleanup remain scoped to the page's useGSAP and matchMedia lifecycle; no parent contextSafe wrapper is called from the nested matchMedia context.

## Research used

[GSAP ScrollTrigger documentation](https://gsap.com/docs/v3/Plugins/ScrollTrigger/) documents scrub-linked progress, class changes and native scrolling. These capabilities inform the chapter rules, photographic depth and active collection styling. Pinning is available but intentionally unnecessary for this retail page.

[GSAP matchMedia documentation](https://gsap.com/docs/v3/GSAP/gsap.matchMedia/) supports responsive setup and automatic cleanup when media conditions change. The page uses different desktop/mobile treatments and a reduced-motion gate.

[web.dev's animation accessibility lesson](https://web.dev/learn/accessibility/motion) and [W3C technique C39](https://www.w3.org/WAI/WCAG22/Techniques/css/C39) inform the explicit reduced-motion alternative. Motion is supplemental; it never carries information that disappears when animation is disabled.

[web.dev's performance guidance](https://web.dev/articles/animations-guide) informs the use of transform and opacity. The reveal curtain uses scale rather than animated width; photographs keep fixed layout frames. No animation changes section height or positions the controls.

These sources establish technical and accessibility principles. The palette, composition and exact timing are design decisions for this shop, not requirements claimed from the references.

## Validation recorded by this implementation task

Focused ESLint passed for `public-landing.tsx` and `public-motion.tsx`. All new selectors are scoped beneath `home-v13`; the login branch and authentication component were not edited. Browser layout, interaction and release validation is performed by the coordinating task and recorded in the release report.
