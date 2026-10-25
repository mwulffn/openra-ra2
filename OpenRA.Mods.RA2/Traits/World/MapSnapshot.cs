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
using System.Numerics;
using System.Reflection;
using OpenRA.FileFormats;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.RA2.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Development aid: when the environment variable is set to a file name, zooms out to the whole map,",
		"saves the rendered world as a PNG and quits. Attach this to the world actor.")]
	public class MapSnapshotInfo : TraitInfo
	{
		[Desc("Environment variable that holds the PNG file name.")]
		public readonly string Variable = "OPENRA_MAP_SNAPSHOT";

		[Desc("Ticks to wait after zooming out before the world is saved.")]
		public readonly int Delay = 10;

		public override object Create(ActorInitializer init) { return new MapSnapshot(this); }
	}

	public class MapSnapshot : IWorldLoaded, ITick
	{
		readonly MapSnapshotInfo info;
		readonly string path;
		WorldRenderer worldRenderer;
		int ticks;

		public MapSnapshot(MapSnapshotInfo info)
		{
			this.info = info;
			path = Environment.GetEnvironmentVariable(info.Variable);
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			if (string.IsNullOrEmpty(path))
				return;

			worldRenderer = wr;
			if (w.LocalPlayer != null)
				w.LocalPlayer.Shroud.Disabled = true;
		}

		void ITick.Tick(Actor self)
		{
			if (worldRenderer == null)
				return;

			// The game centres the camera on the start units after the world is loaded: keep overriding it
			if (++ticks < info.Delay)
			{
				ShowWholeMap(self.World.Map);
				return;
			}

			if (ticks > info.Delay)
				return;

			// The renderer keeps the world framebuffer to itself; the last frame is still in it between frames.
			var field = typeof(Renderer).GetField("worldBuffer", BindingFlags.NonPublic | BindingFlags.Instance);
			var buffer = (IFrameBuffer)field.GetValue(Game.Renderer);
			var data = buffer.Texture.GetData();
			var bufferSize = Game.Renderer.WorldFrameBufferSize;

			// The frame's top left is the viewport's: cut the map out of it.
			var map = self.World.Map;
			var viewport = worldRenderer.Viewport;
			var origin = int2.FromVector(viewport.CenterLocation) - viewport.ViewportSize.ToInt2() / 2;
			var topLeft = int2.Max(worldRenderer.ScreenPxPosition(map.ProjectedTopLeft) - origin, int2.Zero);
			var bottomRight = int2.Min(worldRenderer.ScreenPxPosition(map.ProjectedBottomRight) - origin, bufferSize.ToInt2());
			var width = bottomRight.X - topLeft.X;
			var height = bottomRight.Y - topLeft.Y;
			var image = new byte[4 * width * height];
			for (var y = 0; y < height; y++)
				Array.Copy(data, 4 * ((topLeft.Y + y) * bufferSize.Width + topLeft.X), image, 4 * y * width, 4 * width);

			new Png(image, SpriteFrameType.Bgra32, width, height).Save(path);
			Console.WriteLine($"Saved map snapshot {width}x{height} to {path}");
			Game.Exit();
		}

		// Zoom out until the whole map is in the window: the world is then rendered into one framebuffer.
		// The viewport may not centre on the map's middle, so the zoom allows for where its centre ended up.
		void ShowWholeMap(Map map)
		{
			var viewport = worldRenderer.Viewport;
			viewport.Center(map.ProjectedTopLeft + (map.ProjectedBottomRight - map.ProjectedTopLeft) / 2);

			var center = viewport.CenterLocation;
			var topLeft = worldRenderer.ScreenPxPosition(map.ProjectedTopLeft).ToVector2();
			var bottomRight = worldRenderer.ScreenPxPosition(map.ProjectedBottomRight).ToVector2();
			var halfSize = Vector2.Max(center - topLeft, bottomRight - center) + new Vector2(1, 1);
			var native = Game.Renderer.NativeResolution;
			var zoom = Math.Min(native.Width / (2 * halfSize.X), native.Height / (2 * halfSize.Y));

			viewport.UnlockMinimumZoom(Math.Min(1f, zoom / viewport.MinZoom));
			viewport.AdjustZoom((float)Math.Log(zoom / viewport.Zoom));
		}
	}
}
