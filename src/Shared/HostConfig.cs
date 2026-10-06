using System;
using System.Globalization;

namespace CelesteAndroid
{
	/// <summary>
	/// Contrato entre o host (app Android ou Celeste.Desktop) e o jogo já patcheado.
	/// <para>
	/// O jogo é carregado em runtime, então o host não pode referenciar o Celeste.dll: tudo passa
	/// por <see cref="AppContext"/> — o host publica com <see cref="Publish" /> e o jogo lê as
	/// propriedades. Este arquivo é compilado dentro de cada assembly (projeto compartilhado por
	/// item <c>Compile</c>, sem ProjectReference) justamente para não criar uma quarta assembly
	/// que o carregador do Android teria de resolver: os valores atravessam o AppContext, nunca
	/// referências entre assemblies.
	/// </para>
	/// </summary>
	public static class HostConfig
	{
		public const string PlatformKey = "CelesteAndroid.Platform";
		public const string PrefPathKey = "CelesteAndroid.PrefPath";
		public const string BackgroundPathKey = "CelesteAndroid.BackgroundPath";
		public const string TouchControlsKey = "CelesteAndroid.TouchControls";
		public const string TouchLayoutKey = "CelesteAndroid.TouchLayout";

		/// <summary>Lado do host: publica a configuração lida pelo jogo patcheado.</summary>
		/// <param name="backgroundPath">Imagem das faixas laterais (opcional; o desktop não usa).</param>
		/// <param name="touchControls">Controles na tela (ver TouchControls no módulo de patches).</param>
		/// <param name="touchLayout">Posições dos controles (ver <see cref="TouchLayoutSpec"/>); opcional.</param>
		public static void Publish(
			string platform,
			string prefPath,
			string? backgroundPath = null,
			bool touchControls = false,
			string? touchLayout = null
		)
		{
			AppContext.SetData(PlatformKey, platform);
			AppContext.SetData(PrefPathKey, prefPath);
			if (backgroundPath != null)
			{
				AppContext.SetData(BackgroundPathKey, backgroundPath);
			}
			AppContext.SetData(TouchControlsKey, touchControls ? "1" : "0");
			if (touchLayout != null)
			{
				AppContext.SetData(TouchLayoutKey, touchLayout);
			}
		}

		/// <summary>Lado do jogo: plataforma que o shim do SDL2 reporta.</summary>
		public static string Platform => AppContext.GetData(PlatformKey) as string ?? "Android";

		/// <summary>Imagem para as faixas laterais em telas mais largas que 16:9 (opcional).</summary>
		public static string? BackgroundPath => AppContext.GetData(BackgroundPathKey) as string;

		/// <summary>Controles na tela (toque). Só ligam se o host mandar "1" (ver TouchControls).</summary>
		public static bool TouchControlsEnabled => AppContext.GetData(TouchControlsKey) as string == "1";

		/// <summary>Posições dos controles de toque; nulo/vazio = layout padrão.</summary>
		public static string? TouchLayoutSetting => AppContext.GetData(TouchLayoutKey) as string;

		public static string PrefPath => AppContext.GetData(PrefPathKey) as string
			?? throw new InvalidOperationException($"{PrefPathKey} não foi definido pelo host.");
	}

	/// <summary>
	/// Controles do pad na tela, na ordem em que a posição é guardada. Os valores também são os
	/// índices usados por <see cref="TouchLayoutSpec"/>.
	/// </summary>
	public enum TouchControl
	{
		Stick = 0,
		Jump = 1,
		Dash = 2,
		Grab = 3,
		Pause = 4,
		Journal = 5,
		Retry = 6,
		Keyboard = 7,
	}

	/// <summary>Posição e tamanho de um controle, normalizados pela área do jogo (0..1).</summary>
	public readonly struct TouchControlSpec
	{
		/// <summary>Centro na horizontal, 0 = borda esquerda da área do jogo, 1 = direita.</summary>
		public readonly float X;

		/// <summary>Centro na vertical, 0 = topo da área do jogo, 1 = base.</summary>
		public readonly float Y;

		/// <summary>Raio como fração da altura da área do jogo.</summary>
		public readonly float Size;

		public TouchControlSpec(float x, float y, float size)
		{
			X = x;
			Y = y;
			Size = size;
		}
	}

	/// <summary>
	/// Layout dos controles de toque. Compartilhado entre o editor (no launcher) e o desenho (no
	/// módulo de patches) para não existirem duas definições dele. As posições são relativas à
	/// área do jogo (16:9), então valem para qualquer tela; <see cref="Encode"/> gera o texto
	/// guardado em SharedPreferences e <see cref="TryParse"/> lê qualquer coisa, caindo no
	/// <see cref="Default"/> quando o texto está vazio ou fora do formato.
	/// </summary>
	public sealed class TouchLayoutSpec
	{
		public const int Count = 8;
		private const string Version = "v1";
		private const float MinScale = 0.6f;
		private const float MaxScale = 1.6f;
		private const float MaxSize = 0.35f;

