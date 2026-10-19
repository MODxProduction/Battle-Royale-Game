"""Cuts the takes in Recordings/ into a beat-synced trailer with motion graphics.

    python Tools/make_trailer.py     -> Recordings/trailer.mp4

Needs ffmpeg and Node on PATH, `pip install librosa`, and once:
`npm install` in Tools/trailer-graphics.

Takes come from Unity (Tools > Last Stand > Trailer). Their names sort them:
    ui_*.mp4      menus and lobby, shown in the phone frame during the build (UI_SHOTS)
    play_*.mp4    matches; the action is found by ear - the loudest moments of a take
                  are its gunfights, so no take needs marking up by hand
    win_*.mp4     optional: a take that opens on the Victory screen, which then closes
                  the action

Two steps: ffmpeg cuts the footage to the beats of the game's own music, then Remotion
(Tools/trailer-graphics) lays the captions, cards and end screen over it.
"""
import glob
import json
import os
import shutil
import subprocess
import wave

import librosa
import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GRAPHICS = "Tools/trailer-graphics"
MUSIC = "Assets/Audio/Music/Game].mp3"
OUTPUT = "Recordings/trailer.mp4"

# Where the sections of the trailer sit in the music, in seconds (snapped to the nearest
# beat). The track goes quiet at 134 s, drops at 145 s and breaks at 169 s; the trailer
# is hung on those three moments.
HOOK, BUILD, DROP, END, OUT = 129.45, 134.49, 145.33, 169.20, 176.43

# The build: (take name prefix, seconds into that take, caption lines, small print).
# ponytail: the offsets belong to the takes recorded on 2026-10-06 (menu at 0 s, host
# popup opens at 17.1 s, Duo picked at 28.8 s). Re-record a ui_ take, re-time its row.
UI_SHOTS = [
    ("ui_1menu", 1.0, ["No internet.", "No servers."], "A battle royale that runs on your phone alone"),
    ("ui_1menu", 16.3, ["Host", "a match"], "One phone hosts. Friends join on its hotspot"),
    ("ui_1menu", 27.6, ["Solo.", "Duo.", "Squad."], "Teammates cannot hurt each other"),
    ("ui_2lobby", 1.0, ["1 to 4", "players"], "Empty slots are filled with AI bots"),
]

# Full-screen cards, one beat long, this many beats after the drop: (beat, white, green).
STINGERS = [(8, "Smart", "AI bots"), (16, "Shrinking", "safe zone"), (24, "Last one", "standing")]

END_LINE = "Offline co-op battle royale"
END_SMALL = "For Android   /   1 to 4 players   /   No internet needed"

WIN_BEATS = 3

FPS = 60
STEP = 0.05          # loudness resolution, seconds
GAP = 0.4            # footage left unused either side of a pick, so no shot repeats
MUSIC_GAIN = 0.4     # under the gunfire, not over it
SFX_GAIN = 1.4


