import React from 'react';
import {
  AbsoluteFill,
  Audio,
  Easing,
  Img,
  OffthreadVideo,
  Sequence,
  continueRender,
  delayRender,
  interpolate,
  spring,
  staticFile,
  useCurrentFrame,
  useVideoConfig,
} from 'remotion';

const GREEN = '#3DDC6E';
const INK = '#07090A';
const FONT = 'Bicubik';   // the game's own UI font
const CLAMP = {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'};

const fontReady = delayRender('Loading the game font');
new FontFace(FONT, `url(${staticFile('Bicubik.otf')})`).load().then((face) => {
  document.fonts.add(face);
  continueRender(fontReady);
});

const heading = {fontFamily: FONT, color: 'white', textTransform: 'uppercase', lineHeight: 1.08};

/** Moving stripes and a green glow: what sits behind the phone during the build. */
const Backdrop = () => {
  const frame = useCurrentFrame();
  return (
    <AbsoluteFill style={{background: INK}}>
      <AbsoluteFill
        style={{
          background: `repeating-linear-gradient(115deg, transparent 0 46px, rgba(61,220,110,0.05) 46px 48px)`,
          backgroundPosition: `${frame * 1.2}px 0`,
        }}
      />
      <AbsoluteFill style={{background: 'radial-gradient(circle at 70% 50%, rgba(61,220,110,0.22), transparent 55%)'}} />
    </AbsoluteFill>
  );
};

/** One build line: words rise out of a mask, a bar wipes in under them. */
const BuildCaption = ({index, count, lines, sub, duration}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const leave = interpolate(frame, [duration - 7, duration], [0, 1], {...CLAMP, easing: Easing.in(Easing.cubic)});
  const bar = spring({frame: frame - 12, fps, config: {damping: 200}, durationInFrames: 20});
  const longest = Math.max(...lines.map((line) => line.length));

  return (
    <div style={{position: 'absolute', left: 90, top: 0, bottom: 0, width: 610, display: 'flex', flexDirection: 'column',
                 justifyContent: 'center', transform: `translateX(${-leave * 160}px)`, opacity: 1 - leave}}>
      <div style={{...heading, color: GREEN, fontSize: 26, letterSpacing: 6, marginBottom: 26, opacity: bar}}>
        {`0${index + 1} / 0${count}`}
      </div>
      {lines.map((line, i) => {
        const rise = spring({frame: frame - 3 - i * 5, fps, config: {damping: 15, mass: 0.6}});
        return (
          <div key={line} style={{overflow: 'hidden', paddingBottom: 6}}>
            <div style={{...heading, fontSize: Math.min(84, 900 / longest), transform: `translateY(${(1 - rise) * 115}%)`}}>
              {line}
            </div>
          </div>
        );
      })}
      <div style={{height: 8, width: 300 * bar, background: GREEN, marginTop: 30}} />
      <div style={{...heading, fontSize: 25, letterSpacing: 3, marginTop: 28, color: 'rgba(255,255,255,0.7)', opacity: bar,
                   transform: `translateY(${(1 - bar) * 14}px)`}}>
        {sub}
      </div>
    </div>
  );
};

/** A one-beat full-screen card between bursts of gameplay. */
const Stinger = ({white, green, duration}) => {
  const frame = useCurrentFrame();
  const edge = (from) => interpolate(frame, [from, from + 6], [0, 124], {...CLAMP, easing: Easing.out(Easing.cubic)});
  // A slanted panel that sweeps in from the left and out to the right.
  const panel = (lead) => {
    const right = edge(-lead);
    const left = edge(duration - 6 + lead);
    return `polygon(${left}% 0, ${right}% 0, ${right - 12}% 100%, ${left - 12}% 100%)`;
  };
  const scale = interpolate(frame, [0, duration], [1.22, 1], {...CLAMP, easing: Easing.out(Easing.exp)});
  const bar = interpolate(frame, [4, 16], [0, 1], {...CLAMP, easing: Easing.out(Easing.cubic)});

  return (
    <AbsoluteFill>
      <AbsoluteFill style={{background: GREEN, clipPath: panel(2)}} />
      <AbsoluteFill style={{clipPath: panel(0)}}>
        <Backdrop />
        <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center', transform: `scale(${scale})`}}>
          <div style={{...heading, fontSize: 118, textAlign: 'center', letterSpacing: 4}}>
            {white} <span style={{color: GREEN}}>{green}</span>
          </div>
          <div style={{height: 10, width: 420 * bar, background: GREEN, marginTop: 34}} />
        </AbsoluteFill>
      </AbsoluteFill>
    </AbsoluteFill>
  );
};

const FootageTag = ({duration}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const show = spring({frame, fps, config: {damping: 200}, durationInFrames: 14}) -
               spring({frame: frame - duration + 14, fps, config: {damping: 200}, durationInFrames: 14});
  return (
    <div style={{position: 'absolute', top: 150, left: 0, right: 0, display: 'flex', justifyContent: 'center',
                 opacity: show, transform: `translateY(${(1 - show) * -24}px)`}}>
      <div style={{...heading, fontSize: 24, letterSpacing: 5, padding: '14px 30px', background: 'rgba(7,9,10,0.78)',
                   borderLeft: `6px solid ${GREEN}`, display: 'flex', alignItems: 'center', gap: 16}}>
        <span style={{width: 14, height: 14, borderRadius: 7, background: GREEN, opacity: frame % 40 < 22 ? 1 : 0.25}} />
        In-game footage
      </div>
    </div>
  );
};

const EndCard = ({duration, line, small}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const title = spring({frame: frame - 34, fps, config: {damping: 200}, durationInFrames: 26});
  const detail = spring({frame: frame - 62, fps, config: {damping: 200}, durationInFrames: 26});

  return (
    <AbsoluteFill style={{background: 'black', opacity: interpolate(frame, [duration - 36, duration - 4], [1, 0], CLAMP)}}>
      <Img src={staticFile('banner.jpg')}
           style={{width: '100%', height: '100%', objectFit: 'cover',
                   transform: `scale(${interpolate(frame, [0, duration], [1.12, 1.02], CLAMP)})`}} />
      <div style={{position: 'absolute', left: 0, width: '86%', top: '77%', textAlign: 'center'}}>
        <div style={{height: 5, width: 520 * title, background: GREEN, margin: '0 auto 26px'}} />
        <div style={{...heading, fontSize: 40, opacity: title, letterSpacing: interpolate(title, [0, 1], [26, 8])}}>{line}</div>
        <div style={{...heading, fontSize: 24, marginTop: 22, letterSpacing: 5, color: GREEN, opacity: detail,
                     transform: `translateY(${(1 - detail) * 16}px)`}}>
          {small}
        </div>
      </div>
    </AbsoluteFill>
  );
};

const Hit = ({at, sound, volume = 1}) => (
  <Sequence from={Math.max(0, at)} layout="none">
    <Audio src={staticFile(sound)} volume={volume} />
  </Sequence>
);

export const Trailer = ({total, build, drop, cuts, stingers, end, endLine, endSmall}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const buildStart = build.length ? build[0].from : drop;

  // 0: the footage fills the screen. 1: it sits in the phone, beside the build captions.
  const phone = spring({frame: frame - buildStart, fps, config: {damping: 200}, durationInFrames: 20}) -
                spring({frame: frame - (drop - 9), fps, config: {damping: 200}, durationInFrames: 9});

  // Every cut after the drop kicks the picture in a touch and flashes.
  const inClimax = frame >= drop && frame < end;
  const sinceCut = frame - Math.max(drop, ...cuts.filter((cut) => cut <= frame));
  const punch = inClimax ? interpolate(sinceCut, [0, 10], [0.04, 0], {...CLAMP, easing: Easing.out(Easing.cubic)}) : 0;
  const flash = Math.max(
    inClimax ? interpolate(sinceCut, [0, 4], [0.3, 0], CLAMP) : 0,
    interpolate(frame - drop, [0, 14], [1, 0], CLAMP) * (frame >= drop ? 1 : 0),
    interpolate(frame - end, [0, 16], [1, 0], CLAMP) * (frame >= end ? 1 : 0),
  );

  return (
    <AbsoluteFill style={{background: 'black'}}>
      {phone > 0.001 ? <Backdrop /> : null}

      <AbsoluteFill
        style={{
          transform: `translate(${phone * 330}px, ${phone * Math.sin(frame / 34) * 8}px) perspective(1700px) ` +
                     `rotateY(${phone * -9}deg) scale(${1 - phone * 0.4 + punch})`,
          borderRadius: phone * 44,
          overflow: 'hidden',
          border: `${phone * 16}px solid #16191B`,
          boxShadow: phone > 0.001 ? `0 40px 120px rgba(0,0,0,0.8), 0 0 ${phone * 90}px rgba(61,220,110,0.28)` : 'none',
        }}
      >
        <OffthreadVideo src={staticFile('base.mp4')} style={{width: '100%', height: '100%'}} />
      </AbsoluteFill>

      <Sequence from={24} durationInFrames={Math.max(30, buildStart - 60)}>
        <FootageTag duration={Math.max(30, buildStart - 60)} />
      </Sequence>

      {build.map((shot, index) => (
        <Sequence key={shot.from} from={shot.from} durationInFrames={shot.to - shot.from}>
          <BuildCaption index={index} count={build.length} lines={shot.lines} sub={shot.sub} duration={shot.to - shot.from} />
        </Sequence>
      ))}

      {stingers.map((stinger) => (
        <Sequence key={stinger.from} from={stinger.from} durationInFrames={stinger.to - stinger.from}>
          <Stinger white={stinger.white} green={stinger.green} duration={stinger.to - stinger.from} />
        </Sequence>
      ))}

      <Sequence from={end} durationInFrames={total - end}>
        <EndCard duration={total - end} line={endLine} small={endSmall} />
      </Sequence>

      <AbsoluteFill style={{background: 'white', opacity: flash}} />

      <Hit at={buildStart - 16} sound="whoosh.wav" volume={0.7} />
      <Hit at={drop - 20} sound="whoosh.wav" />
      <Hit at={drop} sound="impact.wav" />
      {stingers.map((stinger) => <Hit key={stinger.from} at={stinger.from} sound="impact.wav" volume={0.8} />)}
      <Hit at={end} sound="impact.wav" />
    </AbsoluteFill>
  );
};