		private readonly TouchControlSpec[] controls;

		public TouchLayoutSpec(TouchControlSpec[] controls, float scale)
		{
			if (controls == null || controls.Length != Count)
			{
				throw new ArgumentException($"O layout precisa de {Count} controles.", nameof(controls));
			}
			this.controls = (TouchControlSpec[])controls.Clone();
			Scale = ClampScale(scale);
		}

		/// <summary>Multiplicador global de tamanho (acessibilidade: dedos/mãos maiores).</summary>
		public float Scale { get; }

		public TouchControlSpec this[TouchControl control] => controls[(int)control];

		public TouchControlSpec this[int index] => controls[index];

		/// <summary>
		/// Layout de fábrica: manche à esquerda, Z/X/C à direita, e a coluna de botões secundários
		/// (pausa, journal, retry) no canto superior direito, longe do trio de ação, e o teclado no
		/// canto oposto — é o único controle que não aperta tecla nenhuma, então fica fora do caminho.
		/// </summary>
		public static TouchLayoutSpec Default { get; } = new TouchLayoutSpec(
			new[]
			{
				new TouchControlSpec(0.122f, 0.783f, 0.150f), // Stick
				new TouchControlSpec(0.938f, 0.889f, 0.082f), // Jump (C)
				new TouchControlSpec(0.822f, 0.889f, 0.082f), // Dash (X)
				new TouchControlSpec(0.938f, 0.684f, 0.082f), // Grab (Z)
				new TouchControlSpec(0.952f, 0.085f, 0.050f), // Pause
				new TouchControlSpec(0.952f, 0.235f, 0.062f), // Journal (Tab)
				new TouchControlSpec(0.952f, 0.400f, 0.062f), // Retry (R)
				new TouchControlSpec(0.048f, 0.085f, 0.050f), // Keyboard (abre o teclado do aparelho)
			},
			1f
		);

		/// <summary>Texto guardado em SharedPreferences (e mandado ao jogo pelo AppContext).</summary>
		public string Encode()
		{
			var parts = new string[Count + 2];
			parts[0] = Version;
			for (int i = 0; i < Count; i++)
			{
				parts[i + 1] = string.Join(",", new[]
				{
					controls[i].X.ToString("0.####", CultureInfo.InvariantCulture),
					controls[i].Y.ToString("0.####", CultureInfo.InvariantCulture),
					controls[i].Size.ToString("0.####", CultureInfo.InvariantCulture),
				});
			}
			parts[Count + 1] = Scale.ToString("0.####", CultureInfo.InvariantCulture);
			return string.Join(";", parts);
		}

		/// <summary>Lê o texto de <see cref="Encode"/>; devolve falso e o padrão quando não dá.</summary>
		public static bool TryParse(string? text, out TouchLayoutSpec layout)
		{
			layout = Default;
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}

			string[] parts = text!.Split(';');
			if (parts.Length < 3 || parts.Length > Count + 2 || parts[0] != Version)
			{
				return false;
			}

			// Um texto gravado antes de existirem mais controles tem menos campos: os que faltam
			// vêm do layout de fábrica, para uma posição já salva não se perder quando uma tecla
			// nova aparece (o último campo é sempre o Scale).
			int written = parts.Length - 2;
			var values = new TouchControlSpec[Count];
			for (int i = 0; i < Count; i++)
			{
				if (i >= written)
				{
					values[i] = Default[i];
					continue;
				}
				string[] fields = parts[i + 1].Split(',');
				if (fields.Length != 3
					|| !TryFloat(fields[0], out float x)
					|| !TryFloat(fields[1], out float y)
					|| !TryFloat(fields[2], out float size))
				{
					return false;
				}
				values[i] = new TouchControlSpec(Clamp01(x), Clamp01(y), Math.Min(Clamp01(size), MaxSize));
			}
			if (!TryFloat(parts[parts.Length - 1], out float scale))
			{
				return false;
			}

			layout = new TouchLayoutSpec(values, scale);
			return true;
		}

		private static bool TryFloat(string value, out float result) =>
			float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

		/// <summary>Trava em 0..1 para um texto estranho não jogar o controle para fora da tela.</summary>
		private static float Clamp01(float value) => float.IsNaN(value) ? 0f : Math.Clamp(value, 0f, 1f);

		private static float ClampScale(float scale) =>
			float.IsNaN(scale) || scale <= 0f ? 1f : Math.Clamp(scale, MinScale, MaxScale);
	}
}