def loudness(path):
    """RMS of a take's sound, one value every STEP seconds."""
    rate = 8000
    raw = subprocess.run(["ffmpeg", "-v", "error", "-i", path, "-ac", "1", "-ar", str(rate), "-f", "s16le", "-"],
                         capture_output=True, check=True).stdout
    samples = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768
    block = int(rate * STEP)
    samples = samples[:len(samples) // block * block].reshape(-1, block)
    return np.sqrt((samples ** 2).mean(axis=1))


def pick(takes, free, seconds):
    """Loudest unused window of this length across all takes: (take index, start in seconds).

    The window is then marked used.
    """
    width = max(1, round(seconds / STEP))
    best = None

    for index, (envelope, unused) in enumerate(zip(takes, free)):
        if len(envelope) < width:
            continue

        score = np.convolve(envelope, np.ones(width) / width, mode="valid")
        # Only windows with every step still unused.
        open_ = np.convolve(unused.astype(float), np.ones(width), mode="valid") > width - 0.5
        if not open_.any():
            continue

        score = np.where(open_, score, -np.inf)
        start = int(score.argmax())

        if best is None or score[start] > best[0]:
            best = (score[start], index, start)

    if best is None:
        raise SystemExit("Not enough footage: record a longer play_ take.")

    _, index, start = best
    pad = round(GAP / STEP)
    free[index][max(0, start - pad):start + width + pad] = False
    return index, start * STEP


def self_test():
    quiet = np.full(400, 0.01, dtype=np.float32)
    quiet[200:220] = 1.0
    takes, free = [quiet], [np.ones(400, dtype=bool)]
    first = pick(takes, free, 1.0)
    second = pick(takes, free, 1.0)
    assert first == (0, 10.0), first
    assert abs(second[1] - first[1]) >= 1.0 + GAP - 1e-6, second   # never the same footage twice


def write_sound(path, samples):
    with wave.open(path, "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(48000)
        out.writeframes((samples / np.abs(samples).max() * 0.9 * 32767).astype(np.int16).tobytes())


def write_hits(folder):
    """The two sounds the graphics land on: a sub drop and a rising rush of air."""
    noise = np.random.default_rng(7).standard_normal(96000)

    t = np.arange(int(48000 * 1.1)) / 48000
    sweep = np.sin(2 * np.pi * np.cumsum(38 + 90 * np.exp(-t / 0.07)) / 48000) * np.exp(-t / 0.28)
    crack = np.convolve(noise[:len(t)], np.ones(24) / 24, mode="same") * np.exp(-t / 0.03)
    write_sound(os.path.join(folder, "impact.wav"), sweep + 0.5 * crack)

    t = np.arange(int(48000 * 0.45)) / 48000
    swell = (t / t[-1]) ** 3 * np.exp(-np.clip(t - 0.4, 0, None) / 0.01)
    write_sound(os.path.join(folder, "whoosh.wav"), np.convolve(noise[:len(t)], np.ones(10) / 10, mode="same") * swell)


def newest(prefix):
    found = sorted(glob.glob(f"Recordings/{prefix}_*.mp4"))
    if not found:
        raise SystemExit(f"No {prefix}_ take in Recordings/. Record one in Unity first.")
    return found[-1]


def main():
    self_test()
    os.chdir(ROOT)

    paths = sorted(glob.glob("Recordings/play_*.mp4"))
    if not paths:
        raise SystemExit("No play_ takes in Recordings/. Record one in Unity first.")

    takes = [loudness(p) for p in paths]
    free = [np.ones(len(t), dtype=bool) for t in takes]

    samples, rate = librosa.load(MUSIC, sr=22050, mono=True)
    _, beats = librosa.beat.beat_track(y=samples, sr=rate, units="time")
    beat = lambda t: int(np.abs(beats - t).argmin())
    hook, build, drop, end, out = (beat(t) for t in (HOOK, BUILD, DROP, END, OUT))
    frame = lambda b: round((beats[b] - beats[hook]) * FPS)

    # Cuts as beat indices: one long hook, the menus, then gameplay cuts that tighten
    # from two beats to one.
    build_cuts = [build + round((drop - build) * i / len(UI_SHOTS)) for i in range(len(UI_SHOTS))]
    action_cuts = [hook] + list(range(drop, drop + 8, 2)) + list(range(drop + 8, end))
    win = sorted(glob.glob("Recordings/win_*.mp4"))
    if win:
        del action_cuts[-(WIN_BEATS - 1):]   # one shot, WIN_BEATS long, right before the end screen
    cuts = sorted(build_cuts + action_cuts) + [end]
    lengths = {a: frame(b) - frame(a) for a, b in zip(cuts, cuts[1:])}

    # Longest shots choose first, so the hook gets the best sustained fight.
    shots = {}
    for cut in sorted(action_cuts, key=lambda c: -lengths[c]):
        index, start = pick(takes, free, lengths[cut] / FPS)
        shots[cut] = (paths[index], start)
    if win:
        shots[action_cuts[-1]] = (win[-1], 0.5)
    for cut, (prefix, start, _, _) in zip(build_cuts, UI_SHOTS):
        shots[cut] = (newest(prefix), start)

    total = frame(out)
    command = ["ffmpeg", "-y", "-v", "error"]
    graph = []

    for n, cut in enumerate(cuts[:-1]):
        path, start = shots[cut]
        seconds = lengths[cut] / FPS
        command += ["-ss", f"{start:.3f}", "-t", f"{seconds + 0.5:.3f}", "-i", path]
        graph.append(f"[{n}:v]fps={FPS},trim=end_frame={lengths[cut]},setpts=PTS-STARTPTS,scale=1920:1080,setsar=1[v{n}]")
        graph.append(f"[{n}:a]aresample=48000,aformat=channel_layouts=stereo,atrim=0:{seconds:.4f},asetpts=PTS-STARTPTS,"
                     f"afade=t=in:d=0.01,afade=t=out:st={seconds - 0.01:.4f}:d=0.01,volume={SFX_GAIN}[a{n}]")
        print(f"{frame(cut) / FPS:6.2f}s  {seconds:4.2f}s  {os.path.basename(path)} @ {start:.2f}s")

    count = len(cuts) - 1
    command += ["-ss", f"{beats[hook]:.3f}", "-t", f"{total / FPS:.3f}", "-i", MUSIC]

    # The end screen is drawn by the graphics; here it is black under the music's tail.
    graph.append("".join(f"[v{n}]" for n in range(count)) +
                 f"concat=n={count}:v=1:a=0,tpad=stop_duration={(total - frame(end)) / FPS:.3f},format=yuv420p[v]")
    graph.append("".join(f"[a{n}]" for n in range(count)) + f"concat=n={count}:v=0:a=1,apad,atrim=0:{total / FPS:.3f}[sfx]")
    graph.append(f"[{count}:a]aresample=48000,volume={MUSIC_GAIN},afade=t=in:d=0.1,"
                 f"afade=t=out:st={total / FPS - 1.5:.2f}:d=1.5[music]")
    graph.append("[sfx][music]amix=inputs=2:normalize=0:duration=first[a]")

    public = os.path.join(GRAPHICS, "public")
    os.makedirs(public, exist_ok=True)
    command += ["-filter_complex", ";".join(graph), "-map", "[v]", "-map", "[a]",
                "-c:v", "libx264", "-preset", "fast", "-crf", "14", "-pix_fmt", "yuv420p", "-r", str(FPS),
                "-c:a", "aac", "-b:a", "256k", os.path.join(public, "base.mp4")]
    subprocess.run(command, check=True)

    timeline = {
        "total": total,
        "drop": frame(drop),
        "end": frame(end),
        "cuts": [frame(c) for c in action_cuts[1:]],
        "build": [{"from": frame(a), "to": frame(b), "lines": lines, "sub": sub}
                  for a, b, (_, _, lines, sub) in zip(build_cuts, build_cuts[1:] + [drop], UI_SHOTS)],
        "stingers": [{"from": frame(drop + b), "to": frame(drop + b + 1), "white": white, "green": green}
                     for b, white, green in STINGERS],
        "endLine": END_LINE,
        "endSmall": END_SMALL,
    }
    with open(os.path.join(public, "timeline.json"), "w") as out_file:
        json.dump(timeline, out_file)

    shutil.copy("Assets/UI/Font/Bicubik/Bicubik.OTF", os.path.join(public, "Bicubik.otf"))
    shutil.copy("docs/images/banner.jpg", os.path.join(public, "banner.jpg"))
    write_hits(public)

    subprocess.run(["npx", "remotion", "render", "src/index.jsx", "Trailer", "public/graphics.mp4",
                    "--props=public/timeline.json", "--codec=h264", "--crf=17", "--audio-bitrate=256k",
                    "--log=error"], cwd=GRAPHICS, check=True, shell=os.name == "nt")

    # Last, because the graphics add their own hits to the mix. -14 LUFS is what YouTube
    # and the other platforms play back at; the true-peak ceiling keeps it from clipping.
    subprocess.run(["ffmpeg", "-y", "-v", "error", "-i", os.path.join(public, "graphics.mp4"), "-c:v", "copy",
                    "-af", "loudnorm=I=-14:LRA=11:TP=-1.5,aresample=48000", "-c:a", "aac", "-b:a", "256k",
                    "-movflags", "+faststart", OUTPUT], check=True)
    print(f"{OUTPUT}: {total / FPS:.1f} s, {count} shots")


if __name__ == "__main__":
    main()
