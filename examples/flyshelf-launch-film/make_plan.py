"""FlyShelf launch film plan: real footage of the shelf, cut on a 120 BPM bar grid (one bar = 2 s).

Structure follows launch-film practice: open on the problem, reveal the product, then one feature per shot with a
close-up on the control that does it, and end on the brand. Set LOOK=light for the bright atelier version.
"""
import json, os, sys

W, H = 1920, 1080
def frac(x, y, w, h):
    return [round(x / W, 4), round(y / H, 4), round(w / W, 4), round(h / H, 4)]

# Exact window rectangles in the recording (snapped to each window's own border), so no desktop shows at the edges.
SHELF = frac(20, 500, 450, 475)            # the floating clipboard shelf
MARKDOWN = frac(360, 51, 1200, 918)        # the markdown QuickLook window
IMAGE = frac(378, 174, 1239, 745)          # the image QuickLook window
MD_HEADING = [round(40 / 1200, 4), round(164 / 918, 4), round(1120 / 1200, 4), round(305 / 918, 4)]  # rendered heading and first paragraph, within MARKDOWN
# The real shelf in front of a QuickLook window: its own cutout, lifted off the window behind it.
SHELF_IN_FRONT = [dict(name='FlyShelf', rect=SHELF, motion='float')]

DARK = dict(accent='#6ea8ff', background='#0a0e1a', titleColor='#f4f6ff', stage='aurora', lighting='soft', lightIntensity=1.1, finish='graphite')
LIGHT = dict(accent='#4f7fe8', background='#e9eef5', titleColor='#141b29', stage='atelier', lighting='soft', lightIntensity=1.25, finish='white')
BRAND = LIGHT if os.environ.get('LOOK') == 'light' else DARK
END = dict(LIGHT, stage='pastel')                # the brand card is always the bright glass card, as in the promo
LENS = dict(depthOfField=0.75, cleanTint=0.9)     # a fast-lens background and the shelf's own neutral glass
FOLDER_ROW = [0.067, 0.333, 0.867, 0.141]        # the folder row with its hover actions (shown from 1.6 s), within SHELF
IMAGE_TOOLS = [0.53, 0.0, 0.36, 0.08]            # Copy / Rotate / Compress / Tools / Draw, within IMAGE
def shot(**fields):
    base = dict(BRAND, **LENS) if fields.get('device') != 'title' else dict(BRAND)
    base.update(fields)
    return base

shots = [
    shot(name='Open', device='title', duration=2, motion='still', title='Copied something\nyou need again?', subtitle='', titleLayout='center', titleScale=1.55, titleMotion='rise', transition='fade'),
    shot(name='Summon', device='panel', duration=4, assetId='summon', crop=SHELF, motion='swoop', easing='dramatic', title='Everything you copy.\nOne shortcut away.', subtitle='Alt + C', titleMotion='rise'),
    shot(name='Hover actions', device='panel', duration=3, assetId='hover', crop=SHELF, motion='push', easing='ease-out', title='Act on it,\nright where it is.', subtitle='Hover actions', titleLayout='bottom-left', turn=-14,
         highlights=[dict(rect=FOLDER_ROW, start=1.5, end=3.0, style='zoom')]),
    shot(name='History', device='panel', duration=3, assetId='scroll', crop=SHELF, motion='orbit', easing='smooth', title='Your whole history.\nA scroll away.', subtitle='Clipboard history'),
    shot(name='Preview card', device='title', duration=2, motion='still', title='Preview anything.\nInstantly.', subtitle='QuickLook · Space', titleLayout='center', titleScale=1.45, titleMotion='rise'),
    shot(name='Markdown', device='panel', duration=4, assetId='markdown', sourceStart=1.35, crop=MARKDOWN, motion='rise', easing='ease-out', title='Markdown,\nbeautifully rendered.', subtitle='Press Space', titleLayout='top-left',
         sections=SHELF_IN_FRONT, explode=0.35, highlights=[dict(rect=MD_HEADING, start=1.5, end=3.8, style='zoom')]),
    shot(name='Image tools', device='panel', duration=3, assetId='image', crop=IMAGE, motion='push', easing='ease-out', title='Rotate. Compress. Draw.', subtitle='Image QuickLook', turn=12,
         sections=SHELF_IN_FRONT, explode=0.35, highlights=[dict(rect=IMAGE_TOOLS, start=1.0, end=2.8, style='zoom')]),
    shot(name='Search', device='panel', duration=4, assetId='search', crop=SHELF, motion='dolly', easing='smooth', title='Find anything\nas you type.', subtitle='Instant search',
         highlights=[dict(rect=[0.03, 0.035, 0.94, 0.085], start=0.6, end=3.6, style='outline')]),
    shot(name='Hub', device='panel', duration=3, assetId='hub', motion='orbit', easing='smooth', explode=0.75, title='Everything,\nsorted for you.', subtitle='FlyShelf Hub', titleLayout='bottom-left',
         sections=[dict(name='Navigation', rect=[0.0, 0.11, 0.245, 0.87], motion='pop'), dict(name='Search', rect=[0.255, 0.055, 0.735, 0.07], motion='pop'), dict(name='Categories', rect=[0.34, 0.225, 0.565, 0.6], motion='pop')]),
    dict(END, name='End', device='panel', duration=4, assetId='logo', motion='push', easing='smooth', depthOfField=0.6, turn=8,
         title='FlyShelf', subtitle='Your clipboard, reimagined', titleLayout='bottom-left', titleScale=1.7, titleMotion='pop'),
]

plan = {
    'output': sys.argv[1] if len(sys.argv) > 1 else 'flyshelf-launch-film.mp4',
    'project': {
        'name': 'FlyShelf — Launch Film', 'width': 1920, 'height': 1080, 'fps': 30, 'audioVolume': 0.8, 'audioId': 'music',
        'assets': [dict(id=i, name=i, source=f'clips/{i}.mp4') for i in ['summon', 'hover', 'scroll', 'markdown', 'image', 'search']]
                  + [dict(id='hub', name='FlyShelf Hub', source='clips/hub.png'), dict(id='logo', name='FlyShelf logo', source='clips/logo_card.png'),
                     dict(id='music', name='Launch soundtrack', source='soundtrack.wav')],
    },
    'storyboard': {'version': 1, 'shots': shots},
}
if len(sys.argv) > 2:
    plan['stills'] = [float(t) for t in sys.argv[2].split(',')]
json.dump(plan, open(sys.argv[3] if len(sys.argv) > 3 else 'flyshelf-ad.plan.json', 'w', encoding='utf-8'), indent=1, ensure_ascii=False)
print('total', sum(s['duration'] for s in shots), 's')
