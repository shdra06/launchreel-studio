# Example: the FlyShelf launch film

This is a 32-second, 10-shot launch film for [FlyShelf](https://github.com/shdra06/FlyShelf). It is built entirely from a plan file, and every frame of app footage is a real recording of the app.

`make_plan.py` writes the plan:
- the shots;
- the crop rectangles for each recorded window;
- the feature zooms;
- the dark *Aurora* look, or the bright *Atelier* look with `LOOK=light`.

The recordings themselves aren't in this repository, because they show a real clipboard history. To make your own film, record your app in Studio, export the takes, and adapt the shots.

```powershell
mkdir clips   # put your recordings here: summon.mp4, hover.mp4, … (see the asset list at the end of make_plan.py)
python ..\..\renderers\soundtrack.py soundtrack.wav --bars 16 --drops 1,7 --breaks 6 --ending 14 --key D
python make_plan.py launch-film.mp4
"..\..\artifacts\studio\LaunchReel Studio.exe" --data-root "$env:TEMP\reel-1" --render flyshelf-ad.plan.json
```

To render review stills instead of the video, pass the times in seconds: `python make_plan.py review 1.5,8,20`.

The thinking behind the look is in [docs/AD-LOOK.md](../../docs/AD-LOOK.md).
