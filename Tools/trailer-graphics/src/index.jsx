import React from 'react';
import {Composition, registerRoot} from 'remotion';
import {Trailer} from './Trailer';

// Tools/make_trailer.py writes public/timeline.json and passes it in as the props.
const Root = () => (
  <Composition
    id="Trailer"
    component={Trailer}
    fps={60}
    width={1920}
    height={1080}
    durationInFrames={2820}
    defaultProps={{total: 2820, build: [], cuts: [], stingers: [], drop: 0, end: 2400}}
    calculateMetadata={({props}) => ({durationInFrames: props.total})}
  />
);

registerRoot(Root);
