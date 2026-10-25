#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.RA2.Traits
{
	[Desc("Flies a missile launched by a MissileSpawnerParent like the original game's rocket locomotor:",
		"a pause and a tilt on the launcher, a climb to the cruise altitude, the cruise and a dive onto the target.",
		"Its flight is worked out in the original's leptons, with the [General] V3Rocket* and DMisl* values unconverted:",
		"256 leptons are a cell (1024 world units across) and 104 a level of height (724 world units up).")]
	public class RocketInfo : TraitInfo, IPositionableInfo, IFacingInfo, IRulesetLoaded
	{
		[Desc("Ticks the missile waits on the launcher before it tilts (PauseFrames).")]
		public readonly int PauseTicks = 0;

		[Desc("Ticks it takes to tilt from PitchInitial to PitchFinal (TiltFrames).")]
		public readonly int TiltTicks = 60;

		[Desc("Pitch before tilting, in quarter turns (PitchInitial).")]
		public readonly float PitchInitial = 0;

		[Desc("Pitch after tilting, in quarter turns (PitchFinal).")]
		public readonly float PitchFinal = 0.5f;

		[Desc("How fast the pitch changes while cruising level and diving, in radians per tick (TurnRate).")]
		public readonly float TurnRate = 0.05f;

		[Desc("Speed gained each tick while climbing and cruising, in leptons (Acceleration).")]
		public readonly float Acceleration = 0.4f;

		[Desc("Top speed, in leptons per tick: the missile type's Speed as the original converts it, Speed * 256 / 100.")]
		public readonly int Speed = 38;

		[Desc("Height above the ground at which the climb ends, in leptons (Altitude).")]
		public readonly int CruiseAltitude = 768;

		[Desc("Distance from the missile's centre to its nose, where it explodes, in leptons (BodyLength).")]
		public readonly int BodyLength = 256;

		[Desc("Fly a big, lazy curve onto the target instead of levelling off and diving (LazyCurve).")]
		public readonly bool LazyCurve = false;

		[Desc("Draw the missile this much nearer the camera, without moving it on the screen, while it waits and tilts",
			"on the launcher. The original draws it over the launcher; here the launcher's model would otherwise hide it.",
			"Once it flies, the offset shrinks by half the distance it has gone from the launcher.")]
		public readonly WDist LauncherDepthOffset = new(512);

		[Desc("How fast the heading turns toward the target while cruising (the missile type's ROT).")]
		public readonly WAngle TurnSpeed = new(12);

		[WeaponReference]
		[FieldLoader.Require]
		[Desc("Weapon detonated at the nose.")]
		public readonly string Weapon = null;

		[WeaponReference]
		[Desc("Weapon detonated instead when launched by an elite launcher.")]
		public readonly string EliteWeapon = null;

		[Desc("Image of the animations played at takeoff and along the trail.")]
		public readonly string EffectImage = null;

		[SequenceReference(nameof(EffectImage), allowNullImage: true)]
		[Desc("Animation played where the missile is when the tilt ends.")]
		public readonly string TakeoffSequence = null;

		[SequenceReference(nameof(EffectImage), allowNullImage: true)]
		[Desc("Animation left behind while climbing, cruising and diving.")]
		public readonly string TrailSequence = null;

		[Desc("Ticks between trail animations.")]
		public readonly int TrailInterval = 3;

		[PaletteReference]
		public readonly string EffectPalette = "effect";

		[Desc("Sounds, one picked at random, played when the tilt ends.")]
		public readonly string[] TakeoffSounds = [];

		public WeaponInfo WeaponInfo { get; private set; }
		public WeaponInfo EliteWeaponInfo { get; private set; }

		public override object Create(ActorInitializer init) { return new Rocket(init, this); }

		public void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			WeaponInfo = rules.Weapons[Weapon.ToLowerInvariant()];
			if (EliteWeapon != null)
				EliteWeaponInfo = rules.Weapons[EliteWeapon.ToLowerInvariant()];
		}

		public WAngle GetInitialFacing() { return WAngle.Zero; }

		public IReadOnlyDictionary<CPos, SubCell> OccupiedCells(ActorInfo info, CPos location, SubCell subCell = SubCell.Any)
		{
			return new Dictionary<CPos, SubCell>();
		}

		bool IOccupySpaceInfo.SharesCell => false;

		public bool CanEnterCell(World world, Actor self, CPos cell, SubCell subCell = SubCell.FullCell,
			Actor ignoreActor = null, BlockedByActor check = BlockedByActor.All)
		{
			return true;
		}
	}

	// See docs/original/aircraft.md ("Missiles: the spawn manager and the rocket locomotor")
	// and crates/ra2-sim/src/movement/rocket.rs of the ra2-engine port for the original's behaviour.
	public class Rocket : ISync, IPositionable, IFacing, ITick, INotifyAddedToWorld, INotifyRemovedFromWorld, INotifyKilled,
		IRenderModifier
	{
		enum State { Waiting, Pause, Tilt, Climb, Cruise, Dive }

		// Pitches are kept in thousandths of a WAngle unit and speeds in thousandths of a lepton,
		// so that the original's fractional rates survive the integer arithmetic.
		const int Fraction = 1000;
		const int QuarterTurn = 256 * Fraction;

		// A lepton is 4 world units across, and a level of height (104 leptons) is 724 world units.
		const int LeptonsAcross = 4;
		const int WorldPerLevel = 724;
		const int LeptonsPerLevel = 104;

		readonly Actor self;
		readonly RocketInfo info;
		readonly int pitchInitial;
		readonly int pitchFinal;
		readonly int turnRate;
		readonly int acceleration;
		readonly int topSpeed;

		[VerifySync]
		public WPos CenterPosition { get; private set; }

		[VerifySync]
		public WAngle Facing { get; set; }

		State state;

		[VerifySync]
		public int SyncedState => (int)state;

		[VerifySync]
		int pitch;

		[VerifySync]
		int speed;

		// Where it is and where it is going, in leptons.
		[VerifySync]
		WVec position;

		WVec destination;
		WPos launchPosition;
		int timer;
		int timerLength;
		int trailTicks;
		int firstDistance;
		bool elite;
		bool exploded;
		Actor launcher;

		public Rocket(ActorInitializer init, RocketInfo info)
		{
			self = init.Self;
			this.info = info;

			pitchInitial = (int)(info.PitchInitial * QuarterTurn);
			pitchFinal = (int)(info.PitchFinal * QuarterTurn);
			turnRate = (int)(info.TurnRate * 512 / Math.PI * Fraction);
			acceleration = (int)(info.Acceleration * Fraction);
			topSpeed = info.Speed * Fraction;
			pitch = pitchInitial;
		}

		public WAngle TurnSpeed => info.TurnSpeed;
		public WRot Orientation => new(WAngle.Zero, new WAngle(pitch / Fraction), Facing);
		public CPos TopLeft => self.World.Map.CellContaining(CenterPosition);
		public bool IsWaiting => state == State.Waiting;

		public (CPos Cell, SubCell SubCell)[] OccupiedCells() { return []; }

		/// <summary>Sends the missile on its way. Like the original's locomotor it only listens the first time.</summary>
		public void Launch(Actor launcher, in Target target, bool elite)
		{
			if (state != State.Waiting)
				return;

			this.launcher = launcher;
			this.elite = elite;
			destination = ToLeptons(target.CenterPosition);
			launchPosition = CenterPosition;
			pitch = pitchInitial;
			StartTimer(info.PauseTicks > 0 ? State.Pause : State.Tilt, info.PauseTicks > 0 ? info.PauseTicks : info.TiltTicks);
		}

		void StartTimer(State newState, int length)
		{
			state = newState;
			timer = 0;
			timerLength = length;
		}

		void ITick.Tick(Actor self)
		{
			if (exploded || state == State.Waiting)
				return;

			var groundZ = CenterPosition.Z - self.World.Map.DistanceAboveTerrain(CenterPosition).Length;
			var height = position.Z - groundZ * LeptonsPerLevel / WorldPerLevel;

			// The heading turns toward the target while cruising, but the move this tick goes the way it faced before.
			var heading = Facing;
			switch (state)
			{
				case State.Pause:
					speed = 0;
					if (++timer >= timerLength)
						StartTimer(State.Tilt, info.TiltTicks);

					break;

				case State.Tilt:
					speed = 0;
					if (++timer < timerLength)
						pitch = pitchInitial + (int)((long)(pitchFinal - pitchInitial) * timer / timerLength);
					else
					{
						pitch = pitchFinal;
						state = State.Climb;
						PlayEffect(info.TakeoffSequence, CenterPosition);
						if (info.TakeoffSounds.Length > 0)
							Game.Sound.Play(SoundType.World, info.TakeoffSounds.Random(Game.CosmeticRandom), CenterPosition);
					}

					break;

				case State.Climb:
					SpeedUp();
					if (height >= info.CruiseAltitude)
					{
						state = State.Cruise;
						firstDistance = (destination - position).HorizontalLength;
					}

					break;

				case State.Cruise:
					if (height <= 0)
					{
						Explode(self);
						return;
					}

					SpeedUp();
					if (info.LazyCurve && firstDistance != 0)
					{
						if (NoseDown(self, height))
							return;

						// A blend of the final pitch and the slope down to the target, the more the nearer.
						var distance = (destination - position).HorizontalLength;
						var down = (long)SlopeToDestination();
						pitch = (int)((down * (firstDistance - distance) + (long)pitchFinal * distance) / firstDistance);
					}
					else
					{
						if (pitch > 0)
							pitch = Math.Max(0, pitch - turnRate);

						// It dives once it is no further across the ground from the target than it is above it.
						var offset = position - destination;
						if (offset.HorizontalLength <= offset.Z)
							state = State.Dive;
					}

					var desired = (destination - position).Yaw;
					Facing = Common.Util.TickFacing(Facing, desired, info.TurnSpeed);
					break;

				case State.Dive:
					if (NoseDown(self, height))
						return;

					var off = SlopeToDestination() - pitch;
					pitch += Math.Clamp(off, -turnRate, turnRate);
					break;
			}

			if (state >= State.Climb && --trailTicks <= 0)
			{
				PlayEffect(info.TrailSequence, CenterPosition);
				trailTicks = info.TrailInterval;
			}

			// Each tick it moves its speed, cut to a whole number, along its pitch and heading.
			if (speed > 0)
				MoveTo(self, position + Step(speed / Fraction, heading));
		}

		static WVec ToLeptons(WPos pos)
		{
			return new WVec(pos.X / LeptonsAcross, pos.Y / LeptonsAcross, pos.Z * LeptonsPerLevel / WorldPerLevel);
		}

		static WPos ToWorld(WVec leptons)
		{
			return new WPos(leptons.X * LeptonsAcross, leptons.Y * LeptonsAcross, leptons.Z * WorldPerLevel / LeptonsPerLevel);
		}

		void MoveTo(Actor self, WVec leptons)
		{
			position = leptons;
			CenterPosition = ToWorld(leptons);
			if (self.IsInWorld)
				self.World.UpdateMaps(self, this);
		}

		void SpeedUp()
		{
			speed = Math.Min(speed + acceleration, topSpeed);
		}

		WVec Step(int length, WAngle heading)
		{
			var angle = new WAngle(pitch / Fraction);
			var across = length * angle.Cos() / 1024;
			var up = length * angle.Sin() / 1024;
			return new WVec(0, -across, up).Rotate(WRot.FromYaw(heading));
		}

		/// <summary>The slope down from the missile to the target, as a pitch; a quarter turn down when right above it.</summary>
		int SlopeToDestination()
		{
			var delta = destination - position;
			var across = delta.HorizontalLength;
			if (across == 0)
				return -QuarterTurn;

			var angle = WAngle.ArcTan(delta.Z, across).Angle;
			return (angle > 512 ? angle - 1024 : angle) * Fraction;
		}

		/// <summary>It explodes once its nose comes down to the target's height, or it reaches the ground.</summary>
		bool NoseDown(Actor self, int height)
		{
			var nose = position.Z + info.BodyLength * new WAngle(pitch / Fraction).Sin() / 1024;
			if (nose > destination.Z && height > 0)
				return false;

			Explode(self);
			return true;
		}

		void Explode(Actor self)
		{
			Detonate(self);
			self.World.AddFrameEndTask(w => self.Dispose());
		}

		void Detonate(Actor self)
		{
			if (exploded)
				return;

			exploded = true;
			var nose = ToWorld(position + Step(info.BodyLength, Facing));
			var weapon = elite && info.EliteWeaponInfo != null ? info.EliteWeaponInfo : info.WeaponInfo;
			var source = launcher != null && !launcher.IsDead ? launcher : self;
			weapon.Impact(Target.FromPos(nose), new WarheadArgs
			{
				Weapon = weapon,
				SourceActor = source,
				Source = CenterPosition,
				ImpactPosition = nose,
				ImpactOrientation = Orientation,
				WeaponTarget = Target.FromPos(nose)
			});
		}

		// A missile shot down still goes off.
		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			if (state != State.Waiting)
				Detonate(self);
		}

		void PlayEffect(string sequence, WPos pos)
		{
			if (sequence == null || info.EffectImage == null)
				return;

			self.World.AddFrameEndTask(w => w.Add(new SpriteEffect(pos, w, info.EffectImage, sequence, info.EffectPalette)));
		}

		// Depth follows the world's Y and the screen's height Y less Z: moving both by the same distance brings the
		// missile nearer the camera without moving it on the screen.
		IEnumerable<IRenderable> IRenderModifier.ModifyRender(Actor self, WorldRenderer wr, IEnumerable<IRenderable> r)
		{
			if (state == State.Waiting || info.LauncherDepthOffset.Length == 0)
				return r;

			var depth = info.LauncherDepthOffset.Length;
			if (state > State.Tilt)
				depth -= (CenterPosition - launchPosition).Length / 2;

			if (depth <= 0)
				return r;

			var offset = new WVec(0, depth, depth);
			return r.Select(renderable => renderable.OffsetBy(offset));
		}

		IEnumerable<Rectangle> IRenderModifier.ModifyScreenBounds(Actor self, WorldRenderer wr, IEnumerable<Rectangle> r)
		{
			return r;
		}

		public bool CanExistInCell(CPos location) { return true; }
		public bool IsLeavingCell(CPos location, SubCell subCell = SubCell.Any) { return false; }
		public bool CanEnterCell(CPos location, Actor ignoreActor = null, BlockedByActor check = BlockedByActor.All) { return true; }
		public SubCell GetValidSubCell(SubCell preferred = SubCell.Any) { return SubCell.FullCell; }

		public SubCell GetAvailableSubCell(CPos location, SubCell preferredSubCell = SubCell.Any,
			Actor ignoreActor = null, BlockedByActor check = BlockedByActor.All)
		{
			return SubCell.FullCell;
		}

		public void SetPosition(Actor self, CPos cell, SubCell subCell = SubCell.Any)
		{
			SetPosition(self, self.World.Map.CenterOfCell(cell));
		}

		public void SetPosition(Actor self, WPos pos)
		{
			position = ToLeptons(pos);
			CenterPosition = pos;
			if (self.IsInWorld)
				self.World.UpdateMaps(self, this);
		}

		public void SetCenterPosition(Actor self, WPos pos) { SetPosition(self, pos); }

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			self.World.AddToMaps(self, this);
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			self.World.RemoveFromMaps(self, this);
		}
	}
}
