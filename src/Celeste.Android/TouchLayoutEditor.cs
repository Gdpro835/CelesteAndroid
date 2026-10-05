using System;
using Android.Content;
using Android.Graphics;
using Android.Views;

namespace CelesteAndroid
{
	/// <summary>
	/// Editor da posição dos controles de toque: mostra a área 16:9 do jogo e deixa arrastar cada
	/// controle. Guarda as posições normalizadas (0..1) dessa área — o mesmo espaço que o
	/// TouchControls usa para desenhar — então o resultado vale para qualquer tela.
	/// </summary>
	internal sealed class TouchLayoutEditor : View
	{
		private const int Stick = (int)TouchControl.Stick;
		private const int Jump = (int)TouchControl.Jump;
		private const int Dash = (int)TouchControl.Dash;
		private const int Grab = (int)TouchControl.Grab;
		private const int Pause = (int)TouchControl.Pause;

		private readonly float[] x = new float[TouchLayoutSpec.Count];
		private readonly float[] y = new float[TouchLayoutSpec.Count];
		private readonly float[] size = new float[TouchLayoutSpec.Count];
		private readonly RectF game = new();
		private readonly Paint background;
		private readonly Paint ring;
		private readonly Paint fill;
		private readonly Paint accent;
		private readonly Paint selected;
		private readonly Paint ink;

		private float scale = 1f;
		private int dragging = -1;

