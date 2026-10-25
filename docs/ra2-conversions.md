# Converting Red Alert 2 values to OpenRA

How the values of the original game's ini files (`rulesmd.ini`, `artmd.ini`,
`soundmd.ini`) become this mod's YAML. The 1.001 patch's `rulesmd.ini` (in
`expandmd01.mix`) is the reference; `./utility.sh --extract rulesmd.ini`
gives it from the mounted game files.

Each rule says how it is known: **verified** (against the original game:
oracle runs of the ra2-engine project, its decompile, or a measurement) or
**mod convention** (what this mod has done so far, not checked against the
original). Prefer verified rules for new work.

## Distances and heights (verified)

- A cell is 256 leptons, and 1024 world units across: horizontal lengths
  are leptons x 4.
- A level of height is 104 leptons, and 724 world units: vertical lengths
  are leptons x 724 / 104 (about 6.96). Both are 15 pixels on the screen,
  so this keeps things where the original draws them. Converting heights
  x 4 makes everything sit too low against the terrain (missiles skimming
  hills).
- A fire position (`PrimaryFireFLH`, `SecondaryFireFLH`, ...) is in leptons,
  forward, lateral and height in the unit's frame: `LocalOffset` is
  `F x 4, L x 4, H x 724 / 104`. Measured on the Dreadnought of `sov02t`:
  its missiles are 51.8 leptons from its middle, which is its FLH 30,43 in
  plain leptons.
- A `Burst` of more than one fires its first shot on the side opposite the
  FLH's lateral sign (`sov02t`: at facing 160 the first Dreadnought missile is
  at +9,+51 leptons from it, the second at -51,-9). List the barrels in that
  order: OpenRA fires `LocalOffset`'s entries in turn for a burst.
- Most units' `LocalOffset`s so far were made as FLH x 5.12 in all three axes
  (mod convention). They are probably slightly wrong; check one against the
  rule above before trusting it.

## Directions (verified)

- A facing is 0 to 255, clockwise from north; OpenRA's `WAngle` runs the other
  way: `WAngle = (256 - facing) x 4`, modulo 1024. Facing 160 is yaw 384.

## Speed, turning and time

- An ini `Speed` is a percentage: the original moves `Speed * 256 / 100`
  leptons a frame (verified).
- The mod's `Mobile.Speed` is ini `Speed` x 15 and its `TurnSpeed` ini `ROT`
  x 4 (mod convention). In world units a frame, the original's `Speed` would
  be x 10.24; the difference has not been looked into.
- One frame of the original is one tick here: `ReloadDelay` is the ini `ROF`,
  timers in frames are ticks (mod convention). The original's campaign runs
  31.25 frames a second (32 ms a frame, verified), the mod's normal game speed
  40 ms a tick: 80% of it.
- An animation's `Rate` is a speed: it shows each image for `900 / Rate`
  frames (verified), so a sequence's `Tick` is that times 40 ms.

## Weapons and warheads (verified)

- `Verses` lists percentages for the armours none, flak, plate, light, medium,
  heavy, wood, steel, concrete, special_1 and special_2: `Versus` None, Flak,
  Plate, Light, Medium, Heavy, Wood, Steel, Concrete, Drone and Rocket.
- Damage falls linearly from 100% at the impact to `PercentAtMax` at
  `CellSpread` cells, and is none beyond: `Spread` is `CellSpread` cells and
  `Falloff` is `100, PercentAtMax`.
- The explosion is the `AnimList` entry for the damage, one for every 25
  points: entry `min(damage, count * 25 - 1) / 25`. Over water,
  `[CombatDamage] SplashList` by every 35 points instead.
- `ProneDamage=70%` is the `Prone70Percent` damage type.

## Models (verified)

- A voxel model records its normals: mode 2 is Tiberian Sun's
  (`NormalsPalette: ts-normals`), mode 4 Red Alert 2's (the default
  `normals`). The wrong one draws the model nearly black.
- A type with `NoSpawnAlt` draws `{image}wo` (`v3wo`, `dredwo`) while nothing
  it spawns is aboard.
- `NoShadow=yes` is `ShowShadow: false` on the model.

## OpenRA's rendering

- OpenRA draws each voxel model as one sprite at one depth, pushed nearer the
  camera by the model's own extent. A small model on a big one (a missile on
  the Dreadnought's deck) is hidden by it although the original draws it on
  top: the `Rocket` trait's `LauncherDepthOffset` draws a missile nearer while
  it is on its launcher, fading as it leaves.
- Depth follows the world's Y and the screen's height Y less Z: moving a
  renderable by (0, d, d) brings it nearer by d without moving it on the
  screen.

## Bridges (verified)

- A low bridge piece (`LOBRDG`, `LOBRDB`) is its overlay on three cells
  across the bridge, the overlay data 0 to 2 saying which; only the middle
  one (1) is drawn, its art being frame 1 of the file (frames 0 and 2 are
  empty). The importer makes one actor per middle cell. Some multiplayer
  maps (South Pacific) hold edge cells with no middle: the original shows
  nothing there.
- A high bridge's deck (`BRIDGE1`/`BRIDGE2`, the wooden `BRIDGEB1`/`B2` as
  overlays 237 and 238) is drawn flat like the ground, 16 pixels up the
  screen (31 along y), and 4 levels plus 2 rows nearer: the sequence
  `Offset` is `0, -16, 62` (`0, -31, 62`). Less lets the ramp tile at the
  bridge's upper end cut into the deck. Checked against the original's
  screenshots of both ends of `all03u`'s bridge.
- `MapSnapshot` (world trait) renders a whole map to a PNG and quits when
  `OPENRA_MAP_SNAPSHOT` names the file: the quickest way to check an
  imported map by eye.
