using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Android.Content;
using Android.Util;

namespace CelesteAndroid
{
	/// <summary>
	/// Carrega o Celeste.dll patcheado e chama o Main original, com o mesmo contrato do host desktop.
	/// </summary>
	public static class CelesteLauncher
	{
		// Armazenamento interno do app: sem FUSE (mais rápido) e sem problemas de dono dos arquivos.

		/// <summary>Pasta com os arquivos do jogo do usuário (Content/ etc.).</summary>
		public static string GameDir(Context context) => Path.Combine(context.FilesDir!.AbsolutePath, "Celeste");

		/// <summary>Celeste.dll gerado pelo Celeste.Patcher.</summary>
		public static string PatchedDll(Context context) => Path.Combine(context.FilesDir!.AbsolutePath, "patched", "Celeste.dll");

		/// <summary>Saves e configurações do jogo.</summary>
		public static string UserDir(Context context) => Path.Combine(context.FilesDir!.AbsolutePath, "userdata");

		public static bool IsInstalled(Context context)
		{
			bool dll = File.Exists(PatchedDll(context));
			bool content = Directory.Exists(Path.Combine(GameDir(context), "Content"));
			Log.Info(CelesteActivity.LogTag, $"Celeste.dll={dll} Content={content} ({GameDir(context)})");
			return dll && content;
		}

		public static void Run(Context context)
		{
			string gameDir = GameDir(context);
			string patchedDll = PatchedDll(context);
			Log.Info(CelesteActivity.LogTag, $"Iniciando Celeste de {gameDir}");

			// Ver CelesteAndroid.HostConfig (Celeste.Android.Patches).
			AppContext.SetData("CelesteAndroid.Platform", "Android");
			AppContext.SetData("CelesteAndroid.PrefPath", UserDir(context));

			// O FNA resolve o Content relativo ao diretório de trabalho no Android.
			AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", gameDir + Path.DirectorySeparatorChar);
			Environment.CurrentDirectory = gameDir;

			Assembly celeste = AssemblyLoadContext.Default.LoadFromAssemblyPath(patchedDll);

			// Engine.AssemblyDirectory vem de Assembly.Location, que não aponta para o jogo.
			celeste.GetType("Monocle.Engine", throwOnError: true)!
				.GetField("AssemblyDirectory", BindingFlags.NonPublic | BindingFlags.Static)!
				.SetValue(null, gameDir);

			MethodInfo main = celeste.GetType("Celeste.Celeste", throwOnError: true)!
				.GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)!;
			main.Invoke(null, new object[] { Array.Empty<string>() });
		}
	}
}
