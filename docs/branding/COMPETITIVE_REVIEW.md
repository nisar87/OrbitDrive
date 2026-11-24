# MyOrbitDrive: competitive review (Phase A, part 2)

Reviewed 2026-11-24. Method: each page was read through a text-extraction tool, so **colours, typefaces, logo shapes and motion could not be observed** and are marked "not assessed". Layout, wording, section order and claims are paraphrased from the extracted text. Nothing here is copied, and claims about competitors are limited to what the pages said at the time. Treat this as input to design decisions, not as proof of superiority.

## 1. Folderbird (closest competitor)

Same job: reach files on your Windows PC from a phone or any browser, with no sync.

| Aspect | Finding |
|---|---|
| Hero | Opening your PC's files from a phone or any browser; free for two computers; nothing synced, files only pass through. Primary action is account creation, secondary is the Windows download, plus a "see how it works" anchor. "No credit card" sits beside the buttons. |
| Order | Hero with a file-browser mockup, three-step how-it-works, comparison with cloud storage, AI access, product tour (devices, folders, capabilities, search, terminal), security, Android app, closing CTA. A sticky on-page index runs alongside. |
| Imagery | Real app screenshots with desktop and phone variants and light/dark swapping; one illustrative mockup in the AI section. |
| Interaction | Capability tabs, anchor index, theme-aware images, cookie banner. |
| Trust | Specific and checkable claims: no third-party cloud copy, two-step verification, encrypted paths in URLs, scoped revocable API keys, audit log, code signing. |
| Weak points | Heavy jargon in the AI and security sections; an unexplained foundation attribution; iPhone not supported; a "never synced" privacy message beside a cookie banner that loads ad and analytics scripts; pricing hidden behind a link; repeated identical CTAs. |

**Lessons.** Real screenshots in tabs, a short three-step flow and specific security claims are the standard to meet. Do not copy the AI-first emphasis (OrbitDrive has no such feature), the jargon density or any ad-tracking contradiction: MyOrbitDrive should ship with no third-party trackers and say so only if it is true of the built page.

## 2. LocalSend
Open-source nearby sharing, no account. Hero is a short three-part promise (without cloud, fast, private). Strong points: no-account message, three-step flow, open-source and code-signing links, an FAQ. Weak points: the homepage shows no app screenshots and no per-platform download buttons; the "how it works" section reuses the logo instead of showing steps; unsourced statistics; security wording differs between the features list (end-to-end) and the FAQ (HTTPS), which is exactly the inconsistency MyOrbitDrive must avoid; hero alt text just repeats the headline. **Lesson:** show the steps visually and keep one precise security statement everywhere.

## 3. Tailscale
Platform positioning for IT and developers. Strong points: quantified customer outcomes with case-study links, a security page, status page and changelog in the footer, clear "free" entry. Weak points: a hero diluted across four audiences, an event banner above the navigation, repeated logo strips and testimonials, security asserted rather than demonstrated, no certifications shown. **Lesson:** one audience and one promise per page; link to a real status page (MyOrbitDrive has the relay status JSON and a documented monitor) rather than assert reliability.

## 4. Taildrop
Feature page. Strong points: short benefit-led copy, real menu-bar screenshots, a one-sentence setup. Weak points: the "no size limits" headline is qualified later in the FAQ (resume works only for a limited time); receive limitations on some platforms buried in the FAQ; "guarantees" with no detail; inconsistent audience figures. **Lesson:** put limitations next to the claim. MyOrbitDrive's honest limits (PC must be on and awake, browser downloads cannot be observed, no E2EE) belong in the main flow, not a footnote.

## 5. AnyDesk
Remote-desktop incumbent. The extracted homepage was thin: a category headline with a version announcement, a licence configurator, a customer-count claim, and no visible security or encryption statement or download selector. **Lesson:** a remote-access product that omits security wording loses trust; MyOrbitDrive's "read-only by default, approved folders only" must be above the fold. It must also never be positioned as remote control (that is out of scope and listed in `CLAUDE.md`).

## 6. RustDesk
Open-source remote support. Direct switching pitch against named rivals, a three-step self-hosting snippet, community scale numbers, a warning about its single official domain. Weak points: the install snippet pipes a remote script into a shell, CTA labels that do not match their destinations, mixed audiences, unsourced figures. **Lesson:** an "official domain" notice is a good anti-phishing habit (MyOrbitDrive could state its two real hostnames); never ask users to pipe scripts.

## 7. Dropbox
Mass-market storage suite. Strong points: benefit-led hero, clear sections for organise/share/find/security, real UI in places. Weak points: abstract hero that leads with AI rather than the core job, many repeated logos and CTAs, unsupported superlatives about encryption. **Lesson:** MyOrbitDrive is explicitly not a cloud-storage replacement and must say what it is in the first sentence.

## 8. Proton Drive
Privacy-led storage. Strongest area is security communication: the hero states what the product will not do, the security section has four pillars each linking to detail, and the FAQ covers breach scenarios. Weak points: promotional clutter beside the hero, CTA labels leading to the pricing page, jargon-heavy copy, missing image alt text. **Lesson:** lead with what the product will *not* do (store a permanent copy, edit your files by default) and link every claim to a plain-language explanation. Do not borrow the "end-to-end" vocabulary: the relay terminates TLS and MyOrbitDrive must say so.

## 9. Cross-site conclusions

1. **Hero:** state the job in one plain sentence, name the audience once, offer one primary and one secondary action. Several rivals dilute this.
2. **Proof:** real screenshots beat illustrations. Folderbird and RustDesk use them; LocalSend does not. MyOrbitDrive needs authentic, reproducible captures with a manifest.
3. **Security tone:** specific, checkable statements win; absolute words ("guarantees", "no limits") are a recurring weakness. Use "approved folders only", "read-only by default", "revoke any browser or PC", "HTTPS to the relay".
4. **Limits up front:** hiding constraints in an FAQ is a pattern to avoid.
5. **Friction:** a visible three-step flow and a "no credit card / no account on the PC beyond sign-in" style line reduce hesitation, but MyOrbitDrive may only say what is true of its own sign-in and pricing.
6. **Cleanliness:** no promotional banners, repeated logo strips, popups or tracking scripts.
7. **Accessibility:** descriptive alt text on every screenshot (several rivals fail here).

## 10. Where MyOrbitDrive can honestly differ (to be proven, not asserted)

- Per-folder approval with read-only default, plus pairing approved on the PC itself.
- A "how a transfer works" explanation that admits the relay processes data in transit.
- Documented, tested resumable transfers and an Inbox for phone uploads.
- A public-facing status/operations story backed by the existing monitor.

Any wording like "more private than X" is **not** to be used; no comparison to a named rival appears on the site without a dated, sourced basis.

## 11. Limits of this review
Text extraction only; visual identity of every site unassessed; one fetch per site; pages may change. A visual pass in the browser (screenshots of each hero) is recommended before the Phase C layout is frozen if the owner wants colour and type benchmarks.
