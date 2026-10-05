using System;
using System.Collections.Generic;
using CelesteAndroid;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CelesteAndroid.Touch
{
	/// <summary>
	/// On-screen pad: an analog stick that walks with the arrow keys plus Z (grab), X (dash),
	/// C (jump) and Escape (pause) — the keys Celeste ships bound by default. Presses are pushed
	/// into SDL's queue and FNA turns them into <c>Keyboard.keys</c> at the start of the next tick
	/// (see <see cref="SdlInput"/>).
	///
	/// It is drawn over the game image from the render path in Monocle/patch_Engine.cs and fades
	/// out after a few seconds without touches, so a real controller still gets a clean screen.
	/// </summary>
	internal sealed class TouchControls
	{
		#region Layout (fractions of the game viewport height)

		private const float StickRadiusRatio = 0.150f;
		private const float KnobRadiusRatio = 0.062f;
		private const float ButtonRadiusRatio = 0.082f;
		private const float PauseRadiusRatio = 0.050f;

		/// <summary>How far the stick has to move to press a direction, and to release it again
		/// (the gap keeps a finger resting on the threshold from flickering).</summary>
		private const float DirectionOn = 0.50f;
		private const float DirectionOff = 0.30f;

		private const float HoldSeconds = 6f;
		private const float FadeSeconds = 0.8f;

		#endregion

		private const int MaxFingers = 10;

		private enum Control
		{
			None,
			Stick,
			Jump,   // C
			Dash,   // X
			Grab,   // Z
			Pause,  // Escape
		}

		private static readonly GameKey[] AllKeys =
		{
			GameKey.C, GameKey.X, GameKey.Z, GameKey.Escape,
			GameKey.Left, GameKey.Right, GameKey.Up, GameKey.Down,
		};

		private readonly record struct Grab(long FingerId, Control Target);

		private readonly List<SdlInput.Finger> fingers = new();
		private readonly Grab[] grabs = new Grab[MaxFingers];
		private readonly bool[] keyDown = new bool[8];
		private readonly bool[] directionDown = new bool[4];
		private int grabCount;

		private Vector2 stickOrigin;
		private Vector2 stick;
		private long lastTouchMs = Environment.TickCount64;

		private GraphicsDevice? device;
		private SpriteBatch? batch;
		private Texture2D? circle;
		private Texture2D? pixel;

		#region Update

		/// <summary>Reads the touches, presses/releases the pad keys and draws the pad.</summary>
		public void UpdateAndDraw(GraphicsDevice graphicsDevice, Viewport viewport)
		{
			if (!HostConfig.TouchControlsEnabled)
			{
				// Turning the setting off must not leave a key held down in the game.
				ReleaseAllKeys();
				return;
			}

			PadLayout layout = new(
				viewport,
				graphicsDevice.PresentationParameters.BackBufferWidth,
				graphicsDevice.PresentationParameters.BackBufferHeight
			);
			if (layout.Height <= 0f || layout.BackWidth <= 0f || layout.BackHeight <= 0f)
			{
				return;
			}

			EnsureResources(graphicsDevice);

			int fingerCount = SdlInput.PollFingers(fingers);
			long now = Environment.TickCount64;

			// The pad fades out when nobody touches the screen (playing with a controller).
			// In that case the touch that brings it back doesn't press anything: a brush against
			// the screen shouldn't dash. Holding the finger down presses normally from the next
			// frame on, because the pad is already back.
			float alpha = MathHelper.Clamp(
				1f - ((now - lastTouchMs) / 1000f - HoldSeconds) / FadeSeconds,
				0f, 1f
			);
			bool wasHidden = alpha <= 0f && fingerCount > 0;
			if (fingerCount > 0)
			{
				lastTouchMs = now;
			}
			if (wasHidden)
			{
				grabCount = 0;
				ReleaseAllKeys();
				Draw(graphicsDevice, layout, 1f);
				return;
			}

			Assign(fingerCount, layout);
			UpdateStick(layout);
			UpdateButtons();

			Draw(graphicsDevice, layout, alpha);
		}

		private void ReleaseAllKeys()
		{
			foreach (GameKey key in AllKeys)
			{
				SetKey(key, false);
			}
			for (int i = 0; i < directionDown.Length; i++)
			{
				directionDown[i] = false;
			}
		}

		private void Assign(int fingerCount, PadLayout layout)
		{
			// Fingers that are still down keep the control they grabbed.
			int kept = 0;
			for (int i = 0; i < grabCount; i++)
			{
				if (FindFinger(grabs[i].FingerId) >= 0)
				{
					grabs[kept++] = grabs[i];
				}
			}
			grabCount = kept;

			for (int i = 0; i < fingerCount && grabCount < MaxFingers; i++)
			{
				long id = unchecked((long)fingers[i].Id);
				if (FindGrab(id) >= 0)
				{
					continue;
				}
				Vector2 point = ToPixels(fingers[i], layout);
				Control target = layout.HitTest(point, stickTaken: FindControl(Control.Stick) >= 0);
				if (target == Control.None)
				{
					continue;
				}
				grabs[grabCount++] = new Grab(id, target);
				if (target == Control.Stick)
				{
					// The stick floats: its centre is wherever the finger landed.
					stickOrigin = point;
				}
			}
		}

		private void UpdateStick(PadLayout layout)
		{
			stick = Vector2.Zero;
			int grab = FindControl(Control.Stick);
			if (grab >= 0)
			{
				int finger = FindFinger(grabs[grab].FingerId);
				if (finger >= 0)
				{
					Vector2 delta = ToPixels(fingers[finger], layout) - stickOrigin;
					float length = delta.Length();
					float max = layout.StickRadius;
					stick = length > max && length > 0f ? delta * (max / length) : delta;
				}
			}

			float on = layout.StickRadius * DirectionOn;
			float off = layout.StickRadius * DirectionOff;
			SetDirection(0, GameKey.Left, -stick.X, on, off);
			SetDirection(1, GameKey.Right, stick.X, on, off);
			SetDirection(2, GameKey.Up, -stick.Y, on, off);
			SetDirection(3, GameKey.Down, stick.Y, on, off);
		}

		private void UpdateButtons()
		{
			SetKey(GameKey.C, FindControl(Control.Jump) >= 0);
			SetKey(GameKey.X, FindControl(Control.Dash) >= 0);
			SetKey(GameKey.Z, FindControl(Control.Grab) >= 0);
			SetKey(GameKey.Escape, FindControl(Control.Pause) >= 0);
		}

		private void SetDirection(int index, GameKey key, float value, float on, float off)
		{
			bool down = directionDown[index] ? value > off : value > on;
			directionDown[index] = down;
			SetKey(key, down);
		}

		private void SetKey(GameKey key, bool pressed)
		{
			int index = (int)key;
			if (keyDown[index] == pressed)
			{
				return;
			}
			keyDown[index] = pressed;
			if (!SdlInput.PushKey(key, pressed))
			{
				FNALoggerEXT.LogWarn?.Invoke("TouchControls: SDL rejected the key event for " + key.Label());
			}
		}

		#endregion

		#region Fingers

		private int FindFinger(long fingerId)
		{
			for (int i = 0; i < fingers.Count; i++)
			{
				if (unchecked((long)fingers[i].Id) == fingerId)
				{
					return i;
				}
			}
			return -1;
		}

		private int FindGrab(long fingerId)
		{
			for (int i = 0; i < grabCount; i++)
			{
				if (grabs[i].FingerId == fingerId)
				{
					return i;
				}
			}
			return -1;
		}

		private int FindControl(Control control)
		{
			for (int i = 0; i < grabCount; i++)
			{
				if (grabs[i].Target == control)
				{
					return i;
				}
			}
			return -1;
		}

		/// <summary>SDL reports the finger position normalized to the window (0..1).</summary>
		private static Vector2 ToPixels(SdlInput.Finger finger, PadLayout layout) =>
			new(finger.X * layout.BackWidth, finger.Y * layout.BackHeight);

		#endregion

		#region Drawing

		private void EnsureResources(GraphicsDevice graphicsDevice)
		{
			if (device == graphicsDevice && IsUsable(batch) && IsUsable(circle) && IsUsable(pixel))
			{
				return;
			}
			DisposeResources();
			device = graphicsDevice;
			batch = new SpriteBatch(graphicsDevice);
			circle = CreateCircleTexture(graphicsDevice, 128);
			pixel = new Texture2D(graphicsDevice, 1, 1);
			pixel.SetData(new[] { Color.White });
		}

		/// <summary>Textures die with the device (Android recreates the window on resume).</summary>
		private static bool IsUsable(GraphicsResource? resource) => resource != null && !resource.IsDisposed;

		private void DisposeResources()
		{
			batch?.Dispose();
			circle?.Dispose();
			pixel?.Dispose();
			batch = null;
			circle = null;
			pixel = null;
			device = null;
		}

		private void Draw(GraphicsDevice graphicsDevice, PadLayout layout, float alpha)
		{
			if (alpha <= 0f || batch == null || circle == null || pixel == null)
			{
				return;
			}

			// The pads are laid out in screen space, so draw with the full backbuffer viewport
			// (SpriteBatch builds its projection from the device viewport).
			Viewport previous = graphicsDevice.Viewport;
			graphicsDevice.Viewport = new Viewport(0, 0, (int)layout.BackWidth, (int)layout.BackHeight);
			try
			{
				SpriteBatch spriteBatch = batch;
				Texture2D pixelTexture = pixel;
				spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, null, null);

				// Stick: a ring with a knob that follows the finger.
				bool stickActive = FindControl(Control.Stick) >= 0;
				DrawCircle(spriteBatch, stickOrigin, layout.StickRadius, Premul(255, 255, 255, (stickActive ? 0.26f : 0.16f) * alpha));
				DrawCircle(spriteBatch, stickOrigin, layout.StickRadius * 0.86f, Premul(18, 12, 34, (stickActive ? 0.34f : 0.22f) * alpha));
				DrawCircle(spriteBatch, stickOrigin + stick, layout.KnobRadius, Premul(255, 255, 255, (stickActive ? 0.55f : 0.30f) * alpha));

				DrawButton(spriteBatch, layout.Jump, layout.ButtonRadius, GlyphC, FindControl(Control.Jump) >= 0, alpha);
				DrawButton(spriteBatch, layout.Dash, layout.ButtonRadius, GlyphX, FindControl(Control.Dash) >= 0, alpha);
				DrawButton(spriteBatch, layout.Grab, layout.ButtonRadius, GlyphZ, FindControl(Control.Grab) >= 0, alpha);

				// Pause: small, out of the way, in the top right corner of the game image.
				bool paused = FindControl(Control.Pause) >= 0;
				DrawCircle(spriteBatch, layout.Pause, layout.PauseRadius, Premul(255, 255, 255, (paused ? 0.9f : 0.5f) * alpha));
				DrawCircle(spriteBatch, layout.Pause, layout.PauseRadius * 0.84f, Premul(18, 12, 34, (paused ? 0.5f : 0.35f) * alpha));
				float bar = layout.PauseRadius * 0.20f;
				Color ink = Premul(255, 255, 255, (paused ? 0.95f : 0.85f) * alpha);
				spriteBatch.Draw(pixelTexture, new Rectangle((int)(layout.Pause.X - bar * 1.7f), (int)(layout.Pause.Y - bar * 1.8f), (int)bar, (int)(bar * 3.6f)), ink);
				spriteBatch.Draw(pixelTexture, new Rectangle((int)(layout.Pause.X + bar * 0.7f), (int)(layout.Pause.Y - bar * 1.8f), (int)bar, (int)(bar * 3.6f)), ink);

				spriteBatch.End();
			}
			finally
			{
				graphicsDevice.Viewport = previous;
			}
		}

		private void DrawButton(SpriteBatch spriteBatch, Vector2 center, float radius, uint[] glyph, bool pressed, float alpha)
		{
			Color ring = pressed ? Premul(242, 184, 216, 0.95f * alpha) : Premul(255, 255, 255, 0.45f * alpha);
			Color fill = pressed ? Premul(242, 184, 216, 0.42f * alpha) : Premul(18, 12, 34, 0.42f * alpha);
			Color ink = pressed ? Premul(18, 12, 34, 0.95f * alpha) : Premul(255, 255, 255, 0.88f * alpha);
			DrawCircle(spriteBatch, center, radius, ring);
			DrawCircle(spriteBatch, center, radius * 0.86f, fill);
			DrawGlyph(spriteBatch, glyph, center, radius * 0.95f, ink);
		}

		private void DrawCircle(SpriteBatch spriteBatch, Vector2 center, float radius, Color color)
		{
			Texture2D texture = circle!;
			spriteBatch.Draw(
				texture,
				center,
				null,
				color,
				0f,
				new Vector2(texture.Width / 2f),
				radius * 2f / texture.Width,
				SpriteEffects.None,
				0f
			);
		}

		/// <summary>Letters are a 5x5 bitmap font, drawn as small squares (no content files needed).</summary>
		private void DrawGlyph(SpriteBatch spriteBatch, uint[] glyph, Vector2 center, float size, Color color)
		{
			float cell = MathF.Max(1f, MathF.Round(size / 5f));
			float half = cell * 2.5f;
			float x0 = MathF.Round(center.X - half);
			float y0 = MathF.Round(center.Y - half);
			for (int y = 0; y < 5; y++)
			{
				uint row = glyph[y];
				for (int x = 0; x < 5; x++)
				{
					if ((row & (1u << (4 - x))) == 0)
					{
						continue;
					}
					spriteBatch.Draw(
						pixel!,
						new Rectangle((int)(x0 + x * cell), (int)(y0 + y * cell), (int)cell, (int)cell),
						color
					);
				}
			}
		}

		private static readonly uint[] GlyphC = { 0b01111, 0b10000, 0b10000, 0b10000, 0b01111 };
		private static readonly uint[] GlyphX = { 0b10001, 0b01010, 0b00100, 0b01010, 0b10001 };
		private static readonly uint[] GlyphZ = { 0b11111, 0b00010, 0b00100, 0b01000, 0b11111 };

		private static Texture2D CreateCircleTexture(GraphicsDevice graphicsDevice, int size)
		{
			var texture = new Texture2D(graphicsDevice, size, size);
			var pixels = new Color[size * size];
			float radius = size / 2f;
			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					float dx = x + 0.5f - radius;
					float dy = y + 0.5f - radius;
					float coverage = MathHelper.Clamp(radius - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
					// Premultiplied white: AlphaBlend multiplies it by the tint.
					pixels[y * size + x] = new Color(coverage, coverage, coverage, coverage);
				}
			}
			texture.SetData(pixels);
			return texture;
		}

		/// <summary>Opaque RGB + opacity, in the premultiplied form SpriteBatch expects.</summary>
		private static Color Premul(int red, int green, int blue, float alpha) =>
			new(red / 255f * alpha, green / 255f * alpha, blue / 255f * alpha, alpha);

		#endregion

		#region Geometry

		private readonly struct PadLayout
		{
			public readonly float BackWidth;
			public readonly float BackHeight;
			public readonly float Height;
			public readonly float StickRadius;
			public readonly float KnobRadius;
			public readonly float ButtonRadius;
			public readonly float PauseRadius;
			public readonly Vector2 StickHome;
			public readonly Vector2 Jump;
			public readonly Vector2 Dash;
			public readonly Vector2 Grab;
			public readonly Vector2 Pause;

			private readonly float viewportLeft;
			private readonly float viewportWidth;

			public PadLayout(Viewport viewport, int backBufferWidth, int backBufferHeight)
			{
				BackWidth = backBufferWidth;
				BackHeight = backBufferHeight;
				Height = viewport.Height;

				StickRadius = StickRadiusRatio * viewport.Height;
				KnobRadius = KnobRadiusRatio * viewport.Height;
				ButtonRadius = ButtonRadiusRatio * viewport.Height;
				PauseRadius = PauseRadiusRatio * viewport.Height;

				float left = viewport.X;
				float top = viewport.Y;
				float right = viewport.X + viewport.Width;
				float bottom = viewport.Y + viewport.Height;
				viewportLeft = left;
				viewportWidth = viewport.Width;

				StickHome = new Vector2(left + StickRadius * 1.45f, bottom - StickRadius * 1.45f);
				Jump = new Vector2(right - ButtonRadius * 1.35f, bottom - ButtonRadius * 1.35f);
				Dash = new Vector2(right - ButtonRadius * 3.85f, bottom - ButtonRadius * 1.35f);
				Grab = new Vector2(right - ButtonRadius * 1.35f, bottom - ButtonRadius * 3.85f);
				Pause = new Vector2(right - PauseRadius * 1.7f, top + PauseRadius * 1.7f);
			}

			public Control HitTest(Vector2 point, bool stickTaken)
			{
				float reach = ButtonRadius * 1.15f;
				if (Vector2.DistanceSquared(point, Jump) <= reach * reach) return Control.Jump;
				if (Vector2.DistanceSquared(point, Dash) <= reach * reach) return Control.Dash;
				if (Vector2.DistanceSquared(point, Grab) <= reach * reach) return Control.Grab;
				float pauseReach = PauseRadius * 1.3f;
				if (Vector2.DistanceSquared(point, Pause) <= pauseReach * pauseReach) return Control.Pause;

				// The stick owns the left half of the game image, wherever the finger lands.
				if (!stickTaken && point.X < viewportLeft + viewportWidth * 0.5f)
				{
					return Control.Stick;
				}
				return Control.None;
			}
		}

		#endregion
	}
}
