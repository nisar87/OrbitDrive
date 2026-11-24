# 100-point rubric: honest self-assessment (before owner review)

The assignment targets 95 or more, "justified with evidence". The honest score is **below** that, and the reasons are listed so nothing is hidden. Scores are my judgement, not an independent review.

| Area (points) | Score | Evidence | Gap |
|---|---|---|---|
| Research and positioning (10) | 8 | `COMPETITIVE_REVIEW.md`, eight sites, lessons and honest-claims rules | Text extraction only: no colour or type benchmarks, no visual pass |
| Logo and identity (15) | 11 | Three rendered concepts, comparison at 16 to 256 px, greyscale, dark, mono; kit with SVG, PNG, ICO, PWA, social; clear space and sizes | No trademark search; one designer's judgement; no owner feedback yet; mark lacks the "orbit" idea |
| Design system and guide (10) | 8 | `tokens.json/css`, computed contrast, voice rules | No component library beyond the site CSS; danger colour unchecked |
| Website design and content (20) | 15 | Full homepage with all sections in the brief, labelled simulation, honest security section, transfer diagram | Not every storyboard story is illustrated; no tour of Inbox or Send to my phone with real screens |
| Authentic screenshots (10) | 6 | Reproducible capture, demo account, 22 real shots, manifest | Windows shots are 1x CI test captures; phone shots are a phone-sized browser, not a phone; icon view has no thumbnails; storyboard items missing (Inbox, Properties, QR, Explorer Send to) |
| Interaction, accessibility, performance (15) | 12 | axe WCAG 2.2 A/AA clean, keyboard tabs and dialog, reduced motion, no third-party requests, LCP 164 ms desktop and 832 ms throttled phone in Chrome, CLS 0 | No Lighthouse run; no Safari/Firefox or screen-reader pass; no real phone |
| Security and abuse (10) | 8 | `SECURITY_AUDIT.md`, existing tests mapped, site CSP | No live abuse run; App Check left to owner |
| Process and safety (10) | 9 | Separate branch, preview only, no deploy, DNS or paid resources, product untouched, tests and a site workflow | CI result for the final SHA still to be confirmed (see DELIVERY.md) |
| **Total (100)** | **77** | | Target 95 is **not** claimed |

## To reach 95
Owner feedback on identity and a trademark check; curated high-resolution Windows captures from a real PC (including Send to my phone); a real-phone pass; Lighthouse and Safari/Firefox/screen-reader checks; remaining storyboard screens; one more design iteration.
