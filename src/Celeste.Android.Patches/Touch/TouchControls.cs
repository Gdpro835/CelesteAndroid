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
	/// out after a few seconds without touches, so a real controller still gets a clean screen —
	/// except the pause button, which stays visible: someone playing with the touch pad alone has
	/// no other way to open the menu. Positions and sizes come from <see cref="TouchLayoutSpec"/>
	/// (the launcher has an editor for them).
	/// </summary>
	internal sealed class TouchControls
	{
		#region Layout constants

		/// <summary>How far the stick has to move to press a direction, and to release it again
		/// (the gap keeps a finger resting on the threshold from flickering).</summary>
		private const float DirectionOn = 0.50f;
		private const float DirectionOff = 0.30f;

		/// <summary>Knob radius, as a fraction of the stick radius.</summary>
		private const float KnobRatio = 0.41f;

		/// <summary>The stick floats inside this box around its place on screen (radio do manche).</summary>
		private const float StickZoneRatio = 2.6f;

		/// <summary>Extra reach of a button hit test, as a fraction of its radius.</summary>
		private const float ButtonReach = 1.15f;

		/// <summary>Alpha da pausa quando o resto do pad já apagou.</summary>
		private const float PauseIdleAlpha = 0.35f;

		private const float HoldSeconds = 6f;
		private const float FadeSeconds = 0.8f;

		#endregion

		private const int MaxFingers = 10;

		/// <summary>
		/// Valores iguais aos de <see cref="TouchControl"/> (o layout é indexado por eles);
		/// <see cref="None"/> é o "nenhum controle".
		/// </summary>
		private enum Control
		{
			None = -1,
			Stick = (int)TouchControl.Stick,
			Jump = (int)TouchControl.Jump,
			Dash = (int)TouchControl.Dash,
			Grab = (int)TouchControl.Grab,
			Pause = (int)TouchControl.Pause,
			Journal = (int)TouchControl.Journal,
			Retry = (int)TouchControl.Retry,
		}

		private static readonly GameKey[] AllKeys =
		{
			GameKey.C, GameKey.X, GameKey.Z, GameKey.Escape, GameKey.Tab, GameKey.R,
			GameKey.Left, GameKey.Right, GameKey.Up, GameKey.Down,
		};

		private readonly record struct Grab(long FingerId, Control Target);

		private readonly List<SdlInput.Finger> fingers = new();
		private readonly Grab[] grabs = new Grab[MaxFingers];
		// Um estado por GameKey: a lista cresce (Tab, R) e um tamanho fixo estouraria aqui.
		private readonly bool[] keyDown = new bool[Enum.GetValues<GameKey>().Length];
		private readonly bool[] directionDown = new bool[4];
		private int grabCount;

		private Vector2 stickOrigin;
		private Vector2 stick;
		private long lastTouchMs = Environment.TickCount64;

		private string? layoutText;
		private TouchLayoutSpec layout = TouchLayoutSpec.Default;

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

			// O editor do launcher muda a posição dos controles: relê quando o host publica outro layout.
			RefreshLayout();

			PadLayout pad = new(
				viewport,
				graphicsDevice.PresentationParameters.BackBufferWidth,
				graphicsDevice.PresentationParameters.BackBufferHeight,
				layout
			);
			if (pad.Height <= 0f || pad.BackWidth <= 0f || pad.BackHeight <= 0f)
			{
				return;
			}

			EnsureResources(graphicsDevice);

			int fingerCount = SdlInput.PollFingers(fingers);
			long now = Environment.TickCount64;

			// The pad fades out when nobody touches the screen (playing with a controller).
			float alpha = MathHelper.Clamp(
				1f - ((now - lastTouchMs) / 1000f - HoldSeconds) / FadeSeconds,
				0f, 1f
			);
			bool wake = alpha <= 0f && fingerCount > 0;
			if (fingerCount > 0)
			{
				lastTouchMs = now;
			}

			if (wake)
			{
				// The touch that brings the pad back doesn't press anything: a brush against the
				// screen shouldn't dash. The pause button is the exception — it has to work on the
				// first try, otherwise a stick-less player can't open the menu.
				grabCount = 0;
				ReleaseAllKeys();
				Assign(fingerCount, pad, pauseOnly: true);
				UpdateButtons();
				Draw(graphicsDevice, pad, 1f);
				return;
			}

			Assign(fingerCount, pad, pauseOnly: false);
			UpdateStick(pad);
			UpdateButtons();

			Draw(graphicsDevice, pad, alpha);
		}

		private void RefreshLayout()
		{
			string? text = HostConfig.TouchLayoutSetting;
			if (text == layoutText)
			{
				return;
			}
			layoutText = text;
			// Texto ausente/estragado cai no layout de fábrica, nunca deixa o pad sem controles.
			layout = TouchLayoutSpec.TryParse(text, out TouchLayoutSpec parsed) ? parsed : TouchLayoutSpec.Default;
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

		private void Assign(int fingerCount, PadLayout layout, bool pauseOnly)
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
				if (pauseOnly && target != Control.Pause)
				{
					// Só a pausa funciona no toque que acorda o pad.
					continue;
				}
				grabs[grabCount++] = new Grab(id, target);
				if (target == Control.Stick)
				{
					// The stick floats: its centre is wherever the finger landed (inside the game image).
					stickOrigin = layout.ClampToGame(point);
				}
			}
		}

		private void UpdateStick(PadLayout layout)
		{
			stick = Vector2.Zero;
			int grab = FindControl(Control.Stick);
			if (grab < 0)
			{
				// Sem dedo no manche: desenha no lugar configurado (não em 0,0).
				stickOrigin = layout.Center(TouchControl.Stick);
			}
			else
			{
				int finger = FindFinger(grabs[grab].FingerId);
				if (finger >= 0)
				{
					Vector2 delta = ToPixels(fingers[finger], layout) - stickOrigin;
					float length = delta.Length();
					float max = layout.Radius(TouchControl.Stick);
					stick = length > max && length > 0f ? delta * (max / length) : delta;
				}
			}

			float on = layout.Radius(TouchControl.Stick) * DirectionOn;
			float off = layout.Radius(TouchControl.Stick) * DirectionOff;
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
			SetKey(GameKey.Tab, FindControl(Control.Journal) >= 0);
			SetKey(GameKey.R, FindControl(Control.Retry) >= 0);
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
			// A pausa continua no ecrã (mais apagada) quando o resto do pad já se escondeu.
			float pauseAlpha = MathF.Max(alpha, PauseIdleAlpha);
			if (pauseAlpha <= 0f || batch == null || circle == null || pixel == null)
			{
				return;
			}

			// The pads are laid out in screen space, so draw with the full backbuffer viewport
			// (SpriteBatch builds its projection from the device viewport).
			Viewport previous = graphicsDevice.Viewport;
			graphicsDevice.Viewport = new Viewport(0, 0, (int)layout.BackWidth, (int)layout.BackHeight);
			try
			{
				SpriteBatch spriteBatch = batch!;
				Texture2D pixelTexture = pixel!;
				spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, null, null);

				if (alpha > 0f)
				{
					// Stick: a ring with a knob that follows the finger.
					bool stickActive = FindControl(Control.Stick) >= 0;
					float stickRadius = layout.Radius(TouchControl.Stick);
					DrawCircle(spriteBatch, stickOrigin, stickRadius, Premul(255, 255, 255, (stickActive ? 0.26f : 0.16f) * alpha));
					DrawCircle(spriteBatch, stickOrigin, stickRadius * 0.86f, Premul(18, 12, 34, (stickActive ? 0.34f : 0.22f) * alpha));
					DrawCircle(spriteBatch, stickOrigin + stick, stickRadius * KnobRatio, Premul(255, 255, 255, (stickActive ? 0.55f : 0.30f) * alpha));

					DrawButton(spriteBatch, layout, TouchControl.Jump, GlyphC, alpha);
					DrawButton(spriteBatch, layout, TouchControl.Dash, GlyphX, alpha);
					DrawButton(spriteBatch, layout, TouchControl.Grab, GlyphZ, alpha);
					DrawButton(spriteBatch, layout, TouchControl.Journal, GlyphT, alpha);
					DrawButton(spriteBatch, layout, TouchControl.Retry, GlyphR, alpha);
				}

				// Pausa: pequena, no canto, e sempre visível (ver PauseIdleAlpha).
				Vector2 pause = layout.Center(TouchControl.Pause);
				float pauseRadius = layout.Radius(TouchControl.Pause);
				bool paused = FindControl(Control.Pause) >= 0;
				DrawCircle(spriteBatch, pause, pauseRadius, Premul(255, 255, 255, (paused ? 0.9f : 0.5f) * pauseAlpha));
				DrawCircle(spriteBatch, pause, pauseRadius * 0.84f, Premul(18, 12, 34, (paused ? 0.5f : 0.35f) * pauseAlpha));
				float bar = pauseRadius * 0.20f;
				Color ink = Premul(255, 255, 255, (paused ? 0.95f : 0.85f) * pauseAlpha);
				spriteBatch.Draw(pixelTexture, new Rectangle((int)(pause.X - bar * 1.7f), (int)(pause.Y - bar * 1.8f), (int)bar, (int)(bar * 3.6f)), ink);
				spriteBatch.Draw(pixelTexture, new Rectangle((int)(pause.X + bar * 0.7f), (int)(pause.Y - bar * 1.8f), (int)bar, (int)(bar * 3.6f)), ink);

				spriteBatch.End();
			}
			finally
			{
				graphicsDevice.Viewport = previous;
			}
		}

		private void DrawButton(SpriteBatch spriteBatch, PadLayout layout, TouchControl control, uint[] glyph, float alpha)
		{
			Vector2 center = layout.Center(control);
			float radius = layout.Radius(control);
			bool pressed = FindControl((Control)(int)control) >= 0;
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
		private static readonly uint[] GlyphT = { 0b11111, 0b00100, 0b00100, 0b00100, 0b00100 };
		private static readonly uint[] GlyphR = { 0b11110, 0b10001, 0b11110, 0b10010, 0b10001 };

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

			private readonly float[] centersX;
			private readonly float[] centersY;
			private readonly float[] radii;
			private readonly float left;
			private readonly float top;
			private readonly float right;
			private readonly float bottom;

			public PadLayout(Viewport viewport, int backBufferWidth, int backBufferHeight, TouchLayoutSpec spec)
			{
				BackWidth = backBufferWidth;
				BackHeight = backBufferHeight;
				Height = viewport.Height;
				left = viewport.X;
				top = viewport.Y;
				right = viewport.X + viewport.Width;
				bottom = viewport.Y + viewport.Height;

				int count = TouchLayoutSpec.Count;
				centersX = new float[count];
				centersY = new float[count];
				radii = new float[count];
				for (int i = 0; i < count; i++)
				{
					TouchControlSpec control = spec[i];
					centersX[i] = left + control.X * viewport.Width;
					centersY[i] = top + control.Y * viewport.Height;
					radii[i] = control.Size * viewport.Height * spec.Scale;
				}
			}

			public Vector2 Center(TouchControl control) => new(centersX[(int)control], centersY[(int)control]);

			public float Radius(TouchControl control) => radii[(int)control];

			/// <summary>Trava um ponto de toque dentro da imagem do jogo (o manche flutua até aí).</summary>
			public Vector2 ClampToGame(Vector2 point)
			{
				float margin = MathF.Max(1f, Radius(TouchControl.Stick));
				return new Vector2(
					MathHelper.Clamp(point.X, left + margin, right - margin),
					MathHelper.Clamp(point.Y, top + margin, bottom - margin)
				);
			}

			public Control HitTest(Vector2 point, bool stickTaken)
			{
				if (Hits(point, TouchControl.Pause)) return Control.Pause;
				if (Hits(point, TouchControl.Jump)) return Control.Jump;
				if (Hits(point, TouchControl.Dash)) return Control.Dash;
				if (Hits(point, TouchControl.Grab)) return Control.Grab;
				if (Hits(point, TouchControl.Journal)) return Control.Journal;
				if (Hits(point, TouchControl.Retry)) return Control.Retry;

				// O manche agarra dentro de uma caixa em volta do lugar configurado e flutua a partir daí.
				if (!stickTaken)
				{
					float zone = Radius(TouchControl.Stick) * StickZoneRatio;
					Vector2 home = Center(TouchControl.Stick);
					if (MathF.Abs(point.X - home.X) <= zone && MathF.Abs(point.Y - home.Y) <= zone)
					{
						return Control.Stick;
					}
				}
				return Control.None;
			}

			private bool Hits(Vector2 point, TouchControl control)
			{
				float reach = Radius(control) * ButtonReach;
				return Vector2.DistanceSquared(point, Center(control)) <= reach * reach;
			}
		}

		#endregion
	}
}
