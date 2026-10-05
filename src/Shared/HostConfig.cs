using System;

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

		/// <summary>Lado do host: publica a configuração lida pelo jogo patcheado.</summary>
		/// <param name="backgroundPath">Imagem das faixas laterais (opcional; o desktop não usa).</param>
		/// <param name="touchControls">Controles na tela (ver TouchControls no módulo de patches).</param>
		public static void Publish(string platform, string prefPath, string? backgroundPath = null, bool touchControls = false)
		{
			AppContext.SetData(PlatformKey, platform);
			AppContext.SetData(PrefPathKey, prefPath);
			if (backgroundPath != null)
			{
				AppContext.SetData(BackgroundPathKey, backgroundPath);
			}
			AppContext.SetData(TouchControlsKey, touchControls ? "1" : "0");
		}

		/// <summary>Lado do jogo: plataforma que o shim do SDL2 reporta.</summary>
		public static string Platform => AppContext.GetData(PlatformKey) as string ?? "Android";

		/// <summary>Imagem para as faixas laterais em telas mais largas que 16:9 (opcional).</summary>
		public static string? BackgroundPath => AppContext.GetData(BackgroundPathKey) as string;

		/// <summary>Controles na tela (toque). Só ligam se o host mandar "1" (ver TouchControls).</summary>
		public static bool TouchControlsEnabled => AppContext.GetData(TouchControlsKey) as string == "1";

		public static string PrefPath => AppContext.GetData(PrefPathKey) as string
			?? throw new InvalidOperationException($"{PrefPathKey} não foi definido pelo host.");
	}
}
