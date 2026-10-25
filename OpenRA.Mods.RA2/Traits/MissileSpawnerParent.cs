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

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.RA2.Traits
{
	[Desc("Launches missile actors (with the Rocket trait) like the original game's spawn manager:",
		"each firing of the armament puts a stored missile up at the muzzle, and once no missile is left in store",
		"the whole salvo takes off. A slot is empty for RespawnTicks after its missile has left the launcher.")]
	public class MissileSpawnerParentInfo : BaseSpawnerParentInfo
	{
		[GrantedConditionReference]
		[Desc("Condition granted once for every missile in store.")]
		public readonly string LoadedCondition = null;

		[Desc("Offset from the muzzle at which a missile is put up.")]
		public readonly WVec LaunchOffset = new(0, 0, 40);

		public override object Create(ActorInitializer init) { return new MissileSpawnerParent(init, this); }
	}

	// See docs/original/aircraft.md ("Missiles: the spawn manager and the rocket locomotor") of the ra2-engine port.
	public class MissileSpawnerParent : BaseSpawnerParent, ITick, INotifyAttack
	{
		sealed class MissileChildEntry : BaseSpawnerChildEntry
		{
			public bool Stored;
			public bool Readied;
			public int TakeoffTicks;
			public int RegenTicks;
		}

		readonly MissileSpawnerParentInfo info;
		readonly Stack<int> loadedTokens = new();
		GainsExperience experience;

		public MissileSpawnerParent(ActorInitializer init, MissileSpawnerParentInfo info)
			: base(init, info)
		{
			this.info = info;
		}

		public override BaseSpawnerChildEntry[] CreateChildEntries(BaseSpawnerParentInfo info)
		{
			return info.Actors.Select(a => new MissileChildEntry { ActorName = a }).ToArray();
		}

		protected override void Created(Actor self)
		{
			base.Created(self);
			experience = self.TraitOrDefault<GainsExperience>();

			var count = Info.InitialActorCount == -1 ? Info.Actors.Length : Info.InitialActorCount;
			for (var i = 0; i < count; i++)
				Replenish(self, ChildEntries);
		}

		public override void InitializeChildEntry(Actor child, BaseSpawnerChildEntry entry)
		{
			base.InitializeChildEntry(child, entry);

			var missileEntry = (MissileChildEntry)entry;
			missileEntry.Stored = true;
			missileEntry.Readied = false;
		}

		public override void Replenish(Actor self, BaseSpawnerChildEntry entry)
		{
			base.Replenish(self, entry);

			if (info.LoadedCondition != null)
				loadedTokens.Push(self.GrantCondition(info.LoadedCondition));
		}

		void INotifyAttack.PreparingAttack(Actor self, in Target target, Armament a, Barrel barrel) { }

		void INotifyAttack.Attacking(Actor self, in Target target, Armament a, Barrel barrel)
		{
			if (IsTraitDisabled || !Info.ArmamentNames.Contains(a.Info.Name))
				return;

			var entry = ChildEntries.Cast<MissileChildEntry>().FirstOrDefault(e => e.Stored && e.IsValid);
			if (entry == null)
				return;

			entry.Stored = false;
			entry.Readied = true;
			if (loadedTokens.Count > 0)
				self.RevokeCondition(loadedTokens.Pop());

			// Put up at the muzzle, facing the way the launcher faces.
			var position = self.CenterPosition + a.MuzzleOffset(self, barrel) + info.LaunchOffset;
			var child = entry.Actor;
			var facing = self.TraitOrDefault<IFacing>()?.Facing ?? WAngle.Zero;
			var salvoTarget = target;
			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead || child.IsDead)
					return;

				child.Trait<IPositionable>().SetCenterPosition(child, position);
				child.Trait<IFacing>().Facing = facing;
				w.Add(child);

				// With nothing left in store, the whole salvo takes off.
				if (!ChildEntries.Cast<MissileChildEntry>().Any(e => e.Stored && e.IsValid))
					Release(self, salvoTarget);
			});
		}

		void Release(Actor self, in Target target)
		{
			var elite = experience != null && experience.MaxLevel > 0 && experience.Level >= experience.MaxLevel;
			foreach (var entry in ChildEntries.Cast<MissileChildEntry>())
			{
				if (!entry.Readied || !entry.IsValid)
					continue;

				var rocket = entry.Actor.Trait<Rocket>();
				var rocketInfo = entry.Actor.Info.TraitInfo<RocketInfo>();
				rocket.Launch(self, target, elite);
				entry.Readied = false;
				entry.TakeoffTicks = rocketInfo.PauseTicks + rocketInfo.TiltTicks;
			}
		}

		void ITick.Tick(Actor self)
		{
			foreach (var entry in ChildEntries.Cast<MissileChildEntry>())
			{
				if (entry.Stored && entry.IsValid)
					continue;

				if (entry.Readied && entry.IsValid)
					continue;

				// Its missile has left the launcher (or was destroyed on it):
				// the slot lets go of it and stays empty for RespawnTicks.
				if (entry.Actor != null && (!entry.IsValid || --entry.TakeoffTicks <= 0))
					Empty(entry);

				if (entry.Actor == null && !Info.NoRegeneration && --entry.RegenTicks <= 0)
					Replenish(self, entry);
			}
		}

		void Empty(MissileChildEntry entry)
		{
			entry.Actor = null;
			entry.SpawnerChild = null;
			entry.Stored = false;
			entry.Readied = false;
			entry.RegenTicks = Info.RespawnTicks;
		}

		protected override void Killed(Actor self, AttackInfo e)
		{
			// Missiles in store are not on the map and go with the launcher; those launched fly on.
			foreach (var entry in ChildEntries.Cast<MissileChildEntry>())
				if (entry.Stored && entry.IsValid)
					entry.Actor.Dispose();
		}
	}
}
