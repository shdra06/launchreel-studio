# What makes an app film look expensive, and how Studio does it

Notes behind the FlyShelf launch films (a dark and a bright version) and the Studio features they use.

## What the references do

From the FlyShelf promo video. Its text and UI were AI-generated and garbled, but its look is the target:

- **Shallow depth of field.** The world behind the product is always soft, and only the product is sharp. This is the single biggest difference from flat CG.
- **Out-of-focus light motes** (bokeh) drift through the background.
- **A glass slab with real thickness** and a lit rim, held at bold three-quarter angles.
- **An end card:** a frosted glass card with the logo on a vivid gradient.

From launch-film practice (sources below):

- **Shot structure:** open on the problem, reveal the product (hero), then the details in close-up, then the product in context, then the brand.
- **One idea per shot**, with generous space and big type.
- **Raw screen recordings get unreadable on phones**, so zoom into the control that does the thing instead of showing a whole window.
- **Eased, overlapping motion** cut to the music.

## How Studio does it

| Look | Studio feature |
| --- | --- |
| Shallow focus | **Depth of field** (shot setting, 0–1; also "bokeh" / "sharp background" commands). A band around the app's own depth always stays sharp, so its text never softens, even turned or tilted. Glows and shadows carry no depth, so the background behind them blurs too. |
| Bokeh | Light motes are added automatically when depth of field is on. They are seeded per shot, so the preview and the export match, and they never pass in front of the app. |
| Lit glass | The glass panel has a fine edge highlight that fades into the accent colour. The white finish is a thick frosted slab. |
| Clean product | **Clean glass tint** (0–1). Windows acrylic and mica show the wallpaper through an app, which reads as a muddy cast in a designed scene. This setting only neutralises dark, muted background pixels, so icons, photos, text and accents keep their colours. Use it only for translucent apps. |
| Exact cutout | Frames and sections snap to the app's own window borders. Cutouts are rounded like Windows 11 windows (8 px). A pop-up and its app are layered as separate cutouts. |
| Feature close-ups | **Zoom** highlights fly the camera to one control. A zoom never magnifies beyond 2.2 screen pixels per recorded pixel, so close-ups stay crisp. |
| Structure | The *Recipes* (one click: launch film, promo short, feature tour) and the FlyShelf plan follow problem, reveal, features, brand, on a 120 BPM bar grid. |

## The FlyShelf film

`.studio-state\ad\build\make_plan.py` (set `LOOK=light` for the bright version):

1. Problem card.
2. Summon (swoop).
3. Hover actions, with a close-up on the folder row's real action buttons.
4. History scroll.
5. Card: "Preview anything."
6. Markdown QuickLook, with the real shelf layered in front and a zoom on the rendered heading.
7. Image QuickLook, with a close-up on Copy / Rotate / Compress / Tools / Draw.
8. Instant search.
9. Hub sections.
10. Frosted glass end card with the real app icon (`FlyShelf_Android\assets\images\icon.png`, copied, not modified).

Everything on screen is real FlyShelf footage, recorded from the app itself.

## Review round 2 (v3 → v5)

What was wrong with v3, judged frame by frame (one frame per second):
- **The product was small.** The clipboard shelf filled about 15% of the frame width in summon, history and search. The background did most of the work.
- **Angles repeated.** Summon, history and search used nearly the same three-quarter view, which read as bland.
- **The background was garish.** Aurora mixed the accent with its complementary colour at high saturation, which competed with the app.

What changed:
- **Camera moves that keep the product big.** *feature*, *low arc*, *crane down*, *macro pass* and *pull-back reveal* hold the product at about 80% of the frame height, capped at 60% of the width beside a headline.
- **A calmer Aurora.** It uses analogous hues only, at lower saturation.
- **Two features from real Hub screenshots.**
  - PDF Studio, where the camera flies onto the Merge PDF card and the card lifts off.
  - Personalization, with the background styles outlined.
- **Rejected after review:**
  - A macro pass on the history, which was too close, so the footage went soft.
  - Pull-backs under a headline, where white text sat on white UI.

Fresh research, applied:
- **Real captured screens are back in favour** over drawn motion graphics (ADVIDS, 100 software UI videos, 2026). The whole film is captured footage.
- **Zoom on the exact control at the moment it matters,** not one fixed zoom level, and never overlap zooms (Screenhance, FloiK). Each feature shot has at most one zoom or outline.
- **Show the payoff early** (within 15–30 s), and **cut dead time** such as loading and slow cursor moves (Vibrantsnap). The shelf appears at 2 s.
- **Small desktop text is unreadable on phones,** so zoom into the UI (Focusee). This is why the shelf fills the frame.

## Still open

- PDF merging and image editing are shown as screens, not in use. They need new recordings, which the user is making.
- The recordings are 1080p, so the close-ups cap their zoom to stay sharp. A 4K or high-DPI take would allow tighter macro shots.
- The cinematic (Blender) renderer has real depth of field but does not apply the clean tint yet; export refuses that combination and says so.

## Sources

- [ADVIDS: UI animation and motion design trends, 2026](https://advids.co/blog/ui-animation-motion-design-trends)
- [Screenhance: software demo video maker](https://screenhance.com/software-demo-video-maker)
- [FloiK: drawing attention with zoom](https://www.floik.com/help-article/how-to-draw-attention-to-key-details-using-zoom)
- [Vibrantsnap: create demos people actually watch](https://vibrantsnap.vercel.app/blog/create-engaging-product-demos)
- [Focusee: SaaS marketing video](https://focusee.imobie.com/video-marketing/saas-marketing-video.htm)

- [motion.so: Apple-style product launch video](https://motion.so/learn/apple-style-product-launch-video)
- [Manfrotto: How to shoot cinematic tech videos like Apple](https://www.manfrotto.com/global-en/stories/how-to-shoot-cinematic-tech-videos-like-apple/)
- [Hatch Studios: SaaS video animation best practices](https://hatchstudios.com/best-practices-for-creating-engaging-saas-product-videos/)
- [Hera: B2B SaaS feature announcement video guide](https://hera.video/blog/b2b-saas-feature-announcement-video-guide)