		public TouchLayoutEditor(Context context, TouchLayoutSpec spec) : base(context)
		{
			background = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(255, 24, 17, 44) };
			ring = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(230, 255, 255, 255) };
			ring.SetStyle(Paint.Style.Stroke);
			fill = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(230, 255, 255, 255) };
			accent = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(255, 242, 184, 216) };
			accent.SetStyle(Paint.Style.Stroke);
			selected = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(255, 242, 184, 216) };
			ink = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(255, 18, 12, 34) };
			ink.TextAlign = Paint.Align.Center;
			ink.Typeface = Typeface.DefaultBold!;
			Apply(spec);
		}

		/// <summary>Multiplicador global de tamanho (o launcher expõe isso como "A-"/"A+").</summary>
		public float Scale
		{
			get => scale;
			set
			{
				scale = Math.Clamp(value, 0.6f, 1.6f);
				Invalidate();
			}
		}

		/// <summary>Layout atual, no formato que o jogo entende.</summary>
		public TouchLayoutSpec Layout => new(BuildSpecs(), scale);

		public void Apply(TouchLayoutSpec spec)
		{
			for (int i = 0; i < TouchLayoutSpec.Count; i++)
			{
				TouchControlSpec control = spec[i];
				x[i] = control.X;
				y[i] = control.Y;
				size[i] = control.Size;
			}
			scale = spec.Scale;
			Invalidate();
		}

		/// <summary>Volta para o layout de fábrica.</summary>
		public void Reset() => Apply(TouchLayoutSpec.Default);

		/// <summary>Espelha na horizontal (esquerdino).</summary>
		public void Mirror()
		{
			for (int i = 0; i < TouchLayoutSpec.Count; i++)
			{
				x[i] = 1f - x[i];
			}
			Invalidate();
		}

		private TouchControlSpec[] BuildSpecs()
		{
			var values = new TouchControlSpec[TouchLayoutSpec.Count];
			for (int i = 0; i < TouchLayoutSpec.Count; i++)
			{
				values[i] = new TouchControlSpec(x[i], y[i], size[i]);
			}
			return values;
		}

		/// <summary>Área 16:9 dentro da view, com uma folga: é o que o jogo ocupa no aparelho.</summary>
		private void UpdateGameRect()
		{
			float padding = 10f * (Resources?.DisplayMetrics?.Density ?? 1f);
			float availableWidth = MathF.Max(1f, Width - padding * 2f);
			float availableHeight = MathF.Max(1f, Height - padding * 2f);
			float width = MathF.Min(availableWidth, availableHeight * 16f / 9f);
			float height = width * 9f / 16f;
			if (height > availableHeight)
			{
				height = availableHeight;
				width = height * 16f / 9f;
			}
			float centerX = Width / 2f;
			float centerY = Height / 2f;
			game.Set(centerX - width / 2f, centerY - height / 2f, centerX + width / 2f, centerY + height / 2f);
		}

		protected override void OnDraw(Canvas? canvas)
		{
			base.OnDraw(canvas);
			if (canvas == null || Width <= 0 || Height <= 0)
			{
				return;
			}
			UpdateGameRect();
			canvas.DrawRect(game.Left, game.Top, game.Right, game.Bottom, background);

			float density = Resources?.DisplayMetrics?.Density ?? 1f;
			ink.TextSize = game.Height() * 0.075f;

			// Manche: anel + bolinha, para o formato ser reconhecível sem legenda.
			Vector2Like stick = Center(Stick);
			float stickRadius = Radius(Stick);
			ring.StrokeWidth = MathF.Max(2f, 3f * density);
			canvas.DrawCircle(stick.X, stick.Y, stickRadius, dragging == Stick ? accent : ring);
			canvas.DrawCircle(stick.X, stick.Y, stickRadius * 0.41f, fill);

			DrawButton(canvas, Jump, "C");
			DrawButton(canvas, Dash, "X");
			DrawButton(canvas, Grab, "Z");
			DrawPause(canvas);
		}

		private void DrawButton(Canvas canvas, int index, string label)
		{
			Vector2Like center = Center(index);
			float radius = Radius(index);
			canvas.DrawCircle(center.X, center.Y, radius, dragging == index ? selected : fill);
			canvas.DrawText(label, center.X, center.Y - (ink.Descent() + ink.Ascent()) / 2f, ink);
		}

		private void DrawPause(Canvas canvas)
		{
			Vector2Like center = Center(Pause);
			float radius = Radius(Pause);
			canvas.DrawCircle(center.X, center.Y, radius, dragging == Pause ? selected : fill);
			float bar = radius * 0.20f;
			canvas.DrawRect(center.X - bar * 1.7f, center.Y - bar * 1.8f, center.X - bar * 0.7f, center.Y + bar * 1.8f, ink);
			canvas.DrawRect(center.X + bar * 0.7f, center.Y - bar * 1.8f, center.X + bar * 1.7f, center.Y + bar * 1.8f, ink);
		}

		private Vector2Like Center(int index) => new(game.Left + x[index] * game.Width(), game.Top + y[index] * game.Height());

		private float Radius(int index) => size[index] * game.Height() * scale;

		public override bool OnTouchEvent(MotionEvent? e)
		{
			if (e == null || e.Action == MotionEventActions.Cancel)
			{
				dragging = -1;
				return true;
			}
			if (Width <= 0 || Height <= 0)
			{
				return true;
			}
			UpdateGameRect();

			switch (e.Action)
			{
				case MotionEventActions.Down:
					dragging = FindControl(e.GetX(), e.GetY());
					Invalidate();
					return true;

				case MotionEventActions.Move:
					if (dragging >= 0)
					{
						Drag(dragging, e.GetX(), e.GetY());
						Invalidate();
					}
					return true;

				case MotionEventActions.Up:
					dragging = -1;
					Invalidate();
					return true;
			}
			return base.OnTouchEvent(e);
		}

		private int FindControl(float touchX, float touchY)
		{
			int found = -1;
			float best = float.MaxValue;
			float minReach = game.Height() * 0.12f;
			for (int i = 0; i < TouchLayoutSpec.Count; i++)
			{
				Vector2Like center = Center(i);
				float distance = MathF.Sqrt((touchX - center.X) * (touchX - center.X) + (touchY - center.Y) * (touchY - center.Y));
				float reach = MathF.Max(Radius(i) * 1.6f, minReach);
				if (distance <= reach && distance < best)
				{
					found = i;
					best = distance;
				}
			}
			return found;
		}

		private void Drag(int index, float touchX, float touchY)
		{
			float radius = Radius(index);
			float marginX = game.Width() > 0f ? radius / game.Width() : 0f;
			float marginY = game.Height() > 0f ? radius / game.Height() : 0f;
			x[index] = Math.Clamp((touchX - game.Left) / game.Width(), marginX, 1f - marginX);
			y[index] = Math.Clamp((touchY - game.Top) / game.Height(), marginY, 1f - marginY);
		}

		/// <summary>Par de floats: evita depender de tipos do FNA dentro do app.</summary>
		private readonly struct Vector2Like
		{
			public readonly float X;
			public readonly float Y;

			public Vector2Like(float x, float y)
			{
				X = x;
				Y = y;
			}
		}
	}
}
