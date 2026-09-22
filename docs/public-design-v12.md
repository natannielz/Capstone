# Public landing and sign-in design v12

Reviewed 23 September 2026. Scope: public landing, customer sign-in and staff sign-in. The storefront carousel and catalogue changes have their own implementation notes.

## What needed correction

The previous landing used a tall arched photo, two rotated cards over that photo, a moving category band and stepped collection cards. These competed for attention and made clipping or awkward overlaps more likely. The login added three overlapping product tiles on top of a photograph that also carried the page heading. The number of independent effects did not explain the page or help someone sign in.

The revised direction is a photographic catalogue for a working-day shop: pantry, meetings and team merchandise. Its job is to introduce those choices and distinguish customer shopping from the internal workspace. It is not an award claim or a copy of a showcase site.

## Visual plan

Palette: petroleum `#073B45`, ink `#143F43`, white `#FFFFFF`, pale green `#EDF5F3`, secondary green `#25726F`, border `#DCE6E5`. Existing Archivo Black is retained for the landing headline; DM Sans carries all explanatory and functional text. Headline tracking changes from approximately -5% to -2.8% with more comfortable line height.

```
LANDING DESKTOP
brand                  sections                 choose access
headline + description       large product photograph
primary / secondary action   caption in its own row
access explanation           [pantry link] [merchandise link]
[pantry category] [meeting category] [merchandise category]
customer access              internal access
three aligned collection cards
handoff photograph           service explanation

LOGIN DESKTOP
back navigation              brand
photograph                   heading + guidance
                             email / password / submit
solid petroleum story        account switch / help
                             demo note
```

The signature is the product contact sheet beneath the landing photograph. Its two cards lead to actual collections; their images, labels and arrows sit in normal flow. Compared with another decorative floating-card layout, this gives the same photography a useful destination and predictable space. On small screens the category links become three complete rows instead of a cropped moving band. Collection cards share one baseline.

Login separates the photo and copy into distinct blocks. The back link remains in normal flow above them. On mobile the photo and short heading use adjacent columns, leaving the form as a readable full-width section. No product tiles, absolute-positioned story text, or photo gradients are needed to make the copy legible.

## Motion decisions and research

The primary reference for responsive motion and cleanup is [GSAP matchMedia documentation](https://gsap.com/docs/v3/GSAP/gsap.matchMedia/). It describes automatic context cleanup and conditional setup for reduced motion. The implementation uses a scoped matchMedia context inside useGSAP, then reverts it on unmount. Event handlers operate on already-created timelines; they do not invoke the parent contextSafe wrapper from a child context. Image-load and font-ready refresh listeners are removed or guarded during cleanup.

[web.dev's animation performance guide](https://web.dev/articles/animations-guide) recommends favouring transform and opacity over properties that repeatedly change layout or paint. The new motion uses a short headline entrance, a subtle photograph scale and single section reveals. The photograph is clipped by its own static frame; the caption and adjacent links do not scale. There is no animated height, viewport tilt, oversized horizontal translation or scroll hijacking.

[W3C's guidance on animation from interactions](https://www.w3.org/WAI/WCAG22/Understanding/animation-from-interactions.html) supports respecting reduced-motion preferences for nonessential motion. The application skips the GSAP sequences when reduced motion is requested and removes relevant CSS transforms. Keyboard focus immediately completes entrance animations. Login controls, its header, navigation, and landing calls to action are not animated into different positions.

Timing: headline lines start 90 ms apart and settle in 750 ms; the hero photograph settles in 1.2 s; section text reveals use 650 ms with 55 ms stagger. These are design choices, not numerical accessibility requirements. Hover feedback changes a border or moves a directional arrow a few pixels. Nothing in these public pages loops automatically.

## Validation

- Focused ESLint passed for `public-landing.tsx`, `login.tsx` and `public-motion.tsx`.
- Desktop browser inspection confirmed separate photo, caption, product-link and copy regions on the landing; document width matched viewport width (1265 px).
- Desktop customer login inspection confirmed visible navigation, form, help and solid-background story without an overlapping image layer.
- Authentication state, validation, role destinations and request handling were not changed.
- Cross-page mobile, navigation and release checks are recorded in the overall v12 release report; this document does not claim those checks before they are performed.
