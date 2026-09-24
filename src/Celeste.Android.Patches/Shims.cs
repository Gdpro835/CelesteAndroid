using System;
using System.Reflection;
using MonoMod;

namespace CelesteAndroid
{
	/// <summary>
	/// Configuração passada pelo host (desktop ou Activity Android) antes do jogo iniciar.
	/// Usa AppContext para não exigir referência ao Celeste.dll patcheado.
	/// </summary>
	public static class HostConfig
	{
		public const string PlatformKey = "CelesteAndroid.Platform";
		public const string PrefPathKey = "CelesteAndroid.PrefPath";

		public static string Platform => AppContext.GetData(PlatformKey) as string ?? "Android";

		public static string PrefPath => AppContext.GetData(PrefPathKey) as string
			?? throw new InvalidOperationException($"{PrefPathKey} não foi definido pelo host.");
	}

	/// <summary>
	/// O Celeste chama SDL2 diretamente, mas o FNA atual roda sobre SDL3.
	/// As chamadas são religadas para cá durante o patch.
	/// </summary>
	public static class SDLShim
	{
		[MonoModLinkFrom("System.String SDL2.SDL::SDL_GetPlatform()")]
		public static string SDL_GetPlatform() => HostConfig.Platform;

		[MonoModLinkFrom("System.String SDL2.SDL::SDL_GetPrefPath(System.String,System.String)")]
		public static string SDL_GetPrefPath(string org, string app)
		{
			string path = System.IO.Path.Combine(HostConfig.PrefPath, app);
			System.IO.Directory.CreateDirectory(path);
			return path;
		}
	}

	/// <summary>
	/// O Celeste usa GetEntryAssembly() para achar os próprios tipos; com um loader, o "entry" não é ele.
	/// </summary>
	public static class ReflectionShim
	{
		[MonoModLinkFrom("System.Reflection.Assembly System.Reflection.Assembly::GetEntryAssembly()")]
		public static Assembly GetEntryAssembly() => typeof(ReflectionShim).Assembly;
	}
}
